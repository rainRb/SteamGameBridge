using Microsoft.Win32;
using System.Diagnostics;
using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal interface IStarwardTransport
{
    bool ProtocolAvailable { get; }
    void SendUri(string uri);
    void SendExecutable(string executable, string uri);
}
internal sealed record StarwardNotification(string State, string Message);
internal sealed class StarwardNotifier(IStarwardTransport transport)
{
    private readonly HashSet<(int Pid, DateTime Created)> notified = [];
    internal static string BuildUri(string presetId, int pid)
    {
        if (pid <= 0) throw new ArgumentException("需要真实游戏 PID。");
        string biz = GamePreset.Find(presetId).StarwardBiz ?? throw new ArgumentException("此游戏无 Starward 计时预设。");
        return $"starward://playtime/{biz}?pid={pid}";
    }
    internal StarwardNotification Notify(string presetId, StarwardIntegrationOptions options, GameProcessIdentity game)
    {
        if (!options.Enabled) return new("disabled", "未启用 Starward 时长联动。");
        if (!notified.Add((game.Pid, game.CreatedUtc))) return new("duplicate", "此游戏进程已尝试通知，不重复发送。");
        try
        {
            string uri = BuildUri(presetId, game.Pid);
            if (transport.ProtocolAvailable) transport.SendUri(uri);
            else if (!string.IsNullOrWhiteSpace(options.Executable)) transport.SendExecutable(options.Executable, uri);
            else return new("unavailable", "Starward 不可用，计时未确认；游戏继续运行。");
            return new("sent", "已发送 Starward 计时请求（PID " + game.Pid + "），尚未确认实际记录。");
        }
        catch (Exception ex) { return new("unavailable", "Starward 通知失败，计时未确认；游戏继续运行。" + ex.Message); }
    }
}
internal static class StarwardLauncher
{
    internal static void Start(SetupRequest request, IStarwardTransport transport)
    {
        string uri = LaunchConfiguration.BuildStarwardStartUri(request);
        if (transport.ProtocolAvailable) transport.SendUri(uri);
        else if (!string.IsNullOrWhiteSpace(request.Starward?.Executable)) transport.SendExecutable(request.Starward.Executable, uri);
        else throw new IOException("未找到 Starward，请选择程序路径。");
    }
}

internal sealed class WindowsStarwardTransport : IStarwardTransport
{
    public bool ProtocolAvailable
    {
        get
        {
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(@"starward\shell\open\command");
                using var protocol = Registry.ClassesRoot.OpenSubKey("starward");
                return protocol?.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase) == true && key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
        }
    }
    public void SendUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    public void SendExecutable(string executable, string uri)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) };
        start.ArgumentList.Add(uri);
        (Process.Start(start) ?? throw new IOException("Starward 未启动。")).Dispose();
    }
}
