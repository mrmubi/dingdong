using System.IO;
using System.Text.Json;

namespace ControlPlan;

public partial class MainForm : Form
{
    private readonly Dictionary<int, CheckBox> _checks = new();
    private readonly AppSettings _settings;
    private readonly HttpHost _host;
    private readonly string _settingsPath;

    private FlowLayoutPanel _flow = null!;
    private NumericUpDown _portInput = null!;
    private Button _toggleBtn = null!;
    private Label _statusLabel = null!;
    private TextBox _logBox = null!;
    private Label _ipLabel = null!;

    public MainForm()
    {
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _settings = LoadSettings();
        // First-run: seed a strong shared-secret token so the device can
        // authenticate. The user can rotate it later from the Security tab.
        if (string.IsNullOrEmpty(_settings.AuthToken))
        {
            _settings.AuthToken = HttpHost.GenerateToken();
            SaveSettings();
        }
        _host = new HttpHost(() => _settings);
        _host.OnLog += (_, msg) => Log(msg);

        InitializeComponent();
        BuildUi();
        ApplySettingsToUi();
        // Auto-start the server so the device gets a response as soon as it boots.
        Load += (_, __) => ToggleServer();
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
                // Migrate legacy single AllowedDeviceIp into the new list.
                if (!string.IsNullOrWhiteSpace(s.AllowedDeviceIp))
                {
                    if (!s.AllowedDeviceIps.Contains(s.AllowedDeviceIp))
                        s.AllowedDeviceIps.Add(s.AllowedDeviceIp);
                    s.AllowedDeviceIp = "";
                }
                return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log("save failed: " + ex.Message); }
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        Text = "DingDong Control Plan";
        // Window/taskbar icon. Embedded via <EmbeddedResource> in the csproj.
        try
        {
            using var s = typeof(MainForm).Assembly
                .GetManifestResourceStream("ControlPlan.app.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { /* icon is cosmetic; ignore load failures */ }
        // Default size large enough to fit all 21 gadgets + Mentions tab + log
        // without scrolling.  Will be overwritten by saved size in ApplySettingsToUi.
        ClientSize = new Size(_settings.WindowWidth, _settings.WindowHeight);
        MinimumSize = new Size(640, 520);
        if (_settings.WindowX >= 0 && _settings.WindowY >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(_settings.WindowX, _settings.WindowY);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;

        FormClosing += (_, __) => { CaptureWindowGeometry(); _host.Stop(); SaveSettings(); };
        ResizeEnd += (_, __) => { CaptureWindowGeometry(); SaveSettings(); };
        // Maximize/restore changes ClientSize without ResizeEnd, so also hook SizeChanged.
        SizeChanged += (_, __) =>
        {
            if (WindowState == FormWindowState.Maximized && !_settings.WindowMaximized)
            {
                _settings.WindowMaximized = true;
                SaveSettings();
            }
            else if (WindowState == FormWindowState.Normal && _settings.WindowMaximized)
            {
                _settings.WindowMaximized = false;
                SaveSettings();
            }
        };
        ResumeLayout(false);
    }

    private void CaptureWindowGeometry()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
        }
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
    }

