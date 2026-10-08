using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppSentry.Services;

namespace AppSentry.Infrastructure;

/// <summary>
/// An app's picture: its own icon when it has one; otherwise a rounded tile with its first letter in a color taken
/// from its name (so a list of programs without icons isn't a column of identical boxes); or, for things that
/// aren't apps (a service, a task, a driver), the glyph for the kind of thing.
/// </summary>
public sealed class AppTile : Grid
{
    public static readonly DependencyProperty IconProperty = Property<ImageSource?>(nameof(Icon), null);
    public static readonly DependencyProperty LabelProperty = Property(nameof(Label), "");
    /// <summary>A glyph for non-app things; leave empty (or the generic app glyph) to get a letter tile.</summary>
    public static readonly DependencyProperty GlyphProperty = Property(nameof(Glyph), "");
    public static readonly DependencyProperty TileSizeProperty = Property(nameof(TileSize), 32.0);

    public ImageSource? Icon { get => (ImageSource?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double TileSize { get => (double)GetValue(TileSizeProperty); set => SetValue(TileSizeProperty, value); }

    // Light: a pale tint with a deep letter. Dark: a deep tint with a pale letter.
    private static readonly (Color Light, Color LightText, Color Dark, Color DarkText)[] Palette =
    [
        (Rgb(0xE0, 0xE7, 0xFF), Rgb(0x3B, 0x4F, 0xD8), Rgb(0x2A, 0x31, 0x6B), Rgb(0xC7, 0xD0, 0xFF)), // indigo
        (Rgb(0xDB, 0xF0, 0xFB), Rgb(0x0B, 0x6A, 0x9A), Rgb(0x14, 0x3A, 0x52), Rgb(0xA8, 0xDC, 0xF5)), // sky
        (Rgb(0xD9, 0xF4, 0xE5), Rgb(0x16, 0x65, 0x34), Rgb(0x13, 0x41, 0x2B), Rgb(0xA6, 0xE8, 0xC0)), // green
        (Rgb(0xFD, 0xF0, 0xCC), Rgb(0x92, 0x5C, 0x06), Rgb(0x4A, 0x37, 0x0E), Rgb(0xF6, 0xD9, 0x8B)), // amber
        (Rgb(0xFB, 0xE0, 0xE6), Rgb(0xB4, 0x23, 0x4E), Rgb(0x55, 0x1C, 0x2D), Rgb(0xF5, 0xB4, 0xC5)), // rose
        (Rgb(0xEC, 0xE3, 0xFB), Rgb(0x6D, 0x3F, 0xC4), Rgb(0x38, 0x25, 0x65), Rgb(0xD5, 0xC2, 0xF5)), // violet
        (Rgb(0xD5, 0xF2, 0xEF), Rgb(0x0F, 0x76, 0x6E), Rgb(0x12, 0x40, 0x3D), Rgb(0x9F, 0xE3, 0xDC)), // teal
        (Rgb(0xFD, 0xE6, 0xD3), Rgb(0xA5, 0x43, 0x0B), Rgb(0x54, 0x2C, 0x12), Rgb(0xF7, 0xC3, 0x9B))  // orange
    ];

    public AppTile()
    {
        Loaded += (_, _) => Rebuild(); // the theme is known by now
    }

    private static DependencyProperty Property<T>(string name, T value) =>
        DependencyProperty.Register(name, typeof(T), typeof(AppTile), new PropertyMetadata(value, (d, _) => ((AppTile)d).Rebuild()));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private void Rebuild()
    {
        Children.Clear();
        var size = TileSize;
        Width = Height = size;
        if (Icon != null)
        {
            var image = new Image { Source = Icon, Width = size - 2, Height = size - 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Children.Add(image);
            return;
        }

        var generic = Glyph.Length == 0 || Glyph == Display.Glyphs.App || Glyph == "";
        var letter = generic ? LetterOf(Label) : "";
        if (letter.Length == 0 && generic) letter = "?";
        Color? back = null, front = null;
        // A Windows high-contrast theme picks its own colors: the tile keeps to them.
        if (generic && !SystemParameters.HighContrast) (back, front) = ColorsFor(Label);

        var border = new Border { CornerRadius = new CornerRadius(Math.Round(size * 0.22)) };
        if (back is { } b) border.Background = Frozen(b);
        else border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = generic ? letter : Glyph
        };
        if (generic)
        {
            text.FontSize = size * 0.46;
            text.FontWeight = FontWeights.SemiBold;
            if (front is { } letterColor) text.Foreground = Frozen(letterColor);
            else text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
        else
        {
            text.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            text.FontSize = size * 0.5;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        border.Child = text;
        Children.Add(border);
    }

    private static string LetterOf(string label)
    {
        foreach (var c in label)
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "";
    }

    /// <summary>For tests: every tile's background and letter colors, light then dark.</summary>
    internal static IEnumerable<(Color Back, Color Front)> AllColors() =>
        Palette.Select(e => (e.Light, e.LightText)).Concat(Palette.Select(e => (e.Dark, e.DarkText)));

    private static (Color Back, Color Front) ColorsFor(string label)
    {
        // The same name always gets the same color (not string.GetHashCode, which changes between runs).
        var hash = 17;
        foreach (var c in label.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        var entry = Palette[(hash & 0x7FFFFFFF) % Palette.Length];
        return BrandTheme.Dark ? (entry.Dark, entry.DarkText) : (entry.Light, entry.LightText);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
