using Core.Bodies;
using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Assignment.Rules;

/// <summary>Example restriction rule: sheet bodies cannot take a casting-category material. Stand-in
/// for the "assignment restrictions" business logic the user flagged as still evolving — replace or
/// extend the condition as real restriction rules are defined, without touching the planner.</summary>
public sealed class BlockRestrictedBodyTypeRule : IMaterialAssignmentRule
{
    public string RuleId => "BLOCK_BODY_TYPE_RESTRICTION";

    public int Order => 100;

    public RuleOutcome Evaluate(MaterialAssignmentRuleContext context)
    {
        var restricted = context.TargetBody.Kind == BodyKind.Sheet
            && string.Equals(context.RequestedMaterial.Category.Key, "casting", StringComparison.OrdinalIgnoreCase);

        return restricted
            ? new RuleOutcome(
                RuleId,
                RuleDecision.Block,
                "BODY_TYPE_RESTRICTED",
                $"'{context.RequestedMaterial.Name}' cannot be assigned to sheet bodies.")
            : new RuleOutcome(RuleId, RuleDecision.Allow, null, null);
    }
}
