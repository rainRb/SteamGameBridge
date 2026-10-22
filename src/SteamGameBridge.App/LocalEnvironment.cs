using SteamGameBridge.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace SteamGameBridge.App;

internal static class LocalEnvironment
{
    internal static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamGameBridge");
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

    internal static (string Helper, string Placeholder) StageTools(string state)
    {
        string source = Environment.ProcessPath ?? throw new IOException("无法定位当前工具。");
        if (!Path.GetFileName(source).Equals("SteamGameBridge.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请直接运行已发布的 SteamGameBridge.exe。");
        string version = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))[..16].ToLowerInvariant();
        string directory = Path.Combine(state, "bin", version);
        GuardPath(directory);
        Directory.CreateDirectory(directory);
        string helper = Path.Combine(directory, "SteamGameBridge.exe");
        if (!Path.GetFullPath(source).Equals(helper, StringComparison.OrdinalIgnoreCase))
        {
            GuardPath(helper);
            if (!File.Exists(helper)) File.Copy(source, helper, false);
            if (!SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(helper))))
                throw new IOException("稳定目录中的工具校验失败，拒绝覆盖。");
        }
        string placeholder = Path.Combine(directory, "SteamPlaceholder.exe");
        GuardPath(placeholder);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamPlaceholder.exe") ?? throw new IOException("缺少占位程序。");
        using var content = new MemoryStream(); resource.CopyTo(content);
        byte[] bytes = content.ToArray();
        if (!File.Exists(placeholder)) { using var file = new FileStream(placeholder, FileMode.CreateNew); file.Write(bytes); }
        if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(placeholder)))) throw new IOException("占位程序校验失败。");
        return (helper, placeholder);
    }
}
