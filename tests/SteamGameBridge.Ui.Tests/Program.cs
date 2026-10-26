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
            Layout(form);
            var caption = Descendants(form).OfType<Label>().Single(x => x.Text == "Starward EXE（可选）");
            Assert(caption.Width >= caption.GetPreferredSize(Size.Empty).Width, "full Starward caption at " + size);
            var fields = (TableLayoutPanel)caption.Parent!;
            var input = fields.GetControlFromPosition(1, fields.GetRow(caption))!;
            Assert(caption.Right <= input.Left, "caption does not overlap input at " + size);
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
            var page = (ScrollableControl)caption.Parent!.Parent!;
            page.ScrollControlIntoView(caption);
            using var bitmap = new Bitmap(surface.Width, surface.Height);
            surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Assert(bitmap.GetPixel(30, 65).ToArgb() != bitmap.GetPixel(2, 2).ToArgb(), "offscreen render contains the interface at " + size);
            bitmap.Save(Path.Combine(Fixture, $"layout-{size.Width}x{size.Height}.png"));
        }

        using (var form = new MainForm(false, Fixture, () => true))
        {
            var preset = Field<ComboBox>(form, "_preset");
            preset.SelectedItem = GamePreset.Find("zzz");
            Assert(Field<Label>(form, "_starwardStatus").Text == "协议可用", "protocol label stays concise");
            int enabledChanges = 0, previewChanges = 0, statusChanges = 0;
            foreach (var control in Descendants(form).Where(c => c is Button or TextBox or ComboBox or CheckBox))
                control.EnabledChanged += (_, _) => enabledChanges++;
            Layout(form);
            var settings = Descendants(form).OfType<TabPage>().Single(p => p.Text == "游戏设置");
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
        using (var icon = AppIcon.Load())
        using (var bitmap = icon.ToBitmap())
        {
            Assert(bitmap.Width >= 16 && bitmap.Height >= 16, "embedded application icon loads");
            Assert(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A > 0, "application icon contains artwork");
        }
        foreach (string published in args)
        {
            using var publishedIcon = Icon.ExtractAssociatedIcon(Path.GetFullPath(published));
            Assert(publishedIcon is not null, "published executable has an icon");
            using var bitmap = publishedIcon!.ToBitmap();
            Color center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            Assert(center.G > 160 && center.G > center.R, "published executable embeds the bridge artwork");
        }
        Console.WriteLine($"RESULT {passed} UI checks passed. Layout rendering stayed offscreen; no desktop windows were opened.");
        Console.WriteLine("Offscreen renders: " + Fixture);
        return 0;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Layout(Control control) { control.CreateControl(); control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
    private static void Assert(bool result, string name) { if (!result) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
}
