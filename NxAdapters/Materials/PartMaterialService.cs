using Core.Assignment;
using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NXOpen;
using NXOpen.UF;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Materials;

/// <summary>Implements <see cref="IPartMaterialService"/> — the seam to the live NX work part.
/// GetSolidBodies/GetCurrentAssignments always rescan (<see cref="BodyResolver.Refresh"/>) rather than
/// trusting cached state, per the interface's contract.
///
/// NOTE: despite the interface's name, <see cref="GetSolidBodies"/> returns every body in the work part
/// (Solid, Sheet, and Unknown kinds), not just solids — <c>BlockRestrictedBodyTypeRule</c> in Core
/// already depends on Sheet bodies showing up here (it blocks casting-category materials specifically
/// on Sheet bodies), so filtering them out here would make that rule unreachable.</summary>
public sealed class PartMaterialService : IPartMaterialService
{
    private readonly NxSessionContext _context;
    private readonly BodyResolver _bodyResolver;
    private readonly CoatingDisplayMaterialAssigner _displayMaterialAssigner;
    private readonly Dictionary<string, Func<SideEffectInstruction, Body, OperationResult>> _executors;
    private IReadOnlyList<NxOpen.Foundation.Contracts.Materials.MaterialLibrary> _resolutionLibraries = Array.Empty<NxOpen.Foundation.Contracts.Materials.MaterialLibrary>();

    public PartMaterialService(         
        NxSessionContext context,
        BodyResolver? bodyResolver = null,
        CoatingDisplayMaterialAssigner? displayMaterialAssigner = null)
    {
        _context = context;
        _bodyResolver = bodyResolver ?? new BodyResolver(context);
        _displayMaterialAssigner = displayMaterialAssigner ?? new CoatingDisplayMaterialAssigner(context);
        _executors = new Dictionary<string, Func<SideEffectInstruction, Body, OperationResult>>
        {
            [SideEffectInstructionTypes.AssignDisplayMaterial] = _displayMaterialAssigner.Execute,
            // SideEffectInstructionTypes.SyncPhysicalProperty has no executor yet — deliberately deferred
            // (see the plan's "out of scope this round" note). ApplyPlan logs+skips unregistered types
            // rather than failing, so SyncPhysicalPropertiesEffectRule can keep emitting them upstream.
        };
    }

    /// <summary>Non-interface: <see cref="GetCurrentAssignments"/> takes no parameters (fixed interface
    /// signature), so the presenter sets which loaded libraries to best-effort match physical-material
    /// names against separately, whenever the library selection changes.</summary>
    public void SetResolutionLibraries(IReadOnlyList<NxOpen.Foundation.Contracts.Materials.MaterialLibrary> libraries) =>
        _resolutionLibraries = libraries;

    public IReadOnlyList<BodyInfo> GetSolidBodies()
    {
        var uf = _context.UFSession;
        var bodies = _bodyResolver.Refresh();
        var result = new List<BodyInfo>(bodies.Count);

        foreach (var body in bodies)
        {
            var kind = ClassifyBody(body);
            var volume = MeasureVolume(uf, body, kind);
            var attributes = ReadAttributes(body);
            result.Add(new BodyInfo(BodyResolver.GetBodyId(body), body.Name ?? string.Empty, kind, volume, attributes));
        }

        return result;
    }

    public IReadOnlyDictionary<BodyId, BodyMaterialAssignment> GetCurrentAssignments()
    {
        var uf = _context.UFSession;
        var bodies = _bodyResolver.Refresh();
        var result = new Dictionary<BodyId, BodyMaterialAssignment>();

        foreach (var body in bodies)
        {
            var id = BodyResolver.GetBodyId(body);
            var (materialName, resolvedId) = ReadPhysicalMaterial(uf, body);
            var displayMaterial = ReadCurrentDisplayMaterial(uf, body);
            result[id] = new BodyMaterialAssignment(id, materialName, resolvedId, displayMaterial);
        }

        return result;
    }

