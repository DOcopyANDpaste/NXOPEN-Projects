using Core.Assignment;
using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.Core.MaterialLibrary;
using NxOpen.Foundation.NxAdapters;

namespace NxAdapters.Ui;

/// <summary>All dialog logic — the generated <c>BlockUI.cs</c> stays a thin set of delegations to this
/// class, per Skills/with-block-ui.md §1.
///
/// Mode: INTERACTIVE for browsing and staging, MODAL-SINGLE-COMMIT for the staged path (with-block-ui.md §5).
/// Library selection, tree population and staging only read via Core and re-render via
/// <see cref="BlockAccessor"/> — zero NX mutation. Mutation is confined to <see cref="CommitEntry"/> (assign)
/// and <see cref="RemoveMaterialFrom"/> (clear), each of which opens its own undo mark inside
/// <see cref="IPartMaterialService"/>, never here.
///
/// Two ways to assign, both driven from the material tree's right-click menu. "Assign now" plans, confirms
/// and applies immediately — one self-contained transaction. "Add to pending" stages the planned entry in
/// <see cref="_pending"/> and shows its per-body rule outcomes in the pending tree; OK/Apply then commits
/// every staged entry. Target bodies always come from the <c>Sel_SoildBodies</c> selection block.</summary>
public sealed class MaterialAssignmentDialogPresenter : ITreeInteractionSink
{
    // Context-menu ids. Distinct ranges per tree so a stray id can never be mistaken for another tree's
    // command while debugging.
    private static class MaterialMenu
    {
        public const int AssignNow = 101;
        public const int AddToPending = 102;
        public const int Properties = 103;
        public const int Refresh = 104;
    }

    private static class AssignmentMenu
    {
        public const int SelectBodies = 201;
        public const int RemoveMaterial = 202;
        public const int Refresh = 203;
    }

    private static class PendingMenu
    {
        public const int Remove = 301;
        public const int ClearAll = 302;
    }

    private readonly NxSessionContext _context;
    private readonly BlockAccessor _blocks;
    private readonly IPartMaterialService _partMaterialService;
    private readonly IMaterialLibraryRepository _libraryRepository;
    private readonly IMaterialLibraryLoader _libraryLoader;
    private readonly IMaterialCategoryTreeBuilder _categoryTreeBuilder;
    private readonly IMaterialAssignmentPlanner _planner;
    private readonly IAssignmentPlanFinalizer _finalizer;
    private readonly IMaterialPropertyWindow? _propertyWindow;

    private IReadOnlyList<MaterialLibraryReference> _libraries = Array.Empty<MaterialLibraryReference>();
    private NxOpen.Foundation.Contracts.Materials.MaterialLibrary? _currentLibrary;
    private Material? _selectedMaterial;
    private IReadOnlyList<BodyInfo> _allBodies = Array.Empty<BodyInfo>();
    private IReadOnlyDictionary<BodyId, BodyMaterialAssignment> _currentAssignments =
        new Dictionary<BodyId, BodyMaterialAssignment>();
    private readonly List<PendingAssignmentEntry> _pending = new();

    public MaterialAssignmentDialogPresenter(
        NxSessionContext context,
        BlockAccessor blocks,
        IPartMaterialService partMaterialService,
        IMaterialLibraryRepository libraryRepository,
        IMaterialLibraryLoader libraryLoader,
        IMaterialCategoryTreeBuilder categoryTreeBuilder,
        IMaterialAssignmentPlanner planner,
        IAssignmentPlanFinalizer finalizer,
        IMaterialPropertyWindow? propertyWindow = null)
    {
        _context = context;
        _blocks = blocks;
        _partMaterialService = partMaterialService;
        _libraryRepository = libraryRepository;
        _libraryLoader = libraryLoader;
        _categoryTreeBuilder = categoryTreeBuilder;
        _planner = planner;
        _finalizer = finalizer;
        _propertyWindow = propertyWindow;
    }

    // ---- Dialog lifecycle ----

    public void OnInitialize() => _blocks.Initialize(this);

