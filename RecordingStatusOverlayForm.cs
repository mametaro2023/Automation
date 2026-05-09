namespace AutomationTool;

public sealed class RecordingStatusOverlayForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Font _font = new("Yu Gothic UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly string _message;

    public RecordingStatusOverlayForm(Screen screen, string message)
    {
        _message = message;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(360, 38);
        var bounds = screen.WorkingArea;
        Location = new Point(
            bounds.Left + (bounds.Width - Width) / 2,
            bounds.Top + 10);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(22, 23, 24);
        Opacity = 0.92;
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var border = new Pen(Color.FromArgb(70, 72, 74));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var textSize = TextRenderer.MeasureText(_message, _font);
        var textPoint = new Point((Width - textSize.Width) / 2, (Height - textSize.Height) / 2);
        TextRenderer.DrawText(e.Graphics, _message, _font, textPoint, Color.FromArgb(232, 234, 237));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}
