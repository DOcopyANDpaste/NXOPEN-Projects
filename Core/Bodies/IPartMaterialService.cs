using Core.Assignment;
using Core.Common;
using NxOpen.Foundation.Contracts.Common;

namespace Core.Bodies;

/// <summary>Seam to the NX work part. Implemented by NxAdapters; Core never touches NXOpen types.
/// <see cref="GetBodies"/> and <see cref="GetCurrentAssignments"/> are always a fresh rescan of the
/// part — the assigned-materials table must reflect live state, never session-cached state.</summary>
public interface IPartMaterialService
{
    /// <summary>Every body in the work part — Solid, Sheet and Unknown alike.
    /// <c>BlockRestrictedBodyTypeRule</c> depends on Sheet bodies arriving here, so this deliberately
    /// does not filter; callers that want solids only filter on <see cref="BodyInfo.Kind"/>.</summary>
    IReadOnlyList<BodyInfo> GetBodies();

    IReadOnlyDictionary<BodyId, BodyMaterialAssignment> GetCurrentAssignments();

    /// <summary>Sets which loaded libraries physical-material names are best-effort matched against.
    /// Separate from <see cref="GetCurrentAssignments"/> because that takes no parameters — the caller
    /// re-registers whenever the library selection changes.</summary>
    void SetResolutionLibraries(IReadOnlyList<NxOpen.Foundation.Contracts.Materials.MaterialLibrary> libraries);

    OperationResult ApplyPlan(ExecutablePlan plan);

    /// <summary>Clears both the physical (bulk) and display/coating material from the given bodies.
    /// Bypasses the planner/finalizer pipeline — there is no requested material to plan against.</summary>
    OperationResult ClearMaterial(IReadOnlyList<BodyId> bodyIds);
}
