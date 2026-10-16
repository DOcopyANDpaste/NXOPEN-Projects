using BANxOpen.Materials.Core.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Materials.Core.Library;
using NXOpen;
using NXOpen.BlockStyler;
using BANxOpen.Materials.NxAdapters;
using BANxOpen.Materials.Contracts;
using BANxOpen.Foundation.NxAdapters;

// NXOpen ships its own Material (a part attribute), SelectObject (a selection API type) and Group, all of which
// collide with the ones meant here. Aliased rather than fully qualified at each use — this file mentions
// them constantly.
using Material = BANxOpen.Materials.Contracts.Material;
using SelectObject = NXOpen.BlockStyler.SelectObject;
using Group = NXOpen.BlockStyler.Group;
using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.SheetMetal.Beads.Rules;
using BANxOpen.SheetMetal.Materials;
using BANxOpen.SheetMetal.Common;
using BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>All <c>TopBlock.FindBlock("stringId")</c> lookups and typed block reads/writes live here, per
/// Skills/with-block-ui.md §3 — when the Styler regenerates and renames/reorders blocks, only this file
/// changes.
///
/// The string IDs below are the REAL ones read out of <c>BlockUI.dlx</c>, replacing the placeholder set this
/// class was originally written against. Two of them — <see cref="PendingAssignmentTreeId"/> and
/// <see cref="MaterialThumbnailId"/> — are not in the .dlx yet and are expected to be added in the Styler;
/// until then <see cref="TryFindBlock{T}"/> logs once and the features that need them stay inert rather than
/// taking down a dialog that is otherwise fully functional.
///
/// Lookups use <c>TopBlock.FindBlock</c> with a typed cast, which is what the Styler itself generates and
/// what the NX samples use, rather than <c>dialog.GetBlock(id).GetProperties()</c>. Every
/// <see cref="PropertyList"/> obtained here is disposed — it is a <c>TransientObject</c>, and the NX samples
/// dispose them at every call site without exception.
///
/// The original conventions still hold. A selection is never turned back into a domain value by parsing what
/// a block displays: it is mapped through what was last populated — for trees, via
/// <see cref="TreeBinding{T}"/>. And populating clears first, so a stale row can never resolve against
/// freshly-swapped contents.</summary>
public sealed class BlockAccessor
{
    // ---- Block IDs, verbatim from BlockUI.dlx ----
    internal const string SelectedBodiesId = "Sel_SoildBodies";
    internal const string SelectAllButtonId = "selectAllButton";
    internal const string SelectUnassignedButtonId = "selectUnassignedButton";
    internal const string LibraryEnumId = "enum_MatLibrary";
    internal const string StandardEnumId = "enum_SmStandard";
    internal const string SmPreferencesGroupId = "group_SMPref";
    internal const string SmPhysicalMaterialEnumId = "enum_SmPhyMatName";
    internal const string SmThicknessEnumId = "enum_smTHK";
    internal const string SmBendRadiusEnumId = "enum_SmBendRad";
    internal const string SmClearButtonId = "btn_SmClearSelection";
    internal const string MaterialTreeId = "MaterialTree";
    internal const string CurrentAssignmentTreeId = "CurrentAssignmentTree";
    internal const string PendingAssignmentTreeId = "PendingAssignments";
    internal const string TabControlId = "tabControl";

    // Tab pages are UGS::UI::Comp::WizardGroup pages (same class the dialog's old Explorer container used),
    // verbatim order from BlockUI.dlx. Each page's native controls are not constructed until the page
    // becomes current, regardless of the .dlx-level Expanded="True" flag on every node.
    private const int MaterialNode = 0;
    private const int CurrentAssignmentNode = 1;
    private const int PendingNode = 2;

    // One Label/Bitmap per tab page. Each one describes the tree it sits under, so all three are
    // driven independently rather than all mirroring the material picker.
    internal const string MaterialLabelId = "lbl_MaterialDisplay";
    internal const string AssignmentLabelId = "lbl_MaterialDisplay1";
    internal const string PendingLabelId = "lbl_MaterialDisplay2";

    /// <summary>What a material label shows when its tree has nothing selected. The blocks are designed
    /// with the placeholder text "XX_MaterialName", which would otherwise sit there looking like data.</summary>
    private const string NoMaterialLabel = UiText.Labels.NoMaterialSelected;

    /// <summary>The first member of every Sheet Metal Preferences filter dropdown. An enumeration cannot be left with
    /// nothing selected, so "no filter" is a member of its own.</summary>
    private const string AnyFilterValue = UiText.Labels.AnyFilter;

    // Column ids. Both trees in the .dlx have ShowHeader/ShowMultipleColumns true but declare no columns at
    // design time, so these are created at runtime in SetUpColumns.
    private static class MaterialColumn
    {
        public const int Name = 0;
        public const int Detail = 1;
        public const int Density = 2;
    }

    private static class AssignmentColumn
    {
        public const int Name = 0;
        public const int Count = 1;
        public const int DisplayMaterial = 2;

        // What a body is about to be changed to — see PendingByBody.
        public const int Pending = 3;

        // Only inserted in the NX Sheet Metal application — see ShowSheetMetalGradeColumn.
        public const int SheetMetalGrade = 4;
    }

