using System.Threading;
using System.Windows.Forms;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Ui.MaterialPropDisplay;

/// <summary>WinForms replacement for <see cref="MaterialPropertyWindow"/> (kept, unused, for rollback).
///
/// NXOpen's Block UI Styler only supports one dialog thread/modal at a time, so a second BlockDialog
/// launched from inside the main dialog's still-running modal Launch() call has no owner relationship to
/// it and is fragile. This implementation instead hosts non-modal <see cref="MaterialPropertyForm"/>
/// windows on a single dedicated background STA thread with its own WinForms message pump, entirely
/// separate from NX's own thread and dialog machinery — any number of popups (for different materials)
/// can be open simultaneously.
///
/// Thread-safety contract: nothing on the pump thread may call into NXOpen. Only the plain immutable
/// <see cref="Material"/> record (already fully resolved, no NXOpen types) crosses onto it. Any error that
/// needs surfacing via <see cref="NxMessageBoxHelper"/> — an NXOpen call — must happen on the caller's own
/// thread (NX's own thread), never inside a delegate posted to the pump thread.</summary>
public sealed class WinFormsMaterialPropertyWindow : IMaterialPropertyWindow, IDisposable
{
    private readonly Action<string>? _logWarning;
    private readonly object _startLock = new();
    private readonly ManualResetEventSlim _pumpReady = new(initialState: false);
    private readonly List<MaterialPropertyForm> _openForms = new(); // touched only on the pump thread

    private Thread? _pumpThread;
    private SynchronizationContext? _pumpSyncContext;
    private volatile bool _pumpStartFailed;

    public WinFormsMaterialPropertyWindow(Action<string>? logWarning = null) => _logWarning = logWarning;

    public void Show(Material material)
    {
        try
        {
            EnsurePumpStarted();
            if (_pumpSyncContext is null)
                return; // EnsurePumpStarted already reported the failure

            _logWarning?.Invoke($"TRACE WinFormsMaterialPropertyWindow.Show: posting popup for '{material.Name}'.");

            _pumpSyncContext.Post(_ =>
            {
                // Runs on the pump thread. Must never touch NXOpen.
                try
                {
                    var form = new MaterialPropertyForm(material);
                    form.FormClosed += (_, _) => _openForms.Remove(form);
                    _openForms.Add(form);
                    form.Show();
                }
                catch
                {
                    // A single popup failing must not take down the shared pump thread; no NX-safe
                    // reporting channel is reachable from this thread.
                }
            }, state: null);
        }
        catch (Exception ex)
        {
            // Still on the caller's (NX) thread here — safe to use NxMessageBoxHelper.
            _logWarning?.Invoke($"Material property window could not be opened: {ex.Message}");
            NxMessageBoxHelper.ShowError(
                $"The material property window could not be opened.{Environment.NewLine}{ex.Message}");
        }
    }

    /// <summary>Closes every open popup and stops the pump thread. Called from the main dialog's shutdown
    /// path so popups don't outlive it — see <c>MaterialAssignmentCommand.Main</c>.</summary>
    public void Dispose()
    {
        if (_pumpSyncContext is null)
            return; // pump never started; nothing to close

        _pumpSyncContext.Send(_ =>
        {
            foreach (var form in _openForms.ToArray())
                form.Close();
            Application.ExitThread();
        }, state: null);
    }

    private void EnsurePumpStarted()
    {
        if (_pumpSyncContext is not null || _pumpStartFailed)
            return;

        lock (_startLock)
        {
            if (_pumpThread is not null)
                return; // another call already started it (or is starting it) while we waited on the lock

            _pumpThread = new Thread(PumpThreadBody)
            {
                IsBackground = true, // never blocks NX process exit
                Name = "MaterialPropertyWindowPump",
            };
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();

            // Wait for the thread to install its SynchronizationContext before the first Post.
            _pumpReady.Wait();

            if (_pumpStartFailed)
            {
                _logWarning?.Invoke("Material property window pump thread failed to start.");
                NxMessageBoxHelper.ShowError("The material property window could not be initialized.");
            }
        }
    }

    private void PumpThreadBody()
    {
        try
        {
            Application.EnableVisualStyles();

            var syncContext = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(syncContext);
            _pumpSyncContext = syncContext;
        }
        catch
        {
            _pumpStartFailed = true;
        }
        finally
        {
            _pumpReady.Set(); // unblock EnsurePumpStarted's Wait() either way
        }

        if (_pumpStartFailed)
            return;

        // Runs until Dispose() calls Application.ExitThread(); the thread then dies naturally.
        Application.Run();
    }
}
