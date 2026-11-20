using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using ListenFlow.Core;

namespace ListenFlow.AIReader;

internal sealed class DesktopFollowSource(
    nint window, string name, string text, TextPatternRange selection, AutomationElement? viewport, AutomationElement document)
{
    internal const int MaxTextLength = 20_000;
    public nint Window { get; } = window;
    public string Name { get; } = name;
    public string Text { get; } = text;

    internal static string? ClientName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName.ToUpperInvariant() switch
        {
            "CLAUDE" => "Claude Code",
            "CODEX" => "Codex",
            "CHATGPT" when process.MainModule?.FileName.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true => "Codex",
            _ => null
        };
    }

    public static DesktopFollowSource? Capture(nint window, Point? hit = null, bool wholeReply = false)
    {
        if (window == 0 || DesktopFollowNative.Foreground != window) return null;
        var root = AutomationElement.FromHandle(window);
        var name = ClientName(root.Current.ProcessId);
        if (name is null) return null;
        if (wholeReply)
        {
            if (hit is not { } point) return null;
            var screenPoint = new System.Windows.Point(point.X, point.Y);
            var leaf = AutomationElement.FromPoint(screenPoint);
            if (leaf.Current.ProcessId != root.Current.ProcessId) return null;
            var reply = FindReplyRange(leaf, name);
            return reply is { } match ? CaptureRange(window, name, match.Document, match.Range, hit) : null;
        }

        var focused = AutomationElement.FocusedElement;
        if (focused?.Current.ProcessId == root.Current.ProcessId && focused.Current.ControlType == ControlType.Edit)
            return null;
        foreach (AutomationElement document in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
        {
            if (document.Current.IsOffscreen || !document.TryGetCurrentPattern(TextPattern.Pattern, out var value)) continue;
            var ranges = ((TextPattern)value).GetSelection();
            if (ranges.Length == 1 && CaptureRange(window, name, document, ranges[0].Clone(), hit) is { } source)
                return source;
        }
        return null;
    }

    internal void SelectInWindow() => selection.Select();

    private static DesktopFollowSource? CaptureRange(nint window, string name, AutomationElement document,
        TextPatternRange range, Point? hit)
    {
        var text = range.GetText(MaxTextLength + 1);
        if (text.Length > MaxTextLength || string.IsNullOrWhiteSpace(text)) return null;
        var boxes = range.GetBoundingRectangles();
        if (boxes.Length == 0 || DesktopFollowNative.Foreground != window) return null;
        var first = boxes[0];
        var center = hit is { } point ? new System.Windows.Point(point.X, point.Y)
            : new System.Windows.Point(first.Left + first.Width / 2, first.Top + first.Height / 2);
        AutomationElement? viewport = null;
        var area = double.PositiveInfinity;
        // Chromium may expose the scroll provider outside the hit-tested text node's ancestry.
        foreach (AutomationElement element in document.FindAll(TreeScope.Subtree,
                     new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)))
        {
            var bounds = element.Current.BoundingRectangle;
            if (!bounds.Contains(center) || bounds.Width * bounds.Height >= area ||
                !element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll) ||
                !((ScrollPattern)scroll).Current.VerticallyScrollable) continue;
            viewport = element;
            area = bounds.Width * bounds.Height;
        }
        if (hit is { } position)
        {
            var clip = DesktopFollowNative.ClientBounds(window);
            if (viewport is not null) clip = Rectangle.Intersect(clip, Pixels(viewport.Current.BoundingRectangle));
            if (!clip.Contains(position) || !boxes.Any(box => Pixels(box).Contains(position))) return null;
        }
        return new DesktopFollowSource(window, name, text, range, viewport, document);
    }

    public int? ResolveClick(Point point)
    {
        if (DesktopFollowNative.Foreground != Window || !DesktopFollowNative.ClientBounds(Window).Contains(point)) return null;
        var screenPoint = new System.Windows.Point(point.X, point.Y);
        var viewportBounds = viewport?.Current.BoundingRectangle;
        if (viewportBounds is { } bounds && !bounds.Contains(screenPoint)) return null;
        var scroll = viewport is not null && viewport.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern)
            ? (ScrollPattern)scrollPattern : null;
        var scrollPosition = scroll?.Current.VerticalScrollPercent;
        if (!string.Equals(selection.GetText(Text.Length + 1), Text, StringComparison.Ordinal)) return null;
        var geometry = selection.GetBoundingRectangles();
        if (!geometry.Any(box => box.Contains(screenPoint))) return null;

        var leaf = AutomationElement.FromPoint(screenPoint);
        var walker = TreeWalker.RawViewWalker;
        var element = leaf;
        for (; element is not null; element = walker.GetParent(element))
        {
            var type = element.Current.ControlType;
            if (type == ControlType.Hyperlink || type == ControlType.Button || type == ControlType.Edit ||
                type == ControlType.ToolBar || type == ControlType.CheckBox || type == ControlType.ComboBox ||
                type == ControlType.ScrollBar || type == ControlType.MenuItem) return null;
            if (type == ControlType.Document) break;
        }
        if (element is null || !Automation.Compare(element, document) ||
            !document.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;

        var hit = ((TextPattern)pattern).RangeFromPoint(screenPoint);
        if (hit.CompareEndpoints(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.Start) < 0 ||
            hit.CompareEndpoints(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End) >= 0) return null;
        var prefix = selection.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, hit, TextPatternRangeEndpoint.Start);
        var preceding = prefix.GetText(Text.Length + 1);
        if (preceding.Length >= Text.Length || !Text.AsSpan().StartsWith(preceding.AsSpan(), StringComparison.Ordinal) ||
            (preceding.Length > 0 && char.IsHighSurrogate(Text[preceding.Length - 1])) ||
            viewportBounds != viewport?.Current.BoundingRectangle || scrollPosition != scroll?.Current.VerticalScrollPercent ||
            !geometry.AsSpan().SequenceEqual(selection.GetBoundingRectangles()) || DesktopFollowNative.Foreground != Window) return null;
        return preceding.Length;
    }

    private static (AutomationElement Document, TextPatternRange Range)? FindReplyRange(AutomationElement leaf, string name)
    {
        var walker = TreeWalker.RawViewWalker;
        AutomationElement? row = null;
        AutomationElement? block = null;
        var element = leaf;
        while (element is not null && element.Current.ControlType != ControlType.Document)
        {
            var current = element.Current;
            var classes = current.ClassName;
            if (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Button ||
                current.ControlType == ControlType.ToolBar || classes.StartsWith("group/bubble ", StringComparison.Ordinal) ||
                classes.StartsWith("bg-user-message ", StringComparison.Ordinal) ||
                classes.StartsWith("group/status ", StringComparison.Ordinal)) return null;
            var parent = walker.GetParent(element);
            if (row is null && ((name == "Claude Code" && classes.StartsWith("group/message-row ", StringComparison.Ordinal)) ||
                (name == "Codex" && classes == "relative shrink-0" && parent?.Current.ClassName.StartsWith(
                    "relative flex flex-col gap-3 electron:transcript-description-color", StringComparison.Ordinal) == true)))
                row = element;
            if (row is null) block = element;
            element = parent;
        }
        if (row is null || block is null || element is null ||
            !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;

        var bounds = name == "Claude Code" ? ClaudeReplyBounds(row, block, walker) : CodexReplyBounds(block, walker);
        if (bounds is not { } body) return null;
        var textPattern = (TextPattern)pattern;
        var range = textPattern.RangeFromChild(body.First);
        var end = textPattern.RangeFromChild(body.Last);
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, end, TextPatternRangeEndpoint.End);
        return (element, range);
    }

    private static (AutomationElement First, AutomationElement Last)? ClaudeReplyBounds(
        AutomationElement row, AutomationElement block, TreeWalker walker)
    {
        // Completed Claude Code replies have a message toolbar following their body blocks.
        var first = walker.GetFirstChild(row);
        AutomationElement? last = null;
        var clickedBody = false;
        for (var child = first; child is not null; child = walker.GetNextSibling(child))
        {
            if (child.Current.ControlType == ControlType.ToolBar)
                return clickedBody && first is not null && last is not null ? (first, last) : null;
            if (child.Current.ClassName.StartsWith("group/status ", StringComparison.Ordinal)) return null;
            clickedBody |= Automation.Compare(child, block);
            last = child;
        }
        return null;
    }

    private static (AutomationElement First, AutomationElement Last)? CodexReplyBounds(AutomationElement block, TreeWalker walker)
    {
        // Codex exposes the body as consecutive siblings after the assistant heading and status.
        var first = block;
        var previous = walker.GetPreviousSibling(first);
        while (previous is not null && IsCodexBody(previous))
        {
            first = previous;
            previous = walker.GetPreviousSibling(first);
        }
        if (!IsCodexBody(block) || previous?.Current.ControlType != ControlType.StatusBar ||
            walker.GetPreviousSibling(previous)?.Current.ClassName.StartsWith("sr-only m-0 select-none ", StringComparison.Ordinal) != true)
            return null;
        var last = block;
        var next = walker.GetNextSibling(last);
        while (next is not null && IsCodexBody(next))
        {
            last = next;
            next = walker.GetNextSibling(last);
        }
        if (next?.Current.ControlType != ControlType.Button ||
            !next.Current.ClassName.StartsWith("no-drag cursor-interaction ", StringComparison.Ordinal)) return null;
        return (first, last);
    }

    private static bool IsCodexBody(AutomationElement element)
    {
        var value = element.Current;
        if (value.ControlType == ControlType.List || value.ControlType == ControlType.Table) return true;
        return value.ClassName.StartsWith("Paragraph-", StringComparison.Ordinal) ||
            value.ClassName.StartsWith("Blockquote-", StringComparison.Ordinal) ||
            value.ClassName.StartsWith("CodeBlock-", StringComparison.Ordinal) ||
            value.ClassName.StartsWith("Heading-", StringComparison.Ordinal) ||
            value.ClassName.StartsWith("Table-", StringComparison.Ordinal);
    }

    public Rectangle[] Locate(TextRange sentence)
    {
        if (DesktopFollowNative.Foreground != Window ||
            !string.Equals(selection.GetText(Text.Length + 1), Text, StringComparison.Ordinal) ||
            sentence.Start < 0 || sentence.Length <= 0 || sentence.Start > Text.Length - sentence.Length)
            return [];
        var start = EndpointAt(sentence.Start);
        var end = EndpointAt(sentence.Start + sentence.Length);
        if (start is null || end is null) return [];
        end.MoveEndpointByRange(TextPatternRangeEndpoint.Start, start, TextPatternRangeEndpoint.End);
        if (!Text.AsSpan(sentence.Start, sentence.Length).SequenceEqual(end.GetText(sentence.Length + 1))) return [];
        var clip = DesktopFollowNative.ClientBounds(Window);
        var viewportBounds = viewport?.Current.BoundingRectangle;
        if (viewportBounds is { } bounds) clip = Rectangle.Intersect(clip, Pixels(bounds));
        var scroll = viewport is not null && viewport.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
            ? (ScrollPattern)pattern : null;
        var scrollPosition = scroll?.Current.VerticalScrollPercent;
        var boxes = end.GetBoundingRectangles();
        if (scrollPosition != scroll?.Current.VerticalScrollPercent || viewportBounds != viewport?.Current.BoundingRectangle)
            return [];
        return boxes.Select(rect => Rectangle.Intersect(clip, Pixels(rect)))
            .Where(rect => rect.Width > 0 && rect.Height > 0).ToArray();
    }

    private TextPatternRange? EndpointAt(int offset)
    {
        var low = 0;
        var high = offset;
        var units = offset;
        while (low <= high)
        {
            var prefix = selection.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
            if (units > 0) prefix.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, units);
            var prefixText = prefix.GetText(offset + 1);
            if (prefixText.Length == offset)
                return Text.AsSpan(0, offset).SequenceEqual(prefixText) ? prefix : null;
            if (prefixText.Length > offset) high = units - 1;
            else low = units + 1;
            units = low + (high - low) / 2;
        }
        // shortcut: a provider may not expose an endpoint inside a grapheme; never highlight by guesswork.
        return null;
    }

    private static Rectangle Pixels(System.Windows.Rect rect) => rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 ||
        !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) ||
        !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)
        ? Rectangle.Empty
        : Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
            (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
}

internal static class DesktopFollowNative
{
    internal const int HotkeyId = 0x4C46;
    internal const int HotkeyMessage = 0x0312;
    public static nint Foreground => GetForegroundWindow();
    public static bool Register(nint window) => RegisterHotKey(window, HotkeyId, 0x4003, (uint)Keys.F9);
    public static void Unregister(nint window) => UnregisterHotKey(window, HotkeyId);

    public static Rectangle ClientBounds(nint window)
    {
        if (!GetClientRect(window, out var rect)) return Rectangle.Empty;
        var origin = new NativePoint();
        return ClientToScreen(window, ref origin)
            ? new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom) : Rectangle.Empty;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
