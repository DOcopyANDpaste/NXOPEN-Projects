using Core.Assignment;
using Core.Assignment.Rules;
using Core.Bodies;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.RuleEngine;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment.Rules;

public class BlockRestrictedBodyTypeRuleTests
{
    private static readonly MaterialCategory CastingCategory = new("casting", "Casting", new[] { "Casting" });

    private readonly BlockRestrictedBodyTypeRule _rule = new();

    [Fact]
    public void Evaluate_BlocksCastingMaterialOnSheetBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Cast Iron") with { Category = CastingCategory },
            MakeBody("sheet1", BodyKind.Sheet),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_AllowsCastingMaterialOnSolidBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Cast Iron") with { Category = CastingCategory },
            MakeBody("solid1", BodyKind.Solid),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }

    [Fact]
    public void Evaluate_AllowsNonCastingMaterialOnSheetBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel"),
            MakeBody("sheet1", BodyKind.Sheet),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }
}
