using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AutomationTool;

public sealed class MacroPreview
{
    public List<Point> PerfectPath { get; } = new();
    public List<List<Point>> NoisyPaths { get; } = new();
    public List<PreviewMarker> PerfectMarkers { get; } = new();
    public List<List<PreviewMarker>> NoisyMarkers { get; } = new();
}

public sealed class PreviewMarker
{
    public Point Location { get; init; }
    public MacroEventKind Kind { get; init; }
    public string Text { get; init; } = "";
}

public static class MacroPreviewBuilder
{
    public static MacroPreview Build(Macro macro, NoiseSettings noise, int variantCount)
    {
        var preview = new MacroPreview();
        var events = macro.Events.OrderBy(e => e.TimeOffsetMs).ToList();
        var count = Math.Clamp(variantCount, 1, 10);
        foreach (var macroEvent in events.Where(e => e.Kind == MacroEventKind.MouseMove))
        {
            preview.PerfectPath.Add(new Point(macroEvent.X, macroEvent.Y));
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

            FlushMoveSegment(noisyPath, segment, anchor, hasAnchor, noise, random);
            segment.Clear();

            if (TryGetPoint(macroEvent, out var point))
            {
                var noisyPoint = CreateNoisyAnchor(point, macroEvent, noise, random);
                noisyMarkers.Add(CreateMarker(noisyPoint, macroEvent));
                noisyPath.Add(noisyPoint);
                anchor = noisyPoint;
                hasAnchor = true;
            }
        }

        FlushMoveSegment(noisyPath, segment, anchor, hasAnchor, noise, random);
        preview.NoisyPaths.Add(noisyPath);
        preview.NoisyMarkers.Add(noisyMarkers);
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
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        DrawPath(e.Graphics, _preview.PerfectPath, Color.Red, 3);
        foreach (var noisyPath in _preview.NoisyPaths)
        {
            DrawPath(e.Graphics, noisyPath, Color.Gold, 2, 145);
        }

        DrawMarkers(e.Graphics, _preview.PerfectMarkers, Color.Red);
        foreach (var markers in _preview.NoisyMarkers)
        {
            DrawMarkers(e.Graphics, markers, Color.Gold, 155);
        }

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

    private void DrawMarkers(Graphics graphics, List<PreviewMarker> markers, Color color, int alpha = 210)
    {
        using var pen = new Pen(Color.FromArgb(Math.Min(255, alpha + 40), color), 2);
        using var brush = new SolidBrush(Color.FromArgb(alpha, color));
        using var textBrush = new SolidBrush(Color.White);
        using var outlineBrush = new SolidBrush(Color.Black);

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
                DrawOutlinedText(graphics, marker.Text, point.X + 10, point.Y + 4, textBrush, outlineBrush);
            }
        }
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
