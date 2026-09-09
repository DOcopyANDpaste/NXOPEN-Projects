using Core.MaterialLibrary;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;

namespace Core.Tests.MaterialLibrary;

public class MaterialCategoryTreeBuilderTests
{
    private static readonly MaterialLibraryId LibraryId = new("lib1");

    private static Material MakeMaterial(string name, MaterialCategory category, MaterialId? id = null) =>
        new(id ?? new MaterialId(name), LibraryId, name, category, Array.Empty<MaterialPropertyValue>());

    private static NxOpen.Foundation.Contracts.Materials.MaterialLibrary MakeLibrary(params Material[] materials) =>
        new(LibraryId, "Lib", materials);

    private static IReadOnlyList<MaterialCategoryNode> Build(params Material[] materials) =>
        new MaterialCategoryTreeBuilder().Build(MakeLibrary(materials));

    [Fact]
    public void Build_NestsCategoriesByTheirPathSegments()
    {
        var alloySteel = new MaterialCategory("metal/steel/alloy", "Alloy Steel", new[] { "Metals", "Steel", "Alloy Steel" });

        var roots = Build(MakeMaterial("4140", alloySteel));

        var metals = Assert.Single(roots);
        Assert.Equal("Metals", metals.DisplayName);
        Assert.Empty(metals.Materials);

        var steel = Assert.Single(metals.Children);
        Assert.Equal("Steel", steel.DisplayName);
        Assert.Empty(steel.Materials);

        var alloy = Assert.Single(steel.Children);
        Assert.Equal("Alloy Steel", alloy.DisplayName);
        Assert.Empty(alloy.Children);
        Assert.Equal(new[] { "4140" }, alloy.Materials.Select(m => m.Name));
    }

    [Fact]
    public void Build_MergesCategoriesThatShareAPathPrefixIntoOneBranch()
    {
        var steel = new MaterialCategory("metal/steel", "Steel", new[] { "Metals", "Steel" });
        var aluminum = new MaterialCategory("metal/aluminum", "Aluminum", new[] { "Metals", "Aluminum" });

        var roots = Build(MakeMaterial("4140", steel), MakeMaterial("6061", aluminum));

        var metals = Assert.Single(roots);
        Assert.Equal(new[] { "Aluminum", "Steel" }, metals.Children.Select(c => c.DisplayName));
    }

    [Fact]
    public void Build_GroupsMaterialsWithEqualButDistinctCategoryInstancesTogether()
    {
        // Two separately-constructed MaterialCategory records with the same Key but different PathSegments
        // array instances — this is exactly the scenario where grouping on the record itself (rather than
        // Category.Key) would silently split them into two branches.
        var steelA = new MaterialCategory("metal/steel", "Steel", new[] { "Metals", "Steel" });
        var steelB = new MaterialCategory("metal/steel", "Steel", new[] { "Metals", "Steel" });

        var roots = Build(MakeMaterial("Steel A", steelA), MakeMaterial("Steel B", steelB));

        var steel = Assert.Single(Assert.Single(roots).Children);
        Assert.Equal(new[] { "Steel A", "Steel B" }, steel.Materials.Select(m => m.Name));
    }

    [Fact]
    public void Build_TreatsPathSegmentsCaseInsensitivelySoOneBranchIsProduced()
    {
        var lower = new MaterialCategory("a", "Steel", new[] { "metals", "steel" });
        var upper = new MaterialCategory("b", "Stainless", new[] { "Metals", "Stainless" });

        var roots = Build(MakeMaterial("4140", lower), MakeMaterial("316", upper));

        var metals = Assert.Single(roots);
        Assert.Equal(2, metals.Children.Count);
    }

