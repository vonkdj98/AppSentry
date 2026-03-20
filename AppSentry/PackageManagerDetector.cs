using System.Diagnostics;

namespace AppSentry;

/// <summary>
/// Detects which package manager (Winget, Chocolatey, Scoop) installed a given app.
/// Caches results per scan to avoid repeated shell calls.
/// </summary>
internal class PackageManagerDetector
{
    private Dictionary<string, string>? _wingetPackages;
    private Dictionary<string, string>? _chocoPackages;
    private Dictionary<string, string>? _scoopPackages;

    private static bool? _hasWinget;
    private static bool? _hasChoco;
    private static bool? _hasScoop;

    /// <summary>
    /// Refreshes cached package lists from all detected managers.
    /// Call once per scan cycle.
    /// </summary>
    public void Refresh()
    {
        _wingetPackages = null;
        _chocoPackages = null;
        _scoopPackages = null;

        // Load in parallel
        var tasks = new List<Task>();

        if (HasWinget())
            tasks.Add(Task.Run(() => _wingetPackages = LoadWingetPackages()));
        if (HasChoco())
            tasks.Add(Task.Run(() => _chocoPackages = LoadChocoPackages()));
        if (HasScoop())
            tasks.Add(Task.Run(() => _scoopPackages = LoadScoopPackages()));

        try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(20)); }
        catch { }
    }

    /// <summary>
    /// Returns the package manager name for a given app, or empty string if unknown.
    /// </summary>
    public string Detect(string appName, string appVersion, string installLocation)
    {
        if (string.IsNullOrWhiteSpace(appName)) return "";

        var nameLower = appName.ToLowerInvariant();

        // Check Winget
        if (_wingetPackages != null)
        {
            foreach (var (name, id) in _wingetPackages)
            {
                if (nameLower.Contains(name) || name.Contains(nameLower))
                    return $"Winget ({id})";
            }
        }

        // Check Chocolatey
        if (_chocoPackages != null)
        {
            foreach (var (name, ver) in _chocoPackages)
            {
                if (nameLower.Contains(name) || name.Contains(nameLower))
                    return $"Chocolatey ({name} {ver})";
            }
        }

        // Check Scoop
        if (_scoopPackages != null)
        {
            foreach (var (name, ver) in _scoopPackages)
            {
                if (nameLower.Contains(name) || name.Contains(nameLower))
                    return $"Scoop ({name} {ver})";
            }
        }

        // Heuristic: check install location for scoop path
        if (!string.IsNullOrEmpty(installLocation))
        {
            if (installLocation.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase))
                return "Scoop";
            if (installLocation.Contains(@"\chocolatey\", StringComparison.OrdinalIgnoreCase))
                return "Chocolatey";
        }

        return "";
    }

    // ── Winget ────────────────────────────────────────────────────────────────

    private static bool HasWinget()
    {
        _hasWinget ??= CommandExists("winget", "--version");
        return _hasWinget.Value;
    }

    private static Dictionary<string, string> LoadWingetPackages()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = RunCommand("winget", "list --accept-source-agreements --disable-interactivity");
            if (string.IsNullOrEmpty(output)) return result;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Find the header line with "Name" and "Id"
            int headerIdx = -1;
            int nameStart = 0, nameEnd = 0, idStart = 0, idEnd = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var nIdx = line.IndexOf("Name", StringComparison.OrdinalIgnoreCase);
                var iIdx = line.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
                if (nIdx >= 0 && iIdx > nIdx)
                {
                    headerIdx = i;
                    nameStart = nIdx;
                    idStart = iIdx;
                    // Find next column after Id
                    var verIdx = line.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
                    idEnd = verIdx > idStart ? verIdx : line.Length;
                    nameEnd = idStart;
                    break;
                }
            }

            if (headerIdx < 0) return result;

            // Skip separator line (dashes)
            int dataStart = headerIdx + 1;
            if (dataStart < lines.Length && lines[dataStart].TrimStart().StartsWith("-"))
                dataStart++;

            for (int i = dataStart; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length < idEnd) continue;

                var name = SafeSubstring(line, nameStart, nameEnd - nameStart).Trim().ToLowerInvariant();
                var id = SafeSubstring(line, idStart, idEnd - idStart).Trim();

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    result[name] = id;
            }
        }
        catch { }
        return result;
    }

    // ── Chocolatey ────────────────────────────────────────────────────────────

    private static bool HasChoco()
    {
        _hasChoco ??= CommandExists("choco", "--version");
        return _hasChoco.Value;
    }

    private static Dictionary<string, string> LoadChocoPackages()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = RunCommand("choco", "list --limit-output");
            if (string.IsNullOrEmpty(output)) return result;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: "packagename|version"
                var parts = line.Trim().Split('|');
                if (parts.Length >= 2)
                    result[parts[0].ToLowerInvariant()] = parts[1];
            }
        }
        catch { }
        return result;
    }

    // ── Scoop ─────────────────────────────────────────────────────────────────

    private static bool HasScoop()
    {
        _hasScoop ??= CommandExists("scoop", "--version");
        return _hasScoop.Value;
    }

    private static Dictionary<string, string> LoadScoopPackages()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = RunCommand("scoop", "list");
            if (string.IsNullOrEmpty(output)) return result;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Skip header/separator lines
                var trimmed = line.Trim();
                if (trimmed.StartsWith("---") || trimmed.StartsWith("Name") || string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // Scoop list output: "name  version  source  updated"
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    result[parts[0].ToLowerInvariant()] = parts[1];
            }
        }
        catch { }
        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool CommandExists(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunCommand(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            return output;
        }
        catch { return ""; }
    }

    private static string SafeSubstring(string s, int start, int length)
    {
        if (start >= s.Length) return "";
        if (start + length > s.Length) length = s.Length - start;
        return s.Substring(start, length);
    }
}
