using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace SteamGameBridge.App;

internal static class UiTheme
{
    private static ThemePalette palette = ThemePalette.Light;
    internal static event Action? Changed;
    internal static ThemePalette Palette => palette;
    internal static bool IsDark => palette.IsDark;
    internal static Color Canvas => palette.Canvas;
    internal static Color Surface => palette.Surface;
    internal static Color Input => palette.Input;
    internal static Color Ink => palette.Ink;
    internal static Color Muted => palette.Muted;
    internal static Color Border => palette.Border;
    internal static Color Accent => palette.Accent;
    internal static Color Selection => palette.Selection;
    internal static Color SelectionInk => palette.HighContrast ? SystemColors.HighlightText : Accent;
    internal static Color PrimaryInk => palette.PrimaryInk;
    internal static Color Disabled => palette.Disabled;
    internal static Color Error => palette.Error;
    internal static Color Hover => palette.Hover;
    internal static Color Pressed => palette.Pressed;
    internal static Color PrimaryHover => palette.PrimaryHover;
    internal static Color PrimaryPressed => palette.PrimaryPressed;

    internal static bool SetMode(bool dark, bool highContrast = false)
    {
        var next = highContrast ? ThemePalette.Contrast() : dark ? ThemePalette.Dark : ThemePalette.Light;
        if (next == palette) return false;
        palette = next; Changed?.Invoke(); return true;
    }

    internal static void ApplyTree(Control control)
    {
        Color background = control is Form ? Canvas : control.Parent?.BackColor ?? Surface;
        if (control is SurfacePanel or GameList) background = Surface;
        if (control is FieldFrame or TextBoxBase or ComboBox or UpDownBase) background = Input;
        if (control is UiButton button)
        {
            if (button.Quiet)
            {
                background = button.Selected ? Selection : Surface;
                button.ForeColor = button.Selected ? SelectionInk : Muted;
            }
            else
            {
                StyleButton(button, button.Primary); background = button.Primary ? Accent : Surface;
            }
        }
        else control.ForeColor = control.Tag is ThemeTone.Error ? Error : control.Tag is ThemeTone.Muted ? Muted : Ink;
        control.BackColor = background;
        if (control.ContextMenuStrip is { } menu) ApplyMenu(menu);
        foreach (Control child in control.Controls) ApplyTree(child);
        control.Invalidate();
    }

    internal static void ApplyMenu(ContextMenuStrip menu)
    {
        menu.BackColor = Surface; menu.ForeColor = Ink;
        if (menu.Renderer is not ToolStripProfessionalRenderer renderer || renderer.ColorTable is not ThemeMenuColors)
            menu.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColors()) { RoundedEdges = false };
        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = Surface; item.ForeColor = item.Enabled ? Ink : Disabled;
            if (item is ToolStripMenuItem branch && branch.HasDropDownItems)
                foreach (ToolStripItem nested in branch.DropDownItems) { nested.BackColor = Surface; nested.ForeColor = Ink; }
        }
        menu.Invalidate();
    }

    internal static void StyleButton(Button button, bool primary = false)
    {
        if (button is UiButton themed) themed.Primary = primary;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? PrimaryHover : Hover;
        button.FlatAppearance.MouseDownBackColor = primary ? PrimaryPressed : Pressed;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? PrimaryInk : Ink;
        button.Cursor = Cursors.Hand;
    }

    internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class UiButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Quiet { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Selected { get; set; }
    private bool hovered, pressed;
    internal UiButton() { DoubleBuffered = true; }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color background = Quiet ? BackColor : Primary ? (Enabled ? UiTheme.Accent : UiTheme.Selection) : UiTheme.Surface;
        if (Enabled && hovered) background = Primary ? UiTheme.PrimaryHover : UiTheme.Hover;
        if (Enabled && pressed) background = Primary ? UiTheme.PrimaryPressed : UiTheme.Pressed;
        using var shape = UiTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 6);
        using var fill = new SolidBrush(background); e.Graphics.FillPath(fill, shape);
        if (!Primary && !Quiet) { using var border = new Pen(UiTheme.Border); e.Graphics.DrawPath(border, shape); }
        Color foreground = Enabled ? (Primary ? UiTheme.PrimaryInk : ForeColor) : UiTheme.Disabled;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), foreground, background);
    }
}

