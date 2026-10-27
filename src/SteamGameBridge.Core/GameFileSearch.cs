using System.Diagnostics;

namespace SteamGameBridge.Core;

public sealed record GameSearchResult(IReadOnlyList<string> Games, IReadOnlyList<string> StarwardExecutables, bool Limited);

public static class GameFileSearch
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "node_modules", "cache", "caches", "logs", "temp", "screenshots", "ScreenShot", "WindowsApps", "Microsoft.NET", "dotnet", "Reference Assemblies" };

    public static IReadOnlyList<string> TargetNames(string presetId, string? existingPath = null) => presetId switch
    {
        "zzz" => ["ZenlessZoneZero.exe"],
        "hi3" => ["BH3.exe"],
        "endfield" => ["Endfield.exe"],
        "wuthering" => ["Wuthering Waves.exe", "Client-Win64-Shipping.exe"],
        "custom" when !string.IsNullOrWhiteSpace(existingPath) => [Path.GetFileName(existingPath)],
        _ => throw new BridgeException("请先选择游戏预设或游戏程序。")
    };

    public static GameSearchResult Find(IEnumerable<string> roots, IReadOnlyList<string> targetNames,
        bool includeStarward = false, string? gameBiz = null, CancellationToken cancellation = default,
        int maxDepth = 6, int maxDirectories = 20000, TimeSpan? timeLimit = null)
    {
        if (targetNames.Count == 0 || targetNames.Any(n => n != Path.GetFileName(n) || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) throw new BridgeException("检索文件名无效。");
        if (maxDepth < 0 || maxDirectories < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starward = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        foreach (string root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            string path;
            try { path = Path.GetFullPath(root); } catch (ArgumentException) { continue; }
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            if (visited.Add(path)) queue.Enqueue((path, 0));
        }
        var watch = Stopwatch.StartNew(); int examined = 0; bool limited = false;
        TimeSpan budget = timeLimit ?? TimeSpan.FromSeconds(15);
        while (queue.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (examined++ >= maxDirectories || watch.Elapsed >= budget) { limited = true; break; }
            var (directory, depth) = queue.Dequeue();
            try
            {
                if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) continue;
                if (File.Exists(Path.Combine(directory, ".steam-game-bridge-owner.json"))) continue;
                foreach (string name in targetNames)
                {
                    string candidate = Path.Combine(directory, name);
                    if (IsExe(candidate) && MatchesBiz(directory, gameBiz)) games.Add(candidate);
                }
                if (includeStarward)
                {
                    string candidate = Path.Combine(directory, "Starward.exe");
                    if (IsExe(candidate)) starward.Add(candidate);
                }
                if (depth >= maxDepth) continue;
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= budget) { limited = true; break; }
                    if (SkipFolders.Contains(Path.GetFileName(child))) continue;
                    if (visited.Count >= maxDirectories) { limited = true; break; }
                    if (visited.Add(child)) queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return new(games.Order(StringComparer.OrdinalIgnoreCase).ToArray(), starward.Order(StringComparer.OrdinalIgnoreCase).ToArray(), limited);
    }

    private static bool IsExe(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static bool MatchesBiz(string directory, string? expected)
    {
        if (expected is null) return true;
        string config = Path.Combine(directory, "config.ini");
        if (!File.Exists(config)) return true;
        if (new FileInfo(config).Length > 1024 * 1024) return false;
        string? marker = File.ReadLines(config).Take(256).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("game_biz=", StringComparison.OrdinalIgnoreCase));
        return marker is null || marker.Equals("game_biz=" + expected, StringComparison.OrdinalIgnoreCase);
    }
}
