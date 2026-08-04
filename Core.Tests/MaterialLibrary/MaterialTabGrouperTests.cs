using Core.MaterialLibrary;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;

namespace Core.Tests.MaterialLibrary;

public class MaterialTabGrouperTests
{
    private static readonly MaterialLibraryId LibraryId = new("lib1");

    private static Material MakeMaterial(string name, MaterialCategory category, MaterialId? id = null) =>
        new(id ?? new MaterialId(name), LibraryId, name, category, Array.Empty<MaterialPropertyValue>());

    [Fact]
    public void GroupByCategory_GroupsMaterialsWithEqualButDistinctCategoryInstancesTogether()
    {
        // Two separately-constructed MaterialCategory records with the same Key but different
        // PathSegments array instances — this is exactly the scenario where grouping on the record
        // itself (rather than Category.Key) would silently split them into two tabs.
        var steelA = new MaterialCategory("metal/steel", "Steel", new[] { "Metal", "Steel" });
        var steelB = new MaterialCategory("metal/steel", "Steel", new[] { "Metal", "Steel" });

        var library = new NxOpen.Foundation.Contracts.Materials.MaterialLibrary(LibraryId, "Lib", new[]
        {
            MakeMaterial("Steel A", steelA),
            MakeMaterial("Steel B", steelB),
        });

        var tabs = new MaterialTabGrouper().GroupByCategory(library);

        var tab = Assert.Single(tabs);
        Assert.Equal(2, tab.Materials.Count);
    }

    [Fact]
    public void GroupByCategory_OrdersTabsBySortOrderThenDisplayName()
    {
        var second = new MaterialCategory("b", "Bravo", new[] { "Bravo" }, SortOrder: 2);
        var first = new MaterialCategory("a", "Alpha", new[] { "Alpha" }, SortOrder: 1);
        var noSortOrder = new MaterialCategory("z", "Zulu", new[] { "Zulu" });

        var library = new NxOpen.Foundation.Contracts.Materials.MaterialLibrary(LibraryId, "Lib", new[]
        {
            MakeMaterial("m-zulu", noSortOrder),
            MakeMaterial("m-bravo", second),
            MakeMaterial("m-alpha", first),
        });

        var tabs = new MaterialTabGrouper().GroupByCategory(library);

        Assert.Equal(new[] { "Alpha", "Bravo", "Zulu" }, tabs.Select(t => t.Category.DisplayName));
    }

    [Fact]
    public void GroupByCategory_SortsMaterialsWithinATabByNameCaseInsensitively()
    {
        var category = new MaterialCategory("cat", "Cat", new[] { "Cat" });
        var library = new NxOpen.Foundation.Contracts.Materials.MaterialLibrary(LibraryId, "Lib", new[]
        {
            MakeMaterial("charlie", category),
            MakeMaterial("Alpha", category),
            MakeMaterial("bravo", category),
        });

        var tabs = new MaterialTabGrouper().GroupByCategory(library);

        var tab = Assert.Single(tabs);
        Assert.Equal(new[] { "Alpha", "bravo", "charlie" }, tab.Materials.Select(m => m.Name));
    }

    [Fact]
    public void GroupByCategory_UncategorizedMaterialsSortLastByDefault()
    {
        var named = new MaterialCategory("named", "Named", new[] { "Named" }, SortOrder: 1);
        var library = new NxOpen.Foundation.Contracts.Materials.MaterialLibrary(LibraryId, "Lib", new[]
        {
            MakeMaterial("m-uncategorized", MaterialCategory.Uncategorized),
            MakeMaterial("m-named", named),
        });

        var tabs = new MaterialTabGrouper().GroupByCategory(library);

        Assert.Equal(new[] { "Named", "Uncategorized" }, tabs.Select(t => t.Category.DisplayName));
    }
}
