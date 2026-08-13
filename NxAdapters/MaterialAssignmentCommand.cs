using Core.Assignment;
using Core.Assignment.Rules;
using Core.MaterialLibrary;
using NXOpen;
using NxAdapters.Materials;
using NxAdapters.Ui;
using NxAdapters.Ui.MaterialPropDisplay;
using NxOpen.Foundation.Core.MaterialLibrary;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters;

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

        var planner = new MaterialAssignmentPlanner(new IMaterialAssignmentRule[]
        {
            new BlockRestrictedBodyTypeRule(),
            new RequireConfirmationOnReassignmentRule(),
            new ValidateCoatingDisplayMaterialRule(),
        });

        // SyncPhysicalPropertiesEffectRule is intentionally NOT registered: nothing executes
        // SYNC_PHYSICAL_PROPERTY instructions, so wiring it would only generate work ApplyPlan discards.
        // The rule and its tests are kept — add it back here alongside a matching executor in
        // PartMaterialService when physical property sync is wanted.
        var finalizer = new AssignmentPlanFinalizer(new IPostAssignmentEffectRule[]
        {
            new SyncCoatingDisplayMaterialEffectRule(),
        });

        // The Styler-generated dialog. Constructing it creates the BlockDialog from BlockUI.dlx, so the
        // accessor can be handed it straight away — it resolves its blocks later, from initialize_cb.
        var dialog = new BlockUI();
        var blocks = new BlockAccessor(dialog.TheDialog, bodyResolver, context.Log.Warn);
        var propertyWindow = new MaterialPropertyWindow(context.Log.Warn);
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
            dialog.Dispose();
        }
    }

    // NX asks the assembly whether it can be unloaded — implemented so the DLL unloads predictably during development.
    public static int GetUnloadOption(string dummy) => (int)Session.LibraryUnloadOption.Immediately;
}
