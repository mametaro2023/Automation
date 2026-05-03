using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AutomationTool;

public sealed class MacroTrimEditorForm : Form
{
    private readonly Macro _macro;
    private readonly TrimPreviewPanel _previewPanel;
    private readonly TrackBar _startTrack;
    private readonly TrackBar _endTrack;
    private readonly Label _rangeLabel;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    public MacroTrimEditorForm(Macro macro)
    {
        _macro = macro;

        Text = "マクロ編集 - トリミング";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(840, 560);
        ClientSize = new Size(980, 680);
        Font = new Font("MS UI Gothic", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        Controls.Add(root);

        _previewPanel = new TrimPreviewPanel
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.Fixed3D
        };
        root.Controls.Add(_previewPanel, 0, 0);

        var duration = Math.Max(1, (int)Math.Min(int.MaxValue, macro.DurationMs));
        _startTrack = CreateTrackBar(duration);
        _endTrack = CreateTrackBar(duration);
        _endTrack.Value = duration;

        root.Controls.Add(CreateTrackRow("開始を捨てる位置", _startTrack), 0, 1);
        root.Controls.Add(CreateTrackRow("終了を捨てる位置", _endTrack), 0, 2);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        root.Controls.Add(bottom, 0, 3);

        _okButton = new Button
        {
            Text = "適用",
            Size = new Size(88, 26)
        };
        _okButton.Click += OkButtonOnClick;
        bottom.Controls.Add(_okButton);

        _cancelButton = new Button
        {
            Text = "キャンセル",
            Size = new Size(88, 26)
        };
        _cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
        bottom.Controls.Add(_cancelButton);

        _rangeLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 420,
            Height = 26
        };
        bottom.Controls.Add(_rangeLabel);

