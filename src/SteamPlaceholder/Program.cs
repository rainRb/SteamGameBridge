using System.Windows.Forms;

internal static class Program
{
    [System.STAThread]
    private static int Main()
    {
        MessageBox.Show("Steam 启动项未正确指向国服。\n\n请完全退出 Steam，打开“绝区零 Steam 接入工具”并点击“检查并修复”。\n\n本占位程序不会启动或下载国际服客户端。", "绝区零 Steam 接入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 1;
    }
}
