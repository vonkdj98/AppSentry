using AppSentry.Core.Backend;
using AppSentry.Models;

namespace AppSentry;

/// <summary>
/// UI-side view of the exclusion list. The engine owns the list (it applies exclusions before
/// anything is persisted); this wrapper keeps the old Add/Remove API the forms use and pushes
/// every change back through the backend.
/// </summary>
internal sealed class ExclusionStore
{
    private readonly IMonitorBackend _backend;
    private List<ExclusionEntry> _entries;

    private ExclusionStore(IMonitorBackend backend, List<ExclusionEntry> entries)
    {
        _backend = backend;
        _entries = entries;
    }

    public static async Task<ExclusionStore> LoadAsync(IMonitorBackend backend)
    {
        List<ExclusionEntry> entries;
        try { entries = await backend.GetExclusionsAsync(); }
        catch { entries = []; }
        return new ExclusionStore(backend, entries);
    }

    public IReadOnlyList<ExclusionEntry> Entries => _entries;

    public ExclusionEntry? GetEntry(string appName) =>
        _entries.FirstOrDefault(e => e.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

    public void Add(ExclusionEntry entry)
    {
        var next = _entries
            .Where(e => !e.AppName.Equals(entry.AppName, StringComparison.OrdinalIgnoreCase))
            .Append(entry)
            .OrderBy(e => e.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Save(next);
    }

    public void Remove(string appName) =>
        Save(_entries.Where(e => !e.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase)).ToList());

    private void Save(List<ExclusionEntry> next)
    {
        try
        {
            // Backends never capture the UI context, so blocking here can't deadlock.
            _backend.SaveExclusionsAsync(next).GetAwaiter().GetResult();
            _entries = next;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save exclusions: {ex.Message}", "Exclusions",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
