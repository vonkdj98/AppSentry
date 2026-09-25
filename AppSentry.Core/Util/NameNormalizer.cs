using System.Text.RegularExpressions;

namespace AppSentry.Core.Util;

/// <summary>
/// Reduces a product name to a version-, architecture- and language-free form so that
/// "Mozilla Firefox (x64 en-US)" and "Mozilla Firefox 131.0 (x64 en-US)" compare equal.
/// Used for upgrade correlation, event-log matching, folder ownership and exclusions.
/// </summary>
public static partial class NameNormalizer
{
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|win64|win32|64-bit|32-bit|64bit|32bit|[a-z]{2}-[a-z]{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchOrLanguage();

    [GeneratedRegex(@"\bv?\d+(?:[.\-_]\d+)*[a-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"[\s\-–—_,:;|/]+")]
    private static partial Regex Separators();

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.ToLowerInvariant();
        s = Bracketed().Replace(s, " ");
        s = ArchOrLanguage().Replace(s, " ");
        s = VersionToken().Replace(s, " ");
        s = Separators().Replace(s, " ").Trim(' ', '.', '-');
        return s.Length == 0 ? name.Trim().ToLowerInvariant() : s;
    }

    /// <summary>Publisher comparison that tolerates suffixes like ", Inc." and "Corporation".</summary>
    public static bool PublishersCompatible(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        var na = CorePublisher(a);
        var nb = CorePublisher(b);
        return na.Length == 0 || nb.Length == 0 || na == nb || na.StartsWith(nb) || nb.StartsWith(na);
    }

    private static string CorePublisher(string publisher)
    {
        var s = Normalize(publisher);
        foreach (var suffix in new[] { " incorporated", " inc", " corporation", " corp", " llc", " ltd", " limited", " gmbh", " co", " s.a", " ag" })
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal)) s = s[..^suffix.Length];
        }
        return s.Trim(' ', '.', ',');
    }
}
