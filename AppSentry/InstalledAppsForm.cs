using AppSentry.Models;
using Microsoft.Win32;

namespace AppSentry;

internal class InstalledAppsForm : Form
{
    private readonly ThemeColors _theme;
    private readonly bool _isDarkMode;
    private readonly PackageManagerDetector _pkgDetector;
    private readonly ExclusionStore _exclusionStore;
    private Dictionary<string, InstalledApp> _apps;

    // ── Controls ─────────────────────────────────────────────────────────────
    private Panel _toolbarPanel = null!;
    private Button _btnRefresh = null!;
    private TextBox _txtSearch = null!;
    private Label _lblCount = null!;
    private ListView _listView = null!;
    private Panel _statusPanel = null!;
    private Label _statusLabel = null!;
    private ContextMenuStrip _contextMenu = null!;
    private ToolTip _toolTip = new();

    // ── Sorting ──────────────────────────────────────────────────────────────
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    // ── Column indices ────────────────────────────────────────────────────────
    private const int ColName = 0;
    private const int ColVersion = 1;
    private const int ColPublisher = 2;
    private const int ColInstallDate = 3;
    private const int ColInstallType = 4;
    private const int ColSize = 5;
    private const int ColPkgManager = 6;
    private const int ColInstallLocation = 7;

    public InstalledAppsForm(Dictionary<string, InstalledApp> apps, ThemeColors theme, bool isDarkMode, PackageManagerDetector pkgDetector, ExclusionStore exclusionStore)
    {
        _apps = apps;
        _theme = theme;
        _isDarkMode = isDarkMode;
        _pkgDetector = pkgDetector;
        _exclusionStore = exclusionStore;

        BuildForm();
        BuildToolbar();
        BuildListView();
        BuildStatusBar();
        BuildContextMenu();

        Controls.Add(_listView);
        Controls.Add(_toolbarPanel);
        Controls.Add(_statusPanel);

        PopulateList();
    }

    // ── Form shell ────────────────────────────────────────────────────────────

    private void BuildForm()
    {
        Text = "Installed Apps — AppSentry";
        Size = new Size(1200, 700);
        MinimumSize = new Size(800, 450);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;
        DoubleBuffered = true;
        Icon = AppIcon.Create();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void BuildToolbar()
    {
        _toolbarPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = _theme.ToolbarBg,
            Padding = new Padding(8, 6, 8, 6)
        };

        _btnRefresh = MakeToolButton("⟳ Refresh", "Re-scan all installed apps");
        _btnRefresh.Click += OnRefreshClick;

        var lblSearch = new Label
        {
            Text = "🔍",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Padding = new Padding(10, 4, 0, 0)
        };

        _txtSearch = new TextBox
        {
            Width = 200,
            PlaceholderText = "Filter apps...",
            BackColor = _theme.InputBg,
            ForeColor = _theme.InputFg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };
        _txtSearch.TextChanged += (_, _) => ApplyFilter();

        _lblCount = new Label
        {
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f),
            Padding = new Padding(10, 4, 0, 0),
            Text = ""
        };

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
        flow.Controls.AddRange([
            _btnRefresh,
            MakeSpacer(12),
            lblSearch, _txtSearch,
            _lblCount
        ]);

