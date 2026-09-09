using BAMaterial.Core.Bodies;
using BAMaterial.Core.Common;
using NxOpen.Foundation.Contracts.Materials;

namespace BAMaterial.Core.Assignment;

public sealed record MaterialAssignmentPlanningInput(
    Material RequestedMaterial,
    IReadOnlyList<BodyInfo> TargetBodies,
    IReadOnlyDictionary<BodyId, BodyMaterialAssignment> CurrentAssignments);
