using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.SheetMetal.Common;

namespace BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

/// <summary>How the dialog judges a sheet metal body against the part's Sheet Metal Preferences, and what it tells the
/// user on open. The judging is <see cref="SheetMetalPreferenceCheck"/>'s, with one rule of this dialog's on top: a body
/// with no material is out of sync with preferences that name one, since the dialog offers to assign it.</summary>
internal static class SheetMetalPreferenceSync
{
    public sealed record BodyCheck(BodyInfo Body, SheetMetalPreferenceCheckResult Check);

    /// <param name="bodyThickness">In part units. Null when NX would not report one: there is then nothing to compare,
    /// so the thickness counts as matching, as it does in <c>SheetMetalRowGateRule</c>.</param>
    public static SheetMetalPreferenceCheckResult Evaluate(
        string? bodyMaterial, double? bodyThickness, SheetMetalPartPreference preference)
    {
        if (bodyMaterial is null && preference.IsMaterialTableEntry && preference.Row is { } row)
        {
            return new SheetMetalPreferenceCheckResult(
                SheetMetalPreferenceStatus.MaterialOutOfSync,
                $"Sheet Metal Preferences have material '{row.Name}', made of '{row.PhysicalMaterialName}', but this " +
                "body has no material.");
        }

        return SheetMetalPreferenceCheck.Evaluate(bodyMaterial, bodyThickness ?? preference.Thickness, preference);
    }

    /// <summary>Shown when the dialog opens on a part whose sheet metal bodies are not in sync with its preferences.
    /// A part-level problem (a material the standards file does not list) is said once; body-level ones are listed per
    /// body, in the check's own words.</summary>
    /// <param name="notInSync">Every checked body whose status is not <see cref="SheetMetalPreferenceStatus.InSync"/>.</param>
    /// <param name="autoSelected">Whether the one body was put in the body selection for the user.</param>
    public static string OpenMessage(
        SheetMetalPartPreference preference, IReadOnlyList<BodyCheck> notInSync, bool autoSelected)
    {
        var lines = new List<string>();

        if (notInSync.FirstOrDefault(c => c.Check.Status == SheetMetalPreferenceStatus.MaterialNotInTable) is { } notInTable)
            lines.Add(notInTable.Check.Message ?? string.Empty);

        var bodyLines = notInSync
            .Where(c => c.Check.Status is SheetMetalPreferenceStatus.MaterialOutOfSync or SheetMetalPreferenceStatus.ThicknessMismatch)
            .Select(c => $"  [{c.Body.Name}] {c.Check.Message}")
            .ToList();
        if (bodyLines.Count > 0)
            lines.Add(string.Join(Environment.NewLine, bodyLines));

        // Only a material mismatch is something this dialog fixes: assigning the preferences row syncs both.
        if (preference.Row is { } row && notInSync.Any(c => c.Check.Status == SheetMetalPreferenceStatus.MaterialOutOfSync))
        {
            lines.Add(autoSelected
                ? $"The body was added to the selection. Pick '{row.Name}' ({row.Describe()}) in the material list and Apply to sync them."
                : $"Select the bodies, pick '{row.Name}' ({row.Describe()}) in the material list and Apply to sync them.");
        }

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", lines);
    }
}
