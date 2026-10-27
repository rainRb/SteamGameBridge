using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal sealed class DataLocationDialog : Form
{
    private readonly TextBox parent = new() { Dock = DockStyle.Fill };
    private readonly Label target = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label error = new() { AutoSize = true, Dock = DockStyle.Fill, Tag = ThemeTone.Error };
    private readonly WindowsThemeBinding theme;
    internal string DataDirectory => Path.Combine(Path.GetFullPath(parent.Text.Trim()), DataLocationSettings.FolderName);

    internal DataLocationDialog(string initialParent, Func<string, Task> confirm, Func<bool>? darkMode = null, string? initialError = null)
    {
        Text = "保存位置"; ShowIcon = false; Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(700, 245); MinimumSize = new Size(650, 270); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 5 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100)); root.ColumnStyles.Add(new(SizeType.Absolute, 82));
        foreach (int height in new[] { 35, 38, 46, 35, 43 }) root.RowStyles.Add(new(SizeType.Absolute, height));
        var heading = new Label { Text = "配置、备份和启动组件保存在这里", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        root.Controls.Add(heading, 0, 0); root.SetColumnSpan(heading, 2);
        var browse = new UiButton { Text = "选择", AutoSize = true, Dock = DockStyle.Fill }; UiTheme.StyleButton(browse);
        root.Controls.Add(parent, 0, 1); root.Controls.Add(browse, 1, 1);
        root.Controls.Add(target, 0, 2); root.SetColumnSpan(target, 2); root.Controls.Add(error, 0, 3); root.SetColumnSpan(error, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new UiButton { Text = "确定", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        var cancel = new UiButton { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(12, 4, 12, 4) };
        UiTheme.StyleButton(save, true); UiTheme.StyleButton(cancel); buttons.Controls.AddRange([save, cancel]); root.Controls.Add(buttons, 0, 4); root.SetColumnSpan(buttons, 2);
        Controls.Add(root); CancelButton = cancel;
        parent.TextChanged += (_, _) => { try { target.Text = "数据目录：" + DataDirectory; } catch { target.Text = "请选择有效目录。"; } };
        parent.Text = initialParent; error.Text = initialError ?? "";
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = parent.Text }; if (picker.ShowDialog(this) == DialogResult.OK) parent.Text = picker.SelectedPath; };
        save.Click += async (_, _) =>
        {
            save.Enabled = cancel.Enabled = browse.Enabled = false; parent.ReadOnly = true;
            try { await confirm(DataDirectory); DialogResult = DialogResult.OK; }
            catch (UnauthorizedAccessException) { error.Text = "目录不可写，请移动程序到可写位置或以管理员身份打开。"; }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { if (!IsDisposed) { save.Enabled = cancel.Enabled = browse.Enabled = true; parent.ReadOnly = false; } }
        };
        theme = new(this, darkMode);
    }
    protected override void Dispose(bool disposing) { if (disposing) theme?.Dispose(); base.Dispose(disposing); }
}
