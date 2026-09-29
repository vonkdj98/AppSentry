using AppSentry.Core.Sources;
using AppSentry.Models;
using AppSentry.Services;
using AppSentry.ViewModels;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AppSentry.UiTests;

public class HistoryLoadingTests
{
    [Fact]
    public void Large_history_loads_in_slim_pages_without_duplicates()
    {
        var backend = new FakeBackend();
        for (var i = 0; i < 6200; i++) backend.Add(ChangeType.Updated, $"App {i}", TimeSpan.FromMinutes(i));

        var shell = FakeBackend.Shell(backend);

        Assert.Equal(6200, shell.AllEvents.Count);
        Assert.Equal(6200, shell.AllEvents.Select(e => e.Id).Distinct().Count());
        Assert.Equal(3, backend.PageRequests);              // 1,000 first, then pages of 5,000
        Assert.All(shell.AllEvents, e => Assert.Null(e.App.RawValues));
        Assert.False(shell.IsLoadingHistory);
        Assert.Equal(6200, shell.TotalHistoryCount);
    }
}

public class EventDetailsTests
{
    [Fact]
    public void Slim_update_fetches_the_full_event_for_its_diff()
    {
        var backend = new FakeBackend();
        backend.Add(ChangeType.Updated, "7-Zip", TimeSpan.FromMinutes(5));
        var shell = FakeBackend.Shell(backend);

        var details = shell.Activity.Details!;

        Assert.Equal(1, backend.FullEventRequests);
        var first = details.DiffRows.First();
        Assert.Equal("DisplayVersion", first.Name);
        Assert.Equal(DiffKind.Changed, first.Kind);
        Assert.Equal(("1.0", "2.0"), (first.OldValue, first.NewValue));
        Assert.DoesNotContain(details.DiffRows, r => r.Kind == DiffKind.Same);

        details.ShowUnchanged = true;
        Assert.Contains(details.DiffRows, r => r.Kind == DiffKind.Same);
        Assert.StartsWith("Updated from 1.0 to 2.0", details.Sentence);
    }

    [Fact]
    public void Service_change_shows_service_facts_and_no_uninstall()
    {
        var backend = new FakeBackend();
        backend.AddServiceBinaryChange("ContosoAgent", TimeSpan.FromMinutes(5));
        var shell = FakeBackend.Shell(backend);

        var details = shell.Activity.Details!;

        Assert.False(details.CanUninstall);
        Assert.True(details.CanReview);
        Assert.Equal(Tone.Caution, details.AttentionBadge!.Tone);
        Assert.Contains(details.Facts, f => f.Label == "Binary" && f.Value == @"C:\Users\Public\evil.exe");
        Assert.Contains(details.Facts, f => f.Label == "Runs as" && f.Value == "LocalSystem");
        Assert.DoesNotContain(details.Facts, f => f.Label == "Install source");
        Assert.Contains(details.DiffRows, r => r.Name == "ImagePath" && r.Kind == DiffKind.Changed);
    }

    [Fact]
    public void Removed_app_cannot_be_uninstalled_again()
    {
        var backend = new FakeBackend();
        backend.Add(ChangeType.Removed, "VLC media player", TimeSpan.FromMinutes(5),
            tweak: e => e with { App = e.App with { UninstallString = @"C:\Program Files\VLC\uninstall.exe" } });
        var shell = FakeBackend.Shell(backend);

        Assert.False(shell.Activity.Details!.CanUninstall);
    }

    [Fact]
    public void Change_record_is_a_bulleted_ticket_summary()
    {
        var backend = new FakeBackend();
        var ev = backend.Add(ChangeType.Updated, "7-Zip", TimeSpan.FromMinutes(5), changedBy: @"CONTOSO\alex");

        var text = EventActions.ChangeRecordText(ev);

        Assert.StartsWith("Subject: Updated 7-Zip 1.0 → 2.0 on", text);
        Assert.Contains(@"- Changed by: CONTOSO\alex", text);
        Assert.All(text.Split('\n').Skip(2).Where(l => l.Trim().Length > 0), l => Assert.StartsWith("- ", l));
    }
}

