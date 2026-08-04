using Core.Common;

namespace Core.Assignment;

/// <summary>An instruction for the adapter layer to carry out after a material is assigned to a body
/// (e.g. syncing a physical property). <see cref="InstructionType"/> is a string discriminator rather
/// than an enum specifically so new instruction types can be introduced by new rules without editing
/// this type or anything that consumes it structurally. <see cref="Data"/> values are <c>object</c> so a
/// single key can hold a structured value (e.g. a <c>double[]</c>) rather than everything being forced
/// through string encoding — the producer (a Core rule) and consumer (the matching adapter code) must
/// agree out-of-band on each key's expected runtime type.</summary>
public sealed record SideEffectInstruction(
    string InstructionType,
    BodyId BodyId,
    IReadOnlyDictionary<string, object> Data);
