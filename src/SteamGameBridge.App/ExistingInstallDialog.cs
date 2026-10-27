using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal sealed class ExistingInstallDialog : Form
{
    private readonly WindowsThemeBinding theme;
    internal ExistingInstallAction SelectedAction { get; private set; }
    internal ExistingInstallDialog(ExistingInstallInspection inspection, Func<bool> darkMode)
    {
        Text = "已有安装"; ShowIcon = false; Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(790, 365); MinimumSize = new Size(720, 390); StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 40)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 58)); root.RowStyles.Add(new(SizeType.Absolute, 38)); root.RowStyles.Add(new(SizeType.Absolute, 48));
        root.Controls.Add(new Label { Text = inspection.DisplayKind, Font = new Font(Font.FontFamily, 13, FontStyle.Bold), AutoSize = true }, 0, 0);
        root.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Text = $"清单：{inspection.ManifestPath}\r\n\r\n安装目录：{inspection.InstallDirectory}\r\n\r\n{inspection.FileCount} 个文件 · {FormatSize(inspection.SizeBytes)}" }, 0, 1);
        root.Controls.Add(new Label { Text = string.Join("；", inspection.BlockReasons), ForeColor = UiTheme.Error, Tag = ThemeTone.Error, Dock = DockStyle.Fill }, 0, 2);
        var deleteConfirmed = new CheckBox { Text = "删除以上安装目录及清单（原游戏文件无法恢复）", AutoSize = true, Enabled = inspection.BlockReasons.Count == 0 };
        root.Controls.Add(deleteConfirmed, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var reconnect = new UiButton { Text = "重新接入", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = inspection.Kind == ExistingInstallKind.ManagedPlaceholder && inspection.BlockReasons.Count == 0 };
        var delete = new UiButton { Text = "删除现有安装并接入", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = false };
        var cancel = new UiButton { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 4, 10, 4) };
        UiTheme.StyleButton(reconnect, true); UiTheme.StyleButton(delete); UiTheme.StyleButton(cancel);
        deleteConfirmed.CheckedChanged += (_, _) => delete.Enabled = deleteConfirmed.Checked && inspection.BlockReasons.Count == 0;
        reconnect.Click += (_, _) => { SelectedAction = ExistingInstallAction.Reconnect; DialogResult = DialogResult.OK; };
        delete.Click += (_, _) => { SelectedAction = ExistingInstallAction.DeleteAndReconnect; DialogResult = DialogResult.OK; };
        actions.Controls.AddRange([reconnect, delete, cancel]); root.Controls.Add(actions, 0, 4); Controls.Add(root); CancelButton = cancel;
        theme = new(this, darkMode);
    }
    private static string FormatSize(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024d:F1} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024:F1} MB" : $"{bytes / 1024d / 1024 / 1024:F1} GB";
    protected override void Dispose(bool disposing) { if (disposing) theme?.Dispose(); base.Dispose(disposing); }
}
