namespace SteamGameBridge.App;

internal sealed class GameSelectionDialog : Form
{
    private readonly ListBox paths = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly WindowsThemeBinding theme;
    internal string SelectedPath => (string)paths.SelectedItem!;

    internal GameSelectionDialog(IReadOnlyList<string> candidates, Func<bool> darkMode)
    {
        Text = "选择游戏程序"; ShowIcon = false; Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(780, 360); MinimumSize = new Size(620, 280); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 2, ColumnCount = 1 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        paths.Items.AddRange(candidates.Cast<object>().ToArray()); paths.SelectedIndex = 0; layout.Controls.Add(paths, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var use = new UiButton { Text = "使用", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        var cancel = new UiButton { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 4, 14, 4) };
        UiTheme.StyleButton(use, primary: true); UiTheme.StyleButton(cancel); buttons.Controls.AddRange([use, cancel]);
        layout.Controls.Add(buttons, 0, 1); Controls.Add(layout); AcceptButton = use; CancelButton = cancel;
        paths.DoubleClick += (_, _) => { if (paths.SelectedIndex >= 0) DialogResult = DialogResult.OK; };
        theme = new(this, darkMode);
    }
    protected override void Dispose(bool disposing) { if (disposing) theme?.Dispose(); base.Dispose(disposing); }
}
