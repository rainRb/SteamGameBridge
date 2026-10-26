namespace SteamGameBridge.App;

internal static class AppIcon
{
    internal static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("SteamGameBridge.ico")
            ?? throw new IOException("缺少应用图标资源。");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
