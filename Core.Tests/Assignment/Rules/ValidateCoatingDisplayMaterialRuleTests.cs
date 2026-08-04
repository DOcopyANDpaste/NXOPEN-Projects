using Core.Assignment;
using Core.Assignment.Rules;
using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.RuleEngine;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment.Rules;

public class ValidateCoatingDisplayMaterialRuleTests
{
    private const string MaterialNamePropertyName = "CoatingStudioMaterialName";
    private const string ColorPropertyName = "CoatingVisualizationColor";

    private readonly ValidateCoatingDisplayMaterialRule _rule = new();

    private static MaterialPropertyValue NameProperty(string value) =>
        new("pr-name", MaterialNamePropertyName, null, value, null);

    private static MaterialPropertyValue ColorProperty(string value) =>
        new("pr-color", ColorPropertyName, null, value, null);

    private RuleOutcome Evaluate(params MaterialPropertyValue[] properties) =>
        Evaluate(null, properties);

    private RuleOutcome Evaluate(BodyMaterialAssignment? currentAssignment, params MaterialPropertyValue[] properties)
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Coated Steel", properties: properties), MakeBody("b1"), currentAssignment, Array.Empty<BodyInfo>());
        return _rule.Evaluate(context);
    }

    [Fact]
    public void Evaluate_NameAndValidColor_Allows()
    {
        var outcome = Evaluate(NameProperty("Chrome"), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NamePresent_ColorMissing_Blocks()
    {
        var outcome = Evaluate(NameProperty("Chrome"));

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NamePresent_ColorWrongComponentCount_Blocks()
    {
        var outcome = Evaluate(NameProperty("Chrome"), ColorProperty("255,128"));

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NamePresent_ColorNonNumeric_Blocks()
    {
        var outcome = Evaluate(NameProperty("Chrome"), ColorProperty("red,green,blue"));

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NamePresent_ColorOutOfRange_Blocks()
    {
        var outcome = Evaluate(NameProperty("Chrome"), ColorProperty("300,0,0"));

        Assert.Equal(RuleDecision.Block, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NameMissing_ColorPresent_Warns()
    {
        var outcome = Evaluate(ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.Warn, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NeitherPropertyPresent_StillWarns()
    {
        var outcome = Evaluate();

        Assert.Equal(RuleDecision.Warn, outcome.Decision);
    }

    [Fact]
    public void Evaluate_NameBlank_TreatedAsMissing_Warns()
    {
        var outcome = Evaluate(NameProperty("   "), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.Warn, outcome.Decision);
    }

    private static DisplayMaterial MakeDisplayMaterial(string name, byte r, byte g, byte b) =>
        new(new MaterialId("dm-1"), name, (r, g, b));

    [Fact]
    public void Evaluate_ExistingAssignment_NoCurrentDisplayMaterial_AllowsWithReferenceInfo()
    {
        var current = new BodyMaterialAssignment(new BodyId("b1"), "Coated Steel", null, null);

        var outcome = Evaluate(current, NameProperty("Chrome"), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
        Assert.Equal("COATING_NOT_APPLIED", outcome.ReasonCode);
    }

    [Fact]
    public void Evaluate_ExistingAssignment_NameDiffers_ColorMatches_RequiresConfirmation()
    {
        var current = new BodyMaterialAssignment(
            new BodyId("b1"), "Coated Steel", null, MakeDisplayMaterial("Brushed Nickel", 255, 128, 0));

        var outcome = Evaluate(current, NameProperty("Chrome"), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.RequireConfirmation, outcome.Decision);
        Assert.Equal("COATING_DISPLAY_MISMATCH", outcome.ReasonCode);
    }

    [Fact]
    public void Evaluate_ExistingAssignment_NameMatches_ColorDiffers_RequiresConfirmation()
    {
        var current = new BodyMaterialAssignment(
            new BodyId("b1"), "Coated Steel", null, MakeDisplayMaterial("Chrome", 0, 0, 0));

        var outcome = Evaluate(current, NameProperty("Chrome"), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.RequireConfirmation, outcome.Decision);
        Assert.Equal("COATING_DISPLAY_MISMATCH", outcome.ReasonCode);
    }

    [Fact]
    public void Evaluate_ExistingAssignment_ColorWithinTolerance_Allows()
    {
        // One 0-255 step off in the red channel (255 vs 254) — within the accepted rounding tolerance.
        var current = new BodyMaterialAssignment(
            new BodyId("b1"), "Coated Steel", null, MakeDisplayMaterial("Chrome", 254, 128, 0));

        var outcome = Evaluate(current, NameProperty("Chrome"), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }

    [Fact]
    public void Evaluate_ExistingAssignment_NameAndColorMatch_Allows()
    {
        var current = new BodyMaterialAssignment(
            new BodyId("b1"), "Coated Steel", null, MakeDisplayMaterial("chrome", 255, 128, 0));

        var outcome = Evaluate(current, NameProperty("Chrome"), ColorProperty("255,128,0"));

        Assert.Equal(RuleDecision.Allow, outcome.Decision);
    }
}
