using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace SteamGameBridge.Core;

public static class DataLocationSettings
{
    public const string FolderName = "SteamGameBridge.Data";
    public const string LocatorName = "SteamGameBridge.ini";
    private const string OwnerFile = ".steamgamebridge-data.json";
    public static string SuggestedDirectory(string executableDirectory) => Path.Combine(PathSafety.Full(executableDirectory), FolderName);

    public static string? Read(string executableDirectory)
    {
        string locator = Path.Combine(PathSafety.Full(executableDirectory), LocatorName); PathSafety.NoReparse(locator);
        if (!File.Exists(locator)) return null;
        string? line = File.ReadLines(locator).SingleOrDefault(l => l.StartsWith("DataDirectory=", StringComparison.Ordinal));
        if (line is null || string.IsNullOrWhiteSpace(line[14..])) throw new BridgeException("保存位置设置无效，请重新选择。");
        string path = Path.GetFullPath(line[14..], executableDirectory); ValidateDirectory(path);
        if (!Directory.Exists(path)) throw new BridgeException("数据目录不存在，请选择保存位置。");
        ValidateOwner(Path.Combine(path, OwnerFile)); return path;
    }

    public static void Confirm(string executableDirectory, string dataDirectory)
    {
        string executable = PathSafety.Full(executableDirectory), data = PathSafety.Full(dataDirectory);
        using var dataLock = AcquireLock(data);
        ValidateDirectory(data); PathSafety.NoReparse(executable);
        if (PathSafety.Same(executable, data) || PathSafety.Under(executable, data)) throw new BridgeException("请使用独立的数据文件夹。");
        bool created = !Directory.Exists(data); bool ownerCreated = false;
        try
        {
            Directory.CreateDirectory(data);
            string owner = Path.Combine(data, OwnerFile);
            if (!File.Exists(owner))
            {
                if (Directory.EnumerateFileSystemEntries(data).Any()) throw new BridgeException("所选数据目录非空且无法确认归属，请选择空文件夹。");
                SteamBridgeService.AtomicWrite(owner, JsonSerializer.SerializeToUtf8Bytes(new { owner = "SteamGameBridge", schema = 1 })); ownerCreated = true;
            }
            else ValidateOwner(owner);
            // The user has confirmed this directory. Probe it now, never during preview.
            string probe = Path.Combine(data, ".write-check-" + Guid.NewGuid().ToString("N"));
            try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Flush(true); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            string relative = Path.GetRelativePath(executable, data);
            string value = PathSafety.Under(data, executable) ? relative : data;
            SteamBridgeService.AtomicWrite(Path.Combine(executable, LocatorName), Encoding.UTF8.GetBytes("DataDirectory=" + value + "\n"));
        }
        catch
        {
            if (created && Directory.Exists(data))
            {
                string owner = Path.Combine(data, OwnerFile);
                if (ownerCreated && File.Exists(owner)) File.Delete(owner);
                if (!Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data);
            }
            throw;
        }
    }

