using BANxOpen.Foundation.Core.Materials.Assignment;
using BANxOpen.Foundation.Core.Materials.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Foundation.Core.Materials.Library;
using BANxOpen.Foundation.Contracts.Materials;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Foundation.Core.Materials.Rules.SheetMetal;
using BANxOpen.SheetMetal.Materials;
using BANxOpen.SheetMetal.NxAdapters.Common;

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
        public const int ApplySheetMetalPreference = 105;
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
    private readonly CachingMaterialLibraryLoader _libraryLoader;
    private readonly MaterialCategoryTreeBuilder _categoryTreeBuilder;
    private readonly IMaterialAssignmentPlanner _planner;
    private readonly IAssignmentPlanFinalizer _finalizer;
    private readonly SheetMetalStandardSelection _standardSelection;
    private readonly SheetMetalLibraries _sheetMetalLibraries;
    private readonly SheetMetalMaterialTable _sheetMetalTable;
    private readonly SheetMetalPreferenceService _sheetMetalPreferences;
    private readonly IMaterialPropertyWindow? _propertyWindow;

    private IReadOnlyList<MaterialLibraryReference> _libraries = Array.Empty<MaterialLibraryReference>();
    private MaterialLibraryReference? _currentLibraryReference;
    private BANxOpen.Foundation.Contracts.Materials.MaterialLibrary? _currentLibrary;
    private MaterialPick? _selectedPick;

    // Sheet Metal Preferences. Only meaningful while a sheet metal library is selected: the standards file rows made of
    // that library's materials, the filters the user set on them, and the rows those filters leave.
    private bool _isSheetMetalLibrary;
    private IReadOnlyList<SheetMetalMaterialRow> _libraryRows = Array.Empty<SheetMetalMaterialRow>();
    private SheetMetalRowFilter _filter = SheetMetalRowFilter.None;
    private IReadOnlyList<SheetMetalMaterialRow> _filteredRows = Array.Empty<SheetMetalMaterialRow>();
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
        CachingMaterialLibraryLoader libraryLoader,
        MaterialCategoryTreeBuilder categoryTreeBuilder,
        IMaterialAssignmentPlanner planner,
        IAssignmentPlanFinalizer finalizer,
        SheetMetalStandardSelection standardSelection,
        SheetMetalLibraries sheetMetalLibraries,
        SheetMetalMaterialTable sheetMetalTable,
        SheetMetalPreferenceService sheetMetalPreferences,
        IMaterialPropertyWindow? propertyWindow = null)
    {
        _standardSelection = standardSelection;
        _sheetMetalLibraries = sheetMetalLibraries;
        _sheetMetalTable = sheetMetalTable;
        _sheetMetalPreferences = sheetMetalPreferences;
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
        // ??= because this may re-fire on a tab switch (see OnUpdate), and a second mark would make Cancel's
        // revert stop short of whatever was applied before the switch.
        _sessionUndo ??= new UndoScope(_context.Session, "Material Assignment dialog", _context.Log.Error);

        _blocks.ClearHighlight();
        _blocks.SetUpColumns();

        // Re-selects the library already in use, for the same re-fire reason as the undo mark: a re-fire must not
        // throw away the library or the Sheet Metal Preferences filters the user chose. The filters live in _filter
        // and are put back by OnLibrarySelectionChanged.
        _libraries = ListLibrariesForApplication();
        _blocks.PopulateLibraryEnum(_libraries, _currentLibraryReference?.Id);

        RefreshBodyState();
        RefreshAssignmentTree();

        // The enumeration block defaults to its first entry without firing an update, so load that library
        // now rather than leaving the material tree empty until the user touches the dropdown.
        if (_libraries.Count > 0)
            OnLibrarySelectionChanged();
    }

    /// <summary>Every library, except in the NX Sheet Metal application, where only sheet metal libraries are offered.
    /// If none is configured there, every library is offered rather than none, and the gap is logged.</summary>
    private IReadOnlyList<MaterialLibraryReference> ListLibrariesForApplication()
    {
        var all = _libraryRepository.ListAvailableLibraries();
        if (!_context.IsSheetMetalApplication)
            return all;

        var sheetMetal = all.Where(l => _sheetMetalLibraries.IsSheetMetalLibrary(l.Id)).ToList();
        if (sheetMetal.Count > 0)
            return sheetMetal;

        _context.Log.Warn("NX is in the Sheet Metal application, but no available library is a sheet metal library; listing every library.");
        return all;
    }

    public void OnUpdate(string changedBlockId)
    {
        switch (changedBlockId)
        {
            case BlockAccessor.LibraryEnumId:
                OnLibrarySelectionChanged();
                break;
            case BlockAccessor.StandardEnumId:
            case BlockAccessor.SmPhysicalMaterialEnumId:
            case BlockAccessor.SmThicknessEnumId:
            case BlockAccessor.SmBendRadiusEnumId:
                // Staged entries keep the row they were staged with; only what can be picked next changes.
                _filter = _blocks.GetSheetMetalFilter();
                RefreshMaterialTree();
                break;
            case BlockAccessor.SmClearButtonId:
                _filter = SheetMetalRowFilter.None;
                RefreshMaterialTree();
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

    /// <summary>Commits what is pending. With nothing pending and a sheet metal material picked (see
    /// <see cref="PickedSheetMetalMaterial"/>), applies that instead — see <see cref="ApplyPickedSheetMetalMaterial"/>.</summary>
    public int OnApply()
    {
        if (_pending.Count > 0)
        {
            CommitPending();
            return 0;
        }

        if (PickedSheetMetalMaterial() is { } pick)
            return ApplyPickedSheetMetalMaterial(pick) ? 0 : 1;

        _blocks.ShowError(
            "Nothing is pending. Select bodies, then right-click a material and choose \"Add to pending\"." +
            (_isSheetMetalLibrary
                ? $"{Environment.NewLine}{Environment.NewLine}For a sheet metal library you can also pick a sheet metal " +
                  "material (a row under a physical material) and Apply it."
                : string.Empty));
        return 1;
    }

    // Unlike OnApply, an empty pending list on OK is a normal end state (e.g. every assignment was made via
    // "Assign now", which never touches _pending) — not something worth blocking the dialog's close on. A picked
    // sheet metal material is applied the same way Apply would; backing out of it keeps the dialog open.
    public int OnOk()
    {
        if (_pending.Count > 0)
            CommitPending();
        else if (PickedSheetMetalMaterial() is { } pick && !ApplyPickedSheetMetalMaterial(pick))
            return 1;

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

    // ---- Sheet Metal Preferences without bodies ----

    /// <summary>The sheet metal material Apply/OK act on when nothing is pending: the row selected in the material
    /// tree, else the only row the filters leave. Null outside a sheet metal library, or when no single row is picked.</summary>
    private MaterialPick? PickedSheetMetalMaterial()
    {
        if (!_isSheetMetalLibrary || _currentLibrary is null)
            return null;

        if (_selectedPick is { Row: not null })
            return _selectedPick;

        if (_filteredRows.Count != 1)
            return null;

        var row = _filteredRows[0];
        var material = _currentLibrary.Materials.FirstOrDefault(m => SameName(m.Name, row.PhysicalMaterialName));
        return material is null ? null : new MaterialPick(material, row);
    }

    /// <summary>Apply/OK with a sheet metal material picked and nothing pending. With bodies selected it assigns the
    /// material to them, as "Assign to selected bodies" would. With none, it sets only the part's Sheet Metal
    /// Preferences, after warning that the bodies' own material and colour are left as they are.</summary>
    /// <returns>False when nothing was applied — the user backed out, or NX refused.</returns>
    private bool ApplyPickedSheetMetalMaterial(MaterialPick pick)
    {
        if (_blocks.GetSelectedBodyIds().Count > 0)
            return AssignNow(pick);

        var row = pick.Row!;
        var message =
            $"No solid body is selected. Only the part's Sheet Metal Preferences will be set to '{row.Name}' " +
            $"({BlockAccessor.SheetMetalRowText(row)}).{Environment.NewLine}{Environment.NewLine}" +
            "The physical material and display material (colour) of the bodies will NOT be updated. Select the bodies " +
            "and Apply or Assign again to update them." +
            DifferentSheetMetalBodiesNote(row) +
            $"{Environment.NewLine}{Environment.NewLine}Continue?";

        return _blocks.Confirm(message) && ApplySheetMetalPreferenceOnly(row);
    }

    /// <summary>Sets the part's Sheet Metal Preferences to <paramref name="row"/> and nothing else — no body is
    /// assigned and nothing is staged. Recorded like any other change, so Cancel can revert it.</summary>
    private bool ApplySheetMetalPreferenceOnly(SheetMetalMaterialRow row)
    {
        // The service sets no undo mark of its own; a refused change must not leave a half-built preferences edit.
        OperationResult result;
        using (var undo = new UndoScope(_context.Session, "Set Sheet Metal Preferences", _context.Log.Error))
        {
            result = _sheetMetalPreferences.SyncMaterial(row);
            if (result.Ok)
                undo.Commit();
        }

        _blocks.ShowResult(result,
            $"Sheet Metal Preferences set to '{row.Name}'.{Environment.NewLine}{Environment.NewLine}" +
            "The physical material and display material (colour) of the bodies were not changed. Select the bodies " +
            "and Apply or Assign again to update them." +
            DifferentSheetMetalBodiesNote(row));

        RecordApplied(result, $"Sheet Metal Preferences set to {row.Name}");
        OnRefreshClicked();
        return result.Ok;
    }

    /// <summary>Names the sheet metal bodies whose material is not the one <paramref name="row"/> is made of, since the
    /// preferences and their material will disagree until they are reassigned. Empty when there are none.</summary>
    private string DifferentSheetMetalBodiesNote(SheetMetalMaterialRow row)
    {
        var differing = _allBodies
            .Where(b => b.Kind == BodyKind.SheetMetal)
            .Select(b => (Body: b, Material: _currentAssignments.TryGetValue(b.Id, out var a) ? a.MaterialName : null))
            .Where(x => !SameName(x.Material, row.PhysicalMaterialName))
            .Select(x => $"  [{x.Body.Name}] {x.Material ?? MaterialUsageRow.UnassignedLabel}")
            .ToList();

        return differing.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}These sheet metal bodies are not {row.PhysicalMaterialName}, so " +
              $"their material will not match the preferences until they are reassigned:{Environment.NewLine}" +
              string.Join(Environment.NewLine, differing);
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

        _currentLibraryReference = reference;
        _currentLibrary = _libraryLoader.GetOrLoad(reference);
        _partMaterialService.SetResolutionLibraries(new[] { _currentLibrary });

        // The Sheet Metal Preferences only mean anything for materials that can go on sheet metal bodies.
        _isSheetMetalLibrary = _sheetMetalLibraries.IsSheetMetalLibrary(reference.Id);
        _blocks.SetSheetMetalPreferencesVisible(_isSheetMetalLibrary);

        var materialNames = _currentLibrary.Materials.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _libraryRows = _isSheetMetalLibrary
            ? _sheetMetalTable.Rows.Where(r => materialNames.Contains(r.PhysicalMaterialName)).ToList()
            : Array.Empty<SheetMetalMaterialRow>();

        RefreshMaterialTree();

        // Resolution against the newly loaded library may change which rows now have a ResolvedMaterialId,
        // even though body-to-material-name assignments themselves haven't changed.
        RefreshAssignmentTree();
    }

    /// <summary>Rebuilds the material tree for the current library. For a sheet metal library it also refills the
    /// filter dropdowns, and lists under each physical material the standards file rows the filters leave. With no
    /// filter, every physical material is listed, so one with no row can still go on a plain solid; with a filter,
    /// only the materials that still have a row are.</summary>
    private void RefreshMaterialTree()
    {
        if (_currentLibrary is null)
            return;

        if (!_isSheetMetalLibrary)
        {
            _filteredRows = Array.Empty<SheetMetalMaterialRow>();
            _blocks.PopulateMaterialTree(_categoryTreeBuilder.Build(_currentLibrary));
        }
        else
        {
            _blocks.PopulateSheetMetalFilters(_libraryRows, _filter);

            // Read back: a value the other filters no longer allow falls back to "(Any)" in its dropdown.
            _filter = _blocks.GetSheetMetalFilter();

            // The rule's own row list follows the Standard, so the rows it offers are the rows the tree shows.
            _standardSelection.Selected = _filter.Standard;

            _filteredRows = _filter.Apply(_libraryRows);
            var rowsByMaterial = _filteredRows
                .GroupBy(r => r.PhysicalMaterialName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<SheetMetalMaterialRow>)g.ToList(), StringComparer.OrdinalIgnoreCase);

            var materials = _filter.IsEmpty
                ? _currentLibrary.Materials
                : _currentLibrary.Materials.Where(m => rowsByMaterial.ContainsKey(m.Name)).ToList();

            _blocks.PopulateMaterialTree(
                _categoryTreeBuilder.Build(_currentLibrary with { Materials = materials }),
                m => rowsByMaterial.TryGetValue(m.Name, out var rows) ? rows : Array.Empty<SheetMetalMaterialRow>());
        }

        // A rebuilt tree invalidates the previously picked material.
        _selectedPick = null;
        _blocks.SetMaterialLabel(null);
    }

    public void OnMaterialSelected(MaterialPick? pick)
    {
        _selectedPick = pick;
        _blocks.SetMaterialLabel(pick);
    }

    // Hover only retitles the label; it deliberately does not change _selectedPick, or moving the
    // pointer across the tree would silently repoint what an Assign command acts on.
    public void OnMaterialHovered(MaterialPick? pick) => _blocks.SetMaterialLabel(pick ?? _selectedPick);

    public void OnAssignmentSelected(AssignmentRowRef? row) => _blocks.SetAssignmentLabel(row);

    public void OnAssignmentDefaultAction(AssignmentRowRef row) => AddSolidBodiesToSelection(row.Bodies);

    public void OnPendingSelected(PendingRowRef? row) => _blocks.SetPendingLabel(row);

    public string OnMaterialTooltip(MaterialPick pick)
    {
        var material = pick.Material;
        var lines = new List<string> { pick.DisplayName };

        if (pick.Row is { } row)
        {
            lines.Add($"Sheet metal material made of {material.Name}");
            lines.Add(BlockAccessor.SheetMetalRowText(row));
        }

        if (material.Category.PathSegments.Count > 0)
            lines.Add(string.Join(" > ", material.Category.PathSegments));
        else
            lines.Add(material.Category.DisplayName);

        if (!string.IsNullOrWhiteSpace(material.Description))
            lines.Add(material.Description!);

        return string.Join(Environment.NewLine, lines);
    }

    public void OnMaterialDefaultAction(MaterialPick pick) => ShowProperties(pick.Material);

    // ---- Material tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildMaterialMenu(MaterialPick? clicked)
    {
        if (clicked is null)
            return new[] { new TreeMenuItem(MaterialMenu.Refresh, "Refresh") };

        // Assignment needs a target, and the selection block is the only source of one.
        var hasBodies = _blocks.GetSelectedBodyIds().Count > 0;

        var items = new List<TreeMenuItem>
        {
            new(MaterialMenu.AssignNow, "Assign to selected bodies", hasBodies),
            new(MaterialMenu.AddToPending, "Add to pending", hasBodies),
        };

        // Sets the part's preferences straight away — no bodies, nothing staged. Only a row can be applied.
        if (_isSheetMetalLibrary)
            items.Add(new TreeMenuItem(MaterialMenu.ApplySheetMetalPreference, "Apply sheet metal preference", clicked.Row is not null));

        items.Add(TreeMenuItem.Separator);
        items.Add(new TreeMenuItem(MaterialMenu.Properties, "Properties..."));
        items.Add(new TreeMenuItem(MaterialMenu.Refresh, "Refresh"));
        return items;
    }

    public void OnMaterialMenuCommand(int menuItemId, IReadOnlyList<MaterialPick> targets)
    {
        var pick = targets.FirstOrDefault() ?? _selectedPick;

        switch (menuItemId)
        {
            case MaterialMenu.Refresh:
                OnRefreshClicked();
                return;
            case MaterialMenu.Properties when pick is not null:
                ShowProperties(pick.Material);
                return;
            case MaterialMenu.AssignNow when pick is not null:
                AssignNow(pick);
                return;
            case MaterialMenu.AddToPending when pick is not null:
                AddToPending(pick);
                return;
            case MaterialMenu.ApplySheetMetalPreference when pick?.Row is { } row:
                ApplySheetMetalPreferenceOnly(row);
                return;
        }
    }

    /// <returns>False when nothing was applied: the entry could not be built or conflicted.</returns>
    private bool AssignNow(MaterialPick pick)
    {
        var entry = BuildCheckedEntry(pick);
        if (entry is null)
            return false;

        CommitEntry(entry);
        OnRefreshClicked();
        return true;
    }

    private void AddToPending(MaterialPick pick)
    {
        var entry = BuildCheckedEntry(pick);
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

            _pending[i] = Replan(entry, keptRows.Select(r => r.Body).ToList());
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
    private PendingAssignmentEntry? BuildEntry(MaterialPick pick)
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

        return Replan(pick.Material, pick.Row, targets);
    }

    /// <summary>Plans the selection and refuses a sheet metal body with no row picked or a second sheet metal
    /// material, before anything is applied or staged. Returns null when the entry cannot be built or
    /// conflicts.</summary>
    private PendingAssignmentEntry? BuildCheckedEntry(MaterialPick pick)
    {
        var entry = BuildEntry(pick);
        if (entry is null)
            return null;

        // The Sheet Metal Preferences are set to the row the user picks in the tree, never to one guessed from the
        // physical material. Bodies a rule already blocks are left to that rule's own message.
        if (pick.Row is null
            && entry.Rows.Any(r => r.Body.Kind == BodyKind.SheetMetal && r.Status != PendingBodyStatus.Blocked))
        {
            _blocks.ShowError(
                $"Sheet metal bodies need a sheet metal material, not just '{pick.Material.Name}'.{Environment.NewLine}{Environment.NewLine}" +
                $"Expand '{pick.Material.Name}' in the material tree and pick one of its rows (Standard, thickness, bend " +
                "radius) — that is what the part's Sheet Metal Preferences are set to. Use the Sheet Metal Preferences " +
                "filters to narrow the rows.");
            return null;
        }

        if (FindSheetMetalConflict(entry.Material, entry.SheetMetalRow, entry.Rows.Select(r => r.Body).ToList()) is { } conflict)
        {
            _blocks.ShowError(conflict);
            return null;
        }

        return entry;
    }

    /// <summary>NX keeps one set of Sheet Metal Preferences per part, so every sheet metal body in it has to share
    /// a material and a preferences row — assigning a second one would re-point the preferences out from under the
    /// first. Returns why <paramref name="material"/> (as <paramref name="row"/>) cannot go onto
    /// <paramref name="targets"/>, or null when it can.
    ///
    /// Only the sheet metal bodies outside <paramref name="targets"/> count: the targets are about to take
    /// <paramref name="material"/>, so re-assigning every sheet metal body at once is not a conflict. A body
    /// claimed by a staged entry counts as that entry's material and row, since that is what it will have. A body
    /// already assigned counts as the part's current preferences row when that row is made of its material. Regular
    /// solids have no shared state, so a selection with no sheet metal body never conflicts.</summary>
    private string? FindSheetMetalConflict(Material material, SheetMetalMaterialRow? row, IReadOnlyList<BodyInfo> targets)
    {
        if (!targets.Any(b => b.Kind == BodyKind.SheetMetal))
            return null;

        var targetIds = targets.Select(b => b.Id).ToHashSet();

        var stagedEntries = new Dictionary<BodyId, PendingAssignmentEntry>();
        foreach (var entry in _pending)
        {
            foreach (var pendingRow in entry.Rows)
                stagedEntries[pendingRow.Body.Id] = entry;
        }

        // Read lazily, once: it is an NX query, and most parts have no other sheet metal body to compare against.
        SheetMetalMaterialRow? partRow = null;
        var partRowRead = false;

        var conflicting = new List<(BodyInfo Body, string Material, bool IsStaged)>();
        foreach (var body in _allBodies.Where(b => b.Kind == BodyKind.SheetMetal && !targetIds.Contains(b.Id)))
        {
            string? otherMaterial;
            SheetMetalMaterialRow? otherRow;
            var isStaged = stagedEntries.TryGetValue(body.Id, out var staged);

            if (isStaged)
            {
                otherMaterial = staged!.Material.Name;
                otherRow = staged.SheetMetalRow;
            }
            else
            {
                otherMaterial = _currentAssignments.TryGetValue(body.Id, out var current) ? current.MaterialName : null;
                if (otherMaterial is null)
                    continue;

                if (!partRowRead)
                {
                    partRow = _sheetMetalPreferences.ReadForPart().Preference?.Row;
                    partRowRead = true;
                }

                otherRow = partRow is not null && SameName(partRow.PhysicalMaterialName, otherMaterial) ? partRow : null;
            }

            // An unknown row on either side cannot be told apart, so only the material is compared then.
            var sameMaterial = SameName(otherMaterial, material.Name);
            var sameRow = row is null || otherRow is null || SameName(otherRow.Name, row.Name);
            if (sameMaterial && sameRow)
                continue;

            conflicting.Add((body, otherRow is null ? otherMaterial : $"{otherMaterial} [{otherRow.Name}]", isStaged));
        }

        if (conflicting.Count == 0)
            return null;

        var name = row is null ? material.Name : $"{material.Name} [{row.Name}]";
        var details = conflicting.Select(x => $"  [{x.Body.Name}] {x.Material}{(x.IsStaged ? " (pending)" : string.Empty)}");
        return
            $"{name} cannot be assigned to sheet metal bodies in this part.{Environment.NewLine}{Environment.NewLine}" +
            $"Sheet Metal Preferences belong to the part, so every sheet metal body in it must have the same " +
            $"material and preferences row, and these already have a different one:{Environment.NewLine}" +
            string.Join(Environment.NewLine, details) +
            $"{Environment.NewLine}{Environment.NewLine}Nothing was assigned or staged. Assign {name} to all of the " +
            "sheet metal bodies together, or leave the sheet metal bodies out of the selection.";
    }

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <param name="row">The sheet metal row picked with the material, which the rules check and the Sheet Metal
    /// Preferences are set to. Null when the material was picked on its own.</param>
    private PendingAssignmentEntry Replan(Material material, SheetMetalMaterialRow? row, IReadOnlyList<BodyInfo> targets)
    {
        var input = new MaterialAssignmentPlanningInput(material, targets, _currentAssignments) { RequestedVariant = row?.Name };
        return PendingAssignmentEntry.Create(input, _planner.Plan(input)) with { SheetMetalRow = row };
    }

    /// <summary>Re-plans a staged entry onto <paramref name="targets"/>, keeping the sheet metal row picked when it was
    /// staged.</summary>
    private PendingAssignmentEntry Replan(PendingAssignmentEntry entry, IReadOnlyList<BodyInfo> targets) =>
        Replan(entry.Material, entry.SheetMetalRow, targets);

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
                _pending[i] = Replan(entry, stillPresent);
        }

        _pending.RemoveAll(e => e.Rows.Count == 0);
        _blocks.PopulatePendingTree(_pending);
    }

    /// <summary>Applies one entry, first asking for confirmation for the bodies whose rules need it.</summary>
    private void CommitEntry(PendingAssignmentEntry entry)
    {
        if (!entry.HasAnyApplicableBody)
        {
            // The reasons matter most on "Assign now", which never shows the pending tree they would be listed in.
            var reasons = entry.Rows.Select(r => r.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct();
            _blocks.ShowError(
                $"Every body staged for {entry.DisplayName} is blocked by a rule; nothing was applied." +
                $"{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, reasons)}");
            return;
        }

        // Checked again because the dialog is interactive: the part may have gained a sheet metal material since
        // the entry was staged.
        if (FindSheetMetalConflict(entry.Material, entry.SheetMetalRow, entry.Rows.Select(r => r.Body).ToList()) is { } conflict)
        {
            _blocks.ShowError(conflict);
            return;
        }

        var confirmedBodyIds = GetConfirmedBodyIds(entry);

        var executablePlan = _finalizer.Finalize(entry.Plan, entry.Input, confirmedBodyIds);
        var result = _partMaterialService.ApplyPlan(executablePlan);
        RecordApplied(result, $"{entry.DisplayName} assigned to {executablePlan.Assignments.Count} body(ies)");

        // Warnings never stop an assignment, but "Assign now" skips the pending tree where they would otherwise
        // be seen — for example a bead on the body that matches no SPEC, whose restriction is therefore not
        // enforced. They are appended to the result so the user learns of them either way.
        var warnings = entry.Rows
            .Where(r => r.Status == PendingBodyStatus.Ok && !string.IsNullOrWhiteSpace(r.Message))
            .Select(r => $"[{r.Body.Name}] {r.Message}")
            .ToList();

        var summary = $"{entry.DisplayName} applied to {executablePlan.Assignments.Count} body(ies).";
        if (warnings.Count > 0)
            summary += $"{Environment.NewLine}{Environment.NewLine}Warnings:{Environment.NewLine}{string.Join(Environment.NewLine, warnings)}";

        _blocks.ShowResult(result, summary);

        if (executablePlan.SkippedBlocked.Count > 0 || executablePlan.SkippedDeclinedConfirmation.Count > 0)
        {
            _context.Log.Info(
                $"Skipped {executablePlan.SkippedBlocked.Count} blocked, " +
                $"{executablePlan.SkippedDeclinedConfirmation.Count} declined body(ies).");
        }
    }

    /// <summary>Asks the user to confirm the bodies whose rules returned RequireConfirmation — chiefly
    /// reassignment over an existing material, coating display-material mismatches, and a sheet metal row at another
    /// thickness than the body.
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
