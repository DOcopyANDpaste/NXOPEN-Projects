using Core.Assignment;
using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NxAdapters.Materials;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.MaterialLibrary;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Ui;

/// <summary>All dialog logic — the generated <c>MaterialAssignmentDialog.cs</c> stays a thin set of
/// delegations to this class, per Skills/with-block-ui.md §1.
///
/// Mode: INTERACTIVE for preview, MODAL-SINGLE-COMMIT for mutation (with-block-ui.md §5). Library/
/// material/usage-row/filter changes only read via Core (planner) and re-render via
/// <see cref="BlockAccessor"/> — zero NX mutation. All mutation is confined to <see cref="Commit"/>
/// (assign) and <see cref="OnClearMaterialClicked"/> (clear), each of which opens its own undo mark
/// inside <see cref="PartMaterialService"/>, never here.
///
/// On a library change this presenter is responsible for tearing down and rebuilding the tab pages and
/// per-tab tiles from the newly loaded library's <see cref="MaterialTab"/>s — see
/// <see cref="BlockAccessor.PopulateMaterialTabs"/>'s doc comment for why that method isn't implemented
/// yet (pending an NX-install spike on dynamic block population).</summary>
public sealed class MaterialAssignmentDialogPresenter
{
    private readonly NxSessionContext _context;
    private readonly BlockAccessor _blocks;
    private readonly PartMaterialService _partMaterialService;
    private readonly IMaterialLibraryRepository _libraryRepository;
    private readonly IMaterialLibraryLoader _libraryLoader;
    private readonly IMaterialTabGrouper _tabGrouper;
    private readonly IMaterialAssignmentPlanner _planner;
    private readonly IAssignmentPlanFinalizer _finalizer;

    private IReadOnlyList<MaterialLibraryReference> _libraries = Array.Empty<MaterialLibraryReference>();
    private NxOpen.Foundation.Contracts.Materials.MaterialLibrary? _currentLibrary;
    private Material? _selectedMaterial;
    private IReadOnlyList<BodyInfo> _allBodies = Array.Empty<BodyInfo>();
    private IReadOnlyDictionary<BodyId, BodyMaterialAssignment> _currentAssignments =
        new Dictionary<BodyId, BodyMaterialAssignment>();
    private MaterialAssignmentPlanningInput? _pendingInput;
    private AssignmentPlan? _lastPlan;

    public MaterialAssignmentDialogPresenter(
        NxSessionContext context,
        BlockAccessor blocks,
        PartMaterialService partMaterialService,
        IMaterialLibraryRepository libraryRepository,
        IMaterialLibraryLoader libraryLoader,
        IMaterialTabGrouper tabGrouper,
        IMaterialAssignmentPlanner planner,
        IAssignmentPlanFinalizer finalizer)
    {
        _context = context;
        _blocks = blocks;
        _partMaterialService = partMaterialService;
        _libraryRepository = libraryRepository;
        _libraryLoader = libraryLoader;
        _tabGrouper = tabGrouper;
        _planner = planner;
        _finalizer = finalizer;
    }

    public void OnDialogShown()
    {
        _libraries = _libraryRepository.ListAvailableLibraries();
        _blocks.PopulateLibraryDropdown(_libraries);
        RefreshBodyState();
        RefreshUsageTable();
    }

    public void OnUpdate(string changedBlockId)
    {
        if (changedBlockId == BlockAccessor.LibraryDropdownId)
            OnLibrarySelectionChanged();
        else if (changedBlockId == BlockAccessor.MaterialTabControlId)
            OnMaterialSelectionChanged();
        else if (changedBlockId == BlockAccessor.MaterialUsageTableId)
            OnUsageRowSelectionChanged();
        else if (changedBlockId == BlockAccessor.BodyKindFilterId)
            OnUsageRowSelectionChanged(); // same re-render, filter just narrows the same source rows
    }

    public void OnSelectAllClicked() => _blocks.SelectAllVisibleDrilldownBodies();

    public void OnSelectUnassignedSolidsClicked()
    {
        // One-click composite of: jump to the "(No material assigned)" usage row, filter to Solid, select
        // all — done directly here rather than by simulating the individual block callbacks.
        var unassignedExists = _currentAssignments.Values.Any(a => a.MaterialName is null);
        if (!unassignedExists)
            return;

        _blocks.SetBodyKindFilter(BodyKind.Solid);

        var bodyIds = _currentAssignments.Values
            .Where(a => a.MaterialName is null)
            .Select(a => a.BodyId)
            .ToHashSet();
        var bodies = _allBodies.Where(b => bodyIds.Contains(b.Id) && b.Kind == BodyKind.Solid).ToList();

        _blocks.PopulateBodyDrilldownList(bodies);
        _blocks.SelectAllVisibleDrilldownBodies();
    }

    public void OnClearMaterialClicked()
    {
        var bodyIds = _blocks.GetSelectedDrilldownBodyIds();
        if (bodyIds.Count == 0)
            return;

        if (!_blocks.Confirm($"Clear the physical and display material from {bodyIds.Count} body(ies)?"))
            return;

        var result = _partMaterialService.ClearMaterial(bodyIds);
        _blocks.ShowResult(result);
        RefreshBodyState();
        RefreshUsageTable();
    }

