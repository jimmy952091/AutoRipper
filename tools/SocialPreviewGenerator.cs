// AutoRipper GitHub social-preview generator (1280x640 PNG — GitHub's recommended size).
// Same philosophy as IconGenerator: the artwork is code, so tweaks are one-line edits.
//
// Build + run:
//   csc /out:SocialGen.exe /r:System.Drawing.dll tools\SocialPreviewGenerator.cs
//   SocialGen.exe <output.png>

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

internal static class SocialPreviewGenerator
{
    private const int W = 1280;
    private const int H = 640;

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "social-preview.png";

        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                DrawBackground(g);
                DrawIconArt(g, 60, 130, 1.5f);   // 256-space art scaled 1.5x -> ~384px
                DrawText(g);
            }
            bmp.Save(outPath, ImageFormat.Png);
        }
        Console.WriteLine("Wrote " + outPath);
        return 0;
    }

    private static void DrawBackground(Graphics g)
    {
        // Deep charcoal vertical gradient, like the app's dark theme.
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, W, H),
            Color.FromArgb(24, 25, 31), Color.FromArgb(38, 40, 48), 90f))
        {
            g.FillRectangle(lg, 0, 0, W, H);
        }

        // Faint oversized disc arc in the background for depth.
        using (var pen = new Pen(Color.FromArgb(18, 255, 255, 255), 3f))
        {
            g.DrawEllipse(pen, -220, 180, 720, 720);
            g.DrawEllipse(pen, -160, 240, 600, 600);
        }

        // Amber accent bar along the bottom, echoing the pipeline arrow.
        using (var amber = new SolidBrush(Color.FromArgb(255, 176, 58)))
        {
            g.FillRectangle(amber, 0, H - 10, W, 10);
        }
    }

    /// <summary>The icon's TV + disc + pipeline arrow, drawn in a scaled 256-space at (x, y).</summary>
    private static void DrawIconArt(Graphics g, float x, float y, float scale)
    {
        GraphicsState saved = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(scale, scale);

        var frameColor = Color.FromArgb(58, 62, 70);

        using (var b = new SolidBrush(frameColor))
        {
            g.FillRectangle(b, 130, 172, 28, 20);
            FillRounded(g, b, new Rectangle(92, 190, 104, 14), 7);
            FillRounded(g, b, new Rectangle(52, 34, 192, 144), 18);
        }
        var screenRect = new Rectangle(68, 50, 160, 112);
        using (var lg = new LinearGradientBrush(screenRect,
            Color.FromArgb(31, 111, 224), Color.FromArgb(84, 178, 255), 90f))
        {
            FillRounded(g, lg, screenRect, 10);
        }

        using (var pen = new Pen(Color.FromArgb(255, 176, 58), 17f))
        {
            pen.StartCap = LineCap.Round;
            pen.CustomEndCap = new AdjustableArrowCap(2.6f, 2.6f, true);
            g.DrawLine(pen, 132, 136, 196, 78);
        }

        var center = new PointF(88f, 172f);
        float outerR = 62f, ringR = 24f, holeR = 10f;
        using (var disc = Donut(center, outerR, holeR))
        using (var lg = new LinearGradientBrush(
            new RectangleF(center.X - outerR, center.Y - outerR, outerR * 2, outerR * 2),
            Color.FromArgb(236, 240, 246), Color.FromArgb(168, 178, 192), 60f))
        {
            g.FillPath(lg, disc);
        }
        using (var rim = new Pen(Color.FromArgb(120, 130, 144), 4f))
        {
            g.DrawEllipse(rim, center.X - outerR, center.Y - outerR, outerR * 2, outerR * 2);
        }
        using (var inner = Donut(center, ringR, holeR))
        using (var b = new SolidBrush(Color.FromArgb(246, 249, 252)))
        {
            g.FillPath(b, inner);
        }
        using (var hole = new SolidBrush(Color.FromArgb(30, 31, 38)))
        {
            g.FillEllipse(hole, center.X - holeR, center.Y - holeR, holeR * 2, holeR * 2);
        }

        g.Restore(saved);
    }

    private static void DrawText(Graphics g)
    {
        var white = new SolidBrush(Color.FromArgb(240, 242, 246));
        var gray = new SolidBrush(Color.FromArgb(168, 174, 184));
        var blue = new SolidBrush(Color.FromArgb(110, 170, 255));

        using (var title = new Font("Segoe UI", 74f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            g.DrawString("AutoRipper", title, white, 490, 128);
        }

        using (var tag = new Font("Segoe UI", 31f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.DrawString("Turn your DVDs, Blu-rays & CDs into a", tag, gray, 498, 240);
            g.DrawString("Plex-ready library — automatically.", tag, gray, 498, 282);
        }

        // Substance chips: two rows of rounded pills.
        string[][] rows =
        {
            new[] { "Rip → Encode → Organize", "8 music formats", "Cover art & tags" },
            new[] { "LAN distributed encoding", "Windows 7–11", "AGPL open source" }
        };
        using (var chipFont = new Font("Segoe UI", 21f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var chipBack = new SolidBrush(Color.FromArgb(48, 52, 62)))
        using (var chipBorder = new Pen(Color.FromArgb(78, 84, 96)))
        {
            float yy = 372;
            foreach (string[] row in rows)
            {
                float xx = 498;
                foreach (string chip in row)
                {
                    SizeF size = g.MeasureString(chip, chipFont);
                    var rect = new RectangleF(xx, yy, size.Width + 26, 40);
                    FillRounded(g, chipBack, Rectangle.Round(rect), 19);
                    DrawRounded(g, chipBorder, Rectangle.Round(rect), 19);
                    g.DrawString(chip, chipFont, white, xx + 13, yy + 7);
                    xx += rect.Width + 14;
                }
                yy += 56;
            }
        }

        using (var url = new Font("Segoe UI", 25f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.DrawString("github.com/jimmy952091/AutoRipper", url, blue, 498, 530);
        }

        using (var free = new Font("Segoe UI", 21f, FontStyle.Italic, GraphicsUnit.Pixel))
        {
            g.DrawString("Free forever. Your discs, your library, your hardware.", free, gray, 498, 572);
        }

        white.Dispose(); gray.Dispose(); blue.Dispose();
    }

    // --- shared helpers ---

    private static GraphicsPath Donut(PointF center, float outerR, float holeR)
    {
        var path = new GraphicsPath();
        path.AddEllipse(center.X - outerR, center.Y - outerR, outerR * 2, outerR * 2);
        path.AddEllipse(center.X - holeR, center.Y - holeR, holeR * 2, holeR * 2);
        return path;
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        using (GraphicsPath path = Rounded(r, radius)) { g.FillPath(brush, path); }
    }

    private static void DrawRounded(Graphics g, Pen pen, Rectangle r, int radius)
    {
        using (GraphicsPath path = Rounded(r, radius)) { g.DrawPath(pen, path); }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
