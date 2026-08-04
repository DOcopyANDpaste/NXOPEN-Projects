using NxOpen.Foundation.Contracts.Common;

namespace NxAdapters.Ui;

/// <summary>One row of the material-usage table: a distinct physical material name currently in use in
/// the part (or the synthetic <see cref="Unassigned"/> row for bodies with no physical material), plus
/// how many bodies carry it. Presentation-only — derived at presenter level from
/// <c>IPartMaterialService.GetCurrentAssignments()</c> + <c>GetSolidBodies()</c>, not a shared Core
/// domain type (same reasoning <c>Core.MaterialLibrary.MaterialTab</c> uses for staying out of
/// Core's own domain-DTO folders).</summary>
public sealed record MaterialUsageRow(string MaterialLabel, MaterialId? ResolvedMaterialId, int BodyCount)
{
    public const string UnassignedLabel = "(No material assigned)";

    public bool IsUnassignedBucket => ResolvedMaterialId is null && string.Equals(MaterialLabel, UnassignedLabel, StringComparison.Ordinal);
}
