using System.Diagnostics;

namespace Permissions;

/// <summary>
/// Scale benchmark with built-in differential validation (design doc §3, §6):
/// generates a deterministic topology, runs the naive reference and the design
/// over the same inputs, times both, and requires their outputs to be identical.
/// </summary>
public static class Benchmark
{
    /// <summary>
    /// xorshift64* PRNG with a fixed seed — identical sequence on every run and
    /// platform, unlike System.Random whose algorithm may change across .NET
    /// versions. This is what makes the published numbers reproducible.
    /// </summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int maxExclusive)
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            ulong r = _state * 2685821657736338717UL;
            return (int)(r % (ulong)maxExclusive);
        }
    }

    public static int Run(int chains, int depth, int fileCount, int entriesPerFile)
    {
        // Topology: `chains` independent chains, each `depth` deep. G{c}_0 is the
        // top of chain c and G{c}_{depth-1} the bottom; the target user sits at
        // the bottom of chain 0, so their closure is exactly chain 0 (+ themselves).
        const string user = "U_target";
        var groups = new Dictionary<string, Group>(chains * depth, StringComparer.Ordinal);
        for (int c = 0; c < chains; c++)
            for (int d = 0; d < depth; d++)
            {
                var members = new List<string> { $"u{c}_{d}" };
                if (c == 0 && d == depth - 1)
                    members.Add(user);
                var subs = d < depth - 1 ? new List<string> { $"G{c}_{d + 1}" } : new List<string>();
                groups[$"G{c}_{d}"] = new Group(members, subs);
            }

        // P(entry ∈ closure) = depth / (chains × depth), so expected readable
        // ≈ fileCount × entriesPerFile / chains — sanity anchor for the result count.
        var rng = new Rng(42);
        var files = new List<FileEntry>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            var acl = new List<string>(entriesPerFile);
            for (int e = 0; e < entriesPerFile; e++)
                acl.Add($"G{rng.Next(chains)}_{rng.Next(depth)}");
            files.Add(new FileEntry($"f{i}", acl));
        }
        Console.WriteLine($"topology: {chains} chains x depth {depth} = {chains * depth:N0} groups; {fileCount:N0} files x {entriesPerFile} ACL entries");

        var swNaive = Stopwatch.StartNew();
        var naiveReadable = new List<string>();
        foreach (var f in files)
            if (NaiveChecker.CanRead(user, f.Acl, groups))
                naiveReadable.Add(f.Name);
        swNaive.Stop();

        // index build deliberately included in the design's timing
        var swFast = Stopwatch.StartNew();
        var checker = new PermissionChecker(groups);
        var closure = checker.ComputeClosure(user);
        var fastReadable = new List<string>();
        foreach (var f in files)
            if (PermissionChecker.CanRead(f.Acl, closure))
                fastReadable.Add(f.Name);
        swFast.Stop();

        bool match = naiveReadable.SequenceEqual(fastReadable, StringComparer.Ordinal);
        Console.WriteLine($"naive (per-file downward DFS): {swNaive.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"design (index + closure + scan): {swFast.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"speedup: {(double)swNaive.ElapsedMilliseconds / Math.Max(1, swFast.ElapsedMilliseconds):F1}x");
        Console.WriteLine($"readable: {fastReadable.Count}; outputs identical: {match}");
        return match ? 0 : 1;
    }
}