public class OtherPageTests
{
    [Fact]
    public void Exclusion_rows_show_match_counts_and_dont_record_implies_dont_notify()
    {
        var backend = new FakeBackend { Exclusions = [new ExclusionEntry("Contoso*", true, false)] };
        backend.Add(ChangeType.Installed, "Contoso VPN Client", TimeSpan.FromHours(1));
        backend.Add(ChangeType.Updated, "Contoso Agent", TimeSpan.FromHours(2));
        backend.Add(ChangeType.Updated, "7-Zip", TimeSpan.FromHours(3));
        var shell = FakeBackend.Shell(backend);

        var row = Assert.Single(shell.Exclusions.Rows);
        Assert.Equal("Matches 2 past events", row.MatchText);

        row.DontNotify = false;
        row.DontLog = true;
        Assert.True(row.DontNotify);
        Assert.Equal("Not recorded at all", row.Effect);
    }

    [Fact]
    public void Installed_page_filters_by_scope_and_recency_and_sorts_by_size()
    {
        var backend = new FakeBackend();
        backend.Inventory.AddRange(
        [
            new InstalledApp { KeyPath = "A", Name = "Big", Scope = Scopes.Machine64, EstimatedSizeKb = 900_000, InstallDate = DateTime.Today.AddDays(-100).ToString("yyyyMMdd") },
            new InstalledApp { KeyPath = "B", Name = "Small", Scope = Scopes.Machine64, EstimatedSizeKb = 1_000, InstallDate = DateTime.Today.AddDays(-2).ToString("yyyyMMdd") },
            new InstalledApp { KeyPath = "C", Name = "Store App", Scope = @"STORE\S-1-5-21-1", PackageFullName = "x", InstallDate = DateTime.Today.ToString("yyyyMMdd") }
        ]);
        var shell = FakeBackend.Shell(backend);
        var vm = shell.Installed;
        vm.LoadAsync().GetAwaiter().GetResult();

        List<string> Names() => vm.Apps!.Cast<InstalledItemViewModel>().Select(i => i.Title).ToList();

        vm.SelectedSort = vm.SortOptions.Single(o => o.Value == AppSort.Size);
        Assert.Equal("Big", Names()[0]);
        Assert.Equal(1.0, vm.Apps!.Cast<InstalledItemViewModel>().First().SizeFraction);

        vm.SelectedScope = vm.ScopeOptions.Single(o => o.Value == ScopeKind.Store);
        Assert.Equal(["Store App"], Names());

        vm.SelectedScope = vm.ScopeOptions[0];
        vm.RecentOnly = true;
        Assert.Equal(["Small", "Store App"], Names().Order().ToList());
    }

    [Fact]
    public void Services_page_hides_microsoft_components_by_default()
    {
        var backend = new FakeBackend
        {
            Persistence = new PersistenceInventory
            {
                Services =
                [
                    new ServiceRecord { Name = "ContosoAgent", DisplayName = "Contoso Agent", IsMicrosoft = false },
                    new ServiceRecord { Name = "Spooler", DisplayName = "Print Spooler", IsMicrosoft = true },
                    new ServiceRecord { Name = "cfltr", DisplayName = "Contoso Filter", IsDriver = true }
                ],
                Tasks = [new TaskRecord { Path = @"\Contoso\Updater", RunAs = "SYSTEM", Highest = true }]
            }
        };
        var shell = FakeBackend.Shell(backend);
        var vm = shell.Persistence;
        vm.LoadAsync().GetAwaiter().GetResult();

        List<string> Titles() => vm.Rows!.Cast<PersistenceRowViewModel>().Select(r => r.Title).ToList();

        Assert.Equal(["Contoso Agent"], Titles());
        Assert.Contains("1 Microsoft", vm.CountText);

        vm.HideMicrosoft = false;
        Assert.Equal(2, Titles().Count);

        vm.IsDrivers = true;
        Assert.Equal(["Contoso Filter"], Titles());

        vm.IsTasks = true;
        Assert.Equal([@"Contoso\Updater"], Titles());
        Assert.Contains("highest privileges", vm.Rows!.Cast<PersistenceRowViewModel>().Single().Subtitle);
    }
}

