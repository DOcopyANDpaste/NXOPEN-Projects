using BANxOpen.Materials.Core.Assignment;
using BANxOpen.Materials.Core.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Materials.Core.Library;
using BANxOpen.Materials.Contracts;
using BANxOpen.Foundation.NxAdapters;
using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Materials.Core.Rules.SheetMetal;
using BANxOpen.SheetMetal.Materials;
using BANxOpen.SheetMetal.NxAdapters.Common;
using BANxOpen.SheetMetal.Common;
using BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>All dialog logic — the generated <c>BlockUI.cs</c> stays a thin set of delegations to this
/// class, per Skills/with-block-ui.md §1.
///
/// Assigning previews. "Assign to selected bodies" on the material tree plans the selection, asks the rule
/// confirmations, and applies the material to the part straight away, on top of <see cref="_previewUndo"/> — a mark
/// set the first time something is previewed. What was applied is listed in the pending tree (blocked and declined
/// bodies are reported then and never listed). OK/Apply keep the preview by committing that mark; Cancel, "Remove
/// from pending" and "Clear all pending" undo to it, re-applying whatever is still pending.
/// Target bodies always come from the <c>Sel_SoildBodies</c> selection block.
///
/// <see cref="_sessionUndo"/> is the older mark set when the dialog opens, so that Cancel can also offer to revert
/// what was applied (kept) while it was open. Sheet-metal-specific checks and messages live in
/// the <c>SheetMetalSupport</c> folder.</summary>
public sealed class MaterialAssignmentDialogPresenter : ITreeInteractionSink
{
    // Context-menu ids. Distinct ranges per tree so a stray id can never be mistaken for another tree's
    // command while debugging.
    private static class MaterialMenu
    {
        public const int Assign = 101;
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
    private BANxOpen.Materials.Contracts.MaterialLibrary? _currentLibrary;
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

    // The part's Sheet Metal Preferences, re-read with the body state. Null outside sheet metal parts.
    private SheetMetalPartPreference? _partPreference;

    // How each sheet metal body stands against _partPreference, re-read with it. Empty when there are none.
    private IReadOnlyDictionary<BodyId, SheetMetalPreferenceCheckResult> _preferenceChecks =
        new Dictionary<BodyId, SheetMetalPreferenceCheckResult>();

    // One-time steps of the first dialog show: the filters start from the part's preferences row, and a part whose
    // bodies disagree with that row is pointed out.
    private bool _filterSeeded;
    private bool _initialSyncChecked;

    // What was changed in the part while the dialog was open, for Cancel to list, and the mark it reverts to.
    private readonly List<string> _appliedThisSession = new();
    private UndoScope? _sessionUndo;

    // The mark the pending previews sit on. Null while nothing is previewed.
    private UndoScope? _previewUndo;

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

        _blocks.ShowSheetMetalGradeColumn = _context.IsSheetMetalApplication;
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

        CheckPreferenceSyncOnOpen();
    }

