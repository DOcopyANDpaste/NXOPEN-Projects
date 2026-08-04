using Core.Assignment;
using Core.Bodies;
using Core.Common;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Core.RuleEngine;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment;

public class AssignmentPlanFinalizerTests
{
    private static AssignmentPlan MakePlan(params BodyAssignmentEvaluation[] evaluations) =>
        new("plan-1", new MaterialId("mat"), evaluations);

    private static BodyAssignmentEvaluation Allowed(string bodyId) =>
        new(new BodyId(bodyId), new[] { new RuleOutcome("r", RuleDecision.Allow, null, null) });

    private static BodyAssignmentEvaluation Blocked(string bodyId) =>
        new(new BodyId(bodyId), new[] { new RuleOutcome("r", RuleDecision.Block, "X", "x") });

    private static BodyAssignmentEvaluation NeedsConfirmation(string bodyId) =>
        new(new BodyId(bodyId), new[] { new RuleOutcome("r", RuleDecision.RequireConfirmation, "C", "c") });

    [Fact]
    public void Finalize_PartialApply_SkipsBlockedAndDeclinedButAppliesTheRest()
    {
        var plan = MakePlan(
            Allowed("clean"),
            Blocked("blocked"),
            NeedsConfirmation("declined"),
            NeedsConfirmation("confirmed"));

        var bodies = new[] { MakeBody("clean"), MakeBody("blocked"), MakeBody("declined"), MakeBody("confirmed") };
        var input = new MaterialAssignmentPlanningInput(MakeMaterial(), bodies, new Dictionary<BodyId, BodyMaterialAssignment>());
        var finalizer = new AssignmentPlanFinalizer(Array.Empty<IPostAssignmentEffectRule>());

        var confirmedIds = new HashSet<BodyId> { new BodyId("confirmed") };
        var executablePlan = finalizer.Finalize(plan, input, confirmedIds);

        Assert.Equal(new[] { "clean", "confirmed" }, executablePlan.Assignments.Select(a => a.BodyId.Value));
        Assert.Equal(new[] { "blocked" }, executablePlan.SkippedBlocked.Select(b => b.Value));
        Assert.Equal(new[] { "declined" }, executablePlan.SkippedDeclinedConfirmation.Select(b => b.Value));
    }

    [Fact]
    public void Finalize_PreservesPlanIdOnTheExecutablePlan()
    {
        var plan = MakePlan(Allowed("b1"));
        var input = new MaterialAssignmentPlanningInput(MakeMaterial(), new[] { MakeBody("b1") }, new Dictionary<BodyId, BodyMaterialAssignment>());
        var finalizer = new AssignmentPlanFinalizer(Array.Empty<IPostAssignmentEffectRule>());

        var executablePlan = finalizer.Finalize(plan, input, new HashSet<BodyId>());

        Assert.Equal(plan.PlanId, executablePlan.PlanId);
    }

    [Fact]
    public void Finalize_OnlyRunsEffectRulesForBodiesThatAreActuallyAssigned()
    {
        var plan = MakePlan(Allowed("clean"), Blocked("blocked"));
        var input = new MaterialAssignmentPlanningInput(
            MakeMaterial(), new[] { MakeBody("clean"), MakeBody("blocked") }, new Dictionary<BodyId, BodyMaterialAssignment>());

        var effectRule = new FakeEffectRule("effect", 100, ctx => Array.Empty<SideEffectInstruction>());
        var finalizer = new AssignmentPlanFinalizer(new[] { effectRule });

        finalizer.Finalize(plan, input, new HashSet<BodyId>());

        Assert.Equal(new[] { "clean" }, effectRule.InvokedForBodies.Select(b => b.Value));
    }

    [Fact]
    public void Finalize_AggregatesSideEffectsFromMultipleEffectRulesOntoTheSameAssignment()
    {
        var plan = MakePlan(Allowed("b1"));
        var input = new MaterialAssignmentPlanningInput(MakeMaterial(), new[] { MakeBody("b1") }, new Dictionary<BodyId, BodyMaterialAssignment>());

        var effectA = new FakeEffectRule("a", 100, ctx => new[]
        {
            new SideEffectInstruction("TYPE_A", ctx.TargetBody.Id, new Dictionary<string, object>()),
        });
        var effectB = new FakeEffectRule("b", 200, ctx => new[]
        {
            new SideEffectInstruction("TYPE_B", ctx.TargetBody.Id, new Dictionary<string, object>()),
        });
        var finalizer = new AssignmentPlanFinalizer(new IPostAssignmentEffectRule[] { effectB, effectA });

        var executablePlan = finalizer.Finalize(plan, input, new HashSet<BodyId>());

        var assignment = Assert.Single(executablePlan.Assignments);
        Assert.Equal(new[] { "TYPE_A", "TYPE_B" }, assignment.SideEffects.Select(e => e.InstructionType));
    }
}
