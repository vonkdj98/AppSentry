using System.Media;
using System.Runtime.InteropServices;
using AppSentry.Core.Backend;
using AppSentry.Core.Service;
using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Win32;

namespace AppSentry;

public partial class MainForm : Form
{
    // ── Engine connection ─────────────────────────────────────────────────
    // All detection, scheduling and storage lives in AppSentry.Core behind this backend
    // (in-process engine or the Windows service). The form only displays and forwards.
    private readonly IMonitorBackend _backend;
    private ExclusionStore _exclusionStore = null!;
    private List<ChangeEvent> _allEvents = [];
    private readonly HashSet<long> _knownEventIds = [];
    private bool _applyingSettings;

    // ── Theme ────────────────────────────────────────────────────────────────
    private enum ThemeMode { System, Dark, Light }
    private ThemeMode _themeMode = ThemeMode.System;
    private bool _isDarkMode;
    private ThemeColors _theme = null!;

    // ── Controls ─────────────────────────────────────────────────────────────
    private ToolTip _sharedToolTip = new();
    private bool _isClosing;
    private Panel _toolbarPanel = null!;
    private Button _btnScanNow = null!;
    private Button _btnClearHistory = null!;
    private Button _btnExportCsv = null!;
    private Label _lblInterval = null!;
    private ComboBox _cmbInterval = null!;
    private Label _lblSearch = null!;
    private TextBox _txtSearch = null!;
    private CheckBox _chkStartup = null!;
    private ComboBox _cmbTheme = null!;
    private Label _lblTheme = null!;
    private CheckBox _chkSound = null!;
    private Button _btnCompare = null!;
    private Button _btnInstalledApps = null!;
    private Button _btnExclusions = null!;
    private Button _btnService = null!;
    private ComboBox _cmbNotifyHide = null!;
    private Label _lblNotifyHide = null!;
    private ContextMenuStrip _listContextMenu = null!;
    private ListView _listView = null!;
    private Panel _statusPanel = null!;
    private Label _statusLabel = null!;
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;

    // ── Sorting ──────────────────────────────────────────────────────────────
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    public MainForm(IMonitorBackend backend)
    {
        _backend = backend;
        DetectTheme();
        LoadSoundPref();
        LoadNotifyHidePref();
        InitializeComponent();
        SetupTrayIcon();
        BuildListContextMenu();

        // Subscribe before starting so the startup scan's events aren't missed.
        _backend.EventsDetected += (_, events) => RunOnUi(() => OnEventsDetected(events));
        _backend.StatusChanged += (_, status) => RunOnUi(() => OnStatusChanged(status));
        Load += async (_, _) => await ConnectBackendAsync();
    }

