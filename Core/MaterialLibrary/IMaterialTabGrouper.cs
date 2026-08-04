using NxOpen.Foundation.Contracts.Materials;

namespace Core.MaterialLibrary;

/// <summary>Groups a library's materials into sorted, ready-to-render tabs. The UI layer makes zero
/// grouping/sorting decisions of its own — it renders exactly what this returns.</summary>
public interface IMaterialTabGrouper
{
    IReadOnlyList<MaterialTab> GroupByCategory(NxOpen.Foundation.Contracts.Materials.MaterialLibrary library);
}
