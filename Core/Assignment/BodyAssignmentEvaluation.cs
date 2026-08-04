using Core.Common;
using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Assignment;

/// <summary>All rule outcomes for one body under one requested material assignment.</summary>
public sealed record BodyAssignmentEvaluation(
    BodyId BodyId,
    IReadOnlyList<RuleOutcome> RuleOutcomes)
{
    public bool IsBlocked => RuleOutcomes.Any(r => r.Decision == RuleDecision.Block);

    public bool RequiresConfirmation =>
        !IsBlocked && RuleOutcomes.Any(r => r.Decision == RuleDecision.RequireConfirmation);

    public IReadOnlyList<RuleOutcome> BlockingOutcomes =>
        RuleOutcomes.Where(r => r.Decision == RuleDecision.Block).ToList();

    public IReadOnlyList<RuleOutcome> ConfirmationOutcomes =>
        RuleOutcomes.Where(r => r.Decision == RuleDecision.RequireConfirmation).ToList();

    public IReadOnlyList<RuleOutcome> WarningOutcomes =>
        RuleOutcomes.Where(r => r.Decision == RuleDecision.Warn).ToList();
}
