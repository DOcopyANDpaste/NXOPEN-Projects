using NxOpen.Foundation.Contracts.Materials;

namespace Core.MaterialLibrary;

public sealed class MaterialCategoryTreeBuilder : IMaterialCategoryTreeBuilder
{
    public IReadOnlyList<MaterialCategoryNode> Build(NxOpen.Foundation.Contracts.Materials.MaterialLibrary library)
    {
        var roots = new List<MutableNode>();

        // Group by Category.Key (a plain string), not the MaterialCategory record itself: MaterialCategory
        // carries a PathSegments list, and list-typed record fields compare by reference, not by content —
        // grouping on the record directly would silently split materials with equal-but-distinct category
        // instances into separate branches of the tree.
        foreach (var group in library.Materials.GroupBy(m => m.Category.Key))
        {
            var category = group.First().Category;

            // A category with no class hierarchy still needs somewhere to live, so it becomes a root of its
            // own named after itself. That is what puts MaterialCategory.Uncategorized (empty PathSegments,
            // SortOrder int.MaxValue) at the top level, last.
            var path = category.PathSegments.Count > 0
                ? category.PathSegments
                : new[] { category.DisplayName };

            var node = Descend(roots, path);

            // The curated category label wins over the raw path segment on the node that terminates the
            // path; intermediate nodes keep their segment text, since no category names them.
            node.DisplayName = category.DisplayName;
            node.Materials.AddRange(group);
            node.SortOrder = Math.Min(node.SortOrder, category.SortOrder ?? int.MaxValue);
        }

        foreach (var root in roots)
            ComputeEffectiveSortOrder(root);

        return Freeze(roots);
    }

    /// <summary>Walks <paramref name="path"/> from <paramref name="roots"/>, creating nodes as needed, and
    /// returns the node the path terminates at. Segments match case-insensitively so a library that spells
    /// the same class differently across entries still produces one branch.</summary>
    private static MutableNode Descend(List<MutableNode> roots, IReadOnlyList<string> path)
    {
        var siblings = roots;
        MutableNode? node = null;

        foreach (var segment in path)
        {
            node = siblings.FirstOrDefault(n => string.Equals(n.Segment, segment, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = new MutableNode(segment);
                siblings.Add(node);
            }

            siblings = node.Children;
        }

        // Non-null: callers always pass at least one segment.
        return node!;
    }

    /// <summary>A node sorts by the lowest SortOrder anywhere in its subtree, so an explicitly ordered
    /// category pulls its ancestors up with it — otherwise every intermediate node would tie at
    /// int.MaxValue and fall back to alphabetical, burying a category the library author ordered first.</summary>
    private static int ComputeEffectiveSortOrder(MutableNode node)
    {
        var order = node.SortOrder;
        foreach (var child in node.Children)
            order = Math.Min(order, ComputeEffectiveSortOrder(child));

        node.EffectiveSortOrder = order;
        return order;
    }

    private static IReadOnlyList<MaterialCategoryNode> Freeze(List<MutableNode> nodes) =>
        nodes
            .OrderBy(n => n.EffectiveSortOrder)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(n => new MaterialCategoryNode(
                n.Segment,
                n.DisplayName,
                Freeze(n.Children),
                n.Materials.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

    private sealed class MutableNode(string segment)
    {
        public string Segment { get; } = segment;
        public string DisplayName { get; set; } = segment;
        public List<MutableNode> Children { get; } = new();
        public List<Material> Materials { get; } = new();
        public int SortOrder { get; set; } = int.MaxValue;
        public int EffectiveSortOrder { get; set; } = int.MaxValue;
    }
}
