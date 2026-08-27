using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace UORegionEditor;

// Renders a shareable "player map" image: the radar background with every visible
// region drawn as ONE silhouette - union fill plus a single 1-tile contour in the
// region's own color - and name labels with simple collision avoidance. This is
// deliberately NOT the editor look (no per-box outlines).
public static class MapExport
{
    public const long MaxPixels = 250_000_000;   // bitmap guard (~750MB at 24bpp)

    public static Bitmap Render(Bitmap radar, IReadOnlyList<RegionDef> regions,
        RegionRect area, double scale, bool drawLabels, float labelSize)
    {
        int w = (int)Math.Round(area.W * scale), h = (int)Math.Round(area.H * scale);
        if (w < 1 || h < 1) throw new ArgumentException("empty export area");
        if ((long)w * h > MaxPixels) throw new ArgumentException("export image too large");
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        try
        {
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;   // NN scaling without the half-pixel shift
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(16, 16, 18));

        // radar crop, clamped to the bitmap actually available
        int srcX = Math.Max(0, area.X1), srcY = Math.Max(0, area.Y1);
        int srcW = Math.Min(area.X2 + 1, radar.Width) - srcX;
        int srcH = Math.Min(area.Y2 + 1, radar.Height) - srcY;
        if (srcW > 0 && srcH > 0)
            g.DrawImage(radar,
                new RectangleF((float)((srcX - area.X1) * scale), (float)((srcY - area.Y1) * scale),
                    (float)(srcW * scale), (float)(srcH * scale)),
                new Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);

        // biggest first: small regions land on top and stay readable
        var drawn = regions
            .Where(r => r.Visible && r.Rects.Count > 0 &&
                r.Rects.Any(rc => rc.X2 >= area.X1 && rc.X1 <= area.X2 && rc.Y2 >= area.Y1 && rc.Y1 <= area.Y2))
            .OrderByDescending(r => r.Rects.Sum(rc => (long)rc.W * rc.H))
            .ToList();

        // pass 1: one silhouette per region (union of boxes; contour = region minus
        // its own 4-direction erosion = exactly one tile of edge)
        foreach (var r in drawn)
        {
            using var path = new GraphicsPath(FillMode.Winding);
            foreach (var rc in r.Rects)
                path.AddRectangle(new RectangleF(
                    (float)((rc.X1 - area.X1) * scale), (float)((rc.Y1 - area.Y1) * scale),
                    (float)(rc.W * scale), (float)(rc.H * scale)));
            using var reg = new Region(path);
            using var ero = reg.Clone();
            foreach (var (dx, dy) in new[] { (scale, 0.0), (-scale, 0.0), (0.0, scale), (0.0, -scale) })
            {
                using var t = reg.Clone();
                t.Translate((float)dx, (float)dy);
                ero.Intersect(t);
            }
            using var border = reg.Clone();
            border.Exclude(ero);
            using var fill = new SolidBrush(Color.FromArgb(58, r.Color));
            using var line = new SolidBrush(Color.FromArgb(235, r.Color));
            g.FillRegion(fill, reg);
            g.FillRegion(line, border);
        }

        // pass 2: labels after every fill, so nothing tints the text
        if (drawLabels)
        {
            using var font = new Font("Segoe UI", labelSize, FontStyle.Bold);
            using var bg = new SolidBrush(Color.FromArgb(185, 0, 0, 0));
            var placed = new List<RectangleF>();
            foreach (var r in drawn)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                // anchor on the part of the region actually INSIDE the export area -
                // a distant disjoint cluster must not drag the label off its silhouette
                var vis = r.Rects.Where(t => t.X2 >= area.X1 && t.X1 <= area.X2 &&
                    t.Y2 >= area.Y1 && t.Y1 <= area.Y2).ToList();
                int bx1 = Math.Max(vis.Min(t => t.X1), area.X1), by1 = Math.Max(vis.Min(t => t.Y1), area.Y1);
                int bx2 = Math.Min(vis.Max(t => t.X2), area.X2), by2 = Math.Min(vis.Max(t => t.Y2), area.Y2);
                var sz = g.MeasureString(r.Name, font);
                float lx = (float)(((bx1 + bx2) / 2.0 + 0.5 - area.X1) * scale) - sz.Width / 2f;
                float ly = (float)(((by1 + by2) / 2.0 + 0.5 - area.Y1) * scale) - sz.Height / 2f;
                lx = Math.Clamp(lx, 2, Math.Max(2, w - sz.Width - 4));
                ly = Math.Clamp(ly, 2, Math.Max(2, h - sz.Height - 4));
                var cand = new RectangleF(lx, ly, sz.Width + 6, sz.Height + 2);
                for (int tries = 0; tries < 40 && placed.Any(p => p.IntersectsWith(cand)); tries++)
                {
                    ly += sz.Height + 3;
                    cand = new RectangleF(lx, ly, sz.Width + 6, sz.Height + 2);
                }
                // a pushed-down label must never fall off the image - overlap beats loss
                ly = Math.Min(ly, Math.Max(2, h - sz.Height - 2));
                cand = new RectangleF(lx, ly, sz.Width + 6, sz.Height + 2);
                placed.Add(cand);
                g.FillRectangle(bg, lx - 3, ly - 1, sz.Width + 6, sz.Height + 2);
                using var tb = new SolidBrush(Color.FromArgb(255,
                    Math.Min(255, r.Color.R + 60), Math.Min(255, r.Color.G + 60), Math.Min(255, r.Color.B + 60)));
                g.DrawString(r.Name, font, tb, lx, ly);
            }
        }
        return bmp;
        }
        catch
        {
            bmp.Dispose();   // a mid-render GDI failure must not strand a ~750MB bitmap
            throw;
        }
    }
}
