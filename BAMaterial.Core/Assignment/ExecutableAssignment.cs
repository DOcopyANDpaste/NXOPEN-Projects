using BAMaterial.Core.Common;
using BANxOpen.Foundation.Contracts.Common;

namespace BAMaterial.Core.Assignment;

public sealed record ExecutableAssignment(
    BodyId BodyId,
    MaterialId MaterialId,
    IReadOnlyList<SideEffectInstruction> SideEffects);
