using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal sealed class LaunchOptionsDialog : Form
{
    private readonly RadioButton keep = new() { Text = "保留原设置（本次不接入）", AutoSize = true, Checked = true };
    private readonly RadioButton preserve = new() { Text = "保留游戏参数并接入", AutoSize = true };
    private readonly RadioButton replace = new() { Text = "完全替换启动项", AutoSize = true };
    private readonly TextBox arguments = new() { Dock = DockStyle.Fill };
    private readonly Label note = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly string configured;
    private readonly string? original;
    public ExistingLaunchOptionsAction SelectedAction => keep.Checked ? ExistingLaunchOptionsAction.Keep : preserve.Checked ? ExistingLaunchOptionsAction.PreserveArguments : ExistingLaunchOptionsAction.Replace;
    public string GameArguments => arguments.Text;

    public LaunchOptionsDialog(string? original, string configuredArguments, string command)
    {
        this.original = original; configured = configuredArguments;
        Text = "检查原启动项与最终参数"; Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(790, 590); MinimumSize = new Size(660, 570); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(16) };
        foreach (int height in new[] { 26, 95, 84, 52, 30, 42, 26 }) root.RowStyles.Add(new(SizeType.Absolute, height));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 46));
        root.Controls.Add(new Label { Text = "当前 Steam 启动项（接入前将备份）", AutoSize = true }, 0, 0);
        root.Controls.Add(new TextBox { Text = original ?? "（未设置）", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }, 0, 1);
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown }; choices.Controls.AddRange([keep, preserve, replace]); root.Controls.Add(choices, 0, 2);
        root.Controls.Add(note, 0, 3); root.Controls.Add(new Label { Text = "最终游戏参数（保留顺序，冲突参数由游戏自行处理）", AutoSize = true }, 0, 4); root.Controls.Add(arguments, 0, 5);
        root.Controls.Add(new Label { Text = "预计 Steam 启动项（参数保存在对应配置中）", AutoSize = true }, 0, 6);
        root.Controls.Add(new TextBox { Text = command, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical }, 0, 7);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var confirm = new Button { Text = "确认此选择", AutoSize = true }; var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([confirm, cancel]); root.Controls.Add(buttons, 0, 8); Controls.Add(root); CancelButton = cancel;
        confirm.Click += (_, _) =>
        {
            try { if (!keep.Checked) LaunchConfiguration.ValidateArguments(arguments.Text); DialogResult = DialogResult.OK; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "参数需要修改", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        foreach (var option in new[] { keep, preserve, replace }) option.CheckedChanged += (_, _) => UpdateArguments();
        if (string.IsNullOrWhiteSpace(original)) replace.Checked = true;
        UpdateArguments();
    }
    private void UpdateArguments()
    {
        arguments.Enabled = !keep.Checked;
        if (keep.Checked) { note.Text = "保留当前 Steam 配置，本次不接入。"; arguments.Text = configured; return; }
        if (replace.Checked) { note.Text = "使用本次配置的游戏参数，不沿用原 Steam 参数；原值仍有备份。"; arguments.Text = configured; return; }
        bool complex;
        try { complex = LaunchConfiguration.IsComplexCommand(original ?? ""); }
        catch (BridgeException) { complex = true; }
        if (complex)
        {
            note.Text = "原内容含完整命令或未闭合引号，无法自动当作游戏参数。请仅填入要保留的参数，再确认。";
            arguments.Text = configured;
        }
        else
        {
            note.Text = "已按 Windows 引号规则合并原参数与本次参数。请核对并编辑后确认；不会自动去重或改变优先顺序。";
            arguments.Text = WindowsArguments.Join(WindowsArguments.Parse(original ?? "").Concat(WindowsArguments.Parse(configured)));
        }
    }
}
