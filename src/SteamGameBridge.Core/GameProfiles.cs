using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamGameBridge.Core;

public sealed record LaunchSpec(string AppId, SetupRequest Request);

public enum LaunchMode { Direct, OfficialLauncher, ExistingWrapper, Starward }
public enum ExistingLaunchOptionsAction { Keep, PreserveArguments, Replace }
public sealed record SteamLaunchEntry(string Id, string Description, string Executable)
{
    public override string ToString() => $"{Id}: {Description} ({Executable})";
}
public sealed record GamePreset(string Id, string Name, string AppId, string? StarwardBiz, LaunchMode DefaultMode)
{
    public static IReadOnlyList<GamePreset> All { get; } = [
        new("endfield", "明日方舟：终末地", "4732690", null, LaunchMode.OfficialLauncher),
        new("zzz", "绝区零", "4162040", "nap_cn", LaunchMode.Direct),
        new("wuthering", "鸣潮", "3513350", null, LaunchMode.Direct),
        new("hi3", "崩坏3", "1671200", "bh3_cn", LaunchMode.Direct),
        new("custom", "自定义游戏", "", null, LaunchMode.Direct)
    ];
    public static GamePreset Find(string id) => All.SingleOrDefault(x => x.Id == id) ?? throw new BridgeException("未知游戏预设。");
    public override string ToString() => Name;
}
public sealed record StarwardIntegrationOptions(bool Enabled, string? Executable = null);

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新游戏";
    public string AppId { get; set; } = "";
    public string LaunchId { get; set; } = "";
    public required SetupRequest Request { get; set; }
    public bool Maintain { get; set; } = true;
    public override string ToString() => $"{Name} · {AppId}";
}

public static class BridgeIdentity
{
    public static string Key(string steamRoot, string appId)
    {
        SteamAppMetadata.ValidateAppId(appId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.Full(steamRoot).ToUpperInvariant())))[..20] + "-" + appId;
    }
    public static string IntegrationDirectory(string stateRoot, string steamRoot, string appId) => Path.Combine(PathSafety.Full(stateRoot), "integrations", Key(steamRoot, appId));
}

