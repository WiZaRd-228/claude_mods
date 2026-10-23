namespace ClaudeLimitsTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ClaudeLimitsTray", out var isFirstInstance);
        if (!isFirstInstance) return;

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write($"UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Unhandled: {e.ExceptionObject}");
        Application.Run(new TrayApp());
    }
}