internal sealed class FieldFrame : Panel
{
    private readonly Control editor;
    internal FieldFrame(Control editor)
    {
        this.editor = editor;
        DoubleBuffered = true; Height = 34; Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(0, 4, 8, 4); BackColor = UiTheme.Input;
        if (editor is TextBox text) text.BorderStyle = BorderStyle.None;
        if (editor is NumericUpDown number) { number.BorderStyle = BorderStyle.None; Width = 120; Anchor = AnchorStyles.Left; }
        editor.Dock = DockStyle.None; editor.Margin = Padding.Empty;
        Controls.Add(editor);
        editor.Enter += (_, _) => Invalidate(); editor.Leave += (_, _) => Invalidate();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int height = editor.GetPreferredSize(Size.Empty).Height;
        editor.SetBounds(10, Math.Max(2, (Height - height) / 2), Math.Max(1, Width - 20), height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 6);
        using var border = new Pen(editor.Focused ? UiTheme.Accent : UiTheme.Border);
        e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class SurfacePanel : Panel
{
    internal SurfacePanel() { DoubleBuffered = true; BackColor = UiTheme.Surface; Padding = new Padding(16); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
        using var fill = new SolidBrush(UiTheme.Surface);
        using var border = new Pen(UiTheme.Border);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class GameList : ListBox
{
    internal GameList()
    {
        Dock = DockStyle.Fill; BorderStyle = BorderStyle.None; BackColor = UiTheme.Surface;
        DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 62; IntegralHeight = false;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var bounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4);
        using var background = new SolidBrush(UiTheme.Surface); e.Graphics.FillRectangle(background, e.Bounds);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? UiTheme.Selection : UiTheme.Surface);
        using var shape = UiTheme.Rounded(bounds, 8);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(fill, shape);
        var profile = Items[e.Index] as Core.GameProfile;
        using var nameFont = new Font(Font, FontStyle.Bold);
        var title = new Rectangle(bounds.Left + 12, bounds.Top + 9, bounds.Width - 24, 23);
        TextRenderer.DrawText(e.Graphics, profile?.Name ?? Items[e.Index].ToString(), nameFont, title, selected ? UiTheme.SelectionInk : UiTheme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var subtitle = new Rectangle(title.Left, title.Bottom + 1, title.Width, 20);
        TextRenderer.DrawText(e.Graphics, profile is null ? "" : "AppID " + profile.AppId, Font, subtitle, UiTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

internal enum ThemeTone { Muted, Error }

internal sealed class ThemeComboBox : ComboBox
{
    internal ThemeComboBox() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = Math.Max(20, Font.Height + 4); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Math.Max(20, Font.Height + 4); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = !edit && (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? UiTheme.Selection : UiTheme.Input);
        e.Graphics.FillRectangle(background, e.Bounds);
        string text = (e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text) ?? "";
        Color foreground = !Enabled ? UiTheme.Disabled : selected ? UiTheme.SelectionInk : UiTheme.Ink;
        var bounds = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 8), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

internal sealed record ThemePalette(bool IsDark, bool HighContrast, Color Canvas, Color Surface, Color Input, Color Ink, Color Muted, Color Border, Color Accent, Color Selection, Color PrimaryInk, Color Disabled, Color Error, Color Hover, Color Pressed, Color PrimaryHover, Color PrimaryPressed)
{
    internal static ThemePalette Light { get; } = new(false, false,
        Color.FromArgb(244, 246, 250), Color.White, Color.White, Color.FromArgb(30, 41, 59),
        Color.FromArgb(107, 119, 139), Color.FromArgb(224, 230, 239), Color.FromArgb(50, 96, 218),
        Color.FromArgb(235, 241, 255), Color.White, Color.FromArgb(152, 164, 183), Color.FromArgb(181, 52, 64),
        Color.FromArgb(244, 246, 250), Color.FromArgb(235, 241, 255), Color.FromArgb(39, 80, 193), Color.FromArgb(33, 69, 168));
    internal static ThemePalette Dark { get; } = new(true, false,
        Color.FromArgb(17, 21, 29), Color.FromArgb(28, 35, 48), Color.FromArgb(21, 28, 39), Color.FromArgb(237, 242, 250),
        Color.FromArgb(164, 175, 193), Color.FromArgb(52, 64, 82), Color.FromArgb(127, 164, 255),
        Color.FromArgb(41, 61, 100), Color.FromArgb(17, 27, 45), Color.FromArgb(112, 127, 151), Color.FromArgb(255, 157, 157),
        Color.FromArgb(39, 50, 68), Color.FromArgb(52, 70, 106), Color.FromArgb(152, 182, 255), Color.FromArgb(108, 147, 238));
    internal static ThemePalette Contrast() => new(false, true, SystemColors.Control, SystemColors.Window, SystemColors.Window,
        SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText, SystemColors.Highlight, SystemColors.Highlight,
        SystemColors.HighlightText, SystemColors.GrayText, SystemColors.WindowText, SystemColors.Highlight, SystemColors.Highlight,
        SystemColors.Highlight, SystemColors.Highlight);
}

internal sealed class ThemeMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => UiTheme.Surface;
    public override Color ImageMarginGradientBegin => UiTheme.Surface;
    public override Color ImageMarginGradientMiddle => UiTheme.Surface;
    public override Color ImageMarginGradientEnd => UiTheme.Surface;
    public override Color MenuBorder => UiTheme.Border;
    public override Color MenuItemSelected => UiTheme.Selection;
    public override Color MenuItemBorder => UiTheme.Border;
    public override Color SeparatorDark => UiTheme.Border;
    public override Color SeparatorLight => UiTheme.Surface;
}
