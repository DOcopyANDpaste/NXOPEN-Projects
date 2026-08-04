using NxOpen.Foundation.Contracts.Materials;

namespace Core.MaterialLibrary;

public sealed class MaterialTabGrouper : IMaterialTabGrouper
{
    public IReadOnlyList<MaterialTab> GroupByCategory(NxOpen.Foundation.Contracts.Materials.MaterialLibrary library) =>
        library.Materials
            // Group by Category.Key (a plain string), not the MaterialCategory record itself: MaterialCategory
            // carries a PathSegments list, and list-typed record fields compare by reference, not by content —
            // grouping on the record directly would silently split materials with equal-but-distinct category
            // instances into separate tabs.
            .GroupBy(m => m.Category.Key)
            .OrderBy(g => g.First().Category.SortOrder ?? int.MaxValue)
            .ThenBy(g => g.First().Category.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MaterialTab(
                g.First().Category,
                g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}
