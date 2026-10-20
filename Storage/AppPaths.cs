namespace ListenFlow.Storage;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            return ResolveDataDirectory(
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                File.Exists);
        }
    }

    internal static string ResolveDataDirectory(
        string baseDirectory,
        string? overridePath,
        string localApplicationData,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);
        var isolatedMarker = Path.Combine(baseDirectory, "edge-test-isolated-data.marker");
        return Path.Combine(
            localApplicationData,
            fileExists(isolatedMarker) ? "ListenFlow-EdgeTest\\Data" : "ListenFlow");
    }
}
