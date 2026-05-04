using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Diagnostics;

namespace AutomationTool;

public sealed class MacroPreview
{
    public List<Point> PerfectPath { get; } = new();
    public List<TimedPreviewPoint> PerfectTimedPath { get; } = new();
    public List<List<Point>> NoisyPaths { get; } = new();
    public List<List<TimedPreviewPoint>> NoisyTimedPaths { get; } = new();
    public List<PreviewMarker> PerfectMarkers { get; } = new();
    public List<List<PreviewMarker>> NoisyMarkers { get; } = new();
    public long DurationMs { get; set; }
}

public sealed class PreviewMarker
{
    public Point Location { get; init; }
    public long TimeMs { get; init; }
    public MacroEventKind Kind { get; init; }
    public string Text { get; init; } = "";
}

public sealed record TimedPreviewPoint(long TimeMs, Point Point);

public static class MacroPreviewBuilder
{
    public static MacroPreview Build(Macro macro, NoiseSettings noise, int variantCount)
    {
        var preview = new MacroPreview();
        var events = macro.Events.OrderBy(e => e.TimeOffsetMs).ToList();
        preview.DurationMs = events.Count == 0 ? 0 : events[^1].TimeOffsetMs;
        var count = Math.Clamp(variantCount, 1, 10);
        foreach (var macroEvent in events.Where(e => e.Kind == MacroEventKind.MouseMove))
        {
            var point = new Point(macroEvent.X, macroEvent.Y);
            preview.PerfectPath.Add(point);
            preview.PerfectTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, point));
        }

        foreach (var macroEvent in events.Where(e => e.Kind != MacroEventKind.MouseMove))
        {
            if (TryGetPoint(macroEvent, out var point))
            {
                preview.PerfectMarkers.Add(CreateMarker(point, macroEvent));
            }
        }

        var baseSeed = Environment.TickCount ^ macro.Id.GetHashCode();
        for (var i = 0; i < count; i++)
        {
            BuildNoisyVariant(preview, events, noise, new Random(baseSeed + i * 7919));
        }

        return preview;
    }

    private static void BuildNoisyVariant(
        MacroPreview preview,
        List<MacroEvent> events,
        NoiseSettings noise,
        Random random)
    {
        var noisyPath = new List<Point>();
        var noisyTimedPath = new List<TimedPreviewPoint>();
        var noisyMarkers = new List<PreviewMarker>();
        var anchor = Point.Empty;
        var hasAnchor = false;
        var segment = new List<MacroEvent>();

        foreach (var macroEvent in events)
        {
            if (macroEvent.Kind == MacroEventKind.MouseMove)
            {
                segment.Add(macroEvent);
                continue;
            }

            FlushMoveSegment(noisyPath, noisyTimedPath, segment, anchor, hasAnchor, noise, random);
            segment.Clear();

            if (TryGetPoint(macroEvent, out var point))
            {
                var noisyPoint = CreateNoisyAnchor(point, macroEvent, noise, random);
                noisyMarkers.Add(CreateMarker(noisyPoint, macroEvent));
                noisyPath.Add(noisyPoint);
                noisyTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, noisyPoint));
                anchor = noisyPoint;
                hasAnchor = true;
            }
        }

        FlushMoveSegment(noisyPath, noisyTimedPath, segment, anchor, hasAnchor, noise, random);
        preview.NoisyPaths.Add(noisyPath);
        preview.NoisyTimedPaths.Add(noisyTimedPath);
        preview.NoisyMarkers.Add(noisyMarkers);
    }

    private static void FlushMoveSegment(
        List<Point> noisyPath,
        List<TimedPreviewPoint> noisyTimedPath,
        List<MacroEvent> segment,
        Point anchor,
        bool hasAnchor,
        NoiseSettings noise,
        Random random)
    {
        if (segment.Count == 0)
        {
            return;
        }

        var start = hasAnchor ? anchor : new Point(segment[0].X, segment[0].Y);
        var end = new Point(segment[^1].X, segment[^1].Y);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var normalX = length > 0 ? -dy / length : 0;
        var normalY = length > 0 ? dx / length : 0;
        var distanceScale = Math.Clamp(length / 350.0, 0.25, 1.65);
        var amplitude = length < 24
            ? 0.0
            : Math.Min(60.0, noise.TrajectoryJitterPx * distanceScale);
        var waveA = RandomSigned(random, amplitude * 0.55, amplitude);
        var waveB = RandomSigned(random, amplitude * 0.18, amplitude * 0.45);

        for (var i = 0; i < segment.Count; i++)
        {
            var t = segment.Count == 1 ? 1.0 : i / (double)(segment.Count - 1);
            var macroEvent = segment[i];
            var sideOffset = Math.Sin(Math.PI * t) * waveA + Math.Sin(Math.PI * 2.0 * t) * waveB;
            noisyPath.Add(new Point(
                (int)Math.Round(macroEvent.X + normalX * sideOffset),
                (int)Math.Round(macroEvent.Y + normalY * sideOffset)));
            noisyTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, noisyPath[^1]));
        }
    }

    private static double RandomSigned(Random random, double minMagnitude, double maxMagnitude)
    {
        if (maxMagnitude <= 0)
        {
            return 0;
        }

        var sign = random.Next(0, 2) == 0 ? -1.0 : 1.0;
        return sign * (minMagnitude + random.NextDouble() * Math.Max(0.0, maxMagnitude - minMagnitude));
    }

    private static bool TryGetPoint(MacroEvent macroEvent, out Point point)
    {
        if (macroEvent.Kind is MacroEventKind.MouseDown
            or MacroEventKind.MouseUp
            or MacroEventKind.MouseWheel
            or MacroEventKind.KeyDown
            or MacroEventKind.KeyUp
            or MacroEventKind.MouseMove)
        {
            point = new Point(macroEvent.X, macroEvent.Y);
            return macroEvent.X != 0 || macroEvent.Y != 0;
        }

        point = Point.Empty;
        return false;
    }

    private static Point CreateNoisyAnchor(Point point, MacroEvent macroEvent, NoiseSettings noise, Random random)
    {
        var maxJitter = macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp
            ? Math.Min(noise.CoordinateJitterPx, 2)
            : 0;

        if (maxJitter <= 0)
        {
            return point;
        }

        return new Point(
            point.X + random.Next(-maxJitter, maxJitter + 1),
            point.Y + random.Next(-maxJitter, maxJitter + 1));
    }

    private static PreviewMarker CreateMarker(Point point, MacroEvent macroEvent)
    {
        return new PreviewMarker
        {
            Location = point,
            TimeMs = macroEvent.TimeOffsetMs,
            Kind = macroEvent.Kind,
            Text = macroEvent.Kind switch
            {
                MacroEventKind.MouseDown => "MD",
                MacroEventKind.MouseUp => "MU",
                MacroEventKind.KeyDown => "KD",
                MacroEventKind.KeyUp => "KU",
                MacroEventKind.MouseWheel => "WH",
                _ => ""
            }
        };
    }
}

