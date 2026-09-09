using Core.Assignment;
using Core.Assignment.Rules;
using Core.Bodies;
using Core.Common;
using NxOpen.Foundation.Core.RuleEngine;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment.Rules;

public class RequireConfirmationOnReassignmentRuleTests
{
    private readonly RequireConfirmationOnReassignmentRule _rule = new();

    [Fact]
    public void Evaluate_RequiresConfirmationWhenBodyHasADifferentMaterialAssigned()
    {
        var current = new BodyMaterialAssignment(new BodyId("b1"), "Aluminum", null);
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel"), MakeBody("b1"), current, Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.RequireConfirmation, outcome.Decision);
    }

    [Fact]
    public void Evaluate_AllowsWhenBodyHasNoCurrentAssignment()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel"), MakeBody("b1"), CurrentAssignment: null, Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }

    [Fact]
    public void Evaluate_AllowsWhenReassigningTheSameMaterialNameCaseInsensitively()
    {
        var current = new BodyMaterialAssignment(new BodyId("b1"), "steel", null);
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel"), MakeBody("b1"), current, Array.Empty<BodyInfo>());

        var outcome = _rule.Evaluate(context);

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }
}