    private static class PendingColumn
    {
        public const int Name = 0;
        public const int Kind = 1;
        public const int Status = 2;
    }

    private readonly BlockDialog _dialog;
    private readonly BodyResolver _bodyResolver;
    private readonly Action<string>? _logWarning;

    private SelectObject? _selectedBodies;
    private Enumeration? _libraryEnum;
    private Group? _sheetMetalPreferencesGroup;
    private readonly FilterEnum<string> _standardFilter = new();
    private readonly FilterEnum<string> _physicalMaterialFilter = new();
    private readonly FilterEnum<double> _thicknessFilter = new();
    private readonly FilterEnum<string> _bendRadiusFilter = new();
    private TabControl? _tabControl;
    private Tree? _materialTree;
    private Tree? _currentAssignmentTree;
    private Tree? _pendingAssignmentTree;
    private Label? _materialLabel;
    private Label? _assignmentLabel;
    private Label? _pendingLabel;

    private TreeBinding<MaterialPick>? _materials;
    private TreeBinding<AssignmentRowRef>? _assignments;
    private TreeBinding<PendingRowRef>? _pending;

    /// <summary>Bodies currently highlighted in the graphics window because a current-assignment or pending
    /// tree row is selected. Kept separate from the <see cref="SelectedBodiesId"/> block/<see cref="SetSelectedBodies"/> —
    /// this is transient visual feedback for browsing the trees, not the assignment target.</summary>
    private readonly Dictionary<BodyId, Body> _highlightedBodies = new();

    /// <summary>Per-tab-page construction state. Under the dialog's old Explorer container,
    /// <c>dialogShown_cb</c> fired again every time the user switched node (confirmed empirically —
    /// SetUpColumns re-ran on every node change); with TabControl, the presenter's OnUpdate also calls
    /// <see cref="SetUpColumns"/> on a tab switch as a second trigger, since it's unconfirmed whether tab
    /// switches still route through dialogShown_cb. Either way, column setup must be idempotent per page
    /// rather than a one-time dialog step.
    ///
    /// Whether a tree is ready is asked of the tree itself (<see cref="HasLiveColumns"/>), never remembered: a
    /// tree's native control can be gone, or rebuilt without its columns, after the page was first set up — an
    /// off-screen page, or NX recreating the controls after Apply. A remembered flag then sends a rebuild at a
    /// control that is not there, which is what used to kill the trees on Apply/Refresh with an NXException on
    /// Redraw. Any populate that cannot run now is kept and replayed the next time the page is set up, and the
    /// last one that ran is kept too, so a tree whose control was rebuilt is refilled with its latest data.</summary>
    private sealed class NodeState
    {
        public NodeState(string label, int columnCount)
        {
            Label = label;
            ColumnCount = columnCount;
        }

        public string Label { get; }

        // Settable because the current-assignment tree gains the SheetMetal Grade column in the Sheet Metal application.
        public int ColumnCount { get; set; }
        public Tree? Tree;
        public bool EverSetUp;
        public Action? PendingPopulate;
        public Action? LastPopulate;
    }

    private readonly NodeState _materialNodeState = new("material", columnCount: 3);
    private readonly NodeState _currentAssignmentNodeState = new("currentAssignment", columnCount: 4);
    private readonly NodeState _pendingNodeState = new("pending", columnCount: 3);

    /// <summary>One Sheet Metal Preferences filter dropdown and the values it was last filled with, after the
    /// <see cref="AnyFilterValue"/> member. A selection is mapped back through <see cref="Values"/>, never parsed
    /// from the displayed text — that is what lets thickness be shown rounded and still compare exactly.</summary>
    private sealed class FilterEnum<T>
    {
        public Enumeration? Block;
        public IReadOnlyList<T> Values = Array.Empty<T>();
    }

    private IReadOnlyList<MaterialLibraryReference> _lastPopulatedLibraries = Array.Empty<MaterialLibraryReference>();

    /// <param name="bodyResolver">Maps <see cref="BodyId"/> to the live NXOpen <see cref="Body"/> and back, so
    /// the selection block can be read and written in domain terms. Shared with
    /// <c>PartMaterialService</c> — it is refreshed before every write here, since the cache is only valid
    /// for the scan that filled it.</param>
    /// <param name="logWarning">Warning sink — a plain delegate rather than a concrete NX logger so this class
    /// stays independent of the session context. Callers typically pass <c>context.Log.Warn</c>.</param>
    public BlockAccessor(BlockDialog dialog, BodyResolver bodyResolver, Action<string>? logWarning = null)
    {
        _dialog = dialog;
        _bodyResolver = bodyResolver;
        _logWarning = logWarning;
    }

    // ---- Lifecycle ----