public sealed class PreviewOverlayForm : Form
{
    private readonly MacroPreview _preview;
    private readonly Rectangle _virtualBounds;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly TrackBar _seekBar = new();
    private readonly NumericUpDown _speedBox = new();
    private readonly Button _startButton = new();
    private readonly Button _playButton = new();
    private readonly Button _endButton = new();
    private readonly Font _markerFont = new("MS UI Gothic", 9F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _helpFont = new("MS UI Gothic", 10F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Stopwatch _clock = new();
    private bool _updatingSeek;
    private long _currentMs;

    public PreviewOverlayForm(Macro macro, NoiseSettings noise, int variantCount)
    {
        _preview = MacroPreviewBuilder.Build(macro, noise, variantCount);
        _virtualBounds = GetVirtualBounds();

        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.86;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Default;
        Text = "プレビュー";
        BuildControls();
    }

    private void BuildControls()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            BackColor = Color.FromArgb(245, 20, 20, 20)
        };
        Controls.Add(panel);

        _startButton.Text = "先頭";
        _startButton.Size = new Size(72, 28);
        _startButton.Click += (_, _) =>
        {
            PausePreviewPlayback();
            SetPreviewTime(0);
        };
        panel.Controls.Add(_startButton);

        _playButton.Text = "再生";
        _playButton.Size = new Size(72, 28);
        _playButton.Click += (_, _) => TogglePreviewPlayback();
        panel.Controls.Add(_playButton);

        _endButton.Text = "最後";
        _endButton.Size = new Size(72, 28);
        _endButton.Click += (_, _) =>
        {
            PausePreviewPlayback();
            SetPreviewTime(_preview.DurationMs);
        };
        panel.Controls.Add(_endButton);

        _seekBar.Minimum = 0;
        _seekBar.Maximum = Math.Max(1, (int)Math.Min(int.MaxValue, _preview.DurationMs));
        _seekBar.TickFrequency = Math.Max(1, _seekBar.Maximum / 10);
        _seekBar.ValueChanged += (_, _) =>
        {
            if (_updatingSeek)
            {
                return;
            }

            _currentMs = _seekBar.Value;
            Invalidate();
        };
        panel.Controls.Add(_seekBar);

        var speedLabel = new Label
        {
            Text = "速度(%)",
            ForeColor = Color.White,
            Size = new Size(58, 18)
        };
        panel.Controls.Add(speedLabel);

        _speedBox.Size = new Size(72, 23);
        _speedBox.Minimum = 10;
        _speedBox.Maximum = 500;
        _speedBox.Increment = 10;
        _speedBox.Value = 100;
        panel.Controls.Add(_speedBox);

        panel.Resize += (_, _) => LayoutTransportControls(panel, speedLabel);
        LayoutTransportControls(panel, speedLabel);

        _timer.Interval = 16;
        _timer.Tick += TimerOnTick;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        DrawPath(e.Graphics, _preview.PerfectPath, Color.Red, 1, 42);
        foreach (var noisyPath in _preview.NoisyPaths)
        {
            DrawPath(e.Graphics, noisyPath, Color.Gold, 1, 40);
        }

        List<Point> progressPath;
        Color progressColor;
        if (_preview.NoisyTimedPaths.Count > 0)
        {
            progressPath = GetProgressPath(_preview.NoisyTimedPaths[0], _currentMs);
            progressColor = Color.Gold;
        }
        else
        {
            progressPath = GetProgressPath(_preview.PerfectTimedPath, _currentMs);
            progressColor = Color.Red;
        }

        DrawPath(e.Graphics, progressPath, progressColor, 5, 245);
        DrawCurrentPoint(e.Graphics, progressPath, progressColor);

        DrawMarkers(e.Graphics, _preview.PerfectMarkers, Color.Red, 230, true);
        foreach (var markers in _preview.NoisyMarkers)
        {
            DrawMarkers(e.Graphics, markers, Color.Gold, 95, false);
        }

        DrawLegend(e.Graphics);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _markerFont.Dispose();
            _helpFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LayoutTransportControls(Panel panel, Label speedLabel)
    {
        const int margin = 12;
        const int buttonGap = 8;
        var buttonTop = 18;
        _startButton.Location = new Point(margin, buttonTop);
        _playButton.Location = new Point(_startButton.Right + buttonGap, buttonTop);
        _endButton.Location = new Point(_playButton.Right + buttonGap, buttonTop);

        _speedBox.Location = new Point(panel.ClientSize.Width - _speedBox.Width - margin, 20);
        speedLabel.Location = new Point(_speedBox.Left - speedLabel.Width - 6, 23);

        var seekLeft = _endButton.Right + 14;
        var seekRight = speedLabel.Left - 12;
        _seekBar.Location = new Point(seekLeft, 14);
        _seekBar.Size = new Size(Math.Max(120, seekRight - seekLeft), 38);
    }

    private void TogglePreviewPlayback()
    {
        if (_timer.Enabled)
        {
            PausePreviewPlayback();
            return;
        }

        if (_currentMs >= _preview.DurationMs)
        {
            SetPreviewTime(0);
        }

        StartPreviewPlayback();
    }

    private void StartPreviewPlayback()
    {
        if (_preview.DurationMs <= 0)
        {
            return;
        }

        _playButton.Text = "停止";
        _clock.Restart();
        _timer.Start();
    }

    private void PausePreviewPlayback()
    {
        _timer.Stop();
        _clock.Reset();
        _playButton.Text = "再生";
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        var elapsed = _clock.ElapsedMilliseconds;
        _clock.Restart();
        var speed = (double)_speedBox.Value / 100.0;
        var next = _currentMs + (long)Math.Round(elapsed * speed);
        if (next >= _preview.DurationMs)
        {
            next = _preview.DurationMs;
            PausePreviewPlayback();
        }

        SetPreviewTime(next);
    }

    private void SetPreviewTime(long timeMs)
    {
        _currentMs = Math.Clamp(timeMs, 0, _preview.DurationMs);
        _updatingSeek = true;
        _seekBar.Value = (int)Math.Min(_seekBar.Maximum, _currentMs);
        _updatingSeek = false;
        Invalidate();
    }

    private void DrawCurrentPoint(Graphics graphics, List<Point> progressPath, Color color)
    {
        if (progressPath.Count == 0)
        {
            return;
        }

        var point = ToLocal(progressPath[^1]);
        using var fill = new SolidBrush(Color.FromArgb(245, color));
        using var outline = new Pen(Color.FromArgb(230, Color.Black), 2);
        var rect = new Rectangle(point.X - 5, point.Y - 5, 10, 10);
        graphics.FillEllipse(fill, rect);
        graphics.DrawEllipse(outline, rect);
    }

    private static List<Point> GetProgressPath(List<TimedPreviewPoint> path, long timeMs)
    {
        return path
            .Where(point => point.TimeMs <= timeMs)
            .Select(point => point.Point)
            .ToList();
    }

    private void DrawPath(Graphics graphics, List<Point> points, Color color, int width, int alpha = 220)
    {
        if (points.Count < 2)
        {
            return;
        }

        using var pen = new Pen(Color.FromArgb(alpha, color), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(pen, points.Select(ToLocal).ToArray());
    }

    private void DrawMarkers(Graphics graphics, List<PreviewMarker> markers, Color color, int alpha = 210, bool showText = true)
    {
        using var pen = new Pen(Color.FromArgb(Math.Min(255, alpha + 40), color), 2);
        using var textBrush = new SolidBrush(Color.White);
        using var outlineBrush = new SolidBrush(Color.Black);

        foreach (var marker in markers)
        {
            var point = ToLocal(marker.Location);
            DrawCrossMarker(graphics, pen, point, marker.Kind);

            if (showText && !string.IsNullOrEmpty(marker.Text))
            {
                DrawOutlinedText(graphics, marker.Text, point.X + 10, point.Y + 4, textBrush, outlineBrush);
            }
        }
    }

    private static void DrawCrossMarker(Graphics graphics, Pen pen, Point point, MacroEventKind kind)
    {
        var size = kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? 9 : 7;
        graphics.DrawLine(pen, point.X - size, point.Y - size, point.X + size, point.Y + size);
        graphics.DrawLine(pen, point.X - size, point.Y + size, point.X + size, point.Y - size);

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

    private void DrawLegend(Graphics graphics)
    {
        using var redBrush = new SolidBrush(Color.Red);
        using var yellowBrush = new SolidBrush(Color.Gold);
        using var whiteBrush = new SolidBrush(Color.White);
        using var bgBrush = new SolidBrush(Color.FromArgb(205, Color.Black));
        var box = new Rectangle(18, 18, 330, 92);
        graphics.FillRectangle(bgBrush, box);
        graphics.FillRectangle(redBrush, 34, 38, 32, 5);
        graphics.DrawString("赤: 完全再現", _helpFont, whiteBrush, 74, 30);
        graphics.FillRectangle(yellowBrush, 34, 66, 32, 5);
        graphics.DrawString("黄: ノイズ入り", _helpFont, whiteBrush, 74, 58);
        graphics.DrawString("Esc で終了", _helpFont, whiteBrush, 34, 84);
    }

    private Point ToLocal(Point screenPoint)
    {
        return new Point(screenPoint.X - _virtualBounds.Left, screenPoint.Y - _virtualBounds.Top);
    }

    private static Rectangle GetVirtualBounds()
    {
        var left = SystemInformation.VirtualScreen.Left;
        var top = SystemInformation.VirtualScreen.Top;
        var right = SystemInformation.VirtualScreen.Right;
        var bottom = SystemInformation.VirtualScreen.Bottom;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}
