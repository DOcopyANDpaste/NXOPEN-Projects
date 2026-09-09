using NXOpen;
using NXOpen.BlockStyler;

namespace NxAdapters.Ui;

/// <summary>Owns one Tree block's contents and the mapping from its rows back to the domain objects they
/// were rendered from. Every tree in this dialog goes through one of these, so the rebuild idiom and the
/// row-to-domain lookup exist once.
///
/// Rows are keyed by <see cref="TaggedObject.Tag"/>, NOT by the <see cref="Node"/> instance:
/// <c>BlockStyler.Node</c> derives from <c>TaggedObject</c>, and nothing in that chain (TaggedObject ->
/// NXRemotableObject -> MarshalByRefObject) overrides Equals/GetHashCode — so Nodes compare by reference.
/// If NX ever hands a callback a fresh managed wrapper for an existing row, a <c>Dictionary&lt;Node, T&gt;</c>
/// would silently miss and the user's click would resolve to nothing. Tag is the stable underlying identity
/// and is a plain enum, so it hashes correctly either way.
///
/// This keeps the convention <see cref="BlockAccessor"/> has always run on: a selection is never turned
/// back into a domain value by parsing what the block displays — it is mapped through what was last
/// populated.</summary>
public sealed class TreeBinding<T> where T : class
{
    private readonly Tree _tree;
    private readonly Dictionary<Tag, T> _byNodeTag = new();

    public TreeBinding(Tree tree) => _tree = tree;

    /// <summary>Clears the tree and repopulates it via <paramref name="populate"/>, with the whole rebuild
    /// frozen behind Redraw(false)/Redraw(true) so NX paints once instead of once per node. Redraw is
    /// restored in a finally — leaving a tree frozen after an exception makes the dialog look hung.</summary>
    public void Rebuild(Action populate)
    {
        _tree.Redraw(false);
        try
        {
            Clear();
            populate();
        }
        finally
        {
            _tree.Redraw(true);
        }
    }

    /// <summary>Appends a row. Pass <paramref name="value"/> null for a structural row (an intermediate
    /// category, say) that the user can see but cannot act on — it simply won't resolve to anything.</summary>
    public Node Add(string displayText, T? value, Node? parent = null)
    {
        var node = _tree.CreateNode(displayText);

        // AlwaysLast rather than Sort: order is already decided in Core (MaterialCategoryTreeBuilder, or the
        // usage-row ordering in the presenter). Letting the tree re-sort would silently override it.
        _tree.InsertNode(node, parent, null, Tree.NodeInsertOption.AlwaysLast);

        if (value is not null)
            _byNodeTag[node.Tag] = value;

        return node;
    }

    public T? Resolve(Node? node) =>
        node is not null && _byNodeTag.TryGetValue(node.Tag, out var value) ? value : null;

    public IReadOnlyList<T> ResolveSelected()
    {
        var selected = _tree.GetSelectedNodes();
        if (selected is null)
            return Array.Empty<T>();

        return selected.Select(Resolve).OfType<T>().ToList();
    }

    /// <summary>The rows a context-menu command should act on: the current selection when there is one,
    /// otherwise just the row that was right-clicked. Mirrors the precedence TreeListDemo's menu handler
    /// uses, and is what makes right-clicking a row you never selected behave the way users expect.</summary>
    public IReadOnlyList<T> ResolveSelectedOr(Node? clicked)
    {
        var selected = ResolveSelected();
        if (selected.Count > 0)
            return selected;

        var single = Resolve(clicked);
        return single is null ? Array.Empty<T>() : new[] { single };
    }

    private void Clear()
    {
        // Collect the roots before deleting any: DeleteNode invalidates the node it removes, so walking the
        // sibling chain while deleting from it would step off a dead node. Deleting a root takes its whole
        // subtree with it, so roots are all that need visiting. (There is no Tree.Clear().)
        var roots = new List<Node>();
        for (var node = _tree.RootNode; node is not null; node = node.NextSiblingNode)
            roots.Add(node);

        foreach (var root in roots)
            _tree.DeleteNode(root);

        _byNodeTag.Clear();
    }
}
