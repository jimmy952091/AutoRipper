// AutoRipper icon generator.
//
// Draws the app icon (a disc flowing through a pipeline arrow into a TV screen) with GDI+
// and writes a multi-resolution .ico (PNG-compressed entries: 256/128/64/48/32/24/16 —
// supported since Vista, so fine for the Windows 7 floor). Keeping the icon as code means
// the "source" of the artwork ships with the AGPL project and tweaks are one-line edits.
//
// Build + run:
//   csc /out:IconGen.exe /r:System.Drawing.dll tools\IconGenerator.cs
//   IconGen.exe <output.ico> <preview.png>

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconGenerator
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: IconGen.exe <output.ico> <preview.png>");
            return 2;
        }

        int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
        var payloads = new List<byte[]>();

        foreach (int size in sizes)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(size / 256f, size / 256f);
                    DrawIcon(g, size);
                }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    payloads.Add(ms.ToArray());
                }
                if (size == 256) { bmp.Save(args[1], ImageFormat.Png); }
            }
        }

        WriteIco(args[0], sizes, payloads);
        Console.WriteLine("Wrote " + args[0] + " (" + sizes.Length + " sizes) and preview " + args[1]);
        return 0;
    }

    /// <summary>Draws in a fixed 256x256 coordinate space; the caller scales per size.</summary>
    private static void DrawIcon(Graphics g, int targetSize)
    {
        // Tiny sizes get the simplified mark (TV + disc, no arrow/stand details) — a 16px
        // arrow is just noise.
        bool detailed = targetSize >= 32;

        var frameColor = Color.FromArgb(47, 50, 55);      // charcoal TV shell
        var screenTop = Color.FromArgb(31, 111, 224);     // screen gradient
        var screenBottom = Color.FromArgb(84, 178, 255);

        // --- TV stand (behind everything) ---
        if (detailed)
        {
            using (var b = new SolidBrush(frameColor))
            {
                g.FillRectangle(b, 130, 172, 28, 20);                 // neck
                FillRounded(g, b, new Rectangle(92, 190, 104, 14), 7); // base
            }
        }

        // --- TV frame + screen ---
        using (var b = new SolidBrush(frameColor))
        {
            FillRounded(g, b, new Rectangle(52, 34, 192, 144), 18);
        }
        var screenRect = new Rectangle(68, 50, 160, 112);
        using (var lg = new LinearGradientBrush(screenRect, screenTop, screenBottom, 90f))
        {
            FillRounded(g, lg, screenRect, 10);
        }

        // --- Pipeline arrow: disc -> screen. Starts just OUTSIDE the disc rim so the whole
        // shaft stays visible (starting at the disc center buried it under the disc). ---
        if (detailed)
        {
            using (var pen = new Pen(Color.FromArgb(255, 176, 58), 18f)) // amber
            {
                pen.StartCap = LineCap.Round;
                pen.CustomEndCap = new AdjustableArrowCap(3.0f, 3.0f, true);
                g.DrawLine(pen, 140, 126, 182, 88);
            }
        }

        // --- Disc (front-left, overlapping the TV) ---
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
        if (detailed)
        {
            using (var inner = Donut(center, ringR, holeR))
            using (var b = new SolidBrush(Color.FromArgb(246, 249, 252)))
            {
                g.FillPath(b, inner);
            }
        }
        // Punch the hub hole to full transparency so the icon reads as a real disc.
        var oldMode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(clear, center.X - holeR, center.Y - holeR, holeR * 2, holeR * 2);
        }
        g.CompositingMode = oldMode;
    }

    private static GraphicsPath Donut(PointF center, float outerR, float holeR)
    {
        var path = new GraphicsPath(); // Alternate fill mode: second ellipse becomes the hole
        path.AddEllipse(center.X - outerR, center.Y - outerR, outerR * 2, outerR * 2);
        path.AddEllipse(center.X - holeR, center.Y - holeR, holeR * 2, holeR * 2);
        return path;
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        using (var path = new GraphicsPath())
        {
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }

    /// <summary>Writes a .ico whose entries are PNG-compressed (valid since Vista).</summary>
    private static void WriteIco(string path, int[] sizes, List<byte[]> payloads)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);              // reserved
            w.Write((ushort)1);              // type: icon
            w.Write((ushort)sizes.Length);   // image count

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s == 256 ? 0 : s)); // width (0 = 256)
                w.Write((byte)(s == 256 ? 0 : s)); // height
                w.Write((byte)0);                  // palette colors
                w.Write((byte)0);                  // reserved
                w.Write((ushort)1);                // planes
                w.Write((ushort)32);               // bits per pixel
                w.Write(payloads[i].Length);       // payload size
                w.Write(offset);                   // payload offset
                offset += payloads[i].Length;
            }
            foreach (byte[] payload in payloads) { w.Write(payload); }
        }
    }
}
