using BAMaterial.Core.Bodies;
using BANxOpen.Foundation.Contracts.Materials;

namespace BAMaterial.Core.Assignment;

/// <summary>Everything a rule needs to evaluate one (body, requested material) pair.
/// <see cref="AllTargetBodiesInBatch"/> gives rules visibility into the whole Apply batch for
/// cross-body reasoning, even though every decision is still emitted per-body.</summary>
public sealed record MaterialAssignmentRuleContext(
    Material RequestedMaterial,
    BodyInfo TargetBody,
    BodyMaterialAssignment? CurrentAssignment,
    IReadOnlyList<BodyInfo> AllTargetBodiesInBatch);
