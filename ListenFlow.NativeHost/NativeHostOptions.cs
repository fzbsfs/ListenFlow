using ListenFlow.Protocol;

namespace ListenFlow.NativeHost;

public sealed record NativeHostOptions(
    string PipeName,
    string DesktopExecutablePath,
    TimeSpan StartupTimeout,
    TimeSpan ConnectionAttemptTimeout)
{
    public static NativeHostOptions FromEnvironment(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var pipeName = Environment.GetEnvironmentVariable("LISTENFLOW_PIPE_NAME");
        var appPath = Environment.GetEnvironmentVariable("LISTENFLOW_APP_PATH");
        if (string.IsNullOrWhiteSpace(appPath))
        {
            var sameDirectory = Path.Combine(baseDirectory, "ListenFlow.exe");
            var siblingDirectory = Path.GetFullPath(Path.Combine(baseDirectory, "..", "DesktopApp", "ListenFlow.exe"));
            appPath = File.Exists(sameDirectory) ? sameDirectory : siblingDirectory;
        }

        return new NativeHostOptions(
            string.IsNullOrWhiteSpace(pipeName) ? ProtocolConstants.DesktopPipeName : pipeName,
            Path.GetFullPath(appPath),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(500));
    }
}
