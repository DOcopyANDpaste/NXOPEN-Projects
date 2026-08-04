using Core.Common;
using NxOpen.Foundation.Contracts.Common;

namespace Core.Assignment;

public sealed record ExecutableAssignment(
    BodyId BodyId,
    MaterialId MaterialId,
    IReadOnlyList<SideEffectInstruction> SideEffects);
