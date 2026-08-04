using NxOpen.Foundation.Contracts.Materials;

namespace Core.MaterialLibrary;

/// <summary>One dialog tab's worth of materials, pre-grouped and pre-sorted. This is a
/// presentation-shaped output of <see cref="IMaterialTabGrouper"/>, specific to how this tool renders
/// its material browser — not a reusable domain concept, so it stayed behind in this repo's Core
/// rather than moving to NxOpen.Foundation with the rest of the material-library reading module. Only
/// the presenter (which references Core directly, same as it does for IMaterialTabGrouper itself)
/// ever consumes it.</summary>
public sealed record MaterialTab(
    MaterialCategory Category,
    IReadOnlyList<Material> Materials);
