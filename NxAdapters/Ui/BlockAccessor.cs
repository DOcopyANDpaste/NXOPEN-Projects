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
                sink.OnMaterialSelected(selected ? _materials!.Resolve(node) : null));
            _materialTree.SetOnPreSelectHandler((_, node, _, _) =>
                sink.OnMaterialHovered(_materials!.Resolve(node)));
            _materialTree.SetToolTipTextHandler((_, node, _) =>
            {
                var material = _materials!.Resolve(node);
                return material is null ? string.Empty : sink.OnMaterialTooltip(material);
            });
            _materialTree.SetOnDefaultActionHandler((_, node, _) =>
            {
                var material = _materials!.Resolve(node);
                if (material is not null)
                    sink.OnMaterialDefaultAction(material);
            });
            _materialTree.SetOnMenuHandler((tree, node, _) =>
                ShowMenu(tree, sink.BuildMaterialMenu(_materials!.Resolve(node))));
            _materialTree.SetOnMenuSelectionHandler((_, node, menuItemId) =>
                sink.OnMaterialMenuCommand(menuItemId, _materials!.ResolveSelectedOr(node)));
        }

        if (_currentAssignmentTree is not null)
        {
            Trace("Initialize: wiring currentAssignmentTree callbacks");
            _assignments = new TreeBinding<AssignmentRowRef>(_currentAssignmentTree);
            _currentAssignmentTree.SetOnSelectHandler((_, node, _, selected) =>
                sink.OnAssignmentSelected(selected ? _assignments!.Resolve(node) : null));
            _currentAssignmentTree.SetOnMenuHandler((tree, node, _) =>
                ShowMenu(tree, sink.BuildAssignmentMenu(_assignments!.Resolve(node))));
            _currentAssignmentTree.SetOnMenuSelectionHandler((_, node, menuItemId) =>
                sink.OnAssignmentMenuCommand(menuItemId, _assignments!.ResolveSelectedOr(node)));
        }

        if (_pendingAssignmentTree is not null)
        {
            Trace("Initialize: wiring pendingAssignmentTree callbacks");
            _pending = new TreeBinding<PendingRowRef>(_pendingAssignmentTree);
            _pendingAssignmentTree.SetOnSelectHandler((_, node, _, selected) =>
                sink.OnPendingSelected(selected ? _pending!.Resolve(node) : null));
            _pendingAssignmentTree.SetOnMenuHandler((tree, node, _) =>
                ShowMenu(tree, sink.BuildPendingMenu(_pending!.Resolve(node))));
            _pendingAssignmentTree.SetOnMenuSelectionHandler((_, node, menuItemId) =>
                sink.OnPendingMenuCommand(menuItemId, _pending!.ResolveSelectedOr(node)));
        }

        Trace("Initialize: done");
    }

    /// <summary>Creates the tree columns and sets tree widget properties that require the native control to
    /// already be constructed. Must run from <c>dialogShown_cb</c>, not <c>initialize_cb</c> — the NX
    /// TreeListDemo sample is explicit that columns inserted earlier do not take, and the same construction
    /// timing applies to <see cref="Tree.SetPreSelectionTimeOut"/> (calling it from <c>initialize_cb</c>
    /// throws "operation performed during construction or destruction of the tree").
    ///
    /// That timing gate is per Explorer page, not just per dialog: <c>explorerNode_Material</c>,
    /// <c>explorerNode_Current</c> and <c>ExploreNodePending</c> are WizardGroup pages, and only the page
    /// that is current when the dialog opens has its child controls constructed — the other two trees throw
    /// the same exception even from dialogShown_cb until their page has been visited at least once. Cycling
    /// <see cref="Explorer.CurrentNode"/> through all three forces that construction, then restores whichever
    /// page was current so the visible page doesn't change under the user.</summary>
    public void SetUpColumns()
    {
        Trace("SetUpColumns: start");

        if (_explorer is not null)
        {
            var openingNode = _explorer.CurrentNode;
            Trace($"SetUpColumns: explorer opening node = {openingNode}");

            Trace("SetUpColumns: explorer.CurrentNode -> MaterialNode");
            _explorer.CurrentNode = MaterialNode;

            Trace("SetUpColumns: explorer.CurrentNode -> CurrentAssignmentNode");
            _explorer.CurrentNode = CurrentAssignmentNode;

            Trace("SetUpColumns: explorer.CurrentNode -> PendingNode");
            _explorer.CurrentNode = PendingNode;

            Trace($"SetUpColumns: explorer.CurrentNode -> restore {openingNode}");
            _explorer.CurrentNode = openingNode;
        }
        else
        {
            Trace("SetUpColumns: explorer block not resolved (null) — page cycling skipped");
        }

        // Hover drives the material thumbnail, so it needs a dwell long enough not to thrash the Label while
        // the pointer crosses the tree, but short enough to feel immediate.
        Trace("SetUpColumns: materialTree.SetPreSelectionTimeOut");
        _materialTree?.SetPreSelectionTimeOut(250.0);

        Trace("SetUpColumns: InsertColumns materialTree");
        InsertColumns(_materialTree,
            (MaterialColumn.Name, "Material", 220),
            (MaterialColumn.Detail, "Description", 160),
            (MaterialColumn.Density, "Density", 90));

        Trace("SetUpColumns: InsertColumns currentAssignmentTree");
        InsertColumns(_currentAssignmentTree,
            (AssignmentColumn.Name, "Material / Body", 220),
            (AssignmentColumn.Count, "Bodies / Kind", 110),
            (AssignmentColumn.DisplayMaterial, "Display material", 140));

        Trace("SetUpColumns: InsertColumns pendingAssignmentTree");
        InsertColumns(_pendingAssignmentTree,
            (PendingColumn.Name, "Material / Body", 220),
            (PendingColumn.Kind, "Kind", 90),
            (PendingColumn.Status, "Status", 260));

        Trace("SetUpColumns: done");
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

    /// <summary>Writes to the NX Listing Window via the same sink as warnings, so initialization can be
    /// traced step by step when the exact call that throws needs to be pinned down. Temporary diagnostic
    /// aid — safe to strip once the Explorer/tree construction-timing issue is confirmed fixed.</summary>
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

    /// <summary>Maps the selected entry back to the library it was populated from. Deliberately not built by
    /// wrapping the block's selected text in a <see cref="MaterialLibraryId"/> — display name and id are
    /// separate fields, and only happen to coincide for the filesystem repository.</summary>
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

        _materials.Rebuild(() =>
        {
            foreach (var root in roots)
                AddCategory(root, parent: null);
        });
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

    /// <summary>The density row, if the material has one. MatML property names vary between libraries, so this
    /// matches on the name containing "density" rather than on an exact id, and falls back to blank — the
    /// column is a convenience, never something a decision depends on.</summary>
    private static string DensityText(Material material)
    {
        var density = material.Properties
            .FirstOrDefault(p => p.Name.IndexOf("density", StringComparison.OrdinalIgnoreCase) >= 0);

        if (density is null)
            return string.Empty;

        return string.IsNullOrWhiteSpace(density.Unit)
            ? density.AsString()
            : $"{density.AsString()} {density.Unit}";
    }

    // ---- Material labels, one per Explorer node ----
    //
    // Each label names whatever its own tree currently has selected. Only the title text is written: the
    // blocks are Label/Bitmap, but Bitmap takes an absolute file path and there is no agreed location for
    // material thumbnails yet, so it is left alone rather than pointed at a guessed path.

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
        });
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
        });
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
