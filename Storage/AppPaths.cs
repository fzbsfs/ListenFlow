namespace ListenFlow.Storage;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("LISTENFLOW_DATA_DIR");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ListenFlow")
                : Path.GetFullPath(overridePath);
        }
    }
}
