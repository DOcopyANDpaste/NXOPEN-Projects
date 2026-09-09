using BAMaterial.Core.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.Foundation.Contracts.Bodies;

namespace BAMaterial.Core.Assignment;

public sealed record MaterialAssignmentPlanningInput(
    Material RequestedMaterial,
    IReadOnlyList<BodyInfo> TargetBodies,
    IReadOnlyDictionary<BodyId, BodyMaterialAssignment> CurrentAssignments);
