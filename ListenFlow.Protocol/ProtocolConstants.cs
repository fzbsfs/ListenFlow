namespace ListenFlow.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string OffsetEncoding = "utf-16";
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const int MaximumTextLength = 5_000_000;
    public const string DesktopPipeName = "ListenFlow.Desktop.Protocol.v1";
    public const string DesktopMutexName = "Local\\ListenFlow.Desktop.Singleton.7E5E7364";

    public static class Types
    {
        public const string Hello = "hello";
        public const string Start = "start";
        public const string Pause = "pause";
        public const string Resume = "resume";
        public const string Stop = "stop";
        public const string Seek = "seek";
        public const string SetRate = "set-rate";
        public const string Ping = "ping";
        public const string Ready = "ready";
        public const string Ack = "ack";
        public const string State = "state";
        public const string Progress = "progress";
        public const string Error = "error";
        public const string Pong = "pong";
        public const string Diagnostic = "diagnostic";
    }

    public static readonly IReadOnlySet<string> CommandTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        Types.Hello,
        Types.Start,
        Types.Pause,
        Types.Resume,
        Types.Stop,
        Types.Seek,
        Types.SetRate,
        Types.Ping
    };
}
