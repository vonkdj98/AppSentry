using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppSentry.Services;

/// <summary>
/// AppSentry's own icon, drawn in code (Fluent style, no gradients): a package cube on an indigo
/// tile with a status badge. The badge carries the tray state: green pulse normally, amber when
/// something needs a look, grey with pause bars when paused. Also writes the .ico used for the exe.
/// Master artwork: Assets\AppSentry.svg (128-unit grid; the geometry here is the same at 1/4 scale).
/// </summary>
public static class BrandIcon
{
    public enum State { Normal, Attention, Paused }

    // 32-unit design grid.
    private static readonly Geometry TopFace = Geometry.Parse("M14.5,6 L22.5,10.5 L14.5,15 L6.5,10.5 Z");
    private static readonly Geometry LeftFace = Geometry.Parse("M6.5,10.5 L14.5,15 L14.5,24.5 L6.5,20 Z");
    private static readonly Geometry RightFace = Geometry.Parse("M22.5,10.5 L22.5,20 L14.5,24.5 L14.5,15 Z");
    private static readonly Geometry Tape = Geometry.Parse("M10.5,8.25 L18.5,12.75");
    private static readonly Geometry Pulse = Geometry.Parse("M19.5,23 L21.25,23 L22.25,21 L23.75,25 L24.75,23 L26.5,23");
    private static readonly Point BadgeCenter = new(23, 23);

    private static readonly Color TileColor = Color.FromRgb(0x3B, 0x4F, 0xD8);
    private static readonly Brush TileBrush = Frozen(new SolidColorBrush(TileColor));
    private static readonly Brush TopFaceBrush = Brushes.White;
    private static readonly Brush LeftFaceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC7, 0xD0, 0xFF)));
    private static readonly Brush RightFaceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x97, 0xA6, 0xFF)));
    private static readonly Brush NormalBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush AttentionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush PausedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Pen TapePen = Frozen(new Pen(RightFaceBrush, 1.25));
    private static readonly Pen PulsePen = Frozen(new Pen(Brushes.White, 1.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static readonly Pen BadgeRing = Frozen(new Pen(TileBrush, 1.5)); // cuts the badge out of the cube

    private static readonly Dictionary<(int, State), BitmapSource> Cache = [];

    public static BitmapSource Render(int pixels, State state = State.Normal)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((pixels, state), out var cached)) return cached;
        }

        // At tray sizes the tape and pulse are sub-pixel noise: drop them and enlarge the badge instead.
        var small = pixels <= 20;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Tile in device pixels so its edges land on whole pixels at every size.
            var inset = pixels >= 32 ? Math.Round(pixels / 32.0) : 0;
            var side = pixels - 2 * inset;
            dc.DrawRoundedRectangle(TileBrush, null, new Rect(inset, inset, side, side), side * 0.23, side * 0.23);

            dc.PushTransform(new ScaleTransform(pixels / 32.0, pixels / 32.0));
            dc.DrawGeometry(TopFaceBrush, null, TopFace);
            dc.DrawGeometry(LeftFaceBrush, null, LeftFace);
            dc.DrawGeometry(RightFaceBrush, null, RightFace);
            if (!small) dc.DrawGeometry(null, TapePen, Tape);

            var badge = state switch
            {
                State.Attention => AttentionBrush,
                State.Paused => PausedBrush,
                _ => NormalBrush
            };
            var radius = small ? 6.5 : 5.5;
            dc.DrawEllipse(badge, BadgeRing, BadgeCenter, radius, radius);

            if (state == State.Paused)
            {
                var (bar, gap, height) = small ? (2.4, 1.6, 7.0) : (1.8, 1.2, 6.0);
                var top = BadgeCenter.Y - height / 2;
                dc.DrawRectangle(Brushes.White, null, new Rect(BadgeCenter.X - gap / 2 - bar, top, bar, height));
                dc.DrawRectangle(Brushes.White, null, new Rect(BadgeCenter.X + gap / 2, top, bar, height));
            }
            else if (!small)
            {
                dc.DrawGeometry(null, PulsePen, Pulse);
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
