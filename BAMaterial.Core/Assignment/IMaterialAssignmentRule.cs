using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Assignment;

/// <summary>A gate rule: decides whether an assignment is allowed, blocked, or needs user confirmation.
/// Implement this to add a new business rule (e.g. a body-type/material restriction) without touching
/// the planner or any other rule — that's the whole point of the pipeline. Thin specialization of the
/// shared NxOpen.Foundation.Core.RuleEngine.IGateRule shape.</summary>
public interface IMaterialAssignmentRule : IGateRule<MaterialAssignmentRuleContext, RuleOutcome>
{
}