    public void OnDialogShown()
    {
        _blocks.SetUpColumns();

        _libraries = _libraryRepository.ListAvailableLibraries();
        _blocks.PopulateLibraryEnum(_libraries);

        RefreshBodyState();
        RefreshAssignmentTree();

        // The enumeration block defaults to its first entry without firing an update, so load that library
        // now rather than leaving the material tree empty until the user touches the dropdown.
        if (_libraries.Count > 0)
            OnLibrarySelectionChanged();
    }

    public void OnUpdate(string changedBlockId)
    {
        switch (changedBlockId)
        {
            case BlockAccessor.LibraryEnumId:
                OnLibrarySelectionChanged();
                break;
            case BlockAccessor.SelectAllButtonId:
                OnSelectAllSolidsClicked();
                break;
            case BlockAccessor.SelectUnassignedButtonId:
                OnSelectUnassignedSolidsClicked();
                break;
            case BlockAccessor.TabControlId:
                // Belt-and-suspenders alongside OnDialogShown: it's unconfirmed whether switching tabs
                // still re-fires dialogShown_cb the way Explorer node switches used to, so this covers the
                // case where NX signals the switch through update_cb instead. SetUpColumns is idempotent
                // per page, so it's harmless if both paths end up firing.
                _blocks.SetUpColumns();
                break;
        }
    }

    public int OnApply()
    {
        if (_pending.Count == 0)
        {
            _blocks.ShowError(
                "Nothing is pending. Select bodies, then right-click a material and choose \"Add to pending\".");
            return 1;
        }

        // Snapshot: committing refreshes state, which rebuilds _pending.
        foreach (var entry in _pending.ToList())
            CommitEntry(entry);

        _pending.Clear();
        OnRefreshClicked();
        return 0;
    }

    public int OnOk() => OnApply();

    public void OnCancel()
    {
        // No mutation to undo — staged entries were never applied, and the direct "assign now" path commits
        // its own transaction at the time it runs. The dialog framework closes itself.
        //
        // Note the Styler registered no cancel handler for this dialog, so nothing currently calls this. Do
        // not put cleanup here expecting it to run.
    }

    /// <summary>Re-queries the physical/display material state from the part and re-renders everything derived
    /// from it. The dialog is interactive, so the model can change underneath it.</summary>
    public void OnRefreshClicked()
    {
        RefreshBodyState();
        RefreshAssignmentTree();
        ReplanPending();
    }

    // ---- Quick selection buttons ----

    public void OnSelectAllSolidsClicked()
    {
        var ids = _allBodies.Where(b => b.Kind == BodyKind.Solid).Select(b => b.Id).ToList();
        _blocks.SetSelectedBodies(ids);
    }

    public void OnSelectUnassignedSolidsClicked()
    {
        var unassigned = _currentAssignments.Values
            .Where(a => a.MaterialName is null)
            .Select(a => a.BodyId)
            .ToHashSet();

        var ids = _allBodies
            .Where(b => b.Kind == BodyKind.Solid && unassigned.Contains(b.Id))
            .Select(b => b.Id)
            .ToList();

        if (ids.Count == 0)
        {
            _blocks.ShowError("Every solid body in this part already has a material.");
            return;
        }

        _blocks.SetSelectedBodies(ids);
    }

    // ---- Library / material browsing ----

    private void OnLibrarySelectionChanged()
    {
        var libraryId = _blocks.GetSelectedLibraryId();
        var reference = _libraries.FirstOrDefault(l => l.Id == libraryId);
        if (reference is null)
            return;

        _currentLibrary = _libraryLoader.GetOrLoad(reference);
        _partMaterialService.SetResolutionLibraries(new[] { _currentLibrary });

        _blocks.PopulateMaterialTree(_categoryTreeBuilder.Build(_currentLibrary));

        // Switching library invalidates the previously picked material.
        _selectedMaterial = null;
        _blocks.SetMaterialLabel(null);

        // Resolution against the newly loaded library may change which rows now have a ResolvedMaterialId,
        // even though body-to-material-name assignments themselves haven't changed.
        RefreshAssignmentTree();
    }

