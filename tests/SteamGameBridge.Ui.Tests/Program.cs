using System.Reflection;
using SteamGameBridge.App;
using SteamGameBridge.Core;

internal static class Program
{
    private static int passed;
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "isolated-ui", Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(Fixture);
        foreach (var size in new[] { new Size(950, 730), new Size(1140, 850), new Size(1920, 1080) })
        {
            using var form = new MainForm(true, Fixture, () => true) { ClientSize = size };
            typeof(MainForm).GetMethod("NewProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Field<ComboBox>(form, "_preset").SelectedItem = GamePreset.Find("zzz");
            Field<TextBox>(form, "_game").Text = @"D:\Games\ZenlessZoneZero\ZenlessZoneZero.exe";
            ShowPage(form, "Integrations"); Layout(form);
            var caption = Descendants(form).OfType<Label>().Single(x => x.Text == "Starward EXE（可选）");
            Assert(caption.Width >= caption.GetPreferredSize(Size.Empty).Width, "full Starward caption at " + size);
            var fields = (TableLayoutPanel)caption.Parent!;
            var input = fields.GetControlFromPosition(1, fields.GetRow(caption))!;
            Assert(caption.Right <= input.Left, "caption does not overlap input at " + size);
            Assert(Descendants(form).OfType<SurfacePanel>().All(card => BoundsInForm(card, form).Right <= form.ClientSize.Width && BoundsInForm(card, form).Bottom <= form.ClientSize.Height), "cards stay inside the window at " + size);
            var toolbar = (TableLayoutPanel)Descendants(form).Single(x => x.Name == "ProfileActions");
            var buttons = toolbar.Controls.Cast<Button>().OrderBy(b => b.Left).ToArray();
            Assert(buttons.Length == 3 && buttons.All(b => b.Top == buttons[0].Top), "profile buttons stay on one row at " + size);
            Assert(buttons.Zip(buttons.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left), "profile buttons do not overlap at " + size);
            Assert(buttons.All(b => b.ClientSize.Width >= TextRenderer.MeasureText(b.Text, b.Font).Width), "profile button captions fit at " + size);
            // Render a detached client panel, so the form never has to be shown.
            var client = form.Controls[0]; form.Controls.Remove(client);
            using var surface = new Panel { Size = form.ClientSize };
            surface.Font = form.Font;
            surface.Controls.Add(client); Layout(surface);
            foreach (string key in new[] { "GameSettings", "Integrations", "SteamSettings" })
            {
                ShowPage(form, key); Layout(surface);
                using var bitmap = new Bitmap(surface.Width, surface.Height);
                surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                Assert(bitmap.GetPixel(40, 150).ToArgb() != bitmap.GetPixel(2, 2).ToArgb(), "offscreen " + key + " contains the interface at " + size);
                bitmap.Save(Path.Combine(Fixture, $"{key}-{size.Width}x{size.Height}.png"));
            }
        }

        using (var form = new MainForm(false, Fixture, () => true) { ClientSize = new Size(980, 720) })
        {
            var preset = Field<ComboBox>(form, "_preset");
            preset.SelectedItem = GamePreset.Find("zzz");
            Assert(Field<Label>(form, "_starwardStatus").Text == "协议可用", "protocol label stays concise");
            Assert(!form.ShowIcon, "custom logo removed from the window");
            Assert(Field<Button>(form, "_apply").BackColor == UiTheme.Accent, "main action has a distinct visual priority");
            var mode = Field<ComboBox>(form, "_mode");
            Assert(!Field<Control>(form, "_launcherSection").Visible, "direct launch hides unrelated launcher fields");
            mode.SelectedIndex = 1;
            int enabledChanges = 0, previewChanges = 0, statusChanges = 0;
            foreach (var control in Descendants(form).Where(c => c is Button or TextBox or ComboBox or CheckBox))
                control.EnabledChanged += (_, _) => enabledChanges++;
            Layout(form);
            var settings = (Panel)Descendants(form).Single(p => p.Name == "GameSettings");
            settings.AutoScrollPosition = new Point(0, 120);
            Point scroll = settings.AutoScrollPosition;
            var game = Field<TextBox>(form, "_game");
            game.Text = @"D:\游戏\正在编辑的路径.exe";
            game.SelectionStart = 7; game.SelectionLength = 3;
            var preview = Field<TextBox>(form, "_preview"); preview.TextChanged += (_, _) => previewChanges++;
            Field<Label>(form, "_starwardStatus").TextChanged += (_, _) => statusChanges++;
            var maintenance = typeof(MainForm).GetMethod("MaintainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 4; i++) ((Task)maintenance.Invoke(form, null)!).GetAwaiter().GetResult();
            Assert(enabledChanges == 0, "idle timer never toggles editor controls");
            Assert(previewChanges == 0 && statusChanges == 0, "idle timer does not reset preview or status");
            Assert(settings.AutoScrollPosition == scroll, "idle timer preserves the settings scroll position");
            Assert(game.Text.EndsWith("正在编辑的路径.exe") && game.SelectionStart == 7 && game.SelectionLength == 3, "idle timer preserves edits and caret");
            Assert(!Directory.EnumerateFileSystemEntries(Fixture).Any(p => p.EndsWith("profiles.json") || p.EndsWith("integrations")), "layout polling writes no Steam or profile configuration");
        }

        ThemeChecks();
        Assert(!UserGuide.Text.Contains("不代表") && !UserGuide.Text.Contains("自行验收") && !UserGuide.Text.Contains("不会终止"), "guide omits removed explanatory boilerplate");
        Console.WriteLine($"RESULT {passed} UI checks passed. Layout rendering stayed offscreen; no desktop windows were opened.");
        Console.WriteLine("Offscreen renders: " + Fixture);
        return 0;
    }

    private static void ThemeChecks()
    {
        bool dark = true;
        using var form = new MainForm(false, Path.Combine(Fixture, "theme-state"), () => true, () => dark) { ClientSize = new Size(1140, 850) };
        var preset = Field<ComboBox>(form, "_preset"); preset.SelectedItem = GamePreset.Find("zzz");
        var game = Field<TextBox>(form, "_game"); game.Text = @"D:\游戏\主题切换.exe"; game.SelectionStart = 4; game.SelectionLength = 3;
        Field<ComboBox>(form, "_mode").SelectedIndex = 1;
        Layout(form);
        var settings = (Panel)Descendants(form).Single(p => p.Name == "GameSettings"); settings.AutoScrollPosition = new Point(0, 80);
        Point scroll = settings.AutoScrollPosition;
        int enabledChanges = 0;
        foreach (var control in Descendants(form)) control.EnabledChanged += (_, _) => enabledChanges++;
        var binding = Field<WindowsThemeBinding>(form, "_themeBinding");
        var navigation = Field<Dictionary<string, Button>>(form, "_navigation");
        foreach (bool mode in new[] { true, false, true })
        {
            dark = mode; binding.Refresh();
            Assert(UiTheme.IsDark == mode && form.BackColor == UiTheme.Canvas, "theme follows current application preference: " + mode);
            Assert(preset.SelectedItem is GamePreset { Id: "zzz" }, "theme switch preserves dropdown selection: " + mode);
            Assert(game.BackColor == UiTheme.Input && game.ForeColor == UiTheme.Ink, "editor colors follow theme: " + mode);
            Assert(Descendants(form).OfType<SurfacePanel>().All(p => p.BackColor == UiTheme.Surface), "cards use the matching surface: " + mode);
            Assert(game.Text.EndsWith("主题切换.exe") && game.SelectionStart == 4 && game.SelectionLength == 3, "theme switch preserves text and selection: " + mode);
            Assert(settings.AutoScrollPosition == scroll && ((UiButton)navigation["GameSettings"]).Selected, "theme switch keeps the current page and scroll: " + mode);
            Assert(Contrast(UiTheme.Ink, UiTheme.Input) >= 4.5 && Contrast(UiTheme.PrimaryInk, UiTheme.Accent) >= 4.5, "text and primary button contrast: " + mode);
            var trayMenu = Field<NotifyIcon>(form, "_tray").ContextMenuStrip!;
            var moreMenu = Descendants(form).Select(c => c.ContextMenuStrip).First(m => m is not null)!;
            Assert(trayMenu.BackColor == UiTheme.Surface && moreMenu.BackColor == UiTheme.Surface && trayMenu.ForeColor == UiTheme.Ink, "tray and action menus follow theme: " + mode);
            Assert(((ToolStripProfessionalRenderer)trayMenu.Renderer).ColorTable.MenuItemSelected == UiTheme.Selection, "menu hover uses the theme palette: " + mode);
        }
        _ = form.Handle;
        dark = false;
        typeof(WindowsThemeBinding).GetMethod("SystemPreferenceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(binding, [null, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.General)]);
        Application.DoEvents();
        Assert(!UiTheme.IsDark && form.BackColor == UiTheme.Canvas, "Windows preference notifications refresh the open window");
        Assert(enabledChanges == 0, "theme switching never disables the editor");
        typeof(MainForm).GetMethod("PresentError", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, ["测试提示"]);
        dark = false; binding.Refresh(); Assert(Field<Label>(form, "_feedback").ForeColor == UiTheme.Error, "error text keeps its semantic color when changing theme");
        dark = true; binding.Refresh();
        using (var dialog = new LaunchOptionsDialog("--dx11", "", "fixture command", () => dark))
        {
            var radio = Descendants(dialog).OfType<RadioButton>().First();
            Assert(dialog.BackColor == UiTheme.Canvas && radio.ForeColor == UiTheme.Ink, "launch-options dialog supports dark mode");
            Assert(Descendants(dialog).OfType<TextBox>().All(t => t.BackColor == UiTheme.Input && t.ForeColor == UiTheme.Ink), "dialog text boxes have no white theme leftovers");
            dark = false; Field<WindowsThemeBinding>(dialog, "themeBinding").Refresh();
            Assert(dialog.BackColor == UiTheme.Canvas && form.BackColor == UiTheme.Canvas && !UiTheme.IsDark, "open dialogs and main form update together");
        }
        UiTheme.SetMode(false, highContrast: true);
        Assert(UiTheme.Palette.HighContrast && UiTheme.Ink == SystemColors.WindowText, "contrast themes respect Windows colors");
        binding.Refresh();
        dark = true; binding.Refresh();
        Field<Label>(form, "_feedback").Text = "";
        Field<TextBox>(form, "_name").Text = "绝区零";
        game.Text = @"D:\Games\ZenlessZoneZero\ZenlessZoneZero.exe";
        Field<ComboBox>(form, "_mode").SelectedIndex = 0;
        var root = form.Controls[0]; form.Controls.Remove(root);
        using var surface = new Panel { Size = form.ClientSize, Font = form.Font, BackColor = UiTheme.Canvas };
        surface.Controls.Add(root); Layout(surface);
        foreach (string key in new[] { "GameSettings", "SteamSettings", "Integrations", "Logs", "Help" })
        {
            ShowPage(form, key); Layout(surface);
            using var bitmap = new Bitmap(surface.Width, surface.Height);
            surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Assert(bitmap.GetPixel(40, 150).ToArgb() == UiTheme.Surface.ToArgb(), "dark surface rendered on " + key);
            if (key == "GameSettings")
            {
                foreach (var combo in Descendants(surface).OfType<ComboBox>().Where(c => c.Visible))
                {
                    var bounds = BoundsInForm(combo, surface);
                    Color pixel = bitmap.GetPixel(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    Assert(pixel.R < 128 && pixel.G < 128 && pixel.B < 128, "dark dropdown does not render a white field");
                }
            }
            bitmap.Save(Path.Combine(Fixture, "Dark-" + key + ".png"));
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte v) { double s = v / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    private static Rectangle BoundsInForm(Control control, Control form)
    {
        Point point = control.Location;
        for (Control? parent = control.Parent; parent is not null && parent != form; parent = parent.Parent) point.Offset(parent.Location);
        return new Rectangle(point, control.Size);
    }
    private static void ShowPage(MainForm form, string key) => typeof(MainForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [key]);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Layout(Control control) { control.CreateControl(); control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
    private static void Assert(bool result, string name) { if (!result) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
}
