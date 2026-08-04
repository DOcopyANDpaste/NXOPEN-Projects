using Core.Assignment;
using NXOpen;
using NXOpen.UF;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Materials;

/// <summary>Executes ASSIGN_DISPLAY_MATERIAL side-effect instructions (emitted by
/// Core.Assignment.Rules.SyncCoatingDisplayMaterialEffectRule): looks up a display material by name via
/// UFSession.Disp, creates it if missing, refreshes its RGB either way, and assigns it to the target
/// body.
///
/// IMPORTANT — every UFSession.Disp call below is a BEST-EFFORT PLACEHOLDER. There is no NX
/// installation on the machine this was written on, so exact UFDisp method names/signatures are
/// unverified and must be confirmed/corrected against the real NX Open .NET reference for the installed
/// NX version before this will even compile, let alone run.
///
/// No undo-handling here by design: this must be called by the (future) ApplyPlan dispatcher from
/// within the single Session.UndoScope that already wraps the whole ExecutablePlan. NX's native undo
/// mechanism is assumed (UNVERIFIED) to capture whatever UF calls happen inside that mark automatically;
/// if UFSession.Disp material calls don't participate in the standard undo mechanism, a different
/// mechanism (e.g. explicit UF undo-action registration) would be needed instead.
///
/// BodyId -&gt; Body resolution is out of scope here — this takes an already-resolved Body; that mapping
/// belongs to whoever eventually builds IPartMaterialService.ApplyPlan.</summary>
public sealed class CoatingDisplayMaterialAssigner
{
    private readonly NxSessionContext _context;

    public CoatingDisplayMaterialAssigner(NxSessionContext context) => _context = context;

    public OperationResult Execute(SideEffectInstruction instruction, Body body)
    {
        if (instruction.InstructionType != SideEffectInstructionTypes.AssignDisplayMaterial)
        {
            return OperationResult.Fail(
                "UNSUPPORTED_INSTRUCTION",
                $"{nameof(CoatingDisplayMaterialAssigner)} cannot execute instruction type '{instruction.InstructionType}'.");
        }

        try
        {
            // Key names/shapes here are an out-of-band contract with
            // Core.Assignment.Rules.SyncCoatingDisplayMaterialEffectRule, which produced this
            // instruction — NxAdapters doesn't reference Core, so these are literals, not shared
            // constants. "RGB" -> double[3], normalized 0-1 (NOT 0-255 bytes).
            var name = (string)instruction.Data["DisplayMaterialName"];
            var rgb = (double[])instruction.Data["RGB"];
            var (r, g, b) = (rgb[0], rgb[1], rgb[2]);

            var ufSession = _context.UFSession;
            var materialTag = GetOrCreateDisplayMaterial(ufSession, name, r, g, b);
            AssignToBody(ufSession, body, materialTag);

            return OperationResult.Success();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Failed to assign coating display material: NX {ex.ErrorCode}: {ex.Message}");
            return OperationResult.Fail(ex.ErrorCode.ToString(), ex.Message);
        }
    }

    /// <summary>Clears whatever display/coating material is currently assigned to <paramref name="body"/>.
    /// Used by <c>PartMaterialService.ClearMaterial</c> — unlike <see cref="Execute"/>, this isn't driven
    /// by a <see cref="SideEffectInstruction"/> since clearing bypasses the Core planner/finalizer
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
        // VERIFY: exact "remove/clear display material from object" method/signature — candidate is
        // calling the same "put material" call AssignToBody uses, with Tag.Null in place of a real
        // material tag, but that is unconfirmed; a dedicated removal call may exist instead.
        ufSession.Disp.PutMaterial(body.Tag, Tag.Null);
    }

    private static Tag GetOrCreateDisplayMaterial(UFSession ufSession, string name, double r, double g, double b)
    {
        if (TryFindExistingMaterial(ufSession, name, out var existingTag))
        {
            // Always refresh the color even for an existing material, in case the coating library's
            // color changed since this display material was first created.
            SetMaterialColor(ufSession, existingTag, r, g, b);
            return existingTag;
        }

        // VERIFY: exact "create display material" method/signature (name is a guess).
        ufSession.Disp.CreateMaterial(name, out var newTag);
        SetMaterialColor(ufSession, newTag, r, g, b);
        return newTag;
    }

    private static bool TryFindExistingMaterial(UFSession ufSession, string name, out Tag materialTag)
    {
        try
        {
            // VERIFY: exact "find display material by name" method/signature (name is a guess).
            ufSession.Disp.AskMaterialByName(name, out materialTag);
            return true;
        }
        catch (NXException)
        {
            // VERIFY: confirm "not found" genuinely surfaces as an NXException here rather than, say,
            // a specific out-parameter/return-code convention that should be checked explicitly instead
            // of caught broadly — a broad catch here could also mask an unrelated real failure.
            materialTag = Tag.Null;
            return false;
        }
    }

    private static void SetMaterialColor(UFSession ufSession, Tag materialTag, double r, double g, double b)
    {
        // VERIFY: exact method/signature, and whether it expects normalized 0-1 doubles (assumed/passed
        // here) or 0-255 byte components — if the latter, multiply each by 255 and round before calling.
        ufSession.Disp.SetMaterialColor(materialTag, r, g, b);
    }

    private static void AssignToBody(UFSession ufSession, Body body, Tag materialTag)
    {
        // VERIFY: exact "assign display material to object" method/signature.
        ufSession.Disp.PutMaterial(body.Tag, materialTag);
    }
}
