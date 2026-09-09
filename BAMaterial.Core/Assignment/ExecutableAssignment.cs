using BAMaterial.Core.Common;
using NxOpen.Foundation.Contracts.Common;

namespace BAMaterial.Core.Assignment;

public sealed record ExecutableAssignment(
    BodyId BodyId,
    MaterialId MaterialId,
    IReadOnlyList<SideEffectInstruction> SideEffects);
