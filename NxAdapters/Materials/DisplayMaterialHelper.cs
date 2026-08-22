using Core.Assignment;
using Core.Assignment.Rules;
using NXOpen;
using NXOpen.UF;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Materials;

/// <summary>Reads and writes a body's display/coating material via UFSession.Disp. Executes
/// ASSIGN_DISPLAY_MATERIAL side-effect instructions (emitted by
/// <see cref="SyncCoatingDisplayMaterialEffectRule"/>): finds the named display material in the part,
/// creates it if missing, assigns it to the target body, and colors the body to match the coating.
///
/// The color half needs explaining, because it is not what it looks like. NX has NO API to set a display
/// material's color — <c>uf_disp.h</c> exposes create/assign/delete/query for materials and nothing that
/// writes an RGB, because a Studio material's appearance comes from its own definition. The way NX's own
/// sample code produces a colored coating (see <c>RefONLY/materialInheritColors.txt</c>) is to assign the
/// display material AND set <see cref="Body.Color"/> to the nearest entry in NX's color table. That is
/// what this class does. The mapping is lossy on purpose: <c>AskClosestColor</c> snaps to the table, so the
/// color read back off a body will not equal the coating RGB written to it.
///
/// No undo-handling here by design: <see cref="PartMaterialService.ApplyPlan"/> calls this from inside
/// the per-body UndoScope that already wraps one body's whole assignment. An undo mark is a checkpoint
/// over the session's entire change stream, not something any individual API opts into — so these raw
/// UFSession.Disp calls are captured by <c>UndoToMark</c> the same as any managed NXOpen call, with no
/// separate mechanism needed.
///
/// BodyId -&gt; Body resolution is not done here — this takes an already-resolved Body; that mapping is
/// <see cref="BodyResolver"/>'s job, driven by <see cref="PartMaterialService"/>.</summary>
public sealed class DisplayMaterialHelper
{
    private readonly NxSessionContext _context;

    public DisplayMaterialHelper(NxSessionContext context) => _context = context;

    public OperationResult Execute(SideEffectInstruction instruction, Body body)
    {
        if (instruction.InstructionType != SideEffectInstructionTypes.AssignDisplayMaterial)
        {
            return OperationResult.Fail(
                "UNSUPPORTED_INSTRUCTION",
                $"{nameof(DisplayMaterialHelper)} cannot execute instruction type '{instruction.InstructionType}'.");
        }

        // Data is a plain object bag, so shape is an out-of-band contract with the rule that produced
        // it — shared via that rule's const keys. Validated rather than cast blindly: a malformed bag
        // would otherwise throw KeyNotFound/InvalidCast, which the NXException catch below wouldn't stop.
        if (!instruction.Data.TryGetValue(SyncCoatingDisplayMaterialEffectRule.DisplayMaterialNameDataKey, out var nameValue)
            || nameValue is not string name
            || string.IsNullOrWhiteSpace(name))
        {
            return OperationResult.Fail(
                "MALFORMED_INSTRUCTION",
                $"'{SyncCoatingDisplayMaterialEffectRule.DisplayMaterialNameDataKey}' is missing or not a non-empty string.");
        }

        if (!instruction.Data.TryGetValue(SyncCoatingDisplayMaterialEffectRule.RgbDataKey, out var rgbValue)
            || rgbValue is not double[] { Length: 3 } rgb)
        {
            return OperationResult.Fail(
                "MALFORMED_INSTRUCTION",
                $"'{SyncCoatingDisplayMaterialEffectRule.RgbDataKey}' is missing or not a double[3].");
        }

        try
        {
            var ufSession = _context.UFSession;
            var materialTag = GetOrCreateDisplayMaterial(ufSession, _context.WorkPart.Tag, name);

            AssignToBody(ufSession, body, materialTag);
            ApplyCoatingColor(ufSession, body, rgb);
            RefreshMaterialDisplay(ufSession, materialTag);

            return OperationResult.Success();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Failed to assign coating display material: NX {ex.ErrorCode}: {ex.Message}");
            return OperationResult.Fail(ex.ErrorCode.ToString(), ex.Message);
        }
    }

