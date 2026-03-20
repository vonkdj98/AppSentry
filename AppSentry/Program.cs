namespace AppSentry;

internal static class Program
{
    private static readonly string MutexName = "AppSentry_SingleInstance_Mutex";

    [STAThread]
    static void Main()
    {
        // Prevent multiple instances
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AppSentry is already running.",
                "AppSentry",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
