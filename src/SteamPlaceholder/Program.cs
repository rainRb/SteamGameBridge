using System.Windows.Forms;

internal static class Program
{
    [System.STAThread]
    private static int Main()
    {
        MessageBox.Show("Steam 启动项未正确指向本地游戏。\n\n请完全退出 Steam，打开“SteamGameBridge”并点击“检查并修复”。\n\n本占位程序不会启动或下载国际服客户端。", "SteamGameBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 1;
    }
}
