using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.SheetMetal;
using BANxOpen.SheetMetal.Common;

namespace BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

/// <summary>How the dialog judges a sheet metal body against the part's Sheet Metal Preferences, and what it tells the
/// user on open. The judging and its wording are <see cref="SheetMetalPreferenceCheck"/>'s, with one rule of this
/// dialog's on top: a body with no material is out of sync with preferences that name one, since the dialog offers to
/// assign it — worded by <see cref="SheetMetalText.PreferenceBodyHasNoMaterial"/>.</summary>
internal static class SheetMetalPreferenceSync
{
    public sealed record BodyCheck(BodyInfo Body, SheetMetalPreferenceCheckResult Check);

    /// <param name="bodyThickness">In part units. Null when NX would not report one: there is then nothing to compare,
    /// so the thickness counts as matching, as it does in <c>SheetMetalRowGateRule</c>.</param>
    /// <param name="text">Words the result; <see cref="SheetMetalText.Default"/> when null.</param>
    public static SheetMetalPreferenceCheckResult Evaluate(
        string? bodyMaterial, double? bodyThickness, SheetMetalPartPreference preference, SheetMetalText? text = null)
    {
        text ??= SheetMetalText.Default;
        if (bodyMaterial is null && preference.IsMaterialTableEntry && preference.Row is { } row)
        {
            return new SheetMetalPreferenceCheckResult(
                SheetMetalPreferenceStatus.MaterialOutOfSync, text.PreferenceBodyHasNoMaterial(row.Name, row.PhysicalMaterialName));
        }

        return SheetMetalPreferenceCheck.Evaluate(bodyMaterial, bodyThickness ?? preference.Thickness, preference, text);
    }

    /// <summary>Shown when the dialog opens on a part whose sheet metal bodies are not in sync with its preferences.
    /// A part-level problem (a material the standards file does not list) is said once; body-level ones are listed per
    /// body, in the check's own words.</summary>
    /// <param name="notInSync">Every checked body whose status is not <see cref="SheetMetalPreferenceStatus.InSync"/>.</param>
    /// <param name="autoSelected">Whether the one body was put in the body selection for the user.</param>
    public static string OpenMessage(
        SheetMetalPartPreference preference, IReadOnlyList<BodyCheck> notInSync, bool autoSelected)
    {
        var sections = new List<string>();

        if (notInSync.FirstOrDefault(c => c.Check.Status == SheetMetalPreferenceStatus.MaterialNotInTable) is { } notInTable)
            sections.Add(notInTable.Check.Message ?? string.Empty);

        var bodyLines = notInSync
            .Where(c => c.Check.Status is SheetMetalPreferenceStatus.MaterialOutOfSync or SheetMetalPreferenceStatus.ThicknessMismatch)
            .Select(c => UiText.Reasons.BodyLine(c.Body.Name, c.Check.Message ?? string.Empty))
            .ToList();
        if (bodyLines.Count > 0)
            sections.Add(string.Join(Environment.NewLine, bodyLines));

        // Only a material mismatch is something this dialog fixes: assigning the preferences row syncs both.
        if (preference.Row is { } row && notInSync.Any(c => c.Check.Status == SheetMetalPreferenceStatus.MaterialOutOfSync))
            sections.Add(UiText.SheetMetal.SyncHint(row.Name, row.Describe(), autoSelected));

        return UiText.Info.JoinSections(sections);
    }
}
