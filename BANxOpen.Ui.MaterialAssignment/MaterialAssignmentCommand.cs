using BANxOpen.Foundation.Core.Materials.Library;
using NXOpen;
using BANxOpen.Foundation.NxAdapters.Materials;
using BANxOpen.Ui.MaterialAssignment;
using BANxOpen.Ui.MaterialAssignment.AssignmentChoiceUi;
using BANxOpen.Ui.MaterialAssignment.MaterialPropDisplay;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.SheetMetal.NxAdapters.Common;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>Entry point NX invokes from a MenuScript/ribbon action, per Skills/without-block-ui.md §1.
/// The only class that knows every layer — composes the whole dependency graph once per launch and shows
/// the dialog. Keep this thin: wiring only, no business logic.</summary>
public static class MaterialAssignmentCommand
{
    public static void Main(string[] args)
    {
        if (!NxSessionContext.TryInitialize(out var context, out var failureReason))
        {
            UI.GetUI().NXMessageBox.Show(
                "Material Assignment", NXMessageBox.DialogType.Error, failureReason ?? "Could not start.");
            return;
        }

        // Feature domains that restrict what material a body may take, given what is already built on it, and that
        // carry an assignment through to their own part state (sheet metal: bead SPECs and the Sheet Metal Preferences).
        //
        // Sheet metal config that cannot be loaded stops the dialog rather than dropping the bead rules: a
        // dialog that silently stopped enforcing SPEC restrictions would assign forbidden materials to beaded
        // parts, which is exactly what these rules exist to prevent.
        var sheetMetal = SheetMetalServices.Create(context);
        if (!sheetMetal.Ok)
        {
            UI.GetUI().NXMessageBox.Show(
                "Material Assignment", NXMessageBox.DialogType.Error,
                sheetMetal.Message ?? "Sheet metal configuration could not be loaded.");
            return;
        }

        var sheetMetalServices = sheetMetal.Value!;

        var bodyResolver = new BodyResolver(context);
        var libraryRepository = new FileSystemMaterialLibraryRepository(onWarning: context.Log.Warn);
        var libraryParser = new MaterialLibraryParser();
        var libraryLoader = new CachingMaterialLibraryLoader(libraryRepository, libraryParser);
        var categoryTreeBuilder = new MaterialCategoryTreeBuilder();

        // The shared material engine: the baseline rule modules plus each feature domain's, so this dialog and every
        // feature tool that assigns material enforce exactly the same rules. Add further domains' modules here.
        var engine = MaterialEngine.Create(
            context, bodyResolver, libraryRepository.RootDirectory, sheetMetalServices.MaterialModules);
        if (!engine.Ok)
        {
            UI.GetUI().NXMessageBox.Show(
                "Material Assignment", NXMessageBox.DialogType.Error, engine.Message ?? "Material rules could not be loaded.");
            return;
        }

        var materials = engine.Value!;

        // The Styler-generated dialog. Constructing it creates the BlockDialog from BlockUI.dlx, so the
        // accessor can be handed it straight away — it resolves its blocks later, from initialize_cb.
        var dialog = new BlockUI();
        var blocks = new BlockAccessor(dialog.TheDialog, bodyResolver, context.Log.Warn);
        // var propertyWindow = new MaterialPropertyWindow(context.Log.Warn);
        var propertyWindow = new WinFormsMaterialPropertyWindow(context.Log.Warn);

        // The questions the rules need answered before their side effects can run — for sheet metal, which
        // standards file row the part's Sheet Metal Preferences should be set to. The collector and the window
        // are handed over together: a dialog with one and not the other would either ask nothing and have its
        // bodies skipped, or collect questions it could not put to anyone.
        var choiceWindow = new WinFormsAssignmentChoiceWindow(context.Log.Warn);

        var presenter = new MaterialAssignmentDialogPresenter(
            context,
            blocks,
            materials.PartMaterials,
            libraryRepository,
            libraryLoader,
            categoryTreeBuilder,
            materials.Rules.CreatePlanner(),
            materials.Rules.CreateFinalizer(),
            materials.Rules.CreateChoiceCollector(),
            choiceWindow,
            sheetMetalServices.StandardSelection,
            materials.SheetMetalLibraries,
            propertyWindow);

        dialog.Presenter = presenter;
        try
        {
            dialog.Show();
        }
        finally
        {
            propertyWindow.Dispose(); // closes any still-open property popups before the main dialog goes away
            dialog.Dispose();
        }
    }

    // NX asks the assembly whether it can be unloaded — implemented so the DLL unloads predictably during development.
    public static int GetUnloadOption(string dummy) => (int)Session.LibraryUnloadOption.Immediately;
}
