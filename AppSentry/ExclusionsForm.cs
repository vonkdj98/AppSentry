using AppSentry.Models;

namespace AppSentry;

/// <summary>
/// Management form for viewing, editing, and removing app exclusions.
/// </summary>
internal class ExclusionsForm : Form
{
    private readonly ExclusionStore _store;
    private readonly ThemeColors _theme;
    private readonly bool _isDarkMode;

    private ListView _listView = null!;
    private Panel _toolbarPanel = null!;
    private Panel _statusPanel = null!;
    private Label _statusLabel = null!;
    private Button _btnRemove = null!;
    private Button _btnEdit = null!;
    private Button _btnAdd = null!;
    private ToolTip _toolTip = new();

    public ExclusionsForm(ExclusionStore store, ThemeColors theme, bool isDarkMode)
    {
        _store = store;
        _theme = theme;
        _isDarkMode = isDarkMode;

        BuildForm();
        BuildToolbar();
        BuildListView();
        BuildStatusBar();

        Controls.Add(_listView);
        Controls.Add(_toolbarPanel);
        Controls.Add(_statusPanel);

        PopulateList();
    }

    private void BuildForm()
    {
        Text = "Manage Exclusions — AppSentry";
        Size = new Size(700, 450);
        MinimumSize = new Size(500, 300);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;
        DoubleBuffered = true;
        Icon = AppIcon.Create();
        MaximizeBox = false;
    }

    private void BuildToolbar()
    {
        _toolbarPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = _theme.ToolbarBg,
            Padding = new Padding(8, 6, 8, 6)
        };

        _btnAdd = MakeToolButton("➕ Add", "Manually add an exclusion");
        _btnAdd.Click += OnAddClick;

        _btnEdit = MakeToolButton("✏ Edit", "Edit selected exclusion");
        _btnEdit.Click += OnEditClick;

        _btnRemove = MakeToolButton("🗑 Remove", "Remove selected exclusion");
        _btnRemove.Click += OnRemoveClick;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        flow.Controls.AddRange([_btnAdd, _btnEdit, _btnRemove]);

