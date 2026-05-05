using System.Drawing.Drawing2D;
namespace AutomationTool;

public sealed class PlaybackTraceOverlayForm : Form
{
    private const int MaxPoints = 12000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly List<Point> _points = new();
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

        var point = new Point(screenPoint.X - _virtualBounds.Left, screenPoint.Y - _virtualBounds.Top);
        if (_lastPoint == point)
        {
            return;
        }

        lock (_lock)
        {
            _points.Add(point);
            if (_points.Count > MaxPoints)
            {
                _points.RemoveRange(0, _points.Count - MaxPoints);
            }
        }

        _lastPoint = point;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        List<Point> points;
        lock (_lock)
        {
            points = _points.ToList();
        }

        if (points.Count < 2)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
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
        e.Graphics.DrawLines(glow, points.ToArray());
        e.Graphics.DrawLines(pen, points.ToArray());
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
}
