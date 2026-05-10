using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AutomationTool;

public sealed class PlaybackFeedbackOverlayForm : Form
{
    private const int EventAnimationMs = 260;
    private const int TailVisibleMs = 420;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly List<ActiveMarker> _markers = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly PreviewSoundPlayer _soundPlayer = new();
    private readonly Font _markerFont = new("Yu Gothic UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Rectangle _virtualBounds;

    public PlaybackFeedbackOverlayForm()
    {
        _virtualBounds = GetVirtualBounds();
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        Text = "再生フィードバック";
        _timer.Interval = 16;
        _timer.Tick += (_, _) => Tick();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    public void PostFeedback(PlaybackFeedbackEvent feedback)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => AddFeedback(feedback));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        AddFeedback(feedback);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var now = _clock.ElapsedMilliseconds;
        foreach (var marker in _markers)
        {
            var age = now - marker.StartMs;
            var visual = GetMarkerVisual(marker.Kind, age);
            if (visual.MarkerAlpha <= 0)
            {
                continue;
            }

            var point = ToLocal(marker.Point);
            using var pen = new Pen(Color.FromArgb(visual.MarkerAlpha, marker.Color), visual.Width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            DrawCrossMarker(e.Graphics, pen, point, marker.Kind, visual.Size);

            if (!string.IsNullOrEmpty(marker.Text))
            {
                using var textBrush = new SolidBrush(Color.FromArgb(visual.TextAlpha, Color.White));
                using var outlineBrush = new SolidBrush(Color.FromArgb(visual.TextAlpha, Color.Black));
                DrawOutlinedText(e.Graphics, marker.Text, point.X + 12, point.Y + 5, textBrush, outlineBrush);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _soundPlayer.Dispose();
            _markerFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void AddFeedback(PlaybackFeedbackEvent feedback)
    {
        var point = feedback.Point is { X: 0, Y: 0 } ? Cursor.Position : feedback.Point;
        var text = feedback.Kind switch
        {
            MacroEventKind.MouseDown => $"{feedback.Button} ↓",
            MacroEventKind.MouseUp => $"{feedback.Button} ↑",
            MacroEventKind.KeyDown => $"{feedback.KeyCode} ↓",
            MacroEventKind.KeyUp => $"{feedback.KeyCode} ↑",
            _ => ""
        };
        var color = feedback.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp
            ? Color.FromArgb(95, 220, 255)
            : Color.FromArgb(255, 210, 92);

        _markers.Add(new ActiveMarker(feedback.Kind, point, text, color, _clock.ElapsedMilliseconds));
        _soundPlayer.Play(feedback.Kind);
        if (!_timer.Enabled)
        {
            _timer.Start();
        }

        Invalidate();
    }

    private void Tick()
    {
        var now = _clock.ElapsedMilliseconds;
        _markers.RemoveAll(marker => now - marker.StartMs > TailVisibleMs);
        if (_markers.Count == 0)
        {
            _timer.Stop();
        }

        Invalidate();
    }

    private static MarkerVisual GetMarkerVisual(MacroEventKind kind, long ageMs)
    {
        var isRelease = kind is MacroEventKind.MouseUp or MacroEventKind.KeyUp;
        var baseSize = kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? 9 : 7;
        if (ageMs <= EventAnimationMs)
        {
            var progress = SmoothStep(ageMs / (double)EventAnimationMs);
            var size = isRelease
                ? (int)Math.Round(Lerp(baseSize, 24, progress))
                : (int)Math.Round(Lerp(24, baseSize, progress));
            var alpha = isRelease
                ? (int)Math.Round(Lerp(255, 0, progress))
                : (int)Math.Round(Lerp(45, 255, progress));
            return new MarkerVisual(size, alpha, alpha, 3);
        }

        var tailProgress = Math.Clamp((ageMs - EventAnimationMs) / (double)Math.Max(1, TailVisibleMs - EventAnimationMs), 0.0, 1.0);
        var tailAlpha = (int)Math.Round(Lerp(isRelease ? 0 : 150, 0, tailProgress));
        return new MarkerVisual(baseSize, tailAlpha, tailAlpha, 2);
    }

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private static void DrawCrossMarker(Graphics graphics, Pen pen, Point point, MacroEventKind kind, int size)
    {
        var adjusted = kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? size + 2 : size;
        graphics.DrawLine(pen, point.X - adjusted, point.Y - adjusted, point.X + adjusted, point.Y + adjusted);
        graphics.DrawLine(pen, point.X - adjusted, point.Y + adjusted, point.X + adjusted, point.Y - adjusted);
    }

    private void DrawOutlinedText(
        Graphics graphics,
        string text,
        int x,
        int y,
        Brush textBrush,
        Brush outlineBrush)
    {
        for (var ox = -1; ox <= 1; ox++)
        {
            for (var oy = -1; oy <= 1; oy++)
            {
                if (ox == 0 && oy == 0)
                {
                    continue;
                }

                graphics.DrawString(text, _markerFont, outlineBrush, x + ox, y + oy);
            }
        }

        graphics.DrawString(text, _markerFont, textBrush, x, y);
    }

    private Point ToLocal(Point screenPoint)
    {
        return new Point(screenPoint.X - _virtualBounds.Left, screenPoint.Y - _virtualBounds.Top);
    }

    private static Rectangle GetVirtualBounds()
    {
        return Rectangle.FromLTRB(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN) + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN) + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
    }

    private sealed record ActiveMarker(MacroEventKind Kind, Point Point, string Text, Color Color, long StartMs);
    private readonly record struct MarkerVisual(int Size, int MarkerAlpha, int TextAlpha, int Width);
}
