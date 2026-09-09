using BAMaterial.Core.Bodies;
using BANxOpen.Foundation.Core.RuleEngine;
using BANxOpen.Foundation.Contracts.Bodies;

namespace BAMaterial.Core.Assignment.Rules;

/// <summary>Sheet-metal-specific material libraries are exclusive to sheet bodies: a sheet-metal-library
/// material can only go on a sheet body, and a sheet body can only take materials from a sheet-metal
/// library. Any mismatch between library and body kind is blocked as "not available".</summary>
public sealed class BlockRestrictedBodyTypeRule : IMaterialAssignmentRule
{
    public string RuleId => "BLOCK_BODY_TYPE_RESTRICTION";

    public int Order => 100;

    public RuleOutcome Evaluate(MaterialAssignmentRuleContext context)
    {
        // Previous category-based restriction — kept for reference, may be needed again later.
        // var restricted = context.TargetBody.Kind == BodyKind.Sheet
        //     && string.Equals(context.RequestedMaterial.Category.Key, "casting", StringComparison.OrdinalIgnoreCase);

        var isSheetMetalLibrary = IsSheetMetalLibrary(context.RequestedMaterial.LibraryId.Value);
        var isSheetBody = context.TargetBody.Kind == BodyKind.Sheet;
        var restricted = isSheetMetalLibrary != isSheetBody;

        return restricted
            ? new RuleOutcome(
                RuleId,
                RuleDecision.Block,
                "BODY_TYPE_RESTRICTED",
                $"'{context.RequestedMaterial.Name}' is not available for this body.")
            : new RuleOutcome(RuleId, RuleDecision.Allow, null, null);
    }

    private static bool IsSheetMetalLibrary(string libraryName) =>
        libraryName.Replace(" ", "").IndexOf("sheetmetal", StringComparison.OrdinalIgnoreCase) >= 0;
}
