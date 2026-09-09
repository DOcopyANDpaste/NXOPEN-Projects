using Core.Bodies;
using Core.Common;
using NxOpen.Foundation.Contracts.Materials;

namespace Core.Assignment;

public sealed record MaterialAssignmentPlanningInput(
    Material RequestedMaterial,
    IReadOnlyList<BodyInfo> TargetBodies,
    IReadOnlyDictionary<BodyId, BodyMaterialAssignment> CurrentAssignments);
