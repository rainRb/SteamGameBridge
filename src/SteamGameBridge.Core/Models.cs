using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamGameBridge.Core;

public sealed class BridgeException(string message) : Exception(message);

public sealed record SteamAppMetadata(string AppId, string Name, string InstallDirectory, string Executable, string BuildId, DateTimeOffset FetchedAt)
{
    public IReadOnlyList<SteamDepot> Depots { get; init; } = [];
    public string LaunchId { get; init; } = "";
    public IReadOnlyList<SteamLaunchEntry> LaunchEntries { get; init; } = [];

    public static SteamAppMetadata ParseApiSteamCmdJson(string json, string appId, string? launchId = null)
    {
        try
        {
            ValidateAppId(appId);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (!root.GetProperty("status").GetString()!.Equals("success", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("Steam 元数据接口未返回成功结果。");
            var app = root.GetProperty("data").GetProperty(appId);
            var name = app.GetProperty("common").GetProperty("name").GetString()!;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Any(char.IsControl)) throw new BridgeException("Steam 游戏名称无效。");
            var config = app.GetProperty("config");
            var install = config.GetProperty("installdir").GetString()!;
            ValidateRelativePath(install, false);
            var launches = ReadLaunchEntries(app);
            var selected = launchId is null ? (launches.Count == 1 ? launches[0] : throw new BridgeException("存在多个 Windows 启动入口，请明确选择。"))
                : launches.SingleOrDefault(x => x.Id == launchId) ?? throw new BridgeException("选定的 Windows 启动入口已不存在。");
            var executable = selected.Executable;
            var build = app.GetProperty("depots").GetProperty("branches").GetProperty("public").GetProperty("buildid").GetString()!;
            if (!PositiveId(build)) throw new BridgeException("Steam 公共分支版本号无效。");
            var depots = new List<SteamDepot>();
            foreach (var d in app.GetProperty("depots").EnumerateObject())
            {
                if (!Regex.IsMatch(d.Name, "^[0-9]+$") || d.Value.ValueKind != JsonValueKind.Object) continue;
                if (d.Value.TryGetProperty("config", out var dc) && dc.TryGetProperty("oslist", out var dos) && !dos.GetString()!.Split(',').Contains("windows")) continue;
                if (!d.Value.TryGetProperty("manifests", out var dm) || !dm.TryGetProperty("public", out var pub)) continue;
                var gid = pub.GetProperty("gid").GetString()!;
                var size = pub.GetProperty("size").GetString()!;
                if (!PositiveId(d.Name) || !PositiveId(gid) || !PositiveId(size)) throw new BridgeException("Steam depot 清单格式无效。");
                depots.Add(new(d.Name, gid, size));
            }
            if (depots.Count == 0) throw new BridgeException("Steam 公共 depot 清单为空，拒绝使用猜测的安装元数据。");
            return new(appId, name, install, executable, build, DateTimeOffset.UtcNow) { Depots = depots, LaunchId = selected.Id, LaunchEntries = launches };
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException or ArgumentException)
        { throw new BridgeException("Steam 元数据缺失或格式无效，未更改任何配置。"); }
    }

    public static void ValidateAppId(string appId)
    {
        if (!uint.TryParse(appId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id == 0 || id.ToString(System.Globalization.CultureInfo.InvariantCulture) != appId)
            throw new BridgeException("Steam AppID 必须是有效的正整数。");
    }

    public static IReadOnlyList<SteamLaunchEntry> ReadLaunchChoices(string json, string appId)
    {
        ValidateAppId(appId);
        try { using var doc = JsonDocument.Parse(json); return ReadLaunchEntries(doc.RootElement.GetProperty("data").GetProperty(appId)); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        { throw new BridgeException("Steam 元数据缺少 Windows 启动信息。"); }
    }

    private static IReadOnlyList<SteamLaunchEntry> ReadLaunchEntries(JsonElement app)
    {
        var result = new List<SteamLaunchEntry>();
        foreach (var item in app.GetProperty("config").GetProperty("launch").EnumerateObject())
        {
            var x = item.Value;
            if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("executable", out var exe)) continue;
            if (x.TryGetProperty("config", out var c) && c.TryGetProperty("oslist", out var os) && !os.GetString()!.Split(',').Any(v => v.Trim().Equals("windows", StringComparison.OrdinalIgnoreCase))) continue;
            string executable = exe.GetString()!.Replace('/', '\\');
            ValidateRelativePath(executable, true);
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var description = x.TryGetProperty("description", out var desc) ? desc.GetString() : null;
            if (result.Any(e => e.Id == item.Name)) throw new BridgeException("Steam 启动入口编号重复。");
            result.Add(new(item.Name, description ?? executable, executable));
        }
        if (result.Count == 0) throw new BridgeException("没有可用的 Windows EXE 启动入口。");
        return result;
    }

    internal static bool PositiveId(string value) => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit) && ulong.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;

    internal static void ValidateRelativePath(string value, bool nested)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 180 || value.StartsWith('\\') || value.StartsWith('/') || value.Contains(':') || value.Contains('"') || value.Any(char.IsControl))
            throw new BridgeException("Steam 元数据含不安全路径。");
        var parts = value.Replace('/', '\\').Split('\\');
        if ((!nested && parts.Length != 1) || parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ') || x.IndexOfAny(['*', '?', '<', '>', '|']) >= 0 || Regex.IsMatch(x, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase)))
            throw new BridgeException("Steam 元数据含不安全路径段。");
    }
}

