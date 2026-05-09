using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AutomationTool;

public sealed class CountdownOverlayForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Font _mainFont = new("Yu Gothic UI", 56F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _subFont = new("Yu Gothic UI", 13F, FontStyle.Regular, GraphicsUnit.Point);
    private string _text = "";
    private string _subText = "記録開始";

    public CountdownOverlayForm(Screen screen)
    {
        StartPosition = FormStartPosition.Manual;
        Size = new Size(280, 190);
        var bounds = screen.WorkingArea;
        Location = new Point(
            bounds.Left + (bounds.Width - Width) / 2,
            bounds.Top + (bounds.Height - Height) / 2);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(18, 20, 24);
        Opacity = 0.88;
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
        _subText = "記録開始";
        RestartAnimation();
    }

    public void ShowStart()
    {
        _text = "REC";
        _subText = "記録中";
        RestartAnimation();
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
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var path = CreateRoundedRectangle(ClientRectangle with { Width = Width - 1, Height = Height - 1 }, 16);
        using var fill = new SolidBrush(Color.FromArgb(238, 18, 20, 24));
        using var border = new Pen(Color.FromArgb(180, 240, 240, 240), 1);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        var progress = Math.Clamp(_clock.ElapsedMilliseconds / 650.0, 0.0, 1.0);
        var scale = 1.12 - 0.12 * EaseOut(progress);
        var alpha = progress < 0.12
            ? (int)Math.Round(255 * (progress / 0.12))
            : 255;

        var center = new PointF(Width / 2f, Height / 2f - 10);
        e.Graphics.TranslateTransform(center.X, center.Y);
        e.Graphics.ScaleTransform((float)scale, (float)scale);
        e.Graphics.TranslateTransform(-center.X, -center.Y);

        using var textBrush = new SolidBrush(Color.FromArgb(alpha, Color.White));
        using var glowBrush = new SolidBrush(Color.FromArgb(Math.Min(120, alpha), Color.Red));
        var textSize = e.Graphics.MeasureString(_text, _mainFont);
        var x = (Width - textSize.Width) / 2f;
        var y = 42f;
        e.Graphics.DrawString(_text, _mainFont, glowBrush, x + 2, y + 2);
        e.Graphics.DrawString(_text, _mainFont, textBrush, x, y);

        e.Graphics.ResetTransform();
        using var subBrush = new SolidBrush(Color.FromArgb(210, Color.White));
        var subSize = e.Graphics.MeasureString(_subText, _subFont);
        e.Graphics.DrawString(_subText, _subFont, subBrush, (Width - subSize.Width) / 2f, 134f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _mainFont.Dispose();
            _subFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static double EaseOut(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        return 1.0 - Math.Pow(1.0 - t, 3.0);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
