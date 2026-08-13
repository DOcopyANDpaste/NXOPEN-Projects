using NXOpen.BlockStyler;
using NxOpen.Foundation.Contracts.Materials;

namespace NxAdapters.Ui.MaterialPropDisplay;

/// <summary>Typed access to the two blocks inside <c>MaterialDisplay_UIBlock.dlx</c> — the same role
/// <see cref="BlockAccessor"/> plays for the main dialog, kept separate because this is a different block set
/// with a different lifetime.
///
/// The blocks are resolved from the user-defined block's own <c>TopBlock</c> rather than from the host
/// dialog: a user-defined UI block can be dropped into more than one dialog, so its contents are addressed
/// relative to itself.</summary>
public sealed class MaterialPropDisplayAccessor
{
    internal const string ImageLabelId = "lbl_Image";
    internal const string PropertyTreeId = "MaterialPropTree";

    private static class PropertyColumn
    {
        public const int Name = 0;
        public const int Value = 1;
        public const int Unit = 2;
    }

    private readonly Label? _image;
    private readonly Tree? _tree;
    private readonly TreeBinding<MaterialPropertyValue>? _properties;
    private readonly Action<string>? _logWarning;

    public MaterialPropDisplayAccessor(CompositeBlock topBlock, Action<string>? logWarning = null)
    {
        _logWarning = logWarning;
        _image = TryFindBlock<Label>(topBlock, ImageLabelId);
        _tree = TryFindBlock<Tree>(topBlock, PropertyTreeId);

        if (_tree is not null)
            _properties = new TreeBinding<MaterialPropertyValue>(_tree);
    }

    /// <summary>Creates the property tree's columns. Called from the host dialog's <c>dialogShown</c>, for the
    /// same reason the main dialog does it there — columns inserted during initialize do not take.</summary>
    public void SetUpColumns()
    {
        if (_tree is null)
            return;

        _tree.InsertColumn(PropertyColumn.Name, "Property", 220);
        _tree.InsertColumn(PropertyColumn.Value, "Value", 200);
        _tree.InsertColumn(PropertyColumn.Unit, "Unit", 90);

        foreach (var column in new[] { PropertyColumn.Name, PropertyColumn.Value, PropertyColumn.Unit })
            _tree.SetColumnResizePolicy(column, Tree.ColumnResizePolicy.ConstantWidth);
    }

    public void Show(Material material)
    {
        // Title only, not Bitmap: the block is a Label/Bitmap, but Bitmap takes an absolute file path and
        // there is no agreed location for material thumbnails yet. Same call the main dialog's labels make.
        if (_image is not null)
            _image.Label = material.Name;

        if (_properties is null)
            return;

        _properties.Rebuild(() =>
        {
            foreach (var property in material.Properties)
            {
                var values = property.AsArray();

                // MatML properties come in three practical shapes and nothing declares which. A single value
                // is one row; a comma-separated list (a temperature-dependent table, typically) becomes a
                // parent row with one child per entry, which is the reason this is a tree and not a table.
                var isTable = values.Count > 1;

                var node = _properties.Add(NameOf(property), property, parent: null);
                node.SetColumnDisplayText(
                    PropertyColumn.Value,
                    isTable ? $"{values.Count} values" : property.AsString());
                node.SetColumnDisplayText(PropertyColumn.Unit, property.Unit ?? string.Empty);

                if (!isTable)
                    continue;

                foreach (var value in values)
                {
                    var child = _properties.Add(string.Empty, property, node);
                    child.SetColumnDisplayText(PropertyColumn.Value, value);
                    child.SetColumnDisplayText(PropertyColumn.Unit, property.Unit ?? string.Empty);
                }
            }
        });
    }

    private static string NameOf(MaterialPropertyValue property) =>
        string.IsNullOrWhiteSpace(property.Symbol) ? property.Name : $"{property.Name} ({property.Symbol})";

    private T? TryFindBlock<T>(CompositeBlock topBlock, string blockId) where T : class
    {
        try
        {
            var block = topBlock.FindBlock(blockId) as T;
            if (block is null)
                _logWarning?.Invoke($"Material property block '{blockId}' is missing or is not a {typeof(T).Name}.");

            return block;
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"Material property block '{blockId}' could not be resolved ({ex.Message}).");
            return null;
        }
    }
}
