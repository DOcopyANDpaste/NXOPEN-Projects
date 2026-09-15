using System.Drawing;
using System.IO;
using System.Windows.Forms;
using BANxOpen.Foundation.Contracts.Materials;

namespace BANxOpen.Ui.MaterialAssignment.MaterialPropDisplay;

/// <summary>Read-only material-property popup, WinForms replacement for the Block UI Styler tree
/// (<see cref="MaterialDisplay_UIBlock"/>/<see cref="MaterialPropDisplayAccessor"/>, kept for rollback).
///
/// Built and shown entirely on <see cref="WinFormsMaterialPropertyWindow"/>'s dedicated STA pump thread —
/// this class must never reference NXOpen types or call into the NX session; only the plain immutable
/// <see cref="Material"/> record may cross onto that thread.</summary>
internal sealed class MaterialPropertyForm : Form
{
    private const int ImagePanelHeight = 160;
    private const int ImageSize = 128;

    private Image? _pictureBoxImage;

    public MaterialPropertyForm(Material material)
    {
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
            _pictureBoxImage?.Dispose();

        base.Dispose(disposing);
    }

    private Panel BuildImagePanel(Material material)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = ImagePanelHeight,
            Padding = new Padding(8),
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

        var header = new Label
        {
            AutoSize = false,
            Location = new Point(pictureBox.Visible ? ImageSize + 20 : 8, 8),
            Size = new Size(panel.Width - (pictureBox.Visible ? ImageSize + 28 : 16), ImagePanelHeight - 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Text = $"{material.Name}{Environment.NewLine}{material.Category.DisplayName}",
        };

        panel.Controls.Add(header);
        panel.Controls.Add(pictureBox);
        return panel;
    }

    private static ListView BuildListView()
    {
        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
        };
        listView.Columns.Add("Property", 220, HorizontalAlignment.Left);
        listView.Columns.Add("Value", 160, HorizontalAlignment.Left);
        listView.Columns.Add("Unit", 80, HorizontalAlignment.Left);
        return listView;
    }

    private static void PopulateListView(ListView listView, Material material)
    {
        var propertiesGroup = new ListViewGroup("Properties");
        listView.Groups.Add(propertiesGroup);

        foreach (var property in material.Properties)
        {
            var values = property.AsArray();

            // MatML properties come in three practical shapes and nothing declares which. A single value
            // is one row; a comma-separated list (a temperature-dependent table, typically) gets its own
            // group with one row per entry.
            var isTable = values.Count > 1;

            if (!isTable)
            {
                var item = new ListViewItem(NameOf(property)) { Group = propertiesGroup };
                item.SubItems.Add(property.AsString());
                item.SubItems.Add(property.Unit ?? string.Empty);
                listView.Items.Add(item);
                continue;
            }

            var tableGroup = new ListViewGroup(NameOf(property));
            listView.Groups.Add(tableGroup);

            foreach (var value in values)
            {
                var item = new ListViewItem(string.Empty) { Group = tableGroup };
                item.SubItems.Add(value);
                item.SubItems.Add(property.Unit ?? string.Empty);
                listView.Items.Add(item);
            }
        }
    }

    private static string NameOf(MaterialPropertyValue property) =>
        string.IsNullOrWhiteSpace(property.Symbol) ? property.Name : $"{property.Name} ({property.Symbol})";
}