/// <summary>New product state only. Never probes or migrates the previous application's data.</summary>
public sealed class ProfileStore(string stateRoot)
{
    private string FilePath => Path.Combine(PathSafety.Full(stateRoot), "profiles.json");
    public IReadOnlyList<GameProfile> Load()
    {
        PathSafety.NoReparse(FilePath);
        if (!File.Exists(FilePath)) return [];
        var profiles = JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllBytes(FilePath)) ?? throw new BridgeException("游戏配置为空。");
        var ids = new HashSet<string>(); var keys = new HashSet<string>();
        foreach (var p in profiles)
        {
            if (p is null || p.Request is null || !Guid.TryParseExact(p.Id, "N", out _) || !ids.Add(p.Id)) throw new BridgeException("游戏配置编号无效或重复。");
            if (!keys.Add(BridgeIdentity.Key(p.Request.SteamRoot, p.AppId))) throw new BridgeException("同一 Steam 安装和 AppID 只能有一份配置。");
        }
        return profiles;
    }
    public void Save(GameProfile profile)
    {
        PathSafety.NoReparse(FilePath); Directory.CreateDirectory(PathSafety.Full(stateRoot));
        PathSafety.NoReparse(Path.Combine(stateRoot, ".profiles.lock"));
        using var fileLock = new FileStream(Path.Combine(stateRoot, ".profiles.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var list = Load().ToList();
        if (!Guid.TryParseExact(profile.Id, "N", out _)) throw new BridgeException("配置编号无效。");
        string key = BridgeIdentity.Key(profile.Request.SteamRoot, profile.AppId);
        if (list.Any(p => p.Id != profile.Id && BridgeIdentity.Key(p.Request.SteamRoot, p.AppId) == key)) throw new BridgeException("此 Steam 安装的 AppID 已有配置，请先选择现有配置。");
        list.RemoveAll(p => p.Id == profile.Id); list.Add(profile); Write(list);
    }
    public void Delete(string id)
    {
        PathSafety.NoReparse(FilePath); Directory.CreateDirectory(PathSafety.Full(stateRoot));
        PathSafety.NoReparse(Path.Combine(stateRoot, ".profiles.lock"));
        using var fileLock = new FileStream(Path.Combine(stateRoot, ".profiles.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var list = Load().ToList(); list.RemoveAll(p => p.Id == id); Write(list);
    }
    private void Write(List<GameProfile> list)
    {
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, FilePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class LaunchConfiguration
{
    public static void Validate(SetupRequest request)
    {
        GamePreset.Find(request.PresetId);
        if (!Enum.IsDefined(request.LaunchMode) || !Enum.IsDefined(request.ExistingOptionsAction) || request.WaitSeconds is < 1 or > 1800) throw new BridgeException("启动方式或等待时间无效（1–1800 秒）。");
        PathSafety.RequireExe(request.NativeExe);
        ValidateArguments(request.GameArguments);
        if (request.LaunchMode == LaunchMode.Starward)
        {
            _ = BuildStarwardStartUri(request);
            if (!string.IsNullOrWhiteSpace(request.GameArguments)) throw new BridgeException("使用 Starward 启动时，请在 Starward 中设置游戏参数。");
        }
        if (request.LaunchMode is LaunchMode.OfficialLauncher or LaunchMode.ExistingWrapper)
        {
            PathSafety.RequireExe(request.LauncherExe);
            if (PathSafety.Same(request.NativeExe, request.LauncherExe)) throw new BridgeException("启动器与实际游戏 EXE 必须分别配置。");
            if (request.LauncherArguments.Contains("login_auth_ticket", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("请勿保存登录票据。");
            _ = ExpandLauncherArguments(request, []);
        }
        if (request.Starward?.Enabled == true || request.LaunchMode == LaunchMode.Starward)
        {
            if (GamePreset.Find(request.PresetId).StarwardBiz is null) throw new BridgeException("此本地预设不支持 Starward 计时。");
            if (request.Starward?.Executable is { Length: > 0 } executable) PathSafety.Full(executable);
        }
    }
    public static string BuildStarwardStartUri(SetupRequest request)
    {
        string biz = GamePreset.Find(request.PresetId).StarwardBiz ?? throw new BridgeException("此游戏不支持 Starward 启动。");
        string directory = Path.GetDirectoryName(PathSafety.Full(request.NativeExe))!;
        return $"starward://startgame/{biz}?install_path={Uri.EscapeDataString(directory)}";
    }

    public static void ValidateArguments(string text)
    {
        if (text.Any(c => c is '\0' or '\r' or '\n') || text.Contains("%command%", StringComparison.OrdinalIgnoreCase) || text.Contains("--steam-command", StringComparison.OrdinalIgnoreCase) || text.Contains("login_auth_ticket", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("游戏参数包含无效或保留的桥接/Steam 登录参数；请勿保存票据。");
        _ = WindowsArguments.Parse(text);
    }
    public static bool IsComplexCommand(string text) => text.Contains("%command%", StringComparison.OrdinalIgnoreCase) || text.IndexOfAny(['&', '|', '>', '<', '\r', '\n']) >= 0
        || WindowsArguments.Parse(text).Any(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || x.Contains(".exe\"", StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<string> ExpandLauncherArguments(SetupRequest request, IReadOnlyList<string> steamCommand)
    {
        var result = new List<string>();
        foreach (string token in WindowsArguments.Parse(request.LauncherArguments))
        {
            switch (token)
            {
                case "{game}": result.Add(request.NativeExe); break;
                case "{gameArgs}": result.AddRange(WindowsArguments.Parse(request.GameArguments)); break;
                case "{steamCommand}": result.AddRange(steamCommand); break;
                default:
                    if (token.Contains('{') || token.Contains('}') || token.Contains("%command%", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("包装器占位符须为独立参数：{game}、{gameArgs}、{steamCommand}。");
                    result.Add(token); break;
            }
        }
        return result;
    }
}

/// <summary>Windows CRT argument rules, independent of shells; no commands are evaluated.</summary>
public static class WindowsArguments
{
    public static string Quote(string value)
    {
        var b = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') b.Append('\\', slashes * 2 + 1).Append(c);
            else b.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));
    public static IReadOnlyList<string> Parse(string text)
    {
        if (text.Any(c => c is '\0' or '\r' or '\n')) throw new BridgeException("参数含无效控制字符。");
        var result = new List<string>(); int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length) break;
            var b = new StringBuilder(); bool quoted = false;
            while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
            {
                int n = 0; while (i < text.Length && text[i] == '\\') { n++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    b.Append('\\', n / 2); i++;
                    if (n % 2 == 1) b.Append('"');
                    else if (quoted && i < text.Length && text[i] == '"') { b.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else { b.Append('\\', n); if (i < text.Length && (quoted || !char.IsWhiteSpace(text[i]))) b.Append(text[i++]); }
            }
            if (quoted) throw new BridgeException("参数中的引号未闭合。");
            result.Add(b.ToString());
        }
        return result;
    }
}
