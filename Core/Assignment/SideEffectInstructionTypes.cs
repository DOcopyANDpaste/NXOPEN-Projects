namespace Core.Assignment;

/// <summary>Named constants for <see cref="SideEffectInstruction.InstructionType"/> values. The type is
/// still a plain string (not an enum) so new instruction types never require editing
/// <see cref="SideEffectInstruction"/> itself — these constants exist only so the Core rule that
/// produces an instruction and the NxAdapters code that executes it can't drift apart via a typo.</summary>
public static class SideEffectInstructionTypes
{
    public const string SyncPhysicalProperty = "SYNC_PHYSICAL_PROPERTY";
    public const string AssignDisplayMaterial = "ASSIGN_DISPLAY_MATERIAL";
}
