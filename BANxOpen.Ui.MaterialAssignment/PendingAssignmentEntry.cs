using BANxOpen.Materials.Core.Assignment;
using BANxOpen.Materials.Core.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Materials.Contracts;
using BANxOpen.Foundation.Core.RuleEngine;
using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.SheetMetal.Materials;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>What the planner decided about one body in a pending assignment, flattened to the single value
/// the Status column shows.</summary>
public enum PendingBodyStatus
{
    Ok,
    NeedsConfirmation,
    Blocked,
}

public sealed record PendingBodyRow(BodyInfo Body, PendingBodyStatus Status, string? Message);

/// <summary>One previewed "assign this material to these bodies" request: already applied to the part on top of
/// the dialog's preview undo mark, and kept or reverted as a whole by OK/Apply or Cancel. Presentation-only, like
/// <see cref="MaterialUsageRow"/> — it carries the Core types a re-apply needs (<see cref="Input"/> and
/// <see cref="Plan"/> go straight to <c>IAssignmentPlanFinalizer.Finalize</c>) plus the per-body rows the pending
/// tree renders.
///
/// Only bodies that were actually applied are kept in an entry; blocked and declined bodies are reported to the user
/// when the material is assigned and left out. When the preview is rebuilt (a pending body removed or reclaimed), the
/// entry is re-planned and re-applied with the confirmations in <see cref="ConfirmedBodyIds"/>.</summary>
public sealed record PendingAssignmentEntry(
    Material Material,
    MaterialAssignmentPlanningInput Input,
    AssignmentPlan Plan,
    IReadOnlyList<PendingBodyRow> Rows)
{
    /// <summary>The sheet metal standards file row the user picked from the material tree, which the part's Sheet
    /// Metal Preferences are set to when the entry's sheet metal bodies are assigned. Null when a plain physical
    /// material was picked. Carried across re-plans.</summary>
    public SheetMetalMaterialRow? SheetMetalRow { get; init; }

    /// <summary>The bodies whose rule asked for confirmation and the user said yes, so a rebuilt preview does not ask
    /// again. Carried across re-plans.</summary>
    public IReadOnlyCollection<BodyId> ConfirmedBodyIds { get; init; } = Array.Empty<BodyId>();

    /// <summary>How the entry is named to the user: the material, with the picked row when there is one.</summary>
    public string DisplayName => SheetMetalRow is { } row ? $"{Material.Name} [{row.Name}]" : Material.Name;

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
