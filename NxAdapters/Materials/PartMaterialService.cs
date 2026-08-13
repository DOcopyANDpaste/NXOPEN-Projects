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
/// GetBodies/GetCurrentAssignments always rescan (<see cref="BodyResolver.Refresh"/>) rather than
/// trusting cached state, per the interface's contract.</summary>
public sealed class PartMaterialService : IPartMaterialService
{
    private readonly NxSessionContext _context;
    private readonly BodyResolver _bodyResolver;
    private readonly DisplayMaterialHelper _displayMaterialHelper;
    private readonly NxPhysicalMaterialSource _physicalMaterials;
    private readonly Dictionary<string, Func<SideEffectInstruction, Body, OperationResult>> _executors;
    private IReadOnlyList<NxOpen.Foundation.Contracts.Materials.MaterialLibrary> _resolutionLibraries = Array.Empty<NxOpen.Foundation.Contracts.Materials.MaterialLibrary>();

    public PartMaterialService(
        NxSessionContext context,
        BodyResolver? bodyResolver = null,
        DisplayMaterialHelper? displayMaterialHelper = null,
        NxPhysicalMaterialSource? physicalMaterials = null)
    {
        _context = context;
        _bodyResolver = bodyResolver ?? new BodyResolver(context);
        _displayMaterialHelper = displayMaterialHelper ?? new DisplayMaterialHelper(context);
        _physicalMaterials = physicalMaterials ?? new NxPhysicalMaterialSource(context);
        _executors = new Dictionary<string, Func<SideEffectInstruction, Body, OperationResult>>
        {
            [SideEffectInstructionTypes.AssignDisplayMaterial] = _displayMaterialHelper.Execute,
        };
    }

    public void SetResolutionLibraries(IReadOnlyList<NxOpen.Foundation.Contracts.Materials.MaterialLibrary> libraries) =>
        _resolutionLibraries = libraries;