    public void OnMaterialSelected(Material? material)
    {
        _selectedMaterial = material;
        _blocks.SetMaterialLabel(material);
    }

    // Hover only retitles the label; it deliberately does not change _selectedMaterial, or moving the
    // pointer across the tree would silently repoint what an Assign command acts on.
    public void OnMaterialHovered(Material? material) => _blocks.SetMaterialLabel(material ?? _selectedMaterial);

    public void OnAssignmentSelected(AssignmentRowRef? row) => _blocks.SetAssignmentLabel(row);

    public void OnPendingSelected(PendingRowRef? row) => _blocks.SetPendingLabel(row);

    public string OnMaterialTooltip(Material material)
    {
        var lines = new List<string> { material.Name };

        if (material.Category.PathSegments.Count > 0)
            lines.Add(string.Join(" > ", material.Category.PathSegments));
        else
            lines.Add(material.Category.DisplayName);

        if (!string.IsNullOrWhiteSpace(material.Description))
            lines.Add(material.Description!);

        return string.Join(Environment.NewLine, lines);
    }

    public void OnMaterialDefaultAction(Material material) => ShowProperties(material);

    // ---- Material tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildMaterialMenu(Material? clicked)
    {
        if (clicked is null)
            return new[] { new TreeMenuItem(MaterialMenu.Refresh, "Refresh") };

        // Assignment needs a target, and the selection block is the only source of one.
        var hasBodies = _blocks.GetSelectedBodyIds().Count > 0;

        return new[]
        {
            new TreeMenuItem(MaterialMenu.AssignNow, "Assign to selected bodies", hasBodies),
            new TreeMenuItem(MaterialMenu.AddToPending, "Add to pending", hasBodies),
            TreeMenuItem.Separator,
            new TreeMenuItem(MaterialMenu.Properties, "Properties..."),
            new TreeMenuItem(MaterialMenu.Refresh, "Refresh"),
        };
    }

    public void OnMaterialMenuCommand(int menuItemId, IReadOnlyList<Material> targets)
    {
        var material = targets.FirstOrDefault() ?? _selectedMaterial;

        switch (menuItemId)
        {
            case MaterialMenu.Refresh:
                OnRefreshClicked();
                return;
            case MaterialMenu.Properties when material is not null:
                ShowProperties(material);
                return;
            case MaterialMenu.AssignNow when material is not null:
                AssignNow(material);
                return;
            case MaterialMenu.AddToPending when material is not null:
                AddToPending(material);
                return;
        }
    }

    private void AssignNow(Material material)
    {
        var entry = BuildEntry(material);
        if (entry is null)
            return;

        CommitEntry(entry);
        OnRefreshClicked();
    }

    private void AddToPending(Material material)
    {
        var entry = BuildEntry(material);
        if (entry is null)
            return;

        // A body staged twice would be assigned twice, last-one-wins, with the earlier entry's rule outcomes
        // shown as if they still applied. Drop the earlier claim instead so the tree matches what will happen.
        var reclaimed = entry.BodyIds.ToHashSet();
        DropClaims(reclaimed);

        _pending.Add(entry);
        _blocks.PopulatePendingTree(_pending);
    }

    /// <summary>Removes the given bodies from every staged entry, dropping any entry left with none.</summary>
    private void DropClaims(IReadOnlyCollection<BodyId> bodyIds)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var entry = _pending[i];
            var keptRows = entry.Rows.Where(r => !bodyIds.Contains(r.Body.Id)).ToList();
            if (keptRows.Count == entry.Rows.Count)
                continue;

            if (keptRows.Count == 0)
            {
                _pending.RemoveAt(i);
                continue;
            }

