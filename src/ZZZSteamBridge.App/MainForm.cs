using System.Diagnostics;
using System.Text.Json;
using ZZZSteamBridge.Core;

namespace ZZZSteamBridge.App;

internal sealed class MainForm : Form
{
    private readonly bool _demo;
    private readonly string _state;
    private string _steam = "";
    private SteamBridgeService? _service;
    private SteamAppMetadata? _metadata;
    private bool _working;
    private bool _exit;
    private bool _lastBusy;
    private bool _repairDue;
    private DateTimeOffset _lastFetch;
    private DateTimeOffset _nextMaintenanceAttempt;
    private (string Helper, string Placeholder)? _tools;
    private readonly TextBox _game = Input();
    private readonly TextBox _arguments = Input();
    private readonly ComboBox _libraries = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _accounts = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DisplayMember = nameof(AccountRow.Caption) };
    private readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(243, 246, 250) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(34, 95, 140) };
    private readonly Label _metadataLabel = new() { AutoSize = true, Text = "元数据：尚未获取" };
    private readonly CheckBox _maintenance = new() { AutoSize = true, Text = "在托盘维护：Steam 完全退出后自动修复（不随开机启动）", Checked = true };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Text = "绝区零 Steam 接入", Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10000 };
    private readonly List<Button> _actions = [];
    private Button _apply = null!;
    private Button _repair = null!;
    private Button _restore = null!;
    private Button _launch = null!;
    private Button _browse = null!;

    private sealed record AccountRow(SteamAccount Account)
    {
        public string Caption => $"{Account.DisplayName} · {Account.AccountId}";
    }

    public MainForm(bool demo)
    {
        _demo = demo;
        _state = demo ? Path.Combine(Environment.GetEnvironmentVariable("ZZZ_BRIDGE_DEMO_ROOT") ?? Path.Combine(Path.GetTempPath(), "ZZZBridgePreview-" + Guid.NewGuid().ToString("N")), "state") : LocalEnvironment.StateDirectory;
        Text = demo ? "绝区零 Steam 接入 · 界面演示" : "绝区零 Steam 接入";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(1040, 920);
        MinimumSize = new Size(900, 850);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(237, 242, 248);
        BuildUi();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开接入工具", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        trayMenu.Items.Add("退出维护", null, (_, _) => ExitTool());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) =>
        {
            if (!_exit && !_demo && _maintenance.Checked && e.CloseReason == CloseReason.UserClosing && HasReceipt())
            { e.Cancel = true; Hide(); _tray.Visible = true; _tray.ShowBalloonTip(4000, "已在托盘维护", "退出 Steam 后会修复所管理的接入；在托盘菜单中可完全退出。", ToolTipIcon.Info); }
        };
        FormClosed += (_, _) => { _timer.Stop(); _tray.Dispose(); };
        Shown += async (_, _) => await InitializeAsync();
        _timer.Tick += async (_, _) => await MaintenanceTickAsync();
        _libraries.SelectedIndexChanged += (_, _) => UpdatePreview();
        _accounts.SelectedIndexChanged += (_, _) => UpdatePreview();
        _game.TextChanged += (_, _) => UpdatePreview();
        _arguments.TextChanged += (_, _) => UpdatePreview();
    }

    private static TextBox Input() => new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) };

    private void BuildUi()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 10 };
        layout.RowStyles.Add(new(SizeType.Absolute, 80));
        layout.RowStyles.Add(new(SizeType.Absolute, 80));
        layout.RowStyles.Add(new(SizeType.Absolute, 182));
        layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 34));
        layout.RowStyles.Add(new(SizeType.Percent, 45));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.RowStyles.Add(new(SizeType.Absolute, 36));
        layout.RowStyles.Add(new(SizeType.Percent, 55));
        layout.RowStyles.Add(new(SizeType.Absolute, 32));
        Controls.Add(layout);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        heading.Controls.Add(new Label { Text = "绝区零 · Steam 国服接入", Font = new Font(Font.FontFamily, 23f, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(23, 45, 73) });
        heading.Controls.Add(new Label { Text = "直接使用已有国服客户端  /  正式 AppID 4162040  /  独立工具", AutoSize = true, ForeColor = Color.FromArgb(86, 103, 125) });
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Color.FromArgb(223, 234, 247),
            Text = "无需安装 Steam 国际服：本工具只获取元数据，不下载国际服资源。先正常入库绝区零，接入或恢复前完全退出 Steam。\n若国服要求管理员权限，可用下方“管理员打开 Steam”完成正常 UAC 授权。Overlay、时长和好友状态需实测。",
        }, 0, 1);
        var inputs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(0, 12, 0, 0), RowCount = 4 };
        inputs.ColumnStyles.Add(new(SizeType.Absolute, 100)); inputs.ColumnStyles.Add(new(SizeType.Percent, 100)); inputs.ColumnStyles.Add(new(SizeType.Absolute, 90));
        for (int r = 0; r < 4; r++) inputs.RowStyles.Add(new(SizeType.Absolute, 40));
        inputs.Controls.Add(Label("国服程序"), 0, 0); inputs.Controls.Add(_game, 1, 0);
        _browse = Button("选择…", async () => { using var picker = new OpenFileDialog { Filter = "绝区零客户端|ZenlessZoneZero.exe|EXE 文件|*.exe" }; if (picker.ShowDialog(this) == DialogResult.OK) _game.Text = picker.FileName; await Task.CompletedTask; });
        inputs.Controls.Add(_browse, 2, 0);
        inputs.Controls.Add(Label("Steam 游戏库"), 0, 1); inputs.Controls.Add(_libraries, 1, 1); inputs.SetColumnSpan(_libraries, 2);
        inputs.Controls.Add(Label("目标账号"), 0, 2); inputs.Controls.Add(_accounts, 1, 2); inputs.SetColumnSpan(_accounts, 2);
        inputs.Controls.Add(Label("国服参数"), 0, 3); inputs.Controls.Add(_arguments, 1, 3); inputs.SetColumnSpan(_arguments, 2);
        layout.Controls.Add(inputs, 0, 2);
        var metadataButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        metadataButtons.Controls.Add(Button("获取最新元数据", async () => { await FetchMetadataAsync(); UpdatePreview(); }));
        metadataButtons.Controls.Add(Button("导入元数据", ImportMetadataAsync));
        metadataButtons.Controls.Add(Button("复制启动命令", () => { var preview = CurrentPreview(); Clipboard.SetText(preview.LaunchOptions); Log("已复制启动命令。接入按钮会自动写入所选账号；复制入口用于手动核对。"); return Task.CompletedTask; }));
        metadataButtons.Controls.Add(Button("打开备份目录", () => { if (Directory.Exists(_state)) Process.Start(new ProcessStartInfo("explorer.exe", _state) { UseShellExecute = true }); return Task.CompletedTask; }));
        layout.Controls.Add(metadataButtons, 0, 3);
        layout.Controls.Add(_metadataLabel, 0, 4);
        layout.Controls.Add(_preview, 0, 5);
        var operations = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        _apply = Button("一键接入", ApplyAsync, primary: true);
        _repair = Button("检查并修复", RepairAsync);
        _restore = Button("一键恢复", RestoreAsync);
        _launch = Button("从 Steam 启动", LaunchAsync);
        operations.Controls.AddRange([_apply, _repair, _restore, _launch, Button("管理员打开 Steam", OpenElevatedSteamAsync), Button("退出工具", () => { ExitTool(); return Task.CompletedTask; })]);
        layout.Controls.Add(operations, 0, 6);
        layout.Controls.Add(_maintenance, 0, 7);
        layout.Controls.Add(_log, 0, 8);
        layout.Controls.Add(_status, 0, 9);
        if (_demo) { _maintenance.Checked = false; _maintenance.Enabled = false; }
    }

    private Button Button(string caption, Func<Task> action, bool primary = false)
    {
        var button = new Button { Text = caption, AutoSize = true, Height = 31, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 4), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(36, 100, 161) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(29, 48, 71) };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(197, 210, 225);
        button.Click += async (_, _) => await RunAsync(action);
        _actions.Add(button);
        return button;
    }

    private async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            if (_demo) InitializeDemo();
            else
            {
                _steam = LocalEnvironment.SteamRoot();
                var libraries = SteamDiscovery.ReadLibraries(_steam);
                _service = new(new() { StateDirectory = _state, RegisteredLibraries = libraries, IsBusy = LocalEnvironment.Busy });
                _libraries.Items.AddRange(libraries.Cast<object>().ToArray());
                if (_libraries.Items.Count > 0) _libraries.SelectedIndex = 0;
                var accounts = SteamDiscovery.ReadAccounts(_steam);
                foreach (var account in accounts.Accounts) _accounts.Items.Add(new AccountRow(account));
                if (accounts.SuggestedAccount is { } suggested) _accounts.SelectedIndex = accounts.Accounts.ToList().FindIndex(x => x.AccountId == suggested.AccountId);
                _game.Text = LocalEnvironment.NativeGame();
                if (_service.ReadReceipt() is { } receipt)
                {
                    _game.Text = receipt.Request.NativeExe; _arguments.Text = receipt.Request.GameArguments;
                    _libraries.SelectedItem = receipt.Request.LibraryRoot;
                    for (int i = 0; i < _accounts.Items.Count; i++) if (_accounts.Items[i] is AccountRow row && row.Account.LocalConfigPath.Equals(receipt.Request.AccountConfigPath, StringComparison.OrdinalIgnoreCase)) _accounts.SelectedIndex = i;
                    _repairDue = true;
                }
                if (_accounts.SelectedIndex < 0) Log("请选择要接入的 Steam 账号。没有唯一的最近登录账号时，工具不会自行选择。");
            }
            Log(_demo ? "界面演示：使用隔离的模拟游戏库，所有接入、恢复和启动操作已禁用。" : "接入前请先正常在 Steam 入库绝区零，无需下载国际服客户端。仅修改所选账号的本游戏启动选项。");
            Log("验收：正式条目显示运行中 → 确认国服服务器 → Shift+Tab / F12 → 退出后确认停止计时及原生时长增加 → 请好友核对游戏状态。进程存在本身不能证明这些结果。");
            try { await FetchMetadataAsync(); } catch (Exception ex) { Log("元数据获取失败：" + ex.Message); }
            UpdatePreview(); UpdateStatus(); _timer.Start();
        });
    }

    private void InitializeDemo()
    {
        string root = Path.GetDirectoryName(_state)!; _steam = Path.Combine(root, "Steam");
        string native = Path.Combine(root, "国服游戏", "ZenlessZoneZero.exe");
        string profile = Path.Combine(_steam, "userdata", "12345", "config", "localconfig.vdf");
        foreach (string folder in new[] { Path.Combine(_steam, "steamapps"), Path.GetDirectoryName(native)!, Path.GetDirectoryName(profile)! }) Directory.CreateDirectory(folder);
        using var resource = typeof(MainForm).Assembly.GetManifestResourceStream("SteamPlaceholder.exe")!;
        using var bytes = new MemoryStream(); resource.CopyTo(bytes);
        foreach (string exe in new[] { native, Path.Combine(_steam, "steam.exe") }) File.WriteAllBytes(exe, bytes.ToArray());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(native)!, "config.ini"), "[General]\r\ngame_biz=nap_cn\r\n");
        File.WriteAllText(profile, "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { } } } } }");
        Directory.CreateDirectory(Path.Combine(_steam, "config"));
        File.WriteAllText(Path.Combine(_steam, "config", "loginusers.vdf"), "\"users\" { \"76561197960278073\" { \"PersonaName\" \"示例 Steam 用户\" \"MostRecent\" \"1\" } }");
        _tools = (Environment.ProcessPath!, native);
        _libraries.Items.Add(_steam); _libraries.SelectedIndex = 0;
        _accounts.Items.Add(new AccountRow(new("12345", "示例 Steam 用户", profile, true))); _accounts.SelectedIndex = 0;
        _service = new(new() { StateDirectory = _state, RegisteredLibraries = [_steam], IsBusy = () => false });
        _game.Text = native;
    }

    private async Task FetchMetadataAsync()
    {
        string json;
        if (_demo) json = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("ZZZ_BRIDGE_DEMO_INFO") ?? throw new IOException("缺少界面演示元数据。"));
        else
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            json = await http.GetStringAsync("https://api.steamcmd.net/v1/info/4162040");
        }
        _metadata = SteamAppMetadata.ParseApiSteamCmdJson(json);
        _lastFetch = DateTimeOffset.Now;
        _metadataLabel.Text = $"{(_demo ? "演示快照" : "在线元数据")} · Build {_metadata.BuildId} · {_metadata.Depots.Count} 个 Windows depot · {_lastFetch:HH:mm}";
        Log("已获取安装信息；未下载国际服游戏资源。数据源：api.steamcmd.net（公开第三方元数据镜像）。");
    }

    private async Task ImportMetadataAsync()
    {
        using var picker = new OpenFileDialog { Filter = "Steam app-info JSON|*.json" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) throw new IOException("元数据文件过大。");
        _metadata = SteamAppMetadata.ParseApiSteamCmdJson(await File.ReadAllTextAsync(picker.FileName));
        _lastFetch = DateTimeOffset.MinValue;
        _metadataLabel.Text = $"导入快照 · Build {_metadata.BuildId} · 未在线确认是否最新";
        UpdatePreview();
    }

    private SetupRequest CurrentRequest()
    {
        if (_service is null || _metadata is null) throw new BridgeException("请先获取元数据。");
        if (_service.ReadReceipt() is { } active) return active.Request;
        if (_accounts.SelectedItem is not AccountRow account) throw new BridgeException("请选择目标 Steam 账号。");
        if (_libraries.SelectedItem is not string library) throw new BridgeException("请选择游戏库。");
        _tools ??= LocalEnvironment.StageTools(_state);
        return new(_steam, library, account.Account.LocalConfigPath, _game.Text.Trim(), _tools.Value.Helper, _tools.Value.Placeholder, _arguments.Text.Trim());
    }

    private SetupPreview CurrentPreview() => _service!.ReadReceipt()?.Preview ?? _service.Preview(CurrentRequest(), _metadata!);
    private void UpdatePreview()
    {
        if (_working || _service is null || _metadata is null) return;
        try
        {
            var preview = CurrentPreview();
            _preview.Text = $"即将管理的路径（与国服目录分开）\r\n{preview.ManifestPath}\r\n{preview.PlaceholderDirectory}\r\n\r\n生成的 Steam 启动项（接入时自动写入）\r\n{preview.LaunchOptions}";
        }
        catch (Exception ex) { _preview.Text = "接入预览：" + ex.Message; }
    }

    private void RequireLive() { if (_demo) throw new BridgeException("演示模式不会执行接入、恢复或启动操作。"); }
    private async Task ApplyAsync()
    {
        RequireLive();
        var request = CurrentRequest();
        var result = await Task.Run(() => _service!.Apply(request, _metadata!));
        Log(result.Message); _repairDue = false; UpdateStatus();
    }
    private async Task RepairAsync()
    {
        RequireLive();
        await FetchMetadataAsync();
        var result = await Task.Run(() => _service!.Repair(_metadata!));
        Log(result.Message); _repairDue = false; UpdateStatus();
    }
    private async Task RestoreAsync()
    {
        RequireLive();
        var result = await Task.Run(() => _service!.Restore());
        Log(result.Message); _repairDue = false; UpdateStatus();
    }

    private async Task LaunchAsync()
    {
        RequireLive();
        var inspection = _service!.Inspect();
        if (!inspection.Installed) throw new BridgeException("请先完成一键接入。");
        if (_service.ReadReceipt()?.Phase != "active") throw new BridgeException("上次接入操作未完整完成，请先恢复后重新接入。");
        if (inspection.Conflicts.Count > 0) throw new BridgeException("配置需要处理，请完全退出 Steam 后点击检查并修复。" + string.Join("；", inspection.Conflicts));
        if (!LocalEnvironment.SteamRunning())
        {
            if (LocalEnvironment.GameRunning() || NativeLauncher.BridgeRunning()) throw new BridgeException("请先退出国服游戏。");
            await RepairAsync();
            throw new BridgeException("配置已检查。请先用“管理员打开 Steam”登录所选账号，Steam 打开后再点击从 Steam 启动。");
        }
        else if (_lastFetch == DateTimeOffset.MinValue || DateTimeOffset.Now - _lastFetch > TimeSpan.FromHours(1))
            throw new BridgeException("请完全退出 Steam 后检查并修复，确认元数据后再启动。");
        string configuredAccount = Directory.GetParent(Path.GetDirectoryName(_service.ReadReceipt()!.Request.AccountConfigPath)!)!.Name;
        if (LocalEnvironment.ActiveSteamAccount() != configuredAccount)
            throw new BridgeException("Steam 当前登录账号与接入账号不同或尚未完成登录。请登录界面所选账号后再启动，不会修改其他账户。");
        if (LocalEnvironment.GameRunning() || NativeLauncher.BridgeRunning()) throw new BridgeException("国服游戏或启动会话已运行。");
        Process.Start(new ProcessStartInfo(Path.Combine(_steam, "steam.exe"), "-applaunch 4162040") { UseShellExecute = true })?.Dispose();
        Log("已交给 Steam 正式条目启动。若提示权限不足，请完全退出 Steam 后用“管理员打开 Steam”。");
    }

    private Task OpenElevatedSteamAsync()
    {
        RequireLive();
        if (LocalEnvironment.SteamRunning()) throw new BridgeException("请先在 Steam 菜单中退出客户端，再打开管理员 Steam；工具不会强制结束它。");
        Process.Start(new ProcessStartInfo(Path.Combine(_steam, "steam.exe")) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        Log("已请求 Windows 正常 UAC 授权。Steam 打开后，从正式绝区零条目启动。");
        return Task.CompletedTask;
    }

    private async Task MaintenanceTickAsync()
    {
        if (_working || _demo || _service is null) return;
        bool busy = LocalEnvironment.Busy();
        if (_lastBusy && !busy) _repairDue = true;
        _lastBusy = busy;
        UpdateStatus();
        if (!_maintenance.Checked || busy || !_repairDue || DateTimeOffset.Now < _nextMaintenanceAttempt || !_service.Inspect().Installed) return;
        _repairDue = false;
        bool succeeded = false;
        await RunAsync(async () => { await RepairAsync(); succeeded = true; Log("托盘维护已完成，未启动游戏或下载国际服资源。"); });
        if (!succeeded)
        {
            _repairDue = true; _nextMaintenanceAttempt = DateTimeOffset.Now.AddMinutes(5);
            _tray.ShowBalloonTip(5000, "接入维护需要检查", "本次修复未完成，5 分钟后重试。打开工具可查看原因；未知文件不会被覆盖。", ToolTipIcon.Warning);
        }
        else _nextMaintenanceAttempt = DateTimeOffset.MinValue;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_working) return;
        _working = true; foreach (var button in _actions) button.Enabled = false;
        try { await action(); }
        catch (UnauthorizedAccessException) { Log("访问被拒绝。请关闭工具后以管理员身份重新打开，或选择已有写入权限的 Steam 游戏库。没有尝试绕过权限。"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("已取消 Windows 授权，未继续操作。"); }
        catch (Exception ex) { Log(ex.Message); }
        finally
        {
            _working = false; foreach (var button in _actions) button.Enabled = true;
            if (_demo) { _apply.Enabled = _repair.Enabled = _restore.Enabled = _launch.Enabled = false; }
            else UpdateSelectionLocks();
            UpdatePreview(); UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        if (_demo) { _status.Text = "界面演示 · 本机 Steam、游戏与 Starward 未改动"; return; }
        try
        {
            _status.Text = (LocalEnvironment.Busy() ? "Steam／游戏运行中：等待退出后维护" : "Steam 与游戏已退出") + "  ·  " + (_service?.Inspect().Status ?? "尚未连接 Steam");
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Log(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\r\n");
    private bool HasReceipt() { try { return _service?.ReadReceipt() is not null; } catch { return false; } }
    private void UpdateSelectionLocks()
    {
        bool managed = HasReceipt();
        _libraries.Enabled = _accounts.Enabled = _browse.Enabled = !managed;
        _game.ReadOnly = _arguments.ReadOnly = managed;
        _apply.Enabled = !managed; _repair.Enabled = _restore.Enabled = _launch.Enabled = managed;
    }
    private void ExitTool() { _exit = true; Close(); }
}
