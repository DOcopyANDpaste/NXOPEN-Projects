using Core.Assignment;
using Core.Bodies;
using Core.Common;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.RuleEngine;

namespace Core.Tests.Assignment;

internal static class TestFixtures
{
    public static readonly MaterialCategory Category = new("cat", "Category", new[] { "Category" });

    public static Material MakeMaterial(string name = "Steel", MaterialId? id = null, IReadOnlyList<MaterialPropertyValue>? properties = null) =>
        new(id ?? new MaterialId(name), new MaterialLibraryId("lib"), name, Category, properties ?? Array.Empty<MaterialPropertyValue>());

    public static BodyInfo MakeBody(string id, BodyKind kind = BodyKind.Solid) =>
        new(new BodyId(id), id, kind, Volume: 1.0, Attributes: new Dictionary<string, string>());

    /// <summary>A gate rule with fully controllable behavior and an invocation log, for testing the
    /// planner's ordering/short-circuit logic independent of any real business rule.</summary>
    public sealed class FakeGateRule : IMaterialAssignmentRule
    {
        private readonly Func<MaterialAssignmentRuleContext, RuleOutcome> _evaluate;

        public FakeGateRule(string ruleId, int order, Func<MaterialAssignmentRuleContext, RuleOutcome> evaluate)
        {
            RuleId = ruleId;
            Order = order;
            _evaluate = evaluate;
        }

        public string RuleId { get; }

        public int Order { get; }

        public List<BodyId> InvokedForBodies { get; } = new();

        public RuleOutcome Evaluate(MaterialAssignmentRuleContext context)
        {
            InvokedForBodies.Add(context.TargetBody.Id);
            return _evaluate(context);
        }

        public static FakeGateRule AlwaysAllow(string ruleId, int order) =>
            new(ruleId, order, _ => new RuleOutcome(ruleId, RuleDecision.Allow, null, null));

        public static FakeGateRule AlwaysBlock(string ruleId, int order) =>
            new(ruleId, order, _ => new RuleOutcome(ruleId, RuleDecision.Block, "BLOCKED", "blocked"));

        public static FakeGateRule AlwaysRequireConfirmation(string ruleId, int order) =>
            new(ruleId, order, _ => new RuleOutcome(ruleId, RuleDecision.RequireConfirmation, "CONFIRM", "confirm?"));
    }

    /// <summary>An effect rule with controllable behavior and an invocation log.</summary>
    public sealed class FakeEffectRule : IPostAssignmentEffectRule
    {
        private readonly Func<MaterialAssignmentRuleContext, IReadOnlyList<SideEffectInstruction>> _generate;

        public FakeEffectRule(string ruleId, int order, Func<MaterialAssignmentRuleContext, IReadOnlyList<SideEffectInstruction>> generate)
        {
            RuleId = ruleId;
            Order = order;
            _generate = generate;
        }

        public string RuleId { get; }

        public int Order { get; }

        public List<BodyId> InvokedForBodies { get; } = new();

        public IReadOnlyList<SideEffectInstruction> GenerateEffects(MaterialAssignmentRuleContext context)
        {
            InvokedForBodies.Add(context.TargetBody.Id);
            return _generate(context);
        }
    }
}
