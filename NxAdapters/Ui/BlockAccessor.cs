using Core.Assignment;
using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NXOpen.BlockStyler;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Ui;

/// <summary>All <c>dialog.GetBlock("stringId")</c> lookups and typed block reads/writes live here, per
/// Skills/with-block-ui.md §3 — when the Styler regenerates and renames/reorders block fields, only this
/// file changes. String IDs below are PLACEHOLDERS: the real <c>.dlx</c> doesn't exist yet (no NX install
/// in this session), so these will need to match whatever the Styler tool actually names each block once
/// the dialog is laid out.
///
/// Two conventions run through this class. First, every "get selection" method reads only an index (or
/// index array) from the block and maps it back through a <c>LastPopulated*</c> list, so no domain value
/// is ever reconstructed by parsing block text. Second, populating a list clears its selection, so a
/// stale index can never resolve against freshly-swapped contents.
///
/// The material browser is currently a category dropdown + flat material list.
/// <see cref="PopulateMaterialTabs"/> is the seam for the richer tabbed tile grid, which needs an
/// NX-install spike first — see its doc comment.</summary>
public sealed class BlockAccessor
{
    // VERIFY: placeholder string IDs — must match the real .dlx once it exists. Internal (not private) so
    // MaterialAssignmentDialogPresenter.OnUpdate can switch on which block changed without duplicating
    // these literals — the single source of truth for block IDs stays this class, per with-block-ui.md §3.
    internal const string LibraryDropdownId = "libraryDropdown";
    internal const string CategoryDropdownId = "categoryDropdown";
    internal const string MaterialListId = "materialList";
    internal const string MaterialTabControlId = "materialTabs";
    internal const string MaterialPropertyPanelId = "materialPropertyPanel";
    internal const string MaterialUsageTableId = "materialUsageTable";
    internal const string BodyDrilldownListId = "bodyDrilldownList";
    internal const string BodyKindFilterId = "bodyKindFilter";
    internal const string PlanSummaryId = "planSummary";
    internal const string SelectAllButtonId = "selectAllButton";
    internal const string SelectUnassignedButtonId = "selectUnassignedButton";
    internal const string RemoveButtonId = "removeButton";
    internal const string RefreshButtonId = "refreshButton";

    private readonly BlockDialog _dialog;
    private readonly Action<string>? _logWarning;

    /// <param name="logWarning">Warning sink — takes a plain delegate rather than a concrete NX logger
    /// so this class stays independent of the session context, matching the same seam
    /// <c>FileSystemMaterialLibraryRepository</c> uses. Callers typically pass <c>context.Log.Warn</c>.</param>
    public BlockAccessor(BlockDialog dialog, Action<string>? logWarning = null)
    {
        _dialog = dialog;
        _logWarning = logWarning;
    }

    // ---- Library dropdown ----

    public void PopulateLibraryDropdown(IReadOnlyList<MaterialLibraryReference> libraries)
    {
        // VERIFY: exact dropdown/enum-list block API — candidate is a PropertyList-backed
        // "ListItems"/"Options" write, mirroring the PropertyList.GetDouble/GetString pattern shown in
        // Skills/with-block-ui.md §3.
        var properties = _dialog.GetBlock(LibraryDropdownId).GetProperties();
        properties.SetStringArray("ListItems", libraries.Select(l => l.DisplayName).ToArray());
        LastPopulatedLibraries = libraries;
    }

    private IReadOnlyList<MaterialLibraryReference>? LastPopulatedLibraries { get; set; }

    /// <summary>Maps the selected row back to the library it was populated from. Deliberately not built
    /// by wrapping the block's selected text in a <see cref="MaterialLibraryId"/> — display name and id
    /// are separate fields, and only happen to coincide for the filesystem repository.</summary>
    public MaterialLibraryId? GetSelectedLibraryId()
    {
        if (LastPopulatedLibraries is null)
            return null;

        var properties = _dialog.GetBlock(LibraryDropdownId).GetProperties();
        var index = properties.GetInt("SelectedIndex");
        return index >= 0 && index < LastPopulatedLibraries.Count
            ? LastPopulatedLibraries[index].Id
            : null;
    }

    // ---- Material browser: category dropdown + material list ----

    public void PopulateCategoryDropdown(IReadOnlyList<MaterialTab> tabs)
    {
        var properties = _dialog.GetBlock(CategoryDropdownId).GetProperties();
        properties.SetStringArray("ListItems", tabs.Select(t => t.Category.DisplayName).ToArray());
        properties.SetInt("SelectedIndex", tabs.Count > 0 ? 0 : -1);
    }