    public OperationResult ApplyPlan(ExecutablePlan plan)
    {
        using var undo = new UndoScope(_context.Session, "Apply Material Assignment");

        try
        {
            _bodyResolver.Refresh();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Failed to resolve bodies before applying plan: NX {ex.ErrorCode}: {ex.Message}");
            return OperationResult.Fail("APPLY_ABORTED", ex.Message);
        }

        var anyAttempted = false;
        var anyFailed = false;

        foreach (var assignment in plan.Assignments)
        {
            if (!_bodyResolver.TryResolve(assignment.BodyId, out var body))
            {
                anyFailed = true;
                _context.Log.Warn($"Skipped assignment for body '{assignment.BodyId}': body could not be resolved.");
                continue;
            }

            var materialName = ResolveMaterialName(assignment.MaterialId);
            if (materialName is null)
            {
                anyFailed = true;
                _context.Log.Error($"Skipped assignment for body '{assignment.BodyId}': material '{assignment.MaterialId}' not found in the resolution libraries (was SetResolutionLibraries called with the library it came from?).");
                continue;
            }

            anyAttempted = true;

            try
            {
                WritePhysicalMaterial(_context.UFSession, body, materialName);
            }
            catch (NXException ex)
            {
                anyFailed = true;
                _context.Log.Error($"Failed to assign physical material to body '{assignment.BodyId}': NX {ex.ErrorCode}: {ex.Message}");
                continue;
            }

            foreach (var instruction in assignment.SideEffects)
            {
                if (!_executors.TryGetValue(instruction.InstructionType, out var executor))
                {
                    _context.Log.Warn($"No executor registered for instruction type '{instruction.InstructionType}' — skipping (deferred feature).");
                    continue;
                }

                var result = executor(instruction, body);
                if (!result.Ok)
                {
                    anyFailed = true;
                    _context.Log.Error($"Side effect '{instruction.InstructionType}' failed for body '{assignment.BodyId}': {result.ErrorCode} {result.Message}");
                }
            }
        }

        if (!anyAttempted && plan.Assignments.Count > 0)
            return OperationResult.Fail("APPLY_ABORTED", "No target bodies in the plan could be resolved.");

        undo.Commit();
        return anyFailed
            ? OperationResult.Fail("PARTIAL_FAILURE", "One or more bodies failed during Apply; see the listing window for details.")
            : OperationResult.Success();
    }

    /// <summary>Non-interface: direct action for the dialog's "Clear Material" button — bypasses the Core
    /// planner/finalizer pipeline entirely (there is no requested material to plan against when clearing).
    /// Clears both the physical (bulk) material and the display/coating material.</summary>
    public OperationResult ClearMaterial(IReadOnlyList<BodyId> bodyIds)
    {
        using var undo = new UndoScope(_context.Session, "Clear Material");
        _bodyResolver.Refresh();

        var anyAttempted = false;
        var anyFailed = false;

        foreach (var bodyId in bodyIds)
        {
            if (!_bodyResolver.TryResolve(bodyId, out var body))
            {
                anyFailed = true;
                _context.Log.Warn($"Skipped clear for body '{bodyId}': body could not be resolved.");
                continue;
            }

            anyAttempted = true;

            try
            {
                RemovePhysicalMaterial(_context.UFSession, body);
            }
            catch (NXException ex)
            {
                anyFailed = true;
                _context.Log.Error($"Failed to remove physical material from body '{bodyId}': NX {ex.ErrorCode}: {ex.Message}");
            }

            var displayResult = _displayMaterialAssigner.Remove(body);
            if (!displayResult.Ok)
            {
                anyFailed = true;
                _context.Log.Error($"Failed to remove display material from body '{bodyId}': {displayResult.ErrorCode} {displayResult.Message}");
            }
        }

        if (!anyAttempted && bodyIds.Count > 0)
            return OperationResult.Fail("APPLY_ABORTED", "No target bodies could be resolved.");

        undo.Commit();
        return anyFailed
            ? OperationResult.Fail("PARTIAL_FAILURE", "One or more bodies failed during Clear; see the listing window for details.")
            : OperationResult.Success();
    }

    /// <summary>Looks up a requested material's NX-catalog name from whatever libraries the presenter last
    /// registered via <see cref="SetResolutionLibraries"/> — <see cref="ExecutableAssignment.MaterialId"/>
    /// alone isn't enough to write a physical material (NX identifies materials by name, and MaterialId is
    /// the library's own id, e.g. a MatML "id" attribute, not the display name).</summary>
    private string? ResolveMaterialName(MaterialId materialId) =>
        _resolutionLibraries
            .SelectMany(library => library.Materials)
            .FirstOrDefault(material => material.Id == materialId)
            ?.Name;

