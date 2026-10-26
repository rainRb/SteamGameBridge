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

        Assert(!UserGuide.Text.Contains("不代表") && !UserGuide.Text.Contains("自行验收") && !UserGuide.Text.Contains("不会终止"), "guide omits removed explanatory boilerplate");
        Console.WriteLine($"RESULT {passed} UI checks passed. Layout rendering stayed offscreen; no desktop windows were opened.");
        Console.WriteLine("Offscreen renders: " + Fixture);
        return 0;
    }

    private static Rectangle BoundsInForm(Control control, Form form)
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