            _pending[i] = Replan(entry.Material, keptRows.Select(r => r.Body).ToList());
        }
    }

    // ---- Current assignment tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildAssignmentMenu(AssignmentRowRef? clicked)
    {
        if (clicked is null)
            return new[] { new TreeMenuItem(AssignmentMenu.Refresh, "Refresh") };

        var isUnassigned = clicked.Row.IsUnassignedBucket;

        return new[]
        {
            new TreeMenuItem(AssignmentMenu.SelectBodies, "Select these bodies"),
            new TreeMenuItem(AssignmentMenu.RemoveMaterial, "Remove material", !isUnassigned),
            TreeMenuItem.Separator,
            new TreeMenuItem(AssignmentMenu.Refresh, "Refresh"),
        };
    }

    public void OnAssignmentMenuCommand(int menuItemId, IReadOnlyList<AssignmentRowRef> targets)
    {
        var bodyIds = targets.SelectMany(t => t.Bodies).Select(b => b.Id).Distinct().ToList();

        switch (menuItemId)
        {
            case AssignmentMenu.Refresh:
                OnRefreshClicked();
                return;
            case AssignmentMenu.SelectBodies:
                _blocks.SetSelectedBodies(bodyIds);
                return;
            case AssignmentMenu.RemoveMaterial:
                RemoveMaterialFrom(bodyIds);
                return;
        }
    }

    private void RemoveMaterialFrom(IReadOnlyList<BodyId> bodyIds)
    {
        if (bodyIds.Count == 0)
        {
            _blocks.ShowError("Select at least one body to remove its material.");
            return;
        }

        if (!_blocks.Confirm($"Clear the physical and display material from {bodyIds.Count} body(ies)?"))
            return;

        var result = _partMaterialService.ClearMaterial(bodyIds);
        _blocks.ShowResult(result, $"Material cleared from {bodyIds.Count} body(ies).");
        OnRefreshClicked();
    }

    // ---- Pending tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildPendingMenu(PendingRowRef? clicked)
    {
        var clearAll = new TreeMenuItem(PendingMenu.ClearAll, "Clear all pending", _pending.Count > 0);

        if (clicked is null)
            return new[] { clearAll };

        return new[]
        {
            new TreeMenuItem(PendingMenu.Remove, "Remove from pending"),
            TreeMenuItem.Separator,
            clearAll,
        };
    }

    public void OnPendingMenuCommand(int menuItemId, IReadOnlyList<PendingRowRef> targets)
    {
        switch (menuItemId)
        {
            case PendingMenu.ClearAll:
                _pending.Clear();
                break;

            case PendingMenu.Remove:
                // A root row removes the whole entry; a body row removes just that body from it.
                foreach (var entry in targets.Where(t => t.Row is null).Select(t => t.Entry).Distinct().ToList())
                    _pending.Remove(entry);

                var bodyIds = targets.Where(t => t.Row is not null).Select(t => t.Row!.Body.Id).ToHashSet();
                if (bodyIds.Count > 0)
                    DropClaims(bodyIds);
                break;

            default:
                return;
        }

        _blocks.PopulatePendingTree(_pending);
    }

    // ---- Planning and committing ----

    /// <summary>Plans the currently selected bodies against <paramref name="material"/>, or reports why it
    /// cannot and returns null.</summary>
    private PendingAssignmentEntry? BuildEntry(Material material)
    {
        var selectedIds = _blocks.GetSelectedBodyIds();
        if (selectedIds.Count == 0)
        {
            _blocks.ShowError("Select at least one body before assigning a material.");
            return null;
        }

        var selected = selectedIds.ToHashSet();
        var targets = _allBodies.Where(b => selected.Contains(b.Id)).ToList();
        if (targets.Count == 0)
        {
            // The selection resolved to nothing this scan knows about — the part changed under the dialog.
            _blocks.ShowError("The selected bodies are no longer in the work part. Refresh and try again.");
            return null;
        }

        return Replan(material, targets);
    }

    private PendingAssignmentEntry Replan(Material material, IReadOnlyList<BodyInfo> targets)
    {
        var input = new MaterialAssignmentPlanningInput(material, targets, _currentAssignments);
        return PendingAssignmentEntry.Create(input, _planner.Plan(input));
    }

    /// <summary>Re-plans every staged entry against freshly-read part state. Without this a staged entry keeps
    /// the outcomes it was created with, so a body that gained a material since staging would still show as an
    /// unproblematic first assignment instead of a reassignment needing confirmation.</summary>
    private void ReplanPending()
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            var entry = _pending[i];
            var stillPresent = entry.Rows
                .Select(r => _allBodies.FirstOrDefault(b => b.Id == r.Body.Id))
                .OfType<BodyInfo>()
                .ToList();

            if (stillPresent.Count > 0)
                _pending[i] = Replan(entry.Material, stillPresent);
        }

        _pending.RemoveAll(e => e.Rows.Count == 0);
        _blocks.PopulatePendingTree(_pending);
    }

    private void CommitEntry(PendingAssignmentEntry entry)
    {
        if (!entry.HasAnyApplicableBody)
        {
            _blocks.ShowError($"Every body staged for {entry.Material.Name} is blocked by a rule; nothing was applied.");
            return;
        }

        var confirmedBodyIds = GetConfirmedBodyIds(entry);
        var executablePlan = _finalizer.Finalize(entry.Plan, entry.Input, confirmedBodyIds);
        var result = _partMaterialService.ApplyPlan(executablePlan);
        _blocks.ShowResult(result, $"{entry.Material.Name} applied to {executablePlan.Assignments.Count} body(ies).");

        if (executablePlan.SkippedBlocked.Count > 0 || executablePlan.SkippedDeclinedConfirmation.Count > 0)
        {
            _context.Log.Info(
                $"Skipped {executablePlan.SkippedBlocked.Count} blocked, " +
                $"{executablePlan.SkippedDeclinedConfirmation.Count} declined body(ies).");
        }
    }

    /// <summary>Asks the user to confirm the bodies whose rules returned RequireConfirmation — chiefly
    /// reassignment over an existing material, and coating display-material mismatches.
    ///
    /// All-or-nothing per entry: the pending tree already shows which bodies need confirmation and why, so the
    /// prompt only has to collect the answer. The finalizer's per-body handling is untouched, so a per-row
    /// control could replace this later without changing anything downstream. Declining skips those bodies;
    /// the rest of the entry still applies.</summary>
    private HashSet<BodyId> GetConfirmedBodyIds(PendingAssignmentEntry entry)
    {
        var needing = entry.Rows.Where(r => r.Status == PendingBodyStatus.NeedsConfirmation).ToList();
        if (needing.Count == 0)
            return new HashSet<BodyId>();

        var details = needing.Select(r => $"  [{r.Body.Name}] {r.Message}");
        var message =
            $"{needing.Count} body(ies) need confirmation for {entry.Material.Name}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, details) +
            $"{Environment.NewLine}{Environment.NewLine}Apply to all of them?";

        return _blocks.Confirm(message)
            ? needing.Select(r => r.Body.Id).ToHashSet()
            : new HashSet<BodyId>();
    }

    private void ShowProperties(Material material)
    {
        if (_propertyWindow is null)
        {
            _blocks.ShowError("The material property window is not available.");
            return;
        }

        _propertyWindow.Show(material);
    }

    // ---- State refresh ----

    private void RefreshBodyState()
    {
        _allBodies = _partMaterialService.GetBodies();
        _currentAssignments = _partMaterialService.GetCurrentAssignments();
    }

    private void RefreshAssignmentTree()
    {
        var bodiesById = _allBodies.ToDictionary(b => b.Id);

        var groups = _currentAssignments.Values
            .GroupBy(a => a.MaterialName ?? MaterialUsageRow.UnassignedLabel, StringComparer.OrdinalIgnoreCase)
            .Select(g => AssignmentRowRef.ForMaterial(
                new MaterialUsageRow(
                    g.Key,
                    g.Select(a => a.ResolvedMaterialId).FirstOrDefault(id => id is not null),
                    g.Count()),
                g.Select(a => bodiesById.TryGetValue(a.BodyId, out var body) ? body : null)
                    .OfType<BodyInfo>()
                    .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(r => r.Row.IsUnassignedBucket ? 1 : 0)
            .ThenBy(r => r.Row.MaterialLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The display-material column reads off this, so it has to be current before the tree is built.
        _blocks.CurrentAssignments = _currentAssignments;
        _blocks.PopulateCurrentAssignmentTree(groups);
    }
}