    public int GetSelectedCategoryIndex()
    {
        var properties = _dialog.GetBlock(CategoryDropdownId).GetProperties();
        return properties.GetInt("SelectedIndex");
    }

    public void PopulateMaterialList(IReadOnlyList<Material> materials)
    {
        var properties = _dialog.GetBlock(MaterialListId).GetProperties();
        properties.SetStringArray("ListItems", materials.Select(m => m.Name).ToArray());
        properties.SetInt("SelectedIndex", -1);
        LastPopulatedMaterials = materials;
    }

    private IReadOnlyList<Material>? LastPopulatedMaterials { get; set; }

    public MaterialId? GetSelectedMaterialId()
    {
        if (LastPopulatedMaterials is null)
            return null;

        var properties = _dialog.GetBlock(MaterialListId).GetProperties();
        var index = properties.GetInt("SelectedIndex");
        return index >= 0 && index < LastPopulatedMaterials.Count
            ? LastPopulatedMaterials[index].Id
            : null;
    }

    /// <summary>Seam for the richer view: one tab page per <see cref="MaterialTab.Category"/> and, within
    /// each tab, one thumbnail+name tile per <see cref="Material"/>. Dynamically creating a variable
    /// number of tab pages and tiles is the single biggest open risk in this design (Block UI Styler
    /// layouts are normally a fixed, designer-placed set of blocks), so it needs a spike on an
    /// NX-installed machine to pick an approach — true dynamic block instancing vs. a fixed hidden-block
    /// template vs. hosting a native .NET control.
    ///
    /// A no-op until then, not a throw: the category dropdown and material list above already give the
    /// user a working picker off the same <see cref="MaterialTab"/> data, so failing here would take down
    /// a dialog that is otherwise fully functional.</summary>
    public void PopulateMaterialTabs(IReadOnlyList<MaterialTab> tabs) =>
        _logWarning?.Invoke(
            $"Tabbed material tile grid not implemented (pending NX-install spike) — {tabs.Count} " +
            "category tab(s) not rendered; using the category dropdown and material list instead.");

    public void ShowMaterialProperties(Material material)
    {
        // VERIFY: exact read-only rows/grid block API for the property preview panel.
        var properties = _dialog.GetBlock(MaterialPropertyPanelId).GetProperties();
        var rows = material.Properties
            .Select(p => $"{p.Name}: {p.AsString()}{(string.IsNullOrEmpty(p.Unit) ? "" : $" {p.Unit}")}")
            .ToArray();
        properties.SetStringArray("Rows", rows);
    }

    // ---- Material usage table + body drill-down ----

    public void PopulateMaterialUsageTable(IReadOnlyList<MaterialUsageRow> rows)
    {
        // VERIFY: exact table/grid block API — candidate is a two-column string-array write (label, count).
        var properties = _dialog.GetBlock(MaterialUsageTableId).GetProperties();
        properties.SetStringArray("Column0", rows.Select(r => r.MaterialLabel).ToArray());
        properties.SetStringArray("Column1", rows.Select(r => r.BodyCount.ToString()).ToArray());
    }

    public MaterialUsageRow? GetSelectedMaterialUsageRow()
    {
        // VERIFY: exact "selected row index" read — the row itself is re-derived by the presenter (which
        // holds the last-populated MaterialUsageRow[] it can index into), not reconstructed from the block.
        var properties = _dialog.GetBlock(MaterialUsageTableId).GetProperties();
        var index = properties.GetInt("SelectedRow");
        return index < 0 ? null : LastPopulatedUsageRows?.ElementAtOrDefault(index);
    }

    /// <summary>Set by the presenter immediately after <see cref="PopulateMaterialUsageTable"/>, so
    /// <see cref="GetSelectedMaterialUsageRow"/> can map a selected row index back to data without this
    /// class needing to parse it back out of block text.</summary>
    public IReadOnlyList<MaterialUsageRow>? LastPopulatedUsageRows { get; set; }

    public void PopulateBodyDrilldownList(IReadOnlyList<BodyInfo> bodies)
    {
        var properties = _dialog.GetBlock(BodyDrilldownListId).GetProperties();
        properties.SetStringArray("ListItems", bodies.Select(b => $"{b.Name} [{b.Kind}] ({b.Id})").ToArray());

        // Clear selection before swapping the backing list: indices left over from the previously shown
        // set would otherwise resolve against the new one, letting Apply/Remove hit bodies the user never
        // picked — possibly under a different material row entirely.
        properties.SetIntArray("SelectedIndices", Array.Empty<int>());
        LastPopulatedDrilldownBodies = bodies;
    }

