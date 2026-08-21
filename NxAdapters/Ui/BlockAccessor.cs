using Core.Bodies;
using Core.Common;
using Core.MaterialLibrary;
using NXOpen;
using NXOpen.BlockStyler;
using NxAdapters.Materials;
using NxOpen.Foundation.Contracts.Common;
using NxOpen.Foundation.Contracts.Materials;
using NxOpen.Foundation.NxAdapters;

// NXOpen ships its own Material (a part attribute) and SelectObject (a selection API type), both of which
// collide with the ones meant here. Aliased rather than fully qualified at each use — this file mentions
// them constantly.
using Material = NxOpen.Foundation.Contracts.Materials.Material;
using SelectObject = NXOpen.BlockStyler.SelectObject;

namespace NxAdapters.Ui;

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
    internal const string MaterialTreeId = "MaterialTree";
    internal const string CurrentAssignmentTreeId = "CurrentAssignmentTree";
    internal const string PendingAssignmentTreeId = "PendingAssignments";
    internal const string ExplorerId = "explorer";

    // Explorer nodes are UGS::UI::Comp::WizardGroup pages, verbatim order from BlockUI.dlx. Each page's
    // native controls are not constructed until the page becomes current, regardless of the .dlx-level
    // Expanded="True" flag on every node.
    private const int MaterialNode = 0;
    private const int CurrentAssignmentNode = 1;
    private const int PendingNode = 2;

    // One Label/Bitmap per Explorer node. Each one describes the tree it sits under, so all three are
    // driven independently rather than all mirroring the material picker.
    internal const string MaterialLabelId = "lbl_MaterialDisplay";
    internal const string AssignmentLabelId = "lbl_MaterialDisplay1";
    internal const string PendingLabelId = "lbl_MaterialDisplay2";

    /// <summary>What a material label shows when its tree has nothing selected. The blocks are designed
    /// with the placeholder text "XX_MaterialName", which would otherwise sit there looking like data.</summary>
    private const string NoMaterialLabel = "(no material selected)";

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
    }

    private static class PendingColumn
    {
        public const int Name = 0;
        public const int Kind = 1;
        public const int Status = 2;
    }

    // NX color indices used to code pending rows. 186 reads as red and 36 as amber in the default palette.
    private const int BlockedColor = 186;
    private const int NeedsConfirmationColor = 36;

    private readonly BlockDialog _dialog;
    private readonly BodyResolver _bodyResolver;
    private readonly Action<string>? _logWarning;

    private SelectObject? _selectedBodies;
    private Enumeration? _libraryEnum;
    private Explorer? _explorer;
    private Tree? _materialTree;
    private Tree? _currentAssignmentTree;
    private Tree? _pendingAssignmentTree;
    private Label? _materialLabel;
    private Label? _assignmentLabel;
    private Label? _pendingLabel;

    private TreeBinding<Material>? _materials;
    private TreeBinding<AssignmentRowRef>? _assignments;
    private TreeBinding<PendingRowRef>? _pending;

    /// <summary>Per-Explorer-node construction state. <c>dialogShown_cb</c> fires again every time the user
    /// switches Explorer node (confirmed empirically — SetUpColumns re-runs on every node change), so column
    /// setup must be idempotent per node rather than a one-time dialog step. A tree's columns are only ever
    /// touched once — the first time its node becomes current — and any populate call that arrives before
    /// that point is remembered and replayed at that moment instead of running immediately.</summary>
    private sealed class NodeState
    {
        public bool ColumnsReady;
        public Action? PendingPopulate;
    }

    private readonly NodeState _materialNodeState = new();
    private readonly NodeState _currentAssignmentNodeState = new();
    private readonly NodeState _pendingNodeState = new();

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
        _explorer = TryFindBlock<Explorer>(ExplorerId);
        _materialTree = TryFindBlock<Tree>(MaterialTreeId);
        _currentAssignmentTree = TryFindBlock<Tree>(CurrentAssignmentTreeId);
        _pendingAssignmentTree = TryFindBlock<Tree>(PendingAssignmentTreeId);
        _materialLabel = TryFindBlock<Label>(MaterialLabelId);
        _assignmentLabel = TryFindBlock<Label>(AssignmentLabelId);
        _pendingLabel = TryFindBlock<Label>(PendingLabelId);

        Trace($"Initialize: resolved explorer={_explorer is not null} materialTree={_materialTree is not null} " +
              $"currentTree={_currentAssignmentTree is not null} pendingTree={_pendingAssignmentTree is not null}");

        if (_materialTree is not null)
        {
            Trace("Initialize: wiring materialTree callbacks");
            _materials = new TreeBinding<Material>(_materialTree);
            _materialTree.SetOnSelectHandler((_, node, _, selected) =>
                Safe("materialTree.OnSelect", () => sink.OnMaterialSelected(selected ? _materials!.Resolve(node) : null)));
            _materialTree.SetOnPreSelectHandler((_, node, _, _) =>
                Safe("materialTree.OnPreSelect", () => sink.OnMaterialHovered(_materials!.Resolve(node))));
            _materialTree.SetToolTipTextHandler((_, node, _) =>
                SafeFunc("materialTree.ToolTipText", () =>
                {
                    var material = _materials!.Resolve(node);
                    return material is null ? string.Empty : sink.OnMaterialTooltip(material);
                }, fallback: string.Empty));
            _materialTree.SetOnDefaultActionHandler((_, node, _) =>
                Safe("materialTree.OnDefaultAction", () =>
                {
                    var material = _materials!.Resolve(node);
                    if (material is not null)
                        sink.OnMaterialDefaultAction(material);
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
                    sink.OnAssignmentSelected(selected ? _assignments!.Resolve(node) : null)));
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
                    sink.OnPendingSelected(selected ? _pending!.Resolve(node) : null)));
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
        NxMessageBoxHelper.ShowError(
            $"An error occurred handling '{what}':{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
            "Full details were written to the Listing Window.");
    }

    public void SetUpColumns()
    {
        if (_explorer is null)
        {
            Trace("SetUpColumns: explorer block not resolved (null) — nothing to do");
            return;
        }

        var node = _explorer.CurrentNode;
        Trace($"SetUpColumns: active node = {node}");

        switch (node)
        {
            case MaterialNode:
                EnsureNodeColumns(_materialNodeState, "material", () =>
                {
                    _materialTree?.SetPreSelectionTimeOut(250.0);
                    InsertColumns(_materialTree,
                        (MaterialColumn.Name, "Material", 220),
                        (MaterialColumn.Detail, "Description", 160),
                        (MaterialColumn.Density, "Density", 90));
                });
                break;

            case CurrentAssignmentNode:
                EnsureNodeColumns(_currentAssignmentNodeState, "currentAssignment", () =>
                    InsertColumns(_currentAssignmentTree,
                        (AssignmentColumn.Name, "Material / Body", 220),
                        (AssignmentColumn.Count, "Bodies / Kind", 110),
                        (AssignmentColumn.DisplayMaterial, "Display material", 140)));
                break;

            case PendingNode:
                EnsureNodeColumns(_pendingNodeState, "pending", () =>
                    InsertColumns(_pendingAssignmentTree,
                        (PendingColumn.Name, "Material / Body", 220),
                        (PendingColumn.Kind, "Kind", 90),
                        (PendingColumn.Status, "Status", 260)));
                break;

            default:
                Trace($"SetUpColumns: unrecognized node {node}, ignoring");
                break;
        }
    }


    private void EnsureNodeColumns(NodeState state, string label, Action setupColumns)
    {
        if (!state.ColumnsReady)
        {
            Trace($"SetUpColumns: setting up {label}");
            setupColumns();
            state.ColumnsReady = true;
        }
        else
        {
            Trace($"SetUpColumns: {label} already set up");
        }

        // The page just became current (this method only runs for _explorer.CurrentNode), so it's now safe
        // to flush anything queued while the page was off-screen — regardless of whether this is the first
        // time its columns were set up.
        if (state.PendingPopulate is { } populate)
        {
            Trace($"SetUpColumns: flushing deferred populate for {label}");
            state.PendingPopulate = null;
            populate();
        }
    }

    // A tree's native controls only exist while its Explorer page is the current one (see the comment at
    // MaterialNode/CurrentAssignmentNode/PendingNode above); Tree.Redraw on an off-screen page throws
    // NXException. state.ColumnsReady alone only proves the page was current at some point, not that it
    // still is, so both conditions are required before populating immediately.
    private void RunOrDefer(NodeState state, int nodeId, Action populate)
    {
        if (state.ColumnsReady && _explorer?.CurrentNode == nodeId)
            populate();
        else
            state.PendingPopulate = populate;
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
            Trace($"InsertColumns: column {id} ('{title}')");
            tree.InsertColumn(id, title, width);
            tree.SetColumnResizePolicy(id, Tree.ColumnResizePolicy.ConstantWidth);
        }
    }

    private void Trace(string message) => _logWarning?.Invoke($"TRACE {message}");

    // ---- Library enumeration ----

    public void PopulateLibraryEnum(IReadOnlyList<MaterialLibraryReference> libraries)
    {
        _lastPopulatedLibraries = libraries;
        if (_libraryEnum is null)
            return;

        var properties = _libraryEnum.GetProperties();
        try
        {
            properties.SetEnumMembers("Value", libraries.Select(l => l.DisplayName).ToArray());
            if (libraries.Count > 0)
                properties.SetEnum("Value", 0);
        }
        finally
        {
            properties.Dispose();
        }
    }

    public MaterialLibraryId? GetSelectedLibraryId()
    {
        if (_libraryEnum is null || _lastPopulatedLibraries.Count == 0)
            return null;

        var properties = _libraryEnum.GetProperties();
        try
        {
            var index = properties.GetEnum("Value");
            return index >= 0 && index < _lastPopulatedLibraries.Count
                ? _lastPopulatedLibraries[index].Id
                : null;
        }
        finally
        {
            properties.Dispose();
        }
    }

    // ---- Material tree ----

    public void PopulateMaterialTree(IReadOnlyList<MaterialCategoryNode> roots)
    {
        if (_materials is null)
            return;

        RunOrDefer(_materialNodeState, MaterialNode, () =>
            _materials.Rebuild(() =>
            {
                foreach (var root in roots)
                    AddCategory(root, parent: null);
            }));
    }

    private void AddCategory(MaterialCategoryNode category, Node? parent)
    {
        // Category rows map to no domain value on purpose: they are structure, and selecting one should not
        // look like selecting a material.
        var node = _materials!.Add(category.DisplayName, value: null, parent);

        foreach (var child in category.Children)
            AddCategory(child, node);

        foreach (var material in category.Materials)
        {
            var materialNode = _materials.Add(material.Name, material, node);
            materialNode.SetColumnDisplayText(MaterialColumn.Detail, material.Description ?? string.Empty);
            materialNode.SetColumnDisplayText(MaterialColumn.Density, DensityText(material));
        }
    }

    public Material? GetSelectedMaterial() => _materials?.ResolveSelected().FirstOrDefault();
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

    // ---- Material labels, one per Explorer node ----

    public void SetMaterialLabel(Material? material) => SetLabelText(_materialLabel, material?.Name);

    public void SetAssignmentLabel(AssignmentRowRef? row) => SetLabelText(_assignmentLabel, row?.Row.MaterialLabel);

    public void SetPendingLabel(PendingRowRef? row) => SetLabelText(_pendingLabel, row?.Entry.Material.Name);

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

                    foreach (var body in group.Bodies)
                    {
                        var bodyNode = _assignments.Add(body.Name, AssignmentRowRef.ForBody(group.Row, body), node);
                        bodyNode.SetColumnDisplayText(AssignmentColumn.Count, body.Kind.ToString());
                        bodyNode.SetColumnDisplayText(AssignmentColumn.DisplayMaterial, DisplayMaterialText(body));
                    }
                }
            }));
    }

    /// <summary>Set by the presenter alongside the tree population so the display-material column can be
    /// filled without this class re-querying the part.</summary>
    public IReadOnlyDictionary<BodyId, BodyMaterialAssignment> CurrentAssignments { get; set; } =
        new Dictionary<BodyId, BodyMaterialAssignment>();

    private string DisplayMaterialText(BodyInfo body) =>
        CurrentAssignments.TryGetValue(body.Id, out var assignment)
            ? assignment.CurrentDisplayMaterial?.Name ?? string.Empty
            : string.Empty;

    // ---- Pending assignment tree ----

    public void PopulatePendingTree(IReadOnlyList<PendingAssignmentEntry> entries)
    {
        if (_pending is null)
            return;

        RunOrDefer(_pendingNodeState, PendingNode, () =>
            _pending.Rebuild(() =>
            {
                foreach (var entry in entries)
                {
                    var node = _pending.Add(entry.Material.Name, new PendingRowRef(entry, null), parent: null);
                    node.SetColumnDisplayText(PendingColumn.Kind, entry.Rows.Count.ToString());

                    foreach (var row in entry.Rows)
                    {
                        var bodyNode = _pending.Add(row.Body.Name, new PendingRowRef(entry, row), node);
                        bodyNode.SetColumnDisplayText(PendingColumn.Kind, row.Body.Kind.ToString());
                        bodyNode.SetColumnDisplayText(PendingColumn.Status, StatusText(row));

                        if (row.Status == PendingBodyStatus.Blocked)
                            bodyNode.ForegroundColor = BlockedColor;
                        else if (row.Status == PendingBodyStatus.NeedsConfirmation)
                            bodyNode.ForegroundColor = NeedsConfirmationColor;
                    }
                }
            }));
    }

    private static string StatusText(PendingBodyRow row)
    {
        var label = row.Status switch
        {
            PendingBodyStatus.Blocked => "Blocked",
            PendingBodyStatus.NeedsConfirmation => "Needs confirmation",
            _ => "OK",
        };

        return string.IsNullOrWhiteSpace(row.Message) ? label : $"{label} — {row.Message}";
    }

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
    // Forwards to the shared NxOpen.Foundation.NxAdapters.NxMessageBoxHelper — these three have no dependency
    // on this dialog's blocks or domain types, so the implementation lives once in the foundation instead of
    // being duplicated per project.

    public bool Confirm(string message) => NxMessageBoxHelper.Confirm(message);

    public void ShowResult(OperationResult result, string successMessage) =>
        NxMessageBoxHelper.ShowResult(result, successMessage);

    public void ShowError(string message) => NxMessageBoxHelper.ShowError(message);
}
