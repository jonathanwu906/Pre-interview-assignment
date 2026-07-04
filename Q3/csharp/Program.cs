using System.Text;

namespace GridAnalysis;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Analysis.Run(Analysis.BuiltinGrid(), Analysis.BuiltinStars.ToList());
            return 0;
        }
        if (string.Equals(args[0], "selftest", StringComparison.Ordinal))
            return SelfTest();
        if (File.Exists(args[0]))
        {
            var png = Png.Decode(args[0]);
            var (grid, stars) = GridExtractor.Extract(png, Analysis.R, Analysis.C);
            Analysis.Run(grid, stars);
            return 0;
        }
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  dotnet run                 analyze the built-in digitized grid");
        Console.Error.WriteLine("  dotnet run -- <image.png>  digitize a grid image, then analyze");
        Console.Error.WriteLine("  dotnet run -- selftest     codec/extractor round trips + analysis invariants");
        return 2;
    }

    /* Verification: PNG codec round trips, extractor round trip on a synthetic
       image with a known answer, and invariants of the analysis itself
       (deterministic results pinned exactly, Monte Carlo pinned to conclusion-
       preserving ranges so any RNG stream must reproduce the same verdicts). */
    private static int SelfTest()
    {
        int passed = 0, total = 0;

        void Check(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        }

        string tmp = Path.Combine(Path.GetTempPath(), "grid-selftest.png");

        /* 1. codec round trip, filter 0 */
        {
            var rgb = RandomRgb(97, 61, seed: 7);
            Png.EncodeRgb(tmp, 97, 61, rgb);
            var back = Png.Decode(tmp);
            Check("1 PNG round trip (filter 0)",
                back.Width == 97 && back.Height == 61 && back.Rgb.AsSpan().SequenceEqual(rgb));
        }

        /* 2. codec round trip cycling filters 0-4 (exercises Sub/Up/Average/Paeth) */
        {
            var rgb = RandomRgb(33, 17, seed: 11);
            var filters = new byte[17];
            for (int y = 0; y < 17; y++)
                filters[y] = (byte)(y % 5);
            Png.EncodeRgb(tmp, 33, 17, rgb, filters);
            var back = Png.Decode(tmp);
            Check("2 PNG round trip (filters 0-4)", back.Rgb.AsSpan().SequenceEqual(rgb));
        }

        /* 3-4. extractor round trip: synthetic image with known answer */
        {
            var (w, h, rgb) = GridExtractor.Render(Analysis.BuiltinRows, Analysis.BuiltinStars);
            Png.EncodeRgb(tmp, w, h, rgb);
            var (grid, stars) = GridExtractor.Extract(Png.Decode(tmp), Analysis.R, Analysis.C);
            bool gridOk = true;
            for (int r = 0; r < Analysis.R; r++)
                gridOk &= new string(grid[r]) == Analysis.BuiltinRows[r];
            Check("3 extractor recovers all 50 cell colors from synthetic image", gridOk);
            Check("4 extractor recovers both star positions",
                stars.SequenceEqual(Analysis.BuiltinStars));
        }
        File.Delete(tmp);

        var res = Analysis.Compute(Analysis.BuiltinGrid());

        /* 5-9. deterministic findings pinned exactly */
        Check("5 color counts R11 Y10 G15 B14, chi2=1.36 → uniform not rejected",
            res.Counts['R'] == 11 && res.Counts['Y'] == 10 && res.Counts['G'] == 15 &&
            res.Counts['B'] == 14 && Math.Abs(res.Chi2 - 1.36) < 0.005 && res.Chi2 < 7.81);
        Check("6 horizontal adjacent same-color = 0/40", res.HViol.Count == 0);
        Check("7 vertical violations = exactly the row4/5 seam (5 cells)",
            res.VViol.SequenceEqual(new[] { (4, 0), (4, 1), (4, 2), (4, 3), (4, 4) }));
        Check("8 diagonal control = 14/72 (constraint is orthogonal-only)", res.DViol == 14);
        Check("9 near rows {(1,6,1),(4,5,0),(7,9,1)}, near cols {(2,4,2)}",
            res.NearRows.SequenceEqual(new[] { (1, 6, 1), (4, 5, 0), (7, 9, 1) }) &&
            res.NearCols.SequenceEqual(new[] { (2, 4, 2) }));

        /* 10-11. Monte Carlo pinned to conclusion-preserving ranges */
        Check("10 null A: all three observed features significant (p < 0.02)",
            res.PZeroHorizontal < 0.001 && res.PDuplicateRows is > 0.001 and < 0.02 &&
            res.PColPair8 is > 0.0005 and < 0.02);
        Check("11 null B: col2~col4 marginal (p 0.03-0.09), near-rows explained (p 0.30-0.45)",
            res.PCondColHamming2 is > 0.03 and < 0.09 && res.PCondNearRows is > 0.30 and < 0.45);

        /* 12. star battery: counts match reference; no property uniquely picks the stars */
        {
            var counts = res.Battery.Select(b => b.cells.Count).ToArray();
            bool none = res.Battery.All(b =>
                !(b.cells.Count == 2 && b.cells.Contains((5, 3)) && b.cells.Contains((7, 1))));
            Check("12 battery sizes {19,26,20,2,23}; no property uniquely selects the stars",
                counts.SequenceEqual(new[] { 19, 26, 20, 2, 23 }) && none);
        }

        Console.WriteLine($"{passed}/{total} tests passed");
        return passed == total ? 0 : 1;
    }

    private static byte[] RandomRgb(int w, int h, ulong seed)
    {
        var rgb = new byte[w * h * 3];
        ulong s = seed;
        for (int i = 0; i < rgb.Length; i++)
        {
            s ^= s >> 12;
            s ^= s << 25;
            s ^= s >> 27;
            rgb[i] = (byte)(s * 2685821657736338717UL >> 56);
        }
        return rgb;
    }
}
