using System.Drawing;
using System.Drawing.Drawing2D;

namespace AutomationTool;

public sealed class MacroPreview
{
    public List<Point> PerfectPath { get; } = new();
    public List<Point> NoisyPath { get; } = new();
    public List<PreviewMarker> PerfectMarkers { get; } = new();
    public List<PreviewMarker> NoisyMarkers { get; } = new();
}

public sealed class PreviewMarker
{
    public Point Location { get; init; }
    public MacroEventKind Kind { get; init; }
    public string Text { get; init; } = "";
}

public static class MacroPreviewBuilder
{
    public static MacroPreview Build(Macro macro, NoiseSettings noise)
    {
        var preview = new MacroPreview();
        var events = macro.Events.OrderBy(e => e.TimeOffsetMs).ToList();
        var random = new Random(macro.Id.GetHashCode());

        var anchor = Point.Empty;
        var hasAnchor = false;
        var segment = new List<MacroEvent>();

        foreach (var macroEvent in events)
        {
            if (macroEvent.Kind == MacroEventKind.MouseMove)
            {
                preview.PerfectPath.Add(new Point(macroEvent.X, macroEvent.Y));
                segment.Add(macroEvent);
                continue;
            }

            FlushMoveSegment(preview.NoisyPath, segment, anchor, hasAnchor, noise, random);
            segment.Clear();

            if (TryGetPoint(macroEvent, out var point))
            {
                var perfectMarker = CreateMarker(point, macroEvent);
                preview.PerfectMarkers.Add(perfectMarker);

                var noisyPoint = CreateNoisyAnchor(point, macroEvent, noise, random);
                preview.NoisyMarkers.Add(CreateMarker(noisyPoint, macroEvent));
                preview.NoisyPath.Add(noisyPoint);
                anchor = noisyPoint;
                hasAnchor = true;
            }
        }

        FlushMoveSegment(preview.NoisyPath, segment, anchor, hasAnchor, noise, random);
        return preview;
    }

    private static void FlushMoveSegment(
        List<Point> noisyPath,
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
        var accel = Math.Clamp(noise.AccelerationJitterPercent, 0, 50) / 100.0;
        var amplitude = Math.Min(24.0, Math.Max(0.0, length * 0.025 * accel));
        var waveA = (random.NextDouble() * 2.0 - 1.0) * amplitude;
        var waveB = (random.NextDouble() * 2.0 - 1.0) * amplitude * 0.5;

        for (var i = 0; i < segment.Count; i++)
        {
            var t = segment.Count == 1 ? 1.0 : i / (double)(segment.Count - 1);
            var macroEvent = segment[i];
            var sideOffset = Math.Sin(Math.PI * t) * waveA + Math.Sin(Math.PI * 2.0 * t) * waveB;
            noisyPath.Add(new Point(
                (int)Math.Round(macroEvent.X + normalX * sideOffset),
                (int)Math.Round(macroEvent.Y + normalY * sideOffset)));
        }
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
    private readonly Font _markerFont = new("MS UI Gothic", 9F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _helpFont = new("MS UI Gothic", 10F, FontStyle.Regular, GraphicsUnit.Point);

    public PreviewOverlayForm(Macro macro, NoiseSettings noise)
    {
        _preview = MacroPreviewBuilder.Build(macro, noise);
        _virtualBounds = GetVirtualBounds();

        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.72;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "プレビュー";
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Close();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        DrawPath(e.Graphics, _preview.PerfectPath, Color.Red, 3);
        DrawPath(e.Graphics, _preview.NoisyPath, Color.Gold, 2);
        DrawMarkers(e.Graphics, _preview.PerfectMarkers, Color.Red);
        DrawMarkers(e.Graphics, _preview.NoisyMarkers, Color.Gold);
        DrawLegend(e.Graphics);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _markerFont.Dispose();
            _helpFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DrawPath(Graphics graphics, List<Point> points, Color color, int width)
    {
        if (points.Count < 2)
        {
            return;
        }

        using var pen = new Pen(Color.FromArgb(220, color), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(pen, points.Select(ToLocal).ToArray());
    }

    private void DrawMarkers(Graphics graphics, List<PreviewMarker> markers, Color color)
    {
        using var pen = new Pen(color, 2);
        using var brush = new SolidBrush(Color.FromArgb(210, color));
        using var textBrush = new SolidBrush(Color.Black);

        foreach (var marker in markers)
        {
            var point = ToLocal(marker.Location);
            var rect = new Rectangle(point.X - 7, point.Y - 7, 14, 14);

            if (marker.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp)
            {
                var diamond = new[]
                {
                    new Point(point.X, point.Y - 9),
                    new Point(point.X + 9, point.Y),
                    new Point(point.X, point.Y + 9),
                    new Point(point.X - 9, point.Y)
                };
                graphics.FillPolygon(brush, diamond);
                graphics.DrawPolygon(pen, diamond);
            }
            else if (marker.Kind == MacroEventKind.MouseUp)
            {
                graphics.FillRectangle(brush, rect);
                graphics.DrawRectangle(pen, rect);
            }
            else
            {
                graphics.FillEllipse(brush, rect);
                graphics.DrawEllipse(pen, rect);
            }

            if (!string.IsNullOrEmpty(marker.Text))
            {
                graphics.DrawString(marker.Text, _markerFont, textBrush, point.X + 10, point.Y + 4);
            }
        }
    }

    private void DrawLegend(Graphics graphics)
    {
        using var redBrush = new SolidBrush(Color.Red);
        using var yellowBrush = new SolidBrush(Color.Gold);
        using var whiteBrush = new SolidBrush(Color.White);
        using var bgBrush = new SolidBrush(Color.FromArgb(160, Color.Black));
        var box = new Rectangle(18, 18, 360, 92);
        graphics.FillRectangle(bgBrush, box);
        graphics.FillRectangle(redBrush, 34, 38, 32, 5);
        graphics.DrawString("赤: 完全再現", _helpFont, whiteBrush, 74, 30);
        graphics.FillRectangle(yellowBrush, 34, 66, 32, 5);
        graphics.DrawString("黄: ノイズ入り", _helpFont, whiteBrush, 74, 58);
        graphics.DrawString("Esc またはクリックで閉じる", _helpFont, whiteBrush, 34, 84);
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
