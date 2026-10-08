using System.Windows.Media;
using AppSentry.Infrastructure;
using AppSentry.Services;

namespace AppSentry.UiTests;

/// <summary>The brand colors keep their text readable (WCAG AA, 4.5:1 for normal text).</summary>
public class BrandTests
{
    private const double Aa = 4.5;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_on_the_accent_fill_is_readable_in_every_state(bool dark)
    {
        var (fill, hover, pressed, onFill, _) = BrandTheme.AccentColors(dark);
        foreach (var state in new[] { fill, hover, pressed })
            Assert.True(BrandTheme.Contrast(onFill, state) >= Aa, $"{(dark ? "dark" : "light")} {state}: {BrandTheme.Contrast(onFill, state):0.00}");
    }

    [Theory]
    [InlineData(false, 0xFF, 0xFF, 0xFF)]   // a light card
    [InlineData(false, 0xF3, 0xF3, 0xF3)]   // the light page
    [InlineData(true, 0x2B, 0x2B, 0x2B)]    // a dark card
    [InlineData(true, 0x20, 0x20, 0x20)]    // the dark page
    public void Accent_text_is_readable_on_the_page_and_cards(bool dark, byte r, byte g, byte b)
    {
        var (_, _, _, _, text) = BrandTheme.AccentColors(dark);
        var surface = Color.FromRgb(r, g, b);
        Assert.True(BrandTheme.Contrast(text, surface) >= Aa, $"{BrandTheme.Contrast(text, surface):0.00}");
    }

    [Fact]
    public void Every_letter_tile_is_readable()
    {
        foreach (var (back, front) in AppTile.AllColors())
            Assert.True(BrandTheme.Contrast(front, back) >= Aa, $"{front} on {back}: {BrandTheme.Contrast(front, back):0.00}");
    }
}
