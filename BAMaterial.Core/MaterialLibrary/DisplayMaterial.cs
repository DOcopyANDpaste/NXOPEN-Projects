using NxOpen.Foundation.Contracts.Common;

namespace Core.MaterialLibrary;

/// <summary>The display/coating material currently associated with a body in NX, as read off the part.
/// Distinct from a library Material (which has MatML properties, a library id, and a category) — a
/// display material has none of that, just an identity and a color.</summary>
public sealed record DisplayMaterial(MaterialId Id, string Name, (byte R, byte G, byte B) Rgb);
