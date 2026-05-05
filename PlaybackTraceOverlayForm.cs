using System.Drawing.Drawing2D;
namespace AutomationTool;

public sealed class PlaybackTraceOverlayForm : Form
{
    private const int MaxPoints = 12000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly List<Point> _plannedPoints = new();
    private readonly List<Point> _actualPoints = new();
    private readonly object _lock = new();
    private readonly Rectangle _virtualBounds;
    private readonly System.Windows.Forms.Timer _timer = new();
    private Point? _lastPoint;

    public PlaybackTraceOverlayForm()
    {
        _virtualBounds = GetVirtualBounds();
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        Enabled = false;
        Text = "再生軌道";

        _timer.Interval = 33;
        _timer.Tick += (_, _) => Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void Start()
    {
        Show();
        _timer.Start();
    }

    public void SetPlannedPath(IReadOnlyList<Point> screenPoints)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetPlannedPath(screenPoints));
            return;
        }

        lock (_lock)
        {
            _plannedPoints.Clear();
            _actualPoints.Clear();
            _lastPoint = null;
            Point? previous = null;
            foreach (var screenPoint in screenPoints)
            {
                var point = ToOverlayPoint(screenPoint);
                if (previous != point)
                {
                    _plannedPoints.Add(point);
                    previous = point;
                }
            }
        }

        Invalidate();
    }

    public void AddPoint(Point screenPoint)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AddPoint(screenPoint));
            return;
        }

        var point = ToOverlayPoint(screenPoint);
        if (_lastPoint == point)
        {
            return;
        }

        lock (_lock)
        {
            _actualPoints.Add(point);
            if (_actualPoints.Count > MaxPoints)
            {
                _actualPoints.RemoveRange(0, _actualPoints.Count - MaxPoints);
            }
        }

        _lastPoint = point;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        List<Point> plannedPoints;
        List<Point> actualPoints;
        lock (_lock)
        {
            plannedPoints = _plannedPoints.ToList();
            actualPoints = _actualPoints.ToList();
        }

        if (plannedPoints.Count < 2 && actualPoints.Count < 2)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (plannedPoints.Count >= 2)
        {
            using var plannedGlow = new Pen(Color.FromArgb(55, 0, 0, 0), 5)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var plannedPen = new Pen(Color.FromArgb(92, 70, 220, 255), 2)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var plannedPath = CreateSmoothPath(plannedPoints);
            e.Graphics.DrawPath(plannedGlow, plannedPath);
            e.Graphics.DrawPath(plannedPen, plannedPath);
        }

        if (actualPoints.Count < 2)
        {
            return;
        }

        using var glow = new Pen(Color.FromArgb(90, 0, 0, 0), 5)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var pen = new Pen(Color.FromArgb(230, 70, 220, 255), 2)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var actualPath = CreateSmoothPath(actualPoints);
        e.Graphics.DrawPath(glow, actualPath);
        e.Graphics.DrawPath(pen, actualPath);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Rectangle GetVirtualBounds()
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        return new Rectangle(left, top, width, height);
    }

    private Point ToOverlayPoint(Point screenPoint)
    {
        return new Point(screenPoint.X - _virtualBounds.Left, screenPoint.Y - _virtualBounds.Top);
    }

    private static GraphicsPath CreateSmoothPath(IReadOnlyList<Point> points)
    {
        var path = new GraphicsPath();
        if (points.Count == 0)
        {
            return path;
        }

        var simplified = Simplify(points);
        if (simplified.Count == 1)
        {
            path.AddEllipse(simplified[0].X - 1, simplified[0].Y - 1, 2, 2);
            return path;
        }

        if (simplified.Count < 4)
        {
            path.AddLines(simplified.Select(point => new PointF(point.X, point.Y)).ToArray());
            return path;
        }

        path.StartFigure();
        for (var i = 0; i < simplified.Count - 1; i++)
        {
            var p0 = simplified[Math.Max(0, i - 1)];
            var p1 = simplified[i];
            var p2 = simplified[i + 1];
            var p3 = simplified[Math.Min(simplified.Count - 1, i + 2)];
            var c1 = new PointF(
                p1.X + (p2.X - p0.X) / 6f,
                p1.Y + (p2.Y - p0.Y) / 6f);
            var c2 = new PointF(
                p2.X - (p3.X - p1.X) / 6f,
                p2.Y - (p3.Y - p1.Y) / 6f);

            path.AddBezier(
                new PointF(p1.X, p1.Y),
                c1,
                c2,
                new PointF(p2.X, p2.Y));
        }

        return path;
    }

    private static List<Point> Simplify(IReadOnlyList<Point> points)
    {
        var simplified = new List<Point>(points.Count);
        Point? previous = null;
        foreach (var point in points)
        {
            if (previous == point)
            {
                continue;
            }

            simplified.Add(point);
            previous = point;
        }

        return simplified;
    }
}
