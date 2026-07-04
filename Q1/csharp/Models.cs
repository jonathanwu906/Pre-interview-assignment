namespace Permissions;

/// <summary>
/// Stored in parent → child direction: a group names its sub-groups, never its
/// parents. IDs are opaque, case-sensitive strings; referencing an ID that is
/// never defined is legal — dangling IDs simply never match (design doc §1.4).
/// </summary>
public sealed record Group(List<string> Members, List<string> SubGroups);

public sealed record FileEntry(string Name, List<string> Acl);

public sealed record InputDoc(string User, Dictionary<string, Group> Groups, List<FileEntry> Files);