    [Fact]
    public void Build_OrdersSiblingsBySortOrderThenDisplayName()
    {
        var second = new MaterialCategory("b", "Bravo", new[] { "Bravo" }, SortOrder: 2);
        var first = new MaterialCategory("a", "Alpha", new[] { "Alpha" }, SortOrder: 1);
        var noSortOrder = new MaterialCategory("z", "Zulu", new[] { "Zulu" });

        var roots = Build(
            MakeMaterial("m-zulu", noSortOrder),
            MakeMaterial("m-bravo", second),
            MakeMaterial("m-alpha", first));

        Assert.Equal(new[] { "Alpha", "Bravo", "Zulu" }, roots.Select(r => r.DisplayName));
    }

    [Fact]
    public void Build_SortsAnIntermediateNodeByTheLowestSortOrderInItsSubtree()
    {
        // "Plastics" is ordered first outright; "Metals" only earns its place from a grandchild. Without
        // subtree propagation the intermediate Metals node would tie at int.MaxValue and sort alphabetically
        // — ahead of Plastics — burying the ordering the library author expressed.
        var plastics = new MaterialCategory("p", "Plastics", new[] { "Plastics" }, SortOrder: 5);
        var steel = new MaterialCategory("s", "Steel", new[] { "Metals", "Steel" }, SortOrder: 1);

        var roots = Build(MakeMaterial("ABS", plastics), MakeMaterial("4140", steel));

        Assert.Equal(new[] { "Metals", "Plastics" }, roots.Select(r => r.DisplayName));
    }

    [Fact]
    public void Build_SortsMaterialsWithinANodeByNameCaseInsensitively()
    {
        var category = new MaterialCategory("cat", "Cat", new[] { "Cat" });

        var roots = Build(
            MakeMaterial("charlie", category),
            MakeMaterial("Alpha", category),
            MakeMaterial("bravo", category));

        Assert.Equal(new[] { "Alpha", "bravo", "charlie" }, Assert.Single(roots).Materials.Select(m => m.Name));
    }

    [Fact]
    public void Build_PromotesACategoryWithNoPathSegmentsToItsOwnRoot()
    {
        var named = new MaterialCategory("named", "Named", new[] { "Named" }, SortOrder: 1);

        var roots = Build(
            MakeMaterial("m-uncategorized", MaterialCategory.Uncategorized),
            MakeMaterial("m-named", named));

        Assert.Equal(new[] { "Named", "Uncategorized" }, roots.Select(r => r.DisplayName));
        Assert.Equal(new[] { "m-uncategorized" }, roots[1].Materials.Select(m => m.Name));
    }

    [Fact]
    public void Build_PrefersTheCategoryDisplayNameOverTheRawSegmentOnTheTerminatingNode()
    {
        // The last path segment is the source class name; DisplayName is the curated label. They usually
        // coincide, but when they don't the user should see the curated one.
        var category = new MaterialCategory("k", "Stainless Steel", new[] { "Metals", "SS" });

        var roots = Build(MakeMaterial("316", category));

        var terminating = Assert.Single(Assert.Single(roots).Children);
        Assert.Equal("SS", terminating.Segment);
        Assert.Equal("Stainless Steel", terminating.DisplayName);
    }

    [Fact]
    public void Build_HandlesACategoryTerminatingOnAnAncestorOfAnotherCategory()
    {
        // "Metals" is both a real category with its own materials and the parent of "Steel" — the node has
        // to carry materials and children at once.
        var metals = new MaterialCategory("m", "Metals", new[] { "Metals" });
        var steel = new MaterialCategory("s", "Steel", new[] { "Metals", "Steel" });

        var roots = Build(MakeMaterial("Generic Metal", metals), MakeMaterial("4140", steel));

        var root = Assert.Single(roots);
        Assert.Equal(new[] { "Generic Metal" }, root.Materials.Select(m => m.Name));
        Assert.Equal(new[] { "4140" }, Assert.Single(root.Children).Materials.Select(m => m.Name));
    }

    [Fact]
    public void Build_ReturnsNothingForAnEmptyLibrary()
    {
        Assert.Empty(Build());
    }
}
