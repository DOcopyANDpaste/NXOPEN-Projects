using BANxOpen.Foundation.Core.Materials.Assignment;
using BANxOpen.Foundation.Core.Materials.Assignment.Choices;
using BANxOpen.Foundation.Core.Materials.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.Core.Materials.Library;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Foundation.Core.Materials.Rules.SheetMetal;
using BANxOpen.SheetMetal.Materials;
using BANxOpen.Ui.MaterialAssignment.AssignmentChoiceUi;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>All dialog logic — the generated <c>BlockUI.cs</c> stays a thin set of delegations to this
/// class, per Skills/with-block-ui.md §1.
///
/// Mode: INTERACTIVE for browsing and staging, MODAL-SINGLE-COMMIT for the staged path (with-block-ui.md §5).
/// Library selection, tree population and staging only read via Core and re-render via
/// <see cref="BlockAccessor"/> — zero NX mutation. Mutation is confined to <see cref="CommitEntry"/> (assign)
/// and <see cref="RemoveMaterialFrom"/> (clear), each of which opens its own undo mark inside
/// <see cref="IPartMaterialService"/>. The one exception is <see cref="_sessionUndo"/>: a mark set when the
/// dialog opens so that Cancel can offer to revert everything applied while it was open.
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
        public const int AddToBodySelection = 204;
    }

    private static class PendingMenu
    {
        public const int Remove = 301;
        public const int ClearAll = 302;
        public const int ApplyAll = 303;
    }

    private readonly NxSessionContext _context;
    private readonly BlockAccessor _blocks;
    private readonly IPartMaterialService _partMaterialService;
    private readonly IMaterialLibraryRepository _libraryRepository;
    private readonly IMaterialLibraryLoader _libraryLoader;
    private readonly IMaterialCategoryTreeBuilder _categoryTreeBuilder;
    private readonly IMaterialAssignmentPlanner _planner;
    private readonly IAssignmentPlanFinalizer _finalizer;
    private readonly IAssignmentChoiceCollector _choiceCollector;
    private readonly IAssignmentChoiceWindow _choiceWindow;
    private readonly SheetMetalStandardSelection _standardSelection;
    private readonly SheetMetalLibraries _sheetMetalLibraries;
    private readonly IMaterialPropertyWindow? _propertyWindow;

    private IReadOnlyList<MaterialLibraryReference> _libraries = Array.Empty<MaterialLibraryReference>();
    private BANxOpen.Foundation.Contracts.Materials.MaterialLibrary? _currentLibrary;
    private Material? _selectedMaterial;
    private IReadOnlyList<BodyInfo> _allBodies = Array.Empty<BodyInfo>();
    private IReadOnlyDictionary<BodyId, BodyMaterialAssignment> _currentAssignments =
        new Dictionary<BodyId, BodyMaterialAssignment>();
    private readonly List<PendingAssignmentEntry> _pending = new();

    // What was changed in the part while the dialog was open, for Cancel to list, and the mark it reverts to.
    private readonly List<string> _appliedThisSession = new();
    private UndoScope? _sessionUndo;

    public MaterialAssignmentDialogPresenter(
        NxSessionContext context,
        BlockAccessor blocks,
        IPartMaterialService partMaterialService,
        IMaterialLibraryRepository libraryRepository,
        IMaterialLibraryLoader libraryLoader,
        IMaterialCategoryTreeBuilder categoryTreeBuilder,
        IMaterialAssignmentPlanner planner,
        IAssignmentPlanFinalizer finalizer,
        IAssignmentChoiceCollector choiceCollector,
        IAssignmentChoiceWindow choiceWindow,
        SheetMetalStandardSelection standardSelection,
        SheetMetalLibraries sheetMetalLibraries,
        IMaterialPropertyWindow? propertyWindow = null)
    {
        _standardSelection = standardSelection;
        _sheetMetalLibraries = sheetMetalLibraries;
        _context = context;
        _blocks = blocks;
        _partMaterialService = partMaterialService;
        _libraryRepository = libraryRepository;
        _libraryLoader = libraryLoader;
        _categoryTreeBuilder = categoryTreeBuilder;
        _planner = planner;
        _finalizer = finalizer;
        _choiceCollector = choiceCollector;
        _choiceWindow = choiceWindow;
        _propertyWindow = propertyWindow;
    }

    // ---- Dialog lifecycle ----

    public void OnInitialize() => _blocks.Initialize(this);

    public void OnDialogShown()
    {
        // ??= because this may re-fire on a tab switch (see OnUpdate), and a second mark would make Cancel's
        // revert stop short of whatever was applied before the switch.
        _sessionUndo ??= new UndoScope(_context.Session, "Material Assignment dialog", _context.Log.Error);

        _blocks.ClearHighlight();
        _blocks.SetUpColumns();

        _libraries = _libraryRepository.ListAvailableLibraries();
        _blocks.PopulateLibraryEnum(_libraries);

        // Once only, for the same re-fire reason as the undo mark: a tab switch must not reset a Standard the user
        // chose. The first Standard is the default.
        if (_standardSelection.Selected is null)
        {
            _blocks.PopulateStandardEnum(_standardSelection.Standards);
            _standardSelection.Selected = _blocks.GetSelectedStandard();
        }

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
            case BlockAccessor.StandardEnumId:
                // Only decides which rows the Sheet Metal Preferences are offered; the material tree is unaffected.
                // Staged entries keep the row they were answered with.
                _standardSelection.Selected = _blocks.GetSelectedStandard();
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
                _blocks.ClearHighlight();
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

        CommitPending();
        return 0;
    }

    // Unlike OnApply, an empty pending list on OK is a normal end state (e.g. every assignment was made via
    // "Assign now", which never touches _pending) — not something worth blocking the dialog's close on.
    public int OnOk()
    {
        if (_pending.Count > 0)
            CommitPending();

        _blocks.ClearHighlight();
        CloseKeepingChanges();
        return 0;
    }

    private void CommitPending()
    {
        // Snapshot: committing refreshes state, which rebuilds _pending. Every choice was answered at staging
        // time, so nothing here can be backed out of and every entry is dealt with.
        foreach (var entry in _pending.ToList())
            CommitEntry(entry);

        _pending.Clear();
        OnRefreshClicked();
    }

    /// <summary>Cancel (and the title bar's close, which NX routes to the same callback). Staged entries were
    /// never applied, so they only need a warning that they are about to be lost. Anything applied while the
    /// dialog was open — "Assign now", Apply, "Apply all", "Remove material" — is listed, and the user chooses
    /// whether to revert to the state the part was in when the dialog opened or keep it.</summary>
    /// <returns>1 to keep the dialog open, 0 to let it close.</returns>
    public int OnCancel()
    {
        var discardNote = _pending.Count > 0
            ? $"{_pending.Count} staged assignment(s) were never applied and will be discarded."
            : null;

        if (_appliedThisSession.Count == 0)
        {
            if (discardNote is not null && !_blocks.Confirm($"{discardNote}{Environment.NewLine}{Environment.NewLine}Close anyway?"))
                return 1;

            CloseKeepingChanges();
            return 0;
        }

        var message =
            $"These changes were applied while this dialog was open:{Environment.NewLine}" +
            string.Join(Environment.NewLine, _appliedThisSession.Select(c => $"  {c}")) +
            (discardNote is null ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{discardNote}") +
            $"{Environment.NewLine}{Environment.NewLine}Revert them?{Environment.NewLine}" +
            $"  Yes - revert to the state the part was in before this dialog opened.{Environment.NewLine}" +
            "  No - keep the changes.";

        if (_blocks.Confirm(message))
        {
            // Disposing without committing is what undoes to the mark.
            _sessionUndo?.Dispose();
            _sessionUndo = null;
        }
        else
        {
            CloseKeepingChanges();
        }

        _blocks.ClearHighlight();
        return 0;
    }

    /// <summary>Notes a change for Cancel to list. A partial failure still changed the part; an aborted
    /// operation rolled itself back and did not.</summary>
    private void RecordApplied(OperationResult result, string description)
    {
        if (result.Ok || result.ErrorCode == "PARTIAL_FAILURE")
            _appliedThisSession.Add(description);
    }

    private void CloseKeepingChanges()
    {
        _sessionUndo?.Commit();
        _sessionUndo?.Dispose();
        _sessionUndo = null;
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

    /// <summary>Sheet metal bodies are solids in NX, so the solid-body shortcuts include them. Their own kind
    /// exists only so the body-type rule can pair them with sheet metal libraries.</summary>
    private static bool IsSolid(BodyInfo body) => body.Kind is BodyKind.Solid or BodyKind.SheetMetal;

    public void OnSelectAllSolidsClicked()
    {
        var ids = _allBodies.Where(b => IsSolid(b)).Select(b => b.Id).ToList();
        _blocks.SetSelectedBodies(ids);
    }

    public void OnSelectUnassignedSolidsClicked()
    {
        var unassigned = _currentAssignments.Values
            .Where(a => a.MaterialName is null)
            .Select(a => a.BodyId)
            .ToHashSet();

        var ids = _allBodies
            .Where(b => IsSolid(b) && unassigned.Contains(b.Id))
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

        // The Standard only matters for materials that can go on sheet metal bodies.
        _blocks.SetStandardVisible(_sheetMetalLibraries.IsSheetMetalLibrary(reference.Id));

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

    public void OnAssignmentDefaultAction(AssignmentRowRef row) => AddSolidBodiesToSelection(row.Bodies);

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
        var entry = BuildResolvedEntry(material);
        if (entry is null)
            return;

        CommitEntry(entry);
        OnRefreshClicked();
    }

    private void AddToPending(Material material)
    {
        var entry = BuildResolvedEntry(material);
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

            _pending[i] = Replan(entry.Material, keptRows.Select(r => r.Body).ToList())
                with { ResolvedChoices = entry.ResolvedChoices };
        }
    }

    // ---- Current assignment tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildAssignmentMenu(AssignmentRowRef? clicked)
    {
        if (clicked is null)
            return new[] { new TreeMenuItem(AssignmentMenu.Refresh, "Refresh") };

        var isUnassigned = clicked.Row.IsUnassignedBucket;
        var hasSolidBody = clicked.Bodies.Any(b => IsSolid(b));

        return new[]
        {
            new TreeMenuItem(AssignmentMenu.SelectBodies, "Select these bodies"),
            new TreeMenuItem(AssignmentMenu.AddToBodySelection, "Add all to body selection", hasSolidBody),
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
            case AssignmentMenu.AddToBodySelection:
                AddSolidBodiesToSelection(targets.SelectMany(t => t.Bodies));
                return;
            case AssignmentMenu.RemoveMaterial:
                RemoveMaterialFrom(bodyIds);
                return;
        }
    }

    /// <summary>Unions the solid bodies among <paramref name="bodies"/> into the body-selection box, leaving
    /// whatever was already selected in place. Used by both the current-assignment tree's double-click and its
    /// "Add all to body selection" menu command.</summary>
    private void AddSolidBodiesToSelection(IEnumerable<BodyInfo> bodies)
    {
        var solidIds = bodies.Where(b => IsSolid(b)).Select(b => b.Id).ToList();
        if (solidIds.Count == 0)
        {
            _blocks.ShowError("None of the selected rows have a solid body.");
            return;
        }

        var current = _blocks.GetSelectedBodyIds().ToHashSet();
        current.UnionWith(solidIds);
        _blocks.SetSelectedBodies(current.ToList());
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

        RecordApplied(result, $"Material cleared from {bodyIds.Count} body(ies)");
        OnRefreshClicked();
    }

    // ---- Pending tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildPendingMenu(PendingRowRef? clicked)
    {
        var clearAll = new TreeMenuItem(PendingMenu.ClearAll, "Clear all pending", _pending.Count > 0);

        if (clicked is null)
            return new[] { clearAll };

        // "Apply all" only makes sense for a whole material entry, not one body within it.
        if (clicked.Row is null)
        {
            return new[]
            {
                new TreeMenuItem(PendingMenu.ApplyAll, $"Apply all ({clicked.Entry.Material.Name})"),
                new TreeMenuItem(PendingMenu.Remove, "Remove from pending"),
                TreeMenuItem.Separator,
                clearAll,
            };
        }

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

            case PendingMenu.ApplyAll:
                // Commits and re-renders on its own (via OnRefreshClicked), unlike the cases below.
                ApplyAllPending(targets);
                return;

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

    /// <summary>Commits every targeted root/material entry immediately, the same way OK/Apply would, then
    /// drops it from the pending list. Body rows within <paramref name="targets"/> are ignored — applying one
    /// body out of a staged entry isn't what "Apply all" for a material means.</summary>
    private void ApplyAllPending(IReadOnlyList<PendingRowRef> targets)
    {
        var entries = targets.Where(t => t.Row is null).Select(t => t.Entry).Distinct().ToList();
        if (entries.Count == 0)
            return;

        foreach (var entry in entries)
        {
            CommitEntry(entry);
            _pending.Remove(entry);
        }

        OnRefreshClicked();
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

    /// <summary>Plans the selection, refuses a second sheet metal material, then puts the rules' questions to the
    /// user — all before anything is applied or staged, so a staged entry already carries its answers. Returns
    /// null when the entry cannot be built, conflicts, or the user cancelled a question.</summary>
    private PendingAssignmentEntry? BuildResolvedEntry(Material material)
    {
        var entry = BuildEntry(material);
        if (entry is null)
            return null;

        if (FindSheetMetalConflict(material, entry.Rows.Select(r => r.Body).ToList()) is { } conflict)
        {
            _blocks.ShowError(conflict);
            return null;
        }

        // A material with no row at all is blocked by the engine's own constraint; one with rows only under other
        // Standards is not, so it is refused here, before anything is staged or applied.
        if (entry.Rows.Any(r => r.Body.Kind == BodyKind.SheetMetal)
            && _standardSelection.Selected is { } standard
            && _standardSelection.RowsFor(material.Name).Count == 0)
        {
            _blocks.ShowError(
                $"'{material.Name}' has no row under Standard '{standard}' in the sheet metal material standards " +
                "file, so Sheet Metal Preferences cannot be set to it. Choose another Standard.");
            return null;
        }

        if (!TryResolveChoices(entry, out var resolved))
            return null;

        return entry with { ResolvedChoices = resolved };
    }

    /// <summary>NX keeps one set of Sheet Metal Preferences per part, so every sheet metal body in it has to share
    /// a material — assigning a second one would re-point the preferences out from under the first. Returns why
    /// <paramref name="material"/> cannot go onto <paramref name="targets"/>, or null when it can.
    ///
    /// Only the sheet metal bodies outside <paramref name="targets"/> count: the targets are about to take
    /// <paramref name="material"/>, so re-assigning every sheet metal body at once is not a conflict. A body
    /// claimed by a staged entry counts as that entry's material, since that is what it will have. Regular solids
    /// have no shared state, so a selection with no sheet metal body never conflicts.</summary>
    private string? FindSheetMetalConflict(Material material, IReadOnlyList<BodyInfo> targets)
    {
        if (!targets.Any(b => b.Kind == BodyKind.SheetMetal))
            return null;

        var targetIds = targets.Select(b => b.Id).ToHashSet();

        var stagedMaterial = new Dictionary<BodyId, string>();
        foreach (var entry in _pending)
        {
            foreach (var row in entry.Rows)
                stagedMaterial[row.Body.Id] = entry.Material.Name;
        }

        var conflicting = _allBodies
            .Where(b => b.Kind == BodyKind.SheetMetal && !targetIds.Contains(b.Id))
            .Select(b =>
            {
                var name = stagedMaterial.TryGetValue(b.Id, out var staged)
                    ? staged
                    : _currentAssignments.TryGetValue(b.Id, out var current) ? current.MaterialName : null;
                return (Body: b, Material: name, IsStaged: staged is not null);
            })
            .Where(x => x.Material is not null
                        && !string.Equals(x.Material, material.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (conflicting.Count == 0)
            return null;

        var details = conflicting.Select(x => $"  [{x.Body.Name}] {x.Material}{(x.IsStaged ? " (pending)" : string.Empty)}");
        return
            $"{material.Name} cannot be assigned to sheet metal bodies in this part.{Environment.NewLine}{Environment.NewLine}" +
            $"Sheet Metal Preferences belong to the part, so every sheet metal body in it must have the same " +
            $"material, and these already have a different one:{Environment.NewLine}" +
            string.Join(Environment.NewLine, details) +
            $"{Environment.NewLine}{Environment.NewLine}Nothing was assigned or staged. Assign {material.Name} to all of the " +
            "sheet metal bodies together, or leave the sheet metal bodies out of the selection.";
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
                _pending[i] = Replan(entry.Material, stillPresent) with { ResolvedChoices = entry.ResolvedChoices };
        }

        _pending.RemoveAll(e => e.Rows.Count == 0);
        _blocks.PopulatePendingTree(_pending);
    }

    /// <summary>Applies one entry. Its choices were answered when it was built (<see cref="BuildResolvedEntry"/>),
    /// so the only thing still asked here is confirmation for the bodies that need it.</summary>
    private void CommitEntry(PendingAssignmentEntry entry)
    {
        if (!entry.HasAnyApplicableBody)
        {
            _blocks.ShowError($"Every body staged for {entry.Material.Name} is blocked by a rule; nothing was applied.");
            return;
        }

        // Checked again because the dialog is interactive: the part may have gained a sheet metal material since
        // the entry was staged.
        if (FindSheetMetalConflict(entry.Material, entry.Rows.Select(r => r.Body).ToList()) is { } conflict)
        {
            _blocks.ShowError(conflict);
            return;
        }

        var confirmedBodyIds = GetConfirmedBodyIds(entry);

        var answers = AssignmentChoiceAnswers.CreateBuilder();
        foreach (var choice in entry.ResolvedChoices)
            answers.Answer(choice.Question, choice.OptionId);

        var executablePlan = _finalizer.Finalize(entry.Plan, entry.Input, confirmedBodyIds, answers.Build());
        var result = _partMaterialService.ApplyPlan(executablePlan);
        RecordApplied(result, $"{entry.Material.Name} assigned to {executablePlan.Assignments.Count} body(ies)");

        // Warnings never stop an assignment, but "Assign now" skips the pending tree where they would otherwise
        // be seen — for example a bead on the body that matches no SPEC, whose restriction is therefore not
        // enforced. They are appended to the result so the user learns of them either way.
        var warnings = entry.Rows
            .Where(r => r.Status == PendingBodyStatus.Ok && !string.IsNullOrWhiteSpace(r.Message))
            .Select(r => $"[{r.Body.Name}] {r.Message}")
            .ToList();

        warnings.AddRange(entry.ResolvedChoices.SelectMany(c => c.Warnings));

        var summary = $"{entry.Material.Name} applied to {executablePlan.Assignments.Count} body(ies).";
        if (warnings.Count > 0)
            summary += $"{Environment.NewLine}{Environment.NewLine}Warnings:{Environment.NewLine}{string.Join(Environment.NewLine, warnings)}";

        _blocks.ShowResult(result, summary);

        if (executablePlan.SkippedBlocked.Count > 0 || executablePlan.SkippedDeclinedConfirmation.Count > 0)
        {
            _context.Log.Info(
                $"Skipped {executablePlan.SkippedBlocked.Count} blocked, " +
                $"{executablePlan.SkippedDeclinedConfirmation.Count} declined body(ies).");
        }

        // Choices are answered at staging for every body not blocked then, so this only happens when a re-plan
        // unblocked a body that was never asked about, or the collector and the finalizer disagree about which
        // bodies are going ahead. Logged rather than shown: re-staging the material is the user's only remedy
        // and the result message already gives the applied count.
        if (executablePlan.SkippedUnresolvedChoice.Count > 0)
        {
            _context.Log.Error(
                $"Skipped {executablePlan.SkippedUnresolvedChoice.Count} body(ies) with an unanswered choice: " +
                string.Join(", ", executablePlan.SkippedUnresolvedChoice.Select(b => b.Value)));
        }
    }

    /// <summary>Puts every question the rules raised for this entry to the user.
    ///
    /// A choice the domain already settled is shown as an information window rather than asked — the user still
    /// learns what was decided for them, which for the Sheet Metal Preferences is the difference between the
    /// part quietly changing and the user knowing it did.
    ///
    /// A question an already-staged entry of the same material answered is not asked again. For sheet metal this
    /// is the one-material-per-part case: the preferences have one row, and a second batch of bodies taking the
    /// same material takes the same row.
    ///
    /// Asked for every body not blocked, including those still needing confirmation — confirmation is only
    /// collected at commit, and a body the user then declines just leaves its answer unread.
    ///
    /// Cancelling any question abandons the whole entry: the choices feed side effects that are part of what
    /// makes the assignment correct, so applying the material without them would leave exactly the half-done
    /// state the rules exist to prevent.</summary>
    /// <returns>False when the user cancelled, in which case nothing should be applied or staged.</returns>
    private bool TryResolveChoices(PendingAssignmentEntry entry, out IReadOnlyList<ResolvedAssignmentChoice> resolved)
    {
        var answered = new List<ResolvedAssignmentChoice>();
        resolved = answered;

        var questions = _choiceCollector.Collect(entry.Plan, entry.Input, entry.BodyIdsNeedingConfirmation.ToHashSet());

        foreach (var question in questions)
        {
            if (FindStagedAnswer(entry.Material, question) is { } staged)
            {
                answered.Add(new ResolvedAssignmentChoice(question, staged.OptionId, Array.Empty<string>()));
                continue;
            }

            if (question.Choice.Auto is { } auto)
            {
                _blocks.ShowInfo(question.Choice.Title, auto.Message);
                answered.Add(new ResolvedAssignmentChoice(question, auto.OptionId, Array.Empty<string>()));
                continue;
            }

            var result = _choiceWindow.Resolve(question.Choice);
            if (result.WasCancelled || result.OptionId is not { } optionId)
            {
                _context.Log.Info(
                    $"'{question.Choice.Title}' was cancelled; {entry.Material.Name} was not assigned to " +
                    $"{question.BodyIds.Count} body(ies).");
                return false;
            }

            answered.Add(new ResolvedAssignmentChoice(question, optionId, result.Warnings));
        }

        return true;
    }

    private ResolvedAssignmentChoice? FindStagedAnswer(Material material, PendingAssignmentChoice question) =>
        _pending
            .Where(e => string.Equals(e.Material.Name, material.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.ResolvedChoices)
            .FirstOrDefault(c =>
                string.Equals(c.Question.Choice.ChoiceId, question.Choice.ChoiceId, StringComparison.Ordinal)
                && string.Equals(c.Question.Choice.GroupKey, question.Choice.GroupKey, StringComparison.Ordinal)
                && question.Choice.Find(c.OptionId) is not null);

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

        _context.Log.Info($"TRACE ShowProperties: opening property window for '{material.Name}'.");
        _propertyWindow.Show(material);
        _context.Log.Info("TRACE ShowProperties: property window returned control to the main dialog.");
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
