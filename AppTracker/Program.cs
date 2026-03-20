namespace AppTracker;

internal static class Program
{
    private static readonly string MutexName = "AppTracker_SingleInstance_Mutex";

    [STAThread]
    static void Main()
    {
        // Prevent multiple instances
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AppTracker is already running.",
                "AppTracker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