    /// <summary>On the first show only, when the part's Sheet Metal Preferences are set: says which sheet metal bodies
    /// are not in sync with them (see <see cref="SheetMetalPreferenceSync.Evaluate"/>). When the part has a single sheet
    /// metal body and its material is what disagrees, that body is also put in the selection, so picking the row (which
    /// the filters already narrow to) and Apply syncs them.</summary>
    private void CheckPreferenceSyncOnOpen()
    {
        if (_initialSyncChecked)
            return;
        _initialSyncChecked = true;

        if (!_isSheetMetalLibrary
            || _partPreference is not { IsMaterialTableEntry: true } preference
            || string.IsNullOrWhiteSpace(preference.MaterialName))
        {
            return;
        }

        var sheetMetalBodies = _allBodies.Where(b => b.Kind == BodyKind.SheetMetal).ToList();
        var notInSync = sheetMetalBodies
            .Where(b => _preferenceChecks.TryGetValue(b.Id, out var check) && check.Status != SheetMetalPreferenceStatus.InSync)
            .Select(b => new SheetMetalPreferenceSync.BodyCheck(b, _preferenceChecks[b.Id]))
            .ToList();
        if (notInSync.Count == 0)
            return;

        var autoSelect = sheetMetalBodies.Count == 1 && notInSync[0].Check.Status == SheetMetalPreferenceStatus.MaterialOutOfSync;
        if (autoSelect)
            _blocks.SetSelectedBodies(new[] { sheetMetalBodies[0].Id });

        _blocks.ShowInfo("Sheet Metal Preferences", SheetMetalPreferenceSync.OpenMessage(preference, notInSync, autoSelect));
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
                // Pending entries keep the row they were previewed with; only what can be picked next changes.
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

    /// <summary>Keeps what is previewed. With nothing pending and a sheet metal material picked (see
    /// <see cref="PickedSheetMetalMaterial"/>), applies that instead — see <see cref="ApplyPickedSheetMetalMaterial"/>.</summary>
    public int OnApply()
    {
        if (_pending.Count > 0)
        {
            CommitPreview();
            return 0;
        }

        if (PickedSheetMetalMaterial() is { } pick)
            return ApplyPickedSheetMetalMaterial(pick) ? 0 : 1;

        _blocks.ShowError(
            "Nothing is pending. Select bodies, then right-click a material and choose \"Assign to selected bodies\"." +
            (_isSheetMetalLibrary
                ? $"{Environment.NewLine}{Environment.NewLine}For a sheet metal library you can also pick a sheet metal " +
                  "material (a row under a physical material) and Apply it."
                : string.Empty));
        return 1;
    }

    // Unlike OnApply, an empty pending list on OK is a normal end state — not something worth blocking the dialog's
    // close on. A picked sheet metal material is applied the same way Apply would when it has somewhere to go; backing
    // out of it keeps the dialog open. With bodies in the part but none selected there is nothing for it to go on
    // (see ApplyPickedSheetMetalMaterial), so OK just closes rather than trapping the user behind that error.
    public int OnOk()
    {
        if (_pending.Count > 0)
        {
            CommitPreview();
        }
        else if (PickedSheetMetalMaterial() is { } pick
                 && (_blocks.GetSelectedBodyIds().Count > 0 || !PartHasSolid)
                 && !ApplyPickedSheetMetalMaterial(pick))
        {
            return 1;
        }

        _blocks.ClearHighlight();
        CloseKeepingChanges();
        return 0;
    }

    /// <summary>Keeps every previewed assignment. Nothing is applied again — the preview already is the change; this
    /// only commits the preview mark so nothing undoes to it any more, and lists the entries for Cancel's
    /// revert-or-keep prompt.</summary>
    private void CommitPreview()
    {
        _previewUndo?.Commit();
        _previewUndo?.Dispose();
        _previewUndo = null;

        foreach (var entry in _pending)
            _appliedThisSession.Add($"{entry.DisplayName} assigned to {entry.Rows.Count} body(ies)");

        _pending.Clear();
        OnRefreshClicked();
    }

    /// <summary>Undoes every previewed assignment and empties the pending list.</summary>
    private void RevertPreview()
    {
        _previewUndo?.Dispose();
        _previewUndo = null;
        _pending.Clear();
    }

    /// <summary>Cancel (and the title bar's close, which NX routes to the same callback). Previewed entries were
    /// never kept, so they are reverted after a warning. Anything kept while the dialog was open — Apply, "Remove
    /// material", Sheet Metal Preferences — is listed, and the user chooses whether to revert to the state the part
    /// was in when the dialog opened or keep it.</summary>
    /// <returns>1 to keep the dialog open, 0 to let it close.</returns>
    public int OnCancel()
    {
        var previewNote = _pending.Count > 0
            ? $"{_pending.Count} previewed assignment(s) were never applied and will be reverted."
            : null;

        if (_appliedThisSession.Count == 0)
        {
            if (previewNote is not null && !_blocks.Confirm($"{previewNote}{Environment.NewLine}{Environment.NewLine}Close anyway?"))
                return 1;

            RevertPreview();
            CloseKeepingChanges();
            _blocks.ClearHighlight();
            return 0;
        }

        var message =
            $"These changes were applied while this dialog was open:{Environment.NewLine}" +
            string.Join(Environment.NewLine, _appliedThisSession.Select(c => $"  {c}")) +
            (previewNote is null ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{previewNote}") +
            $"{Environment.NewLine}{Environment.NewLine}Revert them?{Environment.NewLine}" +
            $"  Yes - revert to the state the part was in before this dialog opened.{Environment.NewLine}" +
            "  No - keep the changes.";

        var revert = _blocks.Confirm(message);

        // The preview mark is newer than the session mark, so it goes first either way.
        RevertPreview();

        if (revert)
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
    /// from it. The dialog is interactive, so the model can change underneath it. Pending entries are already
    /// applied, so they are not re-planned — that would show them reassigning themselves — only pruned of bodies
    /// that are gone.</summary>
    public void OnRefreshClicked()
    {
        RefreshBodyState();
        RefreshAssignmentTree();
        PrunePending();
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
        var material = _currentLibrary.Materials.FirstOrDefault(m => SheetMetalBodies.SameName(m.Name, row.PhysicalMaterialName));
        return material is null ? null : new MaterialPick(material, row);
    }

    /// <summary>Apply/OK with a sheet metal material picked and nothing pending. With bodies selected it assigns the
    /// material to them, as "Assign to selected bodies" would, and keeps it at once. With none, it sets only the part's
    /// Sheet Metal Preferences, after warning that the bodies' own material and colour are left as they are — but only
    /// on a part with no solid body: once there are bodies, the preferences go with them, so they are never set on
    /// their own.</summary>
    /// <returns>False when nothing was applied — the user backed out, the part has bodies to assign, or NX refused.</returns>
    private bool ApplyPickedSheetMetalMaterial(MaterialPick pick)
    {
        if (_blocks.GetSelectedBodyIds().Count > 0)
        {
            if (!Preview(pick))
                return false;

            CommitPreview();
            return true;
        }

        var row = pick.Row!;
        if (PartHasSolid)
        {
            _blocks.ShowError(
                $"No solid body is selected. This part has bodies, so '{row.Name}' has to be assigned to them: the Sheet " +
                "Metal Preferences are set together with the bodies' material, which keeps the two in sync." +
                $"{Environment.NewLine}{Environment.NewLine}Select the bodies, then Apply again.");
            return false;
        }

        var message =
            $"This part has no solid body, so only its Sheet Metal Preferences will be set to '{row.Name}' " +
            $"({row.Describe()}).{Environment.NewLine}{Environment.NewLine}Continue?";

        return _blocks.Confirm(message) && ApplySheetMetalPreferenceOnly(row);
    }

    /// <summary>Sets the part's Sheet Metal Preferences to <paramref name="row"/> and nothing else — no body is
    /// assigned and nothing is previewed. Recorded like any other change, so Cancel can revert it.</summary>
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

        _blocks.ShowResult(result, $"Sheet Metal Preferences set to '{row.Name}' ({row.Describe()}).");

        RecordApplied(result, $"Sheet Metal Preferences set to {row.Name}");
        OnRefreshClicked();
        return result.Ok;
    }

    // ---- Quick selection buttons ----

    private bool PartHasSolid => _allBodies.Any(SheetMetalBodies.IsSolid);

    public void OnSelectAllSolidsClicked()
    {
        var ids = _allBodies.Where(SheetMetalBodies.IsSolid).Select(b => b.Id).ToList();
        _blocks.SetSelectedBodies(ids);
    }

    public void OnSelectUnassignedSolidsClicked()
    {
        var unassigned = _currentAssignments.Values
            .Where(a => a.MaterialName is null)
            .Select(a => a.BodyId)
            .ToHashSet();

        var ids = _allBodies
            .Where(b => SheetMetalBodies.IsSolid(b) && unassigned.Contains(b.Id))
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

        // The first sheet metal library starts from the part's own preferences row, when NX is using one this library
        // lists ("(Any)" otherwise); after that the user's filters stand.
        if (_isSheetMetalLibrary && !_filterSeeded)
        {
            _filter = _partPreference is { IsMaterialTableEntry: true, Row: { } row }
                      && _libraryRows.Any(r => SheetMetalBodies.SameName(r.Name, row.Name))
                ? SheetMetalRowFilter.ForRow(row)
                : SheetMetalRowFilter.None;
            _filterSeeded = true;
        }

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
            lines.Add(row.Describe());
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
            new(MaterialMenu.Assign, "Assign to selected bodies", hasBodies),
        };

        // Sets the part's preferences straight away — no bodies, nothing previewed. Only a row can be applied, and
        // only on a part with no solid body: once there are bodies, the preferences go with their material.
        if (_isSheetMetalLibrary && !PartHasSolid)
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
            case MaterialMenu.Assign when pick is not null:
                Preview(pick);
                return;
            case MaterialMenu.ApplySheetMetalPreference when pick?.Row is { } row:
                ApplySheetMetalPreferenceOnly(row);
                return;
        }
    }

