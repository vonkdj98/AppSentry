using AppSentry.Models;

namespace AppSentry;

/// <summary>
/// A popup notification that appears in the bottom-right corner of the screen.
/// Stays visible until the user clicks "Dismiss" or "View Details".
/// Styled to match the app theme (dark/light).
/// </summary>
internal class NotificationForm : Form
{
    protected override bool ShowWithoutActivation => true;

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 0x0003;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    private readonly ThemeColors _theme;
    private readonly List<ChangeEvent> _events;
    private readonly Action<ChangeEvent>? _onViewDetails;
    private readonly int _autoHideSeconds;
    private System.Windows.Forms.Timer? _slideTimer;
    private System.Windows.Forms.Timer? _autoHideTimer;
    private int _targetY;
    private const int AnimationStep = 8;

    public NotificationForm(List<ChangeEvent> events, ThemeColors theme,
        Action<ChangeEvent>? onViewDetails = null, int autoHideSeconds = 0)
    {
        _events = events;
        _theme = theme;
        _onViewDetails = onViewDetails;
        _autoHideSeconds = autoHideSeconds;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);
        BuildForm();
    }

    private void BuildForm()
    {
        // Form setup — borderless, topmost, no taskbar
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = _theme.FormBg;
        ForeColor = _theme.FormFg;
        Size = new Size(380, CalculateHeight());
        Opacity = 0.96;

        // Position: bottom-right, just above taskbar
        var screen = Screen.PrimaryScreen ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0] : null);
        if (screen == null) { Close(); return; }
        var workArea = screen.WorkingArea;
        _targetY = workArea.Bottom - Height - 12;
        Location = new Point(workArea.Right - Width - 12, workArea.Bottom); // start offscreen

        // Main panel with rounded-corner effect via padding
        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _theme.FormBg,
            Padding = new Padding(0)
        };

        // ── Header ───────────────────────────────────────────────────────────
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = _theme.ToolbarBg,
            Padding = new Padding(14, 0, 8, 0)
        };

        var iconLabel = new Label
        {
            Text = GetHeaderIcon(),
            Font = new Font("Segoe UI", 14f),
            AutoSize = true,
            Location = new Point(12, 10),
            ForeColor = GetAccentColor(),
            BackColor = Color.Transparent
        };

        var titleLabel = new Label
        {
            Text = GetHeaderTitle(),
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = _theme.FormFg,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(36, 12)
        };

        var btnClose = new Label
        {
            Text = "✕",
            Font = new Font("Segoe UI", 10f),
            ForeColor = _theme.MutedFg,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Location = new Point(Width - 30, 12)
        };
        btnClose.Click += (_, _) => SlideOut();
        btnClose.MouseEnter += (_, _) => btnClose.ForeColor = _theme.RemovedAccent;
        btnClose.MouseLeave += (_, _) => btnClose.ForeColor = _theme.MutedFg;

        headerPanel.Controls.AddRange([iconLabel, titleLabel, btnClose]);

        // ── Content ──────────────────────────────────────────────────────────
        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _theme.FormBg,
            Padding = new Padding(16, 12, 16, 8),
            AutoScroll = true
        };

        var yPos = 8;

        // Show up to 5 events
        var displayEvents = _events.Take(5).ToList();
        foreach (var ev in displayEvents)
        {
            var eventPanel = CreateEventRow(ev, yPos);
            contentPanel.Controls.Add(eventPanel);
            yPos += eventPanel.Height + 6;
        }

        if (_events.Count > 5)
        {
            var moreLabel = new Label
            {
                Text = $"  +{_events.Count - 5} more changes…",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = _theme.MutedFg,
                AutoSize = true,
                Location = new Point(16, yPos + 2)
            };
            contentPanel.Controls.Add(moreLabel);
        }

        // ── Footer with buttons ──────────────────────────────────────────────
        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            BackColor = _theme.ToolbarBg,
            Padding = new Padding(12, 8, 12, 8)
        };

        var btnViewDetails = new Button
        {
            Text = "View Details",
            Width = 110,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = GetAccentColor(),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9f),
            Cursor = Cursors.Hand,
            Location = new Point(Width - 140, 9)
        };
        btnViewDetails.FlatAppearance.BorderSize = 0;
        btnViewDetails.Click += (_, _) =>
        {
            if (_events.Count > 0 && _onViewDetails != null)
                _onViewDetails(_events[0]);
            Close();
        };

        var btnDismiss = new Button
        {
            Text = "Dismiss",
            Width = 80,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBg,
            ForeColor = _theme.ButtonFg,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
            Location = new Point(Width - 234, 9)
        };
        btnDismiss.FlatAppearance.BorderColor = _theme.ButtonBorder;
        btnDismiss.Click += (_, _) => SlideOut();

        footerPanel.Controls.AddRange([btnViewDetails, btnDismiss]);

        // ── Border ───────────────────────────────────────────────────────────
        mainPanel.Controls.Add(contentPanel);
        mainPanel.Controls.Add(headerPanel);
        mainPanel.Controls.Add(footerPanel);
        Controls.Add(mainPanel);
    }

    private Panel CreateEventRow(ChangeEvent ev, int yPos)
    {
        var panel = new Panel
        {
            Location = new Point(4, yPos),
            Size = new Size(340, 48),
            BackColor = ev.ChangeType switch
            {
                ChangeType.Installed => _theme.InstalledBg,
                ChangeType.Updated => _theme.UpdatedBg,
                ChangeType.Removed => _theme.RemovedBg,
                _ => _theme.AltRowBg
            }
        };

        var changeIcon = new Label
        {
            Text = ev.ChangeType switch
            {
                ChangeType.Installed => "⬇",
                ChangeType.Updated => "⟳",
                ChangeType.Removed => "✕",
                _ => "•"
            },
            Font = new Font("Segoe UI", 12f),
            ForeColor = ev.ChangeType switch
            {
                ChangeType.Installed => _theme.InstalledAccent,
                ChangeType.Updated => _theme.UpdatedAccent,
                ChangeType.Removed => _theme.RemovedAccent,
                _ => _theme.MutedFg
            },
            Location = new Point(10, 12),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        var nameLabel = new Label
        {
            Text = ev.App.Name,
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = _theme.FormFg,
            Location = new Point(36, 6),
            AutoSize = true,
            BackColor = Color.Transparent,
            MaximumSize = new Size(280, 0)
        };

        var detailText = ev.ChangeType switch
        {
            ChangeType.Installed => $"Installed  ·  v{ev.App.Version}",
            ChangeType.Updated => $"Updated  ·  {ev.PreviousVersion} → {ev.App.Version}",
            ChangeType.Removed => $"Removed  ·  v{ev.App.Version}",
            _ => ev.ChangeType.ToString()
        };

        var detailLabel = new Label
        {
            Text = detailText,
            Font = new Font("Segoe UI", 8f),
            ForeColor = _theme.MutedFg,
            Location = new Point(36, 27),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        panel.Controls.AddRange([changeIcon, nameLabel, detailLabel]);
        return panel;
    }

    // ── Slide animation ──────────────────────────────────────────────────────

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SlideIn();
    }

    private void SlideIn()
    {
        _slideTimer = new System.Windows.Forms.Timer { Interval = 12 };
        _slideTimer.Tick += (_, _) =>
        {
            if (Top > _targetY)
            {
                Top = Math.Max(_targetY, Top - AnimationStep);
            }
            else
            {
                _slideTimer!.Stop();
                _slideTimer.Dispose();
                _slideTimer = null;
                StartAutoHideTimer();
            }
        };
        _slideTimer.Start();
    }

    private void StartAutoHideTimer()
    {
        if (_autoHideSeconds <= 0) return;
        _autoHideTimer = new System.Windows.Forms.Timer { Interval = _autoHideSeconds * 1000 };
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer!.Stop();
            _autoHideTimer.Dispose();
            _autoHideTimer = null;
            SlideOut();
        };
        _autoHideTimer.Start();
    }

    private void SlideOut()
    {
        _slideTimer?.Stop();
        _slideTimer?.Dispose();
        var screen = Screen.PrimaryScreen ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0] : null);
        if (screen == null) { Close(); return; }
        var workArea = screen.WorkingArea;

        _slideTimer = new System.Windows.Forms.Timer { Interval = 12 };
        _slideTimer.Tick += (_, _) =>
        {
            if (Top < workArea.Bottom)
            {
                Top += AnimationStep + 4;
            }
            else
            {
                _slideTimer!.Stop();
                _slideTimer.Dispose();
                _slideTimer = null;
                Close();
            }
        };
        _slideTimer.Start();
    }

    // ── Paint border ─────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_theme.HeaderBorder, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    // ── Drop shadow via CreateParams ─────────────────────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int CalculateHeight()
    {
        var eventCount = Math.Min(_events.Count, 5);
        var contentHeight = eventCount * 54 + 20;
        if (_events.Count > 5) contentHeight += 24;
        return 44 + contentHeight + 50; // header + content + footer
    }

    private string GetHeaderIcon() => _events.Count == 1
        ? _events[0].ChangeType switch
        {
            ChangeType.Installed => "⬇",
            ChangeType.Updated => "⟳",
            ChangeType.Removed => "✕",
            _ => "ℹ"
        }
        : "ℹ";

    private string GetHeaderTitle()
    {
        if (_events.Count == 1)
        {
            return _events[0].ChangeType switch
            {
                ChangeType.Installed => "App Installed",
                ChangeType.Updated => "App Updated",
                ChangeType.Removed => "App Removed",
                _ => "App Change Detected"
            };
        }
        return $"{_events.Count} Changes Detected";
    }

    private Color GetAccentColor()
    {
        if (_events.Count == 1)
        {
            return _events[0].ChangeType switch
            {
                ChangeType.Installed => _theme.InstalledAccent,
                ChangeType.Updated => _theme.UpdatedAccent,
                ChangeType.Removed => _theme.RemovedAccent,
                _ => _theme.UpdatedAccent
            };
        }
        return _theme.UpdatedAccent;
    }
}