    public IReadOnlyList<BodyInfo> GetBodies()
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
            var (materialName, resolvedId) = ReadPhysicalMaterial(body);
            var displayMaterial = ReadCurrentDisplayMaterial(uf, body);
            result[id] = new BodyMaterialAssignment(id, materialName, resolvedId, displayMaterial);
        }

        return result;
    }

    public OperationResult ApplyPlan(ExecutablePlan plan)
    {
        using var undo = new UndoScope(_context.Session, "Apply Material Assignment");

        // One apply is one batch: a material the user declines to load is asked about once here, not once
        // per body, but the decline does not carry over into the next apply.
        _physicalMaterials.BeginBatch();

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

            var resolved = ResolveMaterial(assignment.MaterialId);
            if (resolved is null)
            {
                anyFailed = true;
                _context.Log.Error($"Skipped assignment for body '{assignment.BodyId}': material '{assignment.MaterialId}' not found in the resolution libraries (was SetResolutionLibraries called with the library it came from?).");
                continue;
            }

            var (libraryName, materialName) = resolved.Value;

            // The material has to exist in the part before it can be assigned, which may mean a slow load
            // from the NX library. Failing here fails only this body — the rest of the plan still applies.
            var physicalMaterial = _physicalMaterials.Resolve(libraryName, materialName, out var failureReason);
            if (physicalMaterial is null)
            {
                anyFailed = true;
                _context.Log.Error($"Skipped assignment for body '{assignment.BodyId}': {failureReason}");
                continue;
            }

            anyAttempted = true;

            try
            {
                WritePhysicalMaterial(physicalMaterial, body);
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

    public OperationResult ClearMaterial(IReadOnlyList<BodyId> bodyIds)
    {
        using var undo = new UndoScope(_context.Session, "Clear Material");

        try
        {
            _bodyResolver.Refresh();
        }
        catch (NXException ex)
        {
            _context.Log.Error($"Failed to resolve bodies before clearing: NX {ex.ErrorCode}: {ex.Message}");
            return OperationResult.Fail("CLEAR_ABORTED", ex.Message);
        }

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

            var displayResult = _displayMaterialHelper.Remove(body);
            if (!displayResult.Ok)
            {
                anyFailed = true;
                _context.Log.Error($"Failed to remove display material from body '{bodyId}': {displayResult.ErrorCode} {displayResult.Message}");
            }
        }

        if (!anyAttempted && bodyIds.Count > 0)
            return OperationResult.Fail("CLEAR_ABORTED", "No target bodies could be resolved.");

        undo.Commit();
        return anyFailed
            ? OperationResult.Fail("PARTIAL_FAILURE", "One or more bodies failed during Clear; see the listing window for details.")
            : OperationResult.Success();
    }

    /// <summary>Looks up a requested material's NX-catalog name, and the library it came from, out of
    /// whatever libraries the presenter last registered via <see cref="SetResolutionLibraries"/>.
    /// <see cref="ExecutableAssignment.MaterialId"/> alone isn't enough to write a physical material: NX
    /// identifies materials by name, and MaterialId is the library's own id (e.g. a MatML "id" attribute),
    /// not the display name.
    ///
    /// The library name comes back too because loading from NX's library needs it, and because both sides
    /// read the same library files the NX library name is taken to be our library's display name.</summary>
    private (string LibraryName, string MaterialName)? ResolveMaterial(MaterialId materialId)
    {
        foreach (var library in _resolutionLibraries)
        {
            var material = library.Materials.FirstOrDefault(candidate => candidate.Id == materialId);
            if (material is not null)
                return (library.DisplayName, material.Name);
        }

        return null;
    }

    // ---- NX reads/writes ----

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

        try
        {
            // acc_value is a fixed double[11]; only [0] (relative accuracy, 0-1) matters for volume.
            var accuracyValues = new double[11];
            accuracyValues[0] = 0.999;

            // mass_props and statistics are caller-allocated and filled in place — they are [Out]-attributed
            // but NOT by-ref, so they must be sized correctly up front rather than passed with `out`.
            var massProperties = new double[47];
            var statistics = new double[13];

            uf.Modl.AskMassProps3d(
                new[] { body.Tag },
                1,                    // num_objs
                1,                    // type: 1 = solid bodies
                4,                    // units: 1=lb/in 2=lb/ft 3=g/cm 4=kg/m. There is no "part units"
                                      // option, so this is a fixed choice — volume comes back in m^3.
                0.0,                  // density — ignored, the body carries its own
                1,                    // accuracy: 1 = use acc_value
                accuracyValues,
                massProperties,
                statistics);

            // [0] is area, [1] volume, [2] mass.
            return massProperties[1];
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

    private (string? name, MaterialId? resolvedId) ReadPhysicalMaterial(Body body)
    {
        // Physical (bulk) material is a managed-API concept — there is no UFSession.Mtrl subsystem. The
        // deprecated UF equivalent is UFSf.LocateMaterial; AskMaterialOfObject is its documented successor.
        string? name = null;
        try
        {
            name = _context.WorkPart.MaterialManager.PhysicalMaterials.AskMaterialOfObject(body)?.Name;
        }
        catch (NXException)
        {
            // A body with no physical material throws rather than returning null, so this is the normal
            // "unassigned" path, not an error.
        }

        if (string.IsNullOrEmpty(name))
            return (null, null);

        var resolved = _resolutionLibraries
            .SelectMany(library => library.Materials)
            .FirstOrDefault(material => string.Equals(material.Name, name, StringComparison.OrdinalIgnoreCase));

        return (name, resolved?.Id);
    }

    private static DisplayMaterial? ReadCurrentDisplayMaterial(UFSession uf, Body body)
    {
        try
        {
            // AskMaterial hands back the name alongside the tag, so no second lookup is needed.
            uf.Disp.AskMaterial(body.Tag, out var materialTag, out var name);
            if (materialTag.Equals(Tag.Null) || string.IsNullOrEmpty(name))
                return null;

            return new DisplayMaterial(new MaterialId(name), name, ReadBodyRgb(uf, body));
        }
        catch (NXException)
        {
            return null;
        }
    }

    /// <summary>The body's color as RGB bytes. Read off the BODY, not the material: NX exposes no color on a
    /// display material, so <see cref="DisplayMaterialHelper"/> writes the coating color to the body and
    /// this is the matching read. It will not round-trip exactly — the write snaps to NX's color table.</summary>
    private static (byte R, byte G, byte B) ReadBodyRgb(UFSession uf, Body body)
    {
        // clr_values is caller-allocated and filled in place (three components, 0-1), not an `out`.
        var rgb = new double[3];
        uf.Disp.AskColor(body.Color, UFConstants.UF_DISP_rgb_model, out _, rgb);

        return ((byte)Math.Round(rgb[0] * 255), (byte)Math.Round(rgb[1] * 255), (byte)Math.Round(rgb[2] * 255));
    }

    private static void WritePhysicalMaterial(PhysicalMaterial material, Body body) =>
        material.AssignObjects(new NXObject[] { body });

    private static void RemovePhysicalMaterial(UFSession uf, Body body)
    {
        // UFSf.UnlinkMaterial is deprecated (NX 2312), and the replacement the header names —
        // PhysicalMaterial.UnassignObjects() — does not exist in NX 2412's managed API. The only managed
        // unassign is UnassignAllObjects(), which strips the material from EVERY body using it. So this
        // stays on the deprecated call deliberately: do not "modernize" it into the all-bodies version.
#pragma warning disable CS0618 // deprecated with no working replacement — see above
        uf.Sf.UnlinkMaterial(body.Tag);
#pragma warning restore CS0618
    }
}
