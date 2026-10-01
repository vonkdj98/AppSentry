using AppSentry.Core.Service;

namespace AppSentry.Tests;

/// <summary>Copying the app into Program Files while a just-stopped program still has a file open.</summary>
[Collection("ServiceInstaller settings")]
public class ServiceInstallerCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AppSentryCopy-" + Guid.NewGuid().ToString("N"));
    private readonly int _attempts = ServiceInstaller.CopyAttempts;
    private readonly TimeSpan _delay = ServiceInstaller.CopyRetryDelay;

    public ServiceInstallerCopyTests()
    {
        ServiceInstaller.CopyRetryDelay = TimeSpan.FromMilliseconds(50);
        Directory.CreateDirectory(Path.Combine(_root, "from"));
        File.WriteAllText(Path.Combine(_root, "from", "AppSentry.exe"), "new");
        Directory.CreateDirectory(Path.Combine(_root, "to"));
        File.WriteAllText(Path.Combine(_root, "to", "AppSentry.exe"), "old");
    }

    public void Dispose()
    {
        ServiceInstaller.CopyAttempts = _attempts;
        ServiceInstaller.CopyRetryDelay = _delay;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_file_that_is_released_a_moment_later_is_copied_and_the_wait_is_logged()
    {
        ServiceInstaller.CopyAttempts = 100;
        var log = new List<string>();
        using var held = new FileStream(Path.Combine(_root, "to", "AppSentry.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });

        ServiceInstaller.CopyDirectory(Path.Combine(_root, "from"), Path.Combine(_root, "to"), log.Add);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "to", "AppSentry.exe")));
        Assert.Single(log);
        Assert.Contains("AppSentry.exe", log[0]);
    }

    [Fact]
    public void A_file_that_stays_locked_fails_after_the_attempts_naming_the_file()
    {
        ServiceInstaller.CopyAttempts = 4;
        using var held = new FileStream(Path.Combine(_root, "to", "AppSentry.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<IOException>(() => ServiceInstaller.CopyDirectory(Path.Combine(_root, "from"), Path.Combine(_root, "to")));

        Assert.Contains("AppSentry.exe", error.Message);
        held.Dispose();
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "to", "AppSentry.exe")));   // nothing was half-written over it
    }
}
