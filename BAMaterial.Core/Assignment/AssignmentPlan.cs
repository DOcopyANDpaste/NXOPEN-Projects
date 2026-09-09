using NxOpen.Foundation.Contracts.Common;

namespace Core.Assignment;

/// <summary>The pure "what would happen" result of running gate rules for one Apply request, before any
/// user confirmation has been collected and before any side effects have been computed.</summary>
public sealed record AssignmentPlan(
    string PlanId,
    MaterialId RequestedMaterialId,
    IReadOnlyList<BodyAssignmentEvaluation> BodyEvaluations)
{
    public bool RequiresAnyConfirmation => BodyEvaluations.Any(b => b.RequiresConfirmation);
}
