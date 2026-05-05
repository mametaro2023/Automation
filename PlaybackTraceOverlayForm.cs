using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AutomationTool;

public sealed class PlaybackTraceOverlayForm : Form
{
    private const int MaxPoints = 12000;
    private const int PlanRevealMs = 520;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly List<Point> _plannedPoints = new();
    private readonly List<Point> _actualPoints = new();
    private readonly object _lock = new();
    private readonly Rectangle _virtualBounds;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Diagnostics.Stopwatch _planRevealClock = new();
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

        _timer.Interval = GetTraceTimerIntervalMs();
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

        _planRevealClock.Restart();
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

        var planProgress = _planRevealClock.IsRunning
            ? Math.Clamp(_planRevealClock.ElapsedMilliseconds / (double)PlanRevealMs, 0.0, 1.0)
            : 1.0;
        if (planProgress >= 1.0)
        {
            _planRevealClock.Stop();
        }

        var visiblePlan = GetPathPrefix(plannedPoints, SmoothStep(planProgress));
        if (visiblePlan.Count >= 2)
        {
            using var plannedGlow = new Pen(Color.FromArgb(55, 0, 0, 0), 5)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var plannedPen = new Pen(Color.FromArgb(82, Color.Gold), 2)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var plannedPath = CreateSmoothPath(visiblePlan);
            e.Graphics.DrawPath(plannedGlow, plannedPath);
            e.Graphics.DrawPath(plannedPen, plannedPath);
        }

        if (actualPoints.Count < 2)
        {
            return;
        }

        using var glow = new Pen(Color.FromArgb(105, 0, 0, 0), 5)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var pen = new Pen(Color.FromArgb(235, Color.Gold), 3)
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
            _planRevealClock.Stop();
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

    private static List<Point> GetPathPrefix(IReadOnlyList<Point> points, double progress)
    {
        if (points.Count < 2)
        {
            return points.ToList();
        }

        if (progress >= 1.0)
        {
            return points.ToList();
        }

        var totalLength = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            totalLength += Distance(points[i - 1], points[i]);
        }

        if (totalLength <= 0.0)
        {
            return points.Take(1).ToList();
        }

        var targetLength = totalLength * Math.Clamp(progress, 0.0, 1.0);
        var result = new List<Point> { points[0] };
        var consumed = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var segmentLength = Distance(previous, current);
            if (segmentLength <= 0.0)
            {
                continue;
            }

            if (consumed + segmentLength >= targetLength)
            {
                var t = Math.Clamp((targetLength - consumed) / segmentLength, 0.0, 1.0);
                result.Add(new Point(
                    (int)Math.Round(previous.X + (current.X - previous.X) * t),
                    (int)Math.Round(previous.Y + (current.Y - previous.Y) * t)));
                return result;
            }

            result.Add(current);
            consumed += segmentLength;
        }

        return result;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static int GetTraceTimerIntervalMs()
    {
        var refreshRate = Screen.AllScreens
            .Select(GetDisplayRefreshRate)
            .Where(hertz => hertz is not null)
            .DefaultIfEmpty(60)
            .Max() ?? 60;
        return Math.Clamp((int)Math.Floor(1000.0 / (refreshRate + 1)), 1, 16);
    }

    private static int? GetDisplayRefreshRate(Screen screen)
    {
        var devMode = new NativeMethods.DevMode
        {
            DmDeviceName = new string('\0', 32),
            DmFormName = new string('\0', 32),
            DmSize = (ushort)Marshal.SizeOf<NativeMethods.DevMode>()
        };
        if (!NativeMethods.EnumDisplaySettings(screen.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
        {
            return null;
        }

        var hertz = (int)devMode.DmDisplayFrequency;
        return hertz is >= 30 and <= 1000 ? hertz : null;
    }
}
