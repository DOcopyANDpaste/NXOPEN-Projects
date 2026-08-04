using Core.Bodies;
using Core.Common;
using NXOpen;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Materials;

/// <summary>Maps between the plain-string <see cref="BodyId"/> used across Core and live NXOpen
/// <see cref="Body"/> objects in the work part. Every <see cref="IPartMaterialService"/>
/// method that promises a fresh rescan calls <see cref="Refresh"/> first, so this cache never survives
/// across calls.</summary>
public sealed class BodyResolver
{
    private readonly NxSessionContext _context;
    private Dictionary<BodyId, Body> _bodiesById = new();

    public BodyResolver(NxSessionContext context) => _context = context;

    public IReadOnlyList<Body> Refresh()
    {
        var bodies = new List<Body>();
        var byId = new Dictionary<BodyId, Body>();

        foreach (Body body in _context.WorkPart.Bodies)
        {
            bodies.Add(body);
            byId[GetBodyId(body)] = body;
        }

        _bodiesById = byId;
        return bodies;
    }

    public bool TryResolve(BodyId id, out Body body)
    {
        if (_bodiesById.TryGetValue(id, out var found))
        {
            body = found;
            return true;
        }

        body = null!;
        return false;
    }

    // VERIFY: exact property name/signature for a stable, session-durable body identifier. JournalIdentifier
    // is NX's journal-visible stable id for an object — confirm it exists on Body with this exact name/type
    // for the installed NX version, and that it survives save/close/reopen, before relying on it.
    public static BodyId GetBodyId(Body body) => new(body.JournalIdentifier);
}
