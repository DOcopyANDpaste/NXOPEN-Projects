using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Materials.Core;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>The text that exists because of this dialog's design: menus, column titles, tree statuses, the staging and
/// apply messages, and the dialog's own errors and prompts. Change a wording here and rebuild; keep the <c>{…}</c>
/// placeholders, which are the values the text is filled with.
///
/// Not here, by design (see the nxopen-csharp-patterns skill, common.md §8):
/// <list type="bullet">
/// <item>What a rule, check or service says — rule outcomes, the Sheet Metal Preferences check, part-service and
/// configuration failures — is worded by its library's describer (<c>MaterialsText</c>, <c>SheetMetalText</c>,
/// <c>FoundationText</c>) and shown as-is, so every tool says it the same way.</item>
/// <item>Message box titles, "could not start", the unexpected-error message and NX error formatting are
/// <see cref="NxDialogText"/>'s, shared by every NX dialog.</item>
/// <item>Log lines are not user text.</item>
/// </list></summary>
internal static class UiText
{
    private static readonly string Nl = Environment.NewLine;
    private static readonly string Para = Environment.NewLine + Environment.NewLine;

    // ---- Message box titles (the generic ones are NxDialogText's) ----

    public static class Titles
    {
        public const string Dialog = "Material Assignment";
        public const string SheetMetalPreferences = "Sheet Metal Preferences";
    }

    // ---- Context menus ----

    public static class Menu
    {
        public const string AddToPending = "Add to pending";
        public const string ApplySheetMetalPreference = "Apply sheet metal preference";
        public const string Properties = "Properties...";
        public const string Refresh = "Refresh";
        public const string SelectBodies = "Select these bodies";
        public const string AddToBodySelection = "Add all to body selection";
        public const string RemoveMaterial = "Remove material";
        public const string RemoveFromPending = "Remove from pending";
        public const string ClearAllPending = "Clear all pending";
    }

    // ---- Tree columns, labels and placeholders ----

    public static class Columns
    {
        public const string Material = "Material";
        public const string Description = "Description";
        public const string Density = "Density";

        public const string MaterialOrBody = "Material / Body";
        public const string BodiesOrKind = "Bodies / Kind";
        public const string DisplayMaterial = "Display material";
        public const string Pending = "Pending";
        public const string SheetMetalGrade = "SheetMetal Grade";

        public const string Kind = "Kind";
        public const string Status = "Status";

        // Material property window.
        public const string Property = "Property";
        public const string Value = "Value";
        public const string Unit = "Unit";
        public const string PropertiesSection = "Properties";
    }

    public static class Labels
    {
        /// <summary>A material label whose tree has nothing selected.</summary>
        public const string NoMaterialSelected = "(no material selected)";

        /// <summary>The "no filter" member of every Sheet Metal Preferences filter dropdown.</summary>
        public const string AnyFilter = "(Any)";

        /// <summary>The current-assignment tree's group for bodies with no physical material.</summary>
        public const string Unassigned = "(No material assigned)";

        /// <summary>The pending tree's entry for bodies whose material is to be removed.</summary>
        public const string RemoveMaterialEntry = "Remove material";

        /// <summary>A material with the sheet metal row picked with it.</summary>
        public static string MaterialWithRow(string material, string row) => $"{material} [{row}]";

        public static string SheetMetalMaterialMadeOf(string material) => $"Sheet metal material made of {material}";

        public const string CategorySeparator = " > ";
    }

    // ---- Pending tree Status column, and the Current tab's Pending / SheetMetal Grade columns ----

    public static class Status
    {
        public const string Pending = "Pending";
        public const string PendingConfirmed = "Pending — confirmed";
        public static string WithNote(string status, string note) => $"{status}: {note}";
        public static string PendingRemoval(string? currentMaterial) =>
            currentMaterial is null ? Pending : $"Pending — currently {currentMaterial}";
        public static string NotApplied(string? reason) => $"Not applied — {reason ?? "unknown reason"}";

        public static string FlagAssign(string entryName) => $"→ {entryName}";
        public const string FlagRemove = "→ (remove material)";
        public const string FlagConfirmedSuffix = " (confirmed)";
        public const string FlagNotAppliedSuffix = " (not applied)";
        public static string GroupPendingCount(int count) => $"{count} pending";

        public const string GradeNotSet = "(not set)";
        public static string GradeMaterialMismatch(string row) => $"{row} (material mismatch)";
        public static string GradeThicknessMismatch(string row) => $"{row} (thickness mismatch)";
        public static string GradeNotInTable(string row) => $"{row} (not in standards file)";
    }

    // ---- Why a body was left out (one line per body in messages, and the reason a pending row shows) ----

    public static class Reasons
    {
        public static string BodyLine(string body, string text) => $"  [{body}] {text}";

        public static string Blocked(string? ruleMessage) => $"blocked: {ruleMessage}";
        public const string NotConfirmed = "not confirmed";
        public const string SkippedNotConfirmed = "skipped: not confirmed";
        public const string ConflictsWithSheetMetal = "conflicts with another sheet metal body's material";
        public const string NoLongerInPart = "no longer in the work part";
        public static string AlreadyHas(string material) => $"already has {material}";
        public const string EarlierPendingChangeRemovedSuffix = "; its earlier pending change was removed";
        public const string HasNoMaterial = "has no material";
        public const string PendingRemovalReplaced = "its pending removal was replaced";
        public static string PendingAssignmentReplaced(string entryName) => $"its pending assignment to {entryName} was replaced";
        public static string Warning(string? message) => $"warning: {message}";

