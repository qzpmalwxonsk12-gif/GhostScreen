namespace GhostScreen;

internal static class Program
{
    private const string MutexName = "GhostScreen_RemoteStealthCurtain_SingleInstance";
    private const string ShowEventName = "GhostScreen_RemoteStealthCurtain_ShowWindowEvent";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!createdNew)
        {
            // Signal the running (potentially hidden) instance to show its interface immediately
            showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();

        bool startActive = args.Any(a =>
            string.Equals(a, "--start-blackout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-b", StringComparison.OrdinalIgnoreCase));

        using var mainForm = new MainForm(startActive);

        // Listen for second-instance launch to unhide the UI
        ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) =>
            {
                if (!mainForm.IsDisposed && mainForm.IsHandleCreated)
                {
                    mainForm.BeginInvoke(() => mainForm.SetAppVisibility(true));
                }
            },
            null,
            -1,
            false);

        Application.Run(mainForm);
    }
}
