using Core.Assignment;
using Core.Assignment.Rules;
using Core.Bodies;
using NxOpen.Foundation.Contracts.Materials;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment.Rules;

public class SyncPhysicalPropertiesEffectRuleTests
{
    private readonly SyncPhysicalPropertiesEffectRule _rule = new();

    [Fact]
    public void GenerateEffects_EmitsOneInstructionPerNumericProperty()
    {
        var properties = new[]
        {
            new MaterialPropertyValue("pr1", "Density", "rho", "7.872", "g/cm^3"),
            new MaterialPropertyValue("pr2", "Yield Strength", null, "250", "MPa"),
        };
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel", properties: properties), MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        Assert.Equal(2, effects.Count);
        Assert.All(effects, e => Assert.Equal("SYNC_PHYSICAL_PROPERTY", e.InstructionType));
        Assert.All(effects, e => Assert.Equal("b1", e.BodyId.Value));

        // Data values are `object` now (see SideEffectInstruction.Data) — Equals(), not `==`, since `==`
        // on an object-typed operand is reference equality, not string value equality.
        var density = effects.Single(e => Equals(e.Data["PropertyName"], "Density"));
        Assert.Equal("7.872", density.Data["RawValue"]);
        Assert.Equal("g/cm^3", density.Data["Unit"]);
    }

    [Fact]
    public void GenerateEffects_SkipsPropertiesWithNoNumericValue()
    {
        var properties = new[]
        {
            new MaterialPropertyValue("pr1", "Density", null, "7.872", "g/cm^3"),
            new MaterialPropertyValue("pr2", "Finish", null, "Matte", null),
        };
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Steel", properties: properties), MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        var effect = Assert.Single(effects);
        Assert.Equal("Density", effect.Data["PropertyName"]);
    }

    [Fact]
    public void GenerateEffects_ReturnsEmptyWhenMaterialHasNoNumericProperties()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Unobtainium"), MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        Assert.Empty(effects);
    }
}
