using Microsoft.Win32;

namespace GhostScreen;

internal sealed class MainForm : Form
{
    private const int HOTKEY_ID_TOGGLE = 9001;
    private const int HOTKEY_ID_UNLOCK = 9002;
    private const int HOTKEY_ID_HIDE_UI = 9003;

    private readonly BlackoutOverlayForm _overlay;
    private readonly BrightnessController _brightness;
    private readonly ParsecVddController _vdd;
    private readonly System.Windows.Forms.Timer _watchdogTimer;
    private readonly System.Windows.Forms.Timer _testCountdownTimer;
    private readonly NotifyIcon _trayIcon;

    private bool _isBlackoutActive;
    private bool _isAppHidden;
    private int _testSecondsRemaining;

    // UI Controls
    private Panel _statusCard = null!;
    private Label _statusIndicatorLabel = null!;
    private Label _statusDetailLabel = null!;
    private Button _toggleButton = null!;
    private Button _hideAppButton = null!;
    private Button _testButton = null!;
    private Button _installDriverButton = null!;
    private CheckBox _dimBacklightCheckBox = null!;
    private RadioButton _zeroLightVddRadio = null!;
    private RadioButton _pitchBlackRadio = null!;
    private RadioButton _peekModeRadio = null!;
    private ToolStripMenuItem _trayToggleItem = null!;

    public MainForm(bool startActive = false)
    {
        _overlay = new BlackoutOverlayForm();
        _ = _overlay.Handle;

        _brightness = new BrightnessController();
        _vdd = new ParsecVddController();

        _watchdogTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _watchdogTimer.Tick += (_, _) =>
        {
            if (_isBlackoutActive && _overlay.Visible)
            {
                _overlay.EnforceStealthAndTopmost();
            }
        };

        _testCountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _testCountdownTimer.Tick += OnTestCountdownTick;

        InitializeWindowAndControls();
        _trayIcon = CreateTrayIcon();
        RefreshVddDriverUiState();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (startActive)
        {
            Shown += (_, _) => SetBlackoutState(true);
        }
    }

    private void InitializeWindowAndControls()
    {
        Text = "GhostScreen — Remote Stealth Curtain (0% Light Edition)";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 23, 42); // Slate 900
        ForeColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        ClientSize = new Size(540, 645);
        Icon = CreateAppIcon(false);

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.Transparent
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Header
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Status card
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Action buttons
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Options card
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Hotkeys card
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // 1. Header
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        var titleLabel = new Label
        {
            Text = "GhostScreen Stealth Curtain",
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(0, 0)
        };

