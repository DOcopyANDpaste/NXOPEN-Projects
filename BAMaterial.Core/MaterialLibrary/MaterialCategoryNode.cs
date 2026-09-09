using NxOpen.Foundation.Contracts.Materials;

namespace Core.MaterialLibrary;

/// <summary>One node of the nested material-category tree the dialog's material browser renders.
/// <see cref="Segment"/> is the raw class-hierarchy segment this node was created from and is what
/// identifies it among its siblings; <see cref="DisplayName"/> is what the user sees — the same thing for
/// intermediate nodes, but the owning category's curated <see cref="MaterialCategory.DisplayName"/> for a
/// node that terminates a category's path.
///
/// <see cref="Materials"/> is non-empty only on nodes that terminate a category path — an intermediate
/// node like "Metals" holds no materials of its own, only child nodes. Both lists are already ordered for
/// rendering, so the UI makes no sorting decisions of its own.
///
/// Presentation-shaped, so it stays in this repo's Core rather than moving to NxOpen.Foundation with the
/// rest of the material-library reading module — only this tool's presenter consumes it.</summary>
public sealed record MaterialCategoryNode(
    string Segment,
    string DisplayName,
    IReadOnlyList<MaterialCategoryNode> Children,
    IReadOnlyList<Material> Materials);
