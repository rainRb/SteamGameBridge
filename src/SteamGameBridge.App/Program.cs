namespace SteamGameBridge.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--profile", StringComparer.Ordinal)) return NativeLauncher.Run(args);
            if (args.Length != 0 && args[0] != "--demo") throw new ArgumentException("未知参数。直接打开本工具进入设置，或使用生成的 Steam 启动命令。");
            Application.Run(new MainForm(args.Length > 0));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "SteamGameBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