        var subtitleLabel = new Label
        {
            Text = "Powers OFF your physical laptop display (0.0% light in a dark room) while Chrome Remote Desktop stays 100% clear.",
            Font = new Font("Segoe UI", 9.25f, FontStyle.Regular),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            MaximumSize = new Size(495, 0),
            Location = new Point(0, 34)
        };

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subtitleLabel);
        mainLayout.Controls.Add(headerPanel, 0, 0);

        // 2. Status Card
        _statusCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 82,
            BackColor = Color.FromArgb(30, 41, 59), // Slate 800
            Padding = new Padding(16, 12, 16, 12),
            Margin = new Padding(0, 0, 0, 14)
        };

        _statusIndicatorLabel = new Label
        {
            Text = "●  PHYSICAL SCREEN: VISIBLE (NORMAL)",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248),
            AutoSize = true,
            Location = new Point(16, 14)
        };

        _statusDetailLabel = new Label
        {
            Text = "Checking Virtual Display Driver status...",
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(16, 42)
        };

        _statusCard.Controls.Add(_statusIndicatorLabel);
        _statusCard.Controls.Add(_statusDetailLabel);
        mainLayout.Controls.Add(_statusCard, 0, 1);

        // 3. Primary Buttons
        var buttonsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 156,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 14)
        };
        buttonsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttonsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
        buttonsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));
        buttonsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));

        _toggleButton = new Button
        {
            Text = "TURN LAPTOP SCREEN OFF   (Ctrl + Alt + B)",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            BackColor = Color.FromArgb(37, 99, 235), // Blue 600
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 8)
        };
        _toggleButton.FlatAppearance.BorderSize = 0;
        _toggleButton.Click += (_, _) =>
        {
            StopTestCountdown();
            SetBlackoutState(!_isBlackoutActive);
        };

        _hideAppButton = new Button
        {
            Text = "Hide App Completely (Shortcuts Stay Active • Ctrl + Alt + H)",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            BackColor = Color.FromArgb(79, 70, 229), // Indigo 600
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 8)
        };
        _hideAppButton.FlatAppearance.BorderSize = 0;
        _hideAppButton.Click += (_, _) => SetAppVisibility(false);

        _testButton = new Button
        {
            Text = "Safe Test: Blackout for 8 Seconds (Auto-Restores)",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            BackColor = Color.FromArgb(51, 65, 85), // Slate 700
            ForeColor = Color.FromArgb(226, 232, 240),
            Cursor = Cursors.Hand,
            Margin = new Padding(0)
        };
        _testButton.FlatAppearance.BorderSize = 0;
        _testButton.Click += (_, _) => StartSafeTest();

        buttonsPanel.Controls.Add(_toggleButton, 0, 0);
        buttonsPanel.Controls.Add(_hideAppButton, 0, 1);
        buttonsPanel.Controls.Add(_testButton, 0, 2);
        mainLayout.Controls.Add(buttonsPanel, 0, 2);

        // 4. Settings & Mode Card
        var optionsCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 185,
            BackColor = Color.FromArgb(30, 41, 59),
            Padding = new Padding(16, 12, 16, 12),
            Margin = new Padding(0, 0, 0, 14)
        };

        _zeroLightVddRadio = new RadioButton
        {
            Text = "0.0% Light: Virtual Display Switch (Powers OFF laptop panel completely)",
            Checked = true,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9.25f, FontStyle.Bold),
            ForeColor = Color.FromArgb(52, 211, 153), // Emerald 400
            Location = new Point(16, 12)
        };

        _installDriverButton = new Button
        {
            Text = "⚡ Install 0%-Light Virtual Display Driver (One-Time Admin Setup)",
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Bold),
            BackColor = Color.FromArgb(5, 150, 105), // Emerald 600
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Size = new Size(464, 32),
            Location = new Point(16, 40)
        };
        _installDriverButton.FlatAppearance.BorderSize = 0;
        _installDriverButton.Click += (_, _) => OnInstallDriverClicked();

        _pitchBlackRadio = new RadioButton
        {
            Text = "Software Curtain Only (Instant Black Overlay + 1% Minimum Backlight)",
            Checked = false,
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(16, 82)
        };
        _pitchBlackRadio.CheckedChanged += (_, _) =>
        {
            if (_pitchBlackRadio.Checked)
                _overlay.SetOverlayAlpha(255);
        };

        _peekModeRadio = new RadioButton
        {
            Text = "88% Dimmed Overlay (Faint local peek, 100% clear on Remote)",
            Checked = false,
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(16, 112)
        };
        _peekModeRadio.CheckedChanged += (_, _) =>
        {
            if (_peekModeRadio.Checked)
                _overlay.SetOverlayAlpha(224);
        };

        string backlightSuffix = _brightness.IsSupported
            ? $" (Current: {_brightness.GetCurrentBrightness() ?? 100}%)"
            : " (Not supported on external desktop display)";

        _dimBacklightCheckBox = new CheckBox
        {
            Text = "Also drop WMI backlight to 0% during blackout" + backlightSuffix,
            Checked = _brightness.IsSupported,
            Enabled = _brightness.IsSupported,
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(241, 245, 249),
            Location = new Point(16, 146)
        };
        _dimBacklightCheckBox.CheckedChanged += (_, _) =>
        {
            if (_isBlackoutActive)
            {
                if (_dimBacklightCheckBox.Checked)
                    _brightness.DimToZero();
                else
                    _brightness.RestoreBrightness();
            }
        };

        optionsCard.Controls.Add(_zeroLightVddRadio);
        optionsCard.Controls.Add(_installDriverButton);
        optionsCard.Controls.Add(_pitchBlackRadio);
        optionsCard.Controls.Add(_peekModeRadio);
        optionsCard.Controls.Add(_dimBacklightCheckBox);
        mainLayout.Controls.Add(optionsCard, 0, 3);

        // 5. Hotkeys & Safety Footer Card
        var footerCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 94,
            BackColor = Color.FromArgb(23, 32, 48),
            Padding = new Padding(16, 10, 16, 10),
            Margin = new Padding(0)
        };

        var hotkeyTitle = new Label
        {
            Text = "GLOBAL SHORTCUTS (WORK EVEN WHEN APP & SCREEN ARE HIDDEN):",
            Font = new Font("Segoe UI Semibold", 8.25f, FontStyle.Bold),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(16, 10)
        };

        var hotkeyDesc = new Label
        {
            Text = "• Ctrl + Alt + B : Toggle 0%-Light Blackout ON / OFF\r\n" +
                   "• Ctrl + Alt + H : Hide / Show This App Completely\r\n" +
                   "• Ctrl + Alt + U : Emergency Restore Physical Screen & Show App",
            Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
            ForeColor = Color.FromArgb(226, 232, 240),
            AutoSize = true,
            Location = new Point(16, 30)
        };

        footerCard.Controls.Add(hotkeyTitle);
        footerCard.Controls.Add(hotkeyDesc);
        mainLayout.Controls.Add(footerCard, 0, 4);

        Controls.Add(mainLayout);
    }

    private void RefreshVddDriverUiState()
    {
        bool installed = _vdd.IsDriverInstalled();
        if (installed)
        {
            _zeroLightVddRadio.Enabled = true;
            _zeroLightVddRadio.Checked = true;
            _installDriverButton.Text = "✓ 0%-Light Virtual Display Driver Installed & Ready (Parsec VDD)";
            _installDriverButton.BackColor = Color.FromArgb(30, 58, 138);
            _installDriverButton.Enabled = false;
            _statusDetailLabel.Text = "0% Light Virtual Display Ready • Powers off laptop panel completely when active";
        }
        else
        {
            _zeroLightVddRadio.Enabled = false;
            _pitchBlackRadio.Checked = true;
            _installDriverButton.Text = "⚡ Install 0%-Light Virtual Display Driver (One-Time Admin Setup)";
            _installDriverButton.BackColor = Color.FromArgb(5, 150, 105);
            _installDriverButton.Enabled = true;
            _statusDetailLabel.Text = "Click 'Install 0%-Light Virtual Display Driver' below to enable 100% backlight power-off";
        }
    }

    private void OnInstallDriverClicked()
    {
        _installDriverButton.Enabled = false;
        _installDriverButton.Text = "Installing Parsec Virtual Display Driver (Approve UAC prompt)...";

        Task.Run(() =>
        {
            bool ok = _vdd.InstallDriverWithUac(out string err);
            BeginInvoke(() =>
            {
                RefreshVddDriverUiState();
                if (!ok && !string.IsNullOrEmpty(err))
                {
                    MessageBox.Show(
                        $"Driver installation did not complete:\n{err}",
                        "Virtual Display Driver Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            });
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterGlobalHotkeys();
    }

    private void RegisterGlobalHotkeys()
    {
        if (!IsHandleCreated) return;

        NativeMethods.RegisterHotKey(
            Handle,
            HOTKEY_ID_TOGGLE,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            (uint)Keys.B);

        NativeMethods.RegisterHotKey(
            Handle,
            HOTKEY_ID_UNLOCK,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            (uint)Keys.U);

        NativeMethods.RegisterHotKey(
            Handle,
            HOTKEY_ID_HIDE_UI,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            (uint)Keys.H);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_ID_TOGGLE)
            {
                StopTestCountdown();
                SetBlackoutState(!_isBlackoutActive);
                return;
            }
            if (id == HOTKEY_ID_UNLOCK)
            {
                StopTestCountdown();
                SetBlackoutState(false);
                SetAppVisibility(true);
                return;
            }
            if (id == HOTKEY_ID_HIDE_UI)
            {
                SetAppVisibility(_isAppHidden);
                return;
            }
        }
        base.WndProc(ref m);
    }

    public void SetAppVisibility(bool visible)
    {
        if (visible)
        {
            _isAppHidden = false;
            _trayIcon.Visible = true;
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }
        else
        {
            _isAppHidden = true;
            _trayIcon.Visible = false;
            Hide();
        }
    }

    public void SetBlackoutState(bool enable)
    {
        _isBlackoutActive = enable;

        if (enable)
        {
            // Always show the capture-excluded black curtain immediately so transition is instant
            _overlay.SetOverlayAlpha(_peekModeRadio.Checked ? (byte)224 : (byte)255);
            _overlay.Bounds = NativeMethods.GetVirtualScreenPhysicalBounds();
            _overlay.ApplyStealthAttributes();
            if (!_overlay.Visible)
            {
                _overlay.Show();
            }
            _overlay.EnforceStealthAndTopmost();
            _watchdogTimer.Start();

            if (_dimBacklightCheckBox.Checked && _brightness.IsSupported)
            {
                _brightness.DimToZero();
            }

            if (_zeroLightVddRadio.Checked && _vdd.IsDriverInstalled())
            {
                _vdd.ActivateZeroLightMode(out string vddMsg);
                _statusDetailLabel.Text = vddMsg;
            }
            else
            {
                _statusDetailLabel.Text = "Physical display covered • Press Ctrl+Alt+H to hide this window too!";
            }

            _statusCard.BackColor = Color.FromArgb(6, 78, 59); // Emerald 900
            _statusIndicatorLabel.Text = _zeroLightVddRadio.Checked
                ? "●  0% LIGHT ACTIVE: LAPTOP PANEL OFF • REMOTE ON VIRTUAL DISPLAY"
                : "●  STEALTH ACTIVE: LAPTOP IS BLACK • REMOTE IS CLEAR";
            _statusIndicatorLabel.ForeColor = Color.FromArgb(52, 211, 153); // Emerald 400

            _toggleButton.Text = "RESTORE LAPTOP SCREEN   (Ctrl + Alt + B)";
            _toggleButton.BackColor = Color.FromArgb(220, 38, 38); // Red 600
            _trayToggleItem.Text = "Restore Laptop Screen (Ctrl+Alt+B)";
            Icon = CreateAppIcon(true);
            _trayIcon.Icon = Icon;
        }
        else
        {
            _watchdogTimer.Stop();
            _vdd.DeactivateZeroLightMode();
            _overlay.Hide();
            _brightness.RestoreBrightness();

            _statusCard.BackColor = Color.FromArgb(30, 41, 59); // Slate 800
            _statusIndicatorLabel.Text = "●  PHYSICAL SCREEN: VISIBLE (NORMAL)";
            _statusIndicatorLabel.ForeColor = Color.FromArgb(56, 189, 248);
            RefreshVddDriverUiState();

            _toggleButton.Text = "TURN LAPTOP SCREEN OFF   (Ctrl + Alt + B)";
            _toggleButton.BackColor = Color.FromArgb(37, 99, 235); // Blue 600
            _trayToggleItem.Text = "Turn Laptop Screen Off (Ctrl+Alt+B)";
            Icon = CreateAppIcon(false);
            _trayIcon.Icon = Icon;
        }
    }

    private void StartSafeTest()
    {
        _testSecondsRemaining = 8;
        SetBlackoutState(true);
        _testButton.Text = $"Testing Blackout... Auto-restoring in {_testSecondsRemaining}s (Click to cancel)";
        _testButton.BackColor = Color.FromArgb(217, 119, 6); // Amber 600
        _testCountdownTimer.Start();
    }

    private void OnTestCountdownTick(object? sender, EventArgs e)
    {
        _testSecondsRemaining--;
        if (_testSecondsRemaining <= 0)
        {
            StopTestCountdown();
            SetBlackoutState(false);
        }
        else
        {
            _testButton.Text = $"Testing Blackout... Auto-restoring in {_testSecondsRemaining}s (Click to cancel)";
        }
    }

    private void StopTestCountdown()
    {
        if (_testCountdownTimer.Enabled)
        {
            _testCountdownTimer.Stop();
            _testButton.Text = "Safe Test: Blackout for 8 Seconds (Auto-Restores)";
            _testButton.BackColor = Color.FromArgb(51, 65, 85);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isBlackoutActive && _overlay.Visible)
        {
            _overlay.EnforceStealthAndTopmost();
        }
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();

        _trayToggleItem = new ToolStripMenuItem("Turn Laptop Screen Off (Ctrl+Alt+B)");
        _trayToggleItem.Click += (_, _) =>
        {
            StopTestCountdown();
            SetBlackoutState(!_isBlackoutActive);
        };

        var hideUiItem = new ToolStripMenuItem("Hide App Completely (Ctrl+Alt+H)");
        hideUiItem.Click += (_, _) => SetAppVisibility(false);

        var testItem = new ToolStripMenuItem("Safe Test for 8 Seconds");
        testItem.Click += (_, _) => StartSafeTest();

        var showItem = new ToolStripMenuItem("Show GhostScreen Control Panel");
        showItem.Click += (_, _) => SetAppVisibility(true);

        var exitItem = new ToolStripMenuItem("Exit & Restore Screen");
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(_trayToggleItem);
        menu.Items.Add(hideUiItem);
        menu.Items.Add(testItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(exitItem);

        var notifyIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "GhostScreen — 0% Light Stealth Curtain (Ctrl+Alt+B)",
            ContextMenuStrip = menu,
            Visible = true
        };

        notifyIcon.DoubleClick += (_, _) => SetAppVisibility(true);

        return notifyIcon;
    }

    private static Icon CreateAppIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(active ? Color.FromArgb(16, 185, 129) : Color.FromArgb(37, 99, 235));
            g.FillEllipse(bgBrush, 2, 2, 28, 28);

            using var innerBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
            g.FillEllipse(innerBrush, 7, 7, 18, 18);

            using var dotBrush = new SolidBrush(active ? Color.FromArgb(52, 211, 153) : Color.White);
            g.FillEllipse(dotBrush, 12, 12, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            StopTestCountdown();
            _watchdogTimer.Stop();
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_TOGGLE);
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_UNLOCK);
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_HIDE_UI);
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _vdd.Dispose();
            _brightness.RestoreBrightness();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _overlay.Close();
            _overlay.Dispose();
        }
        catch
        {
            // Ignore cleanup errors on shutdown
        }
        base.OnFormClosing(e);
    }
}
