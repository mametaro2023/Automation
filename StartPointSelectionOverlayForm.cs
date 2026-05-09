using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AutomationTool;

public sealed class StartPointSelectionOverlayForm : Form
{
    private readonly List<Point> _originalPath;
    private readonly Point _originalStart;
    private readonly Rectangle _virtualBounds;
    private readonly Button _applyButton = new();
    private readonly Button _retryButton = new();
    private readonly Button _cancelButton = new();
    private Point? _selectedStart;

    public StartPointSelectionOverlayForm(IEnumerable<MacroEvent> events, Point originalStart)
    {
        _originalStart = originalStart;
        _originalPath = events
            .Where(IsDrawablePoint)
            .Select(item => new Point(item.X, item.Y))
            .ToList();
        _virtualBounds = SystemInformation.VirtualScreen;

        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.84;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "開始地点修正";

        BuildButtons();
    }

    public Point SelectedStartPoint => _selectedStart ?? _originalStart;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _selectedStart = ToScreenPoint(e.Location);
        Cursor = Cursors.Default;
        SetConfirmationVisible(true);
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }

        if (keyData == Keys.Enter && _selectedStart is not null)
        {
            DialogResult = DialogResult.OK;
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        DrawPath(e.Graphics, _originalPath, Color.FromArgb(120, Color.White), 2);
        DrawStartMarker(e.Graphics, _originalStart, Color.FromArgb(190, 190, 190), "現在");

        if (_selectedStart is not null)
        {
            var shifted = ShiftPath(_originalPath, _selectedStart.Value.X - _originalStart.X, _selectedStart.Value.Y - _originalStart.Y);
            DrawPath(e.Graphics, shifted, Color.FromArgb(245, 95, 220, 255), 4);
            DrawStartMarker(e.Graphics, _selectedStart.Value, Color.FromArgb(95, 220, 255), "変更後");
            DrawMessage(e.Graphics, "変更後はこの軌道になります。承認する場合は Enter または 承認 を押してください。");
            return;
        }

        DrawMessage(e.Graphics, "新しい開始地点を左クリックしてください。Escでキャンセル。");
    }

    private void BuildButtons()
    {
        var width = 86;
        var height = 30;
        var top = _virtualBounds.Height - 56;
        var left = _virtualBounds.Width - 286;

        ConfigureButton(_applyButton, "承認", left, top, width, height);
        ConfigureButton(_retryButton, "選び直し", left + 94, top, width, height);
        ConfigureButton(_cancelButton, "キャンセル", left + 188, top, width, height);

        _applyButton.Click += (_, _) => DialogResult = _selectedStart is null ? DialogResult.None : DialogResult.OK;
        _retryButton.Click += (_, _) =>
        {
            _selectedStart = null;
            Cursor = Cursors.Cross;
            SetConfirmationVisible(false);
            Invalidate();
        };
        _cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[] { _applyButton, _retryButton, _cancelButton });
        SetConfirmationVisible(false);
    }

    private static void ConfigureButton(Button button, string text, int x, int y, int width, int height)
    {
        button.Text = text;
        button.SetBounds(x, y, width, height);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(34, 35, 36);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(80, 82, 84);
        button.UseVisualStyleBackColor = false;
    }

    private void SetConfirmationVisible(bool visible)
    {
        _applyButton.Visible = visible;
        _retryButton.Visible = visible;
        _cancelButton.Visible = visible;
    }

    private void DrawMessage(Graphics graphics, string text)
    {
        var rect = new Rectangle(24, 22, 650, 42);
        using var bg = new SolidBrush(Color.FromArgb(230, 18, 19, 20));
        using var border = new Pen(Color.FromArgb(72, 74, 76));
        graphics.FillRectangle(bg, rect);
        graphics.DrawRectangle(border, rect);
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            rect,
            Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawStartMarker(Graphics graphics, Point screenPoint, Color color, string label)
    {
        var point = ToLocalPoint(screenPoint);
        using var pen = new Pen(color, 3);
        graphics.DrawLine(pen, point.X - 12, point.Y, point.X + 12, point.Y);
        graphics.DrawLine(pen, point.X, point.Y - 12, point.X, point.Y + 12);
        TextRenderer.DrawText(
            graphics,
            label,
            Font,
            new Point(point.X + 14, point.Y - 10),
            color);
    }

    private void DrawPath(Graphics graphics, List<Point> points, Color color, int width)
    {
        if (points.Count < 2)
        {
            return;
        }

        var local = points.Select(ToLocalPoint).ToList();
        var display = DrawingPathOptimizer.SimplifyForDisplay(local);
        if (display.Count < 2)
        {
            return;
        }

        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(pen, display.ToArray());
    }

    private Point ToLocalPoint(Point screenPoint)
    {
        return new Point(screenPoint.X - _virtualBounds.Left, screenPoint.Y - _virtualBounds.Top);
    }

    private Point ToScreenPoint(Point localPoint)
    {
        return new Point(localPoint.X + _virtualBounds.Left, localPoint.Y + _virtualBounds.Top);
    }

    private static List<Point> ShiftPath(List<Point> points, int deltaX, int deltaY)
    {
        return points.Select(point => new Point(point.X + deltaX, point.Y + deltaY)).ToList();
    }

    private static bool IsDrawablePoint(MacroEvent macroEvent)
    {
        return (macroEvent.Kind is MacroEventKind.MouseMove
            or MacroEventKind.MouseDown
            or MacroEventKind.MouseUp
            or MacroEventKind.MouseWheel
            or MacroEventKind.KeyDown
            or MacroEventKind.KeyUp)
            && (macroEvent.X != 0 || macroEvent.Y != 0);
    }
}
