using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppSentry.Services;

/// <summary>
/// AppSentry's own icon, drawn in code: a flat shield with a check mark (Fluent style, no
/// gradients), plus tray variants with a status dot. Also writes the .ico used for the exe.
/// </summary>
public static class BrandIcon
{
    public enum State { Normal, Attention, Paused }

    private static readonly Geometry Shield = Geometry.Parse(
        "M16,2.5 L27,6.5 L27,14.5 C27,21.5 22.5,26.8 16,29.5 C9.5,26.8 5,21.5 5,14.5 L5,6.5 Z");
    private static readonly Geometry ShieldRightHalf = Geometry.Parse(
        "M16,2.5 L27,6.5 L27,14.5 C27,21.5 22.5,26.8 16,29.5 Z");
    private static readonly Geometry Check = Geometry.Parse("M10.8,15.6 L14.6,19.3 L21.6,12.1");

    private static readonly Brush ShieldBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush ShieldShadeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)));
    private static readonly Brush AttentionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush PausedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Pen CheckPen = Frozen(new Pen(Brushes.White, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static readonly Pen DotRing = Frozen(new Pen(Brushes.White, 1.5));

    private static readonly Dictionary<(int, State), BitmapSource> Cache = [];

    public static BitmapSource Render(int pixels, State state = State.Normal)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((pixels, state), out var cached)) return cached;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 32.0, pixels / 32.0));
            dc.DrawGeometry(ShieldBrush, null, Shield);
            dc.DrawGeometry(ShieldShadeBrush, null, ShieldRightHalf); // two-tone for depth, still flat
            dc.DrawGeometry(null, CheckPen, Check);

            if (state != State.Normal)
            {
                var center = new Point(25, 25);
                dc.DrawEllipse(state == State.Attention ? AttentionBrush : PausedBrush, DotRing, center, 6, 6);
                if (state == State.Paused)
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(22.6, 22, 1.8, 6));
                    dc.DrawRectangle(Brushes.White, null, new Rect(25.6, 22, 1.8, 6));
                }
            }
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        lock (Cache) Cache[(pixels, state)] = bitmap;
        return bitmap;
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries, supported since Vista).</summary>
    public static void WriteIco(string path, params int[] sizes)
    {
        using var stream = File.Create(path);
        WriteIco(stream, State.Normal, sizes.Length == 0 ? [16, 20, 24, 32, 40, 48, 64, 256] : sizes);
    }

    /// <summary>Tray icon for a state, as the Win32 icon the notification area needs (small sizes only).</summary>
    public static System.Drawing.Icon TrayIcon(State state)
    {
        var ms = new MemoryStream();
        WriteIco(ms, state, [16, 20, 24, 32, 40, 48]);
        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }

    private static void WriteIco(Stream stream, State state, int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(s, state)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (Size: s, Png: ms.ToArray());
        }).ToList();

        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((short)0);            // reserved
        w.Write((short)1);            // type: icon
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);         // palette
            w.Write((byte)0);         // reserved
            w.Write((short)1);        // planes
            w.Write((short)32);       // bpp
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
