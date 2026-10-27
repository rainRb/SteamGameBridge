using System.Reflection;
using System.Text;

namespace SteamGameBridge.App;

internal sealed class LicenseDialog : Form
{
    private readonly WindowsThemeBinding theme;
    internal static string ReadNotices()
    {
        var assembly = Assembly.GetExecutingAssembly(); var text = new StringBuilder();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Licenses.", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!; using var reader = new StreamReader(stream, Encoding.UTF8);
            text.AppendLine(name).AppendLine().AppendLine(reader.ReadToEnd()).AppendLine();
        }
        if (text.Length == 0) throw new IOException("缺少内置许可证。");
        return text.ToString();
    }
    internal LicenseDialog(Func<bool> darkMode)
    {
        Text = "许可证"; ShowIcon = false; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 550); MinimumSize = new Size(600, 400); Padding = new Padding(16);
        Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), Text = ReadNotices() });
        theme = new(this, darkMode);
    }
    protected override void Dispose(bool disposing) { if (disposing) theme?.Dispose(); base.Dispose(disposing); }
}