    /// <summary>Same pattern as <see cref="LastPopulatedUsageRows"/> — the presenter's last-populated list,
    /// used to map selected list-box indices back to <see cref="BodyId"/>s.</summary>
    public IReadOnlyList<BodyInfo>? LastPopulatedDrilldownBodies { get; private set; }

    public IReadOnlyList<BodyId> GetSelectedDrilldownBodyIds()
    {
        if (LastPopulatedDrilldownBodies is null)
            return Array.Empty<BodyId>();

        // VERIFY: exact multi-select list-box "selected indices" read.
        var properties = _dialog.GetBlock(BodyDrilldownListId).GetProperties();
        var indices = properties.GetIntArray("SelectedIndices") ?? Array.Empty<int>();
        return indices
            .Where(i => i >= 0 && i < LastPopulatedDrilldownBodies.Count)
            .Select(i => LastPopulatedDrilldownBodies[i].Id)
            .ToList();
    }

    public void SelectAllVisibleDrilldownBodies()
    {
        if (LastPopulatedDrilldownBodies is null)
            return;

        // VERIFY: exact multi-select list-box "select these indices" write.
        var properties = _dialog.GetBlock(BodyDrilldownListId).GetProperties();
        properties.SetIntArray("SelectedIndices", Enumerable.Range(0, LastPopulatedDrilldownBodies.Count).ToArray());
    }

    public void SetBodyKindFilter(BodyKind? filter)
    {
        var properties = _dialog.GetBlock(BodyKindFilterId).GetProperties();
        properties.SetString("SelectedValue", filter?.ToString() ?? "All");
    }

    public BodyKind? GetBodyKindFilter()
    {
        var properties = _dialog.GetBlock(BodyKindFilterId).GetProperties();
        var selected = properties.GetString("SelectedValue");
        return Enum.TryParse<BodyKind>(selected, out var kind) ? kind : null;
    }

    // ---- Plan review / confirmation ----

    public void ShowPlanSummary(AssignmentPlan plan)
    {
        // VERIFY: exact read-only text/list block API for blocking/confirmation/warning messages.
        var properties = _dialog.GetBlock(PlanSummaryId).GetProperties();
        var lines = plan.BodyEvaluations
            .SelectMany(evaluation => evaluation.RuleOutcomes
                .Where(o => o.Message is not null)
                .Select(o => $"[{evaluation.BodyId}] {o.Decision}: {o.Message}"))
            .ToArray();
        properties.SetStringArray("Rows", lines);
        LastPlan = plan;
    }

    private AssignmentPlan? LastPlan { get; set; }

    /// <summary>Asks the user to confirm the bodies whose rules returned RequireConfirmation — chiefly
    /// reassignment over an existing material, and coating display-material mismatches.
    ///
    /// All-or-nothing, driven by the plan last passed to <see cref="ShowPlanSummary"/>, because Block UI
    /// Styler has no per-row checkbox control to hang a per-body answer off. The finalizer's per-body
    /// partial-apply handling is untouched, so a real per-body control can replace this later without
    /// changing anything downstream. Returning an empty set on decline skips those bodies; the rest of
    /// the plan still applies.</summary>
    public HashSet<BodyId> GetConfirmedBodyIds()
    {
        var needingConfirmation = LastPlan?.BodyEvaluations.Where(e => e.RequiresConfirmation).ToList();
        if (needingConfirmation is null || needingConfirmation.Count == 0)
            return new HashSet<BodyId>();

        var details = needingConfirmation.SelectMany(evaluation => evaluation.ConfirmationOutcomes
            .Select(outcome => $"  [{evaluation.BodyId}] {outcome.Message}"));
        var message =
            $"{needingConfirmation.Count} body(ies) need confirmation:{Environment.NewLine}" +
            string.Join(Environment.NewLine, details) +
            $"{Environment.NewLine}{Environment.NewLine}Apply to all of them?";

        return Confirm(message)
            ? needingConfirmation.Select(e => e.BodyId).ToHashSet()
            : new HashSet<BodyId>();
    }

    // ---- Generic dialogs ----
    // Forwards to the shared NxOpen.Foundation.NxAdapters.NxMessageBoxHelper — these three have no
    // dependency on this dialog's blocks or domain types, so the implementation lives once in the
    // foundation instead of being duplicated per project (see area A of the reuse plan).

    public bool Confirm(string message) => NxMessageBoxHelper.Confirm(message);

    public void ShowResult(OperationResult result, string successMessage) =>
        NxMessageBoxHelper.ShowResult(result, successMessage);

    public void ShowError(string message) => NxMessageBoxHelper.ShowError(message);
}
