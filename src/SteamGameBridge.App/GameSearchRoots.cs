using Microsoft.Win32;
using SteamGameBridge.Core;

namespace SteamGameBridge.App;

internal static class GameSearchRoots
{
    private static readonly string[] CommonFolders = ["game", "games", "游戏", "游戏库", "Program Files", "Program Files (x86)", "SteamLibrary", "Steam", "Apps", "Programs", "Software", "HoYoPlay", "Hypergryph Launcher"];

    internal static IReadOnlyList<string> Get(GamePreset preset)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (preset.StarwardBiz is { } biz)
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\miHoYo\HYP\1_1\" + biz, "GameInstallPath", null) is string path && Path.IsPathFullyQualified(path)) roots.Add(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                foreach (string name in CommonFolders) roots.Add(Path.Combine(drive.RootDirectory.FullName, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string path = Environment.GetFolderPath(folder); if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs"));
        return roots.ToArray();
    }
}