    public static void Move(string executableDirectory, string source, string destination, IReadOnlyList<string>? steamRoots = null)
    {
        source = PathSafety.Full(source); destination = PathSafety.Full(destination);
        if (PathSafety.Same(source, destination)) return;
        using var dataLock = AcquireLock(source);
        if (PathSafety.Same(executableDirectory, source) || PathSafety.Under(executableDirectory, source)) throw new BridgeException("请从数据目录之外运行程序后再修改保存位置。");
        ValidateDirectory(source); ValidateOwner(Path.Combine(source, OwnerFile)); ValidateDirectory(destination);
        if (PathSafety.Under(source, destination) || PathSafety.Under(destination, source)) throw new BridgeException("新旧数据目录不能互相包含。");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new BridgeException("新数据目录必须为空。");
        var sourceFiles = EnumeratePlainFiles(source).ToArray();
        var sourceHashes = sourceFiles.ToDictionary(p => p, SteamBridgeService.HashFile, StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Any(p => Path.GetFileName(p) is "active.json" or "pending-existing.json")) throw new BridgeException("请先恢复全部活动接入，再修改数据目录。");
        var profiles = new ProfileStore(source).Load();
        foreach (string steam in profiles.Select(p => p.Request.SteamRoot).Concat(steamRoots ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var account in SteamDiscovery.ReadAccounts(steam).Accounts)
                if (File.Exists(account.LocalConfigPath) && ValveKeyValues.ReadAllLaunchOptions(SteamBridgeService.ReadText(account.LocalConfigPath).Text).Values.Any(value => value.Replace('/', '\\').Contains(source.Replace('/', '\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeException("Steam 配置仍引用原数据目录，请先恢复接入。");
        Directory.CreateDirectory(destination);
        foreach (string path in sourceFiles)
        {
            string target = PathSafety.Within(destination, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, false);
            if (SteamBridgeService.HashFile(path) != SteamBridgeService.HashFile(target)) throw new BridgeException("转移校验失败，原目录已保留。");
        }
        // Rebind inactive saved tool paths. External game/account/library paths stay absolute.
        foreach (var profile in new ProfileStore(destination).Load())
        {
            string Rebind(string value) => PathSafety.Under(value, source) ? Path.Combine(destination, Path.GetRelativePath(source, value)) : value;
            profile.Request = profile.Request with { HelperExe = Rebind(profile.Request.HelperExe), PlaceholderExe = profile.Request.PlaceholderExe is { } exe ? Rebind(exe) : null };
            new ProfileStore(destination).Save(profile);
        }
        Confirm(executableDirectory, destination);
        // Only remove the exact files copied above; an added/changed file prevents cleanup.
        var remaining = EnumeratePlainFiles(source).ToArray();
        if (!remaining.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sourceFiles) || sourceFiles.Any(p => SteamBridgeService.HashFile(p) != sourceHashes[p] || !File.Exists(PathSafety.Within(destination, Path.GetRelativePath(source, p)))) )
            throw new BridgeException("保存位置已更改，但原目录出现新内容，未清理。");
        foreach (string path in sourceFiles) File.Delete(path);
        RemoveEmpty(source);
    }

    public static IDisposable AcquireLock(string dataDirectory)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.Full(dataDirectory).ToUpperInvariant())));
        var mutex = new Mutex(false, "Local\\SteamGameBridge.Data." + id); bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); throw new BridgeException("另一个数据操作正在进行，请稍后重试。"); }
        return new DataLease(mutex);
    }
    private sealed class DataLease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }

    public static IEnumerable<string> EnumeratePlainFiles(string directory)
    {
        PathSafety.NoReparse(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            PathSafety.NoReparse(entry);
            if (Directory.Exists(entry)) foreach (string child in EnumeratePlainFiles(entry)) yield return child;
            else yield return entry;
        }
    }
    private static void RemoveEmpty(string directory)
    {
        PathSafety.NoReparse(directory);
        foreach (string child in Directory.EnumerateDirectories(directory)) RemoveEmpty(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
    private static void ValidateDirectory(string path)
    {
        path = PathSafety.Full(path); PathSafety.NoReparse(path);
        if (File.Exists(path) || PathSafety.Same(path, Path.GetPathRoot(path)!)) throw new BridgeException("请选择独立的数据目录。");
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (new[] { "ZenlessZoneZero.exe", "BH3.exe", "Endfield.exe", "Wuthering Waves.exe", "StarwardDatabase.db" }.Any(name => File.Exists(Path.Combine(parent, name))))
                throw new BridgeException("数据目录不能放在游戏或 Starward 安装目录内。");
    }
    private static void ValidateOwner(string path)
    {
        PathSafety.NoReparse(path);
        if (!File.Exists(path)) throw new BridgeException("数据目录缺少归属记录。");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (doc.RootElement.GetProperty("owner").GetString() != "SteamGameBridge") throw new BridgeException("数据目录归属不匹配。");
    }
}
