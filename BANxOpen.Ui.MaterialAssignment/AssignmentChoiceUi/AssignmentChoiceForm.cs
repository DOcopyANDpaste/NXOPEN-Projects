using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BANxOpen.Foundation.Core.Materials.Assignment.Choices;

namespace BANxOpen.Ui.MaterialAssignment.AssignmentChoiceUi;

/// <summary>The window behind <see cref="IAssignmentChoiceWindow"/>: a prompt, a table of options, and a
/// checkbox that narrows the table to the ones that fit the body.
///
/// Renders an <see cref="AssignmentChoice"/> and nothing else — the columns, the order, which rows are
/// preferred and what needs confirming all come from the domain that raised the question. Nothing here knows
/// what a sheet metal grade is, which is what lets a second domain reuse the window unchanged.
///
/// The filter starts on. A domain that sets <see cref="AssignmentChoice.PreferredOnlyLabel"/> is saying the
/// preferred options are the ones normally wanted, and the full list is one click away.</summary>
internal sealed class AssignmentChoiceForm : Form
{
    private readonly AssignmentChoice _choice;
    private readonly ListView _options;
    private readonly CheckBox? _preferredOnly;
    private readonly Button _ok;

    private readonly List<string> _warnings = new();

    public AssignmentChoiceForm(AssignmentChoice choice)
    {
        _choice = choice;

        Text = choice.Title;
        ClientSize = new Size(620, 420);
        MinimumSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;

        _options = BuildListView(choice);
        _options.SelectedIndexChanged += (_, _) => SyncOkEnabled();
        _options.DoubleClick += (_, _) => { if (_options.SelectedItems.Count > 0) Accept(); };

        _ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 90, Enabled = false };
        _ok.Click += (_, _) => Accept();

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

        // Escape cancels; Enter is deliberately NOT wired to OK, so that a confirmation-carrying option
        // cannot be accepted by a stray keypress.
        CancelButton = cancel;

        if (choice.PreferredOnlyLabel is { } filterLabel)
        {
            // Filtering is on by default, except when it would hide the option the domain wants selected —
            // opening on an empty-looking list with the preselection invisible reads as a bug.
            var preselectedIsPreferred = choice.Find(choice.PreselectedOptionId)?.IsPreferred ?? true;

            _preferredOnly = new CheckBox
            {
                Text = filterLabel,
                Checked = preselectedIsPreferred,
                AutoSize = true,
                Dock = DockStyle.Left,
            };
            _preferredOnly.CheckedChanged += (_, _) => Populate();
        }

        // One control per edge, so the layout does not depend on how WinForms orders two docks on the same
        // edge: the prompt and the filter share a header panel, and are docked to different edges inside it.
        Controls.Add(_options);
        Controls.Add(BuildHeaderPanel(choice));
        Controls.Add(BuildButtonPanel(_ok, cancel));

        Populate();
    }

    /// <summary>The option the user accepted, or null while they have not.</summary>
    public string? PickedOptionId { get; private set; }

    /// <summary>Messages about the pick for the caller to surface afterwards.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private static ListView BuildListView(AssignmentChoice choice)
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

        foreach (var column in choice.Columns)
        {
            listView.Columns.Add(
                column.Header,
                140,
                column.IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        }

        return listView;
    }

    /// <summary>The prompt, with the filter checkbox beneath it when the choice has one.</summary>
    private Panel BuildHeaderPanel(AssignmentChoice choice)
    {
        const int PromptHeight = 56;
        const int FilterHeight = 26;

        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = PromptHeight + (_preferredOnly is null ? 0 : FilterHeight),
            Padding = new Padding(10, 8, 10, 2),
        };

        // Added before the Fill label so the checkbox owns the bottom strip and the prompt takes what is left.
        if (_preferredOnly is not null)
        {
            var filterHost = new Panel { Dock = DockStyle.Bottom, Height = FilterHeight };
            filterHost.Controls.Add(_preferredOnly);
            panel.Controls.Add(filterHost);
        }

        // AutoSize off so the text wraps inside the fixed header rather than pushing the table off the form.
        panel.Controls.Add(new Label { Dock = DockStyle.Fill, Text = choice.Prompt, AutoSize = false });

        return panel;
    }

    private static Panel BuildButtonPanel(Button ok, Button cancel)
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(10, 8, 10, 8) };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        panel.Controls.Add(buttons);
        return panel;
    }

    /// <summary>Fills the table from the choice, honouring the filter, and restores the selection the domain
    /// asked for — or the one the user had, if the filter is what moved the rows around.</summary>
    private void Populate()
    {
        var previouslySelected = SelectedOptionId() ?? _choice.PreselectedOptionId;
        var showAll = _preferredOnly is null || !_preferredOnly.Checked;

        _options.BeginUpdate();
        try
        {
            _options.Items.Clear();

            foreach (var option in _choice.Options.Where(o => showAll || o.IsPreferred))
            {
                var item = new ListViewItem(option.Cells.Count > 0 ? option.Cells[0] : option.OptionId)
                {
                    Tag = option.OptionId,
                };

                foreach (var cell in option.Cells.Skip(1))
                    item.SubItems.Add(cell);

                if (string.Equals(option.OptionId, previouslySelected, System.StringComparison.Ordinal))
                    item.Selected = true;

                _options.Items.Add(item);
            }

            foreach (ColumnHeader column in _options.Columns)
                column.Width = -2; // size to the widest of header and content
        }
        finally
        {
            _options.EndUpdate();
        }

        SyncOkEnabled();
    }

    private string? SelectedOptionId() =>
        _options.SelectedItems.Count > 0 ? _options.SelectedItems[0].Tag as string : null;

    private void SyncOkEnabled() => _ok.Enabled = SelectedOptionId() is not null;

    /// <summary>Takes the selected option, asking first if it carries a confirmation. Declining returns to the
    /// window rather than cancelling: the user said no to that option, not to the question.</summary>
    private void Accept()
    {
        if (SelectedOptionId() is not { } optionId || _choice.Find(optionId) is not { } option)
            return;

        if (option.ConfirmationPrompt is { } prompt)
        {
            var answer = MessageBox.Show(
                this, prompt, _choice.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            // Confirmed at the window and then gone; recorded so the Apply result still says why.
            _warnings.Add(prompt.Replace(System.Environment.NewLine, " "));
        }

        PickedOptionId = optionId;
        DialogResult = DialogResult.OK;
        Close();
    }
}