    /// <summary>Resolves every block and registers the tree callbacks. Called from <c>initialize_cb</c>,
    /// which is where the NX samples resolve blocks.</summary>
    public void Initialize(ITreeInteractionSink sink)
    {
        Trace("Initialize: start");

        _selectedBodies = TryFindBlock<SelectObject>(SelectedBodiesId);
        _libraryEnum = TryFindBlock<Enumeration>(LibraryEnumId);
        _sheetMetalPreferencesGroup = TryFindBlock<Group>(SmPreferencesGroupId);
        _standardFilter.Block = TryFindBlock<Enumeration>(StandardEnumId);
        _physicalMaterialFilter.Block = TryFindBlock<Enumeration>(SmPhysicalMaterialEnumId);
        _thicknessFilter.Block = TryFindBlock<Enumeration>(SmThicknessEnumId);
        _bendRadiusFilter.Block = TryFindBlock<Enumeration>(SmBendRadiusEnumId);
        _tabControl = TryFindBlock<TabControl>(TabControlId);
        _materialTree = TryFindBlock<Tree>(MaterialTreeId);
        _currentAssignmentTree = TryFindBlock<Tree>(CurrentAssignmentTreeId);
        _pendingAssignmentTree = TryFindBlock<Tree>(PendingAssignmentTreeId);
        _materialNodeState.Tree = _materialTree;
        _currentAssignmentNodeState.Tree = _currentAssignmentTree;
        _pendingNodeState.Tree = _pendingAssignmentTree;
        _materialLabel = TryFindBlock<Label>(MaterialLabelId);
        _assignmentLabel = TryFindBlock<Label>(AssignmentLabelId);
        _pendingLabel = TryFindBlock<Label>(PendingLabelId);

        Trace($"Initialize: resolved tabControl={_tabControl is not null} materialTree={_materialTree is not null} " +
              $"currentTree={_currentAssignmentTree is not null} pendingTree={_pendingAssignmentTree is not null}");

        if (_materialTree is not null)
        {
            Trace("Initialize: wiring materialTree callbacks");
            _materials = new TreeBinding<MaterialPick>(_materialTree);
            _materialTree.SetOnSelectHandler((_, node, _, selected) =>
                Safe("materialTree.OnSelect", () =>
                {
                    if (!_materials!.IsRebuilding)
                        sink.OnMaterialSelected(selected ? _materials.Resolve(node) : null);
                }));
            _materialTree.SetOnPreSelectHandler((_, node, _, _) =>
                Safe("materialTree.OnPreSelect", () =>
                {
                    if (!_materials!.IsRebuilding)
                        sink.OnMaterialHovered(_materials.Resolve(node));
                }));
            _materialTree.SetToolTipTextHandler((_, node, _) =>
                SafeFunc("materialTree.ToolTipText", () =>
                {
                    var pick = _materials!.Resolve(node);
                    return pick is null ? string.Empty : sink.OnMaterialTooltip(pick);
                }, fallback: string.Empty));
            _materialTree.SetOnDefaultActionHandler((_, node, _) =>
                Safe("materialTree.OnDefaultAction", () =>
                {
                    var pick = _materials!.Resolve(node);
                    if (pick is not null)
                        sink.OnMaterialDefaultAction(pick);
                }));
            _materialTree.SetOnMenuHandler((tree, node, _) =>
                Safe("materialTree.OnMenu", () => ShowMenu(tree, sink.BuildMaterialMenu(_materials!.Resolve(node)))));
            _materialTree.SetOnMenuSelectionHandler((_, node, menuItemId) =>
                Safe("materialTree.OnMenuSelection", () =>
                    sink.OnMaterialMenuCommand(menuItemId, _materials!.ResolveSelectedOr(node))));
        }

        if (_currentAssignmentTree is not null)
        {
            Trace("Initialize: wiring currentAssignmentTree callbacks");
            _assignments = new TreeBinding<AssignmentRowRef>(_currentAssignmentTree);
            _currentAssignmentTree.SetOnSelectHandler((_, node, _, selected) =>
                Safe("currentAssignmentTree.OnSelect", () =>
                {
                    if (_assignments!.IsRebuilding)
                        return;

                    sink.OnAssignmentSelected(selected ? _assignments!.Resolve(node) : null);
                    SyncHighlight(_assignments!.ResolveSelected().SelectMany(r => r.Bodies).Select(b => b.Id));
                }));
            _currentAssignmentTree.SetOnDefaultActionHandler((_, node, _) =>
                Safe("currentAssignmentTree.OnDefaultAction", () =>
                {
                    var row = _assignments!.Resolve(node);
                    if (row is not null)
                        sink.OnAssignmentDefaultAction(row);
                }));
            _currentAssignmentTree.SetOnMenuHandler((tree, node, _) =>
                Safe("currentAssignmentTree.OnMenu", () => ShowMenu(tree, sink.BuildAssignmentMenu(_assignments!.Resolve(node)))));
            _currentAssignmentTree.SetOnMenuSelectionHandler((_, node, menuItemId) =>
                Safe("currentAssignmentTree.OnMenuSelection", () =>
                    sink.OnAssignmentMenuCommand(menuItemId, _assignments!.ResolveSelectedOr(node))));
        }

        if (_pendingAssignmentTree is not null)
        {
            Trace("Initialize: wiring pendingAssignmentTree callbacks");
            _pending = new TreeBinding<PendingRowRef>(_pendingAssignmentTree);
            _pendingAssignmentTree.SetOnSelectHandler((_, node, _, selected) =>
                Safe("pendingAssignmentTree.OnSelect", () =>
                {
                    if (_pending!.IsRebuilding)
                        return;

                    sink.OnPendingSelected(selected ? _pending!.Resolve(node) : null);
                    SyncHighlight(_pending!.ResolveSelected()
                        .SelectMany(r => r.Row is null ? r.Entry.Rows.Select(x => x.Body) : new[] { r.Row.Body })
                        .Select(b => b.Id));
                }));
            _pendingAssignmentTree.SetOnMenuHandler((tree, node, _) =>
                Safe("pendingAssignmentTree.OnMenu", () => ShowMenu(tree, sink.BuildPendingMenu(_pending!.Resolve(node)))));
            _pendingAssignmentTree.SetOnMenuSelectionHandler((_, node, menuItemId) =>
                Safe("pendingAssignmentTree.OnMenuSelection", () =>
                    sink.OnPendingMenuCommand(menuItemId, _pending!.ResolveSelectedOr(node))));
        }

        Trace("Initialize: done");
    }