    // ---- Preview ----

    /// <summary>What applying one entry as a preview did: the entry as it now stands in the pending list (only the
    /// bodies that were applied), or null when nothing was; and what the user has to be told.</summary>
    private sealed record PreviewOutcome(
        PendingAssignmentEntry? Applied,
        IReadOnlyList<string> NotApplied,
        IReadOnlyList<string> Warnings,
        string? Failure);

    /// <summary>Plans the selected bodies against <paramref name="pick"/>, asks the confirmations its rules need, and
    /// applies it to the part as a preview listed in the pending tree. A body already pending is taken out of its
    /// earlier entry first, so one body is never claimed twice. Blocked and declined bodies are reported, not listed.</summary>
    /// <returns>False when nothing was applied.</returns>
    private bool Preview(MaterialPick pick)
    {
        var selected = _blocks.GetSelectedBodyIds().ToHashSet();
        if (selected.Count == 0)
        {
            _blocks.ShowError("Select at least one body before assigning a material.");
            return false;
        }

        // The earlier preview of a reclaimed body has to come off before the new one is planned, or the plan would
        // compare against the previewed material rather than the one the body really has.
        var previous = _pending.ToList();
        var reclaims = previous.Any(e => e.Rows.Any(r => selected.Contains(r.Body.Id)));
        if (reclaims)
            RebuildPreview(WithoutBodies(previous, selected), render: false);

        var entry = BuildCheckedEntry(pick);
        if (entry is null)
        {
            if (reclaims)
                RebuildPreview(previous);
            return false;
        }

        var outcome = ApplyPreview(entry, GetConfirmedBodyIds(entry));
        if (outcome.Applied is { } applied)
            _pending.Add(applied);
        else if (reclaims)
            RebuildPreview(previous, render: false);

        ReportPreview(entry.DisplayName, outcome);

        RefreshBodyState();
        RefreshAssignmentTree();
        _blocks.PopulatePendingTree(_pending);
        return outcome.Applied is not null;
    }

