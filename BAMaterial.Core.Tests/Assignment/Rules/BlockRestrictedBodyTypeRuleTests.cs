using Core.Assignment;
using Core.Assignment.Rules;
using Core.Bodies;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.RuleEngine;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment.Rules;

public class BlockRestrictedBodyTypeRuleTests
{
    private readonly BlockRestrictedBodyTypeRule _rule = new();

    private static Material MakeSheetMetalLibraryMaterial(string name = "Sheet Steel") =>
        MakeMaterial(name) with { LibraryId = new MaterialLibraryId("Sheet Metal Materials") };

    [Fact]
    public void Evaluate_AllowsSheetMetalLibraryMaterialOnSheetBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeSheetMetalLibraryMaterial(),
            MakeBody("sheet1", BodyKind.Sheet),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }

    [Fact]
    public void Evaluate_BlocksSheetMetalLibraryMaterialOnSolidBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeSheetMetalLibraryMaterial(),
            MakeBody("solid1", BodyKind.Solid),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_BlocksNonSheetMetalLibraryMaterialOnSheetBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel"),
            MakeBody("sheet1", BodyKind.Sheet),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_AllowsNonSheetMetalLibraryMaterialOnSolidBody()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel"),
            MakeBody("solid1", BodyKind.Solid),
            CurrentAssignment: null,
            AllTargetBodiesInBatch: Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }
}