    /// <summary>Clears whatever display/coating material is currently assigned to <paramref name="body"/>.
    /// Used by <see cref="PartMaterialService.ClearMaterial"/> — unlike <see cref="Execute"/>, this isn't
    /// driven by a <see cref="SideEffectInstruction"/> since clearing bypasses the Core planner/finalizer
    /// pipeline entirely (there's no requested material to plan against).</summary>
    public OperationResult Remove(Body body)
    {
        try
        {
            RemoveFromBody(_context.UFSession, body);
            return OperationResult.Success();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Failed to remove display material: NX {ex.ErrorCode}: {ex.Message}");
            return OperationResult.Fail(ex.ErrorCode.ToString(), ex.Message);
        }
    }

    private static void RemoveFromBody(UFSession ufSession, Body body)
    {
        // Applies the "None" material to the object. Note this does NOT delete the material from the part,
        // which is the wanted behavior — other bodies may still be using it.
        ufSession.Disp.RemoveMaterialAssignment(body.Tag);
    }

    private static Tag GetOrCreateDisplayMaterial(UFSession ufSession, Tag partTag, string name)
    {
        // Look before creating. This is not an optimization: UF_DISP_create_material has no get-or-create
        // behavior, so calling it for a name that already exists silently produces a duplicate material
        // and every subsequent lookup becomes ambiguous.
        if (TryFindExistingMaterial(ufSession, partTag, name, out var existingTag))
            return existingTag;

        // The name NX actually assigns can differ from the one requested, so it is the created material's
        // own tag that gets used from here — never a second lookup by the requested name.
        ufSession.Disp.CreateMaterial(name, out var newTag, out _);
        return newTag;
    }

    private static bool TryFindExistingMaterial(UFSession ufSession, Tag partTag, string name, out Tag materialTag)
    {
        materialTag = Tag.Null;

        try
        {
            // There is no "find material by name" call, so the part's materials are enumerated and matched
            // by name. VERIFY: MaterialFormatType depends on the active renderer — ShIrayplus matches
            // CreateMaterial's documented iray+-only behavior, but if lookups never find anything at
            // runtime, this is the first thing to try changing (ShAuthor / ShMax).
            ufSession.Disp.AskMaterialsInPart(
                partTag, UFDisp.MaterialFormatType.ShIrayplus, out _, out var tags, out var names);

            if (names is null || tags is null)
                return false;

            for (var i = 0; i < names.Length && i < tags.Length; i++)
            {
                if (!string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                    continue;

                materialTag = tags[i];
                return true;
            }

            return false;
        }
        catch (NXException)
        {
            // A part with no display materials at all is a normal state, not a failure.
            return false;
        }
    }

    private static void AssignToBody(UFSession ufSession, Body body, Tag materialTag)
    {
        // Argument order is material first, object second — the reverse of most UF object calls.
        ufSession.Disp.AssignMaterial(materialTag, body.Tag);
    }

    /// <summary>Colors the body to match the coating. See the class summary for why the color goes on the
    /// body rather than on the display material — NX has no API for the latter.</summary>
    private static void ApplyCoatingColor(UFSession ufSession, Body body, double[] rgb)
    {
        ufSession.Disp.AskClosestColor(
            UFConstants.UF_DISP_rgb_model,
            ToUnitRgb(rgb),
            UFConstants.UF_DISP_CCM_EUCLIDEAN_DISTANCE,
            out var colorNumber);

        body.Color = colorNumber;
        body.RedisplayObject();
    }

    /// <summary>UF_DISP_rgb_model wants components in 0-1. Coating tables have been observed returning
    /// either 0-1 or 0-255 with nothing declaring which, so the range is inferred — the same defensive
    /// normalization the NX reference code does.</summary>
    private static double[] ToUnitRgb(double[] rgb) =>
        rgb.Any(component => component > 1.0)
            ? rgb.Select(component => component / 255.0).ToArray()
            : rgb.ToArray();

    /// <summary>Pushes the new material through to what is actually on screen. Without this the assignment
    /// is made but the viewport can keep showing the previous appearance until something else forces a
    /// redraw.</summary>
    private static void RefreshMaterialDisplay(UFSession ufSession, Tag materialTag)
    {
        ufSession.Disp.AskGeometryOfMaterial(materialTag, out var objectCount, out var objectTags);
        if (objectCount > 0 && objectTags is not null)
            ufSession.Disp.UpdateMaterialDisplayOfGeometry(objectCount, objectTags);
    }
}
