using Core.Assignment;
using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NXOpen;
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
/// The material-tab tile grid (<see cref="PopulateMaterialTabs"/>) is the one section left intentionally
/// thin — dynamically creating a variable number of tab pages and a variable number of tiles per tab is
/// the single biggest open risk in this design (Block UI Styler layouts are normally a fixed, designer-
/// placed set of blocks). See the plan's §2 risk callout: this needs a spike on an NX-installed machine
/// to pick a real approach (true dynamic block instancing vs. a fixed hidden-block template vs. hosting a
/// native .NET control) before this method's body can be finalized.</summary>
public sealed class BlockAccessor
{
    // VERIFY: placeholder string IDs — must match the real .dlx once it exists. Internal (not private) so
    // MaterialAssignmentDialogPresenter.OnUpdate can switch on which block changed without duplicating
    // these literals — the single source of truth for block IDs stays this class, per with-block-ui.md §3.
    internal const string LibraryDropdownId = "libraryDropdown";
    internal const string MaterialTabControlId = "materialTabs";
    internal const string MaterialPropertyPanelId = "materialPropertyPanel";
    internal const string MaterialUsageTableId = "materialUsageTable";
    internal const string BodyDrilldownListId = "bodyDrilldownList";
    internal const string BodyKindFilterId = "bodyKindFilter";
    internal const string PlanSummaryId = "planSummary";

    private readonly BlockDialog _dialog;

    public BlockAccessor(BlockDialog dialog) => _dialog = dialog;

    // ---- Library dropdown ----

    public void PopulateLibraryDropdown(IReadOnlyList<MaterialLibraryReference> libraries)
    {
        // VERIFY: exact dropdown/enum-list block API — candidate is a PropertyList-backed
        // "ListItems"/"Options" write, mirroring the PropertyList.GetDouble/GetString pattern shown in
        // Skills/with-block-ui.md §3.
        var properties = _dialog.GetBlock(LibraryDropdownId).GetProperties();
        properties.SetStringArray("ListItems", libraries.Select(l => l.DisplayName).ToArray());
    }

    public MaterialLibraryId? GetSelectedLibraryId()
    {
        var properties = _dialog.GetBlock(LibraryDropdownId).GetProperties();
        var selected = properties.GetString("SelectedValue");
        return string.IsNullOrEmpty(selected) ? null : new MaterialLibraryId(selected);
    }

    // ---- Material tab control (tiles) ----

    /// <summary>Dynamically (re)builds one tab page per <see cref="MaterialTab.Category"/> and, within
    /// each tab, one thumbnail+name tile per <see cref="Material"/> — see the risk callout on this class.
    /// NOT IMPLEMENTED pending the dynamic-population spike; left as a clear seam (correct signature,
    /// called from the right place in the presenter) rather than a fabricated guess at an API surface
    /// this uncertain.</summary>
    public void PopulateMaterialTabs(IReadOnlyList<MaterialTab> tabs) =>
        throw new NotImplementedException(
            "VERIFY/SPIKE: resolve the dynamic tab/tile population approach on an NX-installed machine " +
            "(see the plan's §2 risk callout) before implementing this method.");

    public MaterialId? GetSelectedMaterialId() =>
        throw new NotImplementedException("Depends on PopulateMaterialTabs' approach — see spike note above.");

    public void ShowMaterialProperties(Material material)
    {
        // VERIFY: exact read-only rows/grid block API for the property preview panel.
        var properties = _dialog.GetBlock(MaterialPropertyPanelId).GetProperties();
        var rows = material.Properties
            .Select(p => $"{p.Name}: {p.AsString()}{(string.IsNullOrEmpty(p.Unit) ? "" : $" {p.Unit}")}")
            .ToArray();
        properties.SetStringArray("Rows", rows);
    }

    private static string? TryGetThumbnailPath(Material material)
    {
        // VERIFY: candidate PropertyId/Name keys — the exact key NX's shipped MatML libraries use for a
        // material's thumbnail/photo reference is unconfirmed; no real library XML sample was available.
        string[] candidateKeys = { "Image", "Photo", "Thumbnail" };
        return material.Properties
            .FirstOrDefault(p => candidateKeys.Any(key => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                               || candidateKeys.Any(key => string.Equals(p.PropertyId, key, StringComparison.OrdinalIgnoreCase)))
            ?.AsString();
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
        properties.SetStringArray("ListItems", bodies.Select(b => b.Name).ToArray());
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
        // VERIFY: exact read-only text/list block API for blocking/confirmation/warning messages, plus
        // whatever per-body checkbox control backs GetConfirmedBodyIds() below.
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

    public HashSet<BodyId> GetConfirmedBodyIds()
    {
        // VERIFY: exact per-body confirmation-checkbox read. Placeholder returns an empty set (nothing
        // confirmed) until the real checkbox block exists — safe default, since an unconfirmed body is
        // simply skipped (partial-apply), never silently applied.
        return new HashSet<BodyId>();
    }

    // ---- Generic dialogs ----
    // Forwards to the shared NxOpen.Foundation.NxAdapters.NxMessageBoxHelper — these three have no
    // dependency on this dialog's blocks or domain types, so the implementation lives once in the
    // foundation instead of being duplicated per project (see area A of the reuse plan).

    public bool Confirm(string message) => NxMessageBoxHelper.Confirm(message);

    public void ShowResult(OperationResult result) =>
        NxMessageBoxHelper.ShowResult(result, "Material assignment applied.");

    public void ShowError(string message) => NxMessageBoxHelper.ShowError(message);
}
