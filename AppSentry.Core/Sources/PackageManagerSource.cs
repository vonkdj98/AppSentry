using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AppSentry.Core.Engine;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Sources;

/// <summary>
/// Package-manager ownership, determined from each manager's own records instead of v1's
/// bidirectional substring match over `winget list` (which labelled nearly everything, since
/// winget lists every installed app, and matched "Git" to "GitHub Desktop").
///
///  - Chocolatey: the .registry snapshot choco writes per package names the exact uninstall
///    key it created; nuspec titles are the fallback. Read from disk, no process.
///  - Scoop: apps never touch the registry, so ~\scoop\apps is read as its own inventory
///    scope and scoop installs/updates/removals are detected like any other app.
///  - Winget: `winget list --source winget` (only rows winget can manage), exact name match.
///    Slow, so the engine runs it only when something new needs labelling.
/// </summary>
public sealed partial class PackageManagerSource
{
    private static readonly TimeSpan WingetRetryAfterFailure = TimeSpan.FromHours(1);

    private Dictionary<string, string> _chocoByKey = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _chocoByTitle = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _chocoStamp;
    private Dictionary<string, string>? _wingetByName;
    private DateTime _wingetLoadedUtc;
    private DateTime _wingetFailedUtc;

    public bool WingetLoaded => _wingetByName != null;

    // ── Labels ────────────────────────────────────────────────────────────────

    public string LabelFor(InstalledApp app)
    {
        if (!Scopes.IsRegistry(app.Scope)) return app.PackageManager;

        var tail = app.KeyPath[(app.KeyPath.LastIndexOf('\\') + 1)..];
        if (_chocoByKey.TryGetValue(tail, out var choco)) return choco;
        if (_chocoByTitle.TryGetValue(NameNormalizer.Normalize(app.Name), out choco)) return choco;

        if (_wingetByName != null)
        {
            if (_wingetByName.TryGetValue(app.Name, out var id)) return $"Winget ({id})";
            // winget truncates long names with "…"
            foreach (var (name, wid) in _wingetByName)
            {
                if (name.EndsWith('…') && app.Name.StartsWith(name[..^1], StringComparison.OrdinalIgnoreCase))
                    return $"Winget ({wid})";
            }
        }

        if (app.InstallLocation.Contains(@"\chocolatey\lib\", StringComparison.OrdinalIgnoreCase)) return "Chocolatey";
        if (app.InstallLocation.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase)) return "Scoop";
        return "";
    }

    // ── Chocolatey ────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\\Uninstall\\(?<key>[^<>""\\\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallKeyInText();

    [GeneratedRegex(@"^(?<id>.+?)\.(?<ver>\d+(\.\d+)*([\-+].*)?)$")]
    private static partial Regex PackageFolder();

    public void RefreshChocolatey(Dictionary<string, string> health)
    {
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        var lib = Path.Combine(root, "lib");
        var meta = Path.Combine(root, ".chocolatey");
        if (!Directory.Exists(lib)) return; // not installed; nothing to label

        try
        {
            var stamp = Max(Directory.GetLastWriteTimeUtc(lib), Directory.Exists(meta) ? Directory.GetLastWriteTimeUtc(meta) : default);
            if (stamp == _chocoStamp) return; // unchanged since last read

            var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(meta))
            {
                foreach (var dir in Directory.EnumerateDirectories(meta))
                {
                    var match = PackageFolder().Match(Path.GetFileName(dir));
                    if (!match.Success) continue;
                    var registryFile = Path.Combine(dir, ".registry");
                    if (!File.Exists(registryFile)) continue;
                    var label = $"Chocolatey ({match.Groups["id"].Value})";
                    foreach (Match key in UninstallKeyInText().Matches(File.ReadAllText(registryFile)))
                        byKey[key.Groups["key"].Value.Trim()] = label;
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(lib))
            {
                var id = Path.GetFileName(dir);
                var nuspec = Path.Combine(dir, id + ".nuspec");
                if (!File.Exists(nuspec)) continue;
                try
                {
                    var title = XDocument.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
                    var label = $"Chocolatey ({id})";
                    byTitle[NameNormalizer.Normalize(string.IsNullOrWhiteSpace(title) ? id : title)] = label;
                }
                catch (Exception)
                {
                    // Malformed nuspec: skip that package.
                }
            }

            _chocoByKey = byKey;
            _chocoByTitle = byTitle;
            _chocoStamp = stamp;
        }
        catch (Exception ex)
        {
            health["Chocolatey"] = $"failed: {ex.Message}";
        }
    }

    // ── Winget ────────────────────────────────────────────────────────────────

    /// <summary>Refreshes winget ids if they're missing or older than <paramref name="maxAge"/>.</summary>
    public bool RefreshWinget(EngineContext context, TimeSpan maxAge, Dictionary<string, string> health)
    {
        if (context.Mode == EngineMode.Service) return false; // winget is a per-user app; SYSTEM can't run it
        var now = DateTime.UtcNow;
        if (_wingetByName != null && now - _wingetLoadedUtc < maxAge) return false;
        if (now - _wingetFailedUtc < WingetRetryAfterFailure) return false;

        var run = ProcessRunner.Run("winget", "list --source winget --accept-source-agreements --disable-interactivity",
            TimeSpan.FromSeconds(60), Encoding.UTF8);
        if (!run.Started || run.TimedOut)
        {
            _wingetFailedUtc = now;
            if (run.Started) health["Winget"] = "timed out";
            return false;
        }

        var parsed = ParseWingetTable(run.StdOut);
        if (parsed.Count == 0 && run.ExitCode != 0)
        {
            _wingetFailedUtc = now;
            return false;
        }
        _wingetByName = parsed;
        _wingetLoadedUtc = now;
        return true;
    }

    internal static Dictionary<string, string> ParseWingetTable(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Progress spinners use \r; keep only what's after the last one on each line.
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l[(l.LastIndexOf('\r') + 1)..]).ToList();

        var header = lines.FindIndex(l => l.StartsWith("Name") && l.Contains(" Id ") && l.Contains("Version"));
        if (header < 0) return result;
        var idStart = lines[header].IndexOf(" Id ", StringComparison.Ordinal) + 1;
        var versionStart = lines[header].IndexOf("Version", idStart, StringComparison.Ordinal);

        foreach (var line in lines.Skip(header + 1))
        {
            if (line.StartsWith("---") || line.Length < versionStart) continue;
            var name = line[..idStart].Trim();
            var id = line[idStart..versionStart].Trim();
            if (name.Length > 0 && id.Length > 0 && !id.Contains(' ')) result[name] = id;
        }
        return result;
    }