    /// <summary>Applies <paramref name="entry"/> on top of the preview mark, setting the mark first if nothing is
    /// previewed yet. Does not touch <see cref="_pending"/>.</summary>
    private PreviewOutcome ApplyPreview(PendingAssignmentEntry entry, HashSet<BodyId> confirmedBodyIds)
    {
        var notApplied = entry.Rows
            .Where(r => r.Status == PendingBodyStatus.Blocked)
            .Select(r => $"  [{r.Body.Name}] blocked: {r.Message}")
            .Concat(entry.Rows
                .Where(r => r.Status == PendingBodyStatus.NeedsConfirmation && !confirmedBodyIds.Contains(r.Body.Id))
                .Select(r => $"  [{r.Body.Name}] skipped: not confirmed"))
            .ToList();

        var executablePlan = _finalizer.Finalize(entry.Plan, entry.Input, confirmedBodyIds);
        if (executablePlan.Assignments.Count == 0)
            return new PreviewOutcome(null, notApplied, Array.Empty<string>(), null);

        _previewUndo ??= new UndoScope(_context.Session, "Material Assignment preview", _context.Log.Error);
        var result = _partMaterialService.ApplyPlan(executablePlan);

        // A partial failure still changed the part, and NX does not say which bodies failed; they are kept, and the
        // assignment tree shows what each body really has.
        if (!result.Ok && result.ErrorCode != "PARTIAL_FAILURE")
            return new PreviewOutcome(null, notApplied, Array.Empty<string>(), result.Message ?? "NX did not apply the material.");

        var appliedIds = executablePlan.Assignments.Select(a => a.BodyId).ToHashSet();
        var appliedRows = entry.Rows.Where(r => appliedIds.Contains(r.Body.Id)).ToList();

        // Warnings never stop an assignment, but they are the only thing the user would otherwise never see — for
        // example a bead on the body that matches no SPEC, whose restriction is therefore not enforced.
        var warnings = appliedRows
            .Where(r => r.Status == PendingBodyStatus.Ok && !string.IsNullOrWhiteSpace(r.Message))
            .Select(r => $"  [{r.Body.Name}] {r.Message}")
            .ToList();

        var applied = entry with { Rows = appliedRows, ConfirmedBodyIds = confirmedBodyIds.ToList() };
        return new PreviewOutcome(applied, notApplied, warnings, result.Ok ? null : result.Message);
    }

