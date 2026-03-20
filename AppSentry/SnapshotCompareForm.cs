using AppSentry.Models;

namespace AppSentry;

/// <summary>
/// Lets the user pick a date range and see all changes between those two dates.
/// </summary>
internal class SnapshotCompareForm : Form
{
    private readonly ThemeColors _theme;
    private readonly List<ChangeEvent> _allEvents;
    private DateTimePicker _dtpFrom = null!;
    private DateTimePicker _dtpTo = null!;
    private ListView _resultList = null!;
    private Label _summaryLabel = null!;

    public SnapshotCompareForm(List<ChangeEvent> allEvents, ThemeColors theme)
    {
        _theme = theme;
        _allEvents = allEvents;
        BuildForm();
    }

    private void BuildForm()
    {
        Text = "Snapshot Comparison — Changes Between Dates";
        Size = new Size(1300, 600);
        MinimumSize = new Size(900, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;

        // ── Top panel with date pickers ──────────────────────────────────────
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 80,
            BackColor = _theme.ToolbarBg,
            Padding = new Padding(16, 10, 16, 10)
        };

        var lblFrom = new Label
        {
            Text = "From:",
            AutoSize = true,
            ForeColor = _theme.FormFg,
            Font = new Font("Segoe UI Semibold", 9f),
            Location = new Point(16, 18)
        };

        // Default: 7 days ago
        var earliestDate = _allEvents.Count > 0
            ? _allEvents.Min(e => e.DetectedAt).Date
            : DateTime.Today.AddDays(-30);

        _dtpFrom = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm",
            Value = DateTime.Today.AddDays(-7) < earliestDate ? earliestDate : DateTime.Today.AddDays(-7),
            Location = new Point(65, 14),
            Width = 180,
            CalendarMonthBackground = _theme.InputBg,
            CalendarForeColor = _theme.InputFg
        };

        var lblTo = new Label
        {
            Text = "To:",
            AutoSize = true,
            ForeColor = _theme.FormFg,
            Font = new Font("Segoe UI Semibold", 9f),
            Location = new Point(270, 18)
        };

