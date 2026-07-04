namespace Permissions;

/// <summary>
/// The design under evaluation: reverse index + upward BFS closure + O(1) hash scan.
/// Single batch query is O(E + A) — E = membership edges, A = total ACL entries.
///
/// Semantic assumptions fixed here (design doc §1):
///  1. Exact ordinal match only — every dictionary/set uses StringComparer.Ordinal;
///     prefix/substring/culture-sensitive matching would grant wrong access.
///  2. Membership propagates upward (child → parent): a member of a sub-group is
///     an effective member of every ancestor group.
///  3. The group graph is NOT assumed to be a tree or acyclic — diamonds, cycles
///     and self-loops must not affect correctness or termination.
///  4. Dangling IDs are tolerated: an ACL entry or sub-group reference that names
///     no defined principal simply never matches; it is not an error.
/// </summary>
public sealed class PermissionChecker
{
    private readonly Dictionary<string, List<string>> _userToDirectGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _childToParents = new(StringComparer.Ordinal);

    /// <summary>
    /// Phase 1 — build the reverse index in O(E). The stored data points
    /// parent → child; the closure query needs child → parent, so we invert once.
    /// </summary>
    public PermissionChecker(IReadOnlyDictionary<string, Group> groups)
    {
        foreach (var (name, g) in groups)
        {
            foreach (var m in g.Members)
            {
                if (!_userToDirectGroups.TryGetValue(m, out var list))
                    _userToDirectGroups[m] = list = new List<string>();
                list.Add(name);
            }
            foreach (var sg in g.SubGroups)
            {
                if (!_childToParents.TryGetValue(sg, out var list))
                    _childToParents[sg] = list = new List<string>();
                list.Add(name);
            }
        }
    }

    /// <summary>
    /// Phase 2 — effective identity set S = {user} ∪ all reachable ancestor groups.
    /// Iterative BFS: no recursion, so a 10⁴-deep chain cannot overflow the stack.
    /// S doubles as the visited set, so under diamonds/cycles every node is
    /// enqueued exactly once and the walk always terminates. Only the subgraph
    /// reachable from this user is ever touched: O(V′ + E′).
    /// </summary>
    public HashSet<string> ComputeClosure(string user)
    {
        var s = new HashSet<string>(StringComparer.Ordinal) { user };
        var queue = new Queue<string>();
        if (_userToDirectGroups.TryGetValue(user, out var direct))
            foreach (var g in direct)
                if (s.Add(g))
                    queue.Enqueue(g);
        while (queue.Count > 0)
        {
            var g = queue.Dequeue();
            if (_childToParents.TryGetValue(g, out var parents))
                foreach (var p in parents)
                    if (s.Add(p))
                        queue.Enqueue(p);
        }
        return s;
    }

    /// <summary>
    /// Phase 3 — one amortized-O(1) ordinal hash lookup per ACL entry; any hit
    /// grants read. S is the only resident state, so the file source can be a
    /// stream and the scan can be partitioned across machines.
    /// </summary>
    public static bool CanRead(IEnumerable<string> acl, HashSet<string> closure)
    {
        foreach (var entry in acl)
            if (closure.Contains(entry))
                return true;
        return false;
    }
}
