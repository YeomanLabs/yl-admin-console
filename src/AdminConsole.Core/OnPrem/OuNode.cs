namespace AdminConsole.Core.OnPrem;

/// <summary>
/// Assembles a flat list of OUs into the tree an admin recognises (M12.3).
/// </summary>
/// <remarks>
/// AD has no parent link — an OU's place in the hierarchy is encoded in its distinguished name and nowhere else. So
/// the tree is derived rather than read, which also means it survives an OU whose parent the search did not return
/// (a permission boundary, or a size limit): such a node is attached at the nearest ancestor that did come back,
/// rather than being dropped and silently under-reporting what is there.
/// </remarks>
public sealed class OuNode
{
    public required OnPremOu Ou { get; init; }

    public List<OuNode> Children { get; } = [];

    public string Name => Ou.Name;

    public string DistinguishedName => Ou.DistinguishedName;

    /// <summary>How deep it sits, for indentation.</summary>
    public int Depth { get; init; }

    /// <summary>Every node beneath this one, depth first, including itself.</summary>
    public IEnumerable<OuNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten()) yield return descendant;
        }
    }

    public static IReadOnlyList<OuNode> Build(IEnumerable<OnPremOu> ous)
    {
        // Shortest DN first, so a parent is always placed before its children.
        var ordered = ous
            .OrderBy(o => DistinguishedNames.Split(o.DistinguishedName).Count)
            .ThenBy(o => o.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byDn = new Dictionary<string, OuNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<OuNode>();

        foreach (var ou in ordered)
        {
            var parent = FindNearestAncestor(ou.DistinguishedName, byDn);
            var node = new OuNode { Ou = ou, Depth = parent is null ? 0 : parent.Depth + 1 };
            byDn[ou.DistinguishedName] = node;

            if (parent is null) roots.Add(node);
            else parent.Children.Add(node);
        }

        return roots;
    }

    /// <summary>
    /// The closest ancestor that is actually present. Walking up rather than taking the immediate parent is what
    /// keeps an orphan visible when its parent was not returned.
    /// </summary>
    private static OuNode? FindNearestAncestor(string dn, Dictionary<string, OuNode> byDn)
    {
        var candidate = DistinguishedNames.Parent(dn);
        while (candidate is not null)
        {
            if (byDn.TryGetValue(candidate, out var node)) return node;
            candidate = DistinguishedNames.Parent(candidate);
        }

        return null;
    }
}
