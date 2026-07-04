namespace Permissions;

/// <summary>
/// Independent reference implementation for differential testing (design doc §6):
/// per file, expand each ACL group entry downward (DFS over sub-groups) with no
/// preprocessing and no shared code with PermissionChecker. Deliberately kept
/// separate — its whole value is being a second, structurally different answer
/// to the same question. Do not refactor it to reuse the indexed path.
/// </summary>
public static class NaiveChecker
{
    public static bool CanRead(string user, IEnumerable<string> acl, IReadOnlyDictionary<string, Group> groups)
    {
        foreach (var entry in acl)
        {
            if (string.Equals(entry, user, StringComparison.Ordinal))
                return true;
            if (!groups.ContainsKey(entry))
                continue; // dangling ID: never matches
            var visited = new HashSet<string>(StringComparer.Ordinal) { entry };
            var stack = new Stack<string>();
            stack.Push(entry);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!groups.TryGetValue(cur, out var g))
                    continue;
                foreach (var m in g.Members)
                    if (string.Equals(m, user, StringComparison.Ordinal))
                        return true;
                foreach (var sg in g.SubGroups)
                    if (visited.Add(sg))
                        stack.Push(sg);
            }
        }
        return false;
    }
}