        _toolbarPanel.Controls.Add(flow);
    }

    private void BuildListView()
    {
        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            MultiSelect = false,
            OwnerDraw = true,
            BackColor = _theme.ListBg,
            ForeColor = _theme.ListFg,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9f)
        };

        _listView.Columns.AddRange([
            new ColumnHeader { Text = "App Name",              Width = 300 },
            new ColumnHeader { Text = "Exclude Notifications", Width = 150 },
            new ColumnHeader { Text = "Exclude Logging",       Width = 150 }
        ]);

        _listView.DrawColumnHeader += OnDrawColumnHeader;
        _listView.DrawItem += (_, e) => e.DrawDefault = false;
        _listView.DrawSubItem += OnDrawSubItem;
        _listView.DoubleClick += OnEditClick;
        _listView.SelectedIndexChanged += (_, _) => UpdateButtonState();
        _listView.Resize += (_, _) => AutoFillLastColumn();
    }

    private void BuildStatusBar()
    {
        _statusPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            BackColor = _theme.StatusBg,
            Padding = new Padding(10, 4, 10, 4)
        };

        _statusLabel = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            ForeColor = _theme.StatusFg,
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _statusPanel.Controls.Add(_statusLabel);
    }

    // ── Owner-draw ────────────────────────────────────────────────────────────

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(_theme.HeaderBg);
        e.Graphics.FillRectangle(bg, e.Bounds);

        using var borderPen = new Pen(_theme.HeaderBorder);
        e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1,
            e.Bounds.Right, e.Bounds.Bottom - 1);

        if (e.ColumnIndex < _listView.Columns.Count - 1)
        {
            using var sepPen = new Pen(_theme.HeaderBorder);
            e.Graphics.DrawLine(sepPen, e.Bounds.Right - 1, e.Bounds.Top + 4,
                e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        }

        var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, _listView.Columns[e.ColumnIndex].Text,
            new Font("Segoe UI", 8.5f, FontStyle.Bold),
            textBounds, _theme.HeaderFg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null) return;
        var bounds = e.Bounds;

        Color rowBg = e.Item.Selected
            ? _theme.SelectedBg
            : (e.ItemIndex % 2 == 0) ? _theme.ListBg : _theme.AltRowBg;

        using (var bg = new SolidBrush(rowBg))
            e.Graphics.FillRectangle(bg, bounds);

        using (var pen = new Pen(_theme.RowBorder))
            e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

        var textColor = e.Item.Selected ? _theme.SelectedFg : _theme.ListFg;

        // Color the Yes/No values
        if (e.ColumnIndex > 0 && !e.Item.Selected)
        {
            textColor = e.SubItem.Text == "Yes"
                ? _theme.RemovedAccent   // red for "excluded"
                : _theme.InstalledAccent; // green for "not excluded"
        }

        var textBounds = new Rectangle(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _listView.Font,
            textBounds, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private void PopulateList()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var entry in _store.Entries)
        {
            var item = new ListViewItem(entry.AppName);
            item.SubItems.Add(entry.ExcludeNotifications || entry.ExcludeLogging ? "Yes" : "No");
            item.SubItems.Add(entry.ExcludeLogging ? "Yes" : "No");
            item.Tag = entry;
            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
        UpdateButtonState();
        _statusLabel.Text = _store.Entries.Count == 0
            ? "No exclusions configured. Right-click an app in the main window to exclude it."
            : $"{_store.Entries.Count} exclusion(s)";
    }

    private void UpdateButtonState()
    {
        var hasSelection = _listView.SelectedItems.Count > 0;
        _btnEdit.Enabled = hasSelection;
        _btnRemove.Enabled = hasSelection;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnAddClick(object? sender, EventArgs e)
    {
        if (ShowExclusionDialog(null, out var result))
        {
            _store.Add(result);
            PopulateList();
        }
    }

    private void OnEditClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        var entry = _listView.SelectedItems[0].Tag as ExclusionEntry;
        if (entry == null) return;

        if (ShowExclusionDialog(entry, out var result))
        {
            // If name changed, remove old first
            if (!entry.AppName.Equals(result.AppName, StringComparison.OrdinalIgnoreCase))
                _store.Remove(entry.AppName);
            _store.Add(result);
            PopulateList();
        }
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        var entry = _listView.SelectedItems[0].Tag as ExclusionEntry;
        if (entry == null) return;

        if (MessageBox.Show($"Remove exclusion for \"{entry.AppName}\"?\n\nThis app will be tracked normally again.",
            "Remove Exclusion", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _store.Remove(entry.AppName);
        PopulateList();
    }

    // ── Exclusion edit dialog ─────────────────────────────────────────────────

    /// <summary>Shows a dialog to add or edit an exclusion. Returns true if user confirmed.</summary>
    internal bool ShowExclusionDialog(ExclusionEntry? existing, out ExclusionEntry result)
    {
        return ShowExclusionDialogStatic(existing, _theme, this, out result);
    }

    /// <summary>
    /// Static method so it can be called from MainForm and InstalledAppsForm too.
    /// </summary>
    internal static bool ShowExclusionDialogStatic(ExclusionEntry? existing, ThemeColors theme, IWin32Window owner, out ExclusionEntry result)
    {
        result = null!;

        var dlg = new Form
        {
            Text = existing == null ? "Add Exclusion" : "Edit Exclusion",
            Size = new Size(440, 260),
            MinimumSize = new Size(380, 240),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = theme.FormBg,
            ForeColor = theme.FormFg,
            Font = new Font("Segoe UI", 9f),
            ShowInTaskbar = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog
        };

        var lblName = new Label
        {
            Text = "App Name (exact match, case-insensitive):",
            Location = new Point(20, 20),
            AutoSize = true,
            ForeColor = theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f)
        };

        var txtName = new TextBox
        {
            Location = new Point(20, 42),
            Width = 385,
            Text = existing?.AppName ?? "",
            BackColor = theme.InputBg,
            ForeColor = theme.InputFg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f)
        };

        var chkNotifications = new CheckBox
        {
            Text = "Exclude from notifications (still logged, no popup/sound)",
            Location = new Point(20, 80),
            AutoSize = true,
            ForeColor = theme.FormFg,
            Checked = existing?.ExcludeNotifications ?? true,
            Font = new Font("Segoe UI", 9f)
        };

        var chkLogging = new CheckBox
        {
            Text = "Exclude from logging (not recorded at all)",
            Location = new Point(20, 110),
            AutoSize = true,
            ForeColor = theme.FormFg,
            Checked = existing?.ExcludeLogging ?? false,
            Font = new Font("Segoe UI", 9f)
        };

        var lblHint = new Label
        {
            Text = "💡 \"Exclude logging\" also suppresses notifications since nothing is recorded.",
            Location = new Point(20, 142),
            AutoSize = true,
            ForeColor = theme.MutedFg,
            Font = new Font("Segoe UI", 8f)
        };

        var btnOk = new Button
        {
            Text = "Save",
            Width = 80,
            Height = 30,
            Location = new Point(226, 178),
            BackColor = theme.ButtonBg,
            ForeColor = theme.ButtonFg,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            Font = new Font("Segoe UI", 9f)
        };
        btnOk.FlatAppearance.BorderColor = theme.ButtonBorder;

        var btnCancel = new Button
        {
            Text = "Cancel",
            Width = 80,
            Height = 30,
            Location = new Point(316, 178),
            BackColor = theme.ButtonBg,
            ForeColor = theme.ButtonFg,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel,
            Font = new Font("Segoe UI", 9f)
        };
        btnCancel.FlatAppearance.BorderColor = theme.ButtonBorder;

        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;
        dlg.Controls.AddRange([lblName, txtName, chkNotifications, chkLogging, lblHint, btnOk, btnCancel]);

        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;

        var name = txtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("App name cannot be empty.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // If logging is excluded, notifications are implicitly excluded too
        var excludeNotif = chkNotifications.Checked || chkLogging.Checked;
        result = new ExclusionEntry(name, excludeNotif, chkLogging.Checked);
        return true;
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void AutoFillLastColumn()
    {
        if (_listView.Columns.Count == 0) return;
        int total = 0;
        for (int i = 0; i < _listView.Columns.Count - 1; i++)
            total += _listView.Columns[i].Width;
        int remaining = _listView.ClientSize.Width - total;
        if (remaining > 80)
            _listView.Columns[_listView.Columns.Count - 1].Width = remaining;
    }

    private Button MakeToolButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBg,
            ForeColor = _theme.ButtonFg,
            Font = new Font("Segoe UI", 8.5f),
            Cursor = Cursors.Hand,
            Padding = new Padding(6, 2, 6, 2),
            Margin = new Padding(0, 0, 4, 0),
            Height = 28
        };
        btn.FlatAppearance.BorderColor = _theme.ButtonBorder;
        btn.FlatAppearance.MouseOverBackColor = _theme.ButtonHover;
        _toolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
