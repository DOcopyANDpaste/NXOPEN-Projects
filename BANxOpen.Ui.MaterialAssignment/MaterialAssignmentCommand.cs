using BANxOpen.Foundation.Core.Materials;
using BANxOpen.Foundation.Core.Materials.Assignment;
using BANxOpen.Foundation.Core.Materials.Library;
using NXOpen;
using BANxOpen.Foundation.NxAdapters.Materials;
using BANxOpen.Ui.MaterialAssignment;
using BANxOpen.Ui.MaterialAssignment.MaterialPropDisplay;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.Contracts.Materials;

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

        var bodyResolver = new BodyResolver(context);
        var displayMaterialHelper = new DisplayMaterialHelper(context);
        // Owns the only path that touches NX's own material library, which is slow — see the class doc for
        // why that happens lazily, per material, and only after asking.
        var physicalMaterials = new NxPhysicalMaterialSource(context);
        var partMaterialService = new PartMaterialService(context, bodyResolver, displayMaterialHelper, physicalMaterials);

        var libraryRepository = new FileSystemMaterialLibraryRepository(onWarning: context.Log.Warn);
        var libraryParser = new MaterialLibraryParser();
        var libraryLoader = new CachingMaterialLibraryLoader(libraryRepository, libraryParser);
        var categoryTreeBuilder = new MaterialCategoryTreeBuilder();

        // Which libraries are reserved for sheet metal bodies. Read from beside the library files so this dialog
        // and the bead dialog apply the same list. A broken file stops the dialog rather than being ignored.
        SheetMetalLibraries sheetMetalLibraries;
        var libraryRulesPath = SheetMetalLibraries.ResolvePath(libraryRepository.RootDirectory);
        try
        {
            sheetMetalLibraries = SheetMetalLibraries.Load(libraryRulesPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            UI.GetUI().NXMessageBox.Show("Material Assignment", NXMessageBox.DialogType.Error, ex.Message);
            return;
        }

        if (!sheetMetalLibraries.IsConfigured)
            context.Log.Info($"No {SheetMetalLibraries.FileName} at '{libraryRulesPath}'; sheet metal libraries are recognised by name.");

        // The shared baseline, so this dialog and any feature tool that assigns material enforce exactly
        // the same rules. See StandardMaterialRules for what is in each set and why
        // SyncPhysicalPropertiesEffectRule is deliberately left out.
        var planner = new MaterialAssignmentPlanner(StandardMaterialRules.Gates(sheetMetalLibraries: sheetMetalLibraries));
        var finalizer = new AssignmentPlanFinalizer(StandardMaterialRules.Effects());

        // The Styler-generated dialog. Constructing it creates the BlockDialog from BlockUI.dlx, so the
        // accessor can be handed it straight away — it resolves its blocks later, from initialize_cb.
        var dialog = new BlockUI();
        var blocks = new BlockAccessor(dialog.TheDialog, bodyResolver, context.Log.Warn);
        // var propertyWindow = new MaterialPropertyWindow(context.Log.Warn);
        var propertyWindow = new WinFormsMaterialPropertyWindow(context.Log.Warn);
        var presenter = new MaterialAssignmentDialogPresenter(
            context,
            blocks,
            partMaterialService,
            libraryRepository,
            libraryLoader,
            categoryTreeBuilder,
            planner,
            finalizer,
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
