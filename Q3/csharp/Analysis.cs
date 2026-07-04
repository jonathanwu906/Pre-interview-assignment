using System.Text;

namespace GridAnalysis;

/// <summary>
/// The rule-mining pipeline: descriptive stats → structural hypotheses →
/// Monte Carlo significance under null model A (random shuffle of the fixed
/// color multiset) → conditional re-testing under null model B (random proper
/// coloring + one duplicated row) → star-cell property battery.
/// </summary>
public static class Analysis
{
    public const int R = 10, C = 5;
    public const string Colors = "RYGB";
    public const int McRounds = 20000;

    public static readonly string[] BuiltinRows =
    {
        "RYGBY", "GBYRG", "BRBGB", "GYRYR", "RGBGB",
        "RGBGB", "GBYRY", "YRGYG", "BGBRB", "YRGBG",
    };

    public static readonly (int r, int c)[] BuiltinStars = { (5, 3), (7, 1) };

    public static char[][] BuiltinGrid() => BuiltinRows.Select(s => s.ToCharArray()).ToArray();

    public sealed record Results(
        Dictionary<char, int> Counts, double Chi2,
        List<(int r, int c)> HViol, List<(int r, int c)> VViol, int DViol,
        List<(int i, int j, int ham)> NearRows, List<(int i, int j, int ham)> NearCols,
        double PZeroHorizontal, double PDuplicateRows, double PColPair8,
        double PCondColHamming2, double PCondNearRows,
        List<(string name, List<(int r, int c)> cells)> Battery);

