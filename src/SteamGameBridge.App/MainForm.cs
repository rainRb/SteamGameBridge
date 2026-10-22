using System.Diagnostics;
using System.Text.Json;
using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal sealed class MainForm : Form
{
    private readonly bool _demo;
    private readonly string _state;
    private readonly ProfileStore _store;
    private readonly ListBox _profiles = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _preset = Choice(), _mode = Choice(), _libraries = Choice(), _accounts = Choice(), _entries = Choice();
    private readonly TextBox _name = Input(), _appId = Input(), _steam = Input(), _game = Input(), _gameArgs = Input(), _launcher = Input(), _template = Input(), _starwardExe = Input();
    private readonly NumericUpDown _wait = new() { Minimum = 1, Maximum = 1800, Value = 180, Width = 100 };
    private readonly CheckBox _starward = new() { Text = "使用 Starward 记录时长", AutoSize = true };
    private readonly CheckBox _maintenance = new() { Text = "在本工具运行期间维护此游戏（不随开机启动）", Checked = true, AutoSize = true };
    private readonly Label _starwardStatus = new() { AutoSize = true }, _mapping = new() { AutoSize = true, Text = "请获取或导入元数据" }, _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TextBox _preview = Output(), _log = Output();
    private readonly List<Button> _actions = [];
    private readonly List<Control> _editors = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10000 };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Text = "SteamGameBridge" };
    private readonly Dictionary<string, DateTimeOffset> _nextRepair = [];
    private readonly Dictionary<string, string> _lastLaunchStatus = [];
    private bool _working, _loading, _exit;
    private string _profileId = Guid.NewGuid().ToString("N");
    private string? _json;
    private SteamAppMetadata? _metadata;
    private (string Helper, string Placeholder)? _tools;
    private Button _apply = null!, _repair = null!, _restore = null!, _launch = null!, _save = null!, _delete = null!;
    private static TextBox Input() => new() { Dock = DockStyle.Fill };
    private static TextBox Output() => new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private sealed record AccountRow(SteamAccount Value) { public override string ToString() => $"{Value.DisplayName} · {Value.AccountId}"; }

    public MainForm(bool demo)
    {
        _demo = demo;
        _state = demo ? Path.Combine(Environment.GetEnvironmentVariable("SGB_DEMO_ROOT") ?? Path.Combine(Path.GetTempPath(), "SteamGameBridge-Demo-" + Guid.NewGuid().ToString("N")), "state") : LocalEnvironment.StateDirectory;
        _store = new(_state);
        Text = "SteamGameBridge · 0.2.0-dev" + (demo ? " · 只读演示" : "");
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(1140, 850); MinimumSize = new Size(950, 730); StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 SteamGameBridge", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("退出工具", null, (_, _) => { _exit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) =>
        {
            if (!_exit && !_demo && e.CloseReason == CloseReason.UserClosing && _store.Load().Any(p => p.Maintain))
            { e.Cancel = true; Hide(); _tray.Visible = true; _tray.ShowBalloonTip(3000, "工具仍在托盘运行", "仅在 Steam 和对应游戏退出后维护。可在托盘菜单中完全退出。", ToolTipIcon.Info); }
        };
        FormClosed += (_, _) => { _timer.Stop(); _tray.Dispose(); };
        _timer.Tick += async (_, _) => await MaintainAsync();
        Shown += async (_, _) => await RunAsync(InitializeAsync);
    }
    private Button Action(string text, Func<Task> work)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(3) };
        b.Click += async (_, _) => await RunAsync(work); _actions.Add(b); return b;
    }
    private static Task Done(Action action) { action(); return Task.CompletedTask; }
    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        root.RowStyles.Add(new(SizeType.Absolute, 43)); root.RowStyles.Add(new(SizeType.Absolute, 58)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 32));
        root.Controls.Add(new Label { Text = "SteamGameBridge", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true }, 0, 0);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "把已有本地游戏接入 Steam 正式条目。先正常入库对应游戏；接入、修复、恢复前完全退出 Steam。\nSteam 自身仍可能下载资源。运行状态、时长与 Overlay 需要分别实测。", Padding = new Padding(4, 7, 4, 3), BackColor = Color.AliceBlue }, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1100, 650), FixedPanel = FixedPanel.Panel1, SplitterDistance = 235 };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        left.RowStyles.Add(new(SizeType.Percent, 100)); left.RowStyles.Add(new(SizeType.Absolute, 78)); left.Controls.Add(_profiles, 0, 0);
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        listButtons.Controls.Add(Action("新增游戏", () => Done(NewProfile)));
        _save = Action("保存设置", SaveAsync); _delete = Action("删除设置", DeleteAsync); listButtons.Controls.AddRange([_save, _delete]);
        left.Controls.Add(listButtons, 0, 1); split.Panel1.Controls.Add(left);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new(SizeType.Percent, 100)); right.RowStyles.Add(new(SizeType.Absolute, 44)); right.RowStyles.Add(new(SizeType.Absolute, 72));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var settings = new TabPage("游戏设置") { AutoScroll = true };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 125)); fields.ColumnStyles.Add(new(SizeType.Percent, 100)); fields.ColumnStyles.Add(new(SizeType.Absolute, 84));
        void Row(string caption, Control value, Button? browse = null)
        {
            int row = fields.RowCount++; fields.RowStyles.Add(new(SizeType.Absolute, 36));
            fields.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            fields.Controls.Add(value, 1, row); if (browse is null) fields.SetColumnSpan(value, 2); else fields.Controls.Add(browse, 2, row);
            _editors.Add(value); if (browse is not null) _editors.Add(browse);
        }
        Button Picker(TextBox box, bool directory = false) => Action("选择…", () => Done(() =>
        {
            if (directory) { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) { box.Text = d.SelectedPath; LoadSteamChoices(); } }
            else { using var d = new OpenFileDialog { Filter = "EXE 程序|*.exe" }; if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName; }
        }));
        Row("配置名称", _name); Row("本地游戏预设", _preset); Row("Steam AppID", _appId);
        Row("Steam 安装目录", _steam, Picker(_steam, true)); Row("游戏库", _libraries); Row("目标 Steam 账号", _accounts);
        Row("实际游戏 EXE", _game, Picker(_game)); Row("游戏参数", _gameArgs); Row("启动方式", _mode);
        Row("启动器 / 包装器", _launcher, Picker(_launcher)); Row("启动器参数模板", _template); Row("等待游戏（秒）", _wait);
        Row("Steam 启动入口", _entries); Row("Starward 计时", _starward); Row("Starward EXE（可选）", _starwardExe, Picker(_starwardExe));
        Row("计时接口状态", _starwardStatus); Row("自动维护", _maintenance);
        settings.Controls.Add(fields); tabs.TabPages.Add(settings);
        var evidence = new TabPage("接入预览与运行日志");
        var evidenceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        evidenceLayout.RowStyles.Add(new(SizeType.Percent, 52)); evidenceLayout.RowStyles.Add(new(SizeType.Percent, 48));
        evidenceLayout.Controls.Add(_preview, 0, 0); evidenceLayout.Controls.Add(_log, 0, 1); evidence.Controls.Add(evidenceLayout); tabs.TabPages.Add(evidence);
        var help = new TabPage("使用说明");
        help.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = "1. 新增游戏，选择本地预设；Steam AppID 可以修改。\r\n2. 选择真正的游戏 EXE，选择游戏库与账号。\r\n3. 官方启动器模式分别选择启动器与游戏；打开后可手动点击开始。\r\n4. 包装器的参数模板支持独立参数 {game}、{gameArgs}、{steamCommand}。\r\n   Better Endfield 示例：--hide-console --game {game} -- {gameArgs} --steam-command {steamCommand}\r\n   只有显式填写 {steamCommand} 才会向启动器/包装器转交 Steam 原始命令。\r\n5. 获取元数据；多个 Windows 启动入口需自行选择。\r\n6. 完全退出 Steam 后一键接入，检查原启动项及最终参数。\r\n7. 重新打开 Steam，登录所选账号，从正式游戏条目启动。\r\n8. 有需要时恢复，再编辑设置重新接入。\r\n\r\n绝区零/崩坏3可交给 Starward 记录时长。选择的是本地国服预设，不由 Steam AppID 推断。\r\n已发送请求不代表记录成功，请在 Starward 自行验收。\r\n启动失败不会终止游戏或启动器。不要保存登录票据、authkey 或其他敏感参数。\r\n\r\n关闭窗口后，启用维护的配置会在托盘继续工作；没有开机自启动。" }); tabs.TabPages.Add(help);
        right.Controls.Add(tabs, 0, 0);
        var metadataBar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        metadataBar.Controls.Add(Action("获取元数据", FetchAsync)); metadataBar.Controls.Add(Action("导入元数据", ImportAsync)); metadataBar.Controls.Add(_mapping); right.Controls.Add(metadataBar, 0, 1);
        var operations = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _apply = Action("一键接入", ApplyAsync); _repair = Action("检查并修复", RepairAsync); _restore = Action("恢复", RestoreAsync); _launch = Action("从 Steam 启动", LaunchAsync);
        operations.Controls.AddRange([_apply, _repair, _restore, _launch, Action("管理员打开 Steam", ElevateSteamAsync), Action("刷新预览", () => Done(UpdatePreview)), Action("打开备份", () => Done(() => { if (Directory.Exists(_state)) Process.Start(new ProcessStartInfo("explorer.exe", _state) { UseShellExecute = true }); })), Action("退出工具", () => Done(() => { _exit = true; Close(); }))]);
        right.Controls.Add(operations, 0, 2); split.Panel2.Controls.Add(right); root.Controls.Add(split, 0, 2); root.Controls.Add(_status, 0, 3); Controls.Add(root);
        _preset.Items.AddRange(GamePreset.All.Cast<object>().ToArray()); _mode.Items.AddRange(["直接启动游戏", "官方启动器", "已有第三方包装器"]);
        _profiles.SelectedIndexChanged += (_, _) => { if (!_loading && !_working && _profiles.SelectedItem is GameProfile p) LoadProfile(p); };
        _preset.SelectedIndexChanged += (_, _) => { if (_loading) return; var p = SelectedPreset; if (_name.Text == "新游戏" || GamePreset.All.Any(x => x.Name == _name.Text)) _name.Text = p.Name; _appId.Text = p.AppId; _mode.SelectedIndex = (int)p.DefaultMode; _starward.Checked = p.StarwardBiz is not null && new WindowsStarwardTransport().ProtocolAvailable; UpdateStarwardState(); };
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
        _mode.SelectedIndex = (int)SelectedPreset.DefaultMode; _wait.Value = 180; _starward.Checked = false; _maintenance.Checked = true;
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
        _loading = false;
    }
    private void LoadProfile(GameProfile p)
    {
        try
        {
            _loading = true; _profileId = p.Id; _name.Text = p.Name; _appId.Text = p.AppId; _steam.Text = p.Request.SteamRoot;
            _preset.SelectedItem = GamePreset.Find(p.Request.PresetId); _game.Text = p.Request.NativeExe; _gameArgs.Text = p.Request.GameArguments;
            _mode.SelectedIndex = (int)p.Request.LaunchMode; _launcher.Text = p.Request.LauncherExe; _template.Text = p.Request.LauncherArguments; _wait.Value = p.Request.WaitSeconds;
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
        var request = new SetupRequest(_steam.Text.Trim(), library, account.Value.LocalConfigPath, _game.Text.Trim(), _tools.Value.Helper, _tools.Value.Placeholder, _gameArgs.Text)
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
        else { _mapping.Text = "存在多个 Windows 入口，请在设置中选择"; Log(_mapping.Text); }
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
        using var dialog = new LaunchOptionsDialog(original.Value, p.Request.GameArguments, preview.LaunchOptions);
        if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
        if (dialog.SelectedAction == ExistingLaunchOptionsAction.Keep) { Log("已保留原设置，本次未接入。"); return Task.CompletedTask; }
        p.Request = p.Request with { GameArguments = dialog.GameArguments, ExistingOptionsAction = dialog.SelectedAction, OriginalOptionsReviewed = true, ExpectedOriginalLaunchOptions = original.Value };
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
        if (_working || _demo) return;
        await RunAsync(async () =>
        {
            foreach (var p in _store.Load())
            {
                try
                {
                    var service = Service(p); var receipt = service.ReadReceipt(); if (receipt is null) continue;
                    string statusFile = Path.Combine(service.State, "launch-status.json");
                    if (File.Exists(statusFile))
                    {
                        string text = File.ReadAllText(statusFile);
                        if (_lastLaunchStatus.GetValueOrDefault(p.Id) != text) { var status = JsonSerializer.Deserialize<LaunchStatus>(text); if (status is not null) Log(p.Name + "：" + status.Message); _lastLaunchStatus[p.Id] = text; }
                    }
                    if (LocalEnvironment.Busy(p)) { _nextRepair[p.Id] = DateTimeOffset.MinValue; continue; }
                    if (!p.Maintain || _nextRepair.GetValueOrDefault(p.Id) > DateTimeOffset.Now) continue;
                    _nextRepair[p.Id] = DateTimeOffset.Now.AddMinutes(5);
                    var metadata = SteamAppMetadata.ParseApiSteamCmdJson(await DownloadAsync(p.AppId), p.AppId, receipt.Metadata.LaunchId);
                    service.Repair(metadata); _nextRepair[p.Id] = DateTimeOffset.MaxValue; Log(p.Name + "：托盘维护完成。");
                }
                catch (Exception ex) { _nextRepair[p.Id] = DateTimeOffset.Now.AddMinutes(5); Log(p.Name + "：维护停止，" + ex.Message); }
            }
        });
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
        bool supported = SelectedPreset.StarwardBiz is not null; _starward.Enabled = supported;
        if (!supported) _starward.Checked = false;
        _starwardStatus.Text = !supported ? "此预设不启用" : new WindowsStarwardTransport().ProtocolAvailable ? "协议可用（不代表已记录）" : "协议不可用；可指定 Starward EXE";
        if (supported && !_starward.Checked) { _starwardStatus.Text = "已关闭 Starward 计时"; return; }
        if (_demo || !supported) return;
        try
        {
            var p = _store.Load().SingleOrDefault(x => x.Id == _profileId);
            if (p is null) return;
            string file = Path.Combine(Service(p).State, "launch-status.json"); LocalEnvironment.GuardPath(file);
            if (File.Exists(file) && new FileInfo(file).Length < 128 * 1024)
            {
                var status = JsonSerializer.Deserialize<LaunchStatus>(File.ReadAllBytes(file));
                if (status?.StarwardState == "sent") _starwardStatus.Text = "已发送计时请求（未确认记录）";
                if (status?.StarwardState == "unavailable") _starwardStatus.Text = "不可用，计时未确认；游戏继续";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void RefreshLocks()
    {
        bool active = false;
        try { var saved = _store.Load().SingleOrDefault(x => x.Id == _profileId); active = saved is not null && Service(saved).ReadReceipt() is not null; } catch (Exception ex) { _status.Text = ex.Message; }
        UpdateStarwardState();
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
        if (_working) return; _working = true; foreach (var a in _actions) a.Enabled = false; _profiles.Enabled = false; foreach (var c in _editors) c.Enabled = false;
        try { await action(); }
        catch (UnauthorizedAccessException) { Log("访问被拒绝。请以具有所选 Steam 目录权限的身份重新打开；工具不会尝试绕过权限。"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("已取消 Windows 授权。"); }
        catch (Exception ex) { Log(ex.Message); }
        finally { _working = false; foreach (var a in _actions) a.Enabled = true; RefreshLocks(); UpdatePreview(); }
    }
    private void Log(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n");
}