    private void BuildUi()
    {
        // ------- Top bar: port, poll, toggle, status -------
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 80,
            ColumnCount = 6,
            Padding = new Padding(8),
        };
        for (int i = 0; i < 6; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "Port:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        _portInput = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 8088, Width = 80 };
        top.Controls.Add(_portInput, 1, 0);

        _toggleBtn = new Button { Text = "Start server", AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        _toggleBtn.Click += (_, __) => ToggleServer();
        top.Controls.Add(_toggleBtn, 2, 0);

        _statusLabel = new Label { Text = "stopped", ForeColor = Color.Firebrick, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(12, 6, 0, 0) };
        top.Controls.Add(_statusLabel, 3, 0);

        _ipLabel = new Label { AutoSize = true, ForeColor = Color.DimGray };
        top.Controls.Add(_ipLabel, 0, 1);
        top.SetColumnSpan(_ipLabel, 6);
        UpdateIpLabel();

        // Note: in WinForms dock layout, controls are laid out in reverse z-order. The Fill
        // control must be added FIRST so it's laid out LAST and only gets the remaining space
        // after the Top/Bottom bars are subtracted. We add `tabs` here and build its pages below.
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);
        Controls.Add(top);

        // ------- Bottom: log -------
        var bottom = new GroupBox { Text = "Log", Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(8) };
        _logBox = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9) };
        bottom.Controls.Add(_logBox);
        Controls.Add(bottom);

        // ------- Middle: tabbed utilities -------
        var gadgetsTab = new TabPage("Gadgets") { Padding = new Padding(8) };

        var gadgetsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        gadgetsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gadgetsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 4) };
        var selectAllBtn = new Button { Text = "Select all", AutoSize = true };
        selectAllBtn.Click += (_, __) => SetAllChecked(true);
        var deselectAllBtn = new Button { Text = "Deselect all", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        deselectAllBtn.Click += (_, __) => SetAllChecked(false);
        var hint = new Label { Text = "  Press Reset on device to sync;  A = next, B = previous", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(12, 6, 0, 0) };
        actions.Controls.Add(selectAllBtn);
        actions.Controls.Add(deselectAllBtn);
        actions.Controls.Add(hint);
        gadgetsLayout.Controls.Add(actions, 0, 0);

        _flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var u in Utilities.All.Where(u => u.Implemented))
        {
            var cb = new CheckBox
            {
                Text = $"  {u.Name}    —    {u.Description}",
                Width = 660,
                AutoSize = false,
                Height = 24,
                Tag = u.Id,
            };
            cb.CheckedChanged += (_, __) =>
            {
                var id = (int)cb.Tag!;
                if (cb.Checked) _settings.Enabled.Add(id);
                else _settings.Enabled.Remove(id);
                SaveSettings();
            };
            _checks[u.Id] = cb;
            _flow.Controls.Add(cb);
        }
        gadgetsLayout.Controls.Add(_flow, 0, 1);
        gadgetsTab.Controls.Add(gadgetsLayout);
        tabs.TabPages.Add(gadgetsTab);

        // ------- Mentions tab -------
        var mentionsTab = new TabPage("Mentions") { Padding = new Padding(12), AutoScroll = true };
        var mLayout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        mLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var info = new Label
        {
            Text = "Counts are served at GET /config (fields: chats, emails) for the device's @Mentions gadget.\r\n" +
                   "Auto mode reads Windows Action Center notifications from Teams + Outlook — no sign-in required.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 0, 0, 12),
        };
        mLayout.Controls.Add(info, 0, 0);
        mLayout.SetColumnSpan(info, 3);

        _mAuto = new CheckBox { Text = "Auto-refresh from Windows notifications (every 30 s)", AutoSize = true };
        _mAuto.CheckedChanged += (_, __) =>
        {
            _settings.MentionsAuto = _mAuto.Checked;
            SaveSettings();
            UpdateMentionsUiState();
            if (_mAuto.Checked) _ = RefreshMentionsAsync();
        };
        mLayout.Controls.Add(_mAuto, 0, 1);
        mLayout.SetColumnSpan(_mAuto, 3);

        var btnRefresh = new Button { Text = "Refresh now", AutoSize = true, Margin = new Padding(0, 4, 8, 8) };
        btnRefresh.Click += async (_, __) => await RefreshMentionsAsync();
        mLayout.Controls.Add(btnRefresh, 0, 2);

        _mStatus = new Label { Text = "Not yet refreshed.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 0, 0) };
        mLayout.Controls.Add(_mStatus, 1, 2);
        mLayout.SetColumnSpan(_mStatus, 2);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 4) };
        var btnNotifSettings = new Button { Text = "Windows notification settings", AutoSize = true };
        btnNotifSettings.Click += (_, __) => OpenUri("ms-settings:notifications");
        var btnTeams = new Button { Text = "Open Teams", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        btnTeams.Click += (_, __) => OpenUri("msteams:");
        var btnOutlook = new Button { Text = "Open Outlook", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        btnOutlook.Click += (_, __) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("outlook.exe") { UseShellExecute = true }); }
            catch { OpenUri("ms-outlook://"); }
        };
        btnRow.Controls.Add(btnNotifSettings);
        btnRow.Controls.Add(btnTeams);
        btnRow.Controls.Add(btnOutlook);
        mLayout.Controls.Add(btnRow, 0, 3);
        mLayout.SetColumnSpan(btnRow, 3);

        _mTrackChats = new CheckBox { Text = "Track unread Teams chats / channel @-mentions", AutoSize = true };
        _mTrackChats.CheckedChanged += (_, __) => { _settings.MentionsTrackChats = _mTrackChats.Checked; SaveSettings(); };
        mLayout.Controls.Add(_mTrackChats, 0, 4);
        mLayout.SetColumnSpan(_mTrackChats, 3);

        _mTrackEmails = new CheckBox { Text = "Track unread Outlook emails", AutoSize = true };
        _mTrackEmails.CheckedChanged += (_, __) => { _settings.MentionsTrackEmails = _mTrackEmails.Checked; SaveSettings(); };
        mLayout.Controls.Add(_mTrackEmails, 0, 5);
        mLayout.SetColumnSpan(_mTrackEmails, 3);

        mLayout.Controls.Add(new Label { Text = "Name aliases:", AutoSize = true, Padding = new Padding(0, 10, 8, 0) }, 0, 6);
        _mAliases = new TextBox { Width = 260, Margin = new Padding(0, 6, 0, 0), Text = _settings.MentionsNameAliases };
        _mAliases.Leave += (_, __) => { _settings.MentionsNameAliases = _mAliases.Text; SaveSettings(); };
        mLayout.Controls.Add(_mAliases, 1, 6);
        mLayout.SetColumnSpan(_mAliases, 2);

        mLayout.Controls.Add(new Label { Text = "Chats count:", AutoSize = true, Padding = new Padding(0, 10, 8, 0) }, 0, 7);
        _mChatsInput = new NumericUpDown { Minimum = 0, Maximum = 9999, Width = 80, Margin = new Padding(0, 6, 0, 0) };
        _mChatsInput.ValueChanged += (_, __) => { _settings.MentionsChats = (int)_mChatsInput.Value; SaveSettings(); };
        mLayout.Controls.Add(_mChatsInput, 1, 7);

        mLayout.Controls.Add(new Label { Text = "Emails count:", AutoSize = true, Padding = new Padding(0, 10, 8, 0) }, 0, 8);
        _mEmailsInput = new NumericUpDown { Minimum = 0, Maximum = 9999, Width = 80, Margin = new Padding(0, 6, 0, 0) };
        _mEmailsInput.ValueChanged += (_, __) => { _settings.MentionsEmails = (int)_mEmailsInput.Value; SaveSettings(); };
        mLayout.Controls.Add(_mEmailsInput, 1, 8);

        var endpointHint = new Label
        {
            Text = "Tip: 'Name aliases' is a comma-separated list of name tokens to match in toast text.\r\n" +
                   "A toast counts as an @-mention only when its body contains '@<alias>'. Default = your\r\n" +
                   "Windows username; add nicknames or display-name fragments (e.g. 'mubi,mubi ali').\r\n" +
                   "After refresh, RESET the device so the firmware fetches the new count.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 12, 0, 0),
        };
        mLayout.Controls.Add(endpointHint, 0, 9);
        mLayout.SetColumnSpan(endpointHint, 3);

        mentionsTab.Controls.Add(mLayout);
        tabs.TabPages.Add(mentionsTab);

        // ------- Security tab -------
        BuildSecurityTab(tabs);

        // Periodic refresh when auto mode is on.
        _mTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _mTimer.Tick += async (_, __) => { if (_settings.MentionsAuto) await RefreshMentionsAsync(); };
        _mTimer.Start();
    }

    private CheckBox _mAuto = null!;
    private Label _mStatus = null!;
    private System.Windows.Forms.Timer _mTimer = null!;
    private readonly MentionsReader _mentionsReader = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? new MentionsReader() : null!;

    // ---- Security tab controls ------------------------------------------
    private ComboBox _secListenAddress = null!;
    private TextBox _secAuthToken = null!;
    private CheckedListBox _secAllowedIps = null!;
    private bool _suppressIpCheckPersist;
    private TextBox _secManualIp = null!;

    private void BuildSecurityTab(TabControl tabs)
    {
        var tab = new TabPage("Security") { Padding = new Padding(12), AutoScroll = true };
        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var info = new Label
        {
            Text = "These settings restrict which network interface accepts requests,\r\n" +
                   "require the device to present a shared-secret token, and (optionally)\r\n" +
                   "only accept requests from one or more known device IPs. Click 'Save\r\n" +
                   "& restart server' to apply changes. Copy the token into Firmware/\r\n" +
                   "secrets.h as DINGDONG_AUTH_TOKEN before re-flashing.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 0, 0, 12),
        };
        layout.Controls.Add(info, 0, 0);
        layout.SetColumnSpan(info, 3);

        // Listen address
        layout.Controls.Add(new Label { Text = "Listen on:", AutoSize = true, Padding = new Padding(0, 10, 8, 0) }, 0, 1);
        _secListenAddress = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(0, 6, 0, 0) };
        _secListenAddress.Items.Add("+");           // any interface
        _secListenAddress.Items.Add("localhost");   // loopback only
        foreach (var ip in HttpHost.GetLocalIPv4Addresses()) _secListenAddress.Items.Add(ip);
        _secListenAddress.Text = string.IsNullOrEmpty(_settings.ListenAddress) ? "+" : _settings.ListenAddress;
        layout.Controls.Add(_secListenAddress, 1, 1);
        layout.SetColumnSpan(_secListenAddress, 2);

        // Auth token
        layout.Controls.Add(new Label { Text = "Auth token:", AutoSize = true, Padding = new Padding(0, 10, 8, 0) }, 0, 2);
        _secAuthToken = new TextBox { Width = 260, Margin = new Padding(0, 6, 0, 0), Text = _settings.AuthToken, ReadOnly = true };
        layout.Controls.Add(_secAuthToken, 1, 2);
        var tokenRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(8, 4, 0, 0) };
        var btnCopy = new Button { Text = "Copy", AutoSize = true };
        btnCopy.Click += (_, __) =>
        {
            try { Clipboard.SetText(_secAuthToken.Text); Log("auth token copied to clipboard"); } catch { }
        };
        var btnRegen = new Button { Text = "Regenerate", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        btnRegen.Click += (_, __) =>
        {
            var ok = MessageBox.Show(this,
                "Generate a new token? You'll need to update Firmware/secrets.h and re-flash the device.",
                "Regenerate token", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ok != DialogResult.OK) return;
            _settings.AuthToken = HttpHost.GenerateToken();
            _secAuthToken.Text = _settings.AuthToken;
            SaveSettings();
            Log("auth token regenerated");
        };
        tokenRow.Controls.Add(btnCopy);
        tokenRow.Controls.Add(btnRegen);
        layout.Controls.Add(tokenRow, 2, 2);

        var tokenHint = new Label
        {
            Text = "Empty token disables auth check (not recommended). The firmware sends this\r\n" +
                   "value in the X-DingDong-Token header on every request.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(tokenHint, 0, 3);
        layout.SetColumnSpan(tokenHint, 3);

        // --- Allowed device IPs (CheckedListBox + auto-discovery) ---
        layout.Controls.Add(new Label { Text = "Allowed device IPs:", AutoSize = true, Padding = new Padding(0, 10, 8, 0) }, 0, 4);

        var ipColumn = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };

        _secAllowedIps = new CheckedListBox
        {
            Width = 320,
            Height = 130,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        // Persist the allow-list to disk whenever the user toggles a tick,
        // so the choice survives app restarts even without clicking
        // "Save & restart server" (which is only needed when changing the
        // listen address). The new value takes effect on the next request
        // because HttpHost reads AppSettings fresh on every call.
        _secAllowedIps.ItemCheck += (_, e) =>
        {
            // Skip while we are programmatically rebuilding the list (also
            // covers the initial population during the ctor, before the
            // form handle exists).
            if (_suppressIpCheckPersist || !IsHandleCreated) return;
            // ItemCheck fires before the state actually changes, so defer.
            BeginInvoke(new Action(() =>
            {
                _settings.AllowedDeviceIps = CollectCheckedIps();
                SaveSettings();
            }));
        };
        ipColumn.Controls.Add(_secAllowedIps);

        // Manual-add row
        var addRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        _secManualIp = new TextBox { Width = 180, PlaceholderText = "e.g. 192.168.1.123" };
        var btnAdd = new Button { Text = "Add", AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
        btnAdd.Click += (_, __) =>
        {
            var t = _secManualIp.Text.Trim();
            if (!System.Net.IPAddress.TryParse(t, out System.Net.IPAddress? _))
            {
                MessageBox.Show(this, "Not a valid IPv4 address.", "Invalid IP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AddSeenIpRow(t, lastSeen: null, accepted: null, checkedNow: true);
            _secManualIp.Clear();
        };
        var btnRemove = new Button { Text = "Remove unchecked", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        btnRemove.Click += (_, __) =>
        {
            for (int i = _secAllowedIps.Items.Count - 1; i >= 0; i--)
                if (!_secAllowedIps.GetItemChecked(i)) _secAllowedIps.Items.RemoveAt(i);
        };
        var btnRefresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        btnRefresh.Click += (_, __) => RefreshAllowedIpList();
        addRow.Controls.Add(_secManualIp);
        addRow.Controls.Add(btnAdd);
        addRow.Controls.Add(btnRemove);
        addRow.Controls.Add(btnRefresh);
        ipColumn.Controls.Add(addRow);

        layout.Controls.Add(ipColumn, 1, 4);
        layout.SetColumnSpan(ipColumn, 2);

        // Live updates when a new client hits the server.
        _host.OnSeenClientsChanged += (_, __) =>
        {
            if (IsDisposed || Disposing) return;
            try { BeginInvoke(new Action(RefreshAllowedIpList)); } catch { }
        };

        // Warning banner
        var warn = new Label
        {
            Text = "WARNING: Only tick IPs you trust. An allowed IP can read your @Mentions\r\n" +
                   "counts and your enabled-utility list. If a stranger's device shows up here\r\n" +
                   "(unexpected MAC on your LAN, guest Wi-Fi, an IoT bulb you don't recognise)\r\n" +
                   "leave it unchecked or remove it. Empty list disables the IP check entirely.",
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Padding = new Padding(0, 8, 0, 8),
            Font = new Font(Font, FontStyle.Bold),
        };
        layout.Controls.Add(warn, 0, 5);
        layout.SetColumnSpan(warn, 3);

        // Save & restart  +  Reserve URL
        var btnApply = new Button { Text = "Save && restart server", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        btnApply.Click += (_, __) =>
        {
            _settings.ListenAddress = string.IsNullOrWhiteSpace(_secListenAddress.Text) ? "+" : _secListenAddress.Text.Trim();
            _settings.AllowedDeviceIps = CollectCheckedIps();
            SaveSettings();
            if (_host.IsRunning) ToggleServer();
            ToggleServer();
            Log("security settings applied; server restarted");
        };

        var btnReserve = new Button { Text = "Reserve URL (admin)...", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
        btnReserve.Click += (_, __) =>
        {
            var addr = string.IsNullOrWhiteSpace(_secListenAddress.Text) ? "+" : _secListenAddress.Text.Trim();
            if (addr == "localhost")
            {
                MessageBox.Show(this, "Localhost binding does not require a URL reservation.", "Not needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var port = (int)_portInput.Value;
            var cmd = HttpHost.GetUrlAclCommand(addr, port);
            var ok = MessageBox.Show(this,
                $"Will run (with elevation prompt):\r\n\r\n  {cmd}\r\n\r\nProceed?",
                "Reserve URL", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", $"http add urlacl url=http://{addr}:{port}/ user=Everyone")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = false,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(10_000);
                Log($"netsh urlacl returned exit={p?.ExitCode}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to run netsh: " + ex.Message, "Reserve URL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var btnRow2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        btnRow2.Controls.Add(btnApply);
        btnRow2.Controls.Add(btnReserve);
        layout.Controls.Add(btnRow2, 0, 6);
        layout.SetColumnSpan(btnRow2, 3);

        tab.Controls.Add(layout);
        tabs.TabPages.Add(tab);

        // Seed list from saved settings + anything already seen.
        RefreshAllowedIpList();
    }

    /// <summary>
    /// Holds both the IP string and the last-seen metadata for display.
    /// </summary>
    private sealed record IpRow(string Ip, string Label)
    {
        public override string ToString() => Label;
    }

    private List<string> CollectCheckedIps()
    {
        var list = new List<string>();
        for (int i = 0; i < _secAllowedIps.Items.Count; i++)
        {
            if (_secAllowedIps.GetItemChecked(i) && _secAllowedIps.Items[i] is IpRow r)
                list.Add(r.Ip);
        }
        return list;
    }

    /// <summary>
    /// Rebuilds the CheckedListBox to show, in order:
    ///   1. Every currently-checked entry (preserve user state).
    ///   2. Every recently-seen client IP (auto-discovery).
    /// Items present in <see cref="AppSettings.AllowedDeviceIps"/> are ticked.
    /// </summary>
    private void RefreshAllowedIpList()
    {
        // Preserve the user's tick state for entries currently in the box.
        var ticked = new HashSet<string>(CollectCheckedIps());
        foreach (var saved in _settings.AllowedDeviceIps) ticked.Add(saved);

        var seen = _host.GetSeenClients();
        var allIps = new List<string>();
        foreach (var t in ticked) if (!allIps.Contains(t)) allIps.Add(t);
        foreach (var c in seen) if (!allIps.Contains(c.Ip)) allIps.Add(c.Ip);

        _suppressIpCheckPersist = true;
        _secAllowedIps.BeginUpdate();
        _secAllowedIps.Items.Clear();
        foreach (var ip in allIps)
        {
            var match = seen.FirstOrDefault(c => c.Ip == ip);
            string suffix;
            if (match != null)
            {
                var ago = (DateTime.UtcNow - match.LastSeenUtc).TotalSeconds;
                var when = ago < 60 ? $"{(int)ago}s ago" : $"{(int)(ago / 60)}m ago";
                suffix = $" -- last seen {when}, {match.Count} req, {(match.LastAccepted ? "OK" : "REJECTED")}";
            }
            else
            {
                suffix = " -- manual entry";
            }
            _secAllowedIps.Items.Add(new IpRow(ip, ip + suffix), ticked.Contains(ip));
        }
        _secAllowedIps.EndUpdate();
        _suppressIpCheckPersist = false;
    }

    private void AddSeenIpRow(string ip, DateTime? lastSeen, bool? accepted, bool checkedNow)
    {
        // Reuse existing entry if present.
        for (int i = 0; i < _secAllowedIps.Items.Count; i++)
            if (_secAllowedIps.Items[i] is IpRow r && r.Ip == ip)
            {
                _secAllowedIps.SetItemChecked(i, checkedNow);
                return;
            }
        _secAllowedIps.Items.Add(new IpRow(ip, ip + " -- manual entry"), checkedNow);
    }

    private void UpdateMentionsUiState()
    {
        var auto = _settings.MentionsAuto;
        _mChatsInput.Enabled = !auto;
        _mEmailsInput.Enabled = !auto;
    }

    private static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* swallow; user can open Settings manually */ }
    }

    private async Task RefreshMentionsAsync()
    {
        if (_mentionsReader is null)
        {
            _mStatus.Text = "Requires Windows 10 19041 or later.";
            return;
        }
        _mStatus.Text = "Refreshing...";
        _mentionsReader.NameAliases = _settings.MentionsNameAliases;
        await _mentionsReader.RefreshAsync();
        switch (_mentionsReader.Access)
        {
            case MentionsReader.AccessState.Denied:
                _mStatus.Text = "Access denied. Enable: Settings > Privacy & security > Notifications > Let apps access your notifications.";
                return;
            case MentionsReader.AccessState.Unsupported:
                _mStatus.Text = $"Unsupported: {_mentionsReader.LastError}";
                return;
            case MentionsReader.AccessState.Unknown:
                _mStatus.Text = "Access not granted yet — click Refresh again.";
                return;
        }
        // Always reflect the latest counts in settings + spinners so the
        // /config endpoint serves them to the device.  The "Auto" checkbox
        // only governs whether the 30 s timer fires; on explicit Refresh
        // we always persist.
        // New Teams bypasses the toast listener entirely (it draws its own
        // banners), so as a fallback we ask TeamsBadgeReader for the unread
        // count.  It returns >0 (OCR'd digit from the badge), 1 (presence
        // only), 0 (no badge), or -1 (error).
        // Use the Teams taskbar badge (OCR'd) as the canonical unread count.
        // It counts all unread activity (chats + channels + replies).
        int teams = _mentionsReader.TeamsCount;
        var (badge, badgeDiag) = await TeamsBadgeReader.TryGetUnreadCountWithDiagAsync();
        if (badge > teams) teams = badge;

        _settings.MentionsChats = teams;
        _settings.MentionsEmails = _mentionsReader.OutlookCount;
        _mChatsInput.Value = Math.Clamp(teams, 0, 9999);
        _mEmailsInput.Value = Math.Clamp(_mentionsReader.OutlookCount, 0, 9999);
        SaveSettings();
        int outlook = _mentionsReader.OutlookCount;
        int total = (_settings.MentionsTrackChats  ? Math.Max(0, teams)   : 0)
                  + (_settings.MentionsTrackEmails ? Math.Max(0, outlook) : 0);
        // "Chats" shows @mention / total-unread-on-taskbar so the user can
        // see both the actionable mention count and the broader activity.
        string chatsStr   = _settings.MentionsTrackChats
            ? teams.ToString()
            : "off";
        string emailsStr  = _settings.MentionsTrackEmails ? outlook.ToString() : "off";
        _mStatus.Text = $"@Mentions  Chats: {chatsStr}  Emails: {emailsStr}  Total: {total}    (badge {badge} {badgeDiag})    (last refresh {_mentionsReader.LastRefresh:HH:mm:ss})";
    }

    private CheckBox _mTrackChats = null!;
    private CheckBox _mTrackEmails = null!;
    private NumericUpDown _mChatsInput = null!;
    private NumericUpDown _mEmailsInput = null!;
    private TextBox _mAliases = null!;

    private void SetAllChecked(bool value)
    {
        foreach (var cb in _checks.Values) cb.Checked = value;
    }

    private void UpdateIpLabel()
    {
        var ips = string.Join(", ", HttpHost.GetLocalIPv4Addresses());
        _ipLabel.Text = string.IsNullOrEmpty(ips)
            ? "No IPv4 interfaces detected."
            : $"Device fetches on boot from:  {string.Join("  |  ", HttpHost.GetLocalIPv4Addresses().Select(ip => $"http://{ip}:{_portInput.Value}/config"))}";
    }

    private void ApplySettingsToUi()
    {
        _portInput.Value = _settings.Port;
        _portInput.ValueChanged += (_, __) => { _settings.Port = (int)_portInput.Value; UpdateIpLabel(); SaveSettings(); };
        foreach (var u in Utilities.All.Where(u => u.Implemented))
            _checks[u.Id].Checked = _settings.Enabled.Contains(u.Id);
        _mTrackChats.Checked = _settings.MentionsTrackChats;
        _mTrackEmails.Checked = _settings.MentionsTrackEmails;
        _mChatsInput.Value = Math.Clamp(_settings.MentionsChats, 0, 9999);
        _mEmailsInput.Value = Math.Clamp(_settings.MentionsEmails, 0, 9999);
        _mAuto.Checked = _settings.MentionsAuto;
        UpdateMentionsUiState();
        if (_settings.MentionsAuto) _ = RefreshMentionsAsync();
    }

    private void ToggleServer()
    {
        if (_host.IsRunning)
        {
            _host.Stop();
            _toggleBtn.Text = "Start server";
            _statusLabel.Text = "stopped";
            _statusLabel.ForeColor = Color.Firebrick;
        }
        else
        {
            try
            {
                _host.Start((int)_portInput.Value);
                _toggleBtn.Text = "Stop server";
                _statusLabel.Text = $"listening :{_host.Port}";
                _statusLabel.ForeColor = Color.ForestGreen;
                UpdateIpLabel();
            }
            catch (Exception ex)
            {
                Log("start failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Could not start HTTP server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }
}
