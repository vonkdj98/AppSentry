using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppSentry.Services;

/// <summary>
/// AppSentry's own accent (the indigo of its icon) in place of the Windows accent color, so the app looks the same
/// whatever the PC's personalization is set to (a near-black or neon system accent made buttons and toggles look
/// wrong). Light and dark each get a tuned set, with text contrast checked against the fills.
/// </summary>
public static class BrandTheme
{
    private sealed record Palette(
        Color Fill, Color FillHover, Color FillPressed, Color FillDisabled,
        Color Text, Color TextSecondary, Color TextTertiary, Color OnFill, Color Tint);

    private static readonly Palette LightPalette = new(
        Fill: Rgb(0x3B, 0x4F, 0xD8), FillHover: Rgb(0x33, 0x45, 0xC0), FillPressed: Rgb(0x2B, 0x3A, 0xA6), FillDisabled: Rgb(0xC4, 0xC4, 0xC4),
        Text: Rgb(0x33, 0x45, 0xC0), TextSecondary: Rgb(0x2B, 0x3A, 0xA6), TextTertiary: Rgb(0x25, 0x32, 0x8F), OnFill: Colors.White,
        Tint: Rgb(0xE8, 0xEB, 0xFC));

    private static readonly Palette DarkPalette = new(
        Fill: Rgb(0x52, 0x62, 0xE4), FillHover: Rgb(0x4A, 0x59, 0xD6), FillPressed: Rgb(0x42, 0x50, 0xC4), FillDisabled: Rgb(0x5C, 0x5C, 0x5C),
        Text: Rgb(0xA5, 0xB4, 0xFF), TextSecondary: Rgb(0xB8, 0xC4, 0xFF), TextTertiary: Rgb(0x8E, 0x9F, 0xF5), OnFill: Colors.White,
        Tint: Rgb(0x25, 0x2A, 0x4F));

    /// <summary>Whether the theme last applied is dark (for colors chosen in code).</summary>
    public static bool Dark { get; private set; }

    public static bool IsDark(string theme) => theme switch
    {
        "Light" => false,
        "Dark" => true,
        _ => SystemUsesDarkApps()
    };

    /// <summary>For tests: the accent fills (with the text on them) and the accent text, per theme.</summary>
    internal static (Color Fill, Color FillHover, Color FillPressed, Color OnFill, Color Text) AccentColors(bool dark)
    {
        var p = dark ? DarkPalette : LightPalette;
        return (p.Fill, p.FillHover, p.FillPressed, p.OnFill, p.Text);
    }

    /// <summary>WCAG contrast ratio between two colors (1 to 21).</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Puts the brand's accent into the application's resources (they win over the Fluent theme's).</summary>
    public static void Apply(Application app, string theme)
    {
        Dark = IsDark(theme);
        // A Windows high-contrast theme chooses its own colors for a reason: leave them alone.
        if (SystemParameters.HighContrast) return;
        var p = Dark ? DarkPalette : LightPalette;
        void Brush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
            if (key.EndsWith("Brush", StringComparison.Ordinal)) app.Resources[key[..^"Brush".Length]] = color; // the Color the theme pairs with it
        }

        Brush("AccentFillColorDefaultBrush", p.Fill);
        Brush("AccentFillColorSecondaryBrush", p.FillHover);
        Brush("AccentFillColorTertiaryBrush", p.FillPressed);
        Brush("AccentFillColorDisabledBrush", p.FillDisabled);
        Brush("AccentFillColorSelectedTextBackgroundBrush", p.Fill);
        Brush("AccentTextFillColorPrimaryBrush", p.Text);
        Brush("AccentTextFillColorSecondaryBrush", p.TextSecondary);
        Brush("AccentTextFillColorTertiaryBrush", p.TextTertiary);
        Brush("TextOnAccentFillColorPrimaryBrush", p.OnFill);
        Brush("TextOnAccentFillColorSecondaryBrush", Color.FromArgb(0xB3, p.OnFill.R, p.OnFill.G, p.OnFill.B));
        Brush("SystemFillColorAttentionBackgroundBrush", p.Tint);
        Brush("SystemFillColorAttentionBrush", p.Text);
        // Controls built on the theme's own keys (the accent button, focus rings) read these.
        Brush("AccentButtonBackground", p.Fill);
        Brush("AccentButtonBackgroundPointerOver", p.FillHover);
        Brush("AccentButtonBackgroundPressed", p.FillPressed);
        Brush("AccentButtonForeground", p.OnFill);
        Brush("AccentButtonForegroundPointerOver", p.OnFill);
        Brush("AccentButtonForegroundPressed", p.OnFill);
        Brush("AccentButtonBorderBrush", p.Fill);
        // Check boxes, radio buttons, text-box focus lines and links draw their "on" state from the same accent.
        foreach (var state in new[] { "Checked", "Indeterminate" })
        {
            Brush($"CheckBoxCheckBackgroundFill{state}", p.Fill);
            Brush($"CheckBoxCheckBackgroundFill{state}PointerOver", p.FillHover);
            Brush($"CheckBoxCheckBackgroundFill{state}Pressed", p.FillPressed);
            Brush($"CheckBoxCheckBackgroundStroke{state}", p.Fill);
            Brush($"CheckBoxCheckBackgroundStroke{state}PointerOver", p.FillHover);
            Brush($"CheckBoxCheckBackgroundStroke{state}Pressed", p.FillPressed);
        }
        Brush("CheckBoxCheckGlyphForeground", p.OnFill);
        Brush("CheckBoxCheckGlyphForegroundPressed", p.OnFill);
        Brush("RadioButtonOuterEllipseCheckedFill", p.Fill);
        Brush("RadioButtonOuterEllipseCheckedStroke", p.Fill);
        Brush("RadioButtonOuterEllipseCheckedStrokePointerOver", p.FillHover);
        Brush("RadioButtonCheckOuterEllipseCheckedFillPointerOver", p.FillHover);
        Brush("RadioButtonCheckOuterEllipseCheckedFillPressed", p.FillPressed);
        Brush("RadioButtonCheckOuterEllipseCheckedStrokePressed", p.FillPressed);
        Brush("TextControlFocusedBorderBrush", p.Fill);
        Brush("ComboBoxBorderBrushFocused", p.Fill);
        Brush("ControlFocusedBorderBrush", p.Fill);
        Brush("HyperlinkButtonForeground", p.Text);
        Brush("HyperlinkButtonForegroundPointerOver", p.TextSecondary);
        Brush("HyperlinkButtonForegroundPressed", p.TextTertiary);
        Brush("ProgressBarForeground", p.Fill);
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
