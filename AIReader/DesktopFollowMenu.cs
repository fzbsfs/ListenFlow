using System.Runtime.InteropServices;

namespace ListenFlow.AIReader;

internal sealed class DesktopFollowMenu(Control owner, Func<nint, Point, string, bool, Func<bool>, Task> read, Action<string> copy,
    Func<Exception, bool> isCaptureFailure, Action<nint, Point, Func<bool>> seek) : IDisposable
{
    private const int MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, RightDown = 0x0204, RightUp = 0x0205;
    private const int MiddleDown = 0x0207, Wheel = 0x020A, ExtraDown = 0x020B, HorizontalWheel = 0x020E;
    private static readonly nuint ReplayMarker = 0x4C464D4E;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private MouseHook? _callback;
    private nint _hook;
    private nint _checkedWindow;
    private nint _clientWindow;
    private (nint Window, Point Point, int Revision)? _press;
    private (nint Window, Point Point, int Revision)? _leftPress;
    private Point _lastClickPoint;
    private long _lastClickTime;
    private nint _lastClickWindow;
    private int _revision;
    private Task<(DesktopFollowSource? Source, bool WholeReply)>? _lookup;
    private bool _disposed;
    private MenuWindow? _menu;

    public bool Register()
    {
        _callback = OnMouse;
        _hook = SetWindowsHookExW(14, _callback, GetModuleHandleW(null), 0);
        if (_hook == 0) return false;
        _timer.Tick += (_, _) => RefreshForeground();
        _timer.Start();
        return true;
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code != 0 || _disposed || !owner.IsHandleCreated) return CallNextHookEx(_hook, code, message, data);
        switch ((int)message)
        {
            case MouseMove when _leftPress is { } moving:
                if (!WithinGesture(moving.Point, ReadPoint(data), SystemInformation.DragSize)) _leftPress = null;
                break;
            case LeftDown:
                var down = ReadPoint(data);
                _leftPress = _menu is null && _clientWindow != 0 && DesktopFollowNative.Foreground == _clientWindow &&
                    WindowAt(down) == _clientWindow && !HasModifiers() ? (_clientWindow, down, _revision + 1) : null;
                goto case MiddleDown;
            case LeftUp when _leftPress is { } clicking:
                _leftPress = null;
                var up = ReadPoint(data);
                if (clicking.Revision == _revision && !HasModifiers() && WindowAt(up) == clicking.Window &&
                    WithinGesture(clicking.Point, up, SystemInformation.DragSize))
                    QueueSeek(clicking.Window, up, clicking.Revision);
                break;
            case MiddleDown:
            case ExtraDown:
            case Wheel:
            case HorizontalWheel:
                if (_menu is { } menu && !menu.Bounds.Contains(ReadPoint(data)))
                    owner.BeginInvoke(() => CloseMenu(menu));
                _revision++;
                break;
            case RightDown:
                var input = Marshal.PtrToStructure<MouseEvent>(data);
                if (input.ExtraInfo == ReplayMarker) break;
                var point = new Point(input.Point.X, input.Point.Y);
                if (_menu is { } openMenu)
                {
                    if (!openMenu.Bounds.Contains(point)) owner.BeginInvoke(() => CloseMenu(openMenu));
                    break;
                }
                _revision++;
                // Resolve the UI Automation element after release, away from the hook callback.
                if (_lookup?.IsCompleted != false && _clientWindow != 0 && DesktopFollowNative.Foreground == _clientWindow &&
                    WindowAt(point) == _clientWindow && DesktopFollowNative.ClientBounds(_clientWindow).Contains(point))
                {
                    _press = (_clientWindow, point, _revision);
                    return 1;
                }
                break;
            case RightUp when _press is { } pressed:
                _press = null;
                var releasedAt = ReadPoint(data);
                if (pressed.Point == releasedAt && pressed.Revision == _revision)
                    owner.BeginInvoke(() => _ = ShowAsync(pressed.Window, releasedAt, pressed.Revision));
                return 1;
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void QueueSeek(nint window, Point point, int revision)
    {
        var now = Environment.TickCount64;
        var repeated = window == _lastClickWindow && now - _lastClickTime <= SystemInformation.DoubleClickTime &&
            WithinGesture(_lastClickPoint, point, SystemInformation.DoubleClickSize);
        _lastClickWindow = window;
        _lastClickPoint = point;
        _lastClickTime = now;
        if (repeated) return;
        owner.BeginInvoke(async () =>
        {
            await Task.Delay(SystemInformation.DoubleClickTime);
            bool IsCurrent() => !_disposed && revision == _revision && WindowAt(point) == window;
            if (IsCurrent()) seek(window, point, IsCurrent);
        });
    }

    private static bool WithinGesture(Point start, Point end, Size size) =>
        Math.Abs(start.X - end.X) * 2 <= size.Width && Math.Abs(start.Y - end.Y) * 2 <= size.Height;

    private static bool HasModifiers() => GetAsyncKeyState((int)Keys.ControlKey) < 0 ||
        GetAsyncKeyState((int)Keys.ShiftKey) < 0 || GetAsyncKeyState((int)Keys.Menu) < 0 ||
        GetAsyncKeyState((int)Keys.LWin) < 0 || GetAsyncKeyState((int)Keys.RWin) < 0;

    private void RefreshForeground()
    {
        var foreground = DesktopFollowNative.Foreground;
        if (_menu is { } menu && (foreground != menu.SourceWindow || GetAsyncKeyState((int)Keys.Escape) < 0))
            CloseMenu(menu);
        if (foreground == _checkedWindow) return;
        _checkedWindow = foreground;
        _clientWindow = 0;
        _revision++;
        GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            if (processId != 0 && DesktopFollowSource.ClientName((int)processId) is not null)
                _clientWindow = foreground;
        }
        catch (Exception error) when (isCaptureFailure(error)) { }
    }

    private async Task ShowAsync(nint window, Point point, int revision)
    {
        if (_disposed || _lookup?.IsCompleted == false || _revision != revision || _menu is not null ||
            DesktopFollowNative.Foreground != window || WindowAt(point) != window) return;
        var lookup = _lookup = Task.Run(() =>
        {
            var reply = TryCapture(window, point, wholeReply: true);
            return reply is not null ? (reply, true) : (TryCapture(window, point, wholeReply: false), false);
        });
        var deadline = Task.Delay(500);
        var finished = await Task.WhenAny(lookup, deadline);
        if (_disposed || _revision != revision || _menu is not null ||
            DesktopFollowNative.Foreground != window || WindowAt(point) != window) return;
        var (source, wholeReply) = finished == lookup ? await lookup : (null, false);
        if (source is null)
        {
            if (Cursor.Position == point)
            {
                mouse_event(0x0008, 0, 0, 0, ReplayMarker);
                mouse_event(0x0010, 0, 0, 0, ReplayMarker);
            }
            return;
        }

        var text = source.Text;
        var menu = new MenuWindow(window, wholeReply);
        _menu = menu;
        menu.Read.Click += (_, _) =>
        {
            CloseMenu(menu);
            var readRevision = _revision;
            _ = read(window, point, text, wholeReply, () => !Volatile.Read(ref _disposed) &&
                Volatile.Read(ref _revision) == readRevision);
        };
        menu.Copy.Click += async (_, _) =>
        {
            CloseMenu(menu);
            var copyRevision = _revision;
            try
            {
                var current = await Task.Run(() => DesktopFollowSource.Capture(window, point, wholeReply));
                if (!_disposed && copyRevision == _revision && DesktopFollowNative.Foreground == window &&
                    current?.Text == text) copy(text);
            }
            catch (Exception error) when (isCaptureFailure(error)) { }
        };
        menu.ShowAt(point);
    }

    private DesktopFollowSource? TryCapture(nint window, Point point, bool wholeReply)
    {
        try { return DesktopFollowSource.Capture(window, point, wholeReply); }
        catch (Exception error) when (isCaptureFailure(error)) { return null; }
    }

    private void CloseMenu(MenuWindow menu)
    {
        if (!ReferenceEquals(_menu, menu)) return;
        _menu = null;
        menu.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _timer.Dispose();
        if (_menu is { } menu) CloseMenu(menu);
    }

    private static Point ReadPoint(nint data) => new(Marshal.ReadInt32(data), Marshal.ReadInt32(data, sizeof(int)));
    private static nint WindowAt(Point point) => GetAncestor(WindowFromPoint(new NativePoint(point.X, point.Y)), 2);

    private sealed class MenuWindow : Form
    {
        public nint SourceWindow { get; }
        public Button Read { get; } = new() { Dock = DockStyle.Fill };
        public Button Copy { get; } = new() { Dock = DockStyle.Fill };

        public MenuWindow(nint sourceWindow, bool wholeReply)
        {
            SourceWindow = sourceWindow;
            Read.Text = wholeReply ? "使用 ListenFlow 朗读整条回复" : "使用 ListenFlow 朗读所选文字";
            Copy.Text = wholeReply ? "复制整条回复" : "复制";
            Text = "ListenFlow · 选区菜单";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = ListenFlow.UI.ComicTheme.Ink;
            ClientSize = new Size(236, 72);
            ListenFlow.UI.ComicTheme.StyleComicButton(Read, primary: true);
            ListenFlow.UI.ComicTheme.StyleComicButton(Copy, primary: false);
            Read.FlatAppearance.BorderSize = 0;
            Copy.FlatAppearance.BorderSize = 0;
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(2), BackColor = ListenFlow.UI.ComicTheme.Ink };
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            rows.Controls.Add(Read, 0, 0);
            rows.Controls.Add(Copy, 0, 1);
            Controls.Add(rows);
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return parameters;
            }
        }

        public void ShowAt(Point point)
        {
            var work = Screen.FromPoint(point).WorkingArea;
            Location = new Point(Math.Max(work.Left, Math.Min(point.X, work.Right - Width)),
                Math.Max(work.Top, Math.Min(point.Y, work.Bottom - Height)));
            Show();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MouseEvent(NativePoint Point, uint MouseData, uint Flags, uint Time, nuint ExtraInfo);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extraInfo);
    private delegate nint MouseHook(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, MouseHook hook, nint module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
