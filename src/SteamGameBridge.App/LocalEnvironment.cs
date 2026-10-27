using SteamGameBridge.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace SteamGameBridge.App;

internal static class LocalEnvironment
{
    internal static string ExecutableDirectory => Path.GetDirectoryName(Environment.ProcessPath ?? throw new IOException("无法定位当前程序。"))!;
    internal static string StateDirectory => DataLocationSettings.Read(ExecutableDirectory) ?? throw new IOException("请先选择数据保存位置。");
    internal static IReadOnlyList<string> RecognizedStateDirectories => new[] { "SteamGameBridge", "ZZZSteamBridge" }.Select(name => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name)).ToArray();
    internal static string SteamRoot()
    {
        string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.Combine(path, "steam.exe"))) throw new IOException("未找到当前 Windows 用户的 Steam，请先安装并登录 Steam。");
        return Path.GetFullPath(path);
    }

    internal static bool SteamRunning() => AnyProcess("steam");
    internal static string? ActiveSteamAccount()
    {
        object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
        uint id = value is int signed ? unchecked((uint)signed) : value is uint unsigned ? unsigned : 0;
        return id == 0 ? null : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static bool Busy(GameProfile profile) => SteamRunning() || NativeLauncher.BridgeRunning(profile.AppId) || (!string.IsNullOrWhiteSpace(profile.Request.NativeExe) && GameProcessTracker.IsRunning(profile.Request.NativeExe));
    private static bool AnyProcess(string name)
    {
        var processes = Process.GetProcessesByName(name);
        bool result = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return result;
    }

    internal static void GuardPath(string path)
    {
        for (string? item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持目录链接或重解析路径：" + item);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static (string Helper, string Placeholder) PlanTools(string state)
    {
        string source = Environment.ProcessPath ?? throw new IOException("无法定位当前工具。");
        if (!Path.GetFileName(source).Equals("SteamGameBridge.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请直接运行已发布的 SteamGameBridge.exe。");
        string version = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))[..16].ToLowerInvariant();
        string directory = Path.Combine(state, "bin", version); GuardPath(directory);
        return (Path.Combine(directory, "SteamGameBridge.exe"), Path.Combine(directory, "SteamPlaceholder.exe"));
    }
    internal static (string Helper, string Placeholder) StageTools(string state)
    {
        using var dataLock = DataLocationSettings.AcquireLock(state);
        var plan = PlanTools(state); string source = Environment.ProcessPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Helper)!);
        GuardPath(plan.Helper);
        if (!File.Exists(plan.Helper)) File.Copy(source, plan.Helper, false);
        if (!SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(plan.Helper)))) throw new IOException("稳定目录中的工具校验失败，拒绝覆盖。");
        GuardPath(plan.Placeholder);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamPlaceholder.exe") ?? throw new IOException("缺少占位程序。");
        using var content = new MemoryStream(); resource.CopyTo(content); byte[] bytes = content.ToArray();
        if (!File.Exists(plan.Placeholder)) { using var file = new FileStream(plan.Placeholder, FileMode.CreateNew); file.Write(bytes); }
        if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(plan.Placeholder)))) throw new IOException("占位程序校验失败。");
        return plan;
    }
}