        _dtpTo = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm",
            Value = DateTime.Now,
            Location = new Point(302, 14),
            Width = 180,
            CalendarMonthBackground = _theme.InputBg,
            CalendarForeColor = _theme.InputFg
        };

        var btnCompare = new Button
        {
            Text = "⟳ Compare",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.UpdatedAccent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9f),
            Location = new Point(510, 12),
            Height = 30,
            Cursor = Cursors.Hand
        };
        btnCompare.FlatAppearance.BorderSize = 0;
        btnCompare.Click += (_, _) => RunComparison();

        // Quick range buttons
        var lblQuick = new Label
        {
            Text = "Quick:",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8f),
            Location = new Point(16, 52)
        };

        var quickRanges = new (string Label, int Days)[]
        {
            ("Today", 0), ("24h", 1), ("7 days", 7), ("30 days", 30), ("All", -1)
        };

        int qx = 65;
        var quickControls = new List<Control> { lblQuick };
        foreach (var (label, days) in quickRanges)
        {
            var btn = new LinkLabel
            {
                Text = label,
                AutoSize = true,
                LinkColor = _theme.UpdatedAccent,
                ActiveLinkColor = _theme.InstalledAccent,
                VisitedLinkColor = _theme.UpdatedAccent,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(qx, 50)
            };
            var d = days;
            btn.Click += (_, _) =>
            {
                _dtpFrom.Value = d == -1 ? earliestDate : DateTime.Today.AddDays(-d);
                _dtpTo.Value = DateTime.Now;
                RunComparison();
            };
            quickControls.Add(btn);
            qx += TextRenderer.MeasureText(label, btn.Font).Width + 12;
        }

        topPanel.Controls.AddRange([lblFrom, _dtpFrom, lblTo, _dtpTo, btnCompare, .. quickControls]);

        // ── Summary label ────────────────────────────────────────────────────
        _summaryLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = _theme.FormBg,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            Text = "Select a date range and click Compare."
        };

        // ── Result list ──────────────────────────────────────────────────────
        _resultList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            OwnerDraw = true,
            BackColor = _theme.ListBg,
            ForeColor = _theme.ListFg,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9f)
        };

        _resultList.Columns.AddRange([
            new ColumnHeader { Text = "Detected At",    Width = 140 },
            new ColumnHeader { Text = "App Name",       Width = 180 },
            new ColumnHeader { Text = "Version",        Width = 80  },
            new ColumnHeader { Text = "Change",         Width = 70  },
            new ColumnHeader { Text = "Publisher",      Width = 130 },
            new ColumnHeader { Text = "Prev Version",   Width = 80  },
            new ColumnHeader { Text = "Installed By",   Width = 100 },
            new ColumnHeader { Text = "Install Source", Width = 140 },
            new ColumnHeader { Text = "Install Type",   Width = 80  },
            new ColumnHeader { Text = "Size",           Width = 70  },
            new ColumnHeader { Text = "Source",         Width = 90  }
        ]);

        _resultList.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(_theme.HeaderBg);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var pen = new Pen(_theme.HeaderBorder);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var bounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, _resultList.Columns[e.ColumnIndex].Text,
                new Font("Segoe UI", 8.5f, FontStyle.Bold), bounds, _theme.HeaderFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };

        _resultList.DrawItem += (_, e) => e.DrawDefault = false;
        _resultList.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            var changeText = e.Item.SubItems.Count > 3 ? e.Item.SubItems[3].Text : "";
            var rowBg = changeText switch
            {
                "Installed" => _theme.InstalledBg,
                "Updated" => _theme.UpdatedBg,
                "Removed" => _theme.RemovedBg,
                _ => (e.ItemIndex % 2 == 0) ? _theme.ListBg : _theme.AltRowBg
            };
            if (e.Item.Selected) rowBg = _theme.SelectedBg;

            using (var bg = new SolidBrush(rowBg))
                e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(_theme.RowBorder))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            var fg = e.Item.Selected ? _theme.SelectedFg : _theme.ListFg;
            if (e.ColumnIndex == 3 && !e.Item.Selected)
            {
                fg = changeText switch
                {
                    "Installed" => _theme.InstalledAccent,
                    "Updated" => _theme.UpdatedAccent,
                    "Removed" => _theme.RemovedAccent,
                    _ => fg
                };
            }

            var bounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _resultList.Font, bounds, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        // ── Footer ───────────────────────────────────────────────────────────
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = _theme.ToolbarBg };

        var btnExport = new Button
        {
            Text = "↓ Export CSV",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBg,
            ForeColor = _theme.ButtonFg,
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(12, 7),
            Height = 28,
            Cursor = Cursors.Hand
        };
        btnExport.FlatAppearance.BorderColor = _theme.ButtonBorder;
        btnExport.Click += OnExportComparison;

        var btnClose = new Button
        {
            Text = "Close",
            Width = 80, Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBg,
            ForeColor = _theme.ButtonFg,
            Font = new Font("Segoe UI", 8.5f),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        btnClose.FlatAppearance.BorderColor = _theme.ButtonBorder;
        btnClose.Location = new Point(footer.Width - 96, 7);
        footer.Resize += (_, _) => btnClose.Location = new Point(footer.Width - 96, 7);

        footer.Controls.AddRange([btnExport, btnClose]);
        AcceptButton = btnClose;

        Controls.Add(_resultList);
        Controls.Add(_summaryLabel);
        Controls.Add(topPanel);
        Controls.Add(footer);
    }

    private void RunComparison()
    {
        var from = _dtpFrom.Value;
        var to = _dtpTo.Value;

        var filtered = _allEvents
            .Where(e => e.DetectedAt >= from && e.DetectedAt <= to)
            .OrderByDescending(e => e.DetectedAt)
            .ToList();

        _resultList.BeginUpdate();
        _resultList.Items.Clear();

        foreach (var ev in filtered)
        {
            var item = new ListViewItem(ev.DetectedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(ev.App.Name);
            item.SubItems.Add(ev.App.Version);
            item.SubItems.Add(ev.ChangeType.ToString());
            item.SubItems.Add(ev.App.Publisher);
            item.SubItems.Add(ev.PreviousVersion ?? "");
            item.SubItems.Add(ev.App.InstalledBy);
            item.SubItems.Add(ev.App.InstallSource);
            item.SubItems.Add(ev.App.InstallType);
            item.SubItems.Add(GetInstallSize(ev.App.InstallLocation));
            item.SubItems.Add(ev.Source.ToString());
            item.Tag = ev;
            _resultList.Items.Add(item);
        }

        _resultList.EndUpdate();

        var installs = filtered.Count(e => e.ChangeType == ChangeType.Installed);
        var updates = filtered.Count(e => e.ChangeType == ChangeType.Updated);
        var removals = filtered.Count(e => e.ChangeType == ChangeType.Removed);

        var parts = new List<string>();
        if (installs > 0) parts.Add($"{installs} installed");
        if (updates > 0) parts.Add($"{updates} updated");
        if (removals > 0) parts.Add($"{removals} removed");

        _summaryLabel.Text = filtered.Count == 0
            ? $"  No changes found between {from:yyyy-MM-dd HH:mm} and {to:yyyy-MM-dd HH:mm}."
            : $"  {filtered.Count} changes ({string.Join(", ", parts)}) between {from:yyyy-MM-dd HH:mm} and {to:yyyy-MM-dd HH:mm}";
        _summaryLabel.ForeColor = filtered.Count == 0 ? _theme.MutedFg : _theme.FormFg;
    }

    private void OnExportComparison(object? sender, EventArgs e)
    {
        if (_resultList.Items.Count == 0)
        {
            MessageBox.Show("No data to export. Run a comparison first.", "Export",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Export Comparison",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"AppSentry_Compare_{_dtpFrom.Value:yyyyMMdd}_{_dtpTo.Value:yyyyMMdd}.csv"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var w = new StreamWriter(dlg.FileName);
            w.WriteLine("DetectedAt,AppName,Version,Change,Publisher,PrevVersion,InstalledBy,InstallSource,InstallType,Size,Source");
            foreach (ListViewItem item in _resultList.Items)
            {
                var fields = new List<string>();
                for (int i = 0; i < item.SubItems.Count; i++)
                    fields.Add(CsvEscape(item.SubItems[i].Text));
                w.WriteLine(string.Join(",", fields));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string CsvEscape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static string GetInstallSize(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return "";
        try
        {
            var dir = new DirectoryInfo(installLocation);
            if (!dir.Exists) return "";
            long totalBytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            if (totalBytes < 1024) return $"{totalBytes} B";
            if (totalBytes < 1024 * 1024) return $"{totalBytes / 1024.0:F1} KB";
            if (totalBytes < 1024 * 1024 * 1024) return $"{totalBytes / (1024.0 * 1024):F1} MB";
            return $"{totalBytes / (1024.0 * 1024 * 1024):F2} GB";
        }
        catch { return ""; }
    }
}
