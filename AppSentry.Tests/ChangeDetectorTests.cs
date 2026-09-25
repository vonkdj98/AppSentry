using AppSentry.Core.Detection;
using AppSentry.Core.Sources;
using AppSentry.Models;

namespace AppSentry.Tests;

public class ChangeDetectorTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 15, 0, 0, DateTimeKind.Utc);

    private static InstalledApp App(string key, string name, string version, string scope = Scopes.Machine64,
        string publisher = "Contoso", string uninstall = "") => new()
    {
        KeyPath = key,
        Name = name,
        Version = version,
        Publisher = publisher,
        Scope = scope,
        UninstallString = uninstall,
        RawValues = new Dictionary<string, string> { ["DisplayName"] = name, ["DisplayVersion"] = version }
    };

    private static Dictionary<string, InstalledApp> Snap(params InstalledApp[] apps) =>
        apps.ToDictionary(a => a.KeyPath, StringComparer.OrdinalIgnoreCase);

    private static InventoryResult Scan(IEnumerable<string> completed, params InstalledApp[] apps)
    {
        var inv = new InventoryResult();
        foreach (var a in apps) inv.Add(a);
        inv.CompletedScopes.UnionWith(completed);
        return inv;
    }

    private static readonly HashSet<string> Known = [Scopes.Machine64, Scopes.Machine32, @"HKU\S-1-5-21-1", @"STORE\S-1-5-21-1"];

    [Fact]
    public void Incomplete_scope_is_carried_forward_not_removed()
    {
        var storeApp = App(@"STORE\S-1-5-21-1\Contoso.App_abc", "Contoso App", "1.0.0.0", @"STORE\S-1-5-21-1");
        var userApp = App(@"HKU\S-1-5-21-1\...\Tool", "Tool", "2.0", @"HKU\S-1-5-21-1");
        var machine = App(@"HKLM\...\Machine", "Machine App", "1.0");
        var previous = Snap(storeApp, userApp, machine);

        // Store call failed and the user signed out: neither scope completed this time.
        var diff = ChangeDetector.Detect(previous, Known, Scan([Scopes.Machine64, Scopes.Machine32], machine), Now);

        Assert.Empty(diff.Events);
        Assert.Equal(3, diff.Snapshot.Count);
    }

    [Fact]
    public void First_sight_of_a_scope_is_a_silent_baseline()
    {
        var newUser = App(@"HKU\S-1-5-21-9\...\A", "A", "1", @"HKU\S-1-5-21-9");
        var diff = ChangeDetector.Detect(Snap(), Known, Scan([@"HKU\S-1-5-21-9"], newUser), Now);

        Assert.Empty(diff.Events);
        Assert.Contains(@"HKU\S-1-5-21-9", diff.BaselinedScopes);
        Assert.Contains(@"HKU\S-1-5-21-9", diff.KnownScopes);
    }

    [Fact]
    public void Store_update_is_one_updated_event()
    {
        var scope = @"STORE\S-1-5-21-1";
        var before = App($@"{scope}\Contoso.Notes_abc", "Notes", "1.0.0.0", scope);
        var after = before with { Version = "1.1.0.0" };

        var diff = ChangeDetector.Detect(Snap(before), Known, Scan([scope], after), Now);

        var ev = Assert.Single(diff.Events);
        Assert.Equal(ChangeType.Updated, ev.ChangeType);
        Assert.Equal(DetectionSource.Store, ev.Source);
        Assert.Equal("1.0.0.0", ev.PreviousVersion);
    }

    [Fact]
    public void Msi_major_upgrade_with_new_product_code_is_one_update()
    {
        var old = App(@"HKLM\...\{11111111-1111-1111-1111-111111111111}", "Zoom Workplace (64-bit)", "6.1.0", publisher: "Zoom Video Communications, Inc.");
        var @new = App(@"HKLM\...\{22222222-2222-2222-2222-222222222222}", "Zoom Workplace (64-bit)", "6.2.5", publisher: "Zoom Video Communications, Inc");

        var diff = ChangeDetector.Detect(Snap(old), Known, Scan([Scopes.Machine64, Scopes.Machine32], @new), Now);

        var ev = Assert.Single(diff.Events);
        Assert.Equal(ChangeType.Updated, ev.ChangeType);
        Assert.Equal("6.1.0", ev.PreviousVersion);
        Assert.Same(old, ev.PreviousApp);
    }

    [Fact]
    public void Move_from_32_to_64_bit_is_correlated()
    {
        var old = App(@"HKLM\WOW\Chrome", "Google Chrome", "130.0", Scopes.Machine32, "Google LLC");
        var @new = App(@"HKLM\Chrome", "Google Chrome", "131.0", Scopes.Machine64, "Google LLC");

        var diff = ChangeDetector.Detect(Snap(old), Known, Scan([Scopes.Machine64, Scopes.Machine32], @new), Now);

        Assert.Equal(ChangeType.Updated, Assert.Single(diff.Events).ChangeType);
    }

    [Fact]
    public void Different_products_or_users_are_not_correlated()
    {
        var removed = App(@"HKLM\A", "Notepad++ (64-bit x64)", "8.6");
        var installed = App(@"HKLM\B", "Paint.NET", "5.0");
        var otherUser = App(@"HKU\S-1-5-21-1\...\N", "Notepad++ (64-bit x64)", "8.7", @"HKU\S-1-5-21-1");

        var diff = ChangeDetector.Detect(Snap(removed), Known,
            Scan([Scopes.Machine64, Scopes.Machine32, @"HKU\S-1-5-21-1"], installed, otherUser), Now);

        Assert.Equal(1, diff.Events.Count(e => e.ChangeType == ChangeType.Removed));
        Assert.Equal(2, diff.Events.Count(e => e.ChangeType == ChangeType.Installed));
    }

    [Fact]
    public void Changed_uninstaller_at_same_version_is_modified()
    {
        var before = App(@"HKLM\Tool", "Tool", "1.0", uninstall: @"""C:\Program Files\Tool\uninstall.exe""");
        var after = before with { UninstallString = @"""C:\Users\Public\evil.exe""" };

        var diff = ChangeDetector.Detect(Snap(before), Known, Scan([Scopes.Machine64, Scopes.Machine32], after), Now);

        var ev = Assert.Single(diff.Events);
        Assert.Equal(ChangeType.Modified, ev.ChangeType);
        Assert.Contains("UninstallString", ev.Details);
        Assert.Same(before, ev.PreviousApp);
    }

    [Fact]
    public void Plain_install_and_removal_still_work()
    {
        var gone = App(@"HKLM\Gone", "Gone App", "1.0");
        var added = App(@"HKLM\New", "New App", "2.0") with { KeyLastWriteUtc = Now.AddMinutes(-3) };

        var diff = ChangeDetector.Detect(Snap(gone), Known, Scan([Scopes.Machine64, Scopes.Machine32], added), Now);

        Assert.Contains(diff.Events, e => e.ChangeType == ChangeType.Removed && e.App.Name == "Gone App");
        var install = Assert.Single(diff.Events, e => e.ChangeType == ChangeType.Installed);
        Assert.Equal(Now.AddMinutes(-3), install.OccurredAt);
    }
}
