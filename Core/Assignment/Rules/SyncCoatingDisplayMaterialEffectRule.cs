namespace Core.Assignment.Rules;

/// <summary>Emits an ASSIGN_DISPLAY_MATERIAL instruction carrying the coating's display material name
/// and RGB, for the adapter layer to look up/create the display material in NX and assign it to the
/// body. Independently re-validates name+color (rather than trusting that
/// <see cref="ValidateCoatingDisplayMaterialRule"/> ran first) — effect rules shouldn't assume a
/// specific gate rule executed, since the pipeline is meant to be composed freely.</summary>
public sealed class SyncCoatingDisplayMaterialEffectRule : IPostAssignmentEffectRule
{
    /// <summary>Data key for the RGB triplet — a <c>double[3]</c> of normalized 0-1 components, in
    /// [R, G, B] order. Callers must know this shape; <see cref="SideEffectInstruction.Data"/> is a
    /// plain object bag, not self-describing.</summary>
    public const string RgbDataKey = "RGB";

    public string RuleId => "SYNC_COATING_DISPLAY_MATERIAL";

    public int Order => 200;

    public IReadOnlyList<SideEffectInstruction> GenerateEffects(MaterialAssignmentRuleContext context)
    {
        var material = context.RequestedMaterial;

        var displayMaterialName = CoatingPropertyReader.GetDisplayMaterialName(material);
        if (displayMaterialName is null)
            return Array.Empty<SideEffectInstruction>();

        if (!CoatingPropertyReader.TryGetRgb(material, out var r, out var g, out var b))
            return Array.Empty<SideEffectInstruction>();

        // R/G/B are normalized 0-1 (see CoatingPropertyReader.TryGetRgb) — rounded to 6 decimal places,
        // far more precision than a color channel needs, just to keep the value clean.
        double[] rgb = [Math.Round(r, 6), Math.Round(g, 6), Math.Round(b, 6)];
        var data = new Dictionary<string, object>
        {
            ["DisplayMaterialName"] = displayMaterialName,
            [RgbDataKey] = rgb,
        };

        return new[] { new SideEffectInstruction(SideEffectInstructionTypes.AssignDisplayMaterial, context.TargetBody.Id, data) };
    }
}
