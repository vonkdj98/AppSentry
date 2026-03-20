using AppSentry.Models;
using Microsoft.Win32;

namespace AppSentry;

/// <summary>
/// Shows a side-by-side diff of registry values between the old and new version of an updated app.
/// </summary>
internal class DiffViewForm : Form
{
    private readonly ThemeColors _theme;

    public DiffViewForm(ChangeEvent ev, ThemeColors theme)
    {
        _theme = theme;
        BuildForm(ev);
    }

    private void BuildForm(ChangeEvent ev)
    {
        Text = $"Diff — {ev.App.Name} ({ev.PreviousVersion ?? "?"} → {ev.App.Version})";
        Size = new Size(820, 560);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        MaximizeBox = true;

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = _theme.ToolbarBg };
        var headerLabel = new Label
        {
            Text = $"⟳  {ev.App.Name}  —  {ev.PreviousVersion ?? "unknown"} → {ev.App.Version}",
            Font = new Font("Segoe UI Semibold", 11f),
            ForeColor = _theme.UpdatedAccent,
            AutoSize = true,
            Location = new Point(16, 14),
            BackColor = Color.Transparent
        };
        header.Controls.Add(headerLabel);

        // Diff list
        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            OwnerDraw = true,
            BackColor = _theme.ListBg,
            ForeColor = _theme.ListFg,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f)
        };

        listView.Columns.AddRange([
            new ColumnHeader { Text = "Property", Width = 180 },
            new ColumnHeader { Text = "Old Value", Width = 280 },
            new ColumnHeader { Text = "New Value", Width = 280 },
            new ColumnHeader { Text = "Status", Width = 80 }
        ]);

        listView.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(_theme.HeaderBg);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var borderPen = new Pen(_theme.HeaderBorder);
            e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, listView.Columns[e.ColumnIndex].Text,
                new Font("Segoe UI", 8.5f, FontStyle.Bold), textBounds, _theme.HeaderFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };

        listView.DrawItem += (_, e) => e.DrawDefault = false;
        listView.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            var status = e.Item.SubItems.Count > 3 ? e.Item.SubItems[3].Text : "";
            var rowBg = status switch
            {
                "Changed" => _theme.UpdatedBg,
                "Added" => _theme.InstalledBg,
                "Removed" => _theme.RemovedBg,
                _ => (e.ItemIndex % 2 == 0) ? _theme.ListBg : _theme.AltRowBg
            };
            if (e.Item.Selected) rowBg = _theme.SelectedBg;

            using (var bg = new SolidBrush(rowBg))
                e.Graphics.FillRectangle(bg, e.Bounds);

            var fg = e.Item.Selected ? _theme.SelectedFg : _theme.ListFg;
            if (e.ColumnIndex == 3 && !e.Item.Selected)
            {
                fg = status switch
                {
                    "Changed" => _theme.UpdatedAccent,
                    "Added" => _theme.InstalledAccent,
                    "Removed" => _theme.RemovedAccent,
                    _ => fg
                };
            }

            var bounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, listView.Font, bounds, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        // Populate diff
        var diffs = ComputeDiff(ev);
        foreach (var d in diffs)
        {
            var item = new ListViewItem(d.Property);
            item.SubItems.Add(d.OldValue);
            item.SubItems.Add(d.NewValue);
            item.SubItems.Add(d.Status);
            listView.Items.Add(item);
        }

        if (diffs.Count == 0)
        {
            var item = new ListViewItem("(no registry differences detected)");
            item.SubItems.Add(""); item.SubItems.Add(""); item.SubItems.Add("");
            listView.Items.Add(item);
        }

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = _theme.ToolbarBg };
        var btnCopy = new Button
        {
            Text = "📋 Copy Diff",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBg,
            ForeColor = _theme.ButtonFg,
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(12, 8),
            Height = 28,
            Cursor = Cursors.Hand
        };
        btnCopy.FlatAppearance.BorderColor = _theme.ButtonBorder;
        btnCopy.Click += (_, _) =>
        {
            var text = $"Diff: {ev.App.Name} ({ev.PreviousVersion} → {ev.App.Version})\n";
            text += new string('─', 60) + "\n";
            foreach (var d in diffs)
                text += $"{d.Property,-25} {d.OldValue,-30} → {d.NewValue,-30} [{d.Status}]\n";
            Clipboard.SetText(text);
        };

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
        btnClose.Location = new Point(footer.Width - btnClose.Width - 16, 8);
        // Re-position on resize
        footer.Resize += (_, _) => btnClose.Location = new Point(footer.Width - btnClose.Width - 16, 8);

        footer.Controls.AddRange([btnCopy, btnClose]);
        AcceptButton = btnClose;

        Controls.Add(listView);
        Controls.Add(header);
        Controls.Add(footer);
    }

    private record DiffEntry(string Property, string OldValue, string NewValue, string Status);

    private static List<DiffEntry> ComputeDiff(ChangeEvent ev)
    {
        var diffs = new List<DiffEntry>();
        var app = ev.App;

        // Try to read current registry values and compare with stored app data
        var currentValues = ReadRegistryValues(app.KeyPath);
        var storedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = app.Name,
            ["DisplayVersion"] = app.Version,
            ["Publisher"] = app.Publisher,
            ["InstallDate"] = app.InstallDate,
            ["InstallLocation"] = app.InstallLocation,
            ["InstallSource"] = app.InstallSource
        };

        // If we can read current registry, compare each property
        if (currentValues.Count > 0)
        {
            var allKeys = new HashSet<string>(storedValues.Keys.Concat(currentValues.Keys),
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in allKeys.OrderBy(k => k))
            {
                storedValues.TryGetValue(key, out var oldVal);
                currentValues.TryGetValue(key, out var newVal);
                oldVal ??= "";
                newVal ??= "";

                if (oldVal == newVal && string.IsNullOrEmpty(oldVal)) continue;

                string status;
                if (string.IsNullOrEmpty(oldVal) && !string.IsNullOrEmpty(newVal))
                    status = "Added";
                else if (!string.IsNullOrEmpty(oldVal) && string.IsNullOrEmpty(newVal))
                    status = "Removed";
                else if (!oldVal.Equals(newVal, StringComparison.Ordinal))
                    status = "Changed";
                else
                    status = "Same";

                diffs.Add(new DiffEntry(key, oldVal, newVal, status));
            }
        }
        else
        {
            // Can't read registry — show what we know from the event
            if (!string.IsNullOrEmpty(ev.PreviousVersion))
                diffs.Add(new DiffEntry("DisplayVersion", ev.PreviousVersion, app.Version, "Changed"));
            diffs.Add(new DiffEntry("DisplayName", app.Name, app.Name, "Same"));
            diffs.Add(new DiffEntry("Publisher", app.Publisher, app.Publisher, "Same"));
            if (!string.IsNullOrEmpty(app.InstallDate))
                diffs.Add(new DiffEntry("InstallDate", "", app.InstallDate, "Changed"));
        }

        return diffs;
    }

    private static Dictionary<string, string> ReadRegistryValues(string keyPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            RegistryKey? hive = null;
            string subPath;

            if (keyPath.StartsWith("HKLM\\")) { hive = Registry.LocalMachine; subPath = keyPath[5..]; }
            else if (keyPath.StartsWith("HKCU\\")) { hive = Registry.CurrentUser; subPath = keyPath[5..]; }
            else if (keyPath.StartsWith("HKU\\")) { hive = Registry.Users; subPath = keyPath[4..]; }
            else return values;

            using var key = hive.OpenSubKey(subPath, false);
            if (key == null) return values;

            foreach (var name in key.GetValueNames())
            {
                var val = key.GetValue(name);
                if (val != null)
                    values[name] = val.ToString() ?? "";
            }
        }
        catch { }
        return values;
    }
}
