using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ZZZSteamBridge.App;

internal static class NativeLauncher
{
    internal const string MutexName = "Local\\ZZZSteamBridge.4162040.Game";

    internal static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("SteamAppId") != "4162040"
            || Environment.GetEnvironmentVariable("SteamGameId") is string id && id != "4162040")
            throw new InvalidOperationException("请从 Steam 中绝区零的正式条目启动。辅助程序不会自行设置 AppID，也不授予游戏许可。");
        if (args.Length < 5 || args[0] != "--game" || args[2] != "--")
            throw new ArgumentException("启动命令无效。请在工具中重新接入。");
        int delimiter = Array.IndexOf(args, "--steam-command", 3);
        if (delimiter < 3 || delimiter == args.Length - 1) throw new ArgumentException("启动项缺少 --steam-command %command%。");
        string game = Path.GetFullPath(args[1]);
        if (!Path.IsPathFullyQualified(args[1]) || !Path.GetFileName(game).Equals("ZenlessZoneZero.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(game)) throw new FileNotFoundException("找不到已设置的国服 ZenlessZoneZero.exe，请重新选择路径。");
        LocalEnvironment.GuardPath(game);
        string config = Path.Combine(Path.GetDirectoryName(game)!, "config.ini");
        if (!File.Exists(config) || !File.ReadLines(config).Any(x => x.Trim().Equals("game_biz=nap_cn", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("目标目录的 config.ini 未标明 game_biz=nap_cn，拒绝将国际服或未知客户端作为国服启动。");
        if (args.Skip(3).Take(delimiter - 3).Any(x => x.Contains("login_auth_ticket", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("请勿将登录票据保存在 Steam 启动项中。");
        using var single = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = single.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("已有国服 Steam 启动会话，请先退出游戏。");
        try
        {
            if (LocalEnvironment.GameRunning()) throw new InvalidOperationException("绝区零已经运行，请先退出再通过 Steam 启动。");
            var info = new ProcessStartInfo(game) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(game) };
            // Keep only explicitly configured CN arguments. The original Steam command is never executed or forwarded.
            foreach (string argument in args.Skip(3).Take(delimiter - 3)) info.ArgumentList.Add(argument);
            using Process process = Process.Start(info) ?? throw new IOException("国服客户端未启动。");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new InvalidOperationException("国服客户端需要管理员权限。请完全退出 Steam，手动以管理员身份重新打开 Steam 并完成正常 UAC 授权，再从绝区零条目启动。本工具不会自动提权或修改反作弊设置。", ex);
        }
        finally { single.ReleaseMutex(); }
    }

    internal static bool BridgeRunning()
    {
        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(0)) return true;
            mutex.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException) { mutex.ReleaseMutex(); return false; }
    }
}