        /// <summary>Why NX left one body unchanged, short enough for its row in the pending tree: the library's own
        /// message on one line, or — for a partial failure, whose message is about the whole operation — that this body
        /// was one that failed.</summary>
        public static string FailedOnBody(OperationResult result) =>
            result.ErrorCode == MaterialsCodes.PartialFailure
                ? "NX failed on this body (see the Listing Window)"
                : string.IsNullOrWhiteSpace(result.Message)
                    ? NxDialogText.Default.NxRefusedTheChange(null)
                    : string.Join(" ", result.Message!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- Errors ----

    public static class Errors
    {
        public static string NothingPending(bool sheetMetalLibrary) =>
            $"Nothing is pending. Select bodies, then right-click a material and choose \"{Menu.AddToPending}\"." +
            (sheetMetalLibrary
                ? $"{Para}For a sheet metal library you can also pick a sheet metal material (a row under a physical " +
                  "material) and Apply it."
                : string.Empty);

        public const string SelectBodiesFirst = "Select at least one body before assigning a material.";
        public const string SelectionNotInPart = "The selected bodies are no longer in the work part. Refresh and try again.";
        public const string EverySolidHasMaterial = "Every solid body in this part already has a material.";
        public const string NoSolidBodyInRows = "None of the selected rows have a solid body.";
        public const string SelectBodiesToRemove = "Select at least one body to remove its material.";

        public static string SheetMetalNeedsRow(string material) =>
            $"Sheet metal bodies need a sheet metal material, not just '{material}'.{Para}" +
            $"Expand '{material}' in the material tree and pick one of its rows (Standard, thickness, bend radius) — that " +
            "is what the part's Sheet Metal Preferences are set to. Use the Sheet Metal Preferences filters to narrow the rows.";

        public static string NoSolidSelectedForRow(string row) =>
            $"No solid body is selected. This part has bodies, so '{row}' has to be assigned to them: the Sheet Metal " +
            $"Preferences are set together with the bodies' material, which keeps the two in sync.{Para}" +
            "Select the bodies, then Apply again.";

        public static string NotAddedToPending(string entryName, string details) =>
            $"{entryName} was not added to pending.{Para}{details}";

        public static string StillPending(string report) =>
            $"Some pending changes were not applied and are still pending:{Para}{report}";

        public const string PropertyWindowUnavailable = "The material property window is not available.";
        public static string PropertyWindowFailed(string detail) =>
            $"The material property window could not be opened.{Nl}{detail}";
        public const string PropertyWindowDlxMissing = "Check that MaterialDisplay_UIBlock.dlx is on the dialog search path.";
        public const string PropertyWindowInitFailed = "The material property window could not be initialized.";
    }

    // ---- Information ----

    public static class Info
    {
        public static string AddedToPending(string entryName, int bodyCount, string details) =>
            $"{entryName} added to pending for {bodyCount} body(ies).{Para}{details}";

        public static string NothingAddedToPending(string details) => $"Nothing to add to pending.{Para}{details}";

        public static string RemovalAddedToPending(int bodyCount, string details) =>
            $"Material removal added to pending for {bodyCount} body(ies).{Para}{details}";

        public static string NothingToRemove(string details) => $"No material to remove.{Para}{details}";

        public static string AppliedWithNotes(string report) =>
            $"The pending changes were applied, with these notes:{Para}{report}";

        public static string SheetMetalPreferencesSet(string row, string rowDescription) =>
            $"Sheet Metal Preferences set to '{row}' ({rowDescription}).";

        // Section headers inside the messages above.
        public const string NotAddedHeader = "Not added to pending:";
        public const string ReplacedHeader = "Replaced:";
        public const string WarningsHeader = "Warnings:";
        public static string Section(string header, IEnumerable<string> lines) => $"{header}{Nl}{string.Join(Nl, lines)}";
        public static string EntryReport(string entryName, IEnumerable<string> lines) => $"{entryName}:{Nl}{string.Join(Nl, lines)}";
        public static string JoinSections(IEnumerable<string> sections) => string.Join(Para, sections);
    }

    // ---- Yes/No questions ----

    public static class Confirm
    {
        public static string DiscardPending(int count) =>
            $"{count} pending change(s) were never applied and will be discarded.{Para}Close anyway?";

        public static string ConfirmBodies(int count, string material, IEnumerable<string> bodyLines) =>
            $"{count} body(ies) need confirmation for {material}:{Nl}{string.Join(Nl, bodyLines)}{Para}Apply to all of them?";

        public static string SetPreferencesOnly(string row, string rowDescription) =>
            $"This part has no solid body, so only its Sheet Metal Preferences will be set to '{row}' ({rowDescription})." +
            $"{Para}Continue?";
    }

    // ---- Sheet metal: this dialog's own logic (pending-aware conflict check, the sync hint on open) ----

    public static class SheetMetal
    {
        public static string Conflict(string name, IEnumerable<string> bodyLines) =>
            $"{name} cannot be assigned to sheet metal bodies in this part.{Para}" +
            "Sheet Metal Preferences belong to the part, so every sheet metal body in it must have the same material and " +
            $"preferences row, and these already have a different one:{Nl}{string.Join(Nl, bodyLines)}{Para}" +
            $"Nothing was assigned. Assign {name} to all of the sheet metal bodies together, or leave the sheet metal " +
            "bodies out of the selection.";

        public static string ConflictLine(string body, string material, bool pending) =>
            $"  [{body}] {material}{(pending ? " (pending)" : string.Empty)}";

        public static string SyncHint(string row, string rowDescription, bool autoSelected) => autoSelected
            ? $"The body was added to the selection. Pick '{row}' ({rowDescription}) in the material list and Apply to sync them."
            : $"Select the bodies, pick '{row}' ({rowDescription}) in the material list and Apply to sync them.";
    }
}
