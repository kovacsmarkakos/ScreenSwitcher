namespace ScreenSwitcher;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // One instance per user session. A second copy could not register the hotkeys anyway (the
        // first one holds them), so it would only sit in the tray doing nothing. This also covers
        // a Debug build started while the installed copy is running.
        using var instance = new Mutex(initiallyOwned: true, @"Local\ScreenSwitcher", out bool firstInstance);
        if (!firstInstance)
        {
            Logger.Log($"Another ScreenSwitcher is already running; this copy ({Application.ExecutablePath}) is exiting.");
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        Application.Run(new InvisibleForm());
    }
}