    /// <summary>One message for everything about a preview the user cannot see in the part: blocked and declined
    /// bodies, warnings and NX failures. A clean preview needs no message — the part and the pending tree show it.</summary>
    private void ReportPreview(string displayName, PreviewOutcome outcome)
    {
        var sections = new List<string>();
        if (outcome.Failure is not null)
            sections.Add(outcome.Failure);
        if (outcome.NotApplied.Count > 0)
            sections.Add($"Not applied:{Environment.NewLine}{string.Join(Environment.NewLine, outcome.NotApplied)}");
        if (outcome.Warnings.Count > 0)
            sections.Add($"Warnings:{Environment.NewLine}{string.Join(Environment.NewLine, outcome.Warnings)}");

        if (sections.Count == 0)
            return;

        var details = string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);
        if (outcome.Applied is null)
        {
            _blocks.ShowError($"{displayName} was not applied to any body.{Environment.NewLine}{Environment.NewLine}{details}");
        }
        else
        {
            _blocks.ShowInfo("Material Assignment",
                $"{displayName} previewed on {outcome.Applied.Rows.Count} body(ies).{Environment.NewLine}{Environment.NewLine}{details}");
        }
    }

    /// <summary>Undoes every preview and re-applies <paramref name="entries"/> in order, each re-planned against the
    /// part as it now stands. The confirmations each entry was given are reused; a body that newly needs one is asked
    /// again. Used to take bodies or entries out of the preview, which a single undo mark can only do by redoing what
    /// stays.</summary>
    /// <param name="render">False when the caller re-renders the trees itself.</param>
    private void RebuildPreview(IReadOnlyList<PendingAssignmentEntry> entries, bool render = true)
    {
        RevertPreview();
        RefreshBodyState();

        var report = new List<string>();
        foreach (var old in entries)
        {
            var bodies = old.Rows
                .Select(r => _allBodies.FirstOrDefault(b => b.Id == r.Body.Id))
                .OfType<BodyInfo>()
                .ToList();
            if (bodies.Count == 0)
                continue;

            var entry = Replan(old.Material, old.SheetMetalRow, bodies);
            var confirmed = old.ConfirmedBodyIds.ToHashSet();
            var unanswered = entry with { Rows = entry.Rows.Where(r => !confirmed.Contains(r.Body.Id)).ToList() };
            confirmed.UnionWith(GetConfirmedBodyIds(unanswered));

            var outcome = ApplyPreview(entry, confirmed);
            if (outcome.Applied is { } applied)
                _pending.Add(applied);

            var lines = outcome.NotApplied.Concat(outcome.Warnings).ToList();
            if (outcome.Failure is not null)
                lines.Insert(0, $"  {outcome.Failure}");
            if (lines.Count > 0)
                report.Add($"{entry.DisplayName}:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");

            // The next entry is planned against what this one did.
            RefreshBodyState();
        }

        if (report.Count > 0)
        {
            _blocks.ShowInfo("Material Assignment",
                $"The pending previews were re-applied, with these notes:{Environment.NewLine}{Environment.NewLine}" +
                string.Join($"{Environment.NewLine}{Environment.NewLine}", report));
        }

        if (render)
        {
            RefreshAssignmentTree();
            _blocks.PopulatePendingTree(_pending);
        }
    }

    /// <summary><paramref name="entries"/> with <paramref name="bodyIds"/> taken out, dropping any entry left with
    /// no body. Not re-planned: <see cref="RebuildPreview"/> re-plans against the part once the preview is undone.</summary>
    private static List<PendingAssignmentEntry> WithoutBodies(IEnumerable<PendingAssignmentEntry> entries, IReadOnlyCollection<BodyId> bodyIds) =>
        entries
            .Select(e => e with { Rows = e.Rows.Where(r => !bodyIds.Contains(r.Body.Id)).ToList() })
            .Where(e => e.Rows.Count > 0)
            .ToList();

    /// <summary>Drops pending bodies that are no longer in the work part, and entries left with none.</summary>
    private void PrunePending()
    {
        var present = _allBodies.Select(b => b.Id).ToHashSet();
        var pruned = WithoutBodies(_pending, _pending.SelectMany(e => e.BodyIds).Where(id => !present.Contains(id)).ToList());

        _pending.Clear();
        _pending.AddRange(pruned);
        _blocks.PopulatePendingTree(_pending);
    }

    // ---- Current assignment tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildAssignmentMenu(AssignmentRowRef? clicked)
    {
        if (clicked is null)
            return new[] { new TreeMenuItem(AssignmentMenu.Refresh, "Refresh") };

        var isUnassigned = clicked.Row.IsUnassignedBucket;
        var hasSolidBody = clicked.Bodies.Any(SheetMetalBodies.IsSolid);

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
        var solidIds = bodies.Where(SheetMetalBodies.IsSolid).Select(b => b.Id).ToList();
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

        // A clear made while previews are pending would sit on the preview mark and be undone with them.
        if (_pending.Count > 0)
        {
            _blocks.ShowError("Assignments are pending. Apply them, or remove them from pending, before removing a material.");
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

        // Nothing is committed from here: Apply/OK keep every preview at once, since the previews stack on one undo mark.
        return new[]
        {
            new TreeMenuItem(PendingMenu.Remove, "Remove from pending"),
            TreeMenuItem.Separator,
            clearAll,
        };
    }

    /// <summary>Removing from pending reverts the preview: the whole preview is undone and what stays is re-applied
    /// (see <see cref="RebuildPreview"/>).</summary>
    public void OnPendingMenuCommand(int menuItemId, IReadOnlyList<PendingRowRef> targets)
    {
        switch (menuItemId)
        {
            case PendingMenu.ClearAll:
                RebuildPreview(Array.Empty<PendingAssignmentEntry>());
                return;

            case PendingMenu.Remove:
                // A root row removes the whole entry; a body row removes just that body from it.
                var removedEntries = targets.Where(t => t.Row is null).Select(t => t.Entry).ToHashSet();
                var removedBodies = targets.Where(t => t.Row is not null).Select(t => t.Row!.Body.Id).ToHashSet();
                RebuildPreview(WithoutBodies(_pending.Where(e => !removedEntries.Contains(e)), removedBodies));
                return;
        }
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
    /// material, before anything is previewed. Returns null when the entry cannot be built or
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

    /// <summary>See <see cref="SheetMetalConflictFinder.Find"/>. A previewed body already carries its new material,
    /// and counts as its pending entry's row.</summary>
    private string? FindSheetMetalConflict(Material material, SheetMetalMaterialRow? row, IReadOnlyList<BodyInfo> targets) =>
        SheetMetalConflictFinder.Find(material, row, targets, _allBodies, _currentAssignments, _pending, () => _partPreference?.Row);

    /// <param name="row">The sheet metal row picked with the material, which the rules check and the Sheet Metal
    /// Preferences are set to. Null when the material was picked on its own.</param>
    private PendingAssignmentEntry Replan(Material material, SheetMetalMaterialRow? row, IReadOnlyList<BodyInfo> targets)
    {
        var input = new MaterialAssignmentPlanningInput(material, targets, _currentAssignments) { RequestedVariant = row?.Name };
        return PendingAssignmentEntry.Create(input, _planner.Plan(input)) with { SheetMetalRow = row };
    }

    /// <summary>Asks the user to confirm the bodies whose rules returned RequireConfirmation — chiefly
    /// reassignment over an existing material, coating display-material mismatches, and a sheet metal row at another
    /// thickness than the body.
    ///
    /// All-or-nothing per entry: the prompt lists which bodies need confirmation and why. The finalizer's per-body
    /// handling is untouched, so a per-row control could replace this later without changing anything downstream.
    /// Declining skips those bodies — they are reported and left out of the preview; the rest of the entry still
    /// applies.</summary>
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

        _context.Log.Debug($"ShowProperties: opening property window for '{material.Name}'.");
        _propertyWindow.Show(material);
        _context.Log.Debug("ShowProperties: property window returned control to the main dialog.");
    }

    // ---- State refresh ----

    private void RefreshBodyState()
    {
        _allBodies = _partMaterialService.GetBodies();
        _currentAssignments = _partMaterialService.GetCurrentAssignments();

        // Only sheet metal parts have preferences worth reading; asking any other part only logs a read failure.
        _partPreference = _context.IsSheetMetalApplication || _allBodies.Any(b => b.Kind == BodyKind.SheetMetal)
            ? _sheetMetalPreferences.ReadForPart().Preference
            : null;

        _preferenceChecks = _partPreference is { } preference
            ? _allBodies
                .Where(b => b.Kind == BodyKind.SheetMetal)
                .ToDictionary(
                    b => b.Id,
                    b => SheetMetalPreferenceSync.Evaluate(
                        SheetMetalBodies.MaterialOf(b.Id, _currentAssignments),
                        _sheetMetalPreferences.ReadFor(b.Id).BodyThickness,
                        preference))
            : new Dictionary<BodyId, SheetMetalPreferenceCheckResult>();
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
        _blocks.PartPreference = _partPreference;
        _blocks.PreferenceChecks = _preferenceChecks;
        _blocks.PopulateCurrentAssignmentTree(groups);
    }
}
