using AppSentry.Core.Detection;
using AppSentry.Core.Engine;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Sources;

public sealed record FsRoot(string Path, string Label, string InstalledFor);

/// <summary>Persisted folder baseline. Pending = folders seen but not yet old enough to report.</summary>
public sealed class FsBaseline
{
    public Dictionary<string, List<string>> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> Pending { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Detects top-level folders appearing in / disappearing from Program Files, Program Files (x86)
/// and each user's %LOCALAPPDATA%\Programs (where per-user VS Code, Slack, Discord, Zoom land).
///
///  - The baseline is persisted, so drops made while AppSentry was closed are caught.
///  - A new folder is held for <see cref="Settle"/> before it's reported: installers create
///    their folder before writing the registry key, and v1 reported that half-finished state
///    as a "portable" install and then the real install again one scan later.
///  - A folder that belongs to a known app (current, previous or recently removed) is never
///    reported — the registry event already covers it.
/// </summary>
public static class FileSystemSource
{
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    // Exact names only. v1 ignored every folder *starting with* "microsoft", which hid
    // Microsoft Office, SQL Server, VS Code and Teams.
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Internet Explorer", "ModifiableWindowsApps", "MSBuild", "Reference Assemblies",
        "Uninstall Information", "InstallShield Installation Information", "Package Cache",
        "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player",
        "Windows Multimedia Platform", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell", "Common", "desktop.ini"
    };

    public static List<FsRoot> GetRoots(EngineContext context)
    {
        var roots = new List<FsRoot>();
        void AddRoot(string? path, string label, string owner)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (roots.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(new FsRoot(path, label, owner));
        }

        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files", "All users");
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)", "All users");

        if (context.Mode == EngineMode.Service)
        {
            foreach (var profile in EngineContext.GetUserProfiles())
                AddRoot(Path.Combine(profile.ProfilePath, "AppData", "Local", "Programs"), $"{profile.UserName}'s AppData\\Local\\Programs", profile.UserName);
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) AddRoot(Path.Combine(local, "Programs"), "AppData\\Local\\Programs", context.UserName);
        }
        return roots;
    }

    public static (List<ChangeEvent> Events, FsBaseline Next, DateTime? FollowUpUtc) Scan(
        FsBaseline? baseline, IReadOnlyList<FsRoot> roots, FolderOwnership ownership, DateTime nowUtc, Dictionary<string, string> health)
    {
        var events = new List<ChangeEvent>();
        var next = new FsBaseline();
        DateTime? followUp = null;
        var problems = new List<string>();
        baseline ??= new FsBaseline();

        foreach (var root in roots)
        {
            List<string> current;
            try
            {
                if (!Directory.Exists(root.Path))
                {
                    // Nothing there (yet). An empty known root lets its first folder count as an install.
                    next.Roots[root.Path] = baseline.Roots.TryGetValue(root.Path, out var prior) && prior.Count > 0 ? prior : [];
                    continue;
                }
                current = new DirectoryInfo(root.Path).EnumerateDirectories()
                    .Where(d => !IsIgnored(d))
                    .Select(d => d.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                problems.Add($"{root.Label}: {ex.GetType().Name}");
                if (baseline.Roots.TryGetValue(root.Path, out var keep)) next.Roots[root.Path] = keep; // carry forward
                continue;
            }

            if (!baseline.Roots.TryGetValue(root.Path, out var knownList))
            {
                next.Roots[root.Path] = current; // first sight: baseline silently
                continue;
            }

            var known = new HashSet<string>(knownList, StringComparer.OrdinalIgnoreCase);
            var nextKnown = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);

            foreach (var name in current.Where(n => !known.Contains(n)))
            {
                var path = Path.Combine(root.Path, name);
                if (!baseline.Pending.TryGetValue(path, out var firstSeen))
                {
                    next.Pending[path] = nowUtc;
                    followUp = Earliest(followUp, nowUtc + Settle + TimeSpan.FromSeconds(5));
                    continue;
                }
                if (nowUtc - firstSeen < Settle)
                {
                    next.Pending[path] = firstSeen;
                    followUp = Earliest(followUp, firstSeen + Settle + TimeSpan.FromSeconds(5));
                    continue;
                }

                nextKnown.Add(name);
                if (ownership.Owns(path, name)) continue;
                events.Add(FolderEvent(root, path, name, ChangeType.Installed, nowUtc, SafeCreationTime(path)));
            }

            foreach (var name in known.Where(n => !currentSet.Contains(n)))
            {
                nextKnown.Remove(name);
                var path = Path.Combine(root.Path, name);
                if (ownership.Owns(path, name)) continue;
                events.Add(FolderEvent(root, path, name, ChangeType.Removed, nowUtc, null));
            }

            next.Roots[root.Path] = nextKnown.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        health["Folders"] = problems.Count == 0 ? "ok" : "partial: " + string.Join("; ", problems);
        return (events, next, followUp);
    }

    private static ChangeEvent FolderEvent(FsRoot root, string path, string name, ChangeType type, DateTime nowUtc, DateTime? occurred) => new()
    {
        App = new InstalledApp
        {
            KeyPath = $@"FILESYSTEM\{path}",
            Scope = "FILESYSTEM",
            Name = name,
            InstallLocation = path,
            InstallSource = $"Folder in {root.Label}",
            InstallType = "Portable/Unknown",
            InstalledFor = root.InstalledFor
        },
        ChangeType = type,
        DetectedAt = nowUtc,
        OccurredAt = occurred,
        Source = DetectionSource.FileSystem,
        Details = type == ChangeType.Installed
            ? "New folder with no matching Add/Remove Programs entry"
            : "Folder removed with no matching uninstall"
    };

    private static bool IsIgnored(DirectoryInfo dir)
    {
        if (Ignored.Contains(dir.Name)) return true;
        if (dir.Name.StartsWith('.') || dir.Name.StartsWith('_') || dir.Name.StartsWith('$')) return true;
        try
        {
            var attrs = dir.Attributes;
            return (attrs & FileAttributes.Hidden) != 0 && (attrs & FileAttributes.System) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? SafeCreationTime(string path)
    {
        try { return Directory.GetCreationTimeUtc(path); } catch { return null; }
    }

    private static DateTime? Earliest(DateTime? a, DateTime b) => a is { } x && x < b ? x : b;
}

/// <summary>
/// Which folders are already explained by an Add/Remove Programs entry: install locations,
/// uninstaller and icon paths, plus folder names that match a product name.
/// </summary>
public sealed class FolderOwnership
{
    private readonly List<string> _dirs = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public FolderOwnership(IEnumerable<InstalledApp> apps)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var app in apps)
        {
            if (app.Scope == "FILESYSTEM") continue;
            var name = NameNormalizer.Normalize(app.Name);
            if (name.Length >= 3) _names.Add(name);

            foreach (var candidate in new[]
                     {
                         app.InstallLocation,
                         DirectoryOf(ExecutableOf(app.UninstallString)),
                         DirectoryOf(ExecutableOf(app.QuietUninstallString)),
                         DirectoryOf(ExecutableOf(app.RawValues?.GetValueOrDefault("DisplayIcon") ?? ""))
                     })
            {
                var dir = ChangeDetector.NormalizePath(candidate ?? "");
                if (dir.Length < 4) continue;
                if (windows.Length > 0 && dir.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                _dirs.Add(dir);
            }
        }
    }

    public bool Owns(string folderPath, string folderName)
    {
        var folder = ChangeDetector.NormalizePath(folderPath);
        if (_dirs.Any(d => d.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                           d.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)))
            return true;
        var name = NameNormalizer.Normalize(folderName);
        return name.Length >= 3 && _names.Contains(name);
    }

    /// <summary>Executable path from a command line such as "\"C:\x\unins000.exe\" /S" or "C:\x\u.exe,0".</summary>
    public static string ExecutableOf(string commandLine)
    {
        var s = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (s.Length == 0) return "";
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }
        var comma = s.IndexOf(',');
        if (comma > 0) s = s[..comma];
        foreach (var ext in new[] { ".exe", ".dll", ".ico", ".msi" })
        {
            var idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return s[..(idx + ext.Length)];
        }
        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }

    private static string? DirectoryOf(string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetDirectoryName(path); } catch { return null; }
    }
}