public sealed class BridgeServiceOptions
{
    public required string StateDirectory { get; init; }
    public required string SteamRoot { get; init; }
    public required string AppId { get; init; }
    public Func<bool> IsBusy { get; init; } = () => false;
    public required IReadOnlyList<string> RegisteredLibraries { get; init; }
}

public sealed record SteamDepot(string DepotId, string ManifestId, string Size);
public sealed record SetupRequest(string SteamRoot, string LibraryRoot, string AccountConfigPath, string NativeExe, string HelperExe, string? PlaceholderExe = null, string GameArguments = "")
{
    public string PresetId { get; init; } = "custom";
    public LaunchMode LaunchMode { get; init; }
    public string LauncherExe { get; init; } = "";
    public string LauncherArguments { get; init; } = "";
    public int WaitSeconds { get; init; } = 180;
    public StarwardIntegrationOptions? Starward { get; init; }
    public ExistingLaunchOptionsAction ExistingOptionsAction { get; init; } = ExistingLaunchOptionsAction.Keep;
    public string? ExpectedOriginalLaunchOptions { get; init; }
    public bool OriginalOptionsReviewed { get; init; }
}
public sealed record SetupPreview(string ManifestPath, string PlaceholderDirectory, string LaunchOptions, string MetadataBuildId, IReadOnlyList<string> Warnings);
public sealed record OperationResult(string ReceiptId, string Message, SetupPreview? Preview = null);
public sealed record Inspection(bool Installed, string? ReceiptId, string Status, IReadOnlyList<string> Conflicts, SetupPreview? Preview);
public sealed record SteamAccount(string AccountId, string DisplayName, string LocalConfigPath, bool MostRecent);
public sealed record AccountDiscovery(IReadOnlyList<SteamAccount> Accounts, SteamAccount? SuggestedAccount);

public sealed class BridgeReceipt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Schema { get; set; } = 1;
    public string Phase { get; set; } = "prepared";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required SetupRequest Request { get; set; }
    public required SteamAppMetadata Metadata { get; set; }
    public required SetupPreview Preview { get; set; }
    public string? OriginalLaunchOptions { get; set; }
    public bool OriginalLaunchOptionsExists { get; set; }
    public string OriginalConfigHash { get; set; } = "";
    public string ConfigBackupFile { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public Dictionary<string, string> OwnedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> RecoveryFileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RecoveryManifestHashes { get; set; } = [];
    public string? LastError { get; set; }
}
