using System.Text;
using System.Text.Json;

namespace SteamGameBridge.Core;

public enum ExistingInstallKind { None, ManagedPlaceholder, SuspectedPlaceholder, GameFiles, IncompleteInstall, Unknown }
public enum ExistingInstallAction { Cancel, Reconnect, DeleteAndReconnect }
public sealed record ExistingInstallInspection(ExistingInstallKind Kind, string ManifestPath, string InstallDirectory,
    long SizeBytes, int FileCount, string Snapshot, IReadOnlyList<string> BlockReasons)
{
    public bool Exists => Kind != ExistingInstallKind.None;
    public string DisplayKind => Kind switch
    {
        ExistingInstallKind.None => "未安装", ExistingInstallKind.ManagedPlaceholder => "本工具占位接入",
        ExistingInstallKind.SuspectedPlaceholder => "疑似工具占位接入", ExistingInstallKind.GameFiles => "已有游戏文件",
        ExistingInstallKind.IncompleteInstall => "下载未完成", _ => "来源不明"
    };
}

public sealed partial class SteamBridgeService
{
    private sealed record Inventory(Dictionary<string, string> Entries, long Bytes, int Files);
    private sealed record OwnedProof(string ReceiptPath, bool OriginalExists, string? OriginalValue, string Command);
    private sealed class InstallResolution
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string AppId { get; set; } = "";
        public ExistingInstallAction Action { get; set; }
        public required SetupRequest Request { get; set; }
        public required SteamAppMetadata Metadata { get; set; }
        public required ExistingInstallInspection Inspection { get; set; }
        public string Phase { get; set; } = "prepared";
        public string Quarantine { get; set; } = "";
        public bool OriginalManifestExists { get; set; }
        public bool OriginalOptionExists { get; set; }
        public string? OriginalOption { get; set; }
        public bool BaselineExists { get; set; }
        public string? Baseline { get; set; }
        public Dictionary<string, string> OriginalEntries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RestoreOnly { get; set; }
        public string? Error { get; set; }
    }

    public ExistingInstallInspection InspectExisting(SetupRequest request, SteamAppMetadata metadata)
    {
        ValidateInspectionRequest(request, metadata);
        var blocks = new List<string>();
        var manifests = options.RegisteredLibraries.Select(l => Path.Combine(l, "steamapps", $"appmanifest_{options.AppId}.acf")).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (manifests.Length > 1) blocks.Add("多个游戏库存在此 AppID 的清单，请先处理重复安装。");
        string manifest = manifests.FirstOrDefault() ?? Path.Combine(request.LibraryRoot, "steamapps", $"appmanifest_{options.AppId}.acf");
        string library = Directory.GetParent(Path.GetDirectoryName(manifest)!)!.FullName;
        string install = metadata.InstallDirectory; bool incomplete = false;
        if (File.Exists(manifest))
        {
            try
            {
                var app = ValveKeyValues.Unique(ValveKeyValues.Parse(ReadText(manifest).Text), "AppState")?.Children ?? throw new BridgeException("安装清单格式无效。");
                if (ValveKeyValues.Unique(app, "appid")?.Value != options.AppId) throw new BridgeException("清单 AppID 不匹配。");
                install = ValveKeyValues.Unique(app, "installdir")?.Value ?? throw new BridgeException("清单缺少安装目录。");
                SteamAppMetadata.ValidateRelativePath(install, false);
                incomplete = ValveKeyValues.Unique(app, "StateFlags")?.Value != "4";
                foreach (string field in new[] { "BytesToDownload", "BytesToStage", "ScheduledAutoUpdate", "TargetBuildID" })
                    if (ValveKeyValues.Unique(app, field)?.Value is string value && value != "0") incomplete = true;
            }
            catch (BridgeException ex) { blocks.Add(ex.Message); install = metadata.InstallDirectory; }
        }
        string directory = Path.Combine(library, "steamapps", "common", install);
        foreach (var lib in options.RegisteredLibraries)
            foreach (string folder in new[] { "downloading", "temp" })
            {
                string path = Path.Combine(lib, "steamapps", folder, options.AppId); PathSafety.NoReparse(path);
                if (Directory.Exists(path)) incomplete = true;
            }
        if (incomplete) blocks.Add("请先取消下载或更新，再完全退出 Steam。");
        try
        {
            PathSafety.NoReparse(manifest); PathSafety.NoReparse(directory);
            GuardReplacementDirectory(request, directory);
            foreach (string other in Directory.Exists(Path.Combine(library, "steamapps")) ? Directory.EnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf") : [])
            {
                if (PathSafety.Same(other, manifest)) continue;
                var app = ValveKeyValues.Unique(ValveKeyValues.Parse(ReadText(other).Text), "AppState")?.Children;
                if (app is not null && ValveKeyValues.Unique(app, "installdir")?.Value?.Equals(install, StringComparison.OrdinalIgnoreCase) == true)
                    blocks.Add("该安装目录被其他 AppID 共用，不能删除。");
            }
        }
        catch (Exception ex) when (ex is BridgeException or IOException or UnauthorizedAccessException) { blocks.Add(ex.Message); }
        Inventory inventory;
        try { inventory = InspectInventory(directory); }
        catch (Exception ex) when (ex is BridgeException or IOException or UnauthorizedAccessException)
        { blocks.Add(ex.Message); inventory = new([], 0, 0); }
        bool exists = File.Exists(manifest) || Directory.Exists(directory) || File.Exists(directory);
        var proof = blocks.Count == 0 && exists ? FindOwnedProof(request, manifest, directory, inventory) : null;
        var kind = !exists && !incomplete ? ExistingInstallKind.None : incomplete ? ExistingInstallKind.IncompleteInstall
            : proof is not null ? ExistingInstallKind.ManagedPlaceholder
            : inventory.Files is > 0 and <= 5 && inventory.Bytes <= 1024 * 1024 ? ExistingInstallKind.SuspectedPlaceholder
            : inventory.Files > 5 || inventory.Bytes > 1024 * 1024 ? ExistingInstallKind.GameFiles : ExistingInstallKind.Unknown;
        string current = ValveKeyValues.ReadLaunchOptions(ReadText(request.AccountConfigPath).Text, options.AppId).Value ?? "";
        string snapshot = HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { manifest, directory, manifestHash = File.Exists(manifest) ? HashFile(manifest) : "", inventory.Entries, current, proof = proof?.ReceiptPath })));
        return new(kind, manifest, directory, inventory.Bytes, inventory.Files, snapshot, blocks.Distinct().ToArray());
    }

    private void ValidateInspectionRequest(SetupRequest request, SteamAppMetadata metadata)
    {
        SteamAppMetadata.ValidateAppId(options.AppId);
        if (metadata.AppId != options.AppId || !PathSafety.Same(request.SteamRoot, options.SteamRoot) || !options.RegisteredLibraries.Any(l => PathSafety.Same(l, request.LibraryRoot))) throw new BridgeException("接入位置与当前配置不一致。");
        SteamAppMetadata.ValidateRelativePath(metadata.InstallDirectory, false);
        if (!SteamDiscovery.ReadAccounts(request.SteamRoot).Accounts.Any(a => PathSafety.Same(a.LocalConfigPath, request.AccountConfigPath))) throw new BridgeException("目标账号未登记。");
        PathSafety.NoReparse(State); PathSafety.NoReparse(request.AccountConfigPath);
        _ = PathSafety.Full(request.NativeExe); PathSafety.NoReparse(request.NativeExe);
    }

    private void GuardReplacementDirectory(SetupRequest request, string directory)
    {
        if (!options.RegisteredLibraries.Any(l => PathSafety.Under(directory, Path.Combine(l, "steamapps", "common")) && Path.GetRelativePath(Path.Combine(l, "steamapps", "common"), directory).Split(Path.DirectorySeparatorChar).Length == 1)) throw new BridgeException("安装目录不在已登记游戏库中。");
        foreach (string protectedPath in new[] { request.NativeExe, Path.GetDirectoryName(request.NativeExe)!, options.StateDirectory, request.HelperExe, request.SourceHelperExe ?? request.HelperExe })
            if (PathSafety.Same(protectedPath, directory) || PathSafety.Under(protectedPath, directory)) throw new BridgeException("安装目录包含国服、程序或数据目录，不能删除。");
        if (PathSafety.Under(directory, Path.GetDirectoryName(request.NativeExe)!)) throw new BridgeException("安装目录位于国服目录内，不能删除。");
    }

    private static Inventory InspectInventory(string directory)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); long bytes = 0; int files = 0;
        if (File.Exists(directory)) throw new BridgeException("安装目录位置是文件，不能自动处理。");
        if (!Directory.Exists(directory)) return new(entries, bytes, files);
        var stack = new Stack<string>(); stack.Push(directory);
        while (stack.TryPop(out string? current))
        {
            PathSafety.NoReparse(current);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                PathSafety.NoReparse(entry);
                if (entries.Count >= 200000) throw new BridgeException("目录内容过多，请通过 Steam 卸载后重试。");
                string key = Path.GetRelativePath(directory, entry);
                if (Directory.Exists(entry)) { entries.Add(key, "directory"); stack.Push(entry); }
                else { var info = new FileInfo(entry); entries.Add(key, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"); bytes += info.Length; files++; }
            }
        }
        return new(entries, bytes, files);
    }

    private OwnedProof? FindOwnedProof(SetupRequest request, string manifest, string directory, Inventory inventory)
    {
        if (!File.Exists(manifest)) return null;
        foreach (string markerName in new[] { MarkerName, ".zzz-steam-bridge-owner.json" })
        {
            string marker = Path.Combine(directory, markerName);
            if (!File.Exists(marker)) continue;
            try
            {
                using var markerDoc = JsonDocument.Parse(File.ReadAllBytes(marker)); var m = markerDoc.RootElement;
                string id = m.GetProperty("receiptId").GetString()!;
                if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c)) || m.GetProperty("appId").GetString() != options.AppId || m.GetProperty("owner").GetString() is not ("SteamGameBridge" or "ZZZSteamBridge")) continue;
                foreach (string root in options.RecognizedStateDirectories.Prepend(options.StateDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string receiptPath in new[] { Path.Combine(root, "active.json"), Path.Combine(BridgeIdentity.IntegrationDirectory(root, options.SteamRoot, options.AppId), "active.json") })
                    {
                        if (!File.Exists(receiptPath)) continue;
                        PathSafety.NoReparse(receiptPath);
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(receiptPath)); var r = doc.RootElement;
                        if (r.GetProperty("Id").GetString() != id || r.GetProperty("Phase").GetString() != "active") continue;
                        var req = r.GetProperty("Request"); var preview = r.GetProperty("Preview");
                        if (!PathSafety.Same(req.GetProperty("SteamRoot").GetString()!, request.SteamRoot) || !PathSafety.Same(req.GetProperty("AccountConfigPath").GetString()!, request.AccountConfigPath)
                            || !PathSafety.Same(preview.GetProperty("ManifestPath").GetString()!, manifest) || !PathSafety.Same(preview.GetProperty("PlaceholderDirectory").GetString()!, directory)) continue;
                        var owned = r.GetProperty("OwnedFiles").EnumerateObject().ToArray();
                        if (!owned.Any(p => p.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase)) || inventory.Files != owned.Length) continue;
                        if (owned.Any(p => !File.Exists(PathSafety.Within(directory, p.Name)) || HashFile(PathSafety.Within(directory, p.Name)) != p.Value.GetString())) continue;
                        if (inventory.Entries.Where(p => p.Value == "directory").Any(d => !owned.Any(p => PathSafety.Under(PathSafety.Within(directory, p.Name), Path.Combine(directory, d.Key))))) continue;
                        string backup = r.GetProperty("ConfigBackupFile").GetString()!;
                        if (!PathSafety.Under(backup, Path.Combine(root, "backups", id)) && !PathSafety.Under(backup, Path.Combine(BridgeIdentity.IntegrationDirectory(root, options.SteamRoot, options.AppId), "backups", id))) continue;
                        if (!File.Exists(backup) || HashFile(backup) != r.GetProperty("OriginalConfigHash").GetString()) continue;
                        bool originalExists = r.GetProperty("OriginalLaunchOptionsExists").GetBoolean(); string? original = r.GetProperty("OriginalLaunchOptions").GetString();
                        var baseline = ValveKeyValues.ReadLaunchOptions(ReadText(backup).Text, options.AppId);
                        if (baseline.Exists != originalExists || baseline.Value != original) continue;
                        var app = ValveKeyValues.Unique(ValveKeyValues.Parse(ReadText(manifest).Text), "AppState")!.Children!;
                        var metadata = r.GetProperty("Metadata");
                        if (ValveKeyValues.Unique(app, "buildid")?.Value != metadata.GetProperty("BuildId").GetString() || ValveKeyValues.Unique(app, "installdir")?.Value != metadata.GetProperty("InstallDirectory").GetString()) continue;
                        var depots = ValveKeyValues.Unique(app, "InstalledDepots")?.Children;
                        var expectedDepots = metadata.GetProperty("Depots").EnumerateArray().ToArray();
                        if (depots is null || depots.Count != expectedDepots.Length) continue;
                        if (expectedDepots.Any(d => { var actual = ValveKeyValues.Unique(depots, d.GetProperty("DepotId").GetString()!)?.Children; return actual is null || ValveKeyValues.Unique(actual, "manifest")?.Value != d.GetProperty("ManifestId").GetString() || ValveKeyValues.Unique(actual, "size")?.Value != d.GetProperty("Size").GetString(); })) continue;
                        string command = preview.GetProperty("LaunchOptions").GetString()!;
                        var setting = ValveKeyValues.ReadLaunchOptions(ReadText(request.AccountConfigPath).Text, options.AppId);
                        if (setting.Value != command && (setting.Exists != originalExists || setting.Value != original)) continue;
                        return new(receiptPath, originalExists, original, command);
                    }
                }
            }
            catch (Exception ex) when (ex is BridgeException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return null;
    }

    private OperationResult ApplyReplacingExisting(SetupRequest request, SteamAppMetadata metadata)
    {
        EnsureIdle(); ValidateRequest(request, metadata, requireStagedTools: true); using var operationLock = Lock();
        if (File.Exists(Active)) throw new BridgeException("已有活动记录，请先恢复接入。");
        var tx = BeginResolution(request, metadata);
        try
        {
            MoveExistingAside(tx);
            var baseline = ValveKeyValues.ReadLaunchOptions(ReadText(request.AccountConfigPath).Text, options.AppId);
            var result = Apply(request with { ExistingInstallationAction = ExistingInstallAction.Cancel, ExistingInstallationSnapshot = null, ResolutionId = tx.Id, OriginalOptionsReviewed = true, ExpectedOriginalLaunchOptions = baseline.Value }, metadata);
            tx.Phase = "new-active"; SaveResolution(tx);
            FinishResolution(tx); return result;
        }
        catch (Exception ex)
        {
            tx.Error = ex.Message; SaveResolution(tx);
            if (tx.Phase is "deleting" or "complete") throw new BridgeException("新接入已建立，原安装清理未完成。恢复资料已保留，请点击恢复继续清理。" + ex.Message);
            try
            {
                if (File.Exists(Active)) Restore();
                RollbackResolution(tx);
            }
            catch (Exception rollback) { throw new BridgeException("处理未完成，恢复资料已保留。请点击恢复。" + rollback.Message); }
            throw new BridgeException("重新接入未完成，已恢复之前的安装。" + ex.Message);
        }
    }

    public void RestoreVerifiedExisting(SetupRequest request, SteamAppMetadata metadata, string expectedSnapshot)
    {
        EnsureIdle(); using var operationLock = Lock();
        var tx = BeginResolution(request with { ExistingInstallationAction = ExistingInstallAction.Reconnect, ExistingInstallationSnapshot = expectedSnapshot }, metadata);
        tx.RestoreOnly = true; SaveResolution(tx);
        try { MoveExistingAside(tx); tx.Phase = "restored-only"; SaveResolution(tx); FinishResolution(tx); }
        catch { if (tx.Phase is not ("deleting" or "complete")) RollbackResolution(tx); throw; }
    }

    private InstallResolution BeginResolution(SetupRequest request, SteamAppMetadata metadata)
    {
        if (HasPendingExistingResolution) throw new BridgeException("有待恢复的安装处理，请先点击恢复。");
        var inspection = InspectExisting(request, metadata);
        if (!inspection.Exists || inspection.BlockReasons.Count > 0 || inspection.Snapshot != request.ExistingInstallationSnapshot) throw new BridgeException("安装状态已改变或无法安全处理，请重新检查。" + string.Join("；", inspection.BlockReasons));
        if (request.ExistingInstallationAction is not (ExistingInstallAction.Reconnect or ExistingInstallAction.DeleteAndReconnect)) throw new BridgeException("请先明确选择已有安装的处理方式。");
        var proof = FindOwnedProof(request, inspection.ManifestPath, inspection.InstallDirectory, InspectInventory(inspection.InstallDirectory));
        if (request.ExistingInstallationAction == ExistingInstallAction.Reconnect && proof is null) throw new BridgeException("无法确认占位归属，请选择其他处理方式。");
        var config = ReadText(request.AccountConfigPath); var current = ValveKeyValues.ReadLaunchOptions(config.Text, options.AppId);
        if (request.OriginalOptionsReviewed && current.Value != request.ExpectedOriginalLaunchOptions) throw new BridgeException("启动项已变化，请重新确认。");
        var tx = new InstallResolution { AppId = options.AppId, Action = request.ExistingInstallationAction, Request = request, Metadata = metadata, Inspection = inspection,
            OriginalManifestExists = File.Exists(inspection.ManifestPath), OriginalOptionExists = current.Exists, OriginalOption = current.Value,
            BaselineExists = proof?.OriginalExists ?? current.Exists, Baseline = proof is not null ? proof.OriginalValue : current.Value };
        tx.OriginalEntries = InspectInventory(inspection.InstallDirectory).Entries;
        tx.Quarantine = Path.Combine(Path.GetDirectoryName(inspection.ManifestPath)!, ".steamgamebridge-replaced-" + tx.Id);
        string backup = ResolutionDirectory(tx.Id); PathSafety.NoReparse(backup); Directory.CreateDirectory(backup);
        AtomicNew(Path.Combine(backup, "localconfig.before.vdf"), File.ReadAllBytes(request.AccountConfigPath));
        if (tx.OriginalManifestExists) AtomicNew(Path.Combine(backup, "manifest.before.acf"), File.ReadAllBytes(inspection.ManifestPath));
        SaveResolution(tx); return tx;
    }

    private void MoveExistingAside(InstallResolution tx)
    {
        EnsureIdle();
        if (InspectExisting(tx.Request, tx.Metadata).Snapshot != tx.Inspection.Snapshot) throw new BridgeException("文件在确认后发生变化，已停止。");
        PathSafety.NoReparse(tx.Quarantine); if (Directory.Exists(tx.Quarantine)) throw new BridgeException("恢复暂存目录已经存在。");
        tx.Phase = "moving"; SaveResolution(tx);
        if (Directory.Exists(tx.Inspection.InstallDirectory)) Directory.Move(tx.Inspection.InstallDirectory, tx.Quarantine);
        if (File.Exists(tx.Inspection.ManifestPath)) File.Delete(tx.Inspection.ManifestPath);
        var config = ReadText(tx.Request.AccountConfigPath);
        var current = ValveKeyValues.ReadLaunchOptions(config.Text, options.AppId);
        if (current.Exists != tx.OriginalOptionExists || current.Value != tx.OriginalOption) throw new BridgeException("启动项在处理期间发生变化。");
        EnsureIdle(); WriteTextAtomic(tx.Request.AccountConfigPath, config, ValveKeyValues.SetLaunchOptions(config.Text, options.AppId, tx.Baseline, tx.BaselineExists));
        tx.Phase = "detached"; SaveResolution(tx);
    }

    private void FinishResolution(InstallResolution tx)
    {
        EnsureIdle(); ValidateResolution(tx);
        if (Directory.Exists(tx.Quarantine))
        {
            var remaining = InspectInventory(tx.Quarantine);
            if (remaining.Entries.Any(entry => !tx.OriginalEntries.TryGetValue(entry.Key, out string? expected) || entry.Value != expected)) throw new BridgeException("暂存目录出现未知或变更内容，未继续删除。");
            tx.Phase = "deleting"; SaveResolution(tx);
            PathSafety.NoReparse(tx.Quarantine); Directory.Delete(tx.Quarantine, recursive: true);
        }
        tx.Phase = "complete"; SaveResolution(tx); File.Delete(Path.Combine(State, "pending-existing.json"));
    }

    private void RollbackResolution(InstallResolution tx)
    {
        EnsureIdle(); ValidateResolution(tx);
        if (Directory.Exists(tx.Quarantine))
        {
            _ = InspectInventory(tx.Quarantine);
            if (Directory.Exists(tx.Inspection.InstallDirectory) || File.Exists(tx.Inspection.InstallDirectory)) throw new BridgeException("原安装位置出现其他内容，未覆盖。");
            Directory.Move(tx.Quarantine, tx.Inspection.InstallDirectory);
        }
        if (tx.OriginalManifestExists)
        {
            string backup = Path.Combine(ResolutionDirectory(tx.Id), "manifest.before.acf");
            if (!File.Exists(tx.Inspection.ManifestPath)) AtomicNew(tx.Inspection.ManifestPath, File.ReadAllBytes(backup));
            else if (HashFile(tx.Inspection.ManifestPath) != HashFile(backup)) throw new BridgeException("原清单位置出现其他内容，未覆盖。");
        }
        var config = ReadText(tx.Request.AccountConfigPath); var current = ValveKeyValues.ReadLaunchOptions(config.Text, options.AppId);
        if (current.Exists == tx.BaselineExists && current.Value == tx.Baseline)
            WriteTextAtomic(tx.Request.AccountConfigPath, config, ValveKeyValues.SetLaunchOptions(config.Text, options.AppId, tx.OriginalOption, tx.OriginalOptionExists));
        else if (current.Exists != tx.OriginalOptionExists || current.Value != tx.OriginalOption) throw new BridgeException("启动项另有修改，恢复已停止。");
        tx.Phase = "rolled-back"; SaveResolution(tx); File.Delete(Path.Combine(State, "pending-existing.json"));
    }

    public bool HasPendingExistingResolution => File.Exists(Path.Combine(State, "pending-existing.json"));
    public void RecoverExistingResolution()
    {
        EnsureIdle(); using var operationLock = Lock();
        var tx = JsonSerializer.Deserialize<InstallResolution>(File.ReadAllBytes(Path.Combine(State, "pending-existing.json"))) ?? throw new BridgeException("恢复记录无效。");
        ValidateResolution(tx);
        if (File.Exists(Active))
        {
            var receipt = LoadRequired();
            if (receipt.ResolutionId != tx.Id) throw new BridgeException("活动接入与恢复记录不一致。");
            if (receipt.Phase == "active" && FindConflicts(receipt, false).Count == 0) { FinishResolution(tx); return; }
            Restore();
        }
        if (tx.RestoreOnly && tx.Phase is ("restored-only" or "deleting")) FinishResolution(tx); else RollbackResolution(tx);
    }

    private string ResolutionDirectory(string id)
    {
        if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) throw new BridgeException("恢复编号无效。");
        return Path.Combine(State, "resolutions", id);
    }
    private void ValidateResolution(InstallResolution tx)
    {
        ValidateInspectionRequest(tx.Request, tx.Metadata);
        if (tx.AppId != options.AppId || !options.RegisteredLibraries.Any(l => PathSafety.Same(tx.Inspection.ManifestPath, Path.Combine(l, "steamapps", $"appmanifest_{options.AppId}.acf")))) throw new BridgeException("恢复记录的安装位置无效。");
        GuardReplacementDirectory(tx.Request, tx.Inspection.InstallDirectory);
        string expectedInstall = tx.Metadata.InstallDirectory;
        if (tx.OriginalManifestExists)
        {
            var app = ValveKeyValues.Unique(ValveKeyValues.Parse(ReadText(Path.Combine(ResolutionDirectory(tx.Id), "manifest.before.acf")).Text), "AppState")?.Children ?? throw new BridgeException("原清单备份无效。");
            if (ValveKeyValues.Unique(app, "appid")?.Value != options.AppId) throw new BridgeException("原清单备份 AppID 不匹配。");
            expectedInstall = ValveKeyValues.Unique(app, "installdir")?.Value ?? "";
        }
        SteamAppMetadata.ValidateRelativePath(expectedInstall, false);
        string library = Directory.GetParent(Path.GetDirectoryName(tx.Inspection.ManifestPath)!)!.FullName;
        if (!PathSafety.Same(tx.Inspection.InstallDirectory, Path.Combine(library, "steamapps", "common", expectedInstall))) throw new BridgeException("恢复记录中的安装目录不匹配。");
        foreach (string other in Directory.EnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
        {
            if (PathSafety.Same(other, tx.Inspection.ManifestPath)) continue;
            var app = ValveKeyValues.Unique(ValveKeyValues.Parse(ReadText(other).Text), "AppState")?.Children;
            if (app is not null && ValveKeyValues.Unique(app, "installdir")?.Value?.Equals(expectedInstall, StringComparison.OrdinalIgnoreCase) == true)
                throw new BridgeException("该安装目录被其他 AppID 共用，恢复已停止。");
        }
        foreach (string lib in options.RegisteredLibraries)
            foreach (string folder in new[] { "downloading", "temp" })
            {
                string path = Path.Combine(lib, "steamapps", folder, options.AppId); PathSafety.NoReparse(path);
                if (Directory.Exists(path)) throw new BridgeException("存在下载或更新任务，恢复已停止。");
            }
        string expected = Path.Combine(Path.GetDirectoryName(tx.Inspection.ManifestPath)!, ".steamgamebridge-replaced-" + tx.Id);
        if (!PathSafety.Same(expected, tx.Quarantine)) throw new BridgeException("恢复暂存路径不匹配。");
        PathSafety.NoReparse(ResolutionDirectory(tx.Id)); PathSafety.NoReparse(tx.Quarantine); PathSafety.NoReparse(tx.Inspection.ManifestPath);
    }
    private void SaveResolution(InstallResolution tx)
    {
        AtomicWrite(Path.Combine(ResolutionDirectory(tx.Id), "operation.json"), JsonSerializer.SerializeToUtf8Bytes(tx, JsonOptions));
        if (tx.Phase is not ("complete" or "rolled-back")) AtomicWrite(Path.Combine(State, "pending-existing.json"), JsonSerializer.SerializeToUtf8Bytes(tx, JsonOptions));
    }
    private ExistingInstallAction LoadResolutionAction(string id)
    {
        var tx = JsonSerializer.Deserialize<InstallResolution>(File.ReadAllBytes(Path.Combine(ResolutionDirectory(id), "operation.json"))) ?? throw new BridgeException("恢复记录无效。");
        ValidateResolution(tx); return tx.Action;
    }
}
