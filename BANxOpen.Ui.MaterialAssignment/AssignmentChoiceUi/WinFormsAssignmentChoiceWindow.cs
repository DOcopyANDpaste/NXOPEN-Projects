using System;
using System.Diagnostics;
using System.Windows.Forms;
using BANxOpen.Foundation.Core.Materials.Assignment.Choices;
using BANxOpen.Foundation.NxAdapters;

namespace BANxOpen.Ui.MaterialAssignment.AssignmentChoiceUi;

/// <summary>Shows <see cref="AssignmentChoiceForm"/> modally, on NX's own thread.
///
/// Deliberately not the background STA pump <see cref="MaterialPropDisplay.WinFormsMaterialPropertyWindow"/>
/// uses. That pump exists so several property popups can sit open beside the dialog without blocking it; this
/// window has the opposite requirement — the caller is mid-commit and cannot go on until it has an answer.
/// Posting to another thread and then blocking on the result would deadlock the moment that thread needed
/// anything from this one.
///
/// A modal <see cref="Form"/> is also not a second Block UI Styler dialog, which is the thing the Styler will
/// not have two of. It is an ordinary Win32 modal owned by NX's main window, so NX stays behind it and cannot
/// be driven while the question is open.
///
/// Nothing here touches NXOpen except <see cref="NxMessageBoxHelper"/>, and only on this same thread.</summary>
public sealed class WinFormsAssignmentChoiceWindow : IAssignmentChoiceWindow
{
    private readonly Action<string>? _logWarning;

    public WinFormsAssignmentChoiceWindow(Action<string>? logWarning = null) => _logWarning = logWarning;

    public AssignmentChoiceResult Resolve(AssignmentChoice choice)
    {
        try
        {
            using var form = new AssignmentChoiceForm(choice);

            var owner = NxMainWindow();
            var result = owner is null ? form.ShowDialog() : form.ShowDialog(owner);

            if (result != DialogResult.OK || form.PickedOptionId is not { } optionId)
                return AssignmentChoiceResult.Cancelled;

            return AssignmentChoiceResult.Picked(optionId, form.Warnings);
        }
        catch (Exception ex)
        {
            // Cancelling is the safe reading of "the window did not work": the caller aborts the assignment
            // rather than going ahead with a choice nobody made.
            _logWarning?.Invoke($"The '{choice.Title}' window could not be shown: {ex.Message}");
            NxMessageBoxHelper.ShowError(
                $"The '{choice.Title}' window could not be shown, so nothing was applied." +
                $"{Environment.NewLine}{ex.Message}");

            return AssignmentChoiceResult.Cancelled;
        }
    }

    /// <summary>NX's main window, to own the modal so it cannot end up behind NX.
    ///
    /// Taken from the process rather than from NXOpen: neither NXOpen.UI nor UFUi exposes the handle in
    /// NX 2412 (checked against the installed assemblies), and this code runs in-process inside NX, so the
    /// process main window is NX's. Null when the handle comes back empty — a batch session, or NX still
    /// starting — and the form is then shown unowned rather than not at all.</summary>
    private IWin32Window? NxMainWindow()
    {
        try
        {
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            return handle == IntPtr.Zero ? null : new WindowHandle(handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _logWarning?.Invoke($"NX's main window could not be found, so the choice window is unowned: {ex.Message}");
            return null;
        }
    }

    private sealed class WindowHandle : IWin32Window
    {
        public WindowHandle(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }
}
