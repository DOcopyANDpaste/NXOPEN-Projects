using Core.Common;

namespace Core.Bodies;

public enum BodyKind
{
    Solid,
    Sheet,
    Unknown,
}

public sealed record BodyInfo(
    BodyId Id,
    string Name,
    BodyKind Kind,
    double Volume,
    IReadOnlyDictionary<string, string> Attributes);
