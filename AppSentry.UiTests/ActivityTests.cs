using AppSentry.Models;
using AppSentry.ViewModels;

namespace AppSentry.UiTests;

public class ActivityTests
{
    private static (FakeBackend Backend, ShellViewModel Shell) Setup()
    {
        var backend = new FakeBackend();
        backend.Add(ChangeType.Installed, "Zoom Workplace", TimeSpan.FromHours(1), changedBy: @"CONTOSO\alex");
        backend.Add(ChangeType.Updated, "7-Zip", TimeSpan.FromHours(2), publisher: "Igor Pavlov");
        backend.Add(ChangeType.Removed, "VLC media player", TimeSpan.FromHours(3));
        backend.AddServiceBinaryChange("ContosoAgent", TimeSpan.FromHours(4));
        backend.Add(ChangeType.Failed, "Contoso VPN Client", TimeSpan.FromHours(5), DetectionSource.EventLog);
        backend.Add(ChangeType.Installed, "Old Tool", TimeSpan.FromDays(40));
        return (backend, FakeBackend.Shell(backend));
    }

    private static List<string> Shown(ActivityViewModel vm) =>
        vm.Events!.Cast<EventItemViewModel>().Select(i => i.Title).ToList();

    [Fact]
    public void Default_view_is_the_last_7_days_with_no_type_chips_selected()
    {
        var (_, shell) = Setup();
        var vm = shell.Activity;

        Assert.False(vm.HasFiltersActive);
        Assert.Equal(5, vm.ShownCount);                     // "Old Tool" is 40 days old
        Assert.DoesNotContain("Old Tool", Shown(vm));
        Assert.Equal(1, vm.InstalledCount);
        Assert.Equal(1, vm.UpdatedCount);
        Assert.Equal(1, vm.RemovedCount);
        Assert.Equal(2, vm.NeedsLookCount);                 // service binary change + failed install
        Assert.NotNull(vm.Selected);                        // newest row is selected for the details pane
    }

    [Fact]
    public void Type_chips_narrow_the_list_and_combine()
    {
        var (_, shell) = Setup();
        var vm = shell.Activity;

        vm.FilterUpdated = true;
        Assert.Equal(["7-Zip"], Shown(vm));

        vm.FilterRemoved = true;
        Assert.Equal(["7-Zip", "VLC media player"], Shown(vm));
        Assert.True(vm.HasFiltersActive);
        // Cards ignore the chips: they describe the period.
        Assert.Equal(1, vm.InstalledCount);
    }

    [Fact]
    public void Cards_filter_to_their_type_and_clear_filters_resets()
    {
        var (_, shell) = Setup();
        var vm = shell.Activity;

        vm.CardCommand.Execute("Installed");
        Assert.True(vm.FilterInstalled);
        Assert.False(vm.FilterUpdated);
        Assert.Equal(["Zoom Workplace"], Shown(vm));

        vm.CardCommand.Execute("attention");
        Assert.True(vm.NeedsLookOnly);
        Assert.False(vm.FilterInstalled);
        Assert.Equal(2, vm.ShownCount);

        vm.ClearFiltersCommand.Execute(null);
        Assert.False(vm.HasFiltersActive);
        Assert.Equal(5, vm.ShownCount);
    }

    [Fact]
    public void Source_range_and_search_filters()
    {
        var (_, shell) = Setup();
        var vm = shell.Activity;

        vm.SelectedSource = vm.SourceOptions.Single(o => o.Value == SourceKind.Services);
        Assert.Equal(["ContosoAgent"], Shown(vm));

        vm.SelectedSource = vm.SourceOptions[0];
        vm.SelectedRange = vm.RangeOptions[^1];             // all time
        Assert.Contains("Old Tool", Shown(vm));

        vm.SearchText = "contoso\\alex";                  // who did it is searchable
        vm.Refresh();                                       // (the debounce timer needs a running dispatcher)
        Assert.Equal(["Zoom Workplace"], Shown(vm));
    }

    [Fact]
    public void Reviewing_clears_the_needs_a_look_count_everywhere()
    {
        var (_, shell) = Setup();
        Assert.Equal(2, shell.NeedsLookCount);

        var flagged = shell.AllEvents.First(e => e.Source == DetectionSource.Service);
        shell.SetReviewed([flagged], true);

        Assert.Equal(1, shell.NeedsLookCount);
        Assert.Equal(1, shell.Activity.NeedsLookCount);
        Assert.Contains(flagged.Id, shell.Settings.ReviewedEventIds);

        shell.Activity.MarkAllReviewedCommand.Execute(null);
        Assert.Equal(0, shell.NeedsLookCount);
    }

    [Fact]
    public void Selecting_an_event_outside_the_filters_makes_it_visible()
    {
        var (backend, shell) = Setup();
        var vm = shell.Activity;
        vm.FilterUpdated = true;

        var old = backend.Events.Single(e => e.App.Name == "Old Tool");
        vm.Select(old.Id);

        Assert.False(vm.FilterUpdated);
        Assert.Null(vm.SelectedRange.Value);                // widened to all time
        Assert.Equal("Old Tool", vm.Selected!.Title);
    }

    [Fact]
    public void Live_events_are_added_once_and_raise_notifications()
    {
        var (backend, shell) = Setup();
        var notified = new List<ChangeEvent>();
        shell.NewEvents += e => notified.AddRange(e);

        var fresh = backend.Add(ChangeType.Installed, "Git", TimeSpan.Zero);
        backend.RaiseEvents([fresh]);
        backend.RaiseEvents([fresh]);                       // duplicate delivery (e.g. reconnect)

        Assert.Single(shell.AllEvents, e => e.Id == fresh.Id);
        Assert.Single(notified);
        Assert.Equal("Git", shell.Activity.Events!.Cast<EventItemViewModel>().First().Title);
    }

    [Fact]
    public void Empty_history_explains_the_baseline()
    {
        var shell = FakeBackend.Shell(new FakeBackend());
        Assert.True(shell.Activity.IsEmpty);
        Assert.Equal("Watching for changes", shell.Activity.EmptyTitle);
    }
}
