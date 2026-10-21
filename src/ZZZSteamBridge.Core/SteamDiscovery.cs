using System.Globalization;

namespace ZZZSteamBridge.Core;

public static class SteamDiscovery
{
    public static IReadOnlyList<string> ReadLibraries(string steamRoot)
    {
        steamRoot = PathSafety.Full(steamRoot);
        PathSafety.NoReparse(steamRoot);
        var path = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        PathSafety.NoReparse(path);
        var result = new List<string> { steamRoot };
        if (!File.Exists(path)) return result;
        var nodes = ValveKeyValues.Parse(File.ReadAllText(path));
        var folder = ValveKeyValues.Unique(nodes, "libraryfolders") ?? throw new BridgeException("Steam 游戏库清单格式无效。");
        foreach (var n in folder.Children ?? throw new BridgeException("Steam 游戏库清单格式无效。"))
        {
            if (!uint.TryParse(n.Key, out _)) continue;
            var value = n.Children is not null ? ValveKeyValues.Unique(n.Children, "path")?.Value : n.Value;
            if (string.IsNullOrWhiteSpace(value)) throw new BridgeException("Steam 游戏库路径为空。");
            var full = PathSafety.Full(value); PathSafety.NoReparse(full);
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
        }
        return result;
    }

    public static AccountDiscovery ReadAccounts(string steamRoot)
    {
        var path = Path.Combine(steamRoot, "config", "loginusers.vdf"); PathSafety.NoReparse(path);
        return File.Exists(path) ? ParseLoginUsers(File.ReadAllText(path), steamRoot) : new([], null);
    }

    public static AccountDiscovery ParseLoginUsers(string text, string steamRoot)
    {
        var nodes = ValveKeyValues.Parse(text);
        var users = ValveKeyValues.Unique(nodes, "users")?.Children ?? throw new BridgeException("Steam 登录用户清单格式无效。");
        var accounts = new List<SteamAccount>();
        foreach (var user in users)
        {
            if (!ulong.TryParse(user.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 76561197960265728UL || id - 76561197960265728UL > uint.MaxValue || user.Children is null) continue;
            var accountId = (id - 76561197960265728UL).ToString(CultureInfo.InvariantCulture);
            if (accounts.Any(a => a.AccountId == accountId)) throw new BridgeException("Steam 用户清单包含重复账号。");
            var config = Path.Combine(PathSafety.Full(steamRoot), "userdata", accountId, "config", "localconfig.vdf");
            var persona = ValveKeyValues.Unique(user.Children, "PersonaName")?.Value ?? $"Steam 用户 {accountId}";
            var recent = ValveKeyValues.Unique(user.Children, "MostRecent")?.Value == "1";
            accounts.Add(new(accountId, persona, config, recent));
        }
        var candidates = accounts.Where(x => x.MostRecent).ToArray();
        return new(accounts, candidates.Length == 1 ? candidates[0] : null);
    }
}

internal static class PathSafety
{
    internal static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '"', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path)) throw new BridgeException("需要有效的绝对路径。");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0) throw new BridgeException("不支持网络路径、设备路径或备用数据流。");
        return full;
    }
    internal static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    internal static bool Under(string path, string directory) => Full(path).StartsWith(Full(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void NoReparse(string path)
    {
        var full = Full(path); var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("路径含符号链接、目录联接或其他重解析点，已停止操作。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    internal static string Within(string root, string relative)
    {
        SteamAppMetadata.ValidateRelativePath(relative, true);
        var path = Full(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!Under(path, root)) throw new BridgeException("路径超出独立接入目录。");
        NoReparse(path); return path;
    }
    internal static void RequireExe(string path, string? expectedName = null)
    {
        NoReparse(path);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || expectedName is not null && !Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("请选择实际存在的正确 EXE 文件。");
        using var input = File.OpenRead(path);
        if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new BridgeException("选择的文件没有有效 EXE 标头。");
    }
}
