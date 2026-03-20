using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AppSentry;

/// <summary>
/// Generates the App Tracker icon programmatically using GDI+.
/// Design: Blue rounded square background, green checkmark card (top-left),
/// orange download arrow card (bottom-left), magnifying glass with gear (right).
/// </summary>
internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    public static Icon Create() => RenderIcon(32);
    public static Icon CreateSmall() => RenderIcon(16);

    private static Icon RenderIcon(int size)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = size / 32f; // scale factor

        // ── Background: blue gradient rounded square ─────────────────────────
        var bgRect = new Rectangle(0, 0, size, size);
        using (var path = RoundedRect(bgRect, (int)(6 * s)))
        {
            // Outer dark blue border
            using (var borderBrush = new LinearGradientBrush(bgRect,
                Color.FromArgb(60, 100, 180), Color.FromArgb(30, 60, 140),
                LinearGradientMode.Vertical))
                g.FillPath(borderBrush, path);

            // Inner lighter blue
            var innerRect = new Rectangle((int)(1 * s), (int)(1 * s),
                size - (int)(2 * s), size - (int)(2 * s));
            using var innerPath = RoundedRect(innerRect, (int)(5 * s));
            using var innerBrush = new LinearGradientBrush(innerRect,
                Color.FromArgb(70, 140, 230), Color.FromArgb(30, 80, 190),
                LinearGradientMode.Vertical);
            g.FillPath(innerBrush, innerPath);
        }

        // Glossy top highlight
        using (var glossPath = RoundedRect(
            new Rectangle((int)(2 * s), (int)(2 * s), size - (int)(4 * s), (int)(14 * s)), (int)(4 * s)))
        {
            using var glossBrush = new LinearGradientBrush(
                new Rectangle(0, 0, size, (int)(16 * s)),
                Color.FromArgb(80, 255, 255, 255), Color.FromArgb(10, 255, 255, 255),
                LinearGradientMode.Vertical);
            g.FillPath(glossBrush, glossPath);
        }

        // ── Green checkmark card (top-left) ──────────────────────────────────
        var greenCardRect = new RectangleF(3 * s, 4 * s, 14 * s, 10 * s);
        using (var cardPath = RoundedRect(
            Rectangle.Round(greenCardRect), (int)(2 * s)))
        {
            using var cardBrush = new LinearGradientBrush(
                Rectangle.Round(greenCardRect),
                Color.FromArgb(100, 200, 60), Color.FromArgb(60, 160, 30),
                LinearGradientMode.Vertical);
            g.FillPath(cardBrush, cardPath);

            // White checkmark
            using var checkPen = new Pen(Color.White, 2.2f * s)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLine(checkPen, 6 * s, 9 * s, 9 * s, 12 * s);
            g.DrawLine(checkPen, 9 * s, 12 * s, 14 * s, 6 * s);
        }

        // ── Orange download arrow card (bottom-left) ─────────────────────────
        var orangeCardRect = new RectangleF(3 * s, 16 * s, 14 * s, 10 * s);
        using (var cardPath = RoundedRect(
            Rectangle.Round(orangeCardRect), (int)(2 * s)))
        {
            using var cardBrush = new LinearGradientBrush(
                Rectangle.Round(orangeCardRect),
                Color.FromArgb(240, 170, 40), Color.FromArgb(220, 130, 20),
                LinearGradientMode.Vertical);
            g.FillPath(cardBrush, cardPath);

            // White down arrow
            using var arrowPen = new Pen(Color.White, 2f * s)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            float cx = 10 * s, cy = 21 * s;
            // Arrow stem
            g.DrawLine(arrowPen, cx, 18 * s, cx, 23.5f * s);
            // Arrow head
            g.DrawLine(arrowPen, cx - 3 * s, 21.5f * s, cx, 24 * s);
            g.DrawLine(arrowPen, cx + 3 * s, 21.5f * s, cx, 24 * s);
        }

        // ── Magnifying glass with gear (right side) ──────────────────────────
        float mgCx = 23 * s, mgCy = 14 * s, mgR = 7 * s;

        // Glass circle - white fill with blue tint
        using (var glassBrush = new LinearGradientBrush(
            new RectangleF(mgCx - mgR, mgCy - mgR, mgR * 2, mgR * 2),
            Color.FromArgb(220, 230, 245, 255), Color.FromArgb(200, 180, 210, 240),
            LinearGradientMode.ForwardDiagonal))
        {
            g.FillEllipse(glassBrush, mgCx - mgR, mgCy - mgR, mgR * 2, mgR * 2);
        }

        // Glass border (silver/white)
        using (var glassPen = new Pen(Color.FromArgb(230, 240, 245, 255), 2f * s))
            g.DrawEllipse(glassPen, mgCx - mgR, mgCy - mgR, mgR * 2, mgR * 2);

        // Gear inside the magnifying glass
        DrawGear(g, mgCx, mgCy, 4 * s, s);

        // Handle
        using (var handlePen = new Pen(Color.FromArgb(60, 60, 70), 2.8f * s)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            float hx = mgCx + mgR * 0.65f;
            float hy = mgCy + mgR * 0.65f;
            g.DrawLine(handlePen, hx, hy, hx + 4 * s, hy + 4 * s);
        }

        // Handle highlight
        using (var hlPen = new Pen(Color.FromArgb(100, 80, 80, 90), 1.5f * s)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            float hx = mgCx + mgR * 0.65f + 0.8f * s;
            float hy = mgCy + mgR * 0.65f + 0.8f * s;
            g.DrawLine(hlPen, hx, hy, hx + 3 * s, hy + 3 * s);
        }

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private static void DrawGear(Graphics g, float cx, float cy, float radius, float s)
    {
        // Gear body (dark blue-gray)
        var gearColor = Color.FromArgb(220, 70, 90, 120);
        var gearHighlight = Color.FromArgb(200, 100, 130, 170);

        // Outer teeth
        int teeth = 8;
        float outerR = radius;
        float innerR = radius * 0.7f;
        float toothW = 0.28f;

        var gearPoints = new List<PointF>();
        for (int i = 0; i < teeth; i++)
        {
            float angle = (float)(i * 2 * Math.PI / teeth);
            float a1 = angle - toothW;
            float a2 = angle + toothW;
            float midAngle = (float)((i + 0.5) * 2 * Math.PI / teeth);
            float m1 = midAngle - toothW;
            float m2 = midAngle + toothW;

            gearPoints.Add(new PointF(cx + outerR * MathF.Cos(a1), cy + outerR * MathF.Sin(a1)));
            gearPoints.Add(new PointF(cx + outerR * MathF.Cos(a2), cy + outerR * MathF.Sin(a2)));
            gearPoints.Add(new PointF(cx + innerR * MathF.Cos(m1), cy + innerR * MathF.Sin(m1)));
            gearPoints.Add(new PointF(cx + innerR * MathF.Cos(m2), cy + innerR * MathF.Sin(m2)));
        }

        using (var gearBrush = new SolidBrush(gearColor))
            g.FillPolygon(gearBrush, gearPoints.ToArray());

        // Center circle
        float centerR = radius * 0.3f;
        using (var centerBrush = new SolidBrush(gearHighlight))
            g.FillEllipse(centerBrush, cx - centerR, cy - centerR, centerR * 2, centerR * 2);

        // Center hole
        float holeR = radius * 0.15f;
        using (var holeBrush = new SolidBrush(Color.FromArgb(180, 200, 220, 240)))
            g.FillEllipse(holeBrush, cx - holeR, cy - holeR, holeR * 2, holeR * 2);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
