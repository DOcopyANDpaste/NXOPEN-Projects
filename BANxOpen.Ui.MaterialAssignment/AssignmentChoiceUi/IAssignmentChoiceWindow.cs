using BANxOpen.Foundation.Core.Materials.Assignment.Choices;

namespace BANxOpen.Ui.MaterialAssignment.AssignmentChoiceUi;

/// <summary>What came back from putting one <see cref="AssignmentChoice"/> to the user.</summary>
/// <param name="OptionId">The option picked, or null when the user cancelled.</param>
/// <param name="Warnings">Anything worth telling them about the pick afterwards — a confirmed thickness
/// mismatch, say. The presenter appends these to the Apply result so the reason a part ends up as it did is
/// visible after the window has gone.</param>
public sealed record AssignmentChoiceResult(string? OptionId, IReadOnlyList<string> Warnings)
{
    public static readonly AssignmentChoiceResult Cancelled = new(null, Array.Empty<string>());

    public static AssignmentChoiceResult Picked(string optionId, IReadOnlyList<string>? warnings = null) =>
        new(optionId, warnings ?? Array.Empty<string>());

    public bool WasCancelled => OptionId is null;
}

/// <summary>Puts a choice to the user and returns their answer.
///
/// A seam for the same reason <see cref="IMaterialPropertyWindow"/> is one: the presenter is the dialog's logic
/// and should not be the thing that knows WinForms exists.</summary>
public interface IAssignmentChoiceWindow
{
    /// <summary>Shows <paramref name="choice"/> and blocks until the user answers or cancels.</summary>
    AssignmentChoiceResult Resolve(AssignmentChoice choice);
}
