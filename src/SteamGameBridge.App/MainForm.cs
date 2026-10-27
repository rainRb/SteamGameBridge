using System.Diagnostics;
using System.Text.Json;
using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal sealed class MainForm : Form
{
    private readonly bool _demo;
    private readonly string _state;
    private readonly ProfileStore _store;
    private readonly WindowsThemeBinding _themeBinding;
    private readonly Func<bool> _protocolAvailable;
    private readonly GameList _profiles = new();
    private readonly ComboBox _preset = Choice(), _mode = Choice(), _libraries = Choice(), _accounts = Choice(), _entries = Choice();
    private readonly TextBox _name = Input(), _appId = Input(), _steam = Input(), _game = Input(), _gameArgs = Input(), _launcher = Input(), _template = Input(), _starwardExe = Input();
    private readonly NumericUpDown _wait = new() { Minimum = 1, Maximum = 1800, Value = 180, Width = 100 };
    private readonly CheckBox _starward = new() { Text = "使用 Starward 记录时长", AutoSize = true };
    private readonly CheckBox _maintenance = new() { Text = "退出 Steam 后自动维护", Checked = true, AutoSize = true };
    private readonly Label _starwardStatus = new() { AutoSize = true }, _mapping = new() { AutoSize = true, Text = "请获取或导入元数据" }, _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TextBox _preview = Output(), _log = Output();
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Label _profileTitle = new() { AutoSize = true };
    private readonly Label _feedback = new() { AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Tag = ThemeTone.Muted };
    private readonly Label _emptyProfiles = new() { Text = "保存配置后，游戏会显示在这里", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Tag = ThemeTone.Muted };
    private Control _launcherSection = null!, _starwardSection = null!;
    private TableLayoutPanel _localSection = null!;
    private int _argumentsRow, _waitRow;
    private CancellationTokenSource? _searchCancellation;
    private readonly List<Button> _actions = [];
    private readonly List<Control> _editors = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10000 };
    private readonly NotifyIcon _tray = new() { Text = "SteamGameBridge" };
    private readonly Dictionary<string, DateTimeOffset> _nextRepair = [];
    private readonly Dictionary<string, string> _lastLaunchStatus = [];
    private bool _working, _loading, _exit, _maintaining;
    private string _profileId = Guid.NewGuid().ToString("N");
    private string? _json;
    private SteamAppMetadata? _metadata;
    private (string Helper, string Placeholder)? _tools;
    private Button _apply = null!, _repair = null!, _restore = null!, _launch = null!, _save = null!, _delete = null!;
    private static TextBox Input() => new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, BorderStyle = BorderStyle.FixedSingle, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink, Margin = new Padding(0, 5, 0, 5) };
    private static TextBox Output() => new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = UiTheme.Surface, BorderStyle = BorderStyle.None, ForeColor = UiTheme.Ink };
    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, BackColor = UiTheme.Input, ForeColor = UiTheme.Ink, Margin = new Padding(0, 5, 0, 5) };
    private sealed record AccountRow(SteamAccount Value) { public override string ToString() => $"{Value.DisplayName} · {Value.AccountId}"; }

    public MainForm(bool demo, string? stateDirectory = null, Func<bool>? protocolAvailable = null, Func<bool>? darkMode = null)
    {
        _demo = demo;
        _protocolAvailable = protocolAvailable ?? (() => new WindowsStarwardTransport().ProtocolAvailable);
        _state = stateDirectory ?? (demo ? Path.Combine(Environment.GetEnvironmentVariable("SGB_DEMO_ROOT") ?? Path.Combine(Path.GetTempPath(), "SteamGameBridge-Demo-" + Guid.NewGuid().ToString("N")), "state") : LocalEnvironment.StateDirectory);
        _store = new(_state);
        Text = "SteamGameBridge · " + Application.ProductVersion.Split('+')[0] + (demo ? " · 只读演示" : "");
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(1180, 820); MinimumSize = new Size(980, 720); StartPosition = FormStartPosition.CenterScreen;
        ShowIcon = false; _tray.Icon = SystemIcons.Application;
        BackColor = UiTheme.Canvas; ForeColor = UiTheme.Ink;
        DoubleBuffered = true;
        BuildUi();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 SteamGameBridge", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("退出工具", null, (_, _) => { _exit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) =>
        {
            _searchCancellation?.Cancel();
            if (!_exit && !_demo && e.CloseReason == CloseReason.UserClosing && _store.Load().Any(p => p.Maintain))
            { e.Cancel = true; Hide(); _tray.Visible = true; _tray.ShowBalloonTip(3000, "工具仍在托盘运行", "仅在 Steam 和对应游戏退出后维护。可在托盘菜单中完全退出。", ToolTipIcon.Info); }
        };
        FormClosed += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await MaintainAsync();
        _themeBinding = new WindowsThemeBinding(this, darkMode, menu);
        Shown += async (_, _) => await RunAsync(InitializeAsync);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _searchCancellation?.Cancel(); _themeBinding?.Dispose(); _timer.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }

    private Button Action(string text, Func<Task> work, bool primary = false)
    {
        var b = new UiButton { Text = text, AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 0, 8, 0) };
        UiTheme.StyleButton(b, primary);
        b.Click += async (_, _) => await RunAsync(work); _actions.Add(b); return b;
    }
    private static Task Done(Action action) { action(); return Task.CompletedTask; }
    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(22, 16, 22, 12), BackColor = UiTheme.Canvas };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 76)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 28));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100));
        heading.RowStyles.Add(new(SizeType.Absolute, 37)); heading.RowStyles.Add(new(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "SteamGameBridge", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty });
        heading.Controls.Add(new Label { Text = "将本地游戏接入 Steam，管理启动与时长联动", ForeColor = UiTheme.Muted, Tag = ThemeTone.Muted, AutoSize = true, Margin = new Padding(1, 3, 0, 0) });
        root.Controls.Add(heading, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1120, 650), FixedPanel = FixedPanel.Panel1, Panel1MinSize = 240, SplitterDistance = 254, SplitterWidth = 16, BackColor = UiTheme.Canvas, Margin = Padding.Empty };
        var sidebar = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(14) };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = UiTheme.Surface, Margin = Padding.Empty };
        left.ColumnStyles.Add(new(SizeType.Percent, 100));
        left.RowStyles.Add(new(SizeType.Absolute, 38)); left.RowStyles.Add(new(SizeType.Percent, 100)); left.RowStyles.Add(new(SizeType.Absolute, 42));
        left.Controls.Add(new Label { Text = "我的游戏", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 2, 0, 0) }, 0, 0);
        var listArea = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
        listArea.Controls.Add(_profiles); listArea.Controls.Add(_emptyProfiles); _emptyProfiles.BringToFront(); left.Controls.Add(listArea, 0, 1);
        var listButtons = new TableLayoutPanel { Name = "ProfileActions", Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 7, 0, 0) };
        for (int column = 0; column < 3; column++) listButtons.ColumnStyles.Add(new(SizeType.Percent, 100f / 3));
        listButtons.RowStyles.Add(new(SizeType.Percent, 100));
        var add = Action("新增", () => Done(NewProfile)); _save = Action("保存", SaveAsync); _delete = Action("删除", DeleteAsync);
        Button[] profileActions = [add, _save, _delete];
        for (int column = 0; column < profileActions.Length; column++)
        {
            var button = profileActions[column]; button.AutoSize = false; button.Dock = DockStyle.Fill;
            button.Padding = new Padding(2); button.Margin = new Padding(column == 0 ? 0 : 3, 0, column == 2 ? 0 : 3, 0);
            listButtons.Controls.Add(button, column, 0);
        }
        left.Controls.Add(listButtons, 0, 2); sidebar.Controls.Add(left); split.Panel1.Controls.Add(sidebar);
        var detail = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 12) };
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = UiTheme.Surface, Margin = Padding.Empty };
        right.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var style in new[] { new RowStyle(SizeType.Absolute, 34), new RowStyle(SizeType.Absolute, 48), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.Absolute, 50), new RowStyle(SizeType.Absolute, 76) }) right.RowStyles.Add(style);
        _profileTitle.Font = new Font(Font.FontFamily, 12, FontStyle.Bold); _profileTitle.Margin = new Padding(2, 0, 0, 0); right.Controls.Add(_profileTitle, 0, 0);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = UiTheme.Surface, Margin = Padding.Empty };
        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty };
        Panel Page(string key, string title)
        {
            var page = new Panel { Name = key, Text = title, Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Surface, Padding = new Padding(2, 8, 12, 8), Visible = false };
            _pages.Add(key, page); pageHost.Controls.Add(page);
            var tab = new UiButton { Quiet = true, Text = title, AutoSize = true, Height = 34, Padding = new Padding(11, 5, 11, 5), Margin = new Padding(0, 0, 5, 0), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            tab.FlatAppearance.BorderSize = 0; tab.Click += (_, _) => ShowPage(key); navigation.Controls.Add(tab); _navigation.Add(key, tab);
            return page;
        }
        var settings = Page("GameSettings", "游戏设置"); var steam = Page("SteamSettings", "Steam 设置"); var integrations = Page("Integrations", "维护");
        var logs = Page("Logs", "运行日志"); var help = Page("Help", "使用说明");
        TableLayoutPanel Content(Panel page)
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, MaximumSize = new Size(980, 0), Margin = Padding.Empty };
            content.ColumnStyles.Add(new(SizeType.Percent, 100)); page.Controls.Add(content); return content;
        }
        var gameContent = Content(settings); var steamContent = Content(steam); var integrationContent = Content(integrations);
        TableLayoutPanel Section(TableLayoutPanel content, string title)
        {
            var section = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 4, 0, 14), Margin = new Padding(0, 0, 0, 10), RowCount = 1 };
            section.ColumnStyles.Add(new(SizeType.AutoSize)); section.ColumnStyles.Add(new(SizeType.Percent, 100)); section.ColumnStyles.Add(new(SizeType.AutoSize));
            section.RowStyles.Add(new(SizeType.Absolute, 34));
            var label = new Label { Text = title, Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            section.Controls.Add(label, 0, 0); section.SetColumnSpan(label, 3);
            content.Controls.Add(section); return section;
        }
        void Row(TableLayoutPanel section, string caption, Control value, Control? browse = null)
        {
            int row = section.RowCount++; section.RowStyles.Add(new(SizeType.Absolute, 42));
            section.Controls.Add(new Label { Text = caption, ForeColor = UiTheme.Muted, Tag = ThemeTone.Muted, AutoSize = true, MinimumSize = new Size(146, 0), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 14, 0) }, 0, row);
            Control field = value is TextBox or ComboBox or NumericUpDown ? new FieldFrame(value) : value;
            section.Controls.Add(field, 1, row); if (browse is null) section.SetColumnSpan(field, 2); else section.Controls.Add(browse, 2, row);
            if (value is not Label) _editors.Add(value); if (browse is not null) _editors.Add(browse);
        }
        Button Picker(TextBox box, bool directory = false) => Action("选择", () => Done(() =>
        {
            if (directory) { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) { box.Text = d.SelectedPath; LoadSteamChoices(); } }
            else { using var d = new OpenFileDialog { Filter = "EXE 程序|*.exe" }; if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName; }
        }));
        var identity = Section(gameContent, "游戏信息"); Row(identity, "配置名称", _name); Row(identity, "本地游戏预设", _preset); Row(identity, "Steam AppID", _appId);
        var local = Section(gameContent, "本地启动"); _localSection = local;
        var gameButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        gameButtons.Controls.Add(Action("自动检索", AutoSearchAsync)); gameButtons.Controls.Add(Picker(_game));
        Row(local, "实际游戏 EXE", _game, gameButtons); Row(local, "启动方式", _mode);
        Row(local, "游戏参数", _gameArgs); _argumentsRow = local.RowCount - 1;
        Row(local, "等待游戏（秒）", _wait); _waitRow = local.RowCount - 1;
        var launcher = Section(gameContent, "启动器选项"); _launcherSection = launcher;
        Row(launcher, "启动器 / 包装器", _launcher, Picker(_launcher)); Row(launcher, "启动器参数模板", _template);
        var binding = Section(steamContent, "Steam 账号与游戏库"); Row(binding, "Steam 安装目录", _steam, Picker(_steam, true)); Row(binding, "游戏库", _libraries); Row(binding, "目标 Steam 账号", _accounts); Row(binding, "Steam 启动入口", _entries);
        var starward = Section(gameContent, "Starward"); _starwardSection = starward;
        Row(starward, "Starward 计时", _starward); Row(starward, "Starward EXE（可选）", _starwardExe, Picker(_starwardExe)); Row(starward, "计时接口状态", _starwardStatus);
        var maintenance = Section(integrationContent, "维护"); Row(maintenance, "自动维护", _maintenance);
        var evidence = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        evidence.ColumnStyles.Add(new(SizeType.Percent, 100));
        evidence.RowStyles.Add(new(SizeType.Absolute, 30)); evidence.RowStyles.Add(new(SizeType.Percent, 50)); evidence.RowStyles.Add(new(SizeType.Absolute, 38)); evidence.RowStyles.Add(new(SizeType.Percent, 50));
        evidence.Controls.Add(new Label { Text = "接入预览", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0); evidence.Controls.Add(_preview, 0, 1);
        evidence.Controls.Add(new Label { Text = "运行记录", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 10, 0, 0) }, 0, 2); evidence.Controls.Add(_log, 0, 3); logs.Controls.Add(evidence);
        help.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = UiTheme.Surface, ForeColor = UiTheme.Muted, Tag = ThemeTone.Muted, ScrollBars = ScrollBars.Vertical, Text = UserGuide.Text.ReplaceLineEndings("\r\n") });
        right.Controls.Add(navigation, 0, 1); right.Controls.Add(pageHost, 0, 2);
        var metadataBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty };
        metadataBar.ColumnStyles.Add(new(SizeType.AutoSize)); metadataBar.ColumnStyles.Add(new(SizeType.Percent, 100));
        var metadataButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        metadataButtons.Controls.Add(Action("获取元数据", FetchAsync)); metadataButtons.Controls.Add(Action("导入", ImportAsync));
        _mapping.AutoSize = false; _mapping.Dock = DockStyle.Fill; _mapping.AutoEllipsis = true; _mapping.TextAlign = ContentAlignment.MiddleLeft; _mapping.ForeColor = UiTheme.Muted; _mapping.Tag = ThemeTone.Muted;
        metadataBar.Controls.Add(metadataButtons, 0, 0); metadataBar.Controls.Add(_mapping, 1, 0); right.Controls.Add(metadataBar, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.RowStyles.Add(new(SizeType.Absolute, 42)); footer.RowStyles.Add(new(SizeType.Percent, 100));
        var operations = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _apply = Action("一键接入", ApplyAsync, primary: true); _launch = Action("从 Steam 启动", LaunchAsync); _repair = Action("检查修复", RepairAsync); _restore = Action("恢复", RestoreAsync);
        var moreMenu = new ContextMenuStrip();
        void More(string title, Func<Task> work) => moreMenu.Items.Add(title).Click += async (_, _) => await RunAsync(work);
        More("管理员打开 Steam", ElevateSteamAsync); More("刷新预览", () => Done(UpdatePreview));
        More("打开备份目录", () => Done(() => { if (Directory.Exists(_state)) Process.Start(new ProcessStartInfo("explorer.exe", _state) { UseShellExecute = true }); }));
        More("退出工具", () => Done(() => { _exit = true; Close(); }));
        var more = new UiButton { Text = "更多操作", AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 0, 8, 0) };
        UiTheme.StyleButton(more); more.ContextMenuStrip = moreMenu; _actions.Add(more); more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));
        operations.Controls.AddRange([_apply, _launch, _repair, _restore, more]); footer.Controls.Add(operations, 0, 0); footer.Controls.Add(_feedback, 0, 1); right.Controls.Add(footer, 0, 4);
        detail.Controls.Add(right); split.Panel2.Controls.Add(detail); root.Controls.Add(split, 0, 1);
        _status.ForeColor = UiTheme.Muted; _status.Tag = ThemeTone.Muted; _status.TextAlign = ContentAlignment.MiddleLeft; _status.Margin = new Padding(3, 4, 0, 0); root.Controls.Add(_status, 0, 2); Controls.Add(root);
        ShowPage("GameSettings");
        _preset.Items.AddRange(GamePreset.All.Cast<object>().ToArray()); SetLaunchMode(LaunchMode.Direct);
        _profiles.SelectedIndexChanged += (_, _) => { if (!_loading && !_working && _profiles.SelectedItem is GameProfile p) LoadProfile(p); };
        _preset.SelectedIndexChanged += (_, _) => { if (_loading) return; var p = SelectedPreset; if (_name.Text == "新游戏" || GamePreset.All.Any(x => x.Name == _name.Text)) _name.Text = p.Name; _appId.Text = p.AppId; SetLaunchMode(p.DefaultMode); _starward.Checked = p.StarwardBiz is not null && _protocolAvailable(); RefreshLocks(); };
        _name.TextChanged += (_, _) => { if (!_loading) _profileTitle.Text = string.IsNullOrWhiteSpace(_name.Text) ? "新游戏配置" : _name.Text; };
        _mode.SelectedIndexChanged += (_, _) => { UpdateSections(); UpdateStarwardState(); };
        _starward.CheckedChanged += (_, _) => { if (!_loading) UpdateStarwardState(); };
        _entries.SelectedIndexChanged += (_, _) => { if (!_loading && _json is not null) { try { ParseSelectedMetadata(); UpdatePreview(); } catch (Exception ex) { Log(ex.Message); } } };
        _maintenance.CheckedChanged += (_, _) =>
        {
            if (_loading || _working || _demo) return;
            try
            {
                var saved = _store.Load().SingleOrDefault(p => p.Id == _profileId);
                if (saved is null || Service(saved).ReadReceipt() is null) return;
                saved.Maintain = _maintenance.Checked; _store.Save(saved); ReloadProfiles(saved.Id); Log("已保存此游戏的维护开关。");
            }
            catch (Exception ex) { Log(ex.Message); }
        };
        _appId.TextChanged += (_, _) => { if (!_loading) { _metadata = null; _json = null; _entries.Items.Clear(); _mapping.Text = "AppID 已改变，请重新获取元数据"; } };
    }
    private void ShowPage(string key)
    {
        foreach (var page in _pages) page.Value.Visible = page.Key == key;
        _pages[key].BringToFront();
        foreach (var item in _navigation)
        {
            bool selected = item.Key == key;
            ((UiButton)item.Value).Selected = selected;
            item.Value.BackColor = selected ? UiTheme.Selection : UiTheme.Surface;
            item.Value.ForeColor = selected ? UiTheme.SelectionInk : UiTheme.Muted;
        }
    }
    private void SetLaunchMode(LaunchMode selected)
    {
        _mode.BeginUpdate();
        try
        {
            _mode.Items.Clear(); _mode.Items.AddRange(["直接启动游戏", "官方启动器", "已有第三方包装器"]);
            if (SelectedPreset.StarwardBiz is not null) _mode.Items.Add("使用 Starward 启动");
            _mode.SelectedIndex = (int)selected < _mode.Items.Count ? (int)selected : 0;
        }
        finally { _mode.EndUpdate(); }
    }
    private void UpdateSections()
    {
        var mode = (LaunchMode)_mode.SelectedIndex;
        _launcherSection.Visible = mode is LaunchMode.OfficialLauncher or LaunchMode.ExistingWrapper;
        _starwardSection.Visible = SelectedPreset.StarwardBiz is not null;
        SetRowVisible(_localSection, _argumentsRow, mode != LaunchMode.Starward);
        SetRowVisible(_localSection, _waitRow, mode is LaunchMode.OfficialLauncher or LaunchMode.ExistingWrapper or LaunchMode.Starward);
    }
    private static void SetRowVisible(TableLayoutPanel section, int row, bool visible)
    {
        if (row >= section.RowStyles.Count) return;
        foreach (Control control in section.Controls) if (section.GetRow(control) == row) control.Visible = visible;
        section.RowStyles[row].Height = visible ? 42 : 0;
    }
    private async Task AutoSearchAsync()
    {
        if (_demo) throw new BridgeException("演示模式不检索本机目录。");
        var preset = SelectedPreset;
        var targets = GameFileSearch.TargetNames(preset.Id, _game.Text.Trim());
        using var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation;
        _feedback.ForeColor = UiTheme.Muted; _feedback.Tag = ThemeTone.Muted; _feedback.Text = "正在检索…";
        var libraries = _libraries.Items.Cast<string>().Select(path => Path.Combine(path, "steamapps", "common")).ToArray();
        try
        {
            var result = await Task.Run(() => GameFileSearch.Find(GameSearchRoots.Get(preset).Concat(libraries), targets, preset.StarwardBiz is not null, preset.StarwardBiz, cancellation.Token));
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            ApplySearchResult(result);
        }
        catch (OperationCanceledException) { if (!IsDisposed) _feedback.Text = "检索已取消。"; }
        finally { _searchCancellation = null; }
    }
    private void ApplySearchResult(GameSearchResult result)
    {
        if (result.StarwardExecutables.Count == 1 && string.IsNullOrWhiteSpace(_starwardExe.Text)) _starwardExe.Text = result.StarwardExecutables[0];
        if (result.Games.Count == 0) { _feedback.Text = "未找到游戏，请手动选择。"; return; }
        string path;
        if (result.Games.Count == 1) path = result.Games[0];
        else
        {
            using var dialog = new GameSelectionDialog(result.Games, _themeBinding.ReadPreference);
            if (dialog.ShowDialog(this) != DialogResult.OK) { _feedback.Text = ""; return; }
            path = dialog.SelectedPath;
        }
        _game.Text = path; _feedback.Text = "已找到游戏。"; Log("检索结果：" + path);
    }
    private void PresentError(string message)
    {
        _feedback.Tag = ThemeTone.Error; _feedback.ForeColor = UiTheme.Error; _feedback.Text = message; Log(message);
    }
    private GamePreset SelectedPreset => _preset.SelectedItem as GamePreset ?? GamePreset.Find("custom");
    private Task InitializeAsync()
    {
        ReloadProfiles();
        if (_profiles.Items.Count > 0) { _profiles.SelectedIndex = 0; LoadProfile((GameProfile)_profiles.Items[0]); }
        else NewProfile();
        if (_demo) Log("演示模式：所有接入、保存、修复、恢复、启动操作均禁用；不会读取真实 Steam 配置。");
        else Log("请先确认已正常入库所选 Steam 条目。无需下载 Steam 游戏资源，但 Steam 自身仍可能触发下载。");
        _timer.Start(); return Task.CompletedTask;
    }
    private void NewProfile()
    {
        _loading = true; _profiles.SelectedIndex = -1; _profileId = Guid.NewGuid().ToString("N");
        _name.Text = GamePreset.All[0].Name; _preset.SelectedIndex = 0; _appId.Text = SelectedPreset.AppId; _game.Clear(); _gameArgs.Clear(); _launcher.Clear(); _template.Clear(); _starwardExe.Clear();
        SetLaunchMode(SelectedPreset.DefaultMode); _wait.Value = 180; _starward.Checked = false; _maintenance.Checked = true;
        _metadata = null; _json = null; _entries.Items.Clear(); _mapping.Text = "请获取或导入元数据";
        if (!_demo) { try { _steam.Text = LocalEnvironment.SteamRoot(); LoadSteamChoices(); } catch (Exception ex) { Log(ex.Message + " 可手动选择 Steam 安装目录。"); } }
        _loading = false; UpdateStarwardState(); RefreshLocks();
    }
    private void LoadSteamChoices(string? library = null, string? accountPath = null)
    {
        if (_demo) return;
        _libraries.Items.Clear(); _accounts.Items.Clear();
        foreach (var path in SteamDiscovery.ReadLibraries(_steam.Text.Trim())) _libraries.Items.Add(path);
        if (library is not null) _libraries.SelectedItem = library; else if (_libraries.Items.Count > 0) _libraries.SelectedIndex = 0;
        var accounts = SteamDiscovery.ReadAccounts(_steam.Text.Trim());
        foreach (var account in accounts.Accounts) _accounts.Items.Add(new AccountRow(account));
        for (int i = 0; i < _accounts.Items.Count; i++) if (_accounts.Items[i] is AccountRow row && (accountPath is not null ? row.Value.LocalConfigPath.Equals(accountPath, StringComparison.OrdinalIgnoreCase) : row.Value.AccountId == accounts.SuggestedAccount?.AccountId)) _accounts.SelectedIndex = i;
    }
    private void ReloadProfiles(string? selectId = null)
    {
        _loading = true; _profiles.Items.Clear();
        foreach (var p in _store.Load()) _profiles.Items.Add(p);
        if (selectId is not null) for (int i = 0; i < _profiles.Items.Count; i++) if (((GameProfile)_profiles.Items[i]).Id == selectId) _profiles.SelectedIndex = i;
        _loading = false; _emptyProfiles.Visible = _profiles.Items.Count == 0;
    }
    private void LoadProfile(GameProfile p)
    {
        try
        {
            _loading = true; _profileId = p.Id; _name.Text = p.Name; _appId.Text = p.AppId; _steam.Text = p.Request.SteamRoot;
            _preset.SelectedItem = GamePreset.Find(p.Request.PresetId); _game.Text = p.Request.NativeExe; _gameArgs.Text = p.Request.GameArguments;
            SetLaunchMode(p.Request.LaunchMode); _launcher.Text = p.Request.LauncherExe; _template.Text = p.Request.LauncherArguments; _wait.Value = p.Request.WaitSeconds;
            _starward.Checked = p.Request.Starward?.Enabled == true; _starwardExe.Text = p.Request.Starward?.Executable ?? ""; _maintenance.Checked = p.Maintain;
            LoadSteamChoices(p.Request.LibraryRoot, p.Request.AccountConfigPath);
            _json = null; _metadata = Service(p).ReadReceipt()?.Metadata; _entries.Items.Clear();
            if (_metadata is not null) { _entries.Items.AddRange(_metadata.LaunchEntries.Cast<object>().ToArray()); _entries.SelectedItem = _metadata.LaunchEntries.FirstOrDefault(x => x.Id == _metadata.LaunchId); }
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { _loading = false; UpdateStarwardState(); UpdatePreview(); RefreshLocks(); }
    }
    private SteamBridgeService Service(GameProfile p) => new(new() { StateDirectory = _state, SteamRoot = p.Request.SteamRoot, AppId = p.AppId, RegisteredLibraries = SteamDiscovery.ReadLibraries(p.Request.SteamRoot), IsBusy = () => LocalEnvironment.Busy(p) });
    private GameProfile CaptureProfile()
    {
        SteamAppMetadata.ValidateAppId(_appId.Text.Trim());
        if (_libraries.SelectedItem is not string library || _accounts.SelectedItem is not AccountRow account) throw new BridgeException("请明确选择 Steam 游戏库及目标账号。");
        _tools ??= LocalEnvironment.StageTools(_state);
        var request = new SetupRequest(_steam.Text.Trim(), library, account.Value.LocalConfigPath, _game.Text.Trim(), _tools.Value.Helper, _tools.Value.Placeholder, (LaunchMode)_mode.SelectedIndex == LaunchMode.Starward ? "" : _gameArgs.Text)
        { PresetId = SelectedPreset.Id, LaunchMode = (LaunchMode)_mode.SelectedIndex, LauncherExe = _launcher.Text.Trim(), LauncherArguments = _template.Text, WaitSeconds = (int)_wait.Value,
            Starward = SelectedPreset.StarwardBiz is null ? null : new(_starward.Checked, string.IsNullOrWhiteSpace(_starwardExe.Text) ? null : _starwardExe.Text.Trim()) };
        LaunchConfiguration.Validate(request);
        return new() { Id = _profileId, Name = string.IsNullOrWhiteSpace(_name.Text) ? SelectedPreset.Name : _name.Text.Trim(), AppId = _appId.Text.Trim(), LaunchId = _metadata?.LaunchId ?? "", Request = request, Maintain = _maintenance.Checked };
    }
    private GameProfile Current() => _store.Load().SingleOrDefault(p => p.Id == _profileId) is { } saved && Service(saved).ReadReceipt() is not null ? saved : CaptureProfile();
    private void RequireLive() { if (_demo) throw new BridgeException("只读演示模式不会执行此操作。"); }
    private Task SaveAsync()
    {
        RequireLive(); var p = CaptureProfile();
        if (_store.Load().SingleOrDefault(x => x.Id == p.Id) is { } old && Service(old).ReadReceipt() is not null) throw new BridgeException("请先恢复接入，再修改此游戏设置。");
        _store.Save(p); ReloadProfiles(p.Id); Log("已保存本游戏设置。"); return Task.CompletedTask;
    }
    private Task DeleteAsync()
    {
        RequireLive(); var p = _store.Load().SingleOrDefault(x => x.Id == _profileId) ?? throw new BridgeException("此配置尚未保存。");
        if (Service(p).ReadReceipt() is not null) throw new BridgeException("请先恢复接入；删除设置不能代替恢复。");
        _store.Delete(p.Id); ReloadProfiles(); NewProfile(); Log("已删除游戏设置，历史备份仍保留。"); return Task.CompletedTask;
    }
    private async Task<string> DownloadAsync(string appId)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        return await http.GetStringAsync("https://api.steamcmd.net/v1/info/" + appId);
    }
    private async Task FetchAsync()
    {
        if (_demo) throw new BridgeException("演示模式可导入元数据快照。");
        SteamAppMetadata.ValidateAppId(_appId.Text.Trim()); await SetMetadataJson(await DownloadAsync(_appId.Text.Trim()));
        Log("已获取公开元数据，未下载游戏资源。数据源：api.steamcmd.net（第三方镜像）。");
    }
    private async Task ImportAsync()
    {
        using var picker = new OpenFileDialog { Filter = "Steam app-info JSON|*.json" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) throw new IOException("元数据文件过大。");
        await SetMetadataJson(await File.ReadAllTextAsync(picker.FileName)); Log("已导入快照，尚未在线确认是否最新。");
    }
    private Task SetMetadataJson(string json)
    {
        var entries = SteamAppMetadata.ReadLaunchChoices(json, _appId.Text.Trim());
        string? previous = _metadata?.LaunchId; _metadata = null; _json = json; _loading = true; _entries.Items.Clear(); _entries.Items.AddRange(entries.Cast<object>().ToArray());
        if (entries.Count == 1) _entries.SelectedIndex = 0; else if (previous is not null) _entries.SelectedItem = entries.FirstOrDefault(x => x.Id == previous);
        _loading = false;
        if (_entries.SelectedItem is SteamLaunchEntry) ParseSelectedMetadata();
        else { _mapping.Text = "请选择 Steam 启动入口"; ShowPage("SteamSettings"); Log(_mapping.Text); }
        return Task.CompletedTask;
    }
    private void ParseSelectedMetadata()
    {
        if (_json is null || _entries.SelectedItem is not SteamLaunchEntry entry) throw new BridgeException("请选择 Windows 启动入口。");
        _metadata = SteamAppMetadata.ParseApiSteamCmdJson(_json, _appId.Text.Trim(), entry.Id);
        _mapping.Text = $"Steam：{_metadata.Name} ({_metadata.AppId}) → {SelectedPreset.Name}";
    }
    private Task ApplyAsync()
    {
        RequireLive(); var p = CaptureProfile(); var m = _metadata ?? throw new BridgeException("请获取元数据并选择启动入口。"); var service = Service(p);
        var original = ValveKeyValues.ReadLaunchOptions(File.ReadAllText(p.Request.AccountConfigPath), p.AppId);
        var preview = service.Preview(p.Request, m);
        using var dialog = new LaunchOptionsDialog(original.Value, p.Request.GameArguments, preview.LaunchOptions, _themeBinding.ReadPreference, p.Request.LaunchMode != LaunchMode.Starward);
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
        if (dialog.SelectedAction == ExistingLaunchOptionsAction.Keep) { Log("已保留原设置，本次未接入。"); return Task.CompletedTask; }
        p.Request = p.Request with { GameArguments = dialog.GameArguments, ExistingOptionsAction = dialog.SelectedAction, OriginalOptionsReviewed = true, ExpectedOriginalLaunchOptions = original.Value };
        LaunchConfiguration.Validate(p.Request);
        _store.Save(p); ReloadProfiles(p.Id);
        var result = service.Apply(p.Request, m); Log(result.Message); LoadProfile(p); _nextRepair[p.Id] = DateTimeOffset.MaxValue; return Task.CompletedTask;
    }
    private async Task RepairAsync()
    {
        RequireLive(); var p = Current(); string json = await DownloadAsync(p.AppId);
        var receipt = Service(p).ReadReceipt() ?? throw new BridgeException("尚未接入。");
        var metadata = SteamAppMetadata.ParseApiSteamCmdJson(json, p.AppId, receipt.Metadata.LaunchId);
        var result = Service(p).Repair(metadata); _metadata = metadata; Log(result.Message); _nextRepair[p.Id] = DateTimeOffset.MaxValue;
    }
    private Task RestoreAsync() { RequireLive(); var p = Current(); Log(Service(p).Restore().Message); return Task.CompletedTask; }
    private Task LaunchAsync()
    {
        RequireLive(); var p = Current(); var s = Service(p); var inspection = s.Inspect();
        if (!inspection.Installed || s.ReadReceipt()?.Phase != "active" || inspection.Conflicts.Count > 0) throw new BridgeException("请先在 Steam 退出后检查并修复接入。");
        if (!LocalEnvironment.SteamRunning()) throw new BridgeException("请先打开 Steam 并登录所选账号。");
        string account = Directory.GetParent(Path.GetDirectoryName(p.Request.AccountConfigPath)!)!.Name;
        if (LocalEnvironment.ActiveSteamAccount() != account) throw new BridgeException("Steam 当前登录账号与配置账号不同或尚未完成登录。");
        if (NativeLauncher.BridgeRunning(p.AppId) || GameProcessTracker.IsRunning(p.Request.NativeExe)) throw new BridgeException("本游戏或桥接会话已运行。");
        Process.Start(new ProcessStartInfo(Path.Combine(p.Request.SteamRoot, "steam.exe"), "-applaunch " + p.AppId) { UseShellExecute = true })?.Dispose();
        Log("已交给 Steam 正式条目启动；如使用官方启动器，请在其中点击开始。"); return Task.CompletedTask;
    }
    private Task ElevateSteamAsync()
    {
        RequireLive(); if (LocalEnvironment.SteamRunning()) throw new BridgeException("请先从 Steam 菜单完全退出。工具不会强制结束进程。");
        Process.Start(new ProcessStartInfo(Path.Combine(_steam.Text.Trim(), "steam.exe")) { UseShellExecute = true, Verb = "runas" })?.Dispose(); return Task.CompletedTask;
    }
    private async Task MaintainAsync()
    {
        if (_working || _maintaining || _demo) return;
        _maintaining = true;
        try
        {
            foreach (var p in _store.Load())
            {
                if (_working || IsDisposed) return;
                try
                {
                    var service = Service(p); var receipt = service.ReadReceipt(); if (receipt is null) continue;
                    string statusFile = Path.Combine(service.State, "launch-status.json");
                    if (File.Exists(statusFile))
                    {
                        string text = File.ReadAllText(statusFile);
                        if (_lastLaunchStatus.GetValueOrDefault(p.Id) != text)
                        {
                            var status = JsonSerializer.Deserialize<LaunchStatus>(text);
                            if (status is not null) Log(p.Name + "：" + status.Message);
                            _lastLaunchStatus[p.Id] = text;
                            if (p.Id == _profileId) UpdateStarwardState();
                        }
                    }
                    if (LocalEnvironment.Busy(p)) { _nextRepair[p.Id] = DateTimeOffset.MinValue; continue; }
                    if (!p.Maintain || _nextRepair.GetValueOrDefault(p.Id) > DateTimeOffset.Now) continue;
                    _nextRepair[p.Id] = DateTimeOffset.Now.AddMinutes(5);
                    var metadata = SteamAppMetadata.ParseApiSteamCmdJson(await DownloadAsync(p.AppId), p.AppId, receipt.Metadata.LaunchId);
                    if (_working || IsDisposed) return;
                    // Ordinary polling never disables or redraws the editor. Only an actual
                    // repair enters the foreground operation guard after metadata is ready.
                    await RunAsync(() => Done(() =>
                    {
                        service.Repair(metadata); _nextRepair[p.Id] = DateTimeOffset.MaxValue;
                        Log(p.Name + "：托盘维护完成。");
                    }));
                }
                catch (Exception ex) { _nextRepair[p.Id] = DateTimeOffset.Now.AddMinutes(5); Log(p.Name + "：维护停止，" + ex.Message); }
            }
            if (!_working && !IsDisposed) UpdateStarwardState();
        }
        catch (Exception ex) { Log("维护检查：" + ex.Message); }
        finally { _maintaining = false; }
    }
    private void UpdatePreview()
    {
        if (_demo) { _preview.Text = "只读演示模式：不会接入真实 Steam。"; return; }
        try
        {
            var p = Current(); var service = Service(p); var receipt = service.ReadReceipt();
            var m = receipt?.Metadata ?? _metadata ?? throw new BridgeException("请先获取或导入元数据。");
            var preview = receipt?.Preview ?? service.Preview(p.Request, m);
            string? original = ValveKeyValues.ReadLaunchOptions(File.ReadAllText(p.Request.AccountConfigPath), p.AppId).Value;
            _mapping.Text = $"Steam：{m.Name} ({m.AppId}) → {p.Name}";
            _preview.Text = $"Steam 条目：{m.Name} ({m.AppId})\r\n本地游戏：{p.Request.NativeExe}\r\n启动方式：{_mode.Text}\r\n\r\n原启动项：{original ?? "（未设置）"}\r\n\r\n预计启动项：\r\n{preview.LaunchOptions}\r\n\r\n游戏参数：{p.Request.GameArguments}\r\n启动器参数模板：{p.Request.LauncherArguments}\r\n\r\n安装清单：{preview.ManifestPath}\r\n占位目录：{preview.PlaceholderDirectory}\r\n备份：{service.State}\r\nBuild：{m.BuildId}\r\n\r\n{string.Join("\r\n", preview.Warnings)}";
        }
        catch (Exception ex) { _preview.Text = ex.Message; }
    }
    private void UpdateStarwardState()
    {
        bool supported = SelectedPreset.StarwardBiz is not null;
        bool enabled = _starward.Checked || (LaunchMode)_mode.SelectedIndex == LaunchMode.Starward;
        string text = !supported ? "此预设不启用" : !enabled ? "已关闭 Starward 计时"
            : _protocolAvailable() ? "协议可用" : "协议不可用；可指定 Starward EXE";
        if (!_demo && supported && _starward.Checked)
        {
            try
            {
                var p = _store.Load().SingleOrDefault(x => x.Id == _profileId);
                if (p is not null)
                {
                    string file = Path.Combine(Service(p).State, "launch-status.json"); LocalEnvironment.GuardPath(file);
                    if (File.Exists(file) && new FileInfo(file).Length < 128 * 1024)
                    {
                        var status = JsonSerializer.Deserialize<LaunchStatus>(File.ReadAllBytes(file));
                        if (status?.StarwardState == "sent") text = "已发送计时请求";
                        if (status?.StarwardState == "unavailable") text = "Starward 不可用";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        if (_starwardStatus.Text != text) _starwardStatus.Text = text;
    }
    private void RefreshLocks()
    {
        bool active = false;
        try { var saved = _store.Load().SingleOrDefault(x => x.Id == _profileId); active = saved is not null && Service(saved).ReadReceipt() is not null; } catch (Exception ex) { _status.Text = ex.Message; }
        UpdateStarwardState(); UpdateSections();
        _profileTitle.Text = string.IsNullOrWhiteSpace(_name.Text) ? "新游戏配置" : _name.Text;
        _emptyProfiles.Visible = _profiles.Items.Count == 0;
        foreach (var c in _editors) c.Enabled = !active && !_working;
        if (!active && !_working) _starward.Enabled = SelectedPreset.StarwardBiz is not null;
        _save.Enabled = _delete.Enabled = !_demo && !active && !_working;
        _apply.Enabled = !_demo && !active && !_working; _repair.Enabled = _restore.Enabled = _launch.Enabled = !_demo && active && !_working;
        _maintenance.Enabled = !_demo && !_working;
        _profiles.Enabled = !_working;
        _status.Text = _demo ? "演示模式 · 不写入真实配置" : active ? "此游戏已接入；修改路径、参数或 AppID 前请先恢复。" : "尚未接入 · 设置可编辑";
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_working) return; _feedback.Text = ""; _working = true; foreach (var a in _actions) a.Enabled = false; _profiles.Enabled = false; foreach (var c in _editors) c.Enabled = false;
        try { await action(); }
        catch (UnauthorizedAccessException) { PresentError("访问被拒绝，请使用有权限的目录或以管理员身份打开。"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { PresentError("已取消 Windows 授权。"); }
        catch (Exception ex) { PresentError(ex.Message); }
        finally { _working = false; if (!IsDisposed) { foreach (var a in _actions) a.Enabled = true; RefreshLocks(); UpdatePreview(); } }
    }
    private void Log(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n");
}
