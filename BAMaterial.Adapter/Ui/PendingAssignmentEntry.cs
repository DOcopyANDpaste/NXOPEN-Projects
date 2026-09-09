using Core.Assignment;
using Core.Bodies;
using Core.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.RuleEngine;

namespace NxAdapters.Ui;

/// <summary>What the planner decided about one body in a staged assignment, flattened to the single value
/// the Status column shows.</summary>
public enum PendingBodyStatus
{
    Ok,
    NeedsConfirmation,
    Blocked,
}

public sealed record PendingBodyRow(BodyInfo Body, PendingBodyStatus Status, string? Message);

/// <summary>One staged "assign this material to these bodies" request, held between the user picking it off
/// the material tree and OK/Apply committing it. Presentation-only, like <see cref="MaterialUsageRow"/> —
/// it carries the Core types the commit needs (<see cref="Input"/> and <see cref="Plan"/> go straight to
/// <c>IAssignmentPlanFinalizer.Finalize</c>) plus the per-body rows the tree renders, so nothing has to be
/// re-planned at commit time.
///
/// The plan is computed once, at staging time. That is deliberate: the user sees the rule outcomes for the
/// state the part was in when they staged the entry. <c>OnRefreshClicked</c> re-plans everything pending so
/// a stale entry cannot be committed against a part that has moved on underneath it.</summary>
public sealed record PendingAssignmentEntry(
    Material Material,
    MaterialAssignmentPlanningInput Input,
    AssignmentPlan Plan,
    IReadOnlyList<PendingBodyRow> Rows)
{
    public static PendingAssignmentEntry Create(MaterialAssignmentPlanningInput input, AssignmentPlan plan)
    {
        var evaluationsByBody = plan.BodyEvaluations.ToDictionary(e => e.BodyId);

        var rows = input.TargetBodies
            .Select(body =>
            {
                // A body with no evaluation had no rule to say otherwise, so it simply applies.
                if (!evaluationsByBody.TryGetValue(body.Id, out var evaluation))
                    return new PendingBodyRow(body, PendingBodyStatus.Ok, null);

                if (evaluation.IsBlocked)
                    return new PendingBodyRow(body, PendingBodyStatus.Blocked, Describe(evaluation.BlockingOutcomes));

                if (evaluation.RequiresConfirmation)
                    return new PendingBodyRow(body, PendingBodyStatus.NeedsConfirmation, Describe(evaluation.ConfirmationOutcomes));

                // Warnings don't gate anything, but they are the only thing the user would otherwise never
                // see, so they ride along on the OK row.
                return new PendingBodyRow(body, PendingBodyStatus.Ok, Describe(evaluation.WarningOutcomes));
            })
            .ToList();

        return new PendingAssignmentEntry(input.RequestedMaterial, input, plan, rows);
    }

    public IReadOnlyList<BodyId> BodyIds => Rows.Select(r => r.Body.Id).ToList();

    /// <summary>False when every body in the entry is blocked — committing it would be a no-op, so the
    /// presenter reports that instead of silently applying nothing.</summary>
    public bool HasAnyApplicableBody => Rows.Any(r => r.Status != PendingBodyStatus.Blocked);

    public IReadOnlyList<BodyId> BodyIdsNeedingConfirmation =>
        Rows.Where(r => r.Status == PendingBodyStatus.NeedsConfirmation).Select(r => r.Body.Id).ToList();

    private static string? Describe(IReadOnlyList<RuleOutcome> outcomes)
    {
        var messages = outcomes.Select(o => o.Message).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return messages.Count == 0 ? null : string.Join("; ", messages);
    }
}
