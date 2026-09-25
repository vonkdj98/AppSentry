using System.Text.RegularExpressions;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Detection;

/// <summary>
/// Matches app names against the exclusion list. A pattern matches when it is:
///  - an exact, case-insensitive name ("Google Chrome"),
///  - a wildcard pattern using * and ? ("7-Zip*", "[Scheduled Task] \Adobe*"), or
///  - the same product once versions/arch are stripped, so an exclusion for
///    "7-Zip 23.01 (x64)" keeps matching after the app updates to 24.08.
/// </summary>
public sealed class ExclusionMatcher
{
    private readonly List<(ExclusionEntry Entry, Regex? Wildcard, string Normalized)> _rules;

    public ExclusionMatcher(IEnumerable<ExclusionEntry> entries)
    {
        _rules = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.AppName))
            .Select(e => (e, ToWildcard(e.AppName), NameNormalizer.Normalize(e.AppName)))
            .ToList();
    }

    public ExclusionEntry? Match(string appName)
    {
        if (string.IsNullOrEmpty(appName) || _rules.Count == 0) return null;
        var normalized = NameNormalizer.Normalize(appName);

        foreach (var (entry, wildcard, entryNormalized) in _rules)
        {
            if (wildcard != null)
            {
                if (wildcard.IsMatch(appName)) return entry;
            }
            else if (entry.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                     (entryNormalized.Length >= 3 && entryNormalized == normalized))
            {
                return entry;
            }
        }
        return null;
    }

    public bool ExcludesLogging(string appName) => Match(appName)?.ExcludeLogging == true;

    public bool ExcludesNotifications(string appName) =>
        Match(appName) is { } e && (e.ExcludeNotifications || e.ExcludeLogging);

    private static Regex? ToWildcard(string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0) return null;
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
