using BANxOpen.Materials.Core;
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
/// Changes are staged. "Add to pending" on the material tree and "Remove material" on the current-assignment tree only
/// list the change in the pending tree — the part is not touched, so the current-assignment tree keeps showing what the
/// bodies really have, with the pending change flagged beside each one. A body has at most one pending change; the
/// latest action replaces an earlier one. OK/Apply apply the pending list, and Apply is final; whatever does not apply
/// stays pending with the reason. Cancel, "Remove from pending" and "Clear all pending" only drop entries. Target
/// bodies for an assignment come from the <c>Sel_SoildBodies</c> selection block. The decision tree is in README.md,
/// and every text the user sees is in <see cref="UiText"/>.
///
/// Sheet-metal-specific checks and messages live in the <c>SheetMetalSupport</c> folder.</summary>
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

    // At most one PendingRemovalEntry, and any number of PendingAssignmentEntry; each body in at most one entry.
    private readonly List<PendingEntry> _pending = new();

    // The part's Sheet Metal Preferences, re-read with the body state. Null outside sheet metal parts.
    private SheetMetalPartPreference? _partPreference;

    // How each sheet metal body stands against _partPreference, re-read with it. Empty when there are none.
    private IReadOnlyDictionary<BodyId, SheetMetalPreferenceCheckResult> _preferenceChecks =
        new Dictionary<BodyId, SheetMetalPreferenceCheckResult>();

    // One-time steps of the first dialog show: the filters start from the part's preferences row, and a part whose
    // bodies disagree with that row is pointed out.
    private bool _filterSeeded;
    private bool _initialSyncChecked;

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
        _blocks.ShowSheetMetalGradeColumn = _context.IsSheetMetalApplication;
        _blocks.ClearHighlight();
        _blocks.SetUpColumns();

        // Re-selects the library already in use: this may re-fire on a tab switch (see OnUpdate), and a re-fire must
        // not throw away the library or the Sheet Metal Preferences filters the user chose. The filters live in _filter
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

        _blocks.ShowInfo(UiText.Titles.SheetMetalPreferences, SheetMetalPreferenceSync.OpenMessage(preference, notInSync, autoSelect));
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
                // Pending entries keep the row they were staged with; only what can be picked next changes.
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

    /// <summary>Applies what is pending; whatever cannot be applied stays pending. With nothing pending and a sheet
    /// metal material picked (see <see cref="PickedSheetMetalMaterial"/>), applies that instead — see
    /// <see cref="ApplyPickedSheetMetalMaterial"/>.</summary>
    public int OnApply()
    {
        if (_pending.Count > 0)
        {
            CommitPending();
            return 0;
        }

        if (PickedSheetMetalMaterial() is { } pick)
            return ApplyPickedSheetMetalMaterial(pick) ? 0 : 1;

        _blocks.ShowError(UiText.Errors.NothingPending(_isSheetMetalLibrary));
        return 1;
    }

    // Unlike OnApply, an empty pending list on OK is a normal end state — not something worth blocking the dialog's
    // close on. A picked sheet metal material is applied the same way Apply would when it has somewhere to go; backing
    // out of it keeps the dialog open. With bodies in the part but none selected there is nothing for it to go on
    // (see ApplyPickedSheetMetalMaterial), so OK just closes rather than trapping the user behind that error. Anything
    // left pending after the apply keeps the dialog open, so the user sees what did not go through.
    public int OnOk()
    {
        if (_pending.Count > 0)
        {
            if (!CommitPending())
                return 1;
        }
        else if (PickedSheetMetalMaterial() is { } pick
                 && (_blocks.GetSelectedBodyIds().Count > 0 || !PartHasSolid)
                 && !ApplyPickedSheetMetalMaterial(pick))
        {
            return 1;
        }

        _blocks.ClearHighlight();
        return 0;
    }

    /// <summary>Cancel (and the title bar's close, which NX routes to the same callback). Apply is final, so nothing
    /// applied is reverted; pending changes were never applied, so they are discarded after a warning.</summary>
    /// <returns>1 to keep the dialog open, 0 to let it close.</returns>
    public int OnCancel()
    {
        if (_pending.Count > 0 && !_blocks.Confirm(UiText.Confirm.DiscardPending(_pending.Count)))
            return 1;

        _pending.Clear();
        _blocks.ClearHighlight();
        return 0;
    }

    /// <summary>Re-queries the physical/display material state from the part and re-renders everything derived
    /// from it. The dialog is interactive, so the model can change underneath it. Pending entries are only pruned of
    /// bodies that are gone; Apply re-plans them against the part as it then stands.</summary>
    public void OnRefreshClicked()
    {
        RefreshBodyState();
        PrunePending();
        RenderPending();
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
    /// material to them, as "Add to pending" then Apply would. With none, it sets only the part's Sheet Metal
    /// Preferences, after warning that the bodies' own material and colour are left as they are — but only on a part
    /// with no solid body: once there are bodies, the preferences go with them, so they are never set on their own.</summary>
    /// <returns>False when not everything was applied — the user backed out, the part has bodies to assign, or NX
    /// refused.</returns>
    private bool ApplyPickedSheetMetalMaterial(MaterialPick pick)
    {
        if (_blocks.GetSelectedBodyIds().Count > 0)
            return Stage(pick) && CommitPending();

        var row = pick.Row!;
        if (PartHasSolid)
        {
            _blocks.ShowError(UiText.Errors.NoSolidSelectedForRow(row.Name));
            return false;
        }

        return _blocks.Confirm(UiText.Confirm.SetPreferencesOnly(row.Name, row.Describe())) && ApplySheetMetalPreferenceOnly(row);
    }

    /// <summary>Sets the part's Sheet Metal Preferences to <paramref name="row"/> and nothing else — no body is
    /// assigned and nothing is staged. Final, like Apply.</summary>
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

        _blocks.ShowResult(result, UiText.Info.SheetMetalPreferencesSet(row.Name, row.Describe()));
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
            _blocks.ShowError(UiText.Errors.EverySolidHasMaterial);
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
            lines.Add(UiText.Labels.SheetMetalMaterialMadeOf(material.Name));
            lines.Add(row.Describe());
        }

        if (material.Category.PathSegments.Count > 0)
            lines.Add(string.Join(UiText.Labels.CategorySeparator, material.Category.PathSegments));
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
            return new[] { new TreeMenuItem(MaterialMenu.Refresh, UiText.Menu.Refresh) };

        // Assignment needs a target, and the selection block is the only source of one.
        var hasBodies = _blocks.GetSelectedBodyIds().Count > 0;

        var items = new List<TreeMenuItem>
        {
            new(MaterialMenu.Assign, UiText.Menu.AddToPending, hasBodies),
        };

        // Sets the part's preferences straight away — no bodies, nothing pending. Only a row can be applied, and
        // only on a part with no solid body: once there are bodies, the preferences go with their material.
        if (_isSheetMetalLibrary && !PartHasSolid)
            items.Add(new TreeMenuItem(MaterialMenu.ApplySheetMetalPreference, UiText.Menu.ApplySheetMetalPreference, clicked.Row is not null));

        items.Add(TreeMenuItem.Separator);
        items.Add(new TreeMenuItem(MaterialMenu.Properties, UiText.Menu.Properties));
        items.Add(new TreeMenuItem(MaterialMenu.Refresh, UiText.Menu.Refresh));
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
                Stage(pick);
                return;
            case MaterialMenu.ApplySheetMetalPreference when pick?.Row is { } row:
                ApplySheetMetalPreferenceOnly(row);
                return;
        }
    }

    // ---- Staging an assignment ----

    /// <summary>Plans the selected bodies against <paramref name="pick"/>, asks the confirmations its rules need, and
    /// adds what will be assigned to the pending list. The part is not touched. A body that already has the material
    /// is not staged, and an earlier pending change to it is dropped — assigning what it has means leaving it as it is.
    /// Blocked and declined bodies are reported and not staged, and keep any earlier pending claim. Only the bodies
    /// that are staged are taken out of their earlier entry (an assignment or the removal), so one body is never
    /// claimed twice.</summary>
    /// <returns>False when nothing was staged.</returns>
    private bool Stage(MaterialPick pick)
    {
        var targets = SelectedTargets();
        if (targets is null)
            return false;

        var claims = ClaimsByBody();
        var alreadyHas = targets.Where(b => AlreadyHas(b, pick.Material, pick.Row)).ToList();
        var notStaged = alreadyHas
            .Select(b => UiText.Reasons.BodyLine(b.Name, UiText.Reasons.AlreadyHas(pick.Material.Name) +
                         (claims.ContainsKey(b.Id) ? UiText.Reasons.EarlierPendingChangeRemovedSuffix : string.Empty)))
            .ToList();
        if (alreadyHas.Count > 0)
            ReplacePending(WithoutBodies(_pending, alreadyHas.Select(b => b.Id).ToList()));

        var remaining = targets.Where(b => !alreadyHas.Contains(b)).ToList();
        PendingAssignmentEntry? staged = null;
        var replaced = new List<string>();
        var warnings = new List<string>();

        if (remaining.Count > 0)
        {
            var entry = BuildCheckedEntry(pick, remaining);
            if (entry is null)
            {
                RenderPending();
                return false;
            }

            var confirmed = GetConfirmedBodyIds(entry);
            notStaged.AddRange(NotStagedLines(entry, confirmed));

            var rows = entry.Rows
                .Where(r => r.Status == PendingBodyStatus.Ok
                            || (r.Status == PendingBodyStatus.NeedsConfirmation && confirmed.Contains(r.Body.Id)))
                .ToList();

            if (rows.Count > 0)
            {
                staged = entry with { Rows = rows, ConfirmedBodyIds = confirmed.ToList() };

                // A pending removal being replaced is worth saying: the user may not expect the body to keep a material.
                replaced = rows
                    .Where(r => claims.TryGetValue(r.Body.Id, out var claim) && claim is PendingRemovalEntry)
                    .Select(r => UiText.Reasons.BodyLine(r.Body.Name, UiText.Reasons.PendingRemovalReplaced))
                    .ToList();

                ReplacePending(WithoutBodies(_pending, staged.BodyIds));
                _pending.Add(staged);

                // Warnings never stop an assignment, but they are the only thing the user would otherwise never see —
                // for example a bead on the body that matches no SPEC, whose restriction is therefore not enforced.
                warnings = rows
                    .Where(r => r.Status == PendingBodyStatus.Ok && !string.IsNullOrWhiteSpace(r.Message))
                    .Select(r => UiText.Reasons.BodyLine(r.Body.Name, r.Message!))
                    .ToList();
            }
        }

        ReportStaging(pick.DisplayName, staged, notStaged, replaced, warnings, onlyAlreadyAssigned: remaining.Count == 0);
        RenderPending();
        return staged is not null;
    }

    /// <summary>True when <paramref name="body"/> already has <paramref name="material"/> — and, for a sheet metal body
    /// with a row picked, the part's Sheet Metal Preferences already are that row — so assigning it would change
    /// nothing.</summary>
    private bool AlreadyHas(BodyInfo body, Material material, SheetMetalMaterialRow? row)
    {
        if (!SheetMetalBodies.SameName(SheetMetalBodies.MaterialOf(body.Id, _currentAssignments), material.Name))
            return false;

        return row is null
               || body.Kind != BodyKind.SheetMetal
               || SheetMetalBodies.SameName(_partPreference?.Row?.Name, row.Name);
    }

    /// <summary>The bodies of <paramref name="entry"/> that will not be assigned: blocked by a rule, or needing a
    /// confirmation that was not given.</summary>
    private static IEnumerable<string> NotStagedLines(PendingAssignmentEntry entry, HashSet<BodyId> confirmedBodyIds) =>
        entry.Rows
            .Where(r => r.Status == PendingBodyStatus.Blocked)
            .Select(r => UiText.Reasons.BodyLine(r.Body.Name, UiText.Reasons.Blocked(r.Message)))
            .Concat(entry.Rows
                .Where(r => r.Status == PendingBodyStatus.NeedsConfirmation && !confirmedBodyIds.Contains(r.Body.Id))
                .Select(r => UiText.Reasons.BodyLine(r.Body.Name, UiText.Reasons.SkippedNotConfirmed)));

    /// <summary>One message for everything about an "Add to pending" the pending tree does not show: bodies left out
    /// and why, pending removals it replaced, and warnings. A clean add needs no message — the pending tree shows it.</summary>
    private void ReportStaging(
        string displayName,
        PendingAssignmentEntry? staged,
        IReadOnlyList<string> notStaged,
        IReadOnlyList<string> replaced,
        IReadOnlyList<string> warnings,
        bool onlyAlreadyAssigned)
    {
        var sections = new List<string>();
        if (notStaged.Count > 0)
            sections.Add(UiText.Info.Section(UiText.Info.NotAddedHeader, notStaged));
        if (replaced.Count > 0)
            sections.Add(UiText.Info.Section(UiText.Info.ReplacedHeader, replaced));
        if (warnings.Count > 0)
            sections.Add(UiText.Info.Section(UiText.Info.WarningsHeader, warnings));

        if (sections.Count == 0)
            return;

        var details = UiText.Info.JoinSections(sections);
        if (staged is not null)
            _blocks.ShowInfo(UiText.Titles.Dialog, UiText.Info.AddedToPending(displayName, staged.Rows.Count, details));
        else if (onlyAlreadyAssigned)
            _blocks.ShowInfo(UiText.Titles.Dialog, UiText.Info.NothingAddedToPending(details)); // nothing went wrong
        else
            _blocks.ShowError(UiText.Errors.NotAddedToPending(displayName, details));
    }

    // ---- Staging a removal ----

    /// <summary>Adds <paramref name="bodyIds"/> to the pending removal. The part is not touched. A body with no material
    /// has nothing to remove and is skipped; a body pending an assignment has that assignment replaced by the removal,
    /// and the user is told.</summary>
    private void StageRemoval(IReadOnlyList<BodyId> bodyIds)
    {
        if (bodyIds.Count == 0)
        {
            _blocks.ShowError(UiText.Errors.SelectBodiesToRemove);
            return;
        }

        var requested = bodyIds.ToHashSet();
        var bodies = _allBodies.Where(b => requested.Contains(b.Id)).ToList();
        var claims = ClaimsByBody();

        var noMaterial = bodies.Where(b => SheetMetalBodies.MaterialOf(b.Id, _currentAssignments) is null).ToList();
        var toRemove = bodies.Where(b => !noMaterial.Contains(b)).ToList();

        var notStaged = noMaterial.Select(b => UiText.Reasons.BodyLine(b.Name, UiText.Reasons.HasNoMaterial)).ToList();
        var replaced = toRemove
            .Where(b => claims.TryGetValue(b.Id, out var claim) && claim is PendingAssignmentEntry)
            .Select(b => UiText.Reasons.BodyLine(b.Name, UiText.Reasons.PendingAssignmentReplaced(claims[b.Id].DisplayName)))
            .ToList();

        if (toRemove.Count > 0)
        {
            var existing = _pending.OfType<PendingRemovalEntry>().FirstOrDefault();
            var kept = existing?.Rows.Where(r => !toRemove.Any(b => b.Id == r.Body.Id)) ?? Enumerable.Empty<PendingBodyRow>();
            var removal = new PendingRemovalEntry(
                kept.Concat(toRemove.Select(b => new PendingBodyRow(b, PendingBodyStatus.Ok, null))).ToList());

            ReplacePending(WithoutBodies(_pending.Where(e => e is not PendingRemovalEntry), toRemove.Select(b => b.Id).ToList()));
            _pending.Insert(0, removal);
        }

        var sections = new List<string>();
        if (notStaged.Count > 0)
            sections.Add(UiText.Info.Section(UiText.Info.NotAddedHeader, notStaged));
        if (replaced.Count > 0)
            sections.Add(UiText.Info.Section(UiText.Info.ReplacedHeader, replaced));

        if (sections.Count > 0)
        {
            var details = UiText.Info.JoinSections(sections);
            _blocks.ShowInfo(UiText.Titles.Dialog, toRemove.Count > 0
                ? UiText.Info.RemovalAddedToPending(toRemove.Count, details)
                : UiText.Info.NothingToRemove(details));
        }

        RenderPending();
    }

    // ---- Committing ----

    /// <summary>Applies every pending change to the part — the removal first, then each assignment in order, each
    /// re-planned against the part as it now stands (the dialog is interactive, so it may have changed since the entry
    /// was staged). The confirmations each entry was given are reused; a body that newly needs one is asked. A body
    /// that is not changed — gone from the part, blocked or declined now, in a sheet metal conflict, or refused by NX —
    /// stays pending with the reason. What was applied is final.</summary>
    /// <returns>True when everything was applied and nothing is left pending.</returns>
    private bool CommitPending()
    {
        RefreshBodyState();

        var leftOver = new List<PendingEntry>();
        var report = new List<string>();

        if (_pending.OfType<PendingRemovalEntry>().FirstOrDefault() is { } removal)
            CommitRemoval(removal, leftOver, report);

        foreach (var entry in _pending.OfType<PendingAssignmentEntry>().ToList())
            CommitAssignment(entry, leftOver, report);

        ReplacePending(leftOver);

        if (report.Count > 0)
        {
            var details = UiText.Info.JoinSections(report);
            if (leftOver.Count > 0)
                _blocks.ShowError(UiText.Errors.StillPending(details));
            else
                _blocks.ShowInfo(UiText.Titles.Dialog, UiText.Info.AppliedWithNotes(details));
        }

        OnRefreshClicked();
        return leftOver.Count == 0;
    }

    private void CommitRemoval(PendingRemovalEntry removal, List<PendingEntry> leftOver, List<string> report)
    {
        var lines = new List<string>();
        var (bodies, gone) = StillPresent(removal);
        lines.AddRange(gone);

        var failed = new List<PendingBodyRow>();
        if (bodies.Count > 0)
        {
            var result = _partMaterialService.ClearMaterial(bodies.Select(b => b.Id).ToList());
            RefreshBodyState();

            // NX does not say which bodies failed a partial clear; the part does.
            foreach (var row in removal.Rows.Where(r => bodies.Any(b => b.Id == r.Body.Id)))
            {
                var took = result.Ok
                           || (result.ErrorCode == MaterialsCodes.PartialFailure && SheetMetalBodies.MaterialOf(row.Body.Id, _currentAssignments) is null);
                if (took)
                    continue;

                var reason = UiText.Reasons.FailedOnBody(result);
                failed.Add(NotApplied(row, reason));
                lines.Add(UiText.Reasons.BodyLine(row.Body.Name, reason));
            }
        }

        if (failed.Count > 0)
            leftOver.Add(removal with { Rows = failed });
        if (lines.Count > 0)
            report.Add(UiText.Info.EntryReport(removal.DisplayName, lines));
    }

    private void CommitAssignment(PendingAssignmentEntry old, List<PendingEntry> leftOver, List<string> report)
    {
        var lines = new List<string>();
        var (bodies, gone) = StillPresent(old);
        lines.AddRange(gone);

        if (bodies.Count > 0)
        {
            var failed = new List<PendingBodyRow>();
            var entry = Replan(old.Material, old.SheetMetalRow, bodies);
            var confirmed = old.ConfirmedBodyIds.ToHashSet();

            if (FindSheetMetalConflict(entry.Material, entry.SheetMetalRow, bodies) is { } conflict)
            {
                failed.AddRange(entry.Rows.Select(r => NotApplied(r, UiText.Reasons.ConflictsWithSheetMetal)));
                lines.Add(conflict);
            }
            else
            {
                var unanswered = entry with { Rows = entry.Rows.Where(r => !confirmed.Contains(r.Body.Id)).ToList() };
                confirmed.UnionWith(GetConfirmedBodyIds(unanswered));

                var executablePlan = _finalizer.Finalize(entry.Plan, entry.Input, confirmed);
                var executableIds = executablePlan.Assignments.Select(a => a.BodyId).ToHashSet();

                foreach (var row in entry.Rows.Where(r => !executableIds.Contains(r.Body.Id)))
                {
                    var reason = row.Status == PendingBodyStatus.Blocked
                        ? UiText.Reasons.Blocked(row.Message)
                        : UiText.Reasons.NotConfirmed;
                    failed.Add(NotApplied(row, reason));
                    lines.Add(UiText.Reasons.BodyLine(row.Body.Name, reason));
                }

                if (executablePlan.Assignments.Count > 0)
                {
                    var result = _partMaterialService.ApplyPlan(executablePlan);

                    // The next entry is planned against what this one did, and a partial failure is told apart body by
                    // body from what the part now really has — NX does not say which bodies failed.
                    RefreshBodyState();

                    foreach (var row in entry.Rows.Where(r => executableIds.Contains(r.Body.Id)))
                    {
                        var took = result.Ok
                                   || (result.ErrorCode == MaterialsCodes.PartialFailure
                                       && SheetMetalBodies.SameName(SheetMetalBodies.MaterialOf(row.Body.Id, _currentAssignments), entry.Material.Name));
                        if (took)
                        {
                            if (row.Status == PendingBodyStatus.Ok && !string.IsNullOrWhiteSpace(row.Message))
                                lines.Add(UiText.Reasons.BodyLine(row.Body.Name, UiText.Reasons.Warning(row.Message)));
                            continue;
                        }

                        var reason = UiText.Reasons.FailedOnBody(result);
                        failed.Add(NotApplied(row, reason));
                        lines.Add(UiText.Reasons.BodyLine(row.Body.Name, reason));
                    }
                }
            }

            if (failed.Count > 0)
                leftOver.Add(entry with { Rows = failed, ConfirmedBodyIds = confirmed.ToList() });
        }

        if (lines.Count > 0)
            report.Add(UiText.Info.EntryReport(old.DisplayName, lines));
    }

    /// <summary>The entry's bodies still in the work part, and a report line for each that is gone.</summary>
    private (List<BodyInfo> Present, List<string> GoneLines) StillPresent(PendingEntry entry)
    {
        var present = entry.Rows
            .Select(r => _allBodies.FirstOrDefault(b => b.Id == r.Body.Id))
            .OfType<BodyInfo>()
            .ToList();
        var gone = entry.Rows
            .Where(r => present.All(b => b.Id != r.Body.Id))
            .Select(r => UiText.Reasons.BodyLine(r.Body.Name, UiText.Reasons.NoLongerInPart))
            .ToList();
        return (present, gone);
    }

    /// <summary>A body Apply could not change, as it stays in the pending tree.</summary>
    private static PendingBodyRow NotApplied(PendingBodyRow row, string reason) =>
        row with { Status = PendingBodyStatus.Blocked, Message = reason };

    // ---- Pending list ----

    /// <summary>The entry each pending body is claimed by.</summary>
    private Dictionary<BodyId, PendingEntry> ClaimsByBody()
    {
        var claims = new Dictionary<BodyId, PendingEntry>();
        foreach (var entry in _pending)
        {
            foreach (var row in entry.Rows)
                claims[row.Body.Id] = entry;
        }

        return claims;
    }

    private void ReplacePending(IEnumerable<PendingEntry> entries)
    {
        var list = entries.ToList();
        _pending.Clear();
        _pending.AddRange(list);
    }

    /// <summary>Re-renders the pending tree and the current-assignment tree, whose Pending column flags the bodies the
    /// pending list will change. Called after every change to <see cref="_pending"/>.</summary>
    private void RenderPending()
    {
        _blocks.PopulatePendingTree(_pending);
        RefreshAssignmentTree();
    }

    /// <summary><paramref name="entries"/> with <paramref name="bodyIds"/> taken out, dropping any entry left with
    /// no body. Not re-planned: Apply re-plans against the part as it then stands.</summary>
    private static List<PendingEntry> WithoutBodies(IEnumerable<PendingEntry> entries, IReadOnlyCollection<BodyId> bodyIds) =>
        entries
            .Select(e => e with { Rows = e.Rows.Where(r => !bodyIds.Contains(r.Body.Id)).ToList() })
            .Where(e => e.Rows.Count > 0)
            .ToList();

    /// <summary>Drops pending bodies that are no longer in the work part, and entries left with none.</summary>
    private void PrunePending()
    {
        var present = _allBodies.Select(b => b.Id).ToHashSet();
        ReplacePending(WithoutBodies(_pending, _pending.SelectMany(e => e.BodyIds).Where(id => !present.Contains(id)).ToList()));
    }

    // ---- Current assignment tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildAssignmentMenu(AssignmentRowRef? clicked)
    {
        if (clicked is null)
            return new[] { new TreeMenuItem(AssignmentMenu.Refresh, UiText.Menu.Refresh) };

        var hasSolidBody = clicked.Bodies.Any(SheetMetalBodies.IsSolid);
        var hasMaterial = clicked.Bodies.Any(b => SheetMetalBodies.MaterialOf(b.Id, _currentAssignments) is not null);

        return new[]
        {
            new TreeMenuItem(AssignmentMenu.SelectBodies, UiText.Menu.SelectBodies),
            new TreeMenuItem(AssignmentMenu.AddToBodySelection, UiText.Menu.AddToBodySelection, hasSolidBody),
            new TreeMenuItem(AssignmentMenu.RemoveMaterial, UiText.Menu.RemoveMaterial, hasMaterial),
            TreeMenuItem.Separator,
            new TreeMenuItem(AssignmentMenu.Refresh, UiText.Menu.Refresh),
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
                StageRemoval(bodyIds);
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
            _blocks.ShowError(UiText.Errors.NoSolidBodyInRows);
            return;
        }

        var current = _blocks.GetSelectedBodyIds().ToHashSet();
        current.UnionWith(solidIds);
        _blocks.SetSelectedBodies(current.ToList());
    }

    // ---- Pending tree menu ----

    public IReadOnlyList<TreeMenuItem> BuildPendingMenu(PendingRowRef? clicked)
    {
        var clearAll = new TreeMenuItem(PendingMenu.ClearAll, UiText.Menu.ClearAllPending, _pending.Count > 0);

        if (clicked is null)
            return new[] { clearAll };

        // Nothing is committed from here: Apply/OK apply the whole pending list at once.
        return new[]
        {
            new TreeMenuItem(PendingMenu.Remove, UiText.Menu.RemoveFromPending),
            TreeMenuItem.Separator,
            clearAll,
        };
    }

    /// <summary>Nothing pending has touched the part, so removing from pending only drops it from the list.</summary>
    public void OnPendingMenuCommand(int menuItemId, IReadOnlyList<PendingRowRef> targets)
    {
        switch (menuItemId)
        {
            case PendingMenu.ClearAll:
                _pending.Clear();
                break;

            case PendingMenu.Remove:
                // A root row removes the whole entry; a body row removes just that body from it.
                var removedEntries = targets.Where(t => t.Row is null).Select(t => t.Entry).ToHashSet();
                var removedBodies = targets.Where(t => t.Row is not null).Select(t => t.Row!.Body.Id).ToHashSet();
                ReplacePending(WithoutBodies(_pending.Where(e => !removedEntries.Contains(e)), removedBodies));
                break;

            default:
                return;
        }

        RenderPending();
    }

    // ---- Planning ----

    /// <summary>The bodies in the selection block, or null — after saying why — when there are none.</summary>
    private IReadOnlyList<BodyInfo>? SelectedTargets()
    {
        var selectedIds = _blocks.GetSelectedBodyIds();
        if (selectedIds.Count == 0)
        {
            _blocks.ShowError(UiText.Errors.SelectBodiesFirst);
            return null;
        }

        var selected = selectedIds.ToHashSet();
        var targets = _allBodies.Where(b => selected.Contains(b.Id)).ToList();
        if (targets.Count == 0)
        {
            // The selection resolved to nothing this scan knows about — the part changed under the dialog.
            _blocks.ShowError(UiText.Errors.SelectionNotInPart);
            return null;
        }

        return targets;
    }

    /// <summary>Plans <paramref name="targets"/> and refuses a sheet metal body with no row picked or a second sheet
    /// metal material, before anything is staged. Returns null, after saying why, when the entry conflicts.</summary>
    private PendingAssignmentEntry? BuildCheckedEntry(MaterialPick pick, IReadOnlyList<BodyInfo> targets)
    {
        var entry = Replan(pick.Material, pick.Row, targets);

        // The Sheet Metal Preferences are set to the row the user picks in the tree, never to one guessed from the
        // physical material. Bodies a rule already blocks are left to that rule's own message.
        if (pick.Row is null
            && entry.Rows.Any(r => r.Body.Kind == BodyKind.SheetMetal && r.Status != PendingBodyStatus.Blocked))
        {
            _blocks.ShowError(UiText.Errors.SheetMetalNeedsRow(pick.Material.Name));
            return null;
        }

        if (FindSheetMetalConflict(entry.Material, entry.SheetMetalRow, entry.Rows.Select(r => r.Body).ToList()) is { } conflict)
        {
            _blocks.ShowError(conflict);
            return null;
        }

        return entry;
    }

    /// <summary>See <see cref="SheetMetalConflictFinder.Find"/>. A body pending an assignment counts as that entry's
    /// material and row, and one pending removal as having none, since that is what each will have.</summary>
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
    /// Declining skips those bodies — they are reported and not staged; the rest of the entry still
    /// applies.</summary>
    private HashSet<BodyId> GetConfirmedBodyIds(PendingAssignmentEntry entry)
    {
        var needing = entry.Rows.Where(r => r.Status == PendingBodyStatus.NeedsConfirmation).ToList();
        if (needing.Count == 0)
            return new HashSet<BodyId>();

        var message = UiText.Confirm.ConfirmBodies(
            needing.Count, entry.Material.Name, needing.Select(r => UiText.Reasons.BodyLine(r.Body.Name, r.Message ?? string.Empty)));

        return _blocks.Confirm(message)
            ? needing.Select(r => r.Body.Id).ToHashSet()
            : new HashSet<BodyId>();
    }

    private void ShowProperties(Material material)
    {
        if (_propertyWindow is null)
        {
            _blocks.ShowError(UiText.Errors.PropertyWindowUnavailable);
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
        _blocks.PendingByBody = _pending
            .SelectMany(e => e.Rows.Select(r => new PendingRowRef(e, r)))
            .GroupBy(p => p.Row!.Body.Id)
            .ToDictionary(g => g.Key, g => g.Last());
        _blocks.PartPreference = _partPreference;
        _blocks.PreferenceChecks = _preferenceChecks;
        _blocks.PopulateCurrentAssignmentTree(groups);
    }
}