    private async Task ConnectBackendAsync()
    {
        SetStatus($"Starting monitor ({_backend.Mode})…");
        try
        {
            await _backend.StartAsync();
            _exclusionStore = await ExclusionStore.LoadAsync(_backend);
            ApplySettingsToControls(await _backend.GetSettingsAsync());
            ApplyPermissions();
            await LoadHistoryAsync();
            OnStatusChanged(_backend.Status);
        }
        catch (Exception ex)
        {
            SetStatus($"Monitor failed to start: {ex.Message}");
            MessageBox.Show($"AppSentry could not start its monitor:\n\n{ex.Message}", "AppSentry",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunOnUi(Action action)
    {
        if (_isClosing || IsDisposed) return;
        try
        {
            if (IsHandleCreated) BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static readonly int[] IntervalMinutes = [1, 5, 10, 30, 0];

    private void ApplySettingsToControls(EngineSettings settings)
    {
        _applyingSettings = true;
        var idx = Array.IndexOf(IntervalMinutes, settings.ScanIntervalMinutes);
        _intervalIndex = idx >= 0 ? idx : 1;
        _cmbInterval.SelectedIndex = _intervalIndex;
        _applyingSettings = false;
    }

    // ── Theme Detection ──────────────────────────────────────────────────────

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppSentry");
    private static readonly string ThemePrefPath = Path.Combine(AppDataDir, "theme.txt");
    private static readonly string SoundPrefPath = Path.Combine(AppDataDir, "sound.txt");
    private static readonly string ColumnLayoutPath = Path.Combine(AppDataDir, "columns.txt");
    private static readonly string NotifyHidePath = Path.Combine(AppDataDir, "notifyhide.txt");
    private bool _soundEnabled;
    private int _notifyAutoHideSeconds; // 0 = stay forever
    private int _intervalIndex = 1; // default: 5 min (index into combo); the engine owns the real setting

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(string lpszName, nint hmod, uint fdwSound);
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    private void DetectTheme()
    {
        // Load saved preference
        try
        {
            if (File.Exists(ThemePrefPath))
            {
                var saved = File.ReadAllText(ThemePrefPath).Trim();
                if (Enum.TryParse<ThemeMode>(saved, true, out var mode))
                    _themeMode = mode;
            }
        }
        catch { }

        // Resolve actual dark/light
        _isDarkMode = _themeMode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => DetectSystemTheme()
        };

        _theme = _isDarkMode ? ThemeColors.Dark() : ThemeColors.Light();
    }

    private static bool DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }

    private void SaveThemePref()
    {
        try { File.WriteAllText(ThemePrefPath, _themeMode.ToString()); }
        catch { }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _themeMode = (ThemeMode)_cmbTheme.SelectedIndex;
        SaveThemePref();

        _isDarkMode = _themeMode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => DetectSystemTheme()
        };
        _theme = _isDarkMode ? ThemeColors.Dark() : ThemeColors.Light();
        ApplyThemeToControls();
    }

    private void ApplyThemeToControls()
    {
        SuspendLayout();

        // Form
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;

        // Toolbar
        _toolbarPanel.BackColor = _theme.ToolbarBg;
        foreach (Control c in _toolbarPanel.Controls)
            ApplyThemeRecursive(c);

        // Buttons
        foreach (var btn in new[] { _btnScanNow, _btnClearHistory, _btnExportCsv, _btnCompare, _btnInstalledApps, _btnExclusions, _btnService })
        {
            btn.BackColor = _theme.ButtonBg;
            btn.ForeColor = _theme.ButtonFg;
            btn.FlatAppearance.BorderColor = _theme.ButtonBorder;
            btn.FlatAppearance.MouseOverBackColor = _theme.ButtonHover;
        }

        // Labels
        _lblInterval.ForeColor = _theme.MutedFg;
        _lblSearch.ForeColor = _theme.MutedFg;
        _lblTheme.ForeColor = _theme.MutedFg;
        _chkStartup.ForeColor = _theme.MutedFg;
        _chkSound.ForeColor = _theme.MutedFg;
        _lblNotifyHide.ForeColor = _theme.MutedFg;

        // Inputs
        _cmbInterval.BackColor = _theme.InputBg;
        _cmbInterval.ForeColor = _theme.InputFg;
        _cmbTheme.BackColor = _theme.InputBg;
        _cmbTheme.ForeColor = _theme.InputFg;
        _cmbNotifyHide.BackColor = _theme.InputBg;
        _cmbNotifyHide.ForeColor = _theme.InputFg;
        _txtSearch.BackColor = _theme.InputBg;
        _txtSearch.ForeColor = _theme.InputFg;

        // ListView
        _listView.BackColor = _theme.ListBg;
        _listView.ForeColor = _theme.ListFg;
        _listView.Invalidate();

        // Status bar
        _statusPanel.BackColor = _theme.StatusBg;
        _statusLabel.ForeColor = _theme.StatusFg;

        // Tray menu
        if (_trayMenu != null)
        {
            _trayMenu.BackColor = _theme.FormBg;
            _trayMenu.ForeColor = _theme.FormFg;
        }

        ResumeLayout(true);
        Refresh();
    }

    private void LoadSoundPref()
    {
        try { _soundEnabled = File.Exists(SoundPrefPath) && File.ReadAllText(SoundPrefPath).Trim() == "1"; }
        catch { }
    }

    private void SaveSoundPref()
    {
        try { File.WriteAllText(SoundPrefPath, _soundEnabled ? "1" : "0"); }
        catch { }
    }

    private void PlayAlertSound()
    {
        if (!_soundEnabled) return;
        try { PlaySound("SystemNotification", 0, SND_ASYNC | SND_ALIAS); }
        catch { SystemSounds.Asterisk.Play(); }
    }

    private void LoadNotifyHidePref()
    {
        try
        {
            if (File.Exists(NotifyHidePath) && int.TryParse(File.ReadAllText(NotifyHidePath).Trim(), out var s))
                _notifyAutoHideSeconds = s;
        }
        catch { }
    }

    private void SaveNotifyHidePref()
    {
        try { File.WriteAllText(NotifyHidePath, _notifyAutoHideSeconds.ToString()); }
        catch { }
    }

    // ── Column Layout Persistence ─────────────────────────────────────────────

    private void SaveColumnLayout()
    {
        try
        {
            var lines = new List<string>();
            var order = _listView.Columns.Cast<ColumnHeader>()
                .OrderBy(c => c.DisplayIndex)
                .Select(c => $"{c.DisplayIndex},{c.Width}");
            File.WriteAllLines(ColumnLayoutPath, order);
        }
        catch { }
    }

    private void LoadColumnLayout()
    {
        try
        {
            if (!File.Exists(ColumnLayoutPath)) return;
            var lines = File.ReadAllLines(ColumnLayoutPath);
            if (lines.Length != _listView.Columns.Count) return; // column count changed, skip

            for (int i = 0; i < lines.Length && i < _listView.Columns.Count; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var displayIdx) &&
                    int.TryParse(parts[1], out var width))
                {
                    _listView.Columns[i].DisplayIndex = Math.Clamp(displayIdx, 0, _listView.Columns.Count - 1);
                    _listView.Columns[i].Width = Math.Max(30, width);
                }
            }
        }
        catch { }
    }

    private void ApplyThemeRecursive(Control control)
    {
        if (control is FlowLayoutPanel flow)
        {
            flow.BackColor = Color.Transparent;
            foreach (Control child in flow.Controls)
                ApplyThemeRecursive(child);
        }
    }

    // ── Build UI ─────────────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        Text = "AppSentry — Install & Update Monitor";
        Size = new Size(1400, 700);
        MinimumSize = new Size(950, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;
        DoubleBuffered = true;
        Icon = AppIcon.Create();

        BuildToolbar();
        BuildListView();
        BuildStatusBar();

        // Add in correct z-order
        Controls.Add(_listView);
        Controls.Add(_toolbarPanel);
        Controls.Add(_statusPanel);
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

        _btnScanNow = MakeToolButton("⟳ Scan Now", "Scan registry for changes now");
        _btnScanNow.Click += (_, _) => RequestScan();

        _btnClearHistory = MakeToolButton("✕ Clear", "Remove all recorded events");
        _btnClearHistory.Click += OnClearHistory;

        _btnExportCsv = MakeToolButton("↓ Export CSV", "Save history to CSV");
        _btnExportCsv.Click += OnExportCsv;

        _lblInterval = new Label
        {
            Text = "Interval:",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f),
            Padding = new Padding(6, 4, 0, 0)
        };

