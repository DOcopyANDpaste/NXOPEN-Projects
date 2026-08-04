using Core.Assignment;
using Core.Assignment.Rules;
using Core.Bodies;
using NxOpen.Foundation.Contracts.Materials;
using static Core.Tests.Assignment.TestFixtures;

namespace Core.Tests.Assignment.Rules;

public class SyncCoatingDisplayMaterialEffectRuleTests
{
    private const string MaterialNamePropertyName = "CoatingStudioMaterialName";
    private const string ColorPropertyName = "CoatingVisualizationColor";

    private readonly SyncCoatingDisplayMaterialEffectRule _rule = new();

    private static MaterialPropertyValue NameProperty(string value) =>
        new("pr-name", MaterialNamePropertyName, null, value, null);

    private static MaterialPropertyValue ColorProperty(string value) =>
        new("pr-color", ColorPropertyName, null, value, null);

    [Fact]
    public void GenerateEffects_ValidNameAndColorOn0To255Scale_NormalizesTo0To1()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Coated Steel", properties: new[] { NameProperty("Chrome"), ColorProperty("255,128,0") }),
            MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        var effect = Assert.Single(effects);
        Assert.Equal("ASSIGN_DISPLAY_MATERIAL", effect.InstructionType);
        Assert.Equal("b1", effect.BodyId.Value);
        Assert.Equal("Chrome", effect.Data["DisplayMaterialName"]);
        var rgb = Assert.IsType<double[]>(effect.Data[SyncCoatingDisplayMaterialEffectRule.RgbDataKey]);
        Assert.Equal(new[] { 1.0, 0.501961, 0.0 }, rgb);
    }

    [Fact]
    public void GenerateEffects_ValidNameAndColorAlreadyOn0To1Scale_PassesThroughUnchanged()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Coated Steel", properties: new[] { NameProperty("Chrome"), ColorProperty("1,0.5,0") }),
            MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        var effect = Assert.Single(effects);
        var rgb = Assert.IsType<double[]>(effect.Data[SyncCoatingDisplayMaterialEffectRule.RgbDataKey]);
        Assert.Equal(new[] { 1.0, 0.5, 0.0 }, rgb);
    }

    [Fact]
    public void GenerateEffects_NameMissing_ReturnsEmpty()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Coated Steel", properties: new[] { ColorProperty("255,128,0") }),
            MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        Assert.Empty(effects);
    }

    [Fact]
    public void GenerateEffects_ColorInvalid_ReturnsEmpty()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Coated Steel", properties: new[] { NameProperty("Chrome"), ColorProperty("not,a,color") }),
            MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        Assert.Empty(effects);
    }

    [Fact]
    public void GenerateEffects_NoCoatingPropertiesAtAll_ReturnsEmpty()
    {
        var context = new MaterialAssignmentRuleContext(
            MakeMaterial("Plain Steel"), MakeBody("b1"), null, Array.Empty<BodyInfo>());

        var effects = _rule.GenerateEffects(context);

        Assert.Empty(effects);
    }
}
