using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SteamGameBridge.App;

/// <summary>Reads the Windows app preference; never changes Windows theme settings.</summary>
internal sealed class WindowsThemeBinding : IDisposable
{
    private readonly Form form;
    private readonly Func<bool> preference;
    private readonly ContextMenuStrip[] menus;
    private bool disposed, refreshing, queued;

    internal WindowsThemeBinding(Form form, Func<bool>? preference = null, params ContextMenuStrip[] menus)
    {
        this.form = form; this.preference = preference ?? ReadWindowsDarkMode; this.menus = menus;
        UiTheme.Changed += PaletteChanged;
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        form.HandleCreated += HandleCreated;
        foreach (var menu in menus) menu.Opening += MenuOpening;
        Refresh();
    }

    internal static void InitializeApplication()
    {
        Application.SetColorMode(SystemColorMode.System);
        UiTheme.SetMode(ReadWindowsDarkMode(), SystemInformation.HighContrast);
    }

    internal static bool ReadWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : Application.SystemColorMode == SystemColorMode.Dark;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { return Application.SystemColorMode == SystemColorMode.Dark; }
    }

    internal bool ReadPreference() => preference();

    internal void Refresh()
    {
        if (disposed || refreshing || form.IsDisposed) return;
        refreshing = true;
        try
        {
            bool contrast = SystemInformation.HighContrast;
            bool dark = !contrast && preference();
            // Reapply the native WinForms mode because System mode does not update itself.
            Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
            if (!UiTheme.SetMode(dark, contrast)) Apply();
        }
        finally { refreshing = false; }
    }

    private void PaletteChanged()
    {
        if (disposed || form.IsDisposed) return;
        if (form.IsHandleCreated && form.InvokeRequired) { QueueRefresh(); return; }
        Apply();
    }

    private void Apply()
    {
        UiTheme.ApplyTree(form);
        foreach (var menu in menus) UiTheme.ApplyMenu(menu);
        if (form.IsHandleCreated)
        {
            int dark = UiTheme.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        }
    }

    private void HandleCreated(object? sender, EventArgs e) => Refresh();
    private void MenuOpening(object? sender, System.ComponentModel.CancelEventArgs e) => UiTheme.ApplyMenu((ContextMenuStrip)sender!);
    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (disposed || form.IsDisposed || !form.IsHandleCreated || queued) return;
        queued = true;
        try { form.BeginInvoke(() => { queued = false; Refresh(); }); }
        catch (InvalidOperationException) { queued = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UiTheme.Changed -= PaletteChanged;
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        form.HandleCreated -= HandleCreated;
        foreach (var menu in menus) menu.Opening -= MenuOpening;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
