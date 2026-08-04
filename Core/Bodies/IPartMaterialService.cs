using Core.Assignment;
using Core.Common;
using NxOpen.Foundation.Contracts.Common;

namespace Core.Bodies;

/// <summary>Seam to the NX work part. Implemented by NxAdapters in a later phase; Core never touches
/// NXOpen types. <see cref="GetCurrentAssignments"/> is always a fresh rescan of the part — the
/// assigned-materials table must reflect live state, never session-cached state.</summary>
public interface IPartMaterialService
{
    IReadOnlyList<BodyInfo> GetSolidBodies();

    IReadOnlyDictionary<BodyId, BodyMaterialAssignment> GetCurrentAssignments();

    OperationResult ApplyPlan(ExecutablePlan plan);
}
