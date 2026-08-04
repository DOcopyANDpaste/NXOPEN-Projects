using Core.Assignment;
using Core.Assignment.Rules;
using Core.MaterialLibrary;
using NXOpen;
using NxAdapters.Materials;
using NxAdapters.Ui;
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
        var displayMaterialAssigner = new CoatingDisplayMaterialAssigner(context);
        var partMaterialService = new PartMaterialService(context, bodyResolver, displayMaterialAssigner);

        var libraryRepository = new FileSystemMaterialLibraryRepository(onWarning: context.Log.Warn);
        var libraryParser = new MaterialLibraryParser();
        var libraryLoader = new CachingMaterialLibraryLoader(libraryRepository, libraryParser);
        var tabGrouper = new MaterialTabGrouper();

        var planner = new MaterialAssignmentPlanner(new IMaterialAssignmentRule[]
        {
            new BlockRestrictedBodyTypeRule(),
            new RequireConfirmationOnReassignmentRule(),
            new ValidateCoatingDisplayMaterialRule(),
        });

        var finalizer = new AssignmentPlanFinalizer(new IPostAssignmentEffectRule[]
        {
            new SyncPhysicalPropertiesEffectRule(),
            // Still wired even though PartMaterialService has no executor for SYNC_PHYSICAL_PROPERTY yet
            // (deliberately deferred this round) — ApplyPlan logs and skips that instruction type rather
            // than failing, so this stays safe to include.
            new SyncCoatingDisplayMaterialEffectRule(),
        });

        // VERIFY: real Styler-generated dialog construction/launch — MaterialAssignmentDialog is itself a
        // conceptual placeholder (see its own doc comment) until the real .dlx exists.
        var dialog = new MaterialAssignmentDialog();
        var blocks = new BlockAccessor(dialog.TheDialog);
        var presenter = new MaterialAssignmentDialogPresenter(
            context, blocks, partMaterialService, libraryRepository, libraryLoader, tabGrouper, planner, finalizer);

        dialog.Presenter = presenter;
        dialog.Show();
    }

    // NX asks the assembly whether it can be unloaded — implemented so the DLL unloads predictably during development.
    public static int GetUnloadOption(string dummy) => (int)Session.LibraryUnloadOption.Immediately;
}