    // ---- VERIFY-flagged NX reads/writes ----

    private static BodyKind ClassifyBody(Body body)
    {
        // VERIFY: exact property/API — candidate is a direct boolean on Body (e.g. IsSheetBody); if no
        // such property exists on the installed version, fall back to a UF_MODL body-type query instead
        // (UFSession.Modl.AskBodyType or similar).
        try
        {
            return body.IsSheetBody ? BodyKind.Sheet : BodyKind.Solid;
        }
        catch (NXException)
        {
            return BodyKind.Unknown;
        }
    }

    private static double MeasureVolume(UFSession uf, Body body, BodyKind kind)
    {
        if (kind != BodyKind.Solid)
            return 0.0;

        // VERIFY: exact mass-properties API — candidates are UFSession.Modl.AskMassProps3d (low-level UF,
        // real signature has more parameters than shown here — accuracy, density, output arrays for mass/
        // volume/inertia) or Session.MeasureManager's higher-level measure-bodies call. Units are assumed
        // to come back in part units, unconverted.
        try
        {
            uf.Modl.AskMassProps3d(body.Tag, 0.999, out var volume);
            return volume;
        }
        catch (NXException)
        {
            return 0.0;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAttributes(Body body)
    {
        // VERIFY: exact GetUserAttributes overload/return shape — NXObject.AttributeInformation with a
        // Title/StringValue pair is a best-effort guess.
        var result = new Dictionary<string, string>();
        try
        {
            foreach (var attribute in body.GetUserAttributes())
                result[attribute.Title] = attribute.StringValue ?? string.Empty;
        }
        catch (NXException)
        {
            // best-effort — attribute read failures shouldn't block body enumeration
        }

        return result;
    }

    private (string? name, MaterialId? resolvedId) ReadPhysicalMaterial(UFSession uf, Body body)
    {
        // VERIFY: candidate UF_MTRL function group (UFSession.Mtrl) for bulk/physical material assignment,
        // mirroring the same UFSession.<Subsystem> pattern already used for display material
        // (UFSession.Disp) in CoatingDisplayMaterialAssigner. Exact method name/signature, and even
        // whether this subsystem exists under this name on the installed version, are unconfirmed.
        string? name = null;
        try
        {
            uf.Mtrl.AskBodyMaterial(body.Tag, out name);
        }
        catch (NXException)
        {
            // treated as "no physical material assigned"
        }

        if (string.IsNullOrEmpty(name))
            return (null, null);

        var resolved = _resolutionLibraries
            .SelectMany(library => library.Materials)
            .FirstOrDefault(material => string.Equals(material.Name, name, StringComparison.OrdinalIgnoreCase));

        return (name, resolved?.Id);
    }

    private DisplayMaterial? ReadCurrentDisplayMaterial(UFSession uf, Body body)
    {
        // VERIFY: exact UFSession.Disp "ask material on object" + "ask name/color" calls — read-side
        // counterpart of the already-VERIFY-flagged calls in CoatingDisplayMaterialAssigner
        // (CreateMaterial/AskMaterialByName/SetMaterialColor/PutMaterial).
        try
        {
            uf.Disp.AskMaterial(body.Tag, out var materialTag);
            if (materialTag.Equals(Tag.Null))
                return null;

            uf.Disp.AskMaterialName(materialTag, out var name);
            uf.Disp.AskMaterialColor(materialTag, out var r, out var g, out var b);
            return new DisplayMaterial(new MaterialId(name), name, ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)));
        }
        catch (NXException)
        {
            return null;
        }
    }

    private static void WritePhysicalMaterial(UFSession uf, Body body, string materialName)
    {
        // VERIFY: write-side counterpart of ReadPhysicalMaterial — exact UFSession.Mtrl method/signature.
        uf.Mtrl.SetBodyMaterial(body.Tag, materialName);
    }

    private static void RemovePhysicalMaterial(UFSession uf, Body body)
    {
        // VERIFY: may not simply be SetBodyMaterial(body.Tag, "") — a distinct removal call may exist
        // instead (mirrors the same open question as CoatingDisplayMaterialAssigner.RemoveFromBody).
        uf.Mtrl.SetBodyMaterial(body.Tag, string.Empty);
    }
}
