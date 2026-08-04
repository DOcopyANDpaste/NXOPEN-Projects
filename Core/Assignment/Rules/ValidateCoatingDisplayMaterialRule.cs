using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Assignment.Rules;

/// <summary>Validates the coating/studio display-material data on the requested material, and, when the
/// target body already has a material assigned, cross-checks that against the body's actual current
/// display-material state:
/// <list type="bullet">
/// <item>Name present, color present and valid, and (if the body already has a material assigned) no
/// conflict with the body's current display material → Allow.</item>
/// <item>Name present, color missing or malformed → Block (bad library data; not something the end user
/// can fix, so the message directs them to the material library administrator).</item>
/// <item>Name missing, regardless of color → Warn (non-blocking; the display material simply won't be
/// synced for this assignment). This applies even when neither property is present at all — deliberately
/// not special-cased, per direct confirmation with the user.</item>
/// <item>Body already has a material assigned but no display material currently associated with it →
/// Allow (this is the expected first-sync case, not a problem to flag; the sync effect rule will apply
/// the display material automatically). A reason code and message are still attached for reference/
/// logging even though the decision itself doesn't require the user's attention.</item>
/// <item>Body already has a material assigned, and its current display material's name and/or color
/// differs from what the requested material's library data specifies → RequireConfirmation, so the user
/// can choose to keep the body's current display material (decline) or update it to match the library
/// (confirm) — per direct confirmation with the user, this is a decision, not just information.</item>
/// </list></summary>
public sealed class ValidateCoatingDisplayMaterialRule : IMaterialAssignmentRule
{
    public string RuleId => "VALIDATE_COATING_DISPLAY_MATERIAL";

    public int Order => 300;

    public RuleOutcome Evaluate(MaterialAssignmentRuleContext context)
    {
        var material = context.RequestedMaterial;
        var displayMaterialName = CoatingPropertyReader.GetDisplayMaterialName(material);

        if (displayMaterialName is null)
        {
            return new RuleOutcome(
                RuleId,
                RuleDecision.Warn,
                "COATING_NAME_MISSING",
                $"Material '{material.Name}' has no {CoatingPropertyReader.MaterialNamePropertyName} defined — its display material will not be synced.");
        }

        if (!CoatingPropertyReader.TryGetRgb(material, out var r, out var g, out var b))
        {
            return new RuleOutcome(
                RuleId,
                RuleDecision.Block,
                "COATING_COLOR_INVALID",
                $"Material '{material.Name}' has an invalid or missing {CoatingPropertyReader.ColorPropertyName}. Contact your material library administrator.");
        }

        if (context.CurrentAssignment is not null)
        {
            var currentDisplayMaterial = context.CurrentAssignment.CurrentDisplayMaterial;

            if (currentDisplayMaterial is null)
            {
                return new RuleOutcome(
                    RuleId,
                    RuleDecision.Allow,
                    "COATING_NOT_APPLIED",
                    $"Body already has material '{context.CurrentAssignment.MaterialName}' assigned but no display material is currently associated with it — it will be assigned automatically.");
            }

            var nameMatches = string.Equals(displayMaterialName, currentDisplayMaterial.Name, StringComparison.OrdinalIgnoreCase);
            var colorMatches = CoatingPropertyReader.ColorsApproximatelyEqual(r, g, b, currentDisplayMaterial.Rgb);

            if (!nameMatches || !colorMatches)
            {
                var libraryRgb = $"{Math.Round(r * 255)},{Math.Round(g * 255)},{Math.Round(b * 255)}";
                var currentRgb = $"{currentDisplayMaterial.Rgb.R},{currentDisplayMaterial.Rgb.G},{currentDisplayMaterial.Rgb.B}";

                return new RuleOutcome(
                    RuleId,
                    RuleDecision.RequireConfirmation,
                    "COATING_DISPLAY_MISMATCH",
                    $"Library: '{displayMaterialName}' ({libraryRgb}) vs current: '{currentDisplayMaterial.Name}' ({currentRgb}).\nConfirm to update to the library value, or leave unconfirmed to keep the current one.");
            }
        }

        return new RuleOutcome(RuleId, RuleDecision.Allow, null, null);
    }
}
