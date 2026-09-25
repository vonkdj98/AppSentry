using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AppSentry.Core.Sources;
using AppSentry.Models;

namespace AppSentry.Services;

/// <summary>
/// Real icons for apps, services and tasks, loaded off the UI thread and cached by source file:
///   - Store/MSIX: the package's logo from its AppxManifest (best scale variant)
///   - Registry apps: DisplayIcon ("file.exe,index" or an .ico), else the uninstaller's icon
///   - Services, drivers, tasks, folders: the binary they run
/// Anything under the Windows folder gets the generic glyph instead (they're all the same icon).
/// </summary>
public static class AppIconCache
{
    private const int IconPixels = 48;
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Throttle = new(4);
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static bool TryGetCached(InstalledApp app, out ImageSource? image)
    {
        image = null;
        var source = ResolveSource(app);
        if (source == null) return true; // nothing to load: the glyph it is
        return Cache.TryGetValue(source, out image);
    }

    public static async Task<ImageSource?> LoadAsync(InstalledApp app)
    {
        var source = ResolveSource(app);
        if (source == null) return null;
        if (Cache.TryGetValue(source, out var cached)) return cached;

        await Throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(source, out cached)) return cached;
            var image = await Task.Run(() => Load(source)).ConfigureAwait(false);
            Cache[source] = image;
            return image;
        }
        finally
        {
            Throttle.Release();
        }
    }

    // ── Resolve which file holds the icon ────────────────────────────────────

    private static string? ResolveSource(InstalledApp app)
    {
        if (app.PackageFullName.Length > 0 && app.InstallLocation.Length > 0)
            return "appx|" + app.InstallLocation;

        var raw = app.RawValues;
        var candidates = new List<string?>();
        if (raw != null)
        {
            candidates.Add(raw.GetValueOrDefault("DisplayIcon"));
            if (raw.TryGetValue("ImagePath", out var image) || raw.TryGetValue("ServiceDll", out image))
                candidates.Add(ServiceSource.ResolveBinary(raw.GetValueOrDefault("ServiceDll") is { Length: > 0 } dll ? dll : image));
            if (raw.TryGetValue("Actions", out var actions))
                candidates.Add(FolderOwnership.ExecutableOf(actions.Split(" ; ")[0]));
        }
        candidates.Add(app.UninstallString);
        if (app.Scope == "FILESYSTEM") candidates.Add(FirstExeIn(app.InstallLocation));

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var (file, index) = ParseIconLocation(candidate);
            if (file.Length == 0 || IsGeneric(file)) continue;
            return $"file|{file}|{index}";
        }
        return null;
    }

    private static (string File, int Index) ParseIconLocation(string location)
    {
        var text = Environment.ExpandEnvironmentVariables(location.Trim());
        var index = 0;
        string file;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            file = end > 1 ? text[1..end] : text.Trim('"');
            var rest = end > 0 ? text[(end + 1)..].Trim() : "";
            if (rest.StartsWith(',') && int.TryParse(rest[1..], out var i)) index = i;
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..], out var i))
            {
                index = i;
                text = text[..comma];
            }
            file = FolderOwnership.ExecutableOf(text);
        }
        return (Path.IsPathFullyQualified(file) ? file : "", index);
    }

    private static bool IsGeneric(string file)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();
        if (name is "msiexec.exe" or "rundll32.exe" or "cmd.exe" or "powershell.exe" or "svchost.exe" or "wscript.exe") return true;
        return WindowsDir.Length > 0 && file.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)
               && !file.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase); // MSI icons live in C:\Windows\Installer
    }

    private static string? FirstExeIn(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var name = Path.GetFileName(folder);
            var exes = Directory.EnumerateFiles(folder, "*.exe").Take(20).ToList();
            return exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Contains(name, StringComparison.OrdinalIgnoreCase))
                   ?? exes.FirstOrDefault(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private static ImageSource? Load(string source)
    {
        try
        {
            var parts = source.Split('|');
            return parts[0] == "appx" ? LoadPackageLogo(parts[1]) : LoadFileIcon(parts[1], int.Parse(parts[2]));
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadFileIcon(string file, int index)
    {
        if (!File.Exists(file)) return null;
        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return LoadBitmap(file);

        var icons = new IntPtr[1];
        var ids = new int[1];
        var count = PrivateExtractIcons(file, index, IconPixels, IconPixels, icons, ids, 1, 0);
        if (count == 0 || icons[0] == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    private static ImageSource? LoadPackageLogo(string installLocation)
    {
        var manifest = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifest)) return null;
        var doc = XDocument.Load(manifest);
        var logo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Logo" && e.Parent?.Name.LocalName == "Properties")?.Value;
        if (string.IsNullOrWhiteSpace(logo)) return null;

        var path = Path.Combine(installLocation, logo.Replace('/', '\\'));
        if (File.Exists(path)) return LoadBitmap(path);

        // Logos ship as scale/target-size variants: StoreLogo.scale-200.png, StoreLogo.targetsize-48.png, ...
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var variants = Directory.EnumerateFiles(dir, stem + "*" + Path.GetExtension(path)).ToList();
        var best = variants.FirstOrDefault(v => v.Contains("scale-200")) ?? variants.FirstOrDefault(v => v.Contains("targetsize-48"))
                   ?? variants.FirstOrDefault(v => v.Contains("scale-100")) ?? variants.FirstOrDefault();
        return best == null ? null : LoadBitmap(best);
    }

    private static ImageSource LoadBitmap(string file)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = IconPixels;
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