        _toolbarPanel.Controls.Add(flow);
    }

    // ── ListView ──────────────────────────────────────────────────────────────

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
            AllowColumnReorder = true,
            BackColor = _theme.ListBg,
            ForeColor = _theme.ListFg,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9f)
        };

        _listView.Columns.AddRange([
            new ColumnHeader { Text = "App Name",         Width = 220 },
            new ColumnHeader { Text = "Version",          Width = 90  },
            new ColumnHeader { Text = "Publisher",        Width = 160 },
            new ColumnHeader { Text = "Install Date",     Width = 90  },
            new ColumnHeader { Text = "Install Type",     Width = 90  },
            new ColumnHeader { Text = "Size",             Width = 75  },
            new ColumnHeader { Text = "Pkg Manager",      Width = 110 },
            new ColumnHeader { Text = "Install Location", Width = 240 }
        ]);

        _listView.DrawColumnHeader += OnDrawColumnHeader;
        _listView.DrawItem += (_, e) => e.DrawDefault = false;
        _listView.DrawSubItem += OnDrawSubItem;
        _listView.ColumnClick += OnColumnClick;
        _listView.DoubleClick += (_, _) => ShowDetailDialogForSelected();
        _listView.Resize += (_, _) => AutoFillLastColumn();
    }

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

    // ── Status bar ────────────────────────────────────────────────────────────

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
            Text = "Ready",
            Dock = DockStyle.Fill,
            ForeColor = _theme.StatusFg,
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _statusPanel.Controls.Add(_statusLabel);
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenuStrip();
        if (_isDarkMode)
        {
            _contextMenu.BackColor = _theme.ToolbarBg;
            _contextMenu.ForeColor = _theme.FormFg;
            _contextMenu.Renderer = new DarkToolStripRenderer();
        }

        var menuViewDetails = new ToolStripMenuItem("🔍 View Details…");
        menuViewDetails.Click += (_, _) => ShowDetailDialogForSelected();

        var menuUninstall = new ToolStripMenuItem("🗑 Uninstall…");
        menuUninstall.Click += OnContextUninstall;

        var menuOpenLocation = new ToolStripMenuItem("📂 Open Install Location");
        menuOpenLocation.Click += OnContextOpenLocation;

        var menuCopyName = new ToolStripMenuItem("📋 Copy App Name");
        menuCopyName.Click += OnContextCopyName;

        var menuCopyDetails = new ToolStripMenuItem("📋 Copy Details to Clipboard");
        menuCopyDetails.Click += OnContextCopyDetails;

        var menuExclude = new ToolStripMenuItem("🚫 Exclude this app…");
        menuExclude.Click += OnContextExclude;

        _contextMenu.Items.AddRange([
            menuViewDetails,
            new ToolStripSeparator(),
            menuUninstall,
            menuOpenLocation,
            new ToolStripSeparator(),
            menuCopyName,
            menuCopyDetails,
            new ToolStripSeparator(),
            menuExclude
        ]);

        _listView.ContextMenuStrip = _contextMenu;

        _contextMenu.Opening += (_, e) =>
        {
            var app = GetSelectedApp();
            var hasSelection = app != null;

            foreach (ToolStripItem item in _contextMenu.Items)
                item.Enabled = hasSelection;

            menuOpenLocation.Enabled = hasSelection && app != null &&
                !string.IsNullOrWhiteSpace(app.InstallLocation) &&
                Directory.Exists(app.InstallLocation);

            menuUninstall.Enabled = hasSelection;

            if (!hasSelection) e.Cancel = true;
        };
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

        var text = _listView.Columns[e.ColumnIndex].Text;
        if (e.ColumnIndex == _sortColumn)
            text += _sortAscending ? " ▲" : " ▼";

        var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, new Font("Segoe UI", 8.5f, FontStyle.Bold),
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

        var textBounds = new Rectangle(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _listView.Font,
            textBounds, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private void PopulateList()
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = _txtSearch?.Text?.Trim() ?? "";
        _listView.BeginUpdate();
        _listView.Items.Clear();

        var source = string.IsNullOrEmpty(filter)
            ? _apps.Values
            : _apps.Values.Where(a =>
                a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Version.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.InstallType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.InstallLocation.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var app in source.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            _listView.Items.Add(MakeItem(app));

        _listView.EndUpdate();

        var shown = _listView.Items.Count;
        var total = _apps.Count;
        _lblCount.Text = shown == total
            ? $"  {total} apps installed"
            : $"  {shown} of {total} apps";
        _statusLabel.Text = shown == total
            ? $"{total} apps installed on this machine"
            : $"Showing {shown} of {total} installed apps  ·  Filter: \"{filter}\"";
    }

    private ListViewItem MakeItem(InstalledApp app)
    {
        var item = new ListViewItem(app.Name);
        item.SubItems.Add(app.Version);
        item.SubItems.Add(app.Publisher);
        item.SubItems.Add(FormatInstallDate(app.InstallDate));
        item.SubItems.Add(app.InstallType);
        item.SubItems.Add(GetInstallSize(app.InstallLocation));
        item.SubItems.Add(_pkgDetector.Detect(app.Name, app.Version, app.InstallLocation));
        item.SubItems.Add(app.InstallLocation);
        item.Tag = app;
        return item;
    }

    private static string FormatInstallDate(string raw)
    {
        // Registry stores as YYYYMMDD
        if (raw.Length == 8 &&
            int.TryParse(raw[..4], out int y) &&
            int.TryParse(raw[4..6], out int m) &&
            int.TryParse(raw[6..8], out int d))
        {
            try { return new DateTime(y, m, d).ToString("yyyy-MM-dd"); }
            catch { }
        }
        return raw;
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = e.Column;
            _sortAscending = true;
        }

        _listView.ListViewItemSorter = new ListViewItemComparer(_sortColumn, _sortAscending);
        _listView.Sort();
        _listView.Invalidate();
    }

    private class ListViewItemComparer : System.Collections.IComparer
    {
        private readonly int _col;
        private readonly bool _asc;
        public ListViewItemComparer(int col, bool asc) { _col = col; _asc = asc; }
        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem a || y is not ListViewItem b) return 0;
            var ta = a.SubItems.Count > _col ? a.SubItems[_col].Text : "";
            var tb = b.SubItems.Count > _col ? b.SubItems[_col].Text : "";
            return (_asc ? 1 : -1) * string.Compare(ta, tb, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Context menu handlers ─────────────────────────────────────────────────

    private InstalledApp? GetSelectedApp()
    {
        if (_listView.SelectedItems.Count == 0) return null;
        return _listView.SelectedItems[0].Tag as InstalledApp;
    }

    private void OnContextUninstall(object? sender, EventArgs e)
    {
        var app = GetSelectedApp();
        if (app == null) return;

        var uninstallString = GetUninstallString(app.KeyPath);
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            MessageBox.Show(
                $"No uninstall command found for {app.Name}.\n\nYou can uninstall via Windows Settings > Apps.",
                "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                $"Uninstall {app.Name} {app.Version}?\n\nCommand: {uninstallString}",
                "Confirm Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {uninstallString}",
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start uninstaller: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnContextOpenLocation(object? sender, EventArgs e)
    {
        var app = GetSelectedApp();
        if (app == null || string.IsNullOrWhiteSpace(app.InstallLocation)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{app.InstallLocation}\""
            });
        }
        catch { }
    }

    private void OnContextCopyName(object? sender, EventArgs e)
    {
        var app = GetSelectedApp();
        if (app == null) return;
        Clipboard.SetText(app.Name);
        _statusLabel.Text = $"Copied: {app.Name}";
    }

    private void OnContextCopyDetails(object? sender, EventArgs e)
    {
        var app = GetSelectedApp();
        if (app == null) return;

        var details = $"""
            App Name:         {app.Name}
            Version:          {app.Version}
            Publisher:        {app.Publisher}
            Install Date:     {FormatInstallDate(app.InstallDate)}
            Install Type:     {app.InstallType}
            Install Location: {app.InstallLocation}
            Install Source:   {app.InstallSource}
            Installed By:     {app.InstalledBy}
            Size:             {GetInstallSize(app.InstallLocation)}
            Pkg Manager:      {_pkgDetector.Detect(app.Name, app.Version, app.InstallLocation)}
            Registry Key:     {app.KeyPath}
            """;
        Clipboard.SetText(details);
        _statusLabel.Text = $"Copied details for {app.Name} to clipboard.";
    }

    private void OnContextExclude(object? sender, EventArgs e)
    {
        var app = GetSelectedApp();
        if (app == null) return;

        var existing = _exclusionStore.GetEntry(app.Name);
        var prefill = existing ?? new ExclusionEntry(app.Name, true, false);

        if (ExclusionsForm.ShowExclusionDialogStatic(prefill, _theme, this, out var result))
        {
            _exclusionStore.Add(result);
            _statusLabel.Text = $"✓ Exclusion saved for \"{result.AppName}\"";
        }
    }

    // ── View Details dialog ────────────────────────────────────────────────────

    private void ShowDetailDialogForSelected()
    {
        var app = GetSelectedApp();
        if (app == null) return;
        ShowDetailDialog(app);
    }

    private void ShowDetailDialog(InstalledApp app)
    {
        var dlg = new Form
        {
            Text = $"Details — {app.Name}",
            Size = new Size(580, 440),
            MinimumSize = new Size(450, 350),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = _theme.FormBg,
            ForeColor = _theme.FormFg,
            Font = new Font("Segoe UI", 9f),
            ShowInTaskbar = false,
            MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.Sizable
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(20, 16, 20, 10),
            BackColor = _theme.FormBg
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var fields = new (string Label, string Value)[]
        {
            ("App Name",         app.Name),
            ("Version",          app.Version),
            ("Publisher",        app.Publisher),
            ("",                 ""),
            ("Install Date",     FormatInstallDate(app.InstallDate)),
            ("Install Type",     app.InstallType),
            ("Installed By",     app.InstalledBy),
            ("Install Source",   string.IsNullOrEmpty(app.InstallSource) ? "—" : app.InstallSource),
            ("Install Location", string.IsNullOrEmpty(app.InstallLocation) ? "—" : app.InstallLocation),
            ("Install Size",     GetInstallSize(app.InstallLocation)),
            ("Pkg Manager",      _pkgDetector.Detect(app.Name, app.Version, app.InstallLocation)),
            ("",                 ""),
            ("Registry Key",     app.KeyPath)
        };

        int row = 0;
        foreach (var (label, value) in fields)
        {
            if (string.IsNullOrEmpty(label))
            {
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
                panel.RowCount = ++row;
                continue;
            }

            var lblName = new Label
            {
                Text = label,
                AutoSize = true,
                ForeColor = _theme.MutedFg,
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Padding(0, 4, 12, 4),
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            };

            var lblValue = new Label
            {
                Text = value,
                AutoSize = true,
                ForeColor = _theme.FormFg,
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(0, 4, 0, 4),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                MaximumSize = new Size(400, 0)
            };

            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(lblName, 0, row);
            panel.Controls.Add(lblValue, 1, row);
            panel.RowCount = ++row;
        }

        // Footer
        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            BackColor = _theme.ToolbarBg,
            Padding = new Padding(10, 6, 10, 6)
        };

        var btnClose = new Button
        {
            Text = "Close",
            Width = 80,
            Height = 30,
            BackColor = _theme.ButtonBg,
            ForeColor = _theme.FormFg,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            Font = new Font("Segoe UI", 9f),
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        btnClose.FlatAppearance.BorderColor = _theme.ButtonBorder;
        btnClose.Location = new Point(footerPanel.Width - 96, 7);
        footerPanel.Resize += (_, _) => btnClose.Location = new Point(footerPanel.Width - 96, 7);
        dlg.AcceptButton = btnClose;
        footerPanel.Controls.Add(btnClose);

        dlg.Controls.Add(panel);
        dlg.Controls.Add(footerPanel);
        dlg.ShowDialog(this);
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private void OnRefreshClick(object? sender, EventArgs e)
    {
        _btnRefresh.Enabled = false;
        _statusLabel.Text = "Scanning installed apps…";
        Cursor = Cursors.WaitCursor;

        Task.Run(() =>
        {
            var apps = RegistryScanner.Scan();
            Invoke(() =>
            {
                _apps = apps;
                Cursor = Cursors.Default;
                _btnRefresh.Enabled = true;
                ApplyFilter();
            });
        });
    }

    // ── Static helpers (duplicated from MainForm) ─────────────────────────────

    private static string GetUninstallString(string keyPath)
    {
        try
        {
            RegistryKey? hive = null;
            string subPath;

            if (keyPath.StartsWith("HKLM\\"))
            {
                hive = Registry.LocalMachine;
                subPath = keyPath[5..];
            }
            else if (keyPath.StartsWith("HKCU\\"))
            {
                hive = Registry.CurrentUser;
                subPath = keyPath[5..];
            }
            else if (keyPath.StartsWith("HKU\\"))
            {
                hive = Registry.Users;
                subPath = keyPath[4..];
            }
            else return "";

            using var key = hive.OpenSubKey(subPath, false);
            return key?.GetValue("UninstallString") as string ?? "";
        }
        catch { return ""; }
    }

    private static string GetInstallSize(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return "";
        try
        {
            var dir = new DirectoryInfo(installLocation);
            if (!dir.Exists) return "";
            long totalBytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return FormatSize(totalBytes);
        }
        catch { return ""; }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

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

    private static Panel MakeSpacer(int width) =>
        new() { Width = width, Height = 1, BackColor = Color.Transparent, Margin = Padding.Empty };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
