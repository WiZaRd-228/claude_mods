namespace ClaudeLimitsTray;

internal static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeLimitsTray");

    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string WebProfile = Path.Combine(Root, "WebView2");
    public static readonly string LogFile = Path.Combine(Root, "log.txt");

    static AppPaths() => Directory.CreateDirectory(Root);
}

internal static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }
}