        _cmbInterval = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70,
            BackColor = _theme.InputBg,
            ForeColor = _theme.InputFg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f)
        };
        _cmbInterval.Items.AddRange(["1 min", "5 min", "10 min", "30 min", "Off"]);
        _cmbInterval.SelectedIndex = _intervalIndex;
        _cmbInterval.SelectedIndexChanged += OnIntervalChanged;

        _lblSearch = new Label
        {
            Text = "🔍",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Padding = new Padding(10, 4, 0, 0)
        };

        _txtSearch = new TextBox
        {
            Width = 180,
            PlaceholderText = "Filter apps...",
            BackColor = _theme.InputBg,
            ForeColor = _theme.InputFg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };
        _txtSearch.TextChanged += (_, _) => ApplyFilter();

        _chkStartup = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f),
            Checked = IsStartupEnabled(),
            Padding = new Padding(10, 2, 0, 0),
            FlatStyle = FlatStyle.Flat
        };
        _chkStartup.CheckedChanged += OnStartupToggled;

        _lblTheme = new Label
        {
            Text = "Theme:",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f),
            Padding = new Padding(10, 4, 0, 0)
        };

        _cmbTheme = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 80,
            BackColor = _theme.InputBg,
            ForeColor = _theme.InputFg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f)
        };
        _cmbTheme.Items.AddRange(["System", "Dark", "Light"]);
        _cmbTheme.SelectedIndex = (int)_themeMode;
        _cmbTheme.SelectedIndexChanged += OnThemeChanged;

        _chkSound = new CheckBox
        {
            Text = "🔔 Sound",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f),
            Checked = _soundEnabled,
            Padding = new Padding(10, 2, 0, 0),
            FlatStyle = FlatStyle.Flat
        };
        _chkSound.CheckedChanged += (_, _) =>
        {
            _soundEnabled = _chkSound.Checked;
            SaveSoundPref();
            if (_soundEnabled) PlayAlertSound(); // preview the sound
        };

        _btnCompare = MakeToolButton("📊 Compare", "Compare snapshots between dates");
        _btnCompare.Click += (_, _) =>
        {
            using var frm = new SnapshotCompareForm(_allEvents, _theme);
            frm.ShowDialog(this);
        };

        _btnInstalledApps = MakeToolButton("📦 Installed Apps", "Browse all currently installed programs");
        _btnInstalledApps.Click += OnInstalledAppsClick;

        _btnService = _backend.Mode == "Service"
            ? MakeToolButton("🛡 Service", "Connected to the AppSentry background service")
            : MakeToolButton("🛡 Install Service", "Run AppSentry as a background service for every user");
        _btnService.Click += OnServiceClick;

        _btnExclusions = MakeToolButton("🚫 Exclusions", "Manage apps excluded from notifications or logging");
        _btnExclusions.Click += (_, _) =>
        {
            using var frm = new ExclusionsForm(_exclusionStore, _theme, _isDarkMode);
            frm.ShowDialog(this);
        };

        _lblNotifyHide = new Label
        {
            Text = "Auto-hide notifications:",
            AutoSize = true,
            ForeColor = _theme.MutedFg,
            Font = new Font("Segoe UI", 8.5f),
            Padding = new Padding(6, 4, 0, 0)
        };

        _cmbNotifyHide = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 75,
            BackColor = _theme.InputBg,
            ForeColor = _theme.InputFg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f)
        };
        _cmbNotifyHide.Items.AddRange(["Never", "10 sec", "30 sec", "60 sec"]);
        _cmbNotifyHide.SelectedIndex = _notifyAutoHideSeconds switch
        {
            10 => 1, 30 => 2, 60 => 3, _ => 0
        };
        _cmbNotifyHide.SelectedIndexChanged += (_, _) =>
        {
            _notifyAutoHideSeconds = _cmbNotifyHide.SelectedIndex switch
            {
                1 => 10, 2 => 30, 3 => 60, _ => 0
            };
            SaveNotifyHidePref();
        };

        // Layout using FlowLayoutPanel
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
            _btnScanNow, _btnClearHistory, _btnExportCsv, _btnCompare, _btnInstalledApps, _btnExclusions, _btnService,
            MakeSpacer(12),
            _lblInterval, _cmbInterval,
            _lblSearch, _txtSearch,
            _chkStartup,
            _lblTheme, _cmbTheme,
            _chkSound,
            _lblNotifyHide, _cmbNotifyHide
        ]);

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
            AllowColumnReorder = true,
            BackColor = _theme.ListBg,
            ForeColor = _theme.ListFg,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9f)
        };

        _listView.Columns.AddRange([
            new ColumnHeader { Text = "Detected At",     Width = 150 },
            new ColumnHeader { Text = "App Name",        Width = 200 },
            new ColumnHeader { Text = "Version",         Width = 90  },
            new ColumnHeader { Text = "Change",          Width = 75  },
            new ColumnHeader { Text = "Publisher",       Width = 150 },
            new ColumnHeader { Text = "Prev Version",    Width = 85  },
            new ColumnHeader { Text = "Installed By",    Width = 105 },
            new ColumnHeader { Text = "Install Source",  Width = 160 },
            new ColumnHeader { Text = "Install Type",    Width = 85  },
            new ColumnHeader { Text = "Size",            Width = 80  },
            new ColumnHeader { Text = "Pkg Manager",     Width = 120 }
        ]);

        _listView.DrawColumnHeader += OnDrawColumnHeader;
        _listView.DrawItem += OnDrawItem;
        _listView.DrawSubItem += OnDrawSubItem;
        _listView.ColumnClick += OnColumnClick;
        _listView.DoubleClick += OnListDoubleClick;
        _listView.Resize += (_, _) => AutoFillLastColumn();
        _listView.ColumnWidthChanged += (_, _) => SaveColumnLayout();
        _listView.ColumnReordered += (_, _) => BeginInvoke(SaveColumnLayout);

        LoadColumnLayout();
    }

    private void AutoFillLastColumn()
    {
        if (_listView.Columns.Count == 0) return;
        int totalWidth = 0;
        for (int i = 0; i < _listView.Columns.Count - 1; i++)
            totalWidth += _listView.Columns[i].Width;

        int remaining = _listView.ClientSize.Width - totalWidth;
        var lastCol = _listView.Columns[_listView.Columns.Count - 1];
        if (remaining > 80)
            lastCol.Width = remaining;
    }

    private void BuildListContextMenu()
    {
        _listContextMenu = new ContextMenuStrip();
        if (_isDarkMode)
        {
            _listContextMenu.BackColor = _theme.ToolbarBg;
            _listContextMenu.ForeColor = _theme.FormFg;
            _listContextMenu.Renderer = new DarkToolStripRenderer();
        }

        var menuUninstall = new ToolStripMenuItem("🗑 Uninstall…");
        menuUninstall.Click += OnContextUninstall;

        var menuOpenLocation = new ToolStripMenuItem("📂 Open Install Location");
        menuOpenLocation.Click += OnContextOpenLocation;

        var menuCopyDetails = new ToolStripMenuItem("📋 Copy Details to Clipboard");
        menuCopyDetails.Click += OnContextCopyDetails;

        var menuCopyName = new ToolStripMenuItem("📋 Copy App Name");
        menuCopyName.Click += OnContextCopyName;

        var menuViewDetails = new ToolStripMenuItem("🔍 View Details…");
        menuViewDetails.Click += (_, _) => OnListDoubleClick(null, EventArgs.Empty);

        var menuViewDiff = new ToolStripMenuItem("⟳ View Diff…");
        menuViewDiff.Click += (_, _) =>
        {
            var ev = GetSelectedEvent();
            if (ev == null) return;
            using var diffForm = new DiffViewForm(ev, _theme);
            diffForm.ShowDialog(this);
        };

        var menuExclude = new ToolStripMenuItem("🚫 Exclude this app…");
        menuExclude.Click += OnContextExclude;

        _listContextMenu.Items.AddRange([menuViewDetails, menuViewDiff, new ToolStripSeparator(),
            menuUninstall, menuOpenLocation, new ToolStripSeparator(),
            menuCopyName, menuCopyDetails,
            new ToolStripSeparator(), menuExclude]);

        _listView.ContextMenuStrip = _listContextMenu;

        // Enable/disable items based on selection
        _listContextMenu.Opening += (_, e) =>
        {
            var hasSelection = _listView.SelectedItems.Count > 0;
            var ev = hasSelection ? _listView.SelectedItems[0].Tag as ChangeEvent : null;

            foreach (ToolStripItem item in _listContextMenu.Items)
                item.Enabled = hasSelection;

            // Disable "Open Location" if no install path
            menuOpenLocation.Enabled = hasSelection && ev != null &&
                !string.IsNullOrWhiteSpace(ev.App.InstallLocation) &&
                Directory.Exists(ev.App.InstallLocation);

            // Disable "Uninstall" for removed apps
            menuUninstall.Enabled = hasSelection && ev != null &&
                ev.ChangeType != ChangeType.Removed;

            // Diff for updates and modifications (both carry the previous values)
            menuViewDiff.Enabled = hasSelection && ev != null &&
                ev.ChangeType is ChangeType.Updated or ChangeType.Modified;

            if (!hasSelection) e.Cancel = true;
        };
    }

    private ChangeEvent? GetSelectedEvent()
    {
        if (_listView.SelectedItems.Count == 0) return null;
        return _listView.SelectedItems[0].Tag as ChangeEvent;
    }

    private void OnContextUninstall(object? sender, EventArgs e)
    {
        var app = GetSelectedEvent()?.App;
        if (app != null) UninstallAction.Run(this, app, SetStatus);
    }


    private void OnContextOpenLocation(object? sender, EventArgs e)
    {
        var ev = GetSelectedEvent();
        if (ev == null || string.IsNullOrWhiteSpace(ev.App.InstallLocation)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ev.App.InstallLocation}\""
            });
        }
        catch { }
    }

    private void OnContextCopyName(object? sender, EventArgs e)
    {
        var ev = GetSelectedEvent();
        if (ev == null) return;
        Clipboard.SetText(ev.App.Name);
        SetStatus($"Copied: {ev.App.Name}");
    }

    private void OnContextCopyDetails(object? sender, EventArgs e)
    {
        var ev = GetSelectedEvent();
        if (ev == null) return;

        var details = $"""
            App Name:        {ev.App.Name}
            Version:         {ev.App.Version}
            Publisher:        {ev.App.Publisher}
            Change:          {ev.ChangeType}
            Detected At:     {ev.DetectedAt:yyyy-MM-dd HH:mm:ss}
            Previous Version: {ev.PreviousVersion ?? "—"}
            Installed By:    {ev.App.InstalledBy}
            Install Source:  {ev.App.InstallSource}
            Install Type:    {ev.App.InstallType}
            Install Location: {ev.App.InstallLocation}
            Detection Source: {ev.Source}
            Registry Key:    {ev.App.KeyPath}
            """;
        Clipboard.SetText(details);
        SetStatus($"Copied details for {ev.App.Name} to clipboard.");
    }

    private void OnContextExclude(object? sender, EventArgs e)
    {
        var ev = GetSelectedEvent();
        if (ev == null) return;

        var existing = _exclusionStore.GetEntry(ev.App.Name);
        var prefill = existing ?? new Models.ExclusionEntry(ev.App.Name, true, false);

        if (ExclusionsForm.ShowExclusionDialogStatic(prefill, _theme, this, out var result))
        {
            _exclusionStore.Add(result);
            SetStatus($"✓ Exclusion saved for \"{result.AppName}\"");
        }
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
            Text = "Ready",
            Dock = DockStyle.Fill,
            ForeColor = _theme.StatusFg,
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _statusPanel.Controls.Add(_statusLabel);
    }

    // ── Owner-draw ListView ──────────────────────────────────────────────────

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(_theme.HeaderBg);
        e.Graphics.FillRectangle(bg, e.Bounds);

        // Bottom border line
        using var borderPen = new Pen(_theme.HeaderBorder);
        e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1,
            e.Bounds.Right, e.Bounds.Bottom - 1);

        // Right separator
        if (e.ColumnIndex < _listView.Columns.Count - 1)
        {
            using var sepPen = new Pen(_theme.HeaderBorder);
            e.Graphics.DrawLine(sepPen, e.Bounds.Right - 1, e.Bounds.Top + 4,
                e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        }

        // Sort indicator
        var text = _listView.Columns[e.ColumnIndex].Text;
        if (e.ColumnIndex == _sortColumn)
            text += _sortAscending ? " ▲" : " ▼";

        var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, new Font("Segoe UI", 8.5f, FontStyle.Bold),
            textBounds, _theme.HeaderFg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis);
    }

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        // Let DrawSubItem handle everything
        e.DrawDefault = false;
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null) return;

        var ev = e.Item.Tag as ChangeEvent;
        var bounds = e.Bounds;

        // Determine row background
        Color rowBg;
        if (e.Item.Selected)
        {
            rowBg = _theme.SelectedBg;
        }
        else if (ev != null)
        {
            rowBg = EventDisplay.RowBackground(ev.ChangeType, _theme);
        }
        else
        {
            rowBg = (e.ItemIndex % 2 == 0) ? _theme.ListBg : _theme.AltRowBg;
        }

        // Fill background
        using (var bg = new SolidBrush(rowBg))
            e.Graphics.FillRectangle(bg, bounds);

        // Subtle bottom border
        using (var pen = new Pen(_theme.RowBorder))
            e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

        // Text color
        var textColor = e.Item.Selected ? _theme.SelectedFg : _theme.ListFg;

        // Special color for Change column
        if (e.ColumnIndex == 3 && ev != null && !e.Item.Selected)
        {
            textColor = EventDisplay.Accent(ev.ChangeType, _theme);
        }

        // Draw text
        var textBounds = new Rectangle(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _listView.Font,
            textBounds, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // ── System Tray ──────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        _trayMenu = new ContextMenuStrip();
        if (_isDarkMode)
        {
            _trayMenu.BackColor = _theme.ToolbarBg;
            _trayMenu.ForeColor = _theme.FormFg;
            _trayMenu.Renderer = new DarkToolStripRenderer();
        }
        _trayMenu.Items.Add("Show AppSentry", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add("Scan Now", null, (_, _) => { RestoreFromTray(); RequestScan(); });
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Exit", null, (_, _) => { _trayIcon.Visible = false; Application.Exit(); });

        _trayIcon = new NotifyIcon
        {
            Text = "AppSentry — Monitoring",
            Icon = AppIcon.CreateSmall(),
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(1500, "AppSentry",
                "Minimized to tray. Still monitoring for changes.", ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isClosing = true;
        _sharedToolTip.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Disposing the backend lets an in-flight scan finish its transaction first.
        _backend.Dispose();
        base.OnFormClosed(e);
    }

    // ── Scanning (delegated to the engine) ───────────────────────────────────

    private async void RequestScan()
    {
        try
        {
            await _backend.RequestScanAsync();
            SetStatus("Scan queued…");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start a scan: {ex.Message}");
        }
    }

    /// <summary>Standard users connected to the service can look but not change history, exclusions or settings.</summary>
    private void ApplyPermissions()
    {
        if (_backend.CanModify) return;
        const string why = "Requires an administrator account (the service is shared by every user)";
        foreach (Control c in new Control[] { _btnClearHistory, _btnExclusions, _cmbInterval })
        {
            c.Enabled = false;
            _sharedToolTip.SetToolTip(c, why);
        }
    }

    // ── Background service ───────────────────────────────────────────────────

    private async void OnServiceClick(object? sender, EventArgs e)
    {
        if (_backend.Mode == "Service")
        {
            var role = _backend.CanModify ? "an administrator" : "a standard user (read-only)";
            if (MessageBox.Show(
                    $"Connected to the AppSentry service as {role}.\n\n" +
                    $"History is kept in {ServiceDataDir.Path}, and every change is also written to the " +
                    "Windows Application log (source \"AppSentry\", event IDs 1000–1004).\n\n" +
                    "Uninstall the service? (History is kept.)",
                    "AppSentry service", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;
            if (await Task.Run(() => ServiceInstaller.RunElevated("--uninstall-service")) == 0)
                RestartApp();
            return;
        }

        var prompt = ServiceInstaller.GetStatus() == null
            ? "Install the AppSentry background service?\n\n" +
              "• Monitors every user on this PC and keeps running when this window is closed or nobody is signed in\n" +
              "• Writes every change to the Windows event log (for Wazuh)\n" +
              "• Copies AppSentry to Program Files — Windows will ask for administrator approval"
            : "The AppSentry service is installed, but this window couldn't reach it (it may be stopped).\n\nReinstall and start it?";
        if (MessageBox.Show(prompt, "AppSentry service", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _btnService.Enabled = false;
        SetStatus("Installing the AppSentry service…");
        var code = await Task.Run(() => ServiceInstaller.RunElevated("--install-service"));
        _btnService.Enabled = true;

        if (code == 1223) { SetStatus("Service install cancelled."); return; }
        if (code != 0) { SetStatus($"Service install failed (exit code {code}); see the message for details."); return; }
        if (MessageBox.Show("The service is running. Restart AppSentry now to switch this window over to it?",
                "AppSentry service", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            RestartApp();
    }

    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--after-exit {Environment.ProcessId}") { UseShellExecute = true });
        _trayIcon.Visible = false;
        Application.Exit();
    }

    private void OnEventsDetected(IReadOnlyList<ChangeEvent> events)
    {
        var added = InsertEvents(events);
        if (added.Count == 0) return;
        ApplyFilter();
        ShowNotifications(added.Where(e => !e.Silent).ToList());
        OnStatusChanged(_backend.Status);
    }

    /// <summary>Adds events not already shown (a scan can race the initial history load).</summary>
    private List<ChangeEvent> InsertEvents(IEnumerable<ChangeEvent> events)
    {
        var added = events.Where(e => e.Id == 0 || _knownEventIds.Add(e.Id)).ToList();
        if (added.Count > 0)
            _allEvents.InsertRange(0, added.OrderByDescending(e => e.DetectedAt));
        return added;
    }

    private void OnStatusChanged(EngineStatus status) => SetStatus(FormatStatus(status));

    private string FormatStatus(EngineStatus s)
    {
        if (s.IsScanning)
            return $"Scanning registry, Store, event log, services and folders…  ·  {_backend.Mode} mode";

        var parts = new List<string>
        {
            s.LastScanUtc is { } last ? $"Last scanned: {last.ToLocalTime():HH:mm:ss}" : "Waiting for first scan",
            $"{s.TrackedApps} apps tracked",
            $"{_allEvents.Count} change events",
            $"{_backend.Mode} mode"
        };
        var degraded = s.Sources.Where(kv => kv.Value != "ok").Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        if (degraded.Count > 0) parts.Add(string.Join(", ", degraded));
        if (!string.IsNullOrEmpty(s.LastError)) parts.Add("⚠ " + s.LastError);
        else if (!string.IsNullOrEmpty(s.Notice)) parts.Add(s.Notice);
        return string.Join("  ·  ", parts);
    }

    // ── Notifications ────────────────────────────────────────────────────────

    private NotificationForm? _activeNotification;

    private void ShowNotifications(List<ChangeEvent> events)
    {
        if (events.Count == 0) return;

        // Play alert sound if enabled
        PlayAlertSound();

        // Close any existing notification
        if (_activeNotification != null && !_activeNotification.IsDisposed)
        {
            _activeNotification.Close();
            _activeNotification = null;
        }

        // Show the popup notification
        _activeNotification = new NotificationForm(events, _theme, onViewDetails: ev =>
        {
            // Bring main window to front and select the event
            RestoreFromTray();
            for (int i = 0; i < _listView.Items.Count; i++)
            {
                if (_listView.Items[i].Tag is ChangeEvent ce && ce == ev)
                {
                    _listView.Items[i].Selected = true;
                    _listView.Items[i].EnsureVisible();
                    break;
                }
            }
        }, autoHideSeconds: _notifyAutoHideSeconds);
        _activeNotification.Show();
    }

    // ── Search / Filter ──────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var filter = _txtSearch?.Text?.Trim() ?? "";
        _listView.BeginUpdate();
        _listView.Items.Clear();

        var source = string.IsNullOrEmpty(filter)
            ? _allEvents
            : _allEvents.Where(ev =>
                ev.App.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                ev.App.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                ev.App.InstallType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                ev.App.InstalledBy.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                ev.App.Version.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                ev.ChangeType.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                ev.Source.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var ev in source)
            _listView.Items.Add(MakeItem(ev));

        _listView.EndUpdate();
    }

    // ── List management ──────────────────────────────────────────────────────

    private async Task LoadHistoryAsync()
    {
        var history = await _backend.GetHistoryAsync();
        var historyIds = history.Select(e => e.Id).ToHashSet();

        // Keep anything that arrived from a scan while the history was loading.
        var arrivedMeanwhile = _allEvents.Where(e => !historyIds.Contains(e.Id));
        _allEvents = arrivedMeanwhile.Concat(history).ToList();

        _knownEventIds.Clear();
        _knownEventIds.UnionWith(_allEvents.Select(e => e.Id));
        ApplyFilter();
    }

    private ListViewItem MakeItem(ChangeEvent ev)
    {
        // Every column comes from data captured at detection time — no disk or
        // package-manager lookups while drawing the list.
        var item = new ListViewItem(EventDisplay.LocalTime(ev.EffectiveTime));
        item.SubItems.Add(ev.App.Name);
        item.SubItems.Add(ev.App.Version);
        item.SubItems.Add(ev.ChangeType.ToString());
        item.SubItems.Add(ev.App.Publisher);
        item.SubItems.Add(ev.PreviousVersion ?? "");
        item.SubItems.Add(EventDisplay.Who(ev));
        item.SubItems.Add(ev.App.InstallSource);
        item.SubItems.Add(ev.App.InstallType);
        item.SubItems.Add(EventDisplay.Size(ev));
        item.SubItems.Add(ev.App.PackageManager);
        item.Tag = ev;
        return item;
    }

    // ── Column Sorting ───────────────────────────────────────────────────────

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
        _listView.Invalidate(); // redraw headers with sort indicator
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

    // ── Details Panel ────────────────────────────────────────────────────────

    private void OnListDoubleClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        if (_listView.SelectedItems[0].Tag is not ChangeEvent ev) return;

        var app = ev.App;
        ShowDetailDialog(ev, app);
    }

    private void ShowDetailDialog(ChangeEvent ev, InstalledApp app)
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
            ("App Name", app.Name),
            ("Version", app.Version),
            ("Publisher", app.Publisher),
            ("Change Type", ev.ChangeType.ToString()),
            ("Happened At", ev.OccurredAt is { } at ? EventDisplay.LocalTime(at) : "—"),
            ("Detected At", EventDisplay.LocalTime(ev.DetectedAt)),
            ("Previous Version", ev.PreviousVersion ?? "—"),
            ("Details", string.IsNullOrEmpty(ev.Details) ? "—" : ev.Details),
            ("", ""),  // spacer
            ("Changed By", string.IsNullOrEmpty(ev.ChangedBy) ? "—" : ev.ChangedBy),
            ("Installed For", string.IsNullOrEmpty(app.InstalledFor) ? "—" : app.InstalledFor),
            ("Install Source", string.IsNullOrEmpty(app.InstallSource) ? "—" : app.InstallSource),
            ("Install Type", app.InstallType),
            ("Install Date", string.IsNullOrEmpty(app.InstallDate) ? "—" : app.InstallDate),
            ("Install Location", string.IsNullOrEmpty(app.InstallLocation) ? "—" : app.InstallLocation),
            ("Install Size", EventDisplay.Size(ev)),
            ("Pkg Manager", string.IsNullOrEmpty(app.PackageManager) ? "—" : app.PackageManager),
            ("", ""),  // spacer
            ("Detection Source", ev.Source.ToString()),
            ("Registry Key", app.KeyPath)
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

            var txtValue = new TextBox
            {
                Text = value,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = _theme.FormBg,
                ForeColor = _theme.FormFg,
                Font = new Font("Segoe UI", 9f),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Width = 400,
                Margin = new Padding(0, 4, 0, 4)
            };

            // Color the change type
            if (label == "Change Type")
            {
                txtValue.ForeColor = EventDisplay.Accent(ev.ChangeType, _theme);
                txtValue.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            }

            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(lblName, 0, row);
            panel.Controls.Add(txtValue, 1, row);
            panel.RowCount = ++row;
        }

        // Footer panel with buttons
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

        // Add "View Diff" button for Updated events
        if (ev.ChangeType is ChangeType.Updated or ChangeType.Modified)
        {
            var btnDiff = new Button
            {
                Text = "⟳ View Diff",
                AutoSize = true,
                Height = 30,
                BackColor = _theme.UpdatedAccent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Cursor = Cursors.Hand,
                Location = new Point(10, 7)
            };
            btnDiff.FlatAppearance.BorderSize = 0;
            btnDiff.Click += (_, _) =>
            {
                using var diffForm = new DiffViewForm(ev, _theme);
                diffForm.ShowDialog(dlg);
            };
            footerPanel.Controls.Add(btnDiff);
        }

        dlg.Controls.Add(panel);
        dlg.Controls.Add(footerPanel);
        dlg.ShowDialog(this);
    }

    // ── Run at Startup ───────────────────────────────────────────────────────

    private const string StartupRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "AppSentry";

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, false);
            return key?.GetValue(StartupValueName) != null;
        }
        catch { return false; }
    }

    private void OnStartupToggled(object? sender, EventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: true);
            if (key == null) return;

            if (_chkStartup.Checked)
            {
                key.SetValue(StartupValueName, $"\"{Application.ExecutablePath}\"");
                SetStatus("✓ AppSentry will start with Windows.");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                SetStatus("✓ AppSentry will no longer start with Windows.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to update startup: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _chkStartup.Checked = !_chkStartup.Checked;
        }
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private async void OnClearHistory(object? sender, EventArgs e)
    {
        if (MessageBox.Show("Clear all change history?\nThis cannot be undone.",
            "Clear History", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            await _backend.ClearHistoryAsync();
            _allEvents.Clear();
            _knownEventIds.Clear();
            _listView.Items.Clear();
            SetStatus("History cleared.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not clear history: {ex.Message}", "Clear History",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnExportCsv(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export Change History",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"AppSentry_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var w = new StreamWriter(dlg.FileName);
            w.WriteLine("OccurredAt,DetectedAt,AppName,Version,ChangeType,Publisher,PreviousVersion,ChangedBy,InstalledFor,InstallSource,InstallType,Size,PkgManager,Source,Details");
            foreach (var ev in _allEvents)
            {
                w.WriteLine(string.Join(",",
                    Csv(ev.OccurredAt is { } at ? EventDisplay.LocalTime(at) : ""),
                    Csv(EventDisplay.LocalTime(ev.DetectedAt)),
                    Csv(ev.App.Name), Csv(ev.App.Version),
                    Csv(ev.ChangeType.ToString()), Csv(ev.App.Publisher),
                    Csv(ev.PreviousVersion ?? ""), Csv(EventDisplay.Who(ev)), Csv(ev.App.InstalledFor),
                    Csv(ev.App.InstallSource), Csv(ev.App.InstallType),
                    Csv(EventDisplay.Size(ev)), Csv(ev.App.PackageManager),
                    Csv(ev.Source.ToString()), Csv(ev.Details)));
            }
            SetStatus($"✓ Exported {_allEvents.Count} events to {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnIntervalChanged(object? sender, EventArgs e)
    {
        if (_applyingSettings) return;
        _intervalIndex = _cmbInterval.SelectedIndex;
        var minutes = IntervalMinutes[Math.Clamp(_intervalIndex, 0, IntervalMinutes.Length - 1)];
        try
        {
            var current = await _backend.GetSettingsAsync();
            await _backend.SaveSettingsAsync(current with { ScanIntervalMinutes = minutes });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not change the scan interval: {ex.Message}");
        }
    }

    private async void OnInstalledAppsClick(object? sender, EventArgs e)
    {
        _btnInstalledApps.Enabled = false;
        try
        {
            // The engine keeps the inventory current; no extra scan is needed to show it.
            var apps = await _backend.GetInventoryAsync();
            SetStatus($"Ready  ·  {apps.Count} apps found on this machine");
            using var frm = new InstalledAppsForm(apps, _theme, _isDarkMode, _exclusionStore, _backend.GetInventoryAsync);
            frm.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load installed apps: {ex.Message}");
        }
        finally
        {
            _btnInstalledApps.Enabled = true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string text) => _statusLabel.Text = text;

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

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
        _sharedToolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    private static Panel MakeSpacer(int width) =>
        new() { Width = width, Height = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
}

// ── Theme color definitions ──────────────────────────────────────────────────

internal class ThemeColors
{
    // Form
    public Color FormBg, FormFg;
    // Toolbar
    public Color ToolbarBg;
    // List
    public Color ListBg, ListFg, AltRowBg, RowBorder;
    // Header
    public Color HeaderBg, HeaderFg, HeaderBorder;
    // Status
    public Color StatusBg, StatusFg, MutedFg;
    // Selection
    public Color SelectedBg, SelectedFg;
    // Change type backgrounds
    public Color InstalledBg, UpdatedBg, RemovedBg, ModifiedBg;
    // Change type text accents
    public Color InstalledAccent, UpdatedAccent, RemovedAccent, ModifiedAccent;
    // Input
    public Color InputBg, InputFg;
    // Buttons
    public Color ButtonBg, ButtonFg, ButtonBorder, ButtonHover;

    public static ThemeColors Dark() => new()
    {
        FormBg = Color.FromArgb(28, 28, 32),
        FormFg = Color.FromArgb(220, 222, 228),
        ToolbarBg = Color.FromArgb(36, 36, 42),
        ListBg = Color.FromArgb(22, 22, 28),
        ListFg = Color.FromArgb(210, 212, 218),
        AltRowBg = Color.FromArgb(28, 28, 35),
        RowBorder = Color.FromArgb(40, 40, 48),
        HeaderBg = Color.FromArgb(34, 34, 42),
        HeaderFg = Color.FromArgb(160, 165, 180),
        HeaderBorder = Color.FromArgb(50, 50, 60),
        StatusBg = Color.FromArgb(30, 30, 36),
        StatusFg = Color.FromArgb(140, 145, 160),
        MutedFg = Color.FromArgb(140, 145, 160),
        SelectedBg = Color.FromArgb(45, 80, 140),
        SelectedFg = Color.FromArgb(255, 255, 255),
        InstalledBg = Color.FromArgb(22, 42, 28),
        UpdatedBg = Color.FromArgb(22, 32, 52),
        RemovedBg = Color.FromArgb(45, 28, 28),
        ModifiedBg = Color.FromArgb(48, 40, 20),
        InstalledAccent = Color.FromArgb(80, 200, 120),
        UpdatedAccent = Color.FromArgb(100, 160, 255),
        RemovedAccent = Color.FromArgb(220, 100, 100),
        ModifiedAccent = Color.FromArgb(230, 180, 70),
        InputBg = Color.FromArgb(40, 40, 48),
        InputFg = Color.FromArgb(210, 212, 218),
        ButtonBg = Color.FromArgb(48, 48, 58),
        ButtonFg = Color.FromArgb(210, 212, 218),
        ButtonBorder = Color.FromArgb(65, 65, 78),
        ButtonHover = Color.FromArgb(58, 58, 72)
    };

    public static ThemeColors Light() => new()
    {
        FormBg = Color.FromArgb(248, 249, 252),
        FormFg = Color.FromArgb(30, 30, 40),
        ToolbarBg = Color.FromArgb(240, 242, 248),
        ListBg = Color.White,
        ListFg = Color.FromArgb(30, 30, 40),
        AltRowBg = Color.FromArgb(247, 248, 252),
        RowBorder = Color.FromArgb(235, 237, 242),
        HeaderBg = Color.FromArgb(242, 244, 250),
        HeaderFg = Color.FromArgb(90, 95, 110),
        HeaderBorder = Color.FromArgb(218, 222, 232),
        StatusBg = Color.FromArgb(242, 244, 250),
        StatusFg = Color.FromArgb(100, 105, 120),
        MutedFg = Color.FromArgb(110, 115, 130),
        SelectedBg = Color.FromArgb(50, 110, 200),
        SelectedFg = Color.White,
        InstalledBg = Color.FromArgb(232, 250, 235),
        UpdatedBg = Color.FromArgb(232, 240, 255),
        RemovedBg = Color.FromArgb(252, 235, 235),
        ModifiedBg = Color.FromArgb(255, 246, 224),
        InstalledAccent = Color.FromArgb(30, 140, 60),
        UpdatedAccent = Color.FromArgb(40, 100, 210),
        RemovedAccent = Color.FromArgb(200, 50, 50),
        ModifiedAccent = Color.FromArgb(176, 110, 0),
        InputBg = Color.White,
        InputFg = Color.FromArgb(30, 30, 40),
        ButtonBg = Color.White,
        ButtonFg = Color.FromArgb(40, 45, 60),
        ButtonBorder = Color.FromArgb(200, 205, 215),
        ButtonHover = Color.FromArgb(232, 235, 242)
    };
}

// ── Dark ToolStrip renderer (for tray context menu) ──────────────────────────

internal class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Color.FromArgb(210, 212, 218);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Color.FromArgb(36, 36, 42));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Bounds.Height / 2;
        using var pen = new Pen(Color.FromArgb(55, 55, 65));
        e.Graphics.DrawLine(pen, 4, y, e.Item.Bounds.Width - 4, y);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected)
        {
            using var brush = new SolidBrush(Color.FromArgb(50, 50, 62));
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
    }
}

internal class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(36, 36, 42);
    public override Color MenuItemSelected => Color.FromArgb(50, 50, 62);
    public override Color MenuBorder => Color.FromArgb(55, 55, 65);
    public override Color MenuItemBorder => Color.FromArgb(55, 55, 65);
    public override Color ImageMarginGradientBegin => Color.FromArgb(36, 36, 42);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(36, 36, 42);
    public override Color ImageMarginGradientEnd => Color.FromArgb(36, 36, 42);
    public override Color SeparatorDark => Color.FromArgb(55, 55, 65);
    public override Color SeparatorLight => Color.FromArgb(45, 45, 55);
}
