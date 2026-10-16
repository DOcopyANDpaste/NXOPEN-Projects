using BANxOpen.SheetMetal.Common;

namespace BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

/// <summary>What the Current Assignments tab's SheetMetal Grade column shows: the name of the part's Sheet Metal
/// Preferences row, which governs every sheet metal body in the part, marked with how the body stands against it.
/// The words are in <see cref="UiText.Status"/>.</summary>
internal static class SheetMetalGradeText
{
    /// <param name="check">The body's check (see <see cref="SheetMetalPreferenceSync.Evaluate"/>); null for a body that
    /// is not sheet metal, which shows nothing.</param>
    public static string ForBody(SheetMetalPreferenceCheckResult? check, SheetMetalPartPreference? preference)
    {
        if (check is null || preference is null)
            return string.Empty;

        if (check.Status == SheetMetalPreferenceStatus.MaterialNotSet)
            return UiText.Status.GradeNotSet;

        var name = preference.Row?.Name ?? preference.MaterialName ?? string.Empty;
        return check.Status switch
        {
            SheetMetalPreferenceStatus.MaterialOutOfSync => UiText.Status.GradeMaterialMismatch(name),
            SheetMetalPreferenceStatus.ThicknessMismatch => UiText.Status.GradeThicknessMismatch(name),
            SheetMetalPreferenceStatus.MaterialNotInTable => UiText.Status.GradeNotInTable(name),
            _ => name,
        };
    }

    /// <summary>The row name on the material row the preferences row is made of; empty on any other.</summary>
    public static string ForMaterial(string materialLabel, SheetMetalPartPreference? preference) =>
        preference?.Row is { } row && SheetMetalBodies.SameName(materialLabel, row.PhysicalMaterialName)
            ? row.Name
            : string.Empty;
}
