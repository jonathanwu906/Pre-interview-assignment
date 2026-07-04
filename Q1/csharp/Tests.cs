namespace Permissions;

/// <summary>
/// The 13 test cases from design doc §6 — semantics, structure, and scale.
/// Zero test-framework dependencies: `dotnet run` executes them directly.
/// Wherever the naive reference applies, both implementations must agree.
/// </summary>
public static class Tests
{
    public static int RunAll()
    {
        int passed = 0, total = 0;

        void Check(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        }

        static bool Both(Dictionary<string, Group> groups, string user, string[] acl, bool expected)
        {
            var closure = new PermissionChecker(groups).ComputeClosure(user);
            bool fast = PermissionChecker.CanRead(acl, closure);
            bool naive = NaiveChecker.CanRead(user, acl, groups);
            return fast == expected && naive == expected;
        }

        var sample = SampleData.Groups();

        Check("1 sample ACL (U5n, G7E33aR, U33, G333, U7a01 — all near-miss) → not readable",
            Both(sample, "U5nn", SampleData.SampleAcl, expected: false));

        var s = new PermissionChecker(sample).ComputeClosure("U5nn");
        var expectedS = new HashSet<string>(StringComparer.Ordinal)
            { "U5nn", "G33", "G54A", "G7E", "G17yT", "G7E33a", "G6f4D9", "G8ig5" };
        Check("2 closure(U5nn) = 8 identities incl. G7E33a", s.SetEquals(expectedS));

        var cross = new Dictionary<string, Group>(StringComparer.Ordinal)
        {
            ["G7E"] = new(new List<string>(), new List<string> { "G54A" }),
            ["G54A"] = new(new List<string> { "Ua" }, new List<string>()),
            ["G7E33a"] = new(new List<string>(), new List<string> { "G17yT" }),
            ["G17yT"] = new(new List<string> { "Ub" }, new List<string>()),
        };
        Check("3 G7E / G7E33a cross grants do not cover each other",
            Both(cross, "Ua", new[] { "G7E" }, true) &&
            Both(cross, "Ub", new[] { "G7E" }, false) &&
            Both(cross, "Ub", new[] { "G7E33a" }, true) &&
            Both(cross, "Ua", new[] { "G7E33a" }, false));

        Check("4 direct user grant → readable",
            Both(sample, "U5nn", new[] { "U5nn" }, true));

        Check("5 single-layer group grant → readable",
            Both(sample, "U5nn", new[] { "G33" }, true));

        const int deep = 10_000;
        var chain = new Dictionary<string, Group>(StringComparer.Ordinal);
        for (int i = 0; i < deep; i++)
        {
            var members = i == deep - 1 ? new List<string> { "Udeep" } : new List<string>();
            var subs = i < deep - 1 ? new List<string> { $"C{i + 1}" } : new List<string>();
            chain[$"C{i}"] = new Group(members, subs);
        }
        Check("6 10^4-deep chain → readable, no overflow (why the closure BFS is iterative)",
            Both(chain, "Udeep", new[] { "C0" }, true));

        var diamond = new Dictionary<string, Group>(StringComparer.Ordinal)
        {
            ["A"] = new(new List<string>(), new List<string> { "B", "C" }),
            ["B"] = new(new List<string>(), new List<string> { "D" }),
            ["C"] = new(new List<string>(), new List<string> { "D" }),
            ["D"] = new(new List<string> { "Ud" }, new List<string>()),
        };
        var sd = new PermissionChecker(diamond).ComputeClosure("Ud");
        Check("7 diamond → closure deduplicated",
            sd.Count == 5 && Both(diamond, "Ud", new[] { "A" }, true));

        var cycle = new Dictionary<string, Group>(StringComparer.Ordinal)
        {
            ["X"] = new(new List<string> { "Uc" }, new List<string> { "Y" }),
            ["Y"] = new(new List<string>(), new List<string> { "X" }),
        };
        Check("8 cycle → terminates, readable via either group",
            Both(cycle, "Uc", new[] { "Y" }, true) && Both(cycle, "Uc", new[] { "X" }, true));

        var selfLoop = new Dictionary<string, Group>(StringComparer.Ordinal)
        {
            ["Z"] = new(new List<string> { "Uz" }, new List<string> { "Z" }),
        };
        Check("9 self-loop → terminates", Both(selfLoop, "Uz", new[] { "Z" }, true));

        Check("10 dangling-only ACL → not readable, no crash",
            Both(sample, "U5nn", new[] { "Unobody", "G_nothing" }, false));

        Check("11 empty ACL → not readable",
            Both(sample, "U5nn", Array.Empty<string>(), false));

        Check("12 groupless user (no index entry) granted directly → readable",
            Both(sample, "Ulone", new[] { "Ulone" }, true));

        var files = new List<FileEntry>
        {
            new("f1", new List<string> { "U5nn" }),
            new("f2", new List<string> { "U5n" }),
            new("f3", new List<string> { "G8ig5" }),
            new("f4", new List<string>()),
            new("f5", new List<string> { "G54A" }),
        };
        var closure13 = new PermissionChecker(sample).ComputeClosure("U5nn");
        var readable = files.Where(f => PermissionChecker.CanRead(f.Acl, closure13))
                            .Select(f => f.Name).ToList();
        Check("13 mixed file set → readable subset in input order",
            readable.SequenceEqual(new[] { "f1", "f3", "f5" }, StringComparer.Ordinal));

        Console.WriteLine($"{passed}/{total} tests passed");
        return passed == total ? 0 : 1;
    }
}

/// <summary>The assignment example topology — same data as sample-input.json.</summary>
public static class SampleData
{
    public static readonly string[] SampleAcl = { "U5n", "G7E33aR", "U33", "G333", "U7a01" };

    public static Dictionary<string, Group> Groups() => new(StringComparer.Ordinal)
    {
        // chain A: U5nn ∈ G33 ⊂ G54A ⊂ G7E
        ["G7E"] = new(new List<string>(), new List<string> { "G54A" }),
        ["G54A"] = new(new List<string>(), new List<string> { "G33" }),
        ["G33"] = new(new List<string> { "U5nn" }, new List<string>()),
        // chain B: U5nn ∈ G17yT ⊂ G7E33a ⊂ G6f4D9 ⊂ G8ig5
        ["G8ig5"] = new(new List<string>(), new List<string> { "G6f4D9" }),
        ["G6f4D9"] = new(new List<string>(), new List<string> { "G7E33a" }),
        ["G7E33a"] = new(new List<string>(), new List<string> { "G17yT" }),
        ["G17yT"] = new(new List<string> { "U5nn" }, new List<string>()),
        // near-miss IDs exist as real, unrelated principals — exact-match semantics under test
        ["G7E33aR"] = new(new List<string> { "U5n" }, new List<string>()),
        ["G333"] = new(new List<string> { "U33", "U7a01" }, new List<string>()),
    };
}
