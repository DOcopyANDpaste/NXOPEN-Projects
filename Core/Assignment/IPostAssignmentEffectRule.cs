using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Assignment;

/// <summary>An effect rule: given an assignment that IS going to happen, produces side-effect
/// instructions (e.g. "sync this physical property") for the adapter layer to execute. Core never
/// performs the effect itself — it only describes what should happen, as plain data. Thin
/// specialization of the shared NxOpen.Foundation.Core.RuleEngine.IEffectRule shape.</summary>
public interface IPostAssignmentEffectRule : IEffectRule<MaterialAssignmentRuleContext, SideEffectInstruction>
{
}