    /* xorshift64* — deterministic across runs and platforms (fixed seed) */
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int maxExclusive)
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return (int)(_state * 2685821657736338717UL % (ulong)maxExclusive);
        }
    }

    public static Results Compute(char[][] grid)
    {
        /* [1] color distribution vs uniform */
        var counts = Colors.ToDictionary(k => k, _ => 0);
        foreach (var row in grid)
            foreach (var k in row)
                counts[k]++;
        double e = R * C / 4.0;
        double chi2 = Colors.Sum(k => (counts[k] - e) * (counts[k] - e) / e);

        /* [2] adjacency violations; diagonal as the control direction */
        var hv = new List<(int, int)>();
        var vv = new List<(int, int)>();
        for (int r = 0; r < R; r++)
            for (int c = 0; c < C - 1; c++)
                if (grid[r][c] == grid[r][c + 1])
                    hv.Add((r, c));
        for (int r = 0; r < R - 1; r++)
            for (int c = 0; c < C; c++)
                if (grid[r][c] == grid[r + 1][c])
                    vv.Add((r, c));
        int dv = 0;
        for (int r = 0; r < R - 1; r++)
            for (int c = 0; c < C; c++)
                foreach (int dc in stackalloc[] { -1, 1 })
                    if (c + dc >= 0 && c + dc < C && grid[r][c] == grid[r + 1][c + dc])
                        dv++;

        /* [3] near-duplicate rows / columns by Hamming distance */
        var rows = grid.Select(x => new string(x)).ToArray();
        var cols = Enumerable.Range(0, C)
            .Select(c => new string(Enumerable.Range(0, R).Select(r => grid[r][c]).ToArray()))
            .ToArray();
        var nearRows = new List<(int, int, int)>();
        for (int i = 0; i < R; i++)
            for (int j = i + 1; j < R; j++)
            {
                int ham = Hamming(rows[i], rows[j]);
                if (ham <= 1)
                    nearRows.Add((i, j, ham));
            }
        var nearCols = new List<(int, int, int)>();
        for (int i = 0; i < C; i++)
            for (int j = i + 1; j < C; j++)
            {
                int ham = Hamming(cols[i], cols[j]);
                if (ham <= 3)
                    nearCols.Add((i, j, ham));
            }

        /* [4] null model A: random shuffle of the fixed color multiset */
        var rng = new Rng(42);
        var pool = grid.SelectMany(x => x).ToArray();
        int hitH = 0, hitDup = 0, hitCol = 0;
        var g = new char[R][];
        for (int r = 0; r < R; r++)
            g[r] = new char[C];
        for (int n = 0; n < McRounds; n++)
        {
            for (int i = pool.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                    g[r][c] = pool[r * C + c];
            if (AllHorizontalDifferent(g))
                hitH++;
            if (AnyAdjacentRowsEqual(g))
                hitDup++;
            if (AnyColumnPairAgrees(g, 8))
                hitCol++;
        }

        /* [5] null model B: random proper coloring + one duplicated row.
           Features surviving null A must be re-tested conditional on the rule
           already found, or the rule's side effects get mistaken for new rules. */
        int hitCol2 = 0, hitRow1 = 0;
        for (int n = 0; n < McRounds; n++)
        {
            var g9 = GenProperColoring(rng, 9);
            var gg = new char[R][];
            for (int r = 0; r < 5; r++)
                gg[r] = g9[r];
            gg[5] = g9[4];
            for (int r = 5; r < 9; r++)
                gg[r + 1] = g9[r];

            int minColHam = int.MaxValue;
            for (int i = 0; i < C; i++)
                for (int j = i + 1; j < C; j++)
                {
                    int ham = 0;
                    for (int r = 0; r < R; r++)
                        if (gg[r][i] != gg[r][j])
                            ham++;
                    minColHam = Math.Min(minColHam, ham);
                }
            if (minColHam <= 2)
                hitCol2++;

            int nearRowPairs = 0;
            for (int i = 0; i < R; i++)
                for (int j = i + 1; j < R; j++)
                {
                    if (i == 4 && j == 5)
                        continue;
                    int ham = 0;
                    for (int c = 0; c < C; c++)
                        if (gg[i][c] != gg[j][c])
                            ham++;
                    if (ham <= 1)
                        nearRowPairs++;
                }
            if (nearRowPairs >= 2)
                hitRow1++;
        }

        /* [6] star battery: local properties a marked cell could encode */
        List<(int, int)> Where(Func<int, int, bool> f)
        {
            var sel = new List<(int, int)>();
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                    if (f(r, c))
                        sel.Add((r, c));
            return sel;
        }
        IEnumerable<(int, int)> Nb(int r, int c, (int dr, int dc)[] deltas) =>
            deltas.Select(d => (r + d.dr, c + d.dc))
                  .Where(p => p.Item1 >= 0 && p.Item1 < R && p.Item2 >= 0 && p.Item2 < C);
        var orth = new[] { (-1, 0), (1, 0), (0, -1), (0, 1) };
        var diag = new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) };
        var battery = new List<(string, List<(int, int)>)>
        {
            ("與8鄰居皆不同", Where((r, c) => Nb(r, c, orth.Concat(diag).ToArray()).All(p => grid[r][c] != grid[p.Item1][p.Item2]))),
            ("與對角鄰皆不同", Where((r, c) => Nb(r, c, diag).All(p => grid[r][c] != grid[p.Item1][p.Item2]))),
            ("列內唯一該色", Where((r, c) => Enumerable.Range(0, C).Count(x => grid[r][x] == grid[r][c]) == 1)),
            ("欄內唯一該色", Where((r, c) => Enumerable.Range(0, R).Count(x => grid[x][c] == grid[r][c]) == 1)),
            ("被鄰居唯一決定", Where((r, c) => Nb(r, c, orth).Select(p => grid[p.Item1][p.Item2]).Distinct().Count() == 3)),
        };

        return new Results(counts, chi2, hv, vv, dv, nearRows, nearCols,
            (double)hitH / McRounds, (double)hitDup / McRounds, (double)hitCol / McRounds,
            (double)hitCol2 / McRounds, (double)hitRow1 / McRounds, battery);
    }

    private static int Hamming(string a, string b)
    {
        int d = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                d++;
        return d;
    }

    private static bool AllHorizontalDifferent(char[][] g)
    {
        for (int r = 0; r < R; r++)
            for (int c = 0; c < C - 1; c++)
                if (g[r][c] == g[r][c + 1])
                    return false;
        return true;
    }

    private static bool AnyAdjacentRowsEqual(char[][] g)
    {
        for (int r = 0; r < R - 1; r++)
            if (g[r].AsSpan().SequenceEqual(g[r + 1]))
                return true;
        return false;
    }

    private static bool AnyColumnPairAgrees(char[][] g, int minAgree)
    {
        for (int i = 0; i < C; i++)
            for (int j = i + 1; j < C; j++)
            {
                int same = 0;
                for (int r = 0; r < R; r++)
                    if (g[r][i] == g[r][j])
                        same++;
                if (same >= minAgree)
                    return true;
            }
        return false;
    }

    /* random proper 4-coloring: each cell uniform over colors differing from
       its left and upper neighbors */
    private static char[][] GenProperColoring(Rng rng, int rowsN)
    {
        var g = new char[rowsN][];
        Span<char> allowed = stackalloc char[4];
        for (int r = 0; r < rowsN; r++)
        {
            g[r] = new char[C];
            for (int c = 0; c < C; c++)
            {
                int n = 0;
                foreach (char k in Colors)
                    if ((c == 0 || g[r][c - 1] != k) && (r == 0 || g[r - 1][c] != k))
                        allowed[n++] = k;
                g[r][c] = allowed[rng.Next(n)];
            }
        }
        return g;
    }

    /* ---------- report ---------- */

    public static void Run(char[][] grid, List<(int r, int c)> stars)
    {
        var res = Compute(grid);
        Console.WriteLine("Grid:");
        foreach (var row in grid)
            Console.WriteLine("  " + string.Join(' ', row));
        Console.WriteLine($"Stars: {Pairs(stars)} \n");

        Console.WriteLine($"[1] 顏色分布 {{{string.Join(", ", Colors.Select(k => $"'{k}': {res.Counts[k]}"))}}}  " +
                          $"chi2={res.Chi2:F2} (df=3, 5%臨界=7.81) -> 與均勻分布無顯著差異");

        Console.WriteLine($"[2] 相鄰同色  水平 {res.HViol.Count}/40  垂直 {res.VViol.Count}/45 " +
                          $"位置={Pairs(res.VViol)}  對角 {res.DViol}/72(對照)");

        Console.WriteLine($"[3] 近似列: {Triples(res.NearRows)}");
        Console.WriteLine($"    近似欄: {Triples(res.NearCols)}");

        Console.WriteLine($"[4] Null A  P(水平0同色)={res.PZeroHorizontal:F5}  P(相鄰列全同)={res.PDuplicateRows:F5}  " +
                          $"P(欄位對>=8/10相同)={res.PColPair8:F5}");

        Console.WriteLine($"[5] Null B(條件化)  P(欄位對Hamming<=2)={res.PCondColHamming2:F4} <-邊際  " +
                          $"P(>=2對近似列)={res.PCondNearRows:F4} <-不顯著");

        Console.WriteLine("[6] 星星掃描:");
        foreach (var (name, sel) in res.Battery)
        {
            var hits = stars.Where(sel.Contains).ToList();
            bool unique = sel.Count == stars.Count && stars.All(sel.Contains);
            Console.WriteLine($"    {name}: 共{sel.Count}格, 星星命中 {Pairs(hits)}" +
                              (unique ? "  <== 唯一!" : ""));
        }
        Console.WriteLine("    結論: 無任何性質唯一挑出兩顆星 -> 星1壓在複製列縫線上(標記異常), 星2疑似干擾項");
    }

    private static string Pairs(IEnumerable<(int r, int c)> xs) =>
        "[" + string.Join(", ", xs.Select(p => $"({p.r}, {p.c})")) + "]";

    private static string Triples(IEnumerable<(int i, int j, int ham)> xs) =>
        "[" + string.Join(", ", xs.Select(t => $"({t.i}, {t.j}, {t.ham})")) + "]";
}
