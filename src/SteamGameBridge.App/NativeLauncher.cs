using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal sealed record LaunchStatus(DateTimeOffset Time, string AppId, string State, string Message, int? GamePid = null, string? StarwardState = null);

internal static class NativeLauncher
{
    internal static string MutexName(string appId) { SteamAppMetadata.ValidateAppId(appId); return "Local\\SteamGameBridge.Game." + appId; }
    internal static int Run(string[] args, IStarwardTransport? transport = null, Action<LaunchStatus>? report = null)
    {
        if (args.Length < 4 || args[0] != "--profile" || args[2] != "--steam-command") throw new ArgumentException("启动命令无效，缺少 --profile 或 Steam 原始命令。请重新接入。");
        string specPath = Path.GetFullPath(args[1]); LocalEnvironment.GuardPath(specPath);
        if (!Path.IsPathFullyQualified(args[1]) || new FileInfo(specPath).Length > 128 * 1024) throw new IOException("启动配置路径或大小无效。");
        var spec = JsonSerializer.Deserialize<LaunchSpec>(File.ReadAllBytes(specPath)) ?? throw new IOException("启动配置为空。");
        SteamAppMetadata.ValidateAppId(spec.AppId);
        if (Environment.GetEnvironmentVariable("SteamAppId") != spec.AppId || Environment.GetEnvironmentVariable("SteamGameId") is string id && id != spec.AppId)
            throw new InvalidOperationException("请从配置的 Steam 正式条目启动。本工具不设置 AppID，也不授予游戏许可。");
        var request = spec.Request ?? throw new IOException("启动配置缺失。");
        LaunchConfiguration.Validate(request);
        ValidateStarwardIdentity(request);
        string? lastStarwardState = null;
        void Status(string state, string message, int? pid = null)
        {
            if (state is "sent" or "unavailable") lastStarwardState = state;
            var status = new LaunchStatus(DateTimeOffset.UtcNow, spec.AppId, state, message, pid, lastStarwardState);
            report?.Invoke(status);
            // Reporting must never interrupt or end a running game.
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(specPath)!, "launch-status.json");
                LocalEnvironment.GuardPath(path);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temp, JsonSerializer.Serialize(status)); File.Move(temp, path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        using var single = new Mutex(false, MutexName(spec.AppId));
        bool acquired;
        try { acquired = single.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new InvalidOperationException("已有此 Steam 条目的启动会话，请先退出游戏。");
        string nativeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(request.NativeExe).ToUpperInvariant())));
        using var nativeMutex = new Mutex(false, "Local\\SteamGameBridge.NativeGame." + nativeKey);
        bool nativeAcquired = false;
        try
        {
            try { nativeAcquired = nativeMutex.WaitOne(0); } catch (AbandonedMutexException) { nativeAcquired = true; }
            if (!nativeAcquired) throw new InvalidOperationException("已有此本地游戏的启动会话（可能来自其他 Steam AppID）。");
            if (GameProcessTracker.IsRunning(request.NativeExe)) throw new InvalidOperationException("所选本地游戏已经运行，请先退出再从 Steam 启动。");
            DateTime started = DateTime.UtcNow;
            string exe = request.LaunchMode == LaunchMode.Direct ? request.NativeExe : request.LauncherExe;
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            var arguments = request.LaunchMode == LaunchMode.Direct ? WindowsArguments.Parse(request.GameArguments) : LaunchConfiguration.ExpandLauncherArguments(request, args[3..]);
            foreach (string value in arguments) info.ArgumentList.Add(value);
            Status("waiting", "等待确认实际游戏进程；启动器可正常显示，请在其中开始游戏。");
            using Process launcher = Process.Start(info) ?? throw new IOException("程序未启动。");
            using Process game = request.LaunchMode == LaunchMode.Direct
                ? GameProcessTracker.ConfirmDirect(launcher.Id, request.NativeExe, started)
                : GameProcessTracker.WaitForGame(request.NativeExe, started, TimeSpan.FromSeconds(request.WaitSeconds));
            var identity = GameProcessTracker.Identity(game);
            var notifier = new StarwardNotifier(transport ?? new WindowsStarwardTransport());
            if (request.Starward?.Enabled == true)
            {
                var notification = notifier.Notify(request.PresetId, request.Starward, identity);
                Status(notification.State, notification.Message, game.Id);
            }
            else Status("running", "已确认本次实际游戏进程。", game.Id);
            using var gameHandle = GameProcessTracker.OpenLifetimeHandle(game.Id);
            int exit = GameProcessTracker.WaitForExit(gameHandle);
            Status("exited", $"实际游戏已退出（{exit}）；Steam 与 Starward 的计时结果需分别核对。", identity.Pid);
            return exit;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            Status("failed", "启动需要正常 Windows UAC 授权，尚未启动游戏。");
            throw new InvalidOperationException("客户端需要管理员权限。请完全退出 Steam，手动以管理员身份打开 Steam 并完成正常 UAC 授权后重试。本工具不会自动提权或修改反作弊。", ex);
        }
        catch (Exception ex) { Status("failed", ex.Message); throw; }
        finally { if (nativeAcquired) nativeMutex.ReleaseMutex(); single.ReleaseMutex(); }
    }
    private static void ValidateStarwardIdentity(SetupRequest request)
    {
        if (request.Starward?.Enabled != true) return;
        string? expected = request.PresetId switch { "zzz" => "ZenlessZoneZero.exe", "hi3" => "BH3.exe", _ => null };
        if (expected is null || !Path.GetFileName(request.NativeExe).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Starward 需要对应国服的实际游戏 EXE，请检查本地预设和路径。");
        string config = Path.Combine(Path.GetDirectoryName(request.NativeExe)!, "config.ini");
        if (File.Exists(config))
        {
            string? marker = File.ReadLines(config).Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("game_biz=", StringComparison.OrdinalIgnoreCase));
            if (marker is not null && !marker.Equals("game_biz=" + GamePreset.Find(request.PresetId).StarwardBiz, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("本地客户端地区标识与选择的国服预设不符。");
        }
    }
    internal static bool BridgeRunning(string appId)
    {
        using var mutex = new Mutex(false, MutexName(appId));
        try { if (!mutex.WaitOne(0)) return true; mutex.ReleaseMutex(); return false; }
        catch (AbandonedMutexException) { mutex.ReleaseMutex(); return false; }
    }
}
