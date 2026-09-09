using NXOpen;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Materials;

/// <summary>Turns a (library name, material name) pair into a <see cref="PhysicalMaterial"/> that exists in
/// the work part, so it can be assigned to a body. The single place that pays the cost of NX's material
/// library, and the reason browsing the dialog never does.
///
/// The tool browses materials through its own MatML reader (<c>FileSystemMaterialLibraryRepository</c> and
/// friends), which is instant and is the only source of categories and MatML properties. NX's own library is
/// a separate, much slower thing, and it is only consulted at the moment a material is actually assigned —
/// never in bulk, never on a library switch, never on dialog open. Both sides read the same library files,
/// so the NX library name is taken to equal our library's display name.
///
/// Resolution order: cache, then materials already in the part, then — after asking the user, because this
/// is the slow step — a load from the NX library.</summary>
public sealed class NxPhysicalMaterialSource
{
    private readonly NxSessionContext _context;
    private readonly Func<string, bool> _confirmLoad;

    /// <summary>Materials resolved this dialog session. Assigning one material to fifty bodies pays the
    /// library load once.</summary>
    private readonly Dictionary<string, PhysicalMaterial> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Materials the user declined to load. Deliberately shorter-lived than <see cref="_resolved"/>
    /// — see <see cref="BeginBatch"/>.</summary>
    private readonly HashSet<string> _declined = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="confirmLoad">Asks the user whether to run the slow load. Injected rather than calling a
    /// message box directly so this stays free of UI and remains testable; callers normally pass
    /// <c>NxMessageBoxHelper.Confirm</c>.</param>
    public NxPhysicalMaterialSource(NxSessionContext context, Func<string, bool>? confirmLoad = null)
    {
        _context = context;
        _confirmLoad = confirmLoad ?? NxMessageBoxHelper.Confirm;
    }

    /// <summary>Starts a new apply. Clears the declined set — a decline should suppress re-prompting for the
    /// rest of THIS apply (so a fifty-body plan asks once, not fifty times) without silently suppressing it
    /// for the rest of the dialog session, which would leave the user unable to change their mind.</summary>
    public void BeginBatch() => _declined.Clear();

    /// <summary>The material, or null if it could not be resolved — because the user declined the load, or
    /// because NX does not have it. Callers should fail only the body in hand, never the whole plan.</summary>
    public PhysicalMaterial? Resolve(string libraryName, string materialName, out string? failureReason)
    {
        failureReason = null;

        if (_resolved.TryGetValue(materialName, out var cached))
            return cached;

        if (_declined.Contains(materialName))
        {
            failureReason = $"Loading '{materialName}' from the NX library was declined.";
            return null;
        }

        var inPart = FindInPart(materialName);
        if (inPart is not null)
        {
            _resolved[materialName] = inPart;
            return inPart;
        }

        if (!_confirmLoad(
                $"'{materialName}' is not in this part yet.{Environment.NewLine}" +
                $"Load it from the NX material library '{libraryName}'?{Environment.NewLine}{Environment.NewLine}" +
                "This can take a while for a large library."))
        {
            _declined.Add(materialName);
            failureReason = $"Loading '{materialName}' from the NX library was declined.";
            return null;
        }

        var loaded = LoadFromLibrary(libraryName, materialName, out failureReason);
        if (loaded is not null)
            _resolved[materialName] = loaded;

        return loaded;
    }

    private PhysicalMaterial? FindInPart(string materialName)
    {
        try
        {
            // The collection is typed to the Material base, so narrow before matching — a part can hold
            // other material kinds and only a PhysicalMaterial can be assigned as a bulk material.
            return _context.WorkPart.MaterialManager.PhysicalMaterials
                .ToArray()
                .OfType<PhysicalMaterial>()
                .FirstOrDefault(material => string.Equals(material.Name, materialName, StringComparison.OrdinalIgnoreCase));
        }
        catch (NXException ex)
        {
            _context.Log.Warn($"Could not list the physical materials already in the part: {ex.Message}");
            return null;
        }
    }

    private PhysicalMaterial? LoadFromLibrary(string libraryName, string materialName, out string? failureReason)
    {
        failureReason = null;

        try
        {
            _context.Log.Info($"Loading physical material '{materialName}' from NX library '{libraryName}'...");
            var material = _context.WorkPart.MaterialManager.PhysicalMaterials.LoadFromLibrary(libraryName, materialName);
            _context.Log.Info($"Loaded '{materialName}'.");
            return material;
        }
        catch (NXException ex)
        {
            failureReason =
                $"'{materialName}' was not found in the NX material library '{libraryName}' (NX {ex.ErrorCode}: {ex.Message}).";
            return null;
        }
    }
}
