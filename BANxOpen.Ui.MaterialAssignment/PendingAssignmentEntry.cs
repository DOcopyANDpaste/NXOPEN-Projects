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

/// <summary>One staged change to some bodies. Nothing is applied to the part until OK/Apply; Cancel simply discards it.
/// A body is claimed by at most one entry, and the latest action replaces an earlier claim, so the order entries are
/// applied in never matters. A body Apply could not change stays in its entry with
/// <see cref="PendingBodyStatus.Blocked"/> and the reason as its message.</summary>
public abstract record PendingEntry(IReadOnlyList<PendingBodyRow> Rows)
{
    /// <summary>How the entry is named to the user.</summary>
    public abstract string DisplayName { get; }

    /// <summary>The bodies whose rule asked for confirmation and the user said yes, so Apply does not ask again.</summary>
    public IReadOnlyCollection<BodyId> ConfirmedBodyIds { get; init; } = Array.Empty<BodyId>();

    public IReadOnlyList<BodyId> BodyIds => Rows.Select(r => r.Body.Id).ToList();
}

/// <summary>"Remove the material from these bodies". There is at most one, gathering every body whose removal is
/// pending.</summary>
public sealed record PendingRemovalEntry(IReadOnlyList<PendingBodyRow> Rows) : PendingEntry(Rows)
{
    public override string DisplayName => UiText.Labels.RemoveMaterialEntry;
}

/// <summary>"Assign this material to these bodies". Presentation-only, like <see cref="MaterialUsageRow"/> — it carries
/// the Core types an apply needs (<see cref="Input"/> and <see cref="Plan"/> go straight to
/// <c>IAssignmentPlanFinalizer.Finalize</c>) plus the per-body rows the pending tree renders.
///
/// Only bodies that will be assigned are staged; blocked and declined bodies are reported to the user when the
/// material is added and left out. Apply re-plans the entry against the part as it then stands, reusing the
/// confirmations in <see cref="PendingEntry.ConfirmedBodyIds"/>.</summary>
public sealed record PendingAssignmentEntry(
    Material Material,
    MaterialAssignmentPlanningInput Input,
    AssignmentPlan Plan,
    IReadOnlyList<PendingBodyRow> Rows) : PendingEntry(Rows)
{
    /// <summary>The sheet metal standards file row the user picked from the material tree, which the part's Sheet
    /// Metal Preferences are set to when the entry's sheet metal bodies are assigned. Null when a plain physical
    /// material was picked. Carried across re-plans.</summary>
    public SheetMetalMaterialRow? SheetMetalRow { get; init; }

    /// <summary>The material, with the picked row when there is one.</summary>
    public override string DisplayName =>
        SheetMetalRow is { } row ? UiText.Labels.MaterialWithRow(Material.Name, row.Name) : Material.Name;

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

    /// <summary>The outcomes' messages, as their rules worded them (each library's describer).</summary>
    private static string? Describe(IReadOnlyList<RuleOutcome> outcomes)
    {
        var messages = outcomes.Select(o => o.Message).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return messages.Count == 0 ? null : string.Join("; ", messages);
    }
}
