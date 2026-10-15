using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Materials.Contracts;

namespace BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

/// <summary>Body and name checks shared by the presenter and the sheet metal helpers.</summary>
internal static class SheetMetalBodies
{
    /// <summary>Sheet metal bodies are solids in NX, so the solid-body checks include them. Their own kind exists only
    /// so the body-type rule can pair them with sheet metal libraries.</summary>
    public static bool IsSolid(BodyInfo body) => body.Kind is BodyKind.Solid or BodyKind.SheetMetal;

    /// <summary>Material, row and file names compare ignoring case, like everything read from the libraries and the
    /// standards file.</summary>
    public static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The physical material currently on <paramref name="bodyId"/>, or null when it has none.</summary>
    public static string? MaterialOf(BodyId bodyId, IReadOnlyDictionary<BodyId, BodyMaterialAssignment> assignments) =>
        assignments.TryGetValue(bodyId, out var assignment) ? assignment.MaterialName : null;
}
