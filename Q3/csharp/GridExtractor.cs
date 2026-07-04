namespace GridAnalysis;

/// <summary>
/// Digitization: locate the colored grid by saturation, sample each cell's
/// center region, classify hue into R/Y/G/B, and detect the white star
/// markers.
/// </summary>
public static class GridExtractor
{
    public static (char[][] grid, List<(int r, int c)> stars) Extract(Png img, int rows, int cols)
    {
        int w = img.Width, h = img.Height;
        byte[] px = img.Rgb;

        /* high-saturation pixels = colored cells; bounding box by row/col density */
        var rowSat = new int[h];
        var colSat = new int[w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                int mx = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                int mn = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                if (mx - mn > 60 && mx > 120)
                {
                    rowSat[y]++;
                    colSat[x]++;
                }
            }
        int y0 = -1, y1 = -1, x0 = -1, x1 = -1;
        for (int y = 0; y < h; y++)
            if (rowSat[y] > 0.3 * w)
            {
                if (y0 < 0) y0 = y;
                y1 = y;
            }
        for (int x = 0; x < w; x++)
            if (colSat[x] > 0.3 * h)
            {
                if (x0 < 0) x0 = x;
                x1 = x;
            }
        if (y0 < 0 || x0 < 0)
            throw new InvalidDataException("no saturated grid region found in image");

        double cw = (x1 - x0 + 1) / (double)cols;
        double ch = (y1 - y0 + 1) / (double)rows;
        var grid = new char[rows][];
        var stars = new List<(int r, int c)>();
        var rs = new List<int>();
        var gs = new List<int>();
        var bs = new List<int>();

        for (int r = 0; r < rows; r++)
        {
            grid[r] = new char[cols];
            for (int c = 0; c < cols; c++)
            {
                int ya = (int)(y0 + r * ch + ch * 0.25), yb = (int)(y0 + r * ch + ch * 0.75);
                int xa = (int)(x0 + c * cw + cw * 0.25), xb = (int)(x0 + c * cw + cw * 0.75);
                rs.Clear();
                gs.Clear();
                bs.Clear();
                int total = 0, white = 0;
                for (int y = ya; y < yb; y++)
                    for (int x = xa; x < xb; x++)
                    {
                        int i = (y * w + x) * 3;
                        int mx = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                        int mn = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                        total++;
                        if (mx - mn < 50 && mx > 150)
                            white++; /* white pixels = star marker */
                        if (mx - mn >= 50)
                        {
                            rs.Add(px[i]);
                            gs.Add(px[i + 1]);
                            bs.Add(px[i + 2]);
                        }
                    }
                if (total > 0 && white / (double)total > 0.03)
                    stars.Add((r, c));
                if (rs.Count == 0)
                    throw new InvalidDataException($"cell ({r},{c}) has no saturated pixels — wrong image or grid size?");
                double hue = Hue(Median(rs) / 255.0, Median(gs) / 255.0, Median(bs) / 255.0);
                grid[r][c] = hue < 20 || hue >= 340 ? 'R' : hue < 70 ? 'Y' : hue < 180 ? 'G' : 'B';
            }
        }
        return (grid, stars);
    }

    private static double Median(List<int> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    /* hue in degrees [0, 360) — same formula as colorsys.rgb_to_hsv */
    private static double Hue(double r, double g, double b)
    {
        double maxc = Math.Max(r, Math.Max(g, b));
        double minc = Math.Min(r, Math.Min(g, b));
        if (maxc == minc)
            return 0;
        double span = maxc - minc;
        double rc = (maxc - r) / span, gc = (maxc - g) / span, bc = (maxc - b) / span;
        double hh = r == maxc ? bc - gc : g == maxc ? 2 + rc - bc : 4 + gc - rc;
        hh = hh / 6 % 1;
        if (hh < 0)
            hh += 1;
        return hh * 360;
    }

    /* ---------- synthetic grid image for the extractor's round-trip test ---------- */

    public static (int w, int h, byte[] rgb) Render(string[] rows, (int r, int c)[] stars)
    {
        const int cell = 60, margin = 30, star = 14;
        int nr = rows.Length, nc = rows[0].Length;
        int w = nc * cell + 2 * margin, h = nr * cell + 2 * margin;
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)255); /* white background: unsaturated, outside bbox */

        for (int r = 0; r < nr; r++)
            for (int c = 0; c < nc; c++)
            {
                var (cr, cg, cb) = rows[r][c] switch
                {
                    'R' => ((byte)230, (byte)40, (byte)40),
                    'Y' => ((byte)235, (byte)200, (byte)40),
                    'G' => ((byte)50, (byte)180, (byte)70),
                    _ => ((byte)50, (byte)90, (byte)220),
                };
                for (int y = margin + r * cell; y < margin + (r + 1) * cell; y++)
                    for (int x = margin + c * cell; x < margin + (c + 1) * cell; x++)
                    {
                        int i = (y * w + x) * 3;
                        rgb[i] = cr;
                        rgb[i + 1] = cg;
                        rgb[i + 2] = cb;
                    }
            }
        foreach (var (sr, sc) in stars)
        {
            int cy = margin + sr * cell + cell / 2, cx = margin + sc * cell + cell / 2;
            for (int y = cy - star / 2; y < cy + star / 2; y++)
                for (int x = cx - star / 2; x < cx + star / 2; x++)
                {
                    int i = (y * w + x) * 3;
                    rgb[i] = rgb[i + 1] = rgb[i + 2] = 255;
                }
        }
        return (w, h, rgb);
    }
}