    // ── Scoop inventory ───────────────────────────────────────────────────────

    public static void ScanScoop(EngineContext context, InventoryResult result)
    {
        var roots = new List<(string Dir, string Scope, string Owner)>();
        var globalRoot = Environment.GetEnvironmentVariable("SCOOP_GLOBAL");
        if (string.IsNullOrWhiteSpace(globalRoot))
            globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop");
        roots.Add((globalRoot, Scopes.Scoop("global"), "All users"));

        if (context.Mode == EngineMode.Service)
        {
            foreach (var p in EngineContext.GetUserProfiles())
                roots.Add((Path.Combine(p.ProfilePath, "scoop"), Scopes.Scoop(p.Sid), p.UserName));
        }
        else
        {
            var userRoot = Environment.GetEnvironmentVariable("SCOOP");
            if (string.IsNullOrWhiteSpace(userRoot))
                userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");
            roots.Add((userRoot, Scopes.Scoop(context.UserSid), context.UserName));
        }

        var problems = new List<string>();
        foreach (var (dir, scope, owner) in roots)
        {
            var apps = Path.Combine(dir, "apps");
            try
            {
                if (Directory.Exists(apps))
                {
                    foreach (var appDir in Directory.EnumerateDirectories(apps))
                    {
                        var app = ReadScoopApp(appDir, scope, owner);
                        if (app != null) result.Add(app);
                    }
                }
                // A missing scoop folder is a complete, empty scope: the first app installed later is a real install.
                result.CompletedScopes.Add(scope);
            }
            catch (Exception ex)
            {
                problems.Add($"{owner}: {ex.GetType().Name}");
            }
        }
        if (problems.Count > 0) result.Health["Scoop"] = "partial: " + string.Join("; ", problems);
    }

    private static InstalledApp? ReadScoopApp(string appDir, string scope, string owner)
    {
        var name = Path.GetFileName(appDir);
        var current = Path.Combine(appDir, "current");
        var manifest = Path.Combine(current, "manifest.json");
        if (!File.Exists(manifest)) return null;

        string version = "", description = "", homepage = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var rootElement = doc.RootElement;
            if (rootElement.TryGetProperty("version", out var v)) version = v.GetString() ?? "";
            if (rootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString() ?? "";
            if (rootElement.TryGetProperty("homepage", out var h) && h.ValueKind == JsonValueKind.String) homepage = h.GetString() ?? "";
        }
        catch (Exception)
        {
            // Unreadable manifest: still track the app, just without a version.
        }

        string bucket = "";
        try
        {
            var install = Path.Combine(current, "install.json");
            if (File.Exists(install))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(install));
                if (doc.RootElement.TryGetProperty("bucket", out var b)) bucket = b.GetString() ?? "";
            }
        }
        catch (Exception) { }

        DateTime? written = null;
        try { written = Directory.GetLastWriteTimeUtc(current); } catch { }

        return new InstalledApp
        {
            KeyPath = $@"{scope}\{name}",
            Scope = scope,
            Name = name,
            Version = version,
            Publisher = bucket.Length > 0 ? $"scoop/{bucket}" : "scoop",
            InstallLocation = current,
            InstallSource = homepage,
            InstallType = "Scoop",
            InstalledFor = owner,
            PackageManager = $"Scoop ({name})",
            KeyLastWriteUtc = written,
            RawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["version"] = version,
                ["bucket"] = bucket,
                ["description"] = description,
                ["homepage"] = homepage
            }
        };
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