    public int OnApply()
    {
        RecomputePreview();
        if (_lastPlan is null || _pendingInput is null)
        {
            _blocks.ShowError("Select a material and at least one body before applying.");
            return 1;
        }

        Commit();
        return 0;
    }

    // VERIFY: Styler return-code convention for ok_cb (0 = close vs. stay open, or reversed) is
    // unconfirmed — this assumes OnApply's own convention carries over unchanged.
    public int OnOk() => OnApply();

    public void OnCancel()
    {
        // No mutation — the dialog framework closes itself; nothing to undo since Commit() never ran.
    }

    private void OnLibrarySelectionChanged()
    {
        var libraryId = _blocks.GetSelectedLibraryId();
        var reference = _libraries.FirstOrDefault(l => l.Id == libraryId);
        if (reference is null)
            return;

        _currentLibrary = _libraryLoader.GetOrLoad(reference);
        _partMaterialService.SetResolutionLibraries(new[] { _currentLibrary });

        var tabs = _tabGrouper.GroupByCategory(_currentLibrary);
        _blocks.PopulateMaterialTabs(tabs);

        // Resolution against the newly loaded library may change which usage rows now have a
        // ResolvedMaterialId, even though body-to-material-name assignments themselves haven't changed.
        RefreshUsageTable();
    }

    private void OnMaterialSelectionChanged()
    {
        var materialId = _blocks.GetSelectedMaterialId();
        if (materialId is null || _currentLibrary is null)
            return;

        var material = _currentLibrary.Materials.FirstOrDefault(m => m.Id == materialId);
        if (material is null)
            return;

        _selectedMaterial = material;
        _blocks.ShowMaterialProperties(material);
        RecomputePreview();
    }

    private void OnUsageRowSelectionChanged()
    {
        var row = _blocks.GetSelectedMaterialUsageRow();
        if (row is null)
        {
            _blocks.PopulateBodyDrilldownList(Array.Empty<BodyInfo>());
            return;
        }

        var bodyIds = _currentAssignments.Values
            .Where(a => string.Equals(a.MaterialName ?? MaterialUsageRow.UnassignedLabel, row.MaterialLabel, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.BodyId)
            .ToHashSet();

        var bodies = _allBodies.Where(b => bodyIds.Contains(b.Id)).ToList();
        var filter = _blocks.GetBodyKindFilter();
        if (filter is not null)
            bodies = bodies.Where(b => b.Kind == filter).ToList();

        _blocks.PopulateBodyDrilldownList(bodies);
    }

    private void RecomputePreview()
    {
        if (_selectedMaterial is null)
        {
            _pendingInput = null;
            _lastPlan = null;
            return;
        }

        var targetBodyIds = _blocks.GetSelectedDrilldownBodyIds();
        if (targetBodyIds.Count == 0)
        {
            _pendingInput = null;
            _lastPlan = null;
            return;
        }

        var targetBodyIdSet = targetBodyIds.ToHashSet();
        var targetBodies = _allBodies.Where(b => targetBodyIdSet.Contains(b.Id)).ToList();

        _pendingInput = new MaterialAssignmentPlanningInput(_selectedMaterial, targetBodies, _currentAssignments);
        _lastPlan = _planner.Plan(_pendingInput);
        _blocks.ShowPlanSummary(_lastPlan);
    }

    private void Commit()
    {
        if (_lastPlan is null || _pendingInput is null)
            return;

        var confirmedBodyIds = _blocks.GetConfirmedBodyIds();
        var executablePlan = _finalizer.Finalize(_lastPlan, _pendingInput, confirmedBodyIds);
        var result = _partMaterialService.ApplyPlan(executablePlan);
        _blocks.ShowResult(result);

        if (executablePlan.SkippedBlocked.Count > 0 || executablePlan.SkippedDeclinedConfirmation.Count > 0)
        {
            _context.Log.Info(
                $"Skipped {executablePlan.SkippedBlocked.Count} blocked, " +
                $"{executablePlan.SkippedDeclinedConfirmation.Count} declined body(ies).");
        }

        RefreshBodyState();
        RefreshUsageTable();
        _lastPlan = null;
        _pendingInput = null;
    }

    private void RefreshBodyState()
    {
        _allBodies = _partMaterialService.GetSolidBodies();
        _currentAssignments = _partMaterialService.GetCurrentAssignments();
    }

    private void RefreshUsageTable()
    {
        var rows = _currentAssignments.Values
            .GroupBy(a => a.MaterialName ?? MaterialUsageRow.UnassignedLabel, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MaterialUsageRow(
                g.Key,
                g.Select(a => a.ResolvedMaterialId).FirstOrDefault(id => id is not null),
                g.Count()))
            .OrderBy(r => r.IsUnassignedBucket ? 1 : 0)
            .ThenBy(r => r.MaterialLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _blocks.LastPopulatedUsageRows = rows;
        _blocks.PopulateMaterialUsageTable(rows);
    }
}
