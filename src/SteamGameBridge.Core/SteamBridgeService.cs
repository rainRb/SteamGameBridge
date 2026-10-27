using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamGameBridge.Core;

/// <summary>Owns only its isolated Steam placeholder, ACF and one selected user's LaunchOptions.</summary>
public sealed partial class SteamBridgeService(BridgeServiceOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string State => BridgeIdentity.IntegrationDirectory(options.StateDirectory, options.SteamRoot, options.AppId);
    private string Active => Path.Combine(State, "active.json");
    private const string MarkerName = ".steam-game-bridge-owner.json";

    public SetupPreview Preview(SetupRequest request, SteamAppMetadata metadata)
    {
        ValidateRequest(request, metadata);
        var manifest = Path.Combine(PathSafety.Full(request.LibraryRoot), "steamapps", $"appmanifest_{options.AppId}.acf");
        var game = Path.Combine(PathSafety.Full(request.LibraryRoot), "steamapps", "common", metadata.InstallDirectory);
        var command = WindowsArguments.Quote(request.HelperExe) + " --profile " + WindowsArguments.Quote(Path.Combine(State, "launch.json")) + " --steam-command %command%";
        return new(manifest, game, command, metadata.BuildId,
            ["工具仅下载 Steam 元数据，不下载国际服游戏资源。Steam 客户端更新、校验或元数据变更仍可能触发下载；请勿点击验证完整性或强制更新。", "正式游戏识别、游玩时长、好友状态与 Overlay 需在实际 Steam 客户端中验证；本工具不注入游戏、不绕过反作弊。"]);
    }

    public OperationResult Apply(SetupRequest request, SteamAppMetadata metadata)
    {
        if (request.ExistingInstallationAction != ExistingInstallAction.Cancel) return ApplyReplacingExisting(request, metadata);
        if (HasPendingExistingResolution && request.ResolutionId is null) throw new BridgeException("有待恢复的安装处理，请先点击恢复。");
        EnsureIdle(); ValidateRequest(request, metadata, requireStagedTools: true); var preview = Preview(request, metadata);
        using var stateLock = Lock();
        if (File.Exists(Active)) throw new BridgeException("已有接入记录；请检查、修复或恢复该记录后再接入。");
        var config = ReadText(request.AccountConfigPath);
        var original = ValveKeyValues.ReadLaunchOptions(config.Text, options.AppId);
        if (request.ExistingOptionsAction == ExistingLaunchOptionsAction.Keep && !string.IsNullOrWhiteSpace(original.Value))
            return new("", "已保留原设置，本次未接入。");
        if (request.OriginalOptionsReviewed && request.ExpectedOriginalLaunchOptions != original.Value) throw new BridgeException("原启动项在确认后发生变化，请重新预览。");
        if (!string.IsNullOrWhiteSpace(original.Value) && (!request.OriginalOptionsReviewed || request.ExistingOptionsAction == ExistingLaunchOptionsAction.Keep))
            throw new BridgeException("已有启动项，请先明确选择保留参数或完全替换并确认预览。");
        RejectForeignInstall(request, preview);
        var receipt = new BridgeReceipt { ResolutionId = request.ResolutionId, ExistingInstallationAction = request.ResolutionId is null ? ExistingInstallAction.Cancel : LoadResolutionAction(request.ResolutionId), Request = request, Metadata = metadata, Preview = preview, OriginalLaunchOptions = original.Value, OriginalLaunchOptionsExists = original.Exists, OriginalConfigHash = HashFile(request.AccountConfigPath) };
        var backupDir = Path.Combine(State, "backups", receipt.Id); PathSafety.NoReparse(backupDir); Directory.CreateDirectory(backupDir);
        receipt.ConfigBackupFile = Path.Combine(backupDir, "localconfig.vdf.original");
        AtomicNew(receipt.ConfigBackupFile, File.ReadAllBytes(request.AccountConfigPath));
        Save(receipt);
        try
        {
            EnsureIdle();
            if (HashFile(request.AccountConfigPath) != receipt.OriginalConfigHash) throw new BridgeException("Steam 配置在预览后发生变化，已停止接入。");
            // Write the recovery receipt before any external file is created.
            var files = PlannedFiles(receipt, metadata);
            receipt.OwnedFiles = files.ToDictionary(x => x.Key, x => HashBytes(x.Value), StringComparer.OrdinalIgnoreCase);
            var manifestBytes = Encoding.UTF8.GetBytes(RenderManifest(receipt));
            receipt.ManifestHash = HashBytes(manifestBytes); Save(receipt);
            Directory.CreateDirectory(preview.PlaceholderDirectory);
            foreach (var file in files)
            {
                var target = PathSafety.Within(preview.PlaceholderDirectory, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); AtomicNew(target, file.Value);
            }
            EnsureIdle(); AtomicNew(preview.ManifestPath, manifestBytes);
            EnsureIdle();
            if (HashFile(request.AccountConfigPath) != receipt.OriginalConfigHash) throw new BridgeException("Steam 配置已变化，保留恢复记录且未覆盖配置。");
            WriteTextAtomic(request.AccountConfigPath, config, ValveKeyValues.SetLaunchOptions(config.Text, options.AppId, preview.LaunchOptions));
            receipt.Phase = "active"; Save(receipt);
            return new(receipt.Id, "已接入；启动选项已写入所选 Steam 用户。请打开 Steam 后从正式游戏条目测试。", preview);
        }
        catch (Exception ex)
        {
            receipt.Phase = "recovery-required"; receipt.LastError = ex.Message; Save(receipt);
            throw new BridgeException("接入未完整完成，恢复记录已保留。请在 Steam 完全退出后点击恢复。原因：" + ex.Message);
        }
    }

    public OperationResult Repair(SteamAppMetadata freshMetadata)
    {
        EnsureIdle(); using var stateLock = Lock(); var receipt = LoadRequired();
        ValidateRequest(receipt.Request, freshMetadata, requireStagedTools: true);
        var freshPreview = Preview(receipt.Request, freshMetadata);
        if (!PathSafety.Same(freshPreview.PlaceholderDirectory, receipt.Preview.PlaceholderDirectory))
            throw new BridgeException("Steam 安装目录已变更；请先恢复旧接入，再按新元数据接入。");
        var conflicts = FindConflicts(receipt, allowMissing: true, includeConfig: false);
        if (conflicts.Count != 0) throw new BridgeException("修复已停止，检测到非本工具变更：" + string.Join("；", conflicts));
        if (receipt.Phase != "active") throw new BridgeException("存在未完成操作，请先恢复。");
        var config = ReadText(receipt.Request.AccountConfigPath); var value = ValveKeyValues.ReadLaunchOptions(config.Text, options.AppId);
        if (value.Exists && !string.IsNullOrEmpty(value.Value) && value.Value != receipt.Preview.LaunchOptions) throw new BridgeException("所选用户的启动选项已被更改，请先恢复并重新预览接入。");
        // Plan all bytes before any writes; preserve a recovery copy of the previous owned manifest.
        var nextFiles = PlannedFiles(receipt, freshMetadata);
        var nextHashes = nextFiles.ToDictionary(x => x.Key, x => HashBytes(x.Value), StringComparer.OrdinalIgnoreCase);
        var oldReceipt = JsonSerializer.Serialize(receipt, JsonOptions);
        AtomicWrite(Path.Combine(Path.GetDirectoryName(receipt.ConfigBackupFile)!, "receipt.before-repair.json"), Encoding.UTF8.GetBytes(oldReceipt));
        AtomicWrite(Path.Combine(Path.GetDirectoryName(receipt.ConfigBackupFile)!, "localconfig.before-repair.vdf"), File.ReadAllBytes(receipt.Request.AccountConfigPath));
        var previousManifest = File.Exists(receipt.Preview.ManifestPath) ? ReadText(receipt.Preview.ManifestPath) : null;
        var previousMetadata = receipt.Metadata;
        if (File.Exists(receipt.Preview.ManifestPath)) AtomicWrite(Path.Combine(Path.GetDirectoryName(receipt.ConfigBackupFile)!, "manifest.before-repair.acf"), File.ReadAllBytes(receipt.Preview.ManifestPath));
        var oldFiles = new Dictionary<string, string>(receipt.OwnedFiles, StringComparer.OrdinalIgnoreCase);
        receipt.RecoveryManifestHashes = [receipt.ManifestHash];
        if (previousManifest is not null) receipt.RecoveryManifestHashes.Add(HashBytes(EncodeText(previousManifest, previousManifest.Text)));
        receipt.RecoveryFileHashes = oldFiles.ToDictionary(x => x.Key, x => new List<string> { x.Value }, StringComparer.OrdinalIgnoreCase);
        receipt.OwnedFiles = new Dictionary<string, string>(oldFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in nextHashes) receipt.OwnedFiles[file.Key] = file.Value;
        receipt.Metadata = freshMetadata; receipt.Preview = freshPreview;
        var manifestText = previousManifest is null ? RenderManifest(receipt) : RepairManifest(previousManifest.Text, receipt, previousMetadata);
        var manifest = previousManifest is null ? Encoding.UTF8.GetBytes(manifestText) : EncodeText(previousManifest, manifestText);
        receipt.ManifestHash = HashBytes(manifest); receipt.Phase = "repairing";
        Save(receipt);
        try
        {
            EnsureIdle();
            foreach (var file in nextFiles)
            {
                var target = PathSafety.Within(receipt.Preview.PlaceholderDirectory, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); AtomicWrite(target, file.Value);
            }
            foreach (var old in oldFiles.Where(x => !nextHashes.ContainsKey(x.Key)))
            {
                var target = PathSafety.Within(receipt.Preview.PlaceholderDirectory, old.Key);
                if (File.Exists(target)) File.Delete(target);
            }
            EnsureIdle(); AtomicWrite(receipt.Preview.ManifestPath, manifest);
            EnsureIdle();
            if (HashFile(receipt.Request.AccountConfigPath) != HashBytes(EncodeText(config, config.Text))) throw new BridgeException("修复期间 Steam 用户配置发生变化，已停止写入启动选项。");
            if (!value.Exists || string.IsNullOrEmpty(value.Value)) WriteTextAtomic(receipt.Request.AccountConfigPath, config, ValveKeyValues.SetLaunchOptions(config.Text, options.AppId, freshPreview.LaunchOptions));
            receipt.OwnedFiles = nextHashes; receipt.RecoveryFileHashes.Clear(); receipt.RecoveryManifestHashes.Clear(); receipt.Phase = "active"; Save(receipt);
            return new(receipt.Id, "已使用刚获取的公共 Steam 元数据修复自有占位文件和安装清单。", freshPreview);
        }
        catch (Exception ex)
        {
            // The old record plus before-repair backup remains available; never silently hide partial repair.
            receipt.Phase = "recovery-required"; receipt.LastError = ex.Message; Save(receipt);
            throw new BridgeException("修复未完成，恢复记录及修复前备份已保留：" + ex.Message);
        }
    }

    public OperationResult Restore(string? receiptId = null)
    {
        EnsureIdle(); using var stateLock = Lock(); var receipt = LoadRequired();
        if (receiptId is not null && receiptId != receipt.Id) throw new BridgeException("恢复记录编号不匹配。");
        ValidateReceiptPaths(receipt);
        var conflicts = FindConflicts(receipt, allowMissing: true, includeConfig: false);
        var config = ReadText(receipt.Request.AccountConfigPath); var value = ValveKeyValues.ReadLaunchOptions(config.Text, options.AppId);
        var configOwned = value.Exists && value.Value == receipt.Preview.LaunchOptions;
        var configOriginal = value.Exists == receipt.OriginalLaunchOptionsExists && value.Value == receipt.OriginalLaunchOptions;
        // Restore own startup setting even if Steam downloaded content into the placeholder.
        if (configOwned)
        {
            EnsureIdle(); WriteTextAtomic(receipt.Request.AccountConfigPath, config, ValveKeyValues.SetLaunchOptions(config.Text, options.AppId, receipt.OriginalLaunchOptions, receipt.OriginalLaunchOptionsExists));
        }
        else if (!configOriginal) conflicts.Add("启动选项由其他程序或用户更改，已保留当前值");
        if (conflicts.Any(x => !x.StartsWith("启动选项", StringComparison.Ordinal)))
            throw new BridgeException("已恢复仍属于本工具的启动选项。检测到 Steam 下载或文件变化，未删除任何游戏库文件；恢复记录保留：" + string.Join("；", conflicts));
        EnsureIdle();
        if (File.Exists(receipt.Preview.ManifestPath)) File.Delete(receipt.Preview.ManifestPath);
        foreach (var file in receipt.OwnedFiles.Keys)
        {
            var path = PathSafety.Within(receipt.Preview.PlaceholderDirectory, file);
            if (File.Exists(path)) File.Delete(path);
        }
        DeleteEmptyOwnedDirectories(receipt.Preview.PlaceholderDirectory);
        receipt.Phase = "restored"; receipt.LastError = conflicts.Count == 0 ? null : string.Join("；", conflicts);
        var archive = Path.Combine(State, "backups", receipt.Id, "receipt.restored.json");
        AtomicWrite(archive, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, JsonOptions)));
        File.Delete(Active);
        return new(receipt.Id, conflicts.Count == 0 ? "已恢复原启动选项并移除本工具的占位安装。备份仍保留。" : "已移除占位安装；当前用户自改启动选项已保留。备份仍保留。");
    }

    public Inspection Inspect()
    {
        if (!File.Exists(Active)) return HasPendingExistingResolution ? new(true, null, "有待恢复的安装处理", ["请点击恢复继续处理。"], null) : new(false, null, "尚未接入", [], null);
        try
        {
            var receipt = LoadRequired(); ValidateReceiptPaths(receipt);
            var conflicts = FindConflicts(receipt, allowMissing: false);
            return new(true, receipt.Id, receipt.Phase == "active" && conflicts.Count == 0 ? "接入文件完整" : "需要检查或恢复", conflicts, receipt.Preview);
        }
        catch (Exception ex) when (ex is BridgeException or IOException or UnauthorizedAccessException or JsonException)
        { return new(true, null, "恢复记录无法安全读取", [ex.Message], null); }
    }

    public BridgeReceipt? ReadReceipt() => File.Exists(Active) ? LoadRequired() : null;

    private void ValidateRequest(SetupRequest request, SteamAppMetadata metadata, bool requireStagedTools = false)
    {
        SteamAppMetadata.ValidateAppId(options.AppId);
        if (!PathSafety.Same(request.SteamRoot, options.SteamRoot)) throw new BridgeException("Steam 安装位置与配置不一致。");
        LaunchConfiguration.Validate(request);
        if (metadata.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(5) || DateTimeOffset.UtcNow - metadata.FetchedAt > TimeSpan.FromMinutes(30)) throw new BridgeException("Steam 元数据已过期，请重新联网读取。");
        SteamAppMetadata.ValidateRelativePath(metadata.InstallDirectory, false); SteamAppMetadata.ValidateRelativePath(metadata.Executable, true);
        if (string.IsNullOrWhiteSpace(metadata.Name) || metadata.AppId != options.AppId || !SteamAppMetadata.PositiveId(metadata.BuildId) || metadata.Depots.Count == 0) throw new BridgeException("Steam 元数据无效。");
        foreach (var d in metadata.Depots) if (!SteamAppMetadata.PositiveId(d.DepotId) || !SteamAppMetadata.PositiveId(d.ManifestId) || !SteamAppMetadata.PositiveId(d.Size)) throw new BridgeException("Steam depot 元数据无效。");
        foreach (var path in new[] { State, request.SteamRoot, request.LibraryRoot, request.AccountConfigPath, request.NativeExe, request.HelperExe, request.PlaceholderExe ?? request.HelperExe }) PathSafety.NoReparse(path);
        if (!File.Exists(Path.Combine(request.SteamRoot, "steam.exe"))) throw new BridgeException("Steam 根目录没有 steam.exe。");
        if (!options.RegisteredLibraries.Any(x => PathSafety.Same(x, request.LibraryRoot))) throw new BridgeException("目标路径不是已登记的 Steam 游戏库。");
        var account = SteamDiscovery.ReadAccounts(request.SteamRoot).Accounts.FirstOrDefault(x => PathSafety.Same(x.LocalConfigPath, request.AccountConfigPath));
        if (account is null) throw new BridgeException("所选配置不属于 Steam 已登记的明确用户；请重新选择账号。");
        if (!File.Exists(request.AccountConfigPath)) throw new BridgeException("所选用户尚无 localconfig.vdf，请先正常登录 Steam 后退出。");
        PathSafety.RequireExe(request.NativeExe);
        if (requireStagedTools || request.SourceHelperExe is null) { PathSafety.RequireExe(request.HelperExe); PathSafety.RequireExe(request.PlaceholderExe ?? request.HelperExe); }
        else PathSafety.RequireExe(request.SourceHelperExe);
        var nativeDir = Path.GetDirectoryName(PathSafety.Full(request.NativeExe))!;
        foreach (var target in new[] { State, request.AccountConfigPath, Path.Combine(request.LibraryRoot, "steamapps", "common", metadata.InstallDirectory), Path.Combine(request.LibraryRoot, "steamapps", $"appmanifest_{options.AppId}.acf") })
            if (PathSafety.Same(target, nativeDir) || PathSafety.Under(target, nativeDir)) throw new BridgeException("接入写入位置与国服游戏目录重叠，已拒绝。");
        if (request.GameArguments.Any(c => c is '\0' or '\r' or '\n') || request.GameArguments.Contains("%command%", StringComparison.OrdinalIgnoreCase) || request.GameArguments.Contains("--steam-command", StringComparison.OrdinalIgnoreCase) || request.GameArguments.Contains("login_auth_ticket", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("游戏参数包含无效或保留的桥接/Steam 登录参数。");
        if (PathSafety.Under(request.NativeExe, Path.Combine(request.LibraryRoot, "steamapps", "common", metadata.InstallDirectory))) throw new BridgeException("国服路径不能指向 Steam 占位目录。");
    }

    private void ValidateReceiptPaths(BridgeReceipt receipt)
    {
        if (receipt.Request is null || receipt.Metadata is null || receipt.Metadata.AppId != options.AppId || !PathSafety.Same(receipt.Request.SteamRoot, options.SteamRoot)) throw new BridgeException("恢复记录与当前 AppID / Steam 安装不一致。");
        if (receipt.Schema != 1 || string.IsNullOrEmpty(receipt.Id) || receipt.Id.Length != 32 || receipt.Id.Any(c => !Uri.IsHexDigit(c)) || receipt.Request is null || receipt.Metadata is null || receipt.Preview is null || receipt.OwnedFiles is null || receipt.RecoveryFileHashes is null || receipt.RecoveryManifestHashes is null) throw new BridgeException("恢复记录格式无效。");
        var expectedManifest = Path.Combine(receipt.Request.LibraryRoot, "steamapps", $"appmanifest_{options.AppId}.acf");
        SteamAppMetadata.ValidateRelativePath(receipt.Metadata.InstallDirectory, false);
        var expectedGame = Path.Combine(receipt.Request.LibraryRoot, "steamapps", "common", receipt.Metadata.InstallDirectory);
        if (!options.RegisteredLibraries.Any(x => PathSafety.Same(x, receipt.Request.LibraryRoot)) || !PathSafety.Same(receipt.Preview.ManifestPath, expectedManifest) || !PathSafety.Same(receipt.Preview.PlaceholderDirectory, expectedGame) || !PathSafety.Under(receipt.ConfigBackupFile, Path.Combine(State, "backups", receipt.Id))) throw new BridgeException("恢复记录路径不符合接入位置。");
        var accountBase = Path.Combine(receipt.Request.SteamRoot, "userdata");
        var accountRelative = Path.GetRelativePath(accountBase, receipt.Request.AccountConfigPath).Split(Path.DirectorySeparatorChar);
        if (!PathSafety.Under(receipt.Request.AccountConfigPath, accountBase) || accountRelative.Length != 3 || !uint.TryParse(accountRelative[0], out var accountId) || accountId == 0 || !accountRelative[1].Equals("config", StringComparison.OrdinalIgnoreCase) || !accountRelative[2].Equals("localconfig.vdf", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("恢复配置路径无效。");
        foreach (var path in new[] { State, Active, expectedManifest, expectedGame, receipt.Request.AccountConfigPath, receipt.ConfigBackupFile }) PathSafety.NoReparse(path);
        var nativeDir = Path.GetDirectoryName(PathSafety.Full(receipt.Request.NativeExe))!;
        foreach (var target in new[] { State, expectedManifest, expectedGame, receipt.Request.AccountConfigPath })
            if (PathSafety.Same(target, nativeDir) || PathSafety.Under(target, nativeDir)) throw new BridgeException("恢复记录的写入位置与国服目录重叠，已拒绝。");
        if (PathSafety.Under(receipt.Request.NativeExe, expectedGame)) throw new BridgeException("恢复记录的国服路径与占位目录重叠。");
        foreach (var file in receipt.OwnedFiles.Keys) PathSafety.Within(expectedGame, file);
    }

    private void RejectForeignInstall(SetupRequest request, SetupPreview preview)
    {
        foreach (var lib in options.RegisteredLibraries)
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{options.AppId}.acf"); PathSafety.NoReparse(manifest);
            if (File.Exists(manifest)) throw new BridgeException("该 AppID 已有 Steam 安装清单，拒绝覆盖已有国际服安装或未知接入。");
            foreach (var partial in new[] { Path.Combine(lib, "steamapps", "downloading", options.AppId), Path.Combine(lib, "steamapps", "temp", options.AppId) })
            { PathSafety.NoReparse(partial); if (Directory.Exists(partial)) throw new BridgeException("Steam 中存在该游戏的下载或暂存目录，请先自行取消下载。"); }
        }
        PathSafety.NoReparse(preview.PlaceholderDirectory); PathSafety.NoReparse(preview.ManifestPath);
        if (Directory.Exists(preview.PlaceholderDirectory) || File.Exists(preview.PlaceholderDirectory)) throw new BridgeException("Steam 目标游戏目录已存在，拒绝覆盖任何已有文件。");
    }

    private List<string> FindConflicts(BridgeReceipt receipt, bool allowMissing, bool includeConfig = true)
    {
        ValidateReceiptPaths(receipt); var conflicts = new List<string>();
        foreach (var lib in options.RegisteredLibraries)
        {
            foreach (var partial in new[] { Path.Combine(lib, "steamapps", "downloading", options.AppId), Path.Combine(lib, "steamapps", "temp", options.AppId) })
            { PathSafety.NoReparse(partial); if (Directory.Exists(partial)) conflicts.Add("存在 Steam 下载或暂存目录"); }
        }
        if (File.Exists(receipt.Preview.ManifestPath))
        { var hash = HashFile(receipt.Preview.ManifestPath); if (hash != receipt.ManifestHash && !receipt.RecoveryManifestHashes.Contains(hash, StringComparer.OrdinalIgnoreCase) && !IsManifestOwned(receipt)) conflicts.Add("Steam 安装清单包含未知更改或下载状态"); }
        else if (!allowMissing) conflicts.Add("安装清单缺失");
        if (Directory.Exists(receipt.Preview.PlaceholderDirectory))
        {
            foreach (var directory in SafeEnumerateDirectories(receipt.Preview.PlaceholderDirectory))
                if (!receipt.OwnedFiles.Keys.Any(x => PathSafety.Under(PathSafety.Within(receipt.Preview.PlaceholderDirectory, x), directory))) conflicts.Add("占位目录出现非本工具子目录：" + Path.GetRelativePath(receipt.Preview.PlaceholderDirectory, directory));
            foreach (var file in SafeEnumerateFiles(receipt.Preview.PlaceholderDirectory))
            {
                var relative = Path.GetRelativePath(receipt.Preview.PlaceholderDirectory, file);
                if (!receipt.OwnedFiles.TryGetValue(relative, out var expected)) conflicts.Add("占位目录出现非本工具文件：" + relative);
                else { var hash = HashFile(file); if (hash != expected && (!receipt.RecoveryFileHashes.TryGetValue(relative, out var hashes) || !hashes.Contains(hash, StringComparer.OrdinalIgnoreCase))) conflicts.Add("自有文件内容发生变化：" + relative); }
            }
        }
        foreach (var owned in receipt.OwnedFiles)
            if (!allowMissing && !File.Exists(PathSafety.Within(receipt.Preview.PlaceholderDirectory, owned.Key))) conflicts.Add("占位文件缺失：" + owned.Key);
        if (includeConfig)
        {
            var setting = ValveKeyValues.ReadLaunchOptions(ReadText(receipt.Request.AccountConfigPath).Text, options.AppId);
            if (!setting.Exists || setting.Value != receipt.Preview.LaunchOptions) conflicts.Add("所选用户启动选项发生变化");
        }
        return conflicts;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            PathSafety.NoReparse(entry);
            if (Directory.Exists(entry))
            { yield return entry; foreach (var child in SafeEnumerateDirectories(entry)) yield return child; }
        }
    }

    private bool IsManifestOwned(BridgeReceipt receipt)
    {
        try
        {
            var marker = PathSafety.Within(receipt.Preview.PlaceholderDirectory, MarkerName);
            if (!receipt.OwnedFiles.TryGetValue(MarkerName, out var markerHash) || !File.Exists(marker) || HashFile(marker) != markerHash) return false;
            var app = ValveKeyValues.Unique(ValveKeyValues.Parse(ReadText(receipt.Preview.ManifestPath).Text), "AppState")?.Children;
            if (app is null) return false;
            string? Value(string key) => ValveKeyValues.Unique(app, key)?.Value;
            if (Value("appid") != options.AppId || Value("installdir") != receipt.Metadata.InstallDirectory || Value("buildid") != receipt.Metadata.BuildId || Value("StateFlags") != "4") return false;
            foreach (var field in new[] { "BytesToDownload", "BytesToStage", "ScheduledAutoUpdate", "TargetBuildID" }) if (Value(field) is not (null or "0")) return false;
            var depots = ValveKeyValues.Unique(app, "InstalledDepots")?.Children;
            if (depots is null || depots.Count != receipt.Metadata.Depots.Count) return false;
            foreach (var depot in receipt.Metadata.Depots)
            {
                var actual = ValveKeyValues.Unique(depots, depot.DepotId)?.Children;
                if (actual is null || ValveKeyValues.Unique(actual, "manifest")?.Value != depot.ManifestId || ValveKeyValues.Unique(actual, "size")?.Value != depot.Size) return false;
            }
            return true;
        }
        catch (BridgeException) { return false; }
    }

    private string RepairManifest(string text, BridgeReceipt receipt, SteamAppMetadata previousMetadata)
    {
        var m = receipt.Metadata;
        foreach (var item in new Dictionary<string, string> { ["appid"] = options.AppId, ["name"] = m.Name, ["installdir"] = m.InstallDirectory, ["StateFlags"] = "4", ["buildid"] = m.BuildId, ["UpdateResult"] = "0", ["BytesToDownload"] = "0", ["BytesDownloaded"] = "0", ["BytesToStage"] = "0", ["BytesStaged"] = "0", ["TargetBuildID"] = "0", ["ScheduledAutoUpdate"] = "0", ["AutoUpdateBehavior"] = "1", ["SizeOnDisk"] = new FileInfo(receipt.Request.PlaceholderExe ?? receipt.Request.HelperExe).Length.ToString() })
            text = ValveKeyValues.SetValue(text, ["AppState"], item.Key, item.Value);
        foreach (var old in previousMetadata.Depots.Where(x => !m.Depots.Any(n => n.DepotId == x.DepotId))) text = ValveKeyValues.RemoveObject(text, ["AppState", "InstalledDepots"], old.DepotId);
        foreach (var depot in m.Depots)
        { text = ValveKeyValues.SetValue(text, ["AppState", "InstalledDepots", depot.DepotId], "manifest", depot.ManifestId); text = ValveKeyValues.SetValue(text, ["AppState", "InstalledDepots", depot.DepotId], "size", depot.Size); }
        return text;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            PathSafety.NoReparse(entry);
            if (Directory.Exists(entry)) foreach (var child in SafeEnumerateFiles(entry)) yield return child;
            else yield return entry;
        }
    }

    private Dictionary<string, byte[]> PlannedFiles(BridgeReceipt receipt, SteamAppMetadata metadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [MarkerName] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { appId = options.AppId, owner = "SteamGameBridge", receiptId = receipt.Id, createdAt = receipt.CreatedAt })),
            [metadata.Executable.Replace('\\', Path.DirectorySeparatorChar)] = File.ReadAllBytes(receipt.Request.PlaceholderExe ?? receipt.Request.HelperExe)
        };

    public static string RenderManifest(BridgeReceipt receipt)
    {
        var q = ValveKeyValues.Quote; var m = receipt.Metadata;
        var b = new StringBuilder("\"AppState\"\n{\n");
        void Value(string key, string value) => b.Append('\t').Append(q(key)).Append("\t\t").Append(q(value)).Append('\n');
        Value("appid", m.AppId); Value("Universe", "1"); Value("name", m.Name); Value("StateFlags", "4"); Value("installdir", m.InstallDirectory);
        Value("LastUpdated", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()); Value("UpdateResult", "0");
        Value("SizeOnDisk", new FileInfo(receipt.Request.PlaceholderExe ?? receipt.Request.HelperExe).Length.ToString()); Value("buildid", m.BuildId);
        Value("LastOwner", "0"); Value("BytesToDownload", "0"); Value("BytesDownloaded", "0"); Value("BytesToStage", "0"); Value("BytesStaged", "0"); Value("TargetBuildID", "0"); Value("AutoUpdateBehavior", "1"); Value("AllowOtherDownloadsWhileRunning", "0"); Value("ScheduledAutoUpdate", "0");
        b.Append("\t\"InstalledDepots\"\n\t{\n");
        foreach (var depot in m.Depots)
            b.Append("\t\t").Append(q(depot.DepotId)).Append("\n\t\t{\n\t\t\t\"manifest\"\t\t").Append(q(depot.ManifestId)).Append("\n\t\t\t\"size\"\t\t").Append(q(depot.Size)).Append("\n\t\t}\n");
        b.Append("\t}\n\t\"UserConfig\"\n\t{\n\t\t\"language\"\t\t\"schinese\"\n\t}\n}\n"); return b.ToString();
    }

    private void EnsureIdle() { if (options.IsBusy()) throw new BridgeException("请完全退出 Steam 及运行中的桥接游戏进程，再修改配置。"); }
    private IDisposable Lock()
    {
        // One cross-process lock per Steam install also serializes edits to a shared account VDF.
        var dataLock = DataLocationSettings.AcquireLock(options.StateDirectory);
        var mutex = new Mutex(false, "Local\\SteamGameBridge.Config." + BridgeIdentity.Key(options.SteamRoot, "1"));
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); dataLock.Dispose(); throw new BridgeException("另一个 Steam 配置操作正在进行，请稍后重试。"); }
        try { PathSafety.NoReparse(State); Directory.CreateDirectory(State); return new MutexLease(mutex, dataLock); }
        catch { mutex.ReleaseMutex(); mutex.Dispose(); dataLock.Dispose(); throw; }
    }
    private sealed class MutexLease(Mutex mutex, IDisposable dataLock) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); dataLock.Dispose(); } }
    private BridgeReceipt LoadRequired()
    {
        PathSafety.NoReparse(Active);
        var receipt = JsonSerializer.Deserialize<BridgeReceipt>(File.ReadAllBytes(Active), JsonOptions) ?? throw new BridgeException("恢复记录为空。");
        ValidateReceiptPaths(receipt); return receipt;
    }
    private void Save(BridgeReceipt receipt)
    {
        AtomicWrite(Active, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, JsonOptions)));
        AtomicWrite(Path.Combine(State, "launch.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new LaunchSpec(options.AppId, receipt.Request), JsonOptions)));
    }
    internal static string HashFile(string path) { PathSafety.NoReparse(path); using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    internal static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal sealed record TextFile(string Text, Encoding Encoding, byte[] Preamble);
    internal static TextFile ReadText(string path)
    {
        PathSafety.NoReparse(path); var bytes = File.ReadAllBytes(path);
        Encoding encoding; int bom;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); bom = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); bom = 2; }
        else { encoding = new UTF8Encoding(false, true); bom = 0; }
        try { return new(encoding.GetString(bytes, bom, bytes.Length - bom), encoding, bytes[..bom]); }
        catch (DecoderFallbackException) { throw new BridgeException("Steam 配置文件编码无法安全保留。"); }
    }
    internal static byte[] EncodeText(TextFile original, string text) => [.. original.Preamble, .. original.Encoding.GetBytes(text)];
    internal static void WriteTextAtomic(string path, TextFile original, string text) => AtomicWrite(path, EncodeText(original, text));
    private static void AtomicNew(string path, byte[] bytes)
    {
        PathSafety.NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".steamgamebridge-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteNewBytes(temp, bytes); PathSafety.NoReparse(path); File.Move(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void WriteNewBytes(string path, byte[] bytes)
    { PathSafety.NoReparse(path); using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); f.Write(bytes); f.Flush(true); }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        PathSafety.NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".steamgamebridge-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNewBytes(temp, bytes); PathSafety.NoReparse(path);
            if (File.Exists(path)) File.Move(temp, path, true); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void DeleteEmptyOwnedDirectories(string directory)
    {
        if (!Directory.Exists(directory)) return;
        PathSafety.NoReparse(directory);
        foreach (var child in Directory.EnumerateDirectories(directory)) DeleteEmptyOwnedDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
