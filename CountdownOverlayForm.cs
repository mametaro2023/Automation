namespace AutomationTool;

public sealed class CountdownOverlayForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Font _mainFont = new("Yu Gothic UI", 64F, FontStyle.Bold, GraphicsUnit.Point);
    private string _text = "";

    public CountdownOverlayForm(Screen screen)
    {
        StartPosition = FormStartPosition.Manual;
        Size = new Size(240, 160);
        var bounds = screen.WorkingArea;
        Location = new Point(
            bounds.Left + (bounds.Width - Width) / 2,
            bounds.Top + (bounds.Height - Height) / 2);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = BackColor;
        Opacity = 1.0;
        DoubleBuffered = true;

        _timer.Interval = 16;
        _timer.Tick += (_, _) => Invalidate();
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

    public void ShowCountdown(int remaining)
    {
        _text = remaining.ToString();
        RestartAnimation();
    }

    public void ShowStart()
    {
        _text = "";
        Hide();
    }

    private void RestartAnimation()
    {
        if (!Visible)
        {
            Show();
        }

        _clock.Restart();
        if (!_timer.Enabled)
        {
            _timer.Start();
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (string.IsNullOrEmpty(_text))
        {
            return;
        }

        var progress = Math.Clamp(_clock.ElapsedMilliseconds / 220.0, 0.0, 1.0);
        var alpha = (int)Math.Round(255 * progress);
        if (progress >= 1.0)
        {
            _timer.Stop();
        }

        using var textBrush = new SolidBrush(Color.FromArgb(alpha, Color.White));
        using var shadowBrush = new SolidBrush(Color.FromArgb(Math.Min(150, alpha), Color.Black));
        var textSize = e.Graphics.MeasureString(_text, _mainFont);
        var x = (Width - textSize.Width) / 2f;
        var y = (Height - textSize.Height) / 2f - 4;
        e.Graphics.DrawString(_text, _mainFont, shadowBrush, x + 2, y + 2);
        e.Graphics.DrawString(_text, _mainFont, textBrush, x, y);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _mainFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
