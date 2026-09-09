using Core.Common;
using Core.MaterialLibrary;
using NxOpen.Foundation.Contracts.Common;

namespace Core.Bodies;

/// <summary>The material currently assigned to a body, as read off the part. <see cref="MaterialName"/>
/// is the raw name NX stores on the body; <see cref="ResolvedMaterialId"/> is populated only when that
/// name was successfully matched (best-effort, case-insensitive) against a currently loaded library.
/// <see cref="CurrentDisplayMaterial"/> is the display/coating material currently associated with the
/// body in NX, or null if none is associated.</summary>
public sealed record BodyMaterialAssignment(
    BodyId BodyId,
    string? MaterialName,
    MaterialId? ResolvedMaterialId,
    DisplayMaterial? CurrentDisplayMaterial = null);
