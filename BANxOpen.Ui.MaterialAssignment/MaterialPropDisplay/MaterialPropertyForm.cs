using System.Drawing;
using System.IO;
using System.Windows.Forms;
using BANxOpen.Materials.Contracts;

namespace BANxOpen.Ui.MaterialAssignment.MaterialPropDisplay;

/// <summary>Read-only material-property popup, WinForms replacement for the Block UI Styler tree
/// (<see cref="MaterialDisplay_UIBlock"/>/<see cref="MaterialPropDisplayAccessor"/>, kept for rollback).
///
/// Styled after the NX Block UI dialogs it opens from: Segoe UI 9pt on white, blue "▾ Section" headers, a flat
/// light-gray column header and a light-gray selection rather than the Windows blue. NX draws its own controls, so
/// this is a look-alike, not the NX component.
///
/// Built and shown entirely on <see cref="WinFormsMaterialPropertyWindow"/>'s dedicated STA pump thread —
/// this class must never reference NXOpen types or call into the NX session; only the plain immutable
/// <see cref="Material"/> record may cross onto that thread.</summary>
internal sealed class MaterialPropertyForm : Form
{
    private const int ImagePanelHeight = 160;
    private const int TextOnlyHeaderHeight = 56;
    private const int ImageSize = 128;
    private const int RowHeight = 18;
    private const int ChildIndent = 12;
    private const int CellPadding = 4;

    // Picked by eye against the NX dialog (section titles, tree header, selected row).
    private static readonly Color NxSectionBlue = Color.FromArgb(0, 102, 170);
    private static readonly Color NxHeaderFill = Color.FromArgb(240, 240, 240);
    private static readonly Color NxLine = Color.FromArgb(217, 217, 217);
    private static readonly Color NxSelectionFill = Color.FromArgb(229, 229, 229);
    private static readonly Color NxSubtleText = Color.FromArgb(80, 80, 80);

    /// <summary>Marks a list row as a section header rather than a property.</summary>
    private static readonly object SectionTag = new();

    private Image? _pictureBoxImage;
    private readonly ImageList _rowHeightImages = new() { ImageSize = new Size(1, RowHeight) };

    public MaterialPropertyForm(Material material)
    {
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.White;

        Text = $"{material.Name} Properties";
        ClientSize = new Size(480, 520);
        MinimumSize = new Size(360, 300);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        ShowIcon = false;
        MaximizeBox = true;
        MinimizeBox = true;

        var imagePanel = BuildImagePanel(material);
        var listView = BuildListView();
        PopulateListView(listView, material);

        // Dock resolution is by DockStyle priority (Top before Fill), not Controls.Add order, so either
        // add order lays out correctly here.
        Controls.Add(listView);
        Controls.Add(imagePanel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pictureBoxImage?.Dispose();
            _rowHeightImages.Dispose();
        }

        base.Dispose(disposing);
    }

