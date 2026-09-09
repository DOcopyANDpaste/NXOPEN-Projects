using Core.Assignment;
using Core.Bodies;
using Core.Common;
using NxOpen.Foundation.Core.RuleEngine;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment;

public class MaterialAssignmentPlannerTests
{
    [Fact]
    public void Plan_ProducesOneEvaluationPerTargetBody()
    {
        var bodies = new[] { MakeBody("b1"), MakeBody("b2"), MakeBody("b3") };
        var planner = new MaterialAssignmentPlanner(new[] { FakeGateRule.AlwaysAllow("r1", 100) });

        var plan = planner.Plan(new MaterialAssignmentPlanningInput(
            MakeMaterial(), bodies, new Dictionary<BodyId, BodyMaterialAssignment>()));

        Assert.Equal(3, plan.BodyEvaluations.Count);
        Assert.Equal(bodies.Select(b => b.Id), plan.BodyEvaluations.Select(e => e.BodyId));
    }

    [Fact]
    public void Plan_RunsRulesInAscendingOrderRegardlessOfRegistrationOrder()
    {
        var invocationLog = new List<string>();
        var late = new FakeGateRule("late", 300, ctx => { invocationLog.Add("late"); return new RuleOutcome("late", RuleDecision.Allow, null, null); });
        var early = new FakeGateRule("early", 100, ctx => { invocationLog.Add("early"); return new RuleOutcome("early", RuleDecision.Allow, null, null); });
        var mid = new FakeGateRule("mid", 200, ctx => { invocationLog.Add("mid"); return new RuleOutcome("mid", RuleDecision.Allow, null, null); });

        // Registered out of order on purpose — the planner must sort by Order, not registration order.
        var planner = new MaterialAssignmentPlanner(new IMaterialAssignmentRule[] { late, early, mid });

        planner.Plan(new MaterialAssignmentPlanningInput(
            MakeMaterial(), new[] { MakeBody("b1") }, new Dictionary<BodyId, BodyMaterialAssignment>()));

        Assert.Equal(new[] { "early", "mid", "late" }, invocationLog);
    }

    [Fact]
    public void Plan_BlockShortCircuitsRemainingRules_ForThatBodyOnly()
    {
        var blockedBody = MakeBody("blocked");
        var cleanBody = MakeBody("clean");

        // Blocker only blocks the "blocked" body; both bodies still get evaluated independently.
        var conditionalBlocker = new FakeGateRule("blocker", 100, ctx =>
            ctx.TargetBody.Id.Value == "blocked"
                ? new RuleOutcome("blocker", RuleDecision.Block, "X", "x")
                : new RuleOutcome("blocker", RuleDecision.Allow, null, null));
        var tracker = FakeGateRule.AlwaysAllow("later", 200);
        var planner = new MaterialAssignmentPlanner(new IMaterialAssignmentRule[] { conditionalBlocker, tracker });

        var plan = planner.Plan(new MaterialAssignmentPlanningInput(
            MakeMaterial(), new[] { blockedBody, cleanBody }, new Dictionary<BodyId, BodyMaterialAssignment>()));

        var blockedEvaluation = plan.BodyEvaluations.Single(e => e.BodyId.Value == "blocked");
        var cleanEvaluation = plan.BodyEvaluations.Single(e => e.BodyId.Value == "clean");

        Assert.True(blockedEvaluation.IsBlocked);
        Assert.Single(blockedEvaluation.RuleOutcomes); // "later" never ran for the blocked body
        Assert.False(cleanEvaluation.IsBlocked);
        Assert.Equal(2, cleanEvaluation.RuleOutcomes.Count); // both rules ran for the clean body
        Assert.Contains("clean", tracker.InvokedForBodies.Select(b => b.Value));
    }

    [Fact]
    public void AssignmentPlan_RequiresAnyConfirmation_TrueWhenAnyBodyNeedsConfirmation()
    {
        var confirmRule = new TestFixtures.FakeGateRule("confirm", 100, ctx =>
            ctx.TargetBody.Id.Value == "needs-confirm"
                ? new RuleOutcome("confirm", RuleDecision.RequireConfirmation, "C", "c")
                : new RuleOutcome("confirm", RuleDecision.Allow, null, null));
        var planner = new MaterialAssignmentPlanner(new IMaterialAssignmentRule[] { confirmRule });

        var plan = planner.Plan(new MaterialAssignmentPlanningInput(
            MakeMaterial(),
            new[] { MakeBody("clean"), MakeBody("needs-confirm") },
            new Dictionary<BodyId, BodyMaterialAssignment>()));

        Assert.True(plan.RequiresAnyConfirmation);
    }
}
