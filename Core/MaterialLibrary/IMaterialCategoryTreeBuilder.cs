using NxOpen.Foundation.Contracts.Materials;

namespace Core.MaterialLibrary;

/// <summary>Builds the nested, sorted category tree the material browser renders. The UI layer makes zero
/// grouping/sorting decisions of its own — it renders exactly what this returns.</summary>
public interface IMaterialCategoryTreeBuilder
{
    /// <summary>The root nodes of the tree, in render order.</summary>
    IReadOnlyList<MaterialCategoryNode> Build(NxOpen.Foundation.Contracts.Materials.MaterialLibrary library);
}