        _startTrack.ValueChanged += TrackOnValueChanged;
        _endTrack.ValueChanged += TrackOnValueChanged;
        UpdatePreview();
    }

    public List<MacroEvent> TrimmedEvents { get; private set; } = new();

    private static TrackBar CreateTrackBar(int maximum)
    {
        return new TrackBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = maximum,
            TickFrequency = Math.Max(1, maximum / 10),
            SmallChange = Math.Max(1, maximum / 100),
            LargeChange = Math.Max(1, maximum / 20)
        };
    }

    private static Control CreateTrackRow(string labelText, TrackBar trackBar)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        panel.Controls.Add(trackBar, 1, 0);
        return panel;
    }

    private void TrackOnValueChanged(object? sender, EventArgs e)
    {
        if (_startTrack.Value >= _endTrack.Value)
        {
            if (sender == _startTrack)
            {
                _endTrack.Value = Math.Min(_endTrack.Maximum, _startTrack.Value + 1);
            }
            else
            {
                _startTrack.Value = Math.Max(_startTrack.Minimum, _endTrack.Value - 1);
            }
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var startMs = _startTrack.Value;
        var endMs = _endTrack.Value;
        _previewPanel.SetRange(_macro.Events, startMs, endMs);
        _rangeLabel.Text = $"残す範囲: {startMs} ms - {endMs} ms / {_macro.DurationMs} ms";
    }

    private void OkButtonOnClick(object? sender, EventArgs e)
    {
        var startMs = _startTrack.Value;
        var endMs = _endTrack.Value;
        var trimmed = _macro.Events
            .Where(item => item.TimeOffsetMs >= startMs && item.TimeOffsetMs <= endMs)
            .Select(item => CloneWithOffset(item, startMs))
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();

        if (trimmed.Count == 0)
        {
            MessageBox.Show(this, "残すイベントがありません。", "トリミング", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        TrimmedEvents = trimmed;
        DialogResult = DialogResult.OK;
    }

    private static MacroEvent CloneWithOffset(MacroEvent source, long offsetMs)
    {
        return new MacroEvent
        {
            Kind = source.Kind,
            TimeOffsetMs = Math.Max(0, source.TimeOffsetMs - offsetMs),
            X = source.X,
            Y = source.Y,
            Button = source.Button,
            WheelDelta = source.WheelDelta,
            KeyCode = source.KeyCode
        };
    }

    private sealed class TrimPreviewPanel : Panel
    {
        private List<MacroEvent> _events = new();
        private long _startMs;
        private long _endMs;

        public TrimPreviewPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 28, 28);
        }

        public void SetRange(List<MacroEvent> events, long startMs, long endMs)
        {
            _events = events;
            _startMs = startMs;
            _endMs = endMs;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var mousePoints = _events
                .Where(IsDrawablePoint)
                .Select(item => new TimedPoint(item.TimeOffsetMs, new Point(item.X, item.Y)))
                .ToList();

            if (mousePoints.Count == 0)
            {
                DrawEmpty(e.Graphics);
                return;
            }

            var mapper = CreateMapper(mousePoints.Select(item => item.Point).ToList());
            DrawPath(e.Graphics, mousePoints.Select(item => item.Point).ToList(), mapper, Color.FromArgb(90, Color.White), 2);

            var keptPoints = mousePoints
                .Where(item => item.TimeMs >= _startMs && item.TimeMs <= _endMs)
                .Select(item => item.Point)
                .ToList();
            DrawPath(e.Graphics, keptPoints, mapper, Color.Red, 3);

            foreach (var macroEvent in _events.Where(item => item.TimeOffsetMs >= _startMs && item.TimeOffsetMs <= _endMs && IsEventMarker(item)))
            {
                DrawCross(e.Graphics, mapper(new Point(macroEvent.X, macroEvent.Y)), Color.Gold);
            }

            DrawText(e.Graphics);
        }

        private void DrawEmpty(Graphics graphics)
        {
            using var brush = new SolidBrush(Color.White);
            graphics.DrawString("表示できる軌跡がありません。", Font, brush, 12, 12);
        }

        private void DrawText(Graphics graphics)
        {
            using var brush = new SolidBrush(Color.White);
            using var bg = new SolidBrush(Color.FromArgb(160, Color.Black));
            var rect = new Rectangle(10, 10, 420, 50);
            graphics.FillRectangle(bg, rect);
            graphics.DrawString("灰色: 元の軌跡 / 赤: 残す範囲 / 黄×: 残るイベント", Font, brush, 20, 18);
        }

        private Func<Point, Point> CreateMapper(List<Point> points)
        {
            var minX = points.Min(point => point.X);
            var maxX = points.Max(point => point.X);
            var minY = points.Min(point => point.Y);
            var maxY = points.Max(point => point.Y);
            var sourceWidth = Math.Max(1, maxX - minX);
            var sourceHeight = Math.Max(1, maxY - minY);
            var pad = 28;
            var scale = Math.Min(
                Math.Max(0.01, (Width - pad * 2) / (double)sourceWidth),
                Math.Max(0.01, (Height - pad * 2) / (double)sourceHeight));

            return point => new Point(
                pad + (int)Math.Round((point.X - minX) * scale),
                pad + (int)Math.Round((point.Y - minY) * scale));
        }

        private static void DrawPath(Graphics graphics, List<Point> points, Func<Point, Point> mapper, Color color, int width)
        {
            if (points.Count < 2)
            {
                return;
            }

            using var pen = new Pen(color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(pen, points.Select(mapper).ToArray());
        }

        private static void DrawCross(Graphics graphics, Point point, Color color)
        {
            using var pen = new Pen(color, 2);
            const int size = 7;
            graphics.DrawLine(pen, point.X - size, point.Y - size, point.X + size, point.Y + size);
            graphics.DrawLine(pen, point.X - size, point.Y + size, point.X + size, point.Y - size);
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

        private static bool IsEventMarker(MacroEvent macroEvent)
        {
            return macroEvent.Kind is MacroEventKind.MouseDown
                or MacroEventKind.MouseUp
                or MacroEventKind.MouseWheel
                or MacroEventKind.KeyDown
                or MacroEventKind.KeyUp;
        }

        private sealed record TimedPoint(long TimeMs, Point Point);
    }
}
