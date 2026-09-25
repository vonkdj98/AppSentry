using AppSentry.Core.Detection;
using AppSentry.Core.Sources;
using AppSentry.Models;

namespace AppSentry.Tests;

public class SourceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 15, 0, 0, DateTimeKind.Utc);

    // ── MSI correlation ──────────────────────────────────────────────────────

    private static MsiEvent Msi(MsiKind kind, string name, string code = "", string user = @"CONTOSO\alex", int id = 1033) => new()
    {
        Kind = kind, ProductName = name, ProductCode = code, UserName = user, EventId = id, TimeUtc = Now.AddMinutes(-2)
    };

    private static ChangeEvent Registry(ChangeType type, string name, string key, string code = "") => new()
    {
        App = new InstalledApp { KeyPath = key, Name = name, Scope = Scopes.Machine64, ProductCode = code },
        ChangeType = type,
        DetectedAt = Now,
        Source = DetectionSource.Registry
    };

    [Fact]
    public void Msi_user_enriches_the_registry_event_instead_of_being_dropped()
    {
        const string code = "{AAAAAAAA-0000-0000-0000-000000000001}";
        var result = MsiCorrelator.Apply(
            [Registry(ChangeType.Installed, "7-Zip 24.08 (x64 edition)", $@"HKLM\U\{code}", code)],
            [Msi(MsiKind.Installed, "7-Zip 24.08 (x64 edition)", code)],
            new Dictionary<string, InstalledApp>(), Now);

        var ev = Assert.Single(result);
        Assert.Equal(@"CONTOSO\alex", ev.ChangedBy);
        Assert.Equal(@"CONTOSO\alex", ev.App.InstalledBy);
        Assert.Equal(Now.AddMinutes(-2), ev.OccurredAt);
        Assert.Contains("MSI event 1033", ev.Details);
    }

    [Fact]
    public void Failed_install_and_hidden_component_are_recorded_separately()
    {
        var result = MsiCorrelator.Apply(
            [Registry(ChangeType.Installed, "Contoso Suite", @"HKLM\U\Suite")],
            [
                Msi(MsiKind.Failed, "Broken Thing") with { Detail = "Installation failed with status 1603" },
                Msi(MsiKind.Installed, "Contoso Suite Runtime (hidden)")
            ],
            new Dictionary<string, InstalledApp>(), Now);

        Assert.Equal(3, result.Count);
        var failed = Assert.Single(result, e => e.ChangeType == ChangeType.Failed);
        Assert.Contains("1603", failed.Details);
        var hidden = Assert.Single(result, e => e.App.Name == "Contoso Suite Runtime (hidden)");
        Assert.Equal(DetectionSource.EventLog, hidden.Source);
        Assert.True(hidden.Silent); // rides along with a visible install: logged, no popup
    }

    [Fact]
    public void Major_upgrade_consumes_both_the_install_and_the_removal()
    {
        var update = Registry(ChangeType.Updated, "Zoom Workplace", @"HKLM\U\{NEW}") with
        {
            PreviousApp = new InstalledApp { KeyPath = @"HKLM\U\{OLD}", Name = "Zoom Workplace", Scope = Scopes.Machine64 }
        };
        var result = MsiCorrelator.Apply([update],
            [Msi(MsiKind.Removed, "Zoom Workplace", id: 1034), Msi(MsiKind.Installed, "Zoom Workplace")],
            new Dictionary<string, InstalledApp>(), Now);

        Assert.Equal(ChangeType.Updated, Assert.Single(result).ChangeType);
    }

    // ── Folders ──────────────────────────────────────────────────────────────

    [Fact]
    public void New_folder_is_held_until_settled_and_skipped_when_an_app_owns_it()
    {
        using var dir = new TempDir();
        var roots = new List<FsRoot> { new(dir.Path, "Test", "All users") };
        var nobody = new FolderOwnership([]);
        var health = new Dictionary<string, string>();

        var (e0, b0, _) = FileSystemSource.Scan(null, roots, nobody, Now, health);            // baseline
        Directory.CreateDirectory(dir.File("PortableTool"));
        Directory.CreateDirectory(dir.File("Real App"));

        var (e1, b1, followUp) = FileSystemSource.Scan(b0, roots, nobody, Now.AddSeconds(10), health);
        Assert.Empty(e0);
        Assert.Empty(e1);                                                                        // pending, not reported yet
        Assert.NotNull(followUp);

        var owner = new FolderOwnership([new InstalledApp { Name = "Real App", Scope = Scopes.Machine64, InstallLocation = dir.File("Real App") }]);
        var (e2, _, _) = FileSystemSource.Scan(b1, roots, owner, Now.AddMinutes(3), health);
        var ev = Assert.Single(e2);
        Assert.Equal("PortableTool", ev.App.Name);
        Assert.Equal(ChangeType.Installed, ev.ChangeType);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Tool\unins000.exe"" /SILENT", @"C:\Program Files\Tool\unins000.exe")]
    [InlineData(@"C:\Program Files\Tool\uninstall.exe --remove", @"C:\Program Files\Tool\uninstall.exe")]
    [InlineData(@"C:\Program Files\Tool\app.exe,0", @"C:\Program Files\Tool\app.exe")]
    public void Executable_is_extracted_from_command_lines(string commandLine, string expected) =>
        Assert.Equal(expected, FolderOwnership.ExecutableOf(commandLine));

    // ── Services ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\foo.sys", @"System32\drivers\foo.sys")]
    [InlineData(@"""C:\Program Files\Vendor\svc.exe"" -service", @"C:\Program Files\Vendor\svc.exe")]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs -p", @"System32\svchost.exe")]
    public void Service_binary_paths_resolve(string imagePath, string expectedSuffix) =>
        Assert.EndsWith(expectedSuffix, ServiceSource.ResolveBinary(imagePath), StringComparison.OrdinalIgnoreCase);

    // ── Winget ───────────────────────────────────────────────────────────────

    [Fact]
    public void Winget_table_parses_ids_and_truncated_names()
    {
        const string output = "\r   - \r   \\ \r" +
            "Name                                   Id                     Version   Available Source\n" +
            "------------------------------------------------------------------------------------------\n" +
            "Git                                    Git.Git                2.44.0    2.46.0    winget\n" +
            "Microsoft Visual Studio Code (User)    Microsoft.VisualStudi… 1.93.1              winget\n" +
            "Some Very Long Application Name That … Vendor.LongApp         3.0                 winget\n";

        var parsed = PackageManagerSource.ParseWingetTable(output);

        Assert.Equal("Git.Git", parsed["Git"]);
        Assert.Equal("Vendor.LongApp", parsed["Some Very Long Application Name That …"]);
        Assert.Equal(3, parsed.Count);
    }
}