public class NotificationRuleTests
{
    private static ChangeEvent Ev(ChangeType type, DetectionSource source = DetectionSource.Registry, bool silent = false) => new()
    {
        App = new InstalledApp { Name = "App", RawValues = [] },
        ChangeType = type,
        Source = source,
        Silent = silent
    };

    [Fact]
    public void Routine_updates_are_quiet_by_default_but_flagged_items_always_notify()
    {
        var settings = new UiSettings();

        Assert.True(ToastService.ShouldNotify(Ev(ChangeType.Installed), settings));
        Assert.False(ToastService.ShouldNotify(Ev(ChangeType.Updated), settings));
        Assert.False(ToastService.ShouldNotify(Ev(ChangeType.Installed, silent: true), settings));

        // A new driver is flagged; it notifies even with installs switched off.
        settings.NotifyTypes.Remove(ChangeType.Installed);
        Assert.True(ToastService.ShouldNotify(Ev(ChangeType.Installed, DetectionSource.Driver), settings));

        settings.AlwaysNotifyAttention = false;
        Assert.False(ToastService.ShouldNotify(Ev(ChangeType.Installed, DetectionSource.Driver), settings));
    }

    [Fact]
    public void Pausing_silences_everything_until_it_expires()
    {
        var settings = new UiSettings { NotificationsPausedUntilUtc = DateTime.UtcNow.AddHours(1) };
        Assert.False(ToastService.ShouldNotify(Ev(ChangeType.Failed), settings));

        settings.NotificationsPausedUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(ToastService.ShouldNotify(Ev(ChangeType.Failed), settings));
    }

    [Fact]
    public void Notifications_stay_on_screen_until_dismissed_by_default_and_critical_ones_can_always()
    {
        var settings = new UiSettings();
        Assert.Equal((ToastScenario.Reminder, (ToastDuration?)null), ToastService.OnScreenBehavior(settings, critical: false));

        settings.OnScreen = NotificationOnScreen.Long;
        Assert.Equal(((ToastScenario?)null, ToastDuration.Long), ToastService.OnScreenBehavior(settings, critical: false));
        Assert.Equal((ToastScenario.Reminder, (ToastDuration?)null), ToastService.OnScreenBehavior(settings, critical: true));

        settings.OnScreen = NotificationOnScreen.WindowsDefault;
        settings.KeepCriticalOnScreen = false;
        Assert.Equal(((ToastScenario?)null, (ToastDuration?)null), ToastService.OnScreenBehavior(settings, critical: true));
    }
}

public class DisplayTests
{
    [Fact]
    public void Day_labels_and_summaries_read_naturally()
    {
        Assert.Equal("Today", Display.DayLabel(DateTime.UtcNow));
        Assert.Equal("Yesterday", Display.DayLabel(DateTime.Today.AddDays(-1).AddHours(12).ToUniversalTime()));

        var update = new ChangeEvent { App = new InstalledApp { Name = "7-Zip", Version = "24.08" }, ChangeType = ChangeType.Updated, PreviousVersion = "23.01", ChangedBy = @"CONTOSO\alex" };
        Assert.Equal(@"23.01 → 24.08 · CONTOSO\alex", Display.Summary(update, Core.Detection.Attention.None));
        Assert.Equal("Contoso Agent", Display.CleanName("[Windows Service] Contoso Agent"));
        Assert.Equal("5.9 MB", Display.Size(6_200_000));
    }
}
