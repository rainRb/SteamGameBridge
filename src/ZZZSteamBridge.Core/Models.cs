using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZZZSteamBridge.Core;

public sealed class BridgeException(string message) : Exception(message);

public sealed record SteamAppMetadata(string Name, string InstallDirectory, string Executable, string BuildId, DateTimeOffset FetchedAt)
{
    public IReadOnlyList<SteamDepot> Depots { get; init; } = [];
    public const string AppId = "4162040";

    public static SteamAppMetadata ParseApiSteamCmdJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (!root.GetProperty("status").GetString()!.Equals("success", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("Steam 元数据接口未返回成功结果。");
            var app = root.GetProperty("data").GetProperty(AppId);
            var name = app.GetProperty("common").GetProperty("name").GetString()!;
            if (!name.Contains("Zenless Zone Zero", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("元数据中的游戏名称与绝区零不符。");
            var config = app.GetProperty("config");
            var install = config.GetProperty("installdir").GetString()!;
            ValidateRelativePath(install, false);
            var launches = config.GetProperty("launch").EnumerateObject()
                .Select(x => x.Value)
                .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("executable", out _))
                .Where(x => !x.TryGetProperty("config", out var c) || !c.TryGetProperty("oslist", out var os) || os.GetString()!.Split(',').Contains("windows"))
                .Where(x => !x.TryGetProperty("type", out var t) || t.GetString() is "default" or "none")
                .ToArray();
            if (launches.Length != 1) throw new BridgeException("Windows 默认启动项不唯一，拒绝猜测 Steam 的启动程序。");
            var executable = launches[0].GetProperty("executable").GetString()!.Replace('/', '\\');
            ValidateRelativePath(executable, true);
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("Steam 启动程序不是 EXE。");
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
            return new(name, install, executable, build, DateTimeOffset.UtcNow) { Depots = depots };
        }
        catch (BridgeException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException or ArgumentException)
        { throw new BridgeException("Steam 元数据缺失或格式无效，未更改任何配置。"); }
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
    public Func<bool> IsBusy { get; init; } = () => false;
    public required IReadOnlyList<string> RegisteredLibraries { get; init; }
}

public sealed record SteamDepot(string DepotId, string ManifestId, string Size);
public sealed record SetupRequest(string SteamRoot, string LibraryRoot, string AccountConfigPath, string NativeExe, string HelperExe, string? PlaceholderExe = null, string GameArguments = "");
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
