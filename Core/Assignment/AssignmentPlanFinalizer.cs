using Core.Common;

namespace Core.Assignment;

/// <summary>Turns an <see cref="AssignmentPlan"/> plus the user's confirm/decline answers into the
/// single atomic <see cref="ExecutablePlan"/> for one Apply click. Partial-apply semantics: blocked and
/// declined bodies are skipped and reported, clean/confirmed bodies are still assigned as part of the
/// same plan (still one undo mark for the adapter to wrap).</summary>
public sealed class AssignmentPlanFinalizer : IAssignmentPlanFinalizer
{
    private readonly IReadOnlyList<IPostAssignmentEffectRule> _effectRules;

    public AssignmentPlanFinalizer(IEnumerable<IPostAssignmentEffectRule> effectRules) =>
        _effectRules = effectRules.OrderBy(r => r.Order).ToList();

    public ExecutablePlan Finalize(
        AssignmentPlan plan,
        MaterialAssignmentPlanningInput input,
        HashSet<BodyId> confirmedBodyIds)
    {
        var bodiesById = input.TargetBodies.ToDictionary(b => b.Id);
        var assignments = new List<ExecutableAssignment>();
        var skippedBlocked = new List<BodyId>();
        var skippedDeclined = new List<BodyId>();

        foreach (var evaluation in plan.BodyEvaluations)
        {
            if (evaluation.IsBlocked)
            {
                skippedBlocked.Add(evaluation.BodyId);
                continue;
            }

            if (evaluation.RequiresConfirmation && !confirmedBodyIds.Contains(evaluation.BodyId))
            {
                skippedDeclined.Add(evaluation.BodyId);
                continue;
            }

            input.CurrentAssignments.TryGetValue(evaluation.BodyId, out var currentAssignment);
            var context = new MaterialAssignmentRuleContext(
                input.RequestedMaterial, bodiesById[evaluation.BodyId], currentAssignment, input.TargetBodies);

            var effects = _effectRules.SelectMany(rule => rule.GenerateEffects(context)).ToList();
            assignments.Add(new ExecutableAssignment(evaluation.BodyId, plan.RequestedMaterialId, effects));
        }

        return new ExecutablePlan(plan.PlanId, assignments, skippedBlocked, skippedDeclined);
    }
}
