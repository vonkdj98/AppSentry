using AppSentry.Core.Detection;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Tests;

public class MatchingTests
{
    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox 131.0 (x64 en-US)")]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip 24.08 (x64)")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810")]
    [InlineData("Java 8 Update 381", "Java 8 Update 391")]
    [InlineData("Zoom Workplace (64-bit)", "Zoom Workplace (32-bit)")]
    public void Normalize_ignores_versions_arch_and_language(string a, string b) =>
        Assert.Equal(NameNormalizer.Normalize(a), NameNormalizer.Normalize(b));

    [Theory]
    [InlineData("Google Chrome", "Mozilla Firefox")]
    [InlineData("Notepad++", "Notepad")]
    public void Normalize_keeps_different_products_apart(string a, string b) =>
        Assert.NotEqual(NameNormalizer.Normalize(a), NameNormalizer.Normalize(b));

    [Theory]
    [InlineData("Google LLC", "Google, Inc.", true)]
    [InlineData("Microsoft Corporation", "Microsoft Corp.", true)]
    [InlineData("Mozilla", "Google LLC", false)]
    [InlineData("", "Anything", true)]
    public void Publisher_compatibility(string a, string b, bool expected) =>
        Assert.Equal(expected, NameNormalizer.PublishersCompatible(a, b));

    [Fact]
    public void Exclusions_match_exact_wildcard_and_version_free_names()
    {
        var matcher = new ExclusionMatcher(
        [
            new ExclusionEntry("7-Zip 23.01 (x64)", true, false),
            new ExclusionEntry("[Scheduled Task] \\Adobe*", true, true),
            new ExclusionEntry("Google Chrome", true, false)
        ]);

        Assert.True(matcher.ExcludesNotifications("7-Zip 24.08 (x64)"));   // survives the update
        Assert.False(matcher.ExcludesLogging("7-Zip 24.08 (x64)"));
        Assert.True(matcher.ExcludesLogging("[Scheduled Task] \\Adobe Acrobat Update Task"));
        Assert.True(matcher.ExcludesNotifications("google chrome"));
        Assert.Null(matcher.Match("Google Chrome Canary Helper Tool"));
        Assert.Null(matcher.Match("Mozilla Firefox"));
    }
}
