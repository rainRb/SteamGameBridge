using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace SteamGameBridge.App;

internal static class UiTheme
{
    internal static readonly Color Canvas = Color.FromArgb(244, 246, 250);
    internal static readonly Color Ink = Color.FromArgb(30, 41, 59);
    internal static readonly Color Muted = Color.FromArgb(107, 119, 139);
    internal static readonly Color Border = Color.FromArgb(224, 230, 239);
    internal static readonly Color Accent = Color.FromArgb(50, 96, 218);
    internal static readonly Color Selection = Color.FromArgb(235, 241, 255);

    internal static void StyleButton(Button button, bool primary = false)
    {
        if (button is UiButton themed) themed.Primary = primary;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(39, 80, 193) : Canvas;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(33, 69, 168) : Selection;
        button.BackColor = primary ? Accent : Color.White;
        button.ForeColor = primary ? Color.White : Ink;
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
    private bool hovered, pressed;
    internal UiButton() { DoubleBuffered = true; }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color background = Quiet ? BackColor : Primary ? (Enabled ? UiTheme.Accent : UiTheme.Selection) : Color.White;
        if (Enabled && hovered) background = Primary ? Color.FromArgb(39, 80, 193) : UiTheme.Canvas;
        if (Enabled && pressed) background = Primary ? Color.FromArgb(33, 69, 168) : UiTheme.Selection;
        using var shape = UiTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 6);
        using var fill = new SolidBrush(background); e.Graphics.FillPath(fill, shape);
        if (!Primary && !Quiet) { using var border = new Pen(UiTheme.Border); e.Graphics.DrawPath(border, shape); }
        Color foreground = Enabled ? (Primary ? Color.White : ForeColor) : Color.FromArgb(152, 164, 183);
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
        Margin = new Padding(0, 4, 8, 4); BackColor = Color.White;
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
    internal SurfacePanel() { DoubleBuffered = true; BackColor = Color.White; Padding = new Padding(16); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(UiTheme.Border);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class GameList : ListBox
{
    internal GameList()
    {
        Dock = DockStyle.Fill; BorderStyle = BorderStyle.None; BackColor = Color.White;
        DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 62; IntegralHeight = false;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var bounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4);
        e.Graphics.FillRectangle(Brushes.White, e.Bounds);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? UiTheme.Selection : Color.White);
        using var shape = UiTheme.Rounded(bounds, 8);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillPath(fill, shape);
        var profile = Items[e.Index] as Core.GameProfile;
        using var nameFont = new Font(Font, FontStyle.Bold);
        var title = new Rectangle(bounds.Left + 12, bounds.Top + 9, bounds.Width - 24, 23);
        TextRenderer.DrawText(e.Graphics, profile?.Name ?? Items[e.Index].ToString(), nameFont, title, selected ? UiTheme.Accent : UiTheme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var subtitle = new Rectangle(title.Left, title.Bottom + 1, title.Width, 20);
        TextRenderer.DrawText(e.Graphics, profile is null ? "" : "AppID " + profile.AppId, Font, subtitle, UiTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
