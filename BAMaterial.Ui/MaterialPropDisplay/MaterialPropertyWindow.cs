using NXOpen;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.Foundation.NxAdapters;

// Disambiguated from NXOpen.Material, which `using NXOpen` above also brings into scope.
using Material = BANxOpen.Foundation.Contracts.Materials.Material;

namespace BAMaterial.Ui.MaterialPropDisplay;

/// <summary>The full material-property popup, opened from the material tree (double-click, or right-click →
/// Properties).
///
/// <c>MaterialDisplay_UIBlock.dlx</c> doubles as this popup: it is authored as a user-defined UI block so it
/// can also be embedded elsewhere, but it carries its own OK/Cancel navigation, so it is launched
/// directly rather than being registered into a separate host dialog. No Apply button: both OK and Cancel
/// close the popup and return here (NXOpen closes a BlockDialog by default on either), which Apply cannot
/// do by design — see the note on <see cref="MaterialDisplay_UIBlock"/>.
///
/// A fresh instance per invocation, disposed on close — the popup is modal and short-lived, and holding one
/// open across invocations would carry a previous material's tree contents into the next one.
///
/// This launches a second, unparented top-level <see cref="NXOpen.BlockStyler.BlockDialog"/> from inside a
/// tree callback of the main dialog's own still-running <c>Launch()</c> — NXOpen's <c>UI.CreateDialog</c> has
/// no way to declare an owner relationship between the two. If tracing ever shows the main dialog's
/// <c>Launch()</c> returning unexpectedly during/after this popup's Cancel, stop launching
/// <see cref="MaterialDisplay_UIBlock"/> as an independent dialog and register it as a child block into the
/// main dialog instead, via the already-present but unused
/// <see cref="MaterialDisplay_UIBlock.RegisterUserDefinedUIBlock"/>.</summary>
public sealed class MaterialPropertyWindow : IMaterialPropertyWindow
{
    private readonly Action<string>? _logWarning;

    public MaterialPropertyWindow(Action<string>? logWarning = null) => _logWarning = logWarning;

    public void Show(Material material)
    {
        MaterialDisplay_UIBlock? block = null;

        try
        {
            block = new MaterialDisplay_UIBlock();

            // TopBlock's contents only exist once the dialog builds them, so both the column setup and the
            // population have to wait for dialog-shown rather than running straight after construction.
            block.DialogShown = () =>
            {
                var accessor = new MaterialPropDisplayAccessor(block.TopBlock, _logWarning);
                accessor.SetUpColumns();
                accessor.Show(material);
            };

            _logWarning?.Invoke($"TRACE MaterialPropertyWindow.Show: launching popup for '{material.Name}'.");
            var response = block.Show();
            _logWarning?.Invoke($"TRACE MaterialPropertyWindow.Show: popup closed with response '{response}'.");
        }
        catch (Exception ex)
        {
            // Report rather than let an NX exception escape into the calling tree callback, which would
            // cross the managed boundary and destabilise the session.
            _logWarning?.Invoke($"Material property window could not be opened: {ex.Message}");
            NxMessageBoxHelper.ShowError(
                $"The material property window could not be opened.{Environment.NewLine}" +
                "Check that MaterialDisplay_UIBlock.dlx is on the dialog search path.");
        }
        finally
        {
            block?.Dispose();
        }
    }
}