    private Panel BuildImagePanel(Material material)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = ImagePanelHeight,
            Padding = new Padding(8),
            BackColor = Color.White,
        };

        var pictureBox = new PictureBox
        {
            Size = new Size(ImageSize, ImageSize),
            Location = new Point(8, 8),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false,
        };

        if (!string.IsNullOrWhiteSpace(material.ImagePath) && File.Exists(material.ImagePath))
        {
            try
            {
                _pictureBoxImage = Image.FromFile(material.ImagePath);
                pictureBox.Image = _pictureBoxImage;
                pictureBox.Visible = true;
            }
            catch
            {
                // Corrupt/unreadable/locked file, unsupported format, etc. Never let an image failure
                // stop the popup from opening — just hide the control and carry on with text only.
                _pictureBoxImage?.Dispose();
                _pictureBoxImage = null;
                pictureBox.Image = null;
                pictureBox.Visible = false;
            }
        }

        // Without an image the panel only has to hold the two lines of text.
        if (!pictureBox.Visible)
            panel.Height = TextOnlyHeaderHeight;

        var textLeft = pictureBox.Visible ? ImageSize + 20 : 8;
        var textWidth = panel.Width - textLeft - 8;

        var title = new Label
        {
            AutoSize = false,
            Location = new Point(textLeft, 8),
            Size = new Size(textWidth, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.Black,
            Text = material.Name,
        };

        var category = new Label
        {
            AutoSize = false,
            Location = new Point(textLeft, 28),
            Size = new Size(textWidth, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = NxSubtleText,
            Text = material.Category.DisplayName,
        };

        // The thin rule NX draws between a dialog's header and its content.
        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = NxLine };

        panel.Controls.Add(rule);
        panel.Controls.Add(title);
        panel.Controls.Add(category);
        panel.Controls.Add(pictureBox);
        return panel;
    }

    private ListView BuildListView()
    {
        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            ShowGroups = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            OwnerDraw = true,

            // A details view takes its row height from the small image list; a 1px-wide image sets it without
            // showing anything.
            SmallImageList = _rowHeightImages,
        };
        listView.Columns.Add("Property", 220, HorizontalAlignment.Left);
        listView.Columns.Add("Value", 160, HorizontalAlignment.Left);
        listView.Columns.Add("Unit", 80, HorizontalAlignment.Left);

        listView.DrawColumnHeader += DrawColumnHeader;
        listView.DrawItem += (_, e) => e.DrawDefault = false;
        listView.DrawSubItem += DrawSubItem;
        return listView;
    }

    private static void PopulateListView(ListView listView, Material material)
    {
        AddSection(listView, "Properties");

        // MatML properties come in three practical shapes and nothing declares which. A single value is one row;
        // a comma-separated list (a temperature-dependent table, typically) gets its own section with one row per
        // entry, after the single values.
        var tables = new List<MaterialPropertyValue>();
        foreach (var property in material.Properties)
        {
            if (property.AsArray().Count > 1)
            {
                tables.Add(property);
                continue;
            }

            AddRow(listView, NameOf(property), property.AsString(), property.Unit);
        }

        foreach (var property in tables)
        {
            AddSection(listView, NameOf(property));
            foreach (var value in property.AsArray())
                AddRow(listView, string.Empty, value, property.Unit);
        }
    }

    private static void AddSection(ListView listView, string title) =>
        listView.Items.Add(new ListViewItem($"▾ {title}") { Tag = SectionTag, ForeColor = NxSectionBlue });

    private static void AddRow(ListView listView, string name, string value, string? unit)
    {
        var item = new ListViewItem(name);
        item.SubItems.Add(value);
        item.SubItems.Add(unit ?? string.Empty);
        listView.Items.Add(item);
    }

    private static string NameOf(MaterialPropertyValue property) =>
        string.IsNullOrWhiteSpace(property.Symbol) ? property.Name : $"{property.Name} ({property.Symbol})";

    // ---- Owner drawing ----

    private void DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var fill = new SolidBrush(NxHeaderFill))
            e.Graphics.FillRectangle(fill, e.Bounds);

        using (var line = new Pen(NxLine))
        {
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
        }

        var textBounds = Rectangle.FromLTRB(e.Bounds.Left + CellPadding, e.Bounds.Top, e.Bounds.Right - CellPadding, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, textBounds, Color.Black,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
            return;

        var isSection = ReferenceEquals(e.Item.Tag, SectionTag);

        using (var fill = new SolidBrush(e.Item.Selected ? NxSelectionFill : Color.White))
            e.Graphics.FillRectangle(fill, e.Bounds);

        // Property rows sit under their section header, as they would under an NX group.
        var indent = e.ColumnIndex == 0 && !isSection ? ChildIndent : 0;
        var textBounds = Rectangle.FromLTRB(e.Bounds.Left + CellPadding + indent, e.Bounds.Top, e.Bounds.Right - CellPadding, e.Bounds.Bottom);

        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textBounds, isSection ? NxSectionBlue : Color.Black,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
