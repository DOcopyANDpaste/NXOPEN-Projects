namespace Core.Assignment.Rules;

/// <summary>Emits an ASSIGN_DISPLAY_MATERIAL instruction carrying the coating's display material name
/// and RGB, for the adapter layer to look up/create the display material in NX and assign it to the
/// body. Independently re-validates name+color (rather than trusting that
/// <see cref="ValidateCoatingDisplayMaterialRule"/> ran first) — effect rules shouldn't assume a
/// specific gate rule executed, since the pipeline is meant to be composed freely.</summary>
public sealed class SyncCoatingDisplayMaterialEffectRule : IPostAssignmentEffectRule
{
    /// <summary>Data key for the display material name — a <c>string</c>.</summary>
    public const string DisplayMaterialNameDataKey = "DisplayMaterialName";

    /// <summary>Data key for the RGB triplet — a <c>double[3]</c> of normalized 0-1 components, in
    /// [R, G, B] order. Callers must know this shape; <see cref="SideEffectInstruction.Data"/> is a
    /// plain object bag, not self-describing.</summary>
    public const string RgbDataKey = "RGB";

    /// <summary>Data key for whether the effect fell back to
    /// <see cref="CoatingPropertyReader.DefaultDisplayMaterialName"/>/<see cref="CoatingPropertyReader.DefaultDisplayMaterialRgb"/>
    /// — a <c>bool</c>. Lets the adapter layer warn the user when the library data was incomplete.</summary>
    public const string UsedDefaultDataKey = "UsedDefault";

    public string RuleId => "SYNC_COATING_DISPLAY_MATERIAL";

    public int Order => 200;

    public IReadOnlyList<SideEffectInstruction> GenerateEffects(MaterialAssignmentRuleContext context)
    {
        var material = context.RequestedMaterial;

        var displayMaterialName = CoatingPropertyReader.GetDisplayMaterialName(material);
        var hasRgb = CoatingPropertyReader.TryGetRgb(material, out var r, out var g, out var b);

        // Every assignment now syncs a display material — a material whose MatML doesn't define coating
        // name/color falls back to the shared default rather than leaving the body's appearance untouched.
        var usedDefault = displayMaterialName is null || !hasRgb;

        // R/G/B are normalized 0-1 (see CoatingPropertyReader.TryGetRgb) — rounded to 6 decimal places,
        // far more precision than a color channel needs, just to keep the value clean.
        double[] rgb = usedDefault
            ? CoatingPropertyReader.DefaultDisplayMaterialRgb
            : [Math.Round(r, 6), Math.Round(g, 6), Math.Round(b, 6)];

        var data = new Dictionary<string, object>
        {
            [DisplayMaterialNameDataKey] = usedDefault ? CoatingPropertyReader.DefaultDisplayMaterialName : displayMaterialName!,
            [RgbDataKey] = rgb,
            [UsedDefaultDataKey] = usedDefault,
        };

        return new[] { new SideEffectInstruction(SideEffectInstructionTypes.AssignDisplayMaterial, context.TargetBody.Id, data) };
    }
}