    //Safe call back throw, and report
    private void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportCallbackException(what, ex);
        }
    }

    private T SafeFunc<T>(string what, Func<T> func, T fallback)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            ReportCallbackException(what, ex);
            return fallback;
        }
    }

    private void ReportCallbackException(string what, Exception ex)
    {
        _logWarning?.Invoke($"EXCEPTION in {what}: {ex}");
        ShowError(NxDialogText.Default.HandlerFailed(what, ex.Message));
    }

    public void SetUpColumns()
    {
        if (_tabControl is null)
        {
            Trace("SetUpColumns: tabControl block not resolved (null) — nothing to do");
            return;
        }

        var node = _tabControl.ActivePage;
        Trace($"SetUpColumns: active node = {node}");

        switch (node)
        {
            case MaterialNode:
                EnsureNodeColumns(_materialNodeState, () =>
                {
                    _materialTree?.SetPreSelectionTimeOut(250.0);
                    InsertColumns(_materialTree,
                        (MaterialColumn.Name, UiText.Columns.Material, 220),
                        (MaterialColumn.Detail, UiText.Columns.Description, 160),
                        (MaterialColumn.Density, UiText.Columns.Density, 90));
                });
                break;

            case CurrentAssignmentNode:
                _currentAssignmentNodeState.ColumnCount = ShowSheetMetalGradeColumn ? 5 : 4;
                EnsureNodeColumns(_currentAssignmentNodeState, () =>
                {
                    InsertColumns(_currentAssignmentTree,
                        (AssignmentColumn.Name, UiText.Columns.MaterialOrBody, 220),
                        (AssignmentColumn.Count, UiText.Columns.BodiesOrKind, 110),
                        (AssignmentColumn.DisplayMaterial, UiText.Columns.DisplayMaterial, 140),
                        (AssignmentColumn.Pending, UiText.Columns.Pending, 200));
                    if (ShowSheetMetalGradeColumn)
                        InsertColumns(_currentAssignmentTree, (AssignmentColumn.SheetMetalGrade, UiText.Columns.SheetMetalGrade, 150));
                });
                break;

            case PendingNode:
                EnsureNodeColumns(_pendingNodeState, () =>
                    InsertColumns(_pendingAssignmentTree,
                        (PendingColumn.Name, UiText.Columns.MaterialOrBody, 220),
                        (PendingColumn.Kind, UiText.Columns.Kind, 90),
                        (PendingColumn.Status, UiText.Columns.Status, 260)));
                break;

            default:
                Trace($"SetUpColumns: unrecognized node {node}, ignoring");
                break;
        }
    }


    private void EnsureNodeColumns(NodeState state, Action setupColumns)
    {
        if (HasLiveColumns(state))
        {
            Trace($"SetUpColumns: {state.Label} already set up");
        }
        else
        {
            // Set up before means NX rebuilt this tree's control and dropped its rows with it, so the latest
            // data has to go back in even though nothing new was asked for.
            if (state.EverSetUp)
            {
                Trace($"SetUpColumns: {state.Label} lost its columns (control recreated) — rebuilding");
                state.PendingPopulate ??= state.LastPopulate;
            }
            else
            {
                Trace($"SetUpColumns: setting up {state.Label}");
            }

            setupColumns();
            state.EverSetUp = true;
        }

        // The page just became current (this method only runs for the active page), so it's now safe to flush
        // anything queued while the page was off-screen.
        if (state.PendingPopulate is { } populate)
        {
            Trace($"SetUpColumns: flushing deferred populate for {state.Label}");
            state.PendingPopulate = null;
            TryPopulate(state, populate);
        }
    }

    /// <summary>Whether the tree's native control exists right now with its columns in it. Asked of NX every time:
    /// see <see cref="NodeState"/> for why this cannot be remembered.</summary>
    private bool HasLiveColumns(NodeState state)
    {
        if (state.Tree is null)
            return false;

        try
        {
            return state.Tree.NumberOfColumns >= state.ColumnCount;
        }
        catch (NXException ex)
        {
            Trace($"HasLiveColumns: {state.Label} not available (NX {ex.ErrorCode})");
            return false;
        }
    }

    // A tree's native controls only exist while its tab page is the current one (see the comment at
    // MaterialNode/CurrentAssignmentNode/PendingNode above), and Tree.Redraw on a control that is not there throws
    // NXException. So a populate only runs now when the page is current AND the control has its columns; otherwise
    // it waits for the next SetUpColumns.
    private void RunOrDefer(NodeState state, int nodeId, Action populate)
    {
        state.LastPopulate = populate;

        var active = _tabControl?.ActivePage;
        if (active == nodeId && HasLiveColumns(state))
        {
            TryPopulate(state, populate);
        }
        else
        {
            Trace($"RunOrDefer: {state.Label} deferred (active page {active})");
            state.PendingPopulate = populate;
        }
    }

    /// <summary>Runs <paramref name="populate"/>, and if NX refuses the tree after all, keeps it for the next
    /// <see cref="SetUpColumns"/> instead of letting the exception reach the user — the tree recovers on the next tab
    /// switch or dialog show rather than staying broken.</summary>
    private void TryPopulate(NodeState state, Action populate)
    {
        try
        {
            populate();
        }
        catch (TreeUnavailableException ex)
        {
            Trace($"TryPopulate: {state.Label} rebuild refused, deferred — {ex.Message}");
            state.PendingPopulate = populate;
        }
    }

    private void InsertColumns(Tree? tree, params (int Id, string Title, int Width)[] columns)
    {
        if (tree is null)
        {
            Trace("InsertColumns: tree is null, skipping");
            return;
        }

        foreach (var (id, title, width) in columns)
        {
            // Per column, so a control that kept some of its columns still gets the rest.
            try
            {
                Trace($"InsertColumns: column {id} ('{title}')");
                tree.InsertColumn(id, title, width);
                tree.SetColumnResizePolicy(id, Tree.ColumnResizePolicy.ConstantWidth);
            }
            catch (NXException ex)
            {
                Trace($"InsertColumns: column {id} ('{title}') failed: NX {ex.ErrorCode}: {ex.Message}");
            }
        }
    }

    private void Trace(string message) => _logWarning?.Invoke($"TRACE {message}");

    // ---- Library enumeration ----

    /// <param name="select">The library to leave selected, when it is still listed; otherwise the first.</param>
    public void PopulateLibraryEnum(IReadOnlyList<MaterialLibraryReference> libraries, MaterialLibraryId? select = null)
    {
        _lastPopulatedLibraries = libraries;
        if (_libraryEnum is null)
            return;

        var properties = _libraryEnum.GetProperties();
        try
        {
            properties.SetEnumMembers("Value", libraries.Select(l => l.DisplayName).ToArray());
            if (libraries.Count > 0)
            {
                var index = libraries.ToList().FindIndex(l => select is { } id && l.Id == id);
                properties.SetEnum("Value", Math.Max(index, 0));
            }
        }
        finally
        {
            properties.Dispose();
        }
    }

    public MaterialLibraryId? GetSelectedLibraryId() =>
        SelectedIndex(_libraryEnum, _lastPopulatedLibraries.Count) is { } index ? _lastPopulatedLibraries[index].Id : null;

    // ---- Sheet Metal Preferences filters ----

    /// <summary>Shows or hides the whole Sheet Metal Preferences group, which only means anything for a sheet metal
    /// library.</summary>
    public void SetSheetMetalPreferencesVisible(bool visible)
    {
        if (_sheetMetalPreferencesGroup is not null)
            _sheetMetalPreferencesGroup.Show = visible;
    }

    /// <summary>Refills the four filter dropdowns from <paramref name="rows"/>, each with the options
    /// <paramref name="current"/> leaves it (see <see cref="SheetMetalRowFilter"/>), keeping each one's current value
    /// selected. Read the filter back with <see cref="GetSheetMetalFilter"/> afterwards: a value no longer offered
    /// falls back to <see cref="AnyFilterValue"/>.</summary>
    public void PopulateSheetMetalFilters(IReadOnlyList<SheetMetalMaterialRow> rows, SheetMetalRowFilter current)
    {
        PopulateFilter(_standardFilter, current.StandardOptions(rows), current.Standard, v => v, SameText);
        PopulateFilter(_physicalMaterialFilter, current.PhysicalMaterialOptions(rows), current.PhysicalMaterialName, v => v, SameText);
        PopulateFilter(_thicknessFilter, current.ThicknessOptions(rows), current.Thickness,
            ThicknessMatchRule.Format, (a, b) => b is { } t && ThicknessMatchRule.Matches(a, t));
        PopulateFilter(_bendRadiusFilter, current.BendRadiusOptions(rows), current.BendRadius, v => v, SameText);
    }

    public SheetMetalRowFilter GetSheetMetalFilter() =>
        new(SelectedFilterValue(_standardFilter),
            SelectedFilterValue(_physicalMaterialFilter),
            SelectedFilterIndex(_thicknessFilter) is { } i ? _thicknessFilter.Values[i] : null,
            SelectedFilterValue(_bendRadiusFilter));

    private static bool SameText(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void PopulateFilter<T, TCurrent>(
        FilterEnum<T> filter, IReadOnlyList<T> values, TCurrent current, Func<T, string> display, Func<T, TCurrent, bool> matches)
    {
        filter.Values = values;
        if (filter.Block is null)
            return;

        var selected = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (matches(values[i], current))
            {
                selected = i + 1;
                break;
            }
        }

        var properties = filter.Block.GetProperties();
        try
        {
            properties.SetEnumMembers("Value", new[] { AnyFilterValue }.Concat(values.Select(display)).ToArray());
            properties.SetEnum("Value", selected);
        }
        finally
        {
            properties.Dispose();
        }
    }

    /// <summary>The index into <see cref="FilterEnum{T}.Values"/> of the selected member, or null for
    /// <see cref="AnyFilterValue"/>.</summary>
    private static int? SelectedFilterIndex<T>(FilterEnum<T> filter) =>
        SelectedIndex(filter.Block, filter.Values.Count + 1) is { } index and > 0 ? index - 1 : null;

    private static string? SelectedFilterValue(FilterEnum<string> filter) =>
        SelectedFilterIndex(filter) is { } i ? filter.Values[i] : null;

    /// <summary>The selected index of <paramref name="enumeration"/>, or null when nothing valid is selected among the
    /// <paramref name="populatedCount"/> members last populated.</summary>
    private static int? SelectedIndex(Enumeration? enumeration, int populatedCount)
    {
        if (enumeration is null || populatedCount == 0)
            return null;

        var properties = enumeration.GetProperties();
        try
        {
            var index = properties.GetEnum("Value");
            return index >= 0 && index < populatedCount ? index : null;
        }
        finally
        {
            properties.Dispose();
        }
    }

    // ---- Material tree ----

    /// <param name="sheetMetalRowsFor">For a sheet metal library: the standards file rows to list under each physical
    /// material — the Sheet Metal Preferences materials the user actually picks. Null for any other library.</param>
    public void PopulateMaterialTree(
        IReadOnlyList<MaterialCategoryNode> roots,
        Func<Material, IReadOnlyList<SheetMetalMaterialRow>>? sheetMetalRowsFor = null)
    {
        if (_materials is null)
            return;

        RunOrDefer(_materialNodeState, MaterialNode, () =>
            _materials.Rebuild(() =>
            {
                foreach (var root in roots)
                    AddCategory(root, parent: null, sheetMetalRowsFor);
            }));
    }

    private void AddCategory(
        MaterialCategoryNode category, Node? parent, Func<Material, IReadOnlyList<SheetMetalMaterialRow>>? sheetMetalRowsFor)
    {
        // Category rows map to no domain value on purpose: they are structure, and selecting one should not
        // look like selecting a material.
        var node = _materials!.Add(category.DisplayName, value: null, parent);

        foreach (var child in category.Children)
            AddCategory(child, node, sheetMetalRowsFor);

        foreach (var material in category.Materials)
        {
            var materialNode = _materials.Add(material.Name, new MaterialPick(material), node);
            materialNode.SetColumnDisplayText(MaterialColumn.Detail, material.Description ?? string.Empty);
            materialNode.SetColumnDisplayText(MaterialColumn.Density, DensityText(material));

            if (sheetMetalRowsFor is null)
                continue;

            foreach (var row in sheetMetalRowsFor(material))
            {
                var rowNode = _materials.Add(row.Name, new MaterialPick(material, row), materialNode);
                rowNode.SetColumnDisplayText(MaterialColumn.Detail, row.Describe());
                rowNode.SetColumnDisplayText(MaterialColumn.Density, DensityText(material));
            }
        }
    }

    public MaterialPick? GetSelectedMaterialPick() => _materials?.ResolveSelected().FirstOrDefault();
    private static string DensityText(Material material)
    {
        var density = material.Properties
            .FirstOrDefault(p => p?.Name?.IndexOf("density", StringComparison.OrdinalIgnoreCase) >= 0);

        if (density is null)
            return string.Empty;

        return string.IsNullOrWhiteSpace(density.Unit)
            ? density.AsString()
            : $"{density.AsString()} {density.Unit}";
    }

    // ---- Material labels, one per tab page ----

    public void SetMaterialLabel(MaterialPick? pick) => SetLabelText(_materialLabel, pick?.DisplayName);

    public void SetAssignmentLabel(AssignmentRowRef? row) => SetLabelText(_assignmentLabel, row?.Row.MaterialLabel);

    public void SetPendingLabel(PendingRowRef? row) => SetLabelText(_pendingLabel, row?.Entry.DisplayName);

    private static void SetLabelText(Label? label, string? text)
    {
        if (label is null)
            return;

        var properties = label.GetProperties();
        try
        {
            properties.SetString("Label", string.IsNullOrWhiteSpace(text) ? NoMaterialLabel : text);
        }
        finally
        {
            properties.Dispose();
        }
    }

    // ---- Current assignment tree ----

    public void PopulateCurrentAssignmentTree(IReadOnlyList<AssignmentRowRef> groups)
    {
        if (_assignments is null)
            return;

        RunOrDefer(_currentAssignmentNodeState, CurrentAssignmentNode, () =>
            _assignments.Rebuild(() =>
            {
                foreach (var group in groups)
                {
                    var node = _assignments.Add(group.Row.MaterialLabel, group, parent: null);
                    node.SetColumnDisplayText(AssignmentColumn.Count, group.Row.BodyCount.ToString());
                    var pendingCount = group.Bodies.Count(b => PendingByBody.ContainsKey(b.Id));
                    if (pendingCount > 0)
                        node.SetColumnDisplayText(AssignmentColumn.Pending, UiText.Status.GroupPendingCount(pendingCount));
                    if (ShowSheetMetalGradeColumn)
                        node.SetColumnDisplayText(AssignmentColumn.SheetMetalGrade, SheetMetalGradeText.ForMaterial(group.Row.MaterialLabel, PartPreference));

                    foreach (var body in group.Bodies)
                    {
                        var bodyNode = _assignments.Add(body.Name, AssignmentRowRef.ForBody(group.Row, body), node);
                        bodyNode.SetColumnDisplayText(AssignmentColumn.Count, body.Kind.ToString());
                        bodyNode.SetColumnDisplayText(AssignmentColumn.DisplayMaterial, DisplayMaterialText(body));
                        if (PendingByBody.TryGetValue(body.Id, out var pending))
                            bodyNode.SetColumnDisplayText(AssignmentColumn.Pending, PendingFlagText(pending));
                        if (ShowSheetMetalGradeColumn)
                            bodyNode.SetColumnDisplayText(AssignmentColumn.SheetMetalGrade, SheetMetalGradeText.ForBody(
                                PreferenceChecks.TryGetValue(body.Id, out var check) ? check : null, PartPreference));
                    }
                }
            }));
    }

    /// <summary>Set by the presenter alongside the tree population so the display-material column can be
    /// filled without this class re-querying the part.</summary>
    public IReadOnlyDictionary<BodyId, BodyMaterialAssignment> CurrentAssignments { get; set; } =
        new Dictionary<BodyId, BodyMaterialAssignment>();

    /// <summary>Whether the current-assignment tree has the SheetMetal Grade column. Set by the presenter before the
    /// first <see cref="SetUpColumns"/> — true in the NX Sheet Metal application.</summary>
    public bool ShowSheetMetalGradeColumn { get; set; }

    /// <summary>The part's Sheet Metal Preferences, for the SheetMetal Grade column. Set by the presenter alongside
    /// <see cref="CurrentAssignments"/>.</summary>
    public SheetMetalPartPreference? PartPreference { get; set; }

    /// <summary>How each sheet metal body stands against <see cref="PartPreference"/>, for the SheetMetal Grade column.
    /// Set by the presenter alongside it.</summary>
    public IReadOnlyDictionary<BodyId, SheetMetalPreferenceCheckResult> PreferenceChecks { get; set; } =
        new Dictionary<BodyId, SheetMetalPreferenceCheckResult>();

    /// <summary>Each pending body with the entry that claims it, for the current-assignment tree's Pending column. Set by
    /// the presenter alongside <see cref="CurrentAssignments"/>, so the part's real materials and what is about to
    /// change them are shown side by side.</summary>
    public IReadOnlyDictionary<BodyId, PendingRowRef> PendingByBody { get; set; } =
        new Dictionary<BodyId, PendingRowRef>();

    private static string PendingFlagText(PendingRowRef pending)
    {
        var row = pending.Row!;
        var flag = pending.Entry is PendingRemovalEntry
            ? UiText.Status.FlagRemove
            : UiText.Status.FlagAssign(pending.Entry.DisplayName);
        var suffix = row.Status == PendingBodyStatus.Blocked ? UiText.Status.FlagNotAppliedSuffix
            : IsConfirmed(pending.Entry, row) ? UiText.Status.FlagConfirmedSuffix
            : string.Empty;
        return flag + suffix;
    }

    private static bool IsConfirmed(PendingEntry entry, PendingBodyRow row) =>
        row.Status == PendingBodyStatus.NeedsConfirmation && entry.ConfirmedBodyIds.Contains(row.Body.Id);

    private string DisplayMaterialText(BodyInfo body) =>
        CurrentAssignments.TryGetValue(body.Id, out var assignment)
            ? assignment.CurrentDisplayMaterial?.Name ?? string.Empty
            : string.Empty;

    // ---- Pending assignment tree ----

    public void PopulatePendingTree(IReadOnlyList<PendingEntry> entries)
    {
        if (_pending is null)
            return;

        RunOrDefer(_pendingNodeState, PendingNode, () =>
            _pending.Rebuild(() =>
            {
                foreach (var entry in entries)
                {
                    var node = _pending.Add(entry.DisplayName, new PendingRowRef(entry, null), parent: null);
                    node.SetColumnDisplayText(PendingColumn.Kind, entry.Rows.Count.ToString());

                    foreach (var row in entry.Rows)
                    {
                        var bodyNode = _pending.Add(row.Body.Name, new PendingRowRef(entry, row), node);
                        bodyNode.SetColumnDisplayText(PendingColumn.Kind, row.Body.Kind.ToString());
                        bodyNode.SetColumnDisplayText(PendingColumn.Status, StatusText(entry, row));
                    }
                }
            }));
    }

    /// <summary>Nothing in the pending tree has touched the part yet. Blocked and declined bodies are reported when the
    /// material is added and never listed, so a body is either waiting — with a rule's warning or the reassignment the
    /// user confirmed — or was left behind by Apply, with why. A pending removal names the material it will take off.</summary>
    private string StatusText(PendingEntry entry, PendingBodyRow row)
    {
        if (row.Status == PendingBodyStatus.Blocked)
            return UiText.Status.NotApplied(row.Message);

        if (entry is PendingRemovalEntry)
        {
            return UiText.Status.PendingRemoval(
                CurrentAssignments.TryGetValue(row.Body.Id, out var assignment) ? assignment.MaterialName : null);
        }

        var status = IsConfirmed(entry, row) ? UiText.Status.PendingConfirmed : UiText.Status.Pending;
        return string.IsNullOrWhiteSpace(row.Message) ? status : UiText.Status.WithNote(status, row.Message!);
    }

    // ---- Tree selection highlight ----

    /// <summary>Highlights exactly <paramref name="bodyIds"/> in the graphics window, unhighlighting whatever
    /// was highlighted before that isn't in the new set. Driven off the live tree selection on every select
    /// event, so it stays correct for both single- and multi-select.</summary>
    private void SyncHighlight(IEnumerable<BodyId> bodyIds)
    {
        var ids = bodyIds.ToHashSet();

        foreach (var stale in _highlightedBodies.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _highlightedBodies[stale].Unhighlight();
            _highlightedBodies.Remove(stale);
        }

        foreach (var id in ids)
        {
            if (_highlightedBodies.ContainsKey(id))
                continue;
            if (_bodyResolver.TryResolve(id, out var body))
            {
                body.Highlight();
                _highlightedBodies[id] = body;
            }
        }
    }

    /// <summary>Clears any tree-selection highlight — called on tab switch and dialog close so it never
    /// outlives the context it was shown for.</summary>
    public void ClearHighlight() => SyncHighlight(Array.Empty<BodyId>());

    // ---- Body selection block ----

    public IReadOnlyList<BodyId> GetSelectedBodyIds()
    {
        if (_selectedBodies is null)
            return Array.Empty<BodyId>();

        var properties = _selectedBodies.GetProperties();
        try
        {
            var selected = properties.GetTaggedObjectVector("SelectedObjects");
            if (selected is null)
                return Array.Empty<BodyId>();

            // The block is scoped to bodies, but filter anyway rather than cast — a non-Body slipping through
            // should drop out, not throw inside a callback.
            return selected.OfType<Body>().Select(BodyResolver.GetBodyId).ToList();
        }
        finally
        {
            properties.Dispose();
        }
    }

    public void SetSelectedBodies(IReadOnlyList<BodyId> bodyIds)
    {
        if (_selectedBodies is null)
            return;

        // The resolver's cache is only valid for the scan that filled it, and this can be driven from a quick
        // -select button at any point in the dialog's life, so rescan before mapping.
        _bodyResolver.Refresh();

        var bodies = new List<TaggedObject>();
        foreach (var id in bodyIds)
        {
            if (_bodyResolver.TryResolve(id, out var body))
                bodies.Add(body);
        }

        var properties = _selectedBodies.GetProperties();
        try
        {
            properties.SetTaggedObjectVector("SelectedObjects", bodies.ToArray());
        }
        finally
        {
            properties.Dispose();
        }
    }

    // ---- Menus ----

    private static void ShowMenu(Tree tree, IReadOnlyList<TreeMenuItem> items)
    {
        if (items.Count == 0)
            return;

        var menu = tree.CreateMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    menu.AddSeparator();
                    continue;
                }

                menu.AddMenuItem(item.Id, item.Text);
                if (!item.Enabled)
                    menu.SetItemDisable(item.Id, true);
            }

            tree.SetMenu(menu);
        }
        finally
        {
            // Only ever after SetMenu — the tree takes its copy there.
            menu.Dispose();
        }
    }

    // ---- Block lookup ----

    /// <summary>Resolves a block, returning null and warning once rather than throwing when it is absent.
    /// Two of this dialog's blocks are still to be added in the Styler, and a missing one should disable the
    /// feature that needs it, not prevent the dialog from opening.</summary>
    private T? TryFindBlock<T>(string blockId) where T : class
    {
        try
        {
            var block = _dialog.TopBlock.FindBlock(blockId) as T;
            if (block is null)
                _logWarning?.Invoke($"Dialog block '{blockId}' is missing or is not a {typeof(T).Name}; the features that use it are disabled.");

            return block;
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"Dialog block '{blockId}' could not be resolved ({ex.Message}); the features that use it are disabled.");
            return null;
        }
    }

    // ---- Generic dialogs ----
    // Forwards to the shared BANxOpen.Foundation.NxAdapters.NxMessageBoxHelper, whose titles are NxDialogText's. A failed
    // result's message is the library's own wording and is shown as-is.

    public bool Confirm(string message) => NxMessageBoxHelper.Confirm(message);

    public void ShowResult(OperationResult result, string successMessage) =>
        NxMessageBoxHelper.ShowResult(result, successMessage);

    public void ShowError(string message) => NxMessageBoxHelper.ShowError(message);

    public void ShowInfo(string title, string message) => NxMessageBoxHelper.ShowInfo(title, message);
}
