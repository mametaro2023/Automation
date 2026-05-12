using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace AutomationTool;

public sealed class MacroTrimEditorForm : Form
{
    private readonly List<MacroEvent> _events;
    private readonly MacroEditorCanvas _canvas = new();
    private readonly MacroTimelineControl _timeline = new();
    private readonly TrimRangeControl _trimRange = new();
    private readonly SmoothLabel _rangeLabel = new();
    private readonly Label _selectedLabel = new();
    private readonly NumericUpDown _waitBeforeBox = new();
    private readonly NumericUpDown _xBox = new();
    private readonly NumericUpDown _yBox = new();
    private readonly Button _deleteEventButton = new();
    private readonly Button _applyWaitButton = new();
    private readonly Button _applyPositionButton = new();
    private readonly Button _setStartPointButton = new();
    private readonly Button _addEventButton = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
    private readonly System.Windows.Forms.Timer _rangeRefreshTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _playbackTimer = new();
    private readonly PreviewSoundPlayer _editorSoundPlayer = new();
    private readonly System.Diagnostics.Stopwatch _playbackClock = new();
    private bool _rangeRefreshPending;
    private bool _isPreviewPlaying;
    private long _previewStartTimeMs;
    private long _previewBaseTimeMs;
    private long _currentTimeMs;
    private long _lastEditorFeedbackTimeMs;
    private bool _timelineEditUndoSaved;
    private int? _selectedEventIndex;
    private readonly long _initialTrimStartMs;
    private readonly long _initialTrimEndMs;

    public MacroTrimEditorForm(Macro macro, bool showScreenshotBackground = true, string? screenshotPath = null)
    {
        _events = macro.Events
            .Select(CloneEvent)
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();
        var rawDuration = Math.Max(0, GetDuration());
        _initialTrimStartMs = Math.Clamp(macro.TrimStartMs, 0, Math.Max(0, rawDuration - 1));
        _initialTrimEndMs = Math.Clamp(macro.TrimEndMs ?? rawDuration, _initialTrimStartMs + 1, Math.Max(1, rawDuration));

        Text = "マクロ編集";
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1100, 720);
        Font = new Font("Yu Gothic UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.White;
        KeyPreview = true;

        _currentTimeMs = _initialTrimStartMs;
        var refreshInterval = GetRefreshIntervalMs(this);
        _rangeRefreshTimer.Interval = refreshInterval;
        _playbackTimer.Interval = refreshInterval;
        _trimRange.SetRange(_initialTrimStartMs, _initialTrimEndMs, Math.Max(1, GetDuration()));
        if (showScreenshotBackground)
        {
            _canvas.SetBackground(LoadScreenshotBackground(screenshotPath, macro));
        }

        BuildInterface();
        WireEvents();
        RefreshEditor();
    }

    public List<MacroEvent> EditedEvents { get; private set; } = new();
    public long TrimStartMs { get; private set; }
    public long? TrimEndMs { get; private set; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _rangeRefreshTimer.Stop();
        _rangeRefreshTimer.Dispose();
        _playbackTimer.Stop();
        _playbackTimer.Dispose();
        _editorSoundPlayer.Dispose();
        _canvas.DisposeBackground();
        base.OnFormClosed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var refreshInterval = GetRefreshIntervalMs(this);
        _rangeRefreshTimer.Interval = refreshInterval;
        _playbackTimer.Interval = refreshInterval;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.Z)
        {
            Redo();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.Z)
        {
            Undo();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.Y)
        {
            Redo();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }

        if (e.KeyCode == Keys.Space && !IsEditingValue())
        {
            TogglePreviewPlayback();
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Delete && _selectedEventIndex is not null && !IsEditingValue())
        {
            DeleteSelectedEvent();
            return;
        }

        base.OnKeyDown(e);
    }

    private bool IsEditingValue()
    {
        return ActiveControl is NumericUpDown or TextBoxBase;
    }

    private static int GetRefreshIntervalMs(Control control)
    {
        var hertz = 60;
        try
        {
            var screen = Screen.FromControl(control);
            var devMode = new NativeMethods.DevMode
            {
                DmDeviceName = new string('\0', 32),
                DmFormName = new string('\0', 32),
                DmSize = (ushort)Marshal.SizeOf<NativeMethods.DevMode>()
            };
            if (NativeMethods.EnumDisplaySettings(screen.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode)
                && devMode.DmDisplayFrequency is >= 30 and <= 500)
            {
                hertz = (int)devMode.DmDisplayFrequency;
            }
        }
        catch
        {
            hertz = 60;
        }

        return Math.Max(1, (int)Math.Round(1000.0 / hertz));
    }

    private static ScreenshotBackground? LoadScreenshotBackground(string? screenshotPath, Macro macro)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath)
            || !File.Exists(screenshotPath)
            || macro.ScreenshotWidth <= 0
            || macro.ScreenshotHeight <= 0)
        {
            return null;
        }

        try
        {
            return new ScreenshotBackground(
                Image.FromFile(screenshotPath),
                new Rectangle(
                    macro.ScreenshotX,
                    macro.ScreenshotY,
                    macro.ScreenshotWidth,
                    macro.ScreenshotHeight));
        }
        catch
        {
            return null;
        }
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 286));
        Controls.Add(root);

        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(34, 37, 43),
            Padding = new Padding(16, 8, 16, 6)
        };
        root.Controls.Add(header, 0, 0);

        header.Controls.Add(new Label
        {
            Text = "マクロ編集",
            Dock = DockStyle.Left,
            AutoSize = false,
            Width = 160,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White
        });
        header.Controls.Add(new Label
        {
            Text = "軌道またはタイムライン上のイベントを選択。下部タイムラインは動画編集のように時間軸で入力を確認できます。Ctrl+Z / Ctrl+Y 対応",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(205, 210, 218)
        });

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 6,
            BackColor = Color.FromArgb(47, 51, 58)
        };
        root.Controls.Add(mainSplit, 0, 1);

        _canvas.Dock = DockStyle.Fill;
        mainSplit.Panel1.Controls.Add(_canvas);

        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(29, 32, 37)
        };
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainSplit.Panel2.Controls.Add(side);
        Shown += (_, _) => LayoutMainSplit(mainSplit);

        side.Controls.Add(CreatePropertyPanel(), 0, 0);

        root.Controls.Add(CreateBottomPanel(), 0, 2);
    }

    private static void LayoutMainSplit(SplitContainer split)
    {
        const int panel1Min = 420;
        const int panel2Min = 330;
        const int desiredPanel2 = 390;
        var availableWidth = split.ClientSize.Width - split.SplitterWidth;
        if (availableWidth <= panel1Min + panel2Min)
        {
            return;
        }

        split.SplitterDistance = Math.Clamp(
            availableWidth - desiredPanel2,
            panel1Min,
            availableWidth - panel2Min);
        split.Panel1MinSize = panel1Min;
        split.Panel2MinSize = panel2Min;
    }

    private Control CreatePropertyPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 10,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(34, 37, 43)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _selectedLabel.Text = "選択なし";
        _selectedLabel.ForeColor = Color.White;
        _selectedLabel.Dock = DockStyle.Fill;
        _selectedLabel.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_selectedLabel, 0, 0);
        panel.SetColumnSpan(_selectedLabel, 3);

        AddPropertyRow(panel, 1, "直前待ち(ms)", _waitBeforeBox, _applyWaitButton, "適用");
        ConfigureNumber(_waitBeforeBox, 0, 600000, 1);

        AddPropertyRow(panel, 3, "X座標", _xBox, null, "");
        ConfigureNumber(_xBox, -100000, 100000, 1);
        AddPropertyRow(panel, 4, "Y座標", _yBox, _applyPositionButton, "適用");
        ConfigureNumber(_yBox, -100000, 100000, 1);

        _deleteEventButton.Text = "選択イベント削除";
        _deleteEventButton.Dock = DockStyle.Fill;
        panel.Controls.Add(_deleteEventButton, 0, 6);
        panel.SetColumnSpan(_deleteEventButton, 3);

        _setStartPointButton.Text = "開始地点修正";
        _setStartPointButton.Dock = DockStyle.Fill;
        panel.Controls.Add(_setStartPointButton, 0, 7);
        panel.SetColumnSpan(_setStartPointButton, 3);

        _addEventButton.Text = "現在時刻にイベント追加";
        _addEventButton.Dock = DockStyle.Fill;
        panel.Controls.Add(_addEventButton, 0, 8);
        panel.SetColumnSpan(_addEventButton, 3);

        return panel;
    }

    private static void AddPropertyRow(
        TableLayoutPanel panel,
        int row,
        string labelText,
        Control control,
        Button? button,
        string buttonText)
    {
        panel.Controls.Add(new Label
        {
            Text = labelText,
            ForeColor = Color.FromArgb(220, 224, 230),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(control, 1, row);
        if (button is not null)
        {
            button.Text = buttonText;
            button.Dock = DockStyle.Fill;
            panel.Controls.Add(button, 2, row);
        }
    }

    private Control CreateBottomPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.FromArgb(34, 37, 43)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TimelineLabelControl.LabelColumnWidth));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        _rangeLabel.Dock = DockStyle.Fill;
        _rangeLabel.ForeColor = Color.White;
        panel.Controls.Add(_rangeLabel, 0, 0);
        panel.SetColumnSpan(_rangeLabel, 2);

        var timelineLabels = new TimelineLabelControl
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        var timelineHost = new TimelineScrollHostPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.FromArgb(18, 20, 24),
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        timelineHost.WheelRequested += args =>
        {
            var timelinePoint = _timeline.PointToClient(timelineHost.PointToScreen(args.Location));
            _timeline.HandleTimelineWheel(args.Delta, timelinePoint, ModifierKeys);
        };
        var timelineWidth = 0;
        void CenterTimelineOnPlayhead()
        {
            var x = _timeline.CurrentTimeContentX;
            var targetX = Math.Max(0, x - timelineHost.ClientSize.Width / 2);
            timelineHost.AutoScrollPosition = new Point(targetX, Math.Max(0, -timelineHost.AutoScrollPosition.Y));
            UpdateTimelineViewport();
        }

        void ResizeTimeline()
        {
            var width = Math.Max(timelineHost.ClientSize.Width, timelineWidth <= 0 ? timelineHost.ClientSize.Width : timelineWidth);
            _timeline.Width = Math.Max(260, width);
            _timeline.Height = Math.Max(_timeline.ContentHeight, timelineHost.ClientSize.Height);
            timelineLabels.SetContentHeight(_timeline.Height);
            UpdateTimelineViewport();
        }

        void UpdateTimelineViewport()
        {
            var current = timelineHost.AutoScrollPosition;
            _timeline.SetViewport(new Point(-current.X, -current.Y), timelineHost.ClientSize);
            timelineLabels.SetVerticalOffset(-current.Y);
        }

        _timeline.Location = Point.Empty;
        _timeline.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _timeline.MinimumSize = new Size(260, 120);
        _timeline.ContentHeightChanged += height =>
        {
            _timeline.Height = Math.Max(height, timelineHost.ClientSize.Height);
            timelineLabels.SetContentHeight(_timeline.Height);
        };
        _timeline.RowHeadersChanged += timelineLabels.SetRows;
        _timeline.PanRequested += delta =>
        {
            var current = timelineHost.AutoScrollPosition;
            var nextX = Math.Max(0, -current.X - delta.X);
            var nextY = Math.Max(0, -current.Y - delta.Y);
            timelineHost.AutoScrollPosition = new Point(nextX, nextY);
            UpdateTimelineViewport();
        };
        _timeline.HorizontalZoomRequested += (factor, anchorX) =>
        {
            var oldWidth = Math.Max(1, _timeline.Width);
            var hostWidth = Math.Max(1, timelineHost.ClientSize.Width);
            timelineWidth = Math.Clamp((int)Math.Round(oldWidth * factor), hostWidth, Math.Max(hostWidth, 24000));
            ResizeTimeline();
            CenterTimelineOnPlayhead();
        };
        _timeline.VerticalZoomRequested += (factor, anchorY) =>
        {
            var current = timelineHost.AutoScrollPosition;
            _timeline.SetVerticalScale(_timeline.VerticalScale * factor);
            ResizeTimeline();
            timelineHost.AutoScrollPosition = new Point(Math.Max(0, -current.X), Math.Max(0, -current.Y));
            CenterTimelineOnPlayhead();
        };
        timelineHost.Scroll += (_, _) => UpdateTimelineViewport();
        timelineHost.Resize += (_, _) =>
        {
            ResizeTimeline();
        };
        timelineHost.Controls.Add(_timeline);
        timelineHost.HandleCreated += (_, _) =>
        {
            ResizeTimeline();
            UpdateTimelineViewport();
        };
        panel.Controls.Add(timelineLabels, 0, 1);
        panel.Controls.Add(timelineHost, 1, 1);

        _trimRange.Dock = DockStyle.Fill;
        panel.Controls.Add(_trimRange, 1, 2);

        var help = new Label
        {
            Text = "灰: 全体 / 赤: 残す範囲 / 水色: 選択 / 黄: 入力点",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(210, 215, 222),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0)
        };
        panel.Controls.Add(help, 2, 0);
        panel.SetRowSpan(help, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        panel.Controls.Add(buttons, 2, 2);

        _okButton.Text = "適用";
        _okButton.Size = new Size(84, 28);
        buttons.Controls.Add(_okButton);

        _cancelButton.Text = "キャンセル";
        _cancelButton.Size = new Size(92, 28);
        buttons.Controls.Add(_cancelButton);

        return panel;
    }

    private void WireEvents()
    {
        _canvas.EventSelected += SelectEvent;
        _timeline.EventSelected += SelectEvent;
        _timeline.EventTimeEditPreview += TimelineOnEventTimeEditPreview;
        _timeline.EventTimeEdited += TimelineOnEventTimeEdited;
        _timeline.EventTimeEditCompleted += TimelineOnEventTimeEditCompleted;
        _timeline.CurrentTimeSelected += SetCurrentTime;
        _trimRange.RangeChanged += (_, _) => QueueRangeRefresh();
        _trimRange.RangeChangeCompleted += (_, _) => FlushRangeRefresh();
        _rangeRefreshTimer.Tick += (_, _) => FlushRangeRefresh();
        _playbackTimer.Tick += (_, _) => PreviewPlaybackOnTick();
        _deleteEventButton.Click += (_, _) => DeleteSelectedEvent();
        _applyWaitButton.Click += (_, _) => ApplyWaitBefore();
        _applyPositionButton.Click += (_, _) => ApplyPosition();
        _setStartPointButton.Click += (_, _) => BeginSetStartPoint();
        _addEventButton.Click += (_, _) => AddEventAtCurrentTime();
        _okButton.Click += OkButtonOnClick;
        _cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
    }

    private static void ConfigureNumber(NumericUpDown box, int minimum, int maximum, int increment)
    {
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.Increment = increment;
        box.ThousandsSeparator = true;
    }

    private void SelectEvent(int eventIndex)
    {
        if (eventIndex < 0 || eventIndex >= _events.Count)
        {
            return;
        }

        _selectedEventIndex = eventIndex;
        _canvas.SetSelection(eventIndex);
        _timeline.SetSelection(eventIndex);
        RefreshSelectedDetails();
    }

    private void RefreshEditor()
    {
        UpdateTrackRange();
        RefreshCanvasAndRange(rebuildVisualCache: true);
        RefreshSelectedDetails();
    }

    private void QueueRangeRefresh()
    {
        UpdateRangeLabel();
        _timeline.SetData(_events, _trimRange.StartMs, _trimRange.EndMs, GetDuration(), _selectedEventIndex, _currentTimeMs);
        _rangeRefreshPending = true;
        if (!_rangeRefreshTimer.Enabled)
        {
            _rangeRefreshTimer.Start();
        }
    }

    private void FlushRangeRefresh()
    {
        if (!_rangeRefreshPending && !_rangeRefreshTimer.Enabled)
        {
            return;
        }

        _rangeRefreshTimer.Stop();
        _rangeRefreshPending = false;
        RefreshCanvasAndRange(rebuildVisualCache: false);
    }

    private void RefreshCanvasAndRange(bool rebuildVisualCache)
    {
        var startMs = _trimRange.StartMs;
        var endMs = _trimRange.EndMs;
        _currentTimeMs = Math.Clamp(_currentTimeMs, startMs, endMs);
        if (rebuildVisualCache)
        {
            _canvas.SetData(_events, startMs, endMs, _selectedEventIndex);
        }
        else
        {
            _canvas.SetTrimRange(startMs, endMs, _selectedEventIndex);
        }

        _canvas.SetCurrentTime(_currentTimeMs, _isPreviewPlaying);
        _timeline.SetData(_events, startMs, endMs, GetDuration(), _selectedEventIndex, _currentTimeMs);
        _trimRange.SetRange(startMs, endMs, Math.Max(1, GetDuration()));
        UpdateRangeLabel();
    }

    private void UpdateRangeLabel()
    {
        var text = $"残す範囲: {_trimRange.StartMs:N0} ms - {_trimRange.EndMs:N0} ms / {GetDuration():N0} ms";
        if (_rangeLabel.Text != text)
        {
            _rangeLabel.Text = text;
        }
    }

    private void RefreshSelectedDetails()
    {
        var hasSelection = _selectedEventIndex is >= 0 && _selectedEventIndex < _events.Count;
        _deleteEventButton.Enabled = hasSelection;
        _applyWaitButton.Enabled = hasSelection;
        _applyPositionButton.Enabled = hasSelection;
        _waitBeforeBox.Enabled = hasSelection;
        _xBox.Enabled = hasSelection;
        _yBox.Enabled = hasSelection;

        if (!hasSelection)
        {
            _selectedLabel.Text = "選択なし";
            _deleteEventButton.Text = "選択イベント削除";
            _waitBeforeBox.Value = 0;
            _xBox.Value = 0;
            _yBox.Value = 0;
            return;
        }

        var index = _selectedEventIndex!.Value;
        var macroEvent = _events[index];
        var previousMs = index == 0 ? 0 : _events[index - 1].TimeOffsetMs;
        var waitBefore = Math.Max(0, macroEvent.TimeOffsetMs - previousMs);
        var pairedIndex = FindPairedEventIndex(_events, index);
        _selectedLabel.Text = pairedIndex is null
            ? $"{macroEvent.TimeOffsetMs:N0} ms  {GetKindText(macroEvent)}  {GetDetailText(macroEvent)}"
            : $"{macroEvent.TimeOffsetMs:N0} ms  {GetKindText(macroEvent)}  {GetDetailText(macroEvent)}  ペアあり";
        _deleteEventButton.Text = pairedIndex is null ? "選択イベント削除" : "選択ペア削除";
        _waitBeforeBox.Value = Math.Min(_waitBeforeBox.Maximum, waitBefore);
        _xBox.Value = Math.Clamp(macroEvent.X, (int)_xBox.Minimum, (int)_xBox.Maximum);
        _yBox.Value = Math.Clamp(macroEvent.Y, (int)_yBox.Minimum, (int)_yBox.Maximum);
    }

    private void SetCurrentTime(long timeMs)
    {
        _currentTimeMs = Math.Clamp(timeMs, _trimRange.StartMs, _trimRange.EndMs);
        _canvas.SetCurrentTime(_currentTimeMs, _isPreviewPlaying);
        _timeline.SetCurrentTime(_currentTimeMs);
        UpdateRangeLabel();
    }

    private void TogglePreviewPlayback()
    {
        if (_isPreviewPlaying)
        {
            _isPreviewPlaying = false;
            _playbackTimer.Stop();
            _playbackClock.Stop();
            _canvas.SetCurrentTime(_currentTimeMs, false);
            return;
        }

        if (_currentTimeMs >= _trimRange.EndMs)
        {
            _currentTimeMs = _trimRange.StartMs;
        }

        _isPreviewPlaying = true;
        _previewBaseTimeMs = _currentTimeMs;
        _previewStartTimeMs = _playbackClock.ElapsedMilliseconds;
        _lastEditorFeedbackTimeMs = _currentTimeMs;
        _playbackClock.Start();
        _playbackTimer.Interval = GetRefreshIntervalMs(this);
        _playbackTimer.Start();
        _canvas.SetCurrentTime(_currentTimeMs, true);
    }

    private void PreviewPlaybackOnTick()
    {
        if (!_isPreviewPlaying)
        {
            return;
        }

        var elapsed = _playbackClock.ElapsedMilliseconds - _previewStartTimeMs;
        var previousTime = _currentTimeMs;
        var nextTime = _previewBaseTimeMs + elapsed;
        if (nextTime >= _trimRange.EndMs)
        {
            nextTime = _trimRange.EndMs;
            _isPreviewPlaying = false;
            _playbackTimer.Stop();
        }

        PlayEditorFeedbackBetween(previousTime, nextTime);
        SetCurrentTime(nextTime);
    }

    private void PlayEditorFeedbackBetween(long fromMs, long toMs)
    {
        if (toMs <= fromMs)
        {
            _lastEditorFeedbackTimeMs = toMs;
            return;
        }

        var startMs = Math.Max(fromMs, _lastEditorFeedbackTimeMs);
        foreach (var macroEvent in _events
                     .Where(IsEventMarker)
                     .Where(item => item.TimeOffsetMs > startMs && item.TimeOffsetMs <= toMs)
                     .OrderBy(item => item.TimeOffsetMs))
        {
            _canvas.AddPlaybackFeedback(macroEvent);
            _editorSoundPlayer.Play(macroEvent.Kind);
        }

        _lastEditorFeedbackTimeMs = toMs;
    }

    private void TimelineOnEventTimeEdited(TimelineEditRequest request)
    {
        if (!TryNormalizeTimelineEdit(request, out var normalized))
        {
            return;
        }

        var startEvent = request.StartEvent;
        var endEvent = request.EndEvent;
        var newStart = normalized.StartMs;
        var newEnd = normalized.EndMs;
        if (startEvent.TimeOffsetMs == newStart && endEvent.TimeOffsetMs == newEnd)
        {
            return;
        }

        if (!_timelineEditUndoSaved)
        {
            SaveUndoState();
            _timelineEditUndoSaved = true;
        }

        startEvent.TimeOffsetMs = newStart;
        endEvent.TimeOffsetMs = newEnd;
        UpdateEditedEventPosition(startEvent, newStart);
        if (!ReferenceEquals(startEvent, endEvent))
        {
            UpdateEditedEventPosition(endEvent, newEnd);
        }

        var startIndex = _events.IndexOf(startEvent);
        SortEventsPreservingSelection(startIndex);
        RefreshEditor();
    }

    private TimelineEditPreview? TimelineOnEventTimeEditPreview(TimelineEditRequest request)
    {
        if (!TryNormalizeTimelineEdit(request, out var normalized))
        {
            _canvas.ClearEventTimePreview();
            return null;
        }

        var startIndex = _events.IndexOf(request.StartEvent);
        var endIndex = _events.IndexOf(request.EndEvent);
        var startPoint = GetPointAtTime(normalized.StartMs, request.StartEvent, request.EndEvent);
        var endPoint = ReferenceEquals(request.StartEvent, request.EndEvent)
            ? startPoint
            : GetPointAtTime(normalized.EndMs, request.StartEvent, request.EndEvent);
        _canvas.SetEventTimePreview(startIndex, endIndex, normalized.StartMs, normalized.EndMs, startPoint, endPoint);
        return normalized;
    }

    private void TimelineOnEventTimeEditCompleted()
    {
        _timelineEditUndoSaved = false;
        _canvas.ClearEventTimePreview();
    }

    private void UpdateEditedEventPosition(MacroEvent macroEvent, long timeMs)
    {
        if (!IsDrawablePoint(macroEvent))
        {
            return;
        }

        var paired = FindPairedEventIndex(_events, _events.IndexOf(macroEvent));
        var point = paired is not null
            ? GetPointAtTime(timeMs, macroEvent, _events[paired.Value])
            : GetPointAtTime(timeMs, macroEvent);
        if (point is null)
        {
            return;
        }

        macroEvent.X = point.Value.X;
        macroEvent.Y = point.Value.Y;
    }

    private bool TryNormalizeTimelineEdit(TimelineEditRequest request, out TimelineEditPreview normalized)
    {
        normalized = new TimelineEditPreview(request.StartMs, request.EndMs);
        var startIndex = _events.IndexOf(request.StartEvent);
        var endIndex = _events.IndexOf(request.EndEvent);
        if (startIndex < 0 || endIndex < 0)
        {
            return false;
        }

        var startMs = request.StartMs;
        var endMs = request.EndMs;
        if (!TryNormalizeEditedPair(startIndex, endIndex, ref startMs, ref endMs))
        {
            return false;
        }

        normalized = new TimelineEditPreview(startMs, endMs);
        return true;
    }

    private bool TryNormalizeEditedPair(int startIndex, int endIndex, ref long startMs, ref long endMs)
    {
        var duration = Math.Max(1, GetDuration());
        if (startIndex == endIndex)
        {
            var singleMs = startMs;
            if (!TryNormalizeSingleEvent(startIndex, ref singleMs))
            {
                return false;
            }

            startMs = endMs = singleMs;
            return true;
        }

        var startEvent = _events[startIndex];
        var endEvent = _events[endIndex];
        var key = GetOverlapKey(startEvent);
        if (key is null || !IsMatchingPair(startEvent, endEvent))
        {
            return false;
        }

        var originalStartMs = Math.Min(startEvent.TimeOffsetMs, endEvent.TimeOffsetMs);
        var originalEndMs = Math.Max(startEvent.TimeOffsetMs, endEvent.TimeOffsetMs);
        var bounds = GetSameInputBounds(key, originalStartMs, originalEndMs, startIndex, endIndex, duration);
        var minStart = bounds.MinMs;
        var maxEnd = bounds.MaxMs;
        foreach (var pair in GetPairedIntervals())
        {
            if (pair.Key != key || (pair.StartIndex == startIndex && pair.EndIndex == endIndex))
            {
                continue;
            }

            if (pair.EndMs <= originalStartMs)
            {
                minStart = Math.Max(minStart, pair.EndMs + 1);
            }
            else if (pair.StartMs >= originalEndMs)
            {
                maxEnd = Math.Min(maxEnd, Math.Max(pair.StartMs - 1, 0));
            }
            else if (startMs < pair.EndMs && endMs > pair.StartMs)
            {
                return false;
            }
        }

        if (maxEnd <= minStart)
        {
            return false;
        }

        var originalLength = Math.Max(1, originalEndMs - originalStartMs);
        var requestedStartDelta = startMs - startEvent.TimeOffsetMs;
        var requestedEndDelta = endMs - endEvent.TimeOffsetMs;
        var isRangeMove = Math.Abs(requestedStartDelta - requestedEndDelta) <= 1;
        if (isRangeMove)
        {
            var minDelta = minStart - originalStartMs;
            var maxDelta = maxEnd - originalEndMs;
            if (maxDelta < minDelta)
            {
                return false;
            }

            var delta = Math.Clamp(requestedStartDelta, minDelta, maxDelta);
            startMs = originalStartMs + delta;
            endMs = originalEndMs + delta;
            return endMs > startMs;
        }

        startMs = Math.Clamp(startMs, minStart, Math.Min(maxEnd - 1, endMs - 1));
        endMs = Math.Clamp(endMs, startMs + 1, maxEnd);
        return endMs > startMs;
    }

    private bool TryNormalizeSingleEvent(int index, ref long timeMs)
    {
        var duration = Math.Max(1, GetDuration());
        var macroEvent = _events[index];
        var key = GetOverlapKey(macroEvent);
        if (key is null)
        {
            timeMs = Math.Clamp(timeMs, 0, duration);
            return true;
        }

        var originalMs = macroEvent.TimeOffsetMs;
        var bounds = GetSameInputBounds(key, originalMs, originalMs, index, index, duration);
        var minMs = bounds.MinMs;
        var maxMs = bounds.MaxMs;
        var pairedIndex = FindPairedEventIndex(_events, index);
        if (pairedIndex is not null)
        {
            var pairedMs = _events[pairedIndex.Value].TimeOffsetMs;
            if (macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown)
            {
                maxMs = Math.Min(maxMs, Math.Max(0, pairedMs - 1));
            }
            else if (macroEvent.Kind is MacroEventKind.MouseUp or MacroEventKind.KeyUp)
            {
                minMs = Math.Max(minMs, pairedMs + 1);
            }
        }

        foreach (var pair in GetPairedIntervals())
        {
            if (pair.Key != key || pair.StartIndex == index || pair.EndIndex == index)
            {
                continue;
            }

            if (pair.EndMs <= originalMs)
            {
                minMs = Math.Max(minMs, pair.EndMs + 1);
            }
            else if (pair.StartMs >= originalMs)
            {
                maxMs = Math.Min(maxMs, Math.Max(0, pair.StartMs - 1));
            }
            else if (timeMs > pair.StartMs && timeMs < pair.EndMs)
            {
                return false;
            }
        }

        if (maxMs < minMs)
        {
            return false;
        }

        timeMs = Math.Clamp(timeMs, minMs, maxMs);
        return true;
    }

    private (long MinMs, long MaxMs) GetSameInputBounds(
        string key,
        long originalStartMs,
        long originalEndMs,
        int startIndex,
        int endIndex,
        long duration)
    {
        var minMs = 0L;
        var maxMs = duration;
        for (var i = 0; i < _events.Count; i++)
        {
            if (i == startIndex || i == endIndex || GetOverlapKey(_events[i]) != key)
            {
                continue;
            }

            var eventMs = _events[i].TimeOffsetMs;
            if (eventMs <= originalStartMs)
            {
                minMs = Math.Max(minMs, eventMs + 1);
            }
            else if (eventMs >= originalEndMs)
            {
                maxMs = Math.Min(maxMs, Math.Max(0, eventMs - 1));
            }
        }

        return (minMs, maxMs);
    }

    private void SortEventsPreservingSelection(int preferredIndex)
    {
        var selectedEvent = _selectedEventIndex is >= 0 && _selectedEventIndex < _events.Count
            ? _events[_selectedEventIndex.Value]
            : preferredIndex >= 0 && preferredIndex < _events.Count
                ? _events[preferredIndex]
                : null;
        _events.Sort(CompareEvents);
        _selectedEventIndex = selectedEvent is null ? null : _events.IndexOf(selectedEvent);
    }

    private void DeleteSelectedEvent()
    {
        if (_selectedEventIndex is null)
        {
            return;
        }

        SaveUndoState();
        var indexes = GetSelectionGroupIndexes(_selectedEventIndex.Value);
        var nextSearchIndex = indexes.Min();
        foreach (var index in indexes.OrderByDescending(item => item))
        {
            _events.RemoveAt(index);
        }

        _selectedEventIndex = FindNextMarkerIndex(Math.Min(nextSearchIndex, _events.Count - 1));
        RefreshEditor();
    }

    private void AddEventAtCurrentTime()
    {
        using var dialog = new EventCaptureDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.CapturedEvent is null)
        {
            return;
        }

        var point = GetPointAtTime(_currentTimeMs);
        if (point is null && _events.LastOrDefault(IsDrawablePoint) is { } last)
        {
            point = new Point(last.X, last.Y);
        }

        var macroEvent = dialog.CapturedEvent;
        macroEvent.TimeOffsetMs = _currentTimeMs;
        macroEvent.X = point?.X ?? 0;
        macroEvent.Y = point?.Y ?? 0;
        var eventsToAdd = new List<MacroEvent> { macroEvent };
        if (macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown)
        {
            var releaseTimeMs = _currentTimeMs + Math.Max(1, dialog.ReleaseDelayMs);
            var releaseEvent = CreateReleaseEvent(macroEvent);
            var releasePoint = GetPointAtTime(releaseTimeMs) ?? point;
            releaseEvent.TimeOffsetMs = releaseTimeMs;
            releaseEvent.X = releasePoint?.X ?? macroEvent.X;
            releaseEvent.Y = releasePoint?.Y ?? macroEvent.Y;
            eventsToAdd.Add(releaseEvent);
        }

        if (!CanInsertEvents(eventsToAdd))
        {
            MessageBox.Show(this, "同じキーまたはマウスボタンの既存イベントと重なるため、このイベントは追加できません。", "イベント追加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveUndoState();
        _events.AddRange(eventsToAdd);
        SortEventsPreservingSelection(_events.Count - 1);
        _selectedEventIndex = _events.IndexOf(macroEvent);
        RefreshEditor();
    }

    private static MacroEvent CreateReleaseEvent(MacroEvent pressEvent)
    {
        return new MacroEvent
        {
            Kind = pressEvent.Kind switch
            {
                MacroEventKind.MouseDown => MacroEventKind.MouseUp,
                MacroEventKind.KeyDown => MacroEventKind.KeyUp,
                _ => pressEvent.Kind
            },
            Button = pressEvent.Button,
            KeyCode = pressEvent.KeyCode,
            WheelDelta = pressEvent.WheelDelta,
            X = pressEvent.X,
            Y = pressEvent.Y
        };
    }

    private bool CanInsertEvents(IReadOnlyList<MacroEvent> eventsToAdd)
    {
        if (eventsToAdd.Count == 1)
        {
            return CanInsertEvent(eventsToAdd[0]);
        }

        var startEvent = eventsToAdd[0];
        var endEvent = eventsToAdd[^1];
        var key = GetOverlapKey(startEvent);
        if (key is null || !IsMatchingPair(startEvent, endEvent) || endEvent.TimeOffsetMs <= startEvent.TimeOffsetMs)
        {
            return false;
        }

        return !_events.Any(existing =>
            GetOverlapKey(existing) == key
            && existing.TimeOffsetMs >= startEvent.TimeOffsetMs
            && existing.TimeOffsetMs <= endEvent.TimeOffsetMs)
            && GetPairedIntervals()
                .Where(interval => interval.Key == key)
                .All(interval => endEvent.TimeOffsetMs < interval.StartMs || startEvent.TimeOffsetMs > interval.EndMs);
    }

    private bool CanInsertEvent(MacroEvent macroEvent)
    {
        var key = GetOverlapKey(macroEvent);
        if (key is null || macroEvent.Kind is MacroEventKind.MouseWheel)
        {
            return true;
        }

        var insideExisting = GetPairedIntervals().Any(interval => interval.Key == key
            && macroEvent.TimeOffsetMs > interval.StartMs
            && macroEvent.TimeOffsetMs < interval.EndMs);
        return macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown
            ? !insideExisting
            : insideExisting;
    }

    private Point? GetPointAtTime(long timeMs, params MacroEvent[] excludedEvents)
    {
        MacroEvent? previous = null;
        foreach (var current in _events)
        {
            if (!IsDrawablePoint(current) || IsExcludedEvent(current, excludedEvents))
            {
                continue;
            }

            previous ??= current;
            if (current.TimeOffsetMs >= timeMs)
            {
                if (current.TimeOffsetMs == previous.TimeOffsetMs)
                {
                    return new Point(current.X, current.Y);
                }

                var t = Math.Clamp((timeMs - previous.TimeOffsetMs) / (double)(current.TimeOffsetMs - previous.TimeOffsetMs), 0.0, 1.0);
                return new Point(
                    (int)Math.Round(previous.X + (current.X - previous.X) * t),
                    (int)Math.Round(previous.Y + (current.Y - previous.Y) * t));
            }

            previous = current;
        }

        return previous is null ? null : new Point(previous.X, previous.Y);
    }

    private static bool IsExcludedEvent(MacroEvent macroEvent, MacroEvent[] excludedEvents)
    {
        foreach (var excluded in excludedEvents)
        {
            if (ReferenceEquals(macroEvent, excluded))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyWaitBefore()
    {
        if (_selectedEventIndex is null)
        {
            return;
        }

        var index = _selectedEventIndex.Value;
        var previousMs = index == 0 ? 0 : _events[index - 1].TimeOffsetMs;
        var currentWait = Math.Max(0, _events[index].TimeOffsetMs - previousMs);
        var delta = (long)_waitBeforeBox.Value - currentWait;
        if (delta == 0)
        {
            return;
        }

        var selectedWasInsideTrim = _events[index].TimeOffsetMs <= _trimRange.EndMs;
        var adjustedEndMs = selectedWasInsideTrim
            ? _trimRange.EndMs + delta
            : (long?)null;

        SaveUndoState();
        for (var i = index; i < _events.Count; i++)
        {
            _events[i].TimeOffsetMs = Math.Max(0, _events[i].TimeOffsetMs + delta);
        }

        if (adjustedEndMs is not null)
        {
            UpdateTrackRange();
            _trimRange.SetRange(
                _trimRange.StartMs,
                Math.Clamp(Math.Max(_trimRange.StartMs + 1, adjustedEndMs.Value), _trimRange.StartMs + 1, _trimRange.DurationMs),
                _trimRange.DurationMs);
        }

        RefreshEditor();
    }

    private void ApplyPosition()
    {
        if (_selectedEventIndex is null)
        {
            return;
        }

        var macroEvent = _events[_selectedEventIndex.Value];
        if (!IsDrawablePoint(macroEvent))
        {
            return;
        }

        if (macroEvent.X == (int)_xBox.Value && macroEvent.Y == (int)_yBox.Value)
        {
            return;
        }

        SaveUndoState();
        macroEvent.X = (int)_xBox.Value;
        macroEvent.Y = (int)_yBox.Value;
        RefreshEditor();
    }

    private void BeginSetStartPoint()
    {
        var firstDrawable = _events.FirstOrDefault(IsDrawablePoint);
        if (firstDrawable is null)
        {
            MessageBox.Show(this, "開始地点を修正できる座標がありません。", "開始地点修正", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var previousState = WindowState;
        var ownerForm = Owner as Form;
        var ownerPreviousState = ownerForm?.WindowState;
        Point? selectedStart = null;
        var result = DialogResult.Cancel;
        WindowState = FormWindowState.Minimized;
        if (ownerForm is not null)
        {
            ownerForm.WindowState = FormWindowState.Minimized;
        }

        try
        {
            using var selector = new StartPointSelectionOverlayForm(_events, new Point(firstDrawable.X, firstDrawable.Y));
            result = selector.ShowDialog();
            if (result == DialogResult.OK)
            {
                selectedStart = selector.SelectedStartPoint;
            }
        }
        finally
        {
            if (ownerForm is not null)
            {
                ownerForm.WindowState = ownerPreviousState == FormWindowState.Minimized
                    ? FormWindowState.Normal
                    : ownerPreviousState!.Value;
                ownerForm.Show();
            }

            WindowState = previousState == FormWindowState.Minimized ? FormWindowState.Normal : previousState;
            Show();
            ownerForm?.Activate();
            Activate();
        }

        if (result == DialogResult.OK && selectedStart is not null)
        {
            ApplyStartPoint(selectedStart.Value);
        }
    }

    private void ApplyStartPoint(Point newStart)
    {
        var firstDrawable = _events.FirstOrDefault(IsDrawablePoint);
        if (firstDrawable is null)
        {
            return;
        }

        var deltaX = newStart.X - firstDrawable.X;
        var deltaY = newStart.Y - firstDrawable.Y;

        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        SaveUndoState();
        foreach (var macroEvent in _events.Where(IsDrawablePoint))
        {
            macroEvent.X += deltaX;
            macroEvent.Y += deltaY;
        }

        RefreshEditor();
    }

    private void UpdateTrackRange()
    {
        var duration = Math.Max(1, GetDuration());
        var startMs = Math.Clamp(_trimRange.StartMs, 0, Math.Max(0, duration - 1));
        var endMs = _trimRange.EndMs <= 0
            ? duration
            : Math.Clamp(_trimRange.EndMs, startMs + 1, duration);
        _trimRange.SetRange(startMs, endMs, duration);
    }

    private void OkButtonOnClick(object? sender, EventArgs e)
    {
        var startMs = _trimRange.StartMs;
        var endMs = _trimRange.EndMs;
        var edited = _events
            .Select(CloneEvent)
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();

        if (edited.Count == 0)
        {
            MessageBox.Show(this, "残すイベントがありません。", "マクロ編集", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        EditedEvents = edited;
        TrimStartMs = startMs;
        TrimEndMs = endMs >= GetDuration() ? null : endMs;
        DialogResult = DialogResult.OK;
    }

    private int? FindNextMarkerIndex(int startIndex)
    {
        if (_events.Count == 0)
        {
            return null;
        }

        for (var i = Math.Max(0, startIndex); i < _events.Count; i++)
        {
            if (IsEventMarker(_events[i]))
            {
                return i;
            }
        }

        for (var i = Math.Min(startIndex, _events.Count - 1); i >= 0; i--)
        {
            if (IsEventMarker(_events[i]))
            {
                return i;
            }
        }

        return null;
    }

    private List<int> GetSelectionGroupIndexes(int selectedIndex)
    {
        var indexes = new List<int> { selectedIndex };
        var pairedIndex = FindPairedEventIndex(_events, selectedIndex);
        if (pairedIndex is not null)
        {
            if (_events[selectedIndex].Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp)
            {
                var start = Math.Min(selectedIndex, pairedIndex.Value);
                var end = Math.Max(selectedIndex, pairedIndex.Value);
                indexes.AddRange(Enumerable.Range(start, end - start + 1));
            }
            else
            {
                indexes.Add(pairedIndex.Value);
            }
        }

        return indexes.Distinct().OrderBy(item => item).ToList();
    }

    private void SaveUndoState()
    {
        _undoStack.Push(CreateSnapshot());
        _redoStack.Clear();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Push(CreateSnapshot());
        RestoreSnapshot(_undoStack.Pop());
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Push(CreateSnapshot());
        RestoreSnapshot(_redoStack.Pop());
    }

    private EditorSnapshot CreateSnapshot()
    {
        return new EditorSnapshot(
            _events.Select(CloneEvent).ToList(),
            _selectedEventIndex,
            (int)Math.Min(int.MaxValue, _trimRange.StartMs),
            (int)Math.Min(int.MaxValue, _trimRange.EndMs));
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _events.Clear();
        _events.AddRange(snapshot.Events.Select(CloneEvent));
        UpdateTrackRange();
        var duration = Math.Max(1, GetDuration());
        var startMs = Math.Clamp(snapshot.StartMs, 0, Math.Max(0, duration - 1));
        var endMs = Math.Clamp(snapshot.EndMs, startMs + 1, duration);
        _trimRange.SetRange(startMs, endMs, duration);
        _selectedEventIndex = snapshot.SelectedIndex is >= 0 && snapshot.SelectedIndex < _events.Count
            ? snapshot.SelectedIndex
            : FindNextMarkerIndex(Math.Min(snapshot.SelectedIndex ?? 0, _events.Count - 1));
        RefreshEditor();
    }

    private long GetDuration()
    {
        return _events.Count == 0 ? 0 : _events.Max(item => item.TimeOffsetMs);
    }

    private static MacroEvent CloneEvent(MacroEvent source)
    {
        return new MacroEvent
        {
            Kind = source.Kind,
            TimeOffsetMs = source.TimeOffsetMs,
            X = source.X,
            Y = source.Y,
            Button = source.Button,
            WheelDelta = source.WheelDelta,
            KeyCode = source.KeyCode
        };
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

    private static int? FindPairedEventIndex(IReadOnlyList<MacroEvent> events, int index)
    {
        if (index < 0 || index >= events.Count)
        {
            return null;
        }

        var macroEvent = events[index];
        if (macroEvent.Kind == MacroEventKind.MouseDown)
        {
            return FindForward(events, index, item => item.Kind == MacroEventKind.MouseUp && item.Button == macroEvent.Button);
        }

        if (macroEvent.Kind == MacroEventKind.MouseUp)
        {
            return FindBackward(events, index, item => item.Kind == MacroEventKind.MouseDown && item.Button == macroEvent.Button);
        }

        if (macroEvent.Kind == MacroEventKind.KeyDown)
        {
            return FindForward(events, index, item => item.Kind == MacroEventKind.KeyUp && item.KeyCode == macroEvent.KeyCode);
        }

        if (macroEvent.Kind == MacroEventKind.KeyUp)
        {
            return FindBackward(events, index, item => item.Kind == MacroEventKind.KeyDown && item.KeyCode == macroEvent.KeyCode);
        }

        return null;
    }

    private List<PairedInterval> GetPairedIntervals()
    {
        var intervals = new List<PairedInterval>();
        for (var i = 0; i < _events.Count; i++)
        {
            var macroEvent = _events[i];
            if (macroEvent.Kind is not (MacroEventKind.MouseDown or MacroEventKind.KeyDown))
            {
                continue;
            }

            var endIndex = FindPairedEventIndex(_events, i);
            var key = GetOverlapKey(macroEvent);
            if (endIndex is null || key is null)
            {
                continue;
            }

            intervals.Add(new PairedInterval(
                i,
                endIndex.Value,
                key,
                Math.Min(macroEvent.TimeOffsetMs, _events[endIndex.Value].TimeOffsetMs),
                Math.Max(macroEvent.TimeOffsetMs, _events[endIndex.Value].TimeOffsetMs)));
        }

        return intervals;
    }

    private static string? GetOverlapKey(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseDown or MacroEventKind.MouseUp => $"mouse:{macroEvent.Button}",
            MacroEventKind.KeyDown or MacroEventKind.KeyUp => $"key:{macroEvent.KeyCode}",
            _ => null
        };
    }

    private static bool IsMatchingPair(MacroEvent first, MacroEvent second)
    {
        return first.Kind switch
        {
            MacroEventKind.MouseDown => second.Kind == MacroEventKind.MouseUp && second.Button == first.Button,
            MacroEventKind.KeyDown => second.Kind == MacroEventKind.KeyUp && second.KeyCode == first.KeyCode,
            _ => first.Kind == second.Kind
        };
    }

    private static int CompareEvents(MacroEvent a, MacroEvent b)
    {
        var time = a.TimeOffsetMs.CompareTo(b.TimeOffsetMs);
        if (time != 0)
        {
            return time;
        }

        return GetEventSortRank(a.Kind).CompareTo(GetEventSortRank(b.Kind));
    }

    private static int GetEventSortRank(MacroEventKind kind)
    {
        return kind switch
        {
            MacroEventKind.MouseUp or MacroEventKind.KeyUp => 0,
            MacroEventKind.MouseMove => 1,
            MacroEventKind.MouseWheel => 2,
            MacroEventKind.MouseDown or MacroEventKind.KeyDown => 3,
            _ => 4
        };
    }

    private static int? FindForward(IReadOnlyList<MacroEvent> events, int index, Func<MacroEvent, bool> predicate)
    {
        for (var i = index + 1; i < events.Count; i++)
        {
            if (predicate(events[i]))
            {
                return i;
            }
        }

        return null;
    }

    private static int? FindBackward(IReadOnlyList<MacroEvent> events, int index, Func<MacroEvent, bool> predicate)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (predicate(events[i]))
            {
                return i;
            }
        }

        return null;
    }

    private static string GetKindText(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseDown => "マウス押下",
            MacroEventKind.MouseUp => "マウス解放",
            MacroEventKind.MouseWheel => "ホイール",
            MacroEventKind.KeyDown => "キー押下",
            MacroEventKind.KeyUp => "キー解放",
            _ => "移動"
        };
    }

    private static string GetDetailText(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseDown or MacroEventKind.MouseUp => macroEvent.Button.ToString(),
            MacroEventKind.MouseWheel => macroEvent.WheelDelta.ToString(),
            MacroEventKind.KeyDown or MacroEventKind.KeyUp => macroEvent.KeyCode.ToString(),
            _ => ""
        };
    }

    private static string GetShortEventLabel(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseDown => macroEvent.Button.ToString(),
            MacroEventKind.KeyDown => macroEvent.KeyCode.ToString(),
            MacroEventKind.MouseWheel => macroEvent.WheelDelta > 0 ? "Wheel +" : "Wheel -",
            _ => ""
        };
    }

    private sealed record EditorSnapshot(List<MacroEvent> Events, int? SelectedIndex, int StartMs, int EndMs);
    private sealed record ScreenshotBackground(Image Image, Rectangle Bounds);
    private sealed record TimelineEditRequest(MacroEvent StartEvent, MacroEvent EndEvent, long StartMs, long EndMs);
    private sealed record TimelineEditPreview(long StartMs, long EndMs);
    private sealed record PairedInterval(int StartIndex, int EndIndex, string Key, long StartMs, long EndMs);

    private sealed class SmoothLabel : Control
    {
        public SmoothLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }
    }

    private sealed class EventCaptureDialog : Form, IMessageFilter
    {
        private const int FixedReleaseDelayMs = 60;

        private readonly Label _messageLabel = new();
        private readonly Button _cancelButton = new();
        private readonly NativeMethods.HookProc _mouseProc;
        private IntPtr _mouseHook;
        private bool _messageFilterRegistered;

        public EventCaptureDialog()
        {
            _mouseProc = MouseHookCallback;
            Text = "イベント追加";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 126);
            KeyPreview = true;
            BackColor = Color.FromArgb(34, 37, 43);
            ForeColor = Color.White;
            Font = new Font("Yu Gothic UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            _messageLabel.Text = "追加したいキーまたはマウスボタンを押してください。\r\n押下イベントを追加し、解放イベントは60ms後に自動追加します。";
            _messageLabel.Dock = DockStyle.Top;
            _messageLabel.Height = 88;
            _messageLabel.TextAlign = ContentAlignment.MiddleCenter;
            _messageLabel.ForeColor = Color.FromArgb(230, 234, 240);
            Controls.Add(_messageLabel);

            _cancelButton.Text = "キャンセル";
            _cancelButton.Dock = DockStyle.Bottom;
            _cancelButton.Height = 34;
            _cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
            Controls.Add(_cancelButton);
        }

        public MacroEvent? CapturedEvent { get; private set; }
        public int ReleaseDelayMs => FixedReleaseDelayMs;

        public bool PreFilterMessage(ref Message m)
        {
            if (!IsDialogMessageTarget(m.HWnd))
            {
                return false;
            }

            var target = Control.FromHandle(m.HWnd);
            if (IsChildOf(target, _cancelButton))
            {
                return false;
            }

            switch (m.Msg)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                    CaptureMouseButton(RecordedMouseButton.Left);
                    return true;
                case NativeMethods.WM_RBUTTONDOWN:
                    CaptureMouseButton(RecordedMouseButton.Right);
                    return true;
                case NativeMethods.WM_MBUTTONDOWN:
                    CaptureMouseButton(RecordedMouseButton.Middle);
                    return true;
                case NativeMethods.WM_XBUTTONDOWN:
                    CaptureMouseButton(GetXButton(m.WParam));
                    return true;
                case NativeMethods.WM_MOUSEWHEEL:
                    CapturedEvent = new MacroEvent
                    {
                        Kind = MacroEventKind.MouseWheel,
                        WheelDelta = GetWheelDelta(m.WParam)
                    };
                    DialogResult = DialogResult.OK;
                    return true;
                default:
                    return false;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Application.AddMessageFilter(this);
            _messageFilterRegistered = true;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            if (_messageFilterRegistered)
            {
                Application.RemoveMessageFilter(this);
                _messageFilterRegistered = false;
            }

            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                base.OnKeyDown(e);
                return;
            }

            CapturedEvent = new MacroEvent
            {
                Kind = MacroEventKind.KeyDown,
                KeyCode = e.KeyCode
            };
            DialogResult = DialogResult.OK;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            CaptureMouseButton(e.Button switch
            {
                MouseButtons.Left => RecordedMouseButton.Left,
                MouseButtons.Right => RecordedMouseButton.Right,
                MouseButtons.Middle => RecordedMouseButton.Middle,
                MouseButtons.XButton1 => RecordedMouseButton.XButton1,
                MouseButtons.XButton2 => RecordedMouseButton.XButton2,
                _ => RecordedMouseButton.None
            });
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            CapturedEvent = new MacroEvent
            {
                Kind = MacroEventKind.MouseWheel,
                WheelDelta = e.Delta
            };
            DialogResult = DialogResult.OK;
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            var info = Marshal.PtrToStructure<NativeMethods.MouseHookStruct>(lParam);
            var point = new Point(info.Pt.X, info.Pt.Y);
            if (IsPointInFormNonClient(point)
                || IsPointInControl(_cancelButton, point))
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            switch (wParam.ToInt32())
            {
                case NativeMethods.WM_LBUTTONDOWN:
                    BeginCaptureMouseButton(RecordedMouseButton.Left);
                    return (IntPtr)1;
                case NativeMethods.WM_RBUTTONDOWN:
                    BeginCaptureMouseButton(RecordedMouseButton.Right);
                    return (IntPtr)1;
                case NativeMethods.WM_MBUTTONDOWN:
                    BeginCaptureMouseButton(RecordedMouseButton.Middle);
                    return (IntPtr)1;
                case NativeMethods.WM_XBUTTONDOWN:
                    BeginCaptureMouseButton(GetXButton(info.MouseData));
                    return (IntPtr)1;
                default:
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
        }

        private void BeginCaptureMouseButton(RecordedMouseButton button)
        {
            if (button == RecordedMouseButton.None || IsDisposed)
            {
                return;
            }

            BeginInvoke((Action)(() => CaptureMouseButton(button)));
        }

        private void CaptureMouseButton(RecordedMouseButton button)
        {
            if (button == RecordedMouseButton.None)
            {
                return;
            }

            CapturedEvent = new MacroEvent
            {
                Kind = MacroEventKind.MouseDown,
                Button = button
            };
            DialogResult = DialogResult.OK;
        }

        private bool IsPointInFormNonClient(Point screenPoint)
        {
            if (!Bounds.Contains(screenPoint))
            {
                return false;
            }

            return !ClientRectangle.Contains(PointToClient(screenPoint));
        }

        private static bool IsPointInControl(Control control, Point screenPoint)
        {
            if (!control.Visible || !control.IsHandleCreated)
            {
                return false;
            }

            var clientPoint = control.PointToClient(screenPoint);
            return control.ClientRectangle.Contains(clientPoint);
        }

        private bool IsDialogMessageTarget(IntPtr handle)
        {
            var control = Control.FromHandle(handle);
            while (control is not null)
            {
                if (ReferenceEquals(control, this))
                {
                    return true;
                }

                control = control.Parent;
            }

            return false;
        }

        private static bool IsChildOf(Control? control, Control parent)
        {
            while (control is not null)
            {
                if (ReferenceEquals(control, parent))
                {
                    return true;
                }

                control = control.Parent;
            }

            return false;
        }

        private static int GetWheelDelta(IntPtr wParam)
        {
            return unchecked((short)(((long)wParam >> 16) & 0xFFFF));
        }

        private static RecordedMouseButton GetXButton(IntPtr wParam)
        {
            var xButton = unchecked((short)(((long)wParam >> 16) & 0xFFFF));
            return xButton switch
            {
                1 => RecordedMouseButton.XButton1,
                2 => RecordedMouseButton.XButton2,
                _ => RecordedMouseButton.None
            };
        }

        private static RecordedMouseButton GetXButton(int mouseData)
        {
            var xButton = unchecked((short)((mouseData >> 16) & 0xFFFF));
            return xButton switch
            {
                1 => RecordedMouseButton.XButton1,
                2 => RecordedMouseButton.XButton2,
                _ => RecordedMouseButton.None
            };
        }
    }
    private sealed class TrimRangeControl : SKControl
    {
        private const int HandleWidth = 12;
        private const int BarHeight = 14;
        private DragMode _dragMode = DragMode.None;
        private long _dragStartMs;
        private long _dragEndMs;
        private int _dragStartX;
        private bool _suppressRangeChanged;

        public TrimRangeControl()
        {
            BackColor = Color.FromArgb(34, 37, 43);
            Cursor = Cursors.Hand;
            MinimumSize = new Size(220, 42);
        }

        public event EventHandler? RangeChanged;
        public event EventHandler? RangeChangeCompleted;

        public long StartMs { get; private set; }
        public long EndMs { get; private set; } = 1;
        public long DurationMs { get; private set; } = 1;
        public bool IsDragging => _dragMode != DragMode.None;

        public bool SetRange(long startMs, long endMs, long durationMs)
        {
            durationMs = Math.Max(1, durationMs);
            startMs = Math.Clamp(startMs, 0, Math.Max(0, durationMs - 1));
            endMs = Math.Clamp(endMs, startMs + 1, durationMs);
            var changed = StartMs != startMs || EndMs != endMs || DurationMs != durationMs;
            StartMs = startMs;
            EndMs = endMs;
            DurationMs = durationMs;
            if (!changed)
            {
                return false;
            }

            Invalidate();
            if (changed && !_suppressRangeChanged)
            {
                RangeChanged?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var bar = GetBarBounds();
            var startX = TimeToX(StartMs, bar);
            var endX = TimeToX(EndMs, bar);
            _dragMode = Math.Abs(e.X - startX) <= HandleWidth
                ? DragMode.Start
                : Math.Abs(e.X - endX) <= HandleWidth
                    ? DragMode.End
                    : e.X > startX && e.X < endX
                        ? DragMode.Range
                        : e.X < startX
                            ? DragMode.Start
                            : DragMode.End;
            _dragStartX = e.X;
            _dragStartMs = StartMs;
            _dragEndMs = EndMs;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragMode == DragMode.None)
            {
                UpdateCursor(e.Location);
                return;
            }

            var bar = GetBarBounds();
            var time = XToTime(e.X, bar);
            var changed = false;
            _suppressRangeChanged = true;
            try
            {
                switch (_dragMode)
                {
                    case DragMode.Start:
                        changed = SetRange(Math.Min(time, EndMs - 1), EndMs, DurationMs);
                        break;
                    case DragMode.End:
                        changed = SetRange(StartMs, Math.Max(time, StartMs + 1), DurationMs);
                        break;
                    case DragMode.Range:
                        var delta = XToTime(_dragStartX + (e.X - _dragStartX), bar) - XToTime(_dragStartX, bar);
                        var length = _dragEndMs - _dragStartMs;
                        var start = Math.Clamp(_dragStartMs + delta, 0, Math.Max(0, DurationMs - length));
                        changed = SetRange(start, start + length, DurationMs);
                        break;
                }
            }
            finally
            {
                _suppressRangeChanged = false;
            }

            if (changed)
            {
                RangeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragMode = DragMode.None;
            Capture = false;
            UpdateCursor(e.Location);
            RangeChangeCompleted?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            base.OnPaintSurface(e);
            var canvas = e.Surface.Canvas;
            canvas.Clear(ToSKColor(BackColor));
            var bar = GetBarBounds();
            var startX = TimeToX(StartMs, bar);
            var endX = TimeToX(EndMs, bar);
            var range = Rectangle.FromLTRB(startX, bar.Top, Math.Max(startX + 1, endX), bar.Bottom);

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            fill.Color = new SKColor(10, 12, 16, 75);
            canvas.DrawRect(ToSKRect(bar), fill);
            fill.Color = new SKColor(222, 226, 232, 75);
            canvas.DrawRect(ToSKRect(bar), fill);
            fill.Color = new SKColor(95, 220, 255, 210);
            canvas.DrawRect(ToSKRect(range), fill);
            stroke.Color = new SKColor(82, 88, 98);
            canvas.DrawRect(ToSKRect(bar), stroke);
            fill.Color = new SKColor(235, 235, 240, 245);
            DrawHandle(canvas, startX, bar, fill);
            DrawHandle(canvas, endX, bar, fill);

            var text = $"トリム: {StartMs:N0} ms - {EndMs:N0} ms";
            DrawSkText(canvas, text, bar.Left, 16, 12, new SKColor(220, 224, 230));
        }

        private void UpdateCursor(Point location)
        {
            var bar = GetBarBounds();
            var startX = TimeToX(StartMs, bar);
            var endX = TimeToX(EndMs, bar);
            Cursor = Math.Abs(location.X - startX) <= HandleWidth || Math.Abs(location.X - endX) <= HandleWidth
                ? Cursors.SizeWE
                : location.X > startX && location.X < endX
                    ? Cursors.SizeAll
                    : Cursors.Hand;
        }

        private Rectangle GetBarBounds()
        {
            const int leftInset = 112;
            const int rightInset = 10;
            return new Rectangle(leftInset, Height - BarHeight - 8, Math.Max(1, Width - leftInset - rightInset), BarHeight);
        }

        private int TimeToX(long timeMs, Rectangle bar)
        {
            var progress = Math.Clamp(timeMs / (double)Math.Max(1, DurationMs), 0.0, 1.0);
            return bar.Left + (int)Math.Round(progress * Math.Max(1, bar.Width - 1));
        }

        private long XToTime(int x, Rectangle bar)
        {
            var progress = Math.Clamp((x - bar.Left) / (double)Math.Max(1, bar.Width - 1), 0.0, 1.0);
            return (long)Math.Round(progress * DurationMs);
        }

        private static void DrawHandle(SKCanvas canvas, int x, Rectangle bar, SKPaint paint)
        {
            using var path = new SKPath();
            path.MoveTo(x, bar.Top - 6);
            path.LineTo(x - 6, bar.Top);
            path.LineTo(x - 6, bar.Bottom);
            path.LineTo(x + 6, bar.Bottom);
            path.LineTo(x + 6, bar.Top);
            path.Close();
            canvas.DrawPath(path, paint);
        }

        private static SKRect ToSKRect(Rectangle rect)
        {
            return new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        private static SKColor ToSKColor(Color color)
        {
            return new SKColor(color.R, color.G, color.B, color.A);
        }

        private static void DrawSkText(SKCanvas canvas, string text, float x, float baseline, float size, SKColor color)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Yu Gothic UI"), size);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color
            };
            canvas.DrawText(text, x, baseline, font, paint);
        }

        private enum DragMode
        {
            None,
            Start,
            End,
            Range
        }
    }

    private sealed class TimelineScrollHostPanel : Panel
    {
        public event Action<MouseEventArgs>? WheelRequested;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            WheelRequested?.Invoke(e);
        }
    }

    private readonly record struct TimelineRowHeader(string Label, int Y, int Height, SKColor Color);

    private sealed class TimelineLabelControl : SKControl
    {
        public const int LabelColumnWidth = 112;

        private IReadOnlyList<TimelineRowHeader> _rows = Array.Empty<TimelineRowHeader>();
        private int _verticalOffset;
        private int _contentHeight;

        public TimelineLabelControl()
        {
            BackColor = Color.FromArgb(18, 20, 24);
        }

        public void SetRows(IReadOnlyList<TimelineRowHeader> rows)
        {
            _rows = rows;
            Invalidate();
        }

        public void SetVerticalOffset(int offset)
        {
            if (_verticalOffset == offset)
            {
                return;
            }

            _verticalOffset = offset;
            Invalidate();
        }

        public void SetContentHeight(int height)
        {
            if (_contentHeight == height)
            {
                return;
            }

            _contentHeight = height;
            Invalidate();
        }

        protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            base.OnPaintSurface(e);
            var canvas = e.Surface.Canvas;
            canvas.Clear(new SKColor(18, 20, 24));

            using var rowFill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(20, 23, 28) };
            using var alternateFill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(17, 20, 24) };
            using var border = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(52, 58, 66) };
            using var divider = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(58, 62, 70) };

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var y = row.Y - _verticalOffset;
                if (y > Height || y + row.Height < 0)
                {
                    continue;
                }

                var rect = new SKRect(0, y, Width, y + row.Height);
                canvas.DrawRect(rect, i % 2 == 0 ? rowFill : alternateFill);
                canvas.DrawRect(rect, border);
                DrawSkText(canvas, row.Label, 12, y + row.Height / 2F + 4, 12, row.Color);
            }

            canvas.DrawLine(Width - 1, 0, Width - 1, Math.Max(Height, _contentHeight - _verticalOffset), divider);
        }

        private static void DrawSkText(SKCanvas canvas, string text, float x, float baseline, float size, SKColor color)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Yu Gothic UI"), size);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color
            };
            canvas.DrawText(text, x, baseline, font, paint);
        }
    }

    private sealed class MacroTimelineControl : SKControl
    {
        private const int RulerHeight = 26;
        private const int BaseLaneHeight = 24;
        private const int BaseRowPaddingY = 6;
        private const int RowGap = 4;
        private const int BaseClipHeight = 15;
        private const int MinClipWidth = 12;
        private const int PointWidth = 10;
        private const int MinContentHeight = 150;
        private const int DragStartThresholdPixels = 4;

        private readonly List<TimelineHit> _hits = new();
        private readonly System.Windows.Forms.Timer _dragFrameTimer = new();
        private TimelineDrag? _drag;
        private TimelineLayout? _layout;
        private List<MacroEvent> _events = new();
        private SKBitmap? _baseBitmap;
        private Size _baseBitmapSize;
        private bool _baseCacheDirty = true;
        private long _eventSignature;
        private long _startMs;
        private long _endMs;
        private long _durationMs = 1;
        private long _currentTimeMs;
        private int? _selectedIndex;
        private int _contentHeight = MinContentHeight;
        private double _verticalScale = 1.0;
        private bool _isPanning;
        private Point _lastPanPoint;
        private Point _viewportOffset;
        private Size _viewportSize;
        private int _pendingDragX;
        private bool _hasPendingDragFrame;

        public MacroTimelineControl()
        {
            BackColor = Color.FromArgb(18, 20, 24);
            Cursor = Cursors.Hand;
            TabStop = true;
            _dragFrameTimer.Interval = GetRefreshIntervalMs(this);
            _dragFrameTimer.Tick += (_, _) => ProcessPendingDragFrame();
        }

        public event Action<int>? EventSelected;
        public event Action<long>? CurrentTimeSelected;
        public event Func<TimelineEditRequest, TimelineEditPreview?>? EventTimeEditPreview;
        public event Action<TimelineEditRequest>? EventTimeEdited;
        public event Action? EventTimeEditCompleted;
        public event Action<int>? ContentHeightChanged;
        public event Action<IReadOnlyList<TimelineRowHeader>>? RowHeadersChanged;
        public event Action<Point>? PanRequested;
        public event Action<double, int>? HorizontalZoomRequested;
        public event Action<double, int>? VerticalZoomRequested;

        public int ContentHeight => _contentHeight;
        public double VerticalScale => _verticalScale;
        public int CurrentTimeContentX => TimeToX(_currentTimeMs, GetPlotBounds());

        private int LaneHeight => Math.Max(14, (int)Math.Round(BaseLaneHeight * _verticalScale));
        private int RowPaddingY => Math.Max(3, (int)Math.Round(BaseRowPaddingY * _verticalScale));
        private int ClipHeight => Math.Max(8, (int)Math.Round(BaseClipHeight * _verticalScale));

        public void SetVerticalScale(double scale)
        {
            var clamped = Math.Clamp(scale, 0.55, 2.8);
            if (Math.Abs(_verticalScale - clamped) < 0.001)
            {
                return;
            }

            _verticalScale = clamped;
            _layout = null;
            _baseCacheDirty = true;
            if (Width > 0 && Height > 0)
            {
                EnsureLayout(GetPlotBounds());
            }

            Invalidate();
        }

        public void SetViewport(Point offset, Size size)
        {
            if (_viewportOffset == offset && _viewportSize == size)
            {
                return;
            }

            _viewportOffset = offset;
            _viewportSize = size;
            Invalidate();
        }

        public void SetSelection(int? selectedIndex)
        {
            _selectedIndex = selectedIndex;
            _baseCacheDirty = true;
            Invalidate();
        }

        public void SetData(List<MacroEvent> events, long startMs, long endMs, long durationMs, int? selectedIndex, long currentTimeMs)
        {
            var signature = CreateEventSignature(events);
            if (!ReferenceEquals(_events, events) || _durationMs != Math.Max(1, durationMs) || _eventSignature != signature)
            {
                _layout = null;
                _baseCacheDirty = true;
                _eventSignature = signature;
            }

            _events = events;
            _startMs = startMs;
            _endMs = endMs;
            _durationMs = Math.Max(1, durationMs);
            _selectedIndex = selectedIndex;
            _currentTimeMs = Math.Clamp(currentTimeMs, 0, _durationMs);
            Invalidate();
        }

        public void SetCurrentTime(long currentTimeMs)
        {
            _currentTimeMs = Math.Clamp(currentTimeMs, 0, _durationMs);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _layout = null;
            _baseCacheDirty = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle)
            {
                _isPanning = true;
                _lastPanPoint = MousePosition;
                Cursor = Cursors.SizeAll;
                Capture = true;
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            EnsureLayout(GetPlotBounds());
            var hit = _hits
                .OrderBy(item => DistanceSquared(item.Bounds, e.Location))
                .ThenBy(item => GetHitPriority(item.EditKind))
                .FirstOrDefault(item =>
                {
                    var bounds = item.Bounds;
                    bounds.Inflate(8, 8);
                    return bounds.Contains(e.Location);
                });
            if (hit is not null)
            {
                EventSelected?.Invoke(hit.EventIndex);
                if (hit.EditKind != TimelineEditKind.None)
                {
                    _drag = new TimelineDrag(
                        hit,
                        _events[hit.StartIndex],
                        _events[hit.EndIndex],
                        XToTime(e.X, GetPlotBounds()),
                        _events[hit.StartIndex].TimeOffsetMs,
                        _events[hit.EndIndex].TimeOffsetMs);
                    _drag.StartMousePoint = e.Location;
                    _dragFrameTimer.Interval = GetRefreshIntervalMs(this);
                    Capture = true;
                }
            }
            else
            {
                CurrentTimeSelected?.Invoke(XToTime(e.X, GetPlotBounds()));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isPanning)
            {
                var currentPoint = MousePosition;
                var panDelta = new Point(currentPoint.X - _lastPanPoint.X, currentPoint.Y - _lastPanPoint.Y);
                _lastPanPoint = currentPoint;
                if (panDelta != Point.Empty)
                {
                    PanRequested?.Invoke(panDelta);
                }

                return;
            }

            if (_drag is null)
            {
                return;
            }

            if (!_drag.HasMoved)
            {
                var dx = e.X - _drag.StartMousePoint.X;
                var dy = e.Y - _drag.StartMousePoint.Y;
                if ((dx * dx) + (dy * dy) < DragStartThresholdPixels * DragStartThresholdPixels)
                {
                    return;
                }

                _drag.HasMoved = true;
            }

            QueueDragFrame(e.X);
        }

        private void QueueDragFrame(int mouseX)
        {
            _pendingDragX = mouseX;
            _hasPendingDragFrame = true;
            if (!_dragFrameTimer.Enabled)
            {
                _dragFrameTimer.Start();
            }
        }

        private void ProcessPendingDragFrame()
        {
            if (!_hasPendingDragFrame || _drag is null)
            {
                _dragFrameTimer.Stop();
                return;
            }

            _hasPendingDragFrame = false;
            var plot = GetPlotBounds();
            var time = XToTime(_pendingDragX, plot);
            var delta = time - _drag.StartMouseTimeMs;
            var startMs = _drag.StartMs;
            var endMs = _drag.EndMs;
            switch (_drag.Hit.EditKind)
            {
                case TimelineEditKind.Start:
                    startMs = Math.Min(time, endMs - 1);
                    break;
                case TimelineEditKind.End:
                    endMs = Math.Max(time, startMs + 1);
                    break;
                case TimelineEditKind.Range:
                    startMs = Math.Max(0, _drag.StartMs + delta);
                    endMs = Math.Max(startMs + 1, _drag.EndMs + delta);
                    break;
            }

            var preview = EventTimeEditPreview?.Invoke(new TimelineEditRequest(_drag.StartEvent, _drag.EndEvent, startMs, endMs));
            if (preview is null)
            {
                return;
            }

            if (_drag.PreviewStartMs == preview.StartMs && _drag.PreviewEndMs == preview.EndMs)
            {
                return;
            }

            _drag.PreviewStartMs = preview.StartMs;
            _drag.PreviewEndMs = preview.EndMs;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle && _isPanning)
            {
                _isPanning = false;
                Cursor = Cursors.Hand;
                Capture = false;
                return;
            }

            var drag = _drag;
            if (drag is not null && drag.HasMoved)
            {
                ProcessPendingDragFrame();
                drag = _drag;
            }

            _dragFrameTimer.Stop();
            _hasPendingDragFrame = false;
            _drag = null;
            Capture = false;
            if (drag is not null)
            {
                if (drag.HasMoved)
                {
                    EventTimeEdited?.Invoke(new TimelineEditRequest(
                        drag.StartEvent,
                        drag.EndEvent,
                        drag.PreviewStartMs,
                        drag.PreviewEndMs));
                }
            }

            EventTimeEditCompleted?.Invoke();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            HandleTimelineWheel(e.Delta, e.Location, ModifierKeys);
        }

        public void HandleTimelineWheel(int delta, Point location, Keys modifierKeys)
        {
            if (delta == 0)
            {
                return;
            }

            Focus();
            var factor = delta > 0 ? 1.12 : 1.0 / 1.12;
            if ((modifierKeys & Keys.Control) == Keys.Control)
            {
                VerticalZoomRequested?.Invoke(factor, location.Y);
            }
            else
            {
                HorizontalZoomRequested?.Invoke(factor, location.X);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();
        }

        protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            base.OnPaintSurface(e);
            var canvas = e.Surface.Canvas;
            var plot = GetPlotBounds();
            if (plot.Width <= 10 || plot.Height <= 10)
            {
                return;
            }

            EnsureBaseBitmap(plot);
            if (_baseBitmap is not null)
            {
                canvas.DrawBitmap(_baseBitmap, 0, 0);
            }
            else
            {
                canvas.Clear(ToSKColor(BackColor));
            }

            DrawTrimRange(canvas, plot);
            DrawDraggingPreview(canvas, plot);
            DrawCurrentTime(canvas, plot);
            DrawSelectedPlayhead(canvas, plot);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseBitmap?.Dispose();
                _dragFrameTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnsureBaseBitmap(Rectangle plot)
        {
            EnsureLayout(plot);
            if (!_baseCacheDirty && _baseBitmap is not null && _baseBitmapSize == ClientSize)
            {
                return;
            }

            _baseBitmap?.Dispose();
            _baseBitmap = null;
            _baseBitmapSize = ClientSize;
            if (Width <= 0 || Height <= 0 || _layout is null)
            {
                return;
            }

            _baseBitmap = new SKBitmap(Width, Height);
            using var canvas = new SKCanvas(_baseBitmap);
            canvas.Clear(ToSKColor(BackColor));
            DrawRuler(canvas, plot);
            DrawRows(canvas, _layout);
            DrawItems(canvas, _layout);
            _baseCacheDirty = false;
        }

        private void EnsureLayout(Rectangle plot)
        {
            if (_layout is not null)
            {
                return;
            }

            _hits.Clear();
            var layout = BuildLayout(plot);
            _layout = layout;
            RowHeadersChanged?.Invoke(layout.Rows
                .Select(row => new TimelineRowHeader(row.Label, row.Y, row.Height, GetRowColor(row.Kind, 235)))
                .ToList());
            if (_contentHeight != layout.ContentHeight)
            {
                _contentHeight = layout.ContentHeight;
                ContentHeightChanged?.Invoke(_contentHeight);
            }
        }

        private TimelineLayout BuildLayout(Rectangle plot)
        {
            var rows = BuildRows();
            var y = plot.Top + RulerHeight + RowGap;
            foreach (var row in rows)
            {
                AssignSingleLane(row);
                row.Y = y;
                row.Height = RowPaddingY * 2 + LaneHeight;
                BuildItemGeometry(row, plot);
                y += row.Height + RowGap;
            }

            var contentHeight = Math.Max(MinContentHeight, y + 8);
            return new TimelineLayout(rows, contentHeight);
        }

        private List<TimelineRow> BuildRows()
        {
            var rows = new Dictionary<string, TimelineRow>();
            var open = new Dictionary<string, (MacroEvent Event, int Index)>();
            foreach (var item in _events.Select((macroEvent, index) => new { macroEvent, index }))
            {
                if (!IsEventMarker(item.macroEvent))
                {
                    continue;
                }

                var row = GetOrCreateRow(rows, item.macroEvent);
                var inputKey = GetPairKey(item.macroEvent);
                if (item.macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown)
                {
                    if (open.TryGetValue(inputKey, out var previous))
                    {
                        GetOrCreateRow(rows, previous.Event).Items.Add(TimelineItem.Point(previous.Event, previous.Index));
                    }

                    open[inputKey] = (item.macroEvent, item.index);
                }
                else if (item.macroEvent.Kind is MacroEventKind.MouseUp or MacroEventKind.KeyUp)
                {
                    if (open.TryGetValue(inputKey, out var start))
                    {
                        row.Items.Add(TimelineItem.Clip(start.Event, item.macroEvent, start.Index, item.index));
                        open.Remove(inputKey);
                    }
                    else
                    {
                        row.Items.Add(TimelineItem.Point(item.macroEvent, item.index));
                    }
                }
                else
                {
                    row.Items.Add(TimelineItem.Point(item.macroEvent, item.index));
                }
            }

            foreach (var item in open.Values)
            {
                GetOrCreateRow(rows, item.Event).Items.Add(TimelineItem.Point(item.Event, item.Index));
            }

            return rows.Values
                .OrderBy(item => item.SortGroup)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static TimelineRow GetOrCreateRow(Dictionary<string, TimelineRow> rows, MacroEvent macroEvent)
        {
            var key = GetRowKey(macroEvent);
            if (rows.TryGetValue(key, out var row))
            {
                return row;
            }

            row = new TimelineRow(
                key,
                GetRowLabel(macroEvent),
                GetRowKind(macroEvent),
                GetRowSortGroup(macroEvent),
                GetRowSortOrder(macroEvent));
            rows.Add(key, row);
            return row;
        }

        private static void AssignSingleLane(TimelineRow row)
        {
            foreach (var item in row.Items)
            {
                item.Lane = 0;
            }

            row.LaneCount = 1;
        }

        private void BuildItemGeometry(TimelineRow row, Rectangle plot)
        {
            foreach (var item in row.Items)
            {
                var laneTop = row.Y + RowPaddingY + item.Lane * LaneHeight;
                var centerY = laneTop + LaneHeight / 2;
                var x1 = TimeToX(item.StartMs, plot);
                if (item.IsClip)
                {
                    var x2 = TimeToX(item.EndMs, plot);
                    var right = Math.Max(x2, x1 + MinClipWidth);
                    item.Bounds = Rectangle.FromLTRB(x1, centerY - ClipHeight / 2, right, centerY + ClipHeight / 2);
                    item.StartHandle = new Rectangle(item.Bounds.Left - 5, item.Bounds.Top - 4, 10, item.Bounds.Height + 8);
                    item.EndHandle = new Rectangle(item.Bounds.Right - 5, item.Bounds.Top - 4, 10, item.Bounds.Height + 8);
                }
                else
                {
                    item.Bounds = new Rectangle(x1 - PointWidth / 2, centerY - PointWidth / 2, PointWidth, PointWidth);
                    item.StartHandle = item.Bounds;
                    item.EndHandle = item.Bounds;
                }

                item.HitBounds = item.Bounds;
                item.HitBounds.Inflate(item.IsClip ? 0 : 8, item.IsClip ? 7 : 8);
                _hits.Add(new TimelineHit(item.StartIndex, item.StartIndex, item.EndIndex, item.HitBounds, TimelineEditKind.Range, row.Key, item.Lane));
                if (item.IsClip)
                {
                    _hits.Add(new TimelineHit(item.StartIndex, item.StartIndex, item.EndIndex, item.StartHandle, TimelineEditKind.Start, row.Key, item.Lane));
                    _hits.Add(new TimelineHit(item.EndIndex, item.StartIndex, item.EndIndex, item.EndHandle, TimelineEditKind.End, row.Key, item.Lane));
                }
            }
        }

        private Rectangle GetPlotBounds()
        {
            return new Rectangle(0, 4, Math.Max(1, Width - 10), Math.Max(1, Math.Max(Height, _contentHeight) - 12));
        }

        private void DrawRuler(SKCanvas canvas, Rectangle plot)
        {
            using var linePaint = new SKPaint { IsAntialias = true, Color = new SKColor(58, 62, 70), StrokeWidth = 1 };
            var ruler = new Rectangle(plot.Left, plot.Top, plot.Width, RulerHeight);
            canvas.DrawLine(ruler.Left, ruler.Bottom - 1, ruler.Right, ruler.Bottom - 1, linePaint);
            var tickCount = Math.Clamp(plot.Width / 150, 3, 10);
            for (var i = 0; i <= tickCount; i++)
            {
                var time = _durationMs * i / tickCount;
                var x = TimeToX(time, plot);
                canvas.DrawLine(x, ruler.Bottom - 8, x, ruler.Bottom, linePaint);
                var label = $"{time:N0} ms";
                var labelX = i == tickCount ? Math.Max(plot.Left, x - 86) : x + 4;
                DrawSkText(canvas, label, labelX, ruler.Top + 15, 11, new SKColor(205, 210, 218));
            }
        }

        private void DrawRows(SKCanvas canvas, TimelineLayout layout)
        {
            using var rowFill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(25, 28, 33) };
            using var alternateFill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(22, 25, 30) };
            using var border = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(44, 48, 55) };
            for (var i = 0; i < layout.Rows.Count; i++)
            {
                var row = layout.Rows[i];
                var rect = new Rectangle(0, row.Y, Width, row.Height);
                canvas.DrawRect(ToSKRect(rect), i % 2 == 0 ? rowFill : alternateFill);
                canvas.DrawRect(ToSKRect(rect), border);
            }
        }

        private void DrawItems(SKCanvas canvas, TimelineLayout layout)
        {
            foreach (var row in layout.Rows)
            {
                foreach (var item in row.Items)
                {
                    DrawItem(canvas, row, item);
                }
            }
        }

        private void DrawItem(SKCanvas canvas, TimelineRow row, TimelineItem item)
        {
            var selected = _selectedIndex == item.StartIndex || _selectedIndex == item.EndIndex;
            var color = GetRowColor(row.Kind, selected ? (byte)245 : (byte)205);
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
            using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 2 : 1, Color = selected ? new SKColor(245, 250, 255) : GetRowColor(row.Kind, 230) };
            if (item.IsClip)
            {
                canvas.DrawRoundRect(ToSKRect(item.Bounds), 4, 4, fill);
                canvas.DrawRoundRect(ToSKRect(item.Bounds), 4, 4, outline);
                using var handle = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(255, 215, 0, selected ? (byte)255 : (byte)220) };
                canvas.DrawCircle(item.Bounds.Left, item.Bounds.Top + item.Bounds.Height / 2F, 3, handle);
                canvas.DrawCircle(item.Bounds.Right, item.Bounds.Top + item.Bounds.Height / 2F, 3, handle);
            }
            else
            {
                canvas.DrawOval(ToSKRect(item.Bounds), fill);
                canvas.DrawOval(ToSKRect(item.Bounds), outline);
            }
        }

        private void DrawTrimRange(SKCanvas canvas, Rectangle plot)
        {
            var left = TimeToX(_startMs, plot);
            var right = TimeToX(_endMs, plot);
            var trimRect = Rectangle.FromLTRB(left, plot.Top + RulerHeight, Math.Max(left + 1, right), plot.Bottom);
            using var fillPaint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(235, 70, 72, 34) };
            using var linePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(235, 70, 72, 230) };
            canvas.DrawRect(ToSKRect(trimRect), fillPaint);
            canvas.DrawLine(left, plot.Top + RulerHeight, left, plot.Bottom, linePaint);
            canvas.DrawLine(right, plot.Top + RulerHeight, right, plot.Bottom, linePaint);
        }

        private void DrawSelectedPlayhead(SKCanvas canvas, Rectangle plot)
        {
            if (_selectedIndex is null || _selectedIndex < 0 || _selectedIndex >= _events.Count)
            {
                return;
            }

            var selected = _events[_selectedIndex.Value];
            var x = TimeToX(selected.TimeOffsetMs, plot);
            using var linePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(95, 220, 255) };
            canvas.DrawLine(x, plot.Top, x, plot.Bottom, linePaint);
            DrawSkText(canvas, $"{selected.TimeOffsetMs:N0} ms", Math.Min(x + 6, plot.Right - 90), plot.Top + 15, 11, new SKColor(95, 220, 255));
        }

        private void DrawCurrentTime(SKCanvas canvas, Rectangle plot)
        {
            var x = TimeToX(_currentTimeMs, plot);
            using var linePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5F, Color = new SKColor(255, 255, 255, 230) };
            canvas.DrawLine(x, plot.Top, x, plot.Bottom, linePaint);
        }

        private void DrawDraggingPreview(SKCanvas canvas, Rectangle plot)
        {
            if (_drag is null || _layout is null)
            {
                return;
            }

            var row = _layout.Rows.FirstOrDefault(item => item.Key == _drag.Hit.RowKey);
            if (row is null)
            {
                return;
            }

            var laneTop = row.Y + RowPaddingY + _drag.Hit.Lane * LaneHeight;
            var centerY = laneTop + LaneHeight / 2;
            using var markerPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(255, 245, 120, 255) };
            if (_drag.Hit.StartIndex == _drag.Hit.EndIndex)
            {
                DrawPreviewMarker(canvas, TimeToX(_drag.PreviewStartMs, plot), centerY, markerPaint, 13);
                return;
            }

            var startX = TimeToX(_drag.PreviewStartMs, plot);
            var endX = TimeToX(_drag.PreviewEndMs, plot);
            var bar = Rectangle.FromLTRB(Math.Min(startX, endX), centerY - ClipHeight / 2, Math.Max(startX, endX) + 1, centerY + ClipHeight / 2);
            if (bar.Width < MinClipWidth)
            {
                bar.Width = MinClipWidth;
            }

            using var barPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(95, 220, 255, 245) };
            canvas.DrawRoundRect(ToSKRect(bar), 5, 5, barPaint);
            DrawPreviewMarker(canvas, bar.Left, centerY, markerPaint, 12);
            DrawPreviewMarker(canvas, bar.Right, centerY, markerPaint, 12);
        }

        private static void DrawPreviewMarker(SKCanvas canvas, int x, int centerY, SKPaint paint, int size)
        {
            var rect = new Rectangle(x - size / 2, centerY - size / 2, size, size);
            canvas.DrawOval(ToSKRect(rect), paint);
        }

        private int TimeToX(long timeMs, Rectangle plot)
        {
            var progress = Math.Clamp(timeMs / (double)Math.Max(1, _durationMs), 0.0, 1.0);
            return plot.Left + (int)Math.Round(progress * Math.Max(1, plot.Width - 1));
        }

        private long XToTime(int x, Rectangle plot)
        {
            var progress = Math.Clamp((x - plot.Left) / (double)Math.Max(1, plot.Width - 1), 0.0, 1.0);
            return (long)Math.Round(progress * _durationMs);
        }

        private static string GetPairKey(MacroEvent macroEvent)
        {
            return macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp
                ? $"mouse:{macroEvent.Button}"
                : $"key:{macroEvent.KeyCode}";
        }

        private static string GetRowKey(MacroEvent macroEvent)
        {
            return macroEvent.Kind switch
            {
                MacroEventKind.MouseDown or MacroEventKind.MouseUp => $"mouse:{macroEvent.Button}",
                MacroEventKind.KeyDown or MacroEventKind.KeyUp => $"key:{macroEvent.KeyCode}",
                MacroEventKind.MouseWheel => "wheel",
                _ => "other"
            };
        }

        private static string GetRowLabel(MacroEvent macroEvent)
        {
            return macroEvent.Kind switch
            {
                MacroEventKind.MouseDown or MacroEventKind.MouseUp => $"Mouse {macroEvent.Button}",
                MacroEventKind.KeyDown or MacroEventKind.KeyUp => $"Key {macroEvent.KeyCode}",
                MacroEventKind.MouseWheel => "Wheel",
                _ => "Other"
            };
        }

        private static TimelineRowKind GetRowKind(MacroEvent macroEvent)
        {
            return macroEvent.Kind switch
            {
                MacroEventKind.MouseDown or MacroEventKind.MouseUp => TimelineRowKind.Mouse,
                MacroEventKind.KeyDown or MacroEventKind.KeyUp => TimelineRowKind.Key,
                MacroEventKind.MouseWheel => TimelineRowKind.Wheel,
                _ => TimelineRowKind.Other
            };
        }

        private static int GetRowSortGroup(MacroEvent macroEvent)
        {
            return macroEvent.Kind switch
            {
                MacroEventKind.MouseDown or MacroEventKind.MouseUp => 0,
                MacroEventKind.KeyDown or MacroEventKind.KeyUp => 1,
                MacroEventKind.MouseWheel => 2,
                _ => 3
            };
        }

        private static int GetRowSortOrder(MacroEvent macroEvent)
        {
            if (macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp)
            {
                return macroEvent.Button switch
                {
                    RecordedMouseButton.Left => 0,
                    RecordedMouseButton.Right => 1,
                    RecordedMouseButton.Middle => 2,
                    RecordedMouseButton.XButton1 => 3,
                    RecordedMouseButton.XButton2 => 4,
                    _ => 9
                };
            }

            return macroEvent.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? (int)macroEvent.KeyCode : 0;
        }

        private static SKColor GetRowColor(TimelineRowKind kind, byte alpha)
        {
            return kind switch
            {
                TimelineRowKind.Mouse => new SKColor(95, 220, 255, alpha),
                TimelineRowKind.Key => new SKColor(255, 210, 92, alpha),
                TimelineRowKind.Wheel => new SKColor(255, 190, 140, alpha),
                _ => new SKColor(220, 224, 230, alpha)
            };
        }

        private static int DistanceSquared(Rectangle rect, Point point)
        {
            var cx = rect.Left + rect.Width / 2;
            var cy = rect.Top + rect.Height / 2;
            var dx = cx - point.X;
            var dy = cy - point.Y;
            return dx * dx + dy * dy;
        }

        private static int GetHitPriority(TimelineEditKind kind)
        {
            return kind switch
            {
                TimelineEditKind.Start or TimelineEditKind.End => 0,
                TimelineEditKind.Range => 1,
                _ => 2
            };
        }

        private static long CreateEventSignature(List<MacroEvent> events)
        {
            var hash = new HashCode();
            hash.Add(events.Count);
            foreach (var macroEvent in events.Where(IsEventMarker))
            {
                hash.Add((int)macroEvent.Kind);
                hash.Add(macroEvent.TimeOffsetMs);
                hash.Add(macroEvent.Button);
                hash.Add(macroEvent.KeyCode);
                hash.Add(macroEvent.WheelDelta);
            }

            return hash.ToHashCode();
        }

        private static SKRect ToSKRect(Rectangle rect)
        {
            return new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        private static SKColor ToSKColor(Color color)
        {
            return new SKColor(color.R, color.G, color.B, color.A);
        }

        private static void DrawSkText(SKCanvas canvas, string text, float x, float baseline, float size, SKColor color)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Yu Gothic UI"), size);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = color
            };
            canvas.DrawText(text, x, baseline, font, paint);
        }

        private sealed record TimelineLayout(List<TimelineRow> Rows, int ContentHeight);

        private sealed class TimelineRow
        {
            public TimelineRow(string key, string label, TimelineRowKind kind, int sortGroup, int sortOrder)
            {
                Key = key;
                Label = label;
                Kind = kind;
                SortGroup = sortGroup;
                SortOrder = sortOrder;
            }

            public string Key { get; }
            public string Label { get; }
            public TimelineRowKind Kind { get; }
            public int SortGroup { get; }
            public int SortOrder { get; }
            public List<TimelineItem> Items { get; } = new();
            public int Y { get; set; }
            public int Height { get; set; }
            public int LaneCount { get; set; } = 1;
        }

        private sealed class TimelineItem
        {
            private TimelineItem(MacroEvent startEvent, MacroEvent endEvent, int startIndex, int endIndex, bool isClip)
            {
                StartEvent = startEvent;
                EndEvent = endEvent;
                StartIndex = startIndex;
                EndIndex = endIndex;
                IsClip = isClip;
            }

            public MacroEvent StartEvent { get; }
            public MacroEvent EndEvent { get; }
            public int StartIndex { get; }
            public int EndIndex { get; }
            public bool IsClip { get; }
            public long StartMs => Math.Min(StartEvent.TimeOffsetMs, EndEvent.TimeOffsetMs);
            public long EndMs => Math.Max(StartEvent.TimeOffsetMs, EndEvent.TimeOffsetMs);
            public int Lane { get; set; }
            public Rectangle Bounds { get; set; }
            public Rectangle HitBounds { get; set; }
            public Rectangle StartHandle { get; set; }
            public Rectangle EndHandle { get; set; }

            public static TimelineItem Clip(MacroEvent startEvent, MacroEvent endEvent, int startIndex, int endIndex)
            {
                return new TimelineItem(startEvent, endEvent, startIndex, endIndex, true);
            }

            public static TimelineItem Point(MacroEvent macroEvent, int index)
            {
                return new TimelineItem(macroEvent, macroEvent, index, index, false);
            }
        }

        private sealed record TimelineHit(int EventIndex, int StartIndex, int EndIndex, Rectangle Bounds, TimelineEditKind EditKind, string RowKey, int Lane);

        private sealed class TimelineDrag
        {
            public TimelineDrag(
                TimelineHit hit,
                MacroEvent startEvent,
                MacroEvent endEvent,
                long startMouseTimeMs,
                long startMs,
                long endMs)
            {
                Hit = hit;
                StartEvent = startEvent;
                EndEvent = endEvent;
                StartMouseTimeMs = startMouseTimeMs;
                StartMs = startMs;
                EndMs = endMs;
                PreviewStartMs = startMs;
                PreviewEndMs = endMs;
            }

            public TimelineHit Hit { get; }
            public MacroEvent StartEvent { get; }
            public MacroEvent EndEvent { get; }
            public long StartMouseTimeMs { get; }
            public long StartMs { get; }
            public long EndMs { get; }
            public long PreviewStartMs { get; set; }
            public long PreviewEndMs { get; set; }
            public Point StartMousePoint { get; set; }
            public bool HasMoved { get; set; }
        }

        private enum TimelineRowKind
        {
            Mouse,
            Key,
            Wheel,
            Other
        }

        private enum TimelineEditKind
        {
            None,
            Start,
            End,
            Range
        }
    }

    private sealed class MacroEditorCanvas : Panel
    {
        private const int FeedbackAnimationMs = 260;
        private const int FeedbackTailMs = 420;
        private readonly List<MarkerHit> _markerHits = new();
        private readonly List<ActivePlaybackMarker> _activePlaybackMarkers = new();
        private readonly System.Windows.Forms.Timer _feedbackTimer = new() { Interval = 16 };
        private readonly System.Diagnostics.Stopwatch _feedbackClock = System.Diagnostics.Stopwatch.StartNew();
        private List<MacroEvent> _events = new();
        private List<TimedEvent> _drawableEvents = new();
        private List<TimedEvent> _markerEvents = new();
        private List<Point> _fullPathPoints = new();
        private List<TimedCanvasPoint> _timedCanvasPoints = new();
        private Bitmap? _baseBitmap;
        private Size _baseBitmapSize;
        private ScreenshotBackground? _background;
        private CanvasMapping? _mapping;
        private long _startMs;
        private long _endMs;
        private long _currentTimeMs;
        private bool _showCurrentPoint;
        private int? _selectedIndex;
        private EventTimeCanvasPreview? _eventTimePreview;
        private bool _cacheDirty = true;
        private bool _baseCacheDirty = true;
        public MacroEditorCanvas()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(16, 18, 21);
            Cursor = Cursors.Default;
            _feedbackTimer.Tick += (_, _) => FeedbackTimerOnTick();
        }

        public event Action<int>? EventSelected;

        public void SetBackground(ScreenshotBackground? background)
        {
            _background?.Image.Dispose();
            _background = background;
            _cacheDirty = true;
            _baseCacheDirty = true;
            Invalidate();
        }

        public void DisposeBackground()
        {
            _background?.Image.Dispose();
            _baseBitmap?.Dispose();
            _background = null;
            _baseBitmap = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseBitmap?.Dispose();
                _feedbackTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        public void SetData(List<MacroEvent> events, long startMs, long endMs, int? selectedIndex)
        {
            _events = events;
            _startMs = startMs;
            _endMs = endMs;
            _selectedIndex = selectedIndex;
            _cacheDirty = true;
            _baseCacheDirty = true;
            _activePlaybackMarkers.Clear();
            Invalidate();
        }

        public void SetTrimRange(long startMs, long endMs, int? selectedIndex)
        {
            _startMs = startMs;
            _endMs = endMs;
            _selectedIndex = selectedIndex;
            Invalidate();
        }

        public void SetCurrentTime(long currentTimeMs, bool active)
        {
            _currentTimeMs = currentTimeMs;
            _showCurrentPoint = active || currentTimeMs > 0;
            Invalidate();
        }

        public void SetSelection(int? selectedIndex)
        {
            _selectedIndex = selectedIndex;
            Invalidate();
        }

        public void SetEventTimePreview(
            int startIndex,
            int endIndex,
            long startMs,
            long endMs,
            Point? startPoint,
            Point? endPoint)
        {
            var preview = new EventTimeCanvasPreview(startIndex, endIndex, startMs, endMs, startPoint, endPoint);
            if (Equals(_eventTimePreview, preview))
            {
                return;
            }

            _eventTimePreview = preview;
            Invalidate();
        }

        public void ClearEventTimePreview()
        {
            if (_eventTimePreview is null)
            {
                return;
            }

            _eventTimePreview = null;
            Invalidate();
        }

        public void AddPlaybackFeedback(MacroEvent macroEvent)
        {
            if (!IsEventMarker(macroEvent))
            {
                return;
            }

            EnsureDisplayCache();
            if (_mapping is null)
            {
                return;
            }

            Point? source = IsDrawablePoint(macroEvent)
                ? new Point(macroEvent.X, macroEvent.Y)
                : GetPointAtTime(macroEvent.TimeOffsetMs);
            if (source is null)
            {
                return;
            }

            var color = macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp
                ? Color.FromArgb(95, 220, 255)
                : Color.FromArgb(255, 210, 92);
            _activePlaybackMarkers.Add(new ActivePlaybackMarker(
                macroEvent.Kind,
                _mapping.ToCanvas(source.Value),
                color,
                _feedbackClock.ElapsedMilliseconds));
            if (!_feedbackTimer.Enabled)
            {
                _feedbackTimer.Start();
            }

            Invalidate();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            _cacheDirty = true;
            _baseCacheDirty = true;
            base.OnResize(eventargs);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var hit = _markerHits
                .Select(item => new
                {
                    Marker = item,
                    Distance = DistanceSquared(e.Location, item.Location)
                })
                .Where(item => item.Distance <= 18 * 18)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();
            if (hit is not null)
            {
                EventSelected?.Invoke(hit.Marker.EventIndex);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            e.Graphics.Clear(BackColor);
            _markerHits.Clear();
            EnsureDisplayCache();
            if (_drawableEvents.Count == 0 || _mapping is null)
            {
                DrawEmpty(e.Graphics);
                return;
            }

            DrawBaseLayer(e.Graphics);
            DrawTimedPolyline(e.Graphics, _timedCanvasPoints, _startMs, _endMs, Color.FromArgb(235, 235, 70, 72), 3);

            var pairRange = GetSelectedPairRange();
            if (_eventTimePreview is { } preview)
            {
                DrawEventTimePreview(e.Graphics, preview, _mapping.ToCanvas);
            }
            else if (pairRange is not null)
            {
                DrawPath(
                    e.Graphics,
                    _events.Skip(pairRange.Value.Start).Take(pairRange.Value.End - pairRange.Value.Start + 1).ToList(),
                    _mapping.ToCanvas,
                    Color.FromArgb(245, 95, 220, 255),
                    5);
            }

            foreach (var item in _markerEvents)
            {
                DrawMarker(e.Graphics, item, _mapping.ToCanvas);
            }

            DrawSelectedCallout(e.Graphics, _mapping.ToCanvas);
            DrawCurrentPoint(e.Graphics, _mapping.ToCanvas);
            DrawPlaybackFeedback(e.Graphics);
        }

        private void DrawEventTimePreview(Graphics graphics, EventTimeCanvasPreview preview, Func<Point, Point> mapper)
        {
            DrawPreviewSegment(graphics, preview, mapper, Color.FromArgb(245, 95, 220, 255), 5);
            DrawPreviewPoint(graphics, preview.StartPoint, mapper, Color.FromArgb(255, 95, 220, 255), 12);
            if (preview.EndIndex != preview.StartIndex)
            {
                DrawPreviewPoint(graphics, preview.EndPoint, mapper, Color.FromArgb(255, 95, 220, 255), 12);
            }
        }

        private void DrawPreviewSegment(
            Graphics graphics,
            EventTimeCanvasPreview preview,
            Func<Point, Point> mapper,
            Color color,
            int width)
        {
            var startMs = Math.Min(preview.StartMs, preview.EndMs);
            var endMs = Math.Max(preview.StartMs, preview.EndMs);
            var points = new List<Point>();
            if (preview.StartPoint is not null)
            {
                points.Add(mapper(preview.StartPoint.Value));
            }

            var startIndex = FindFirstTimedPointAfter(startMs);
            for (var i = startIndex; i < _timedCanvasPoints.Count && _timedCanvasPoints[i].TimeMs < endMs; i++)
            {
                points.Add(_timedCanvasPoints[i].Point);
            }

            if (preview.EndPoint is not null && preview.EndMs != preview.StartMs)
            {
                points.Add(mapper(preview.EndPoint.Value));
            }

            points = DrawingPathOptimizer.SimplifyForDisplay(points);
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
            graphics.DrawLines(pen, points.ToArray());
        }

        private static void DrawPreviewPoint(Graphics graphics, Point? point, Func<Point, Point> mapper, Color color, int size)
        {
            if (point is null)
            {
                return;
            }

            var mapped = mapper(point.Value);
            using var pen = new Pen(color, 3)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(pen, mapped.X - size, mapped.Y - size, mapped.X + size, mapped.Y + size);
            graphics.DrawLine(pen, mapped.X - size, mapped.Y + size, mapped.X + size, mapped.Y - size);
        }

        private void EnsureDisplayCache()
        {
            if (!_cacheDirty)
            {
                return;
            }

            _drawableEvents = _events
                .Select((macroEvent, index) => new TimedEvent(index, macroEvent))
                .Where(item => IsDrawablePoint(item.Event))
                .OrderBy(item => item.Event.TimeOffsetMs)
                .ToList();
            _markerEvents = _events
                .Select((macroEvent, index) => new TimedEvent(index, macroEvent))
                .Where(item => IsEventMarker(item.Event))
                .ToList();
            _mapping = _drawableEvents.Count == 0
                ? null
                : CreateMapping(_drawableEvents.Select(item => new Point(item.Event.X, item.Event.Y)).ToList());
            _fullPathPoints = _mapping is null
                ? new List<Point>()
                : DrawingPathOptimizer.SimplifyForDisplay(
                    _drawableEvents.Select(item => _mapping.ToCanvas(new Point(item.Event.X, item.Event.Y))).ToList());
            _timedCanvasPoints = _mapping is null
                ? new List<TimedCanvasPoint>()
                : _drawableEvents
                    .Select(item => new TimedCanvasPoint(item.Event.TimeOffsetMs, _mapping.ToCanvas(new Point(item.Event.X, item.Event.Y))))
                    .ToList();
            _cacheDirty = false;
            _baseCacheDirty = true;
        }

        private void DrawBaseLayer(Graphics graphics)
        {
            if (_baseCacheDirty || _baseBitmap is null || _baseBitmapSize != ClientSize)
            {
                RebuildBaseBitmap();
            }

            if (_baseBitmap is not null)
            {
                graphics.DrawImageUnscaled(_baseBitmap, Point.Empty);
            }
        }

        private void RebuildBaseBitmap()
        {
            _baseBitmap?.Dispose();
            _baseBitmap = null;
            _baseBitmapSize = ClientSize;
            if (_mapping is null || Width <= 0 || Height <= 0)
            {
                return;
            }

            _baseBitmap = new Bitmap(Width, Height);
            using var graphics = Graphics.FromImage(_baseBitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(BackColor);
            DrawGrid(graphics);
            DrawBackground(graphics, _mapping.ToCanvas);
            DrawPolyline(graphics, _fullPathPoints, Color.FromArgb(70, Color.White), 2);
            _baseCacheDirty = false;
        }

        private void DrawMarker(Graphics graphics, TimedEvent item, Func<Point, Point> mapper)
        {
            var macroEvent = item.Event;
            var point = mapper(new Point(macroEvent.X, macroEvent.Y));
            var selected = IsSelectedOrPaired(item.Index);
            var kept = macroEvent.TimeOffsetMs >= _startMs && macroEvent.TimeOffsetMs <= _endMs;
            var color = selected
                ? Color.FromArgb(95, 220, 255)
                : kept
                    ? Color.Gold
                    : Color.FromArgb(115, 120, 128);

            _markerHits.Add(new MarkerHit(item.Index, point));
            using var pen = new Pen(color, selected ? 3 : 2);
            var size = macroEvent.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? 10 : 8;
            graphics.DrawLine(pen, point.X - size, point.Y - size, point.X + size, point.Y + size);
            graphics.DrawLine(pen, point.X - size, point.Y + size, point.X + size, point.Y - size);
        }

        private (int Start, int End)? GetSelectedPairRange()
        {
            if (_selectedIndex is null)
            {
                return null;
            }

            var pairedIndex = FindPairedEventIndex(_events, _selectedIndex.Value);
            if (pairedIndex is null)
            {
                return null;
            }

            return (Math.Min(_selectedIndex.Value, pairedIndex.Value), Math.Max(_selectedIndex.Value, pairedIndex.Value));
        }

        private bool IsSelectedOrPaired(int index)
        {
            if (_selectedIndex is null)
            {
                return false;
            }

            if (_selectedIndex == index)
            {
                return true;
            }

            return FindPairedEventIndex(_events, _selectedIndex.Value) == index;
        }

        private void DrawSelectedCallout(Graphics graphics, Func<Point, Point> mapper)
        {
            if (_selectedIndex is null || _selectedIndex < 0 || _selectedIndex >= _events.Count)
            {
                return;
            }

            var macroEvent = _events[_selectedIndex.Value];
            if (!IsDrawablePoint(macroEvent))
            {
                return;
            }

            var point = mapper(new Point(macroEvent.X, macroEvent.Y));
            var text = $"{macroEvent.TimeOffsetMs:N0} ms  {GetKindText(macroEvent)}  {GetDetailText(macroEvent)}  ({macroEvent.X}, {macroEvent.Y})";
            var size = graphics.MeasureString(text, Font);
            var x = Math.Min(Math.Max(10, point.X + 16), Math.Max(10, Width - (int)size.Width - 28));
            var y = Math.Min(Math.Max(10, point.Y - 34), Math.Max(10, Height - (int)size.Height - 18));
            var rect = new Rectangle(x - 8, y - 5, (int)Math.Ceiling(size.Width) + 16, (int)Math.Ceiling(size.Height) + 10);

            using var bg = new SolidBrush(Color.FromArgb(225, 8, 10, 14));
            using var border = new Pen(Color.FromArgb(95, 220, 255), 1);
            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(bg, rect);
            graphics.DrawRectangle(border, rect);
            graphics.DrawString(text, Font, brush, x, y);
        }

        private void DrawCurrentPoint(Graphics graphics, Func<Point, Point> mapper)
        {
            if (!_showCurrentPoint)
            {
                return;
            }

            var source = GetPointAtTime(_currentTimeMs);
            if (source is null)
            {
                return;
            }

            var point = mapper(source.Value);
            using var fill = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
            using var outline = new Pen(Color.FromArgb(245, 20, 22, 26), 2F);
            var rect = new Rectangle(point.X - 6, point.Y - 6, 12, 12);
            graphics.FillEllipse(fill, rect);
            graphics.DrawEllipse(outline, rect);
        }

        private void DrawPlaybackFeedback(Graphics graphics)
        {
            if (_activePlaybackMarkers.Count == 0)
            {
                return;
            }

            var now = _feedbackClock.ElapsedMilliseconds;
            foreach (var marker in _activePlaybackMarkers)
            {
                var age = now - marker.StartMs;
                var visual = GetFeedbackVisual(marker.Kind, age);
                if (visual.Alpha <= 0)
                {
                    continue;
                }

                using var pen = new Pen(Color.FromArgb(visual.Alpha, marker.Color), visual.Width)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                DrawFeedbackCross(graphics, pen, marker.Point, marker.Kind, visual.Size);
            }
        }

        private void FeedbackTimerOnTick()
        {
            var now = _feedbackClock.ElapsedMilliseconds;
            _activePlaybackMarkers.RemoveAll(marker => now - marker.StartMs > FeedbackTailMs);
            if (_activePlaybackMarkers.Count == 0)
            {
                _feedbackTimer.Stop();
            }

            Invalidate();
        }

        private static FeedbackVisual GetFeedbackVisual(MacroEventKind kind, long ageMs)
        {
            var isRelease = kind is MacroEventKind.MouseUp or MacroEventKind.KeyUp;
            var baseSize = kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? 10 : 8;
            if (ageMs <= FeedbackAnimationMs)
            {
                var progress = SmoothStep(ageMs / (double)FeedbackAnimationMs);
                var size = isRelease
                    ? (int)Math.Round(Lerp(baseSize, 24, progress))
                    : (int)Math.Round(Lerp(24, baseSize, progress));
                var alpha = isRelease
                    ? (int)Math.Round(Lerp(255, 0, progress))
                    : (int)Math.Round(Lerp(50, 255, progress));
                return new FeedbackVisual(size, alpha, 3);
            }

            var tailProgress = Math.Clamp((ageMs - FeedbackAnimationMs) / (double)Math.Max(1, FeedbackTailMs - FeedbackAnimationMs), 0.0, 1.0);
            var tailAlpha = (int)Math.Round(Lerp(isRelease ? 0 : 150, 0, tailProgress));
            return new FeedbackVisual(baseSize, tailAlpha, 2);
        }

        private static void DrawFeedbackCross(Graphics graphics, Pen pen, Point point, MacroEventKind kind, int size)
        {
            var adjusted = kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? size + 2 : size;
            graphics.DrawLine(pen, point.X - adjusted, point.Y - adjusted, point.X + adjusted, point.Y + adjusted);
            graphics.DrawLine(pen, point.X - adjusted, point.Y + adjusted, point.X + adjusted, point.Y - adjusted);
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

        private Point? GetPointAtTime(long timeMs)
        {
            if (_drawableEvents.Count == 0)
            {
                return null;
            }

            var index = FindFirstDrawableEventAtOrAfter(timeMs);
            if (index <= 0)
            {
                var first = _drawableEvents[0].Event;
                return new Point(first.X, first.Y);
            }

            if (index >= _drawableEvents.Count)
            {
                var last = _drawableEvents[^1].Event;
                return new Point(last.X, last.Y);
            }

            var previous = _drawableEvents[index - 1].Event;
            var current = _drawableEvents[index].Event;
            if (current.TimeOffsetMs == previous.TimeOffsetMs)
            {
                return new Point(current.X, current.Y);
            }

            var t = Math.Clamp((timeMs - previous.TimeOffsetMs) / (double)(current.TimeOffsetMs - previous.TimeOffsetMs), 0.0, 1.0);
            return new Point(
                (int)Math.Round(previous.X + (current.X - previous.X) * t),
                (int)Math.Round(previous.Y + (current.Y - previous.Y) * t));
        }

        private int FindFirstTimedPointAfter(long timeMs)
        {
            var low = 0;
            var high = _timedCanvasPoints.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (_timedCanvasPoints[mid].TimeMs <= timeMs)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private int FindFirstDrawableEventAtOrAfter(long timeMs)
        {
            var low = 0;
            var high = _drawableEvents.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (_drawableEvents[mid].Event.TimeOffsetMs < timeMs)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private void DrawEmpty(Graphics graphics)
        {
            using var brush = new SolidBrush(Color.White);
            graphics.DrawString("表示できる軌跡がありません。", Font, brush, 18, 18);
        }

        private void DrawGrid(Graphics graphics)
        {
            using var pen = new Pen(Color.FromArgb(22, Color.White), 1);
            const int step = 80;
            for (var x = step; x < Width; x += step)
            {
                graphics.DrawLine(pen, x, 0, x, Height);
            }

            for (var y = step; y < Height; y += step)
            {
                graphics.DrawLine(pen, 0, y, Width, y);
            }
        }

        private void DrawBackground(Graphics graphics, Func<Point, Point> mapper)
        {
            if (_background is null)
            {
                return;
            }

            var topLeft = mapper(new Point(_background.Bounds.Left, _background.Bounds.Top));
            var bottomRight = mapper(new Point(_background.Bounds.Right, _background.Bounds.Bottom));
            var rect = Rectangle.FromLTRB(
                Math.Min(topLeft.X, bottomRight.X),
                Math.Min(topLeft.Y, bottomRight.Y),
                Math.Max(topLeft.X, bottomRight.X),
                Math.Max(topLeft.Y, bottomRight.Y));
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            var matrix = new System.Drawing.Imaging.ColorMatrix
            {
                Matrix33 = 0.32f
            };
            attributes.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
            graphics.DrawImage(
                _background.Image,
                rect,
                0,
                0,
                _background.Image.Width,
                _background.Image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }

        private CanvasMapping CreateMapping(List<Point> points)
        {
            var minX = points.Min(point => point.X);
            var maxX = points.Max(point => point.X);
            var minY = points.Min(point => point.Y);
            var maxY = points.Max(point => point.Y);
            var sourceWidth = Math.Max(1, maxX - minX);
            var sourceHeight = Math.Max(1, maxY - minY);
            var pad = 54;
            var scale = Math.Min(
                Math.Max(0.01, (Width - pad * 2) / (double)sourceWidth),
                Math.Max(0.01, (Height - pad * 2) / (double)sourceHeight));

            return new CanvasMapping(
                point => new Point(
                pad + (int)Math.Round((point.X - minX) * scale),
                pad + (int)Math.Round((point.Y - minY) * scale)),
                point => new Point(
                    minX + (int)Math.Round((point.X - pad) / scale),
                    minY + (int)Math.Round((point.Y - pad) / scale)));
        }

        private static void DrawPath(Graphics graphics, List<MacroEvent> events, Func<Point, Point> mapper, Color color, int width)
        {
            var points = events
                .Where(IsDrawablePoint)
                .Select(item => mapper(new Point(item.X, item.Y)))
                .ToList();
            var displayPoints = DrawingPathOptimizer.SimplifyForDisplay(points);
            if (displayPoints.Count < 2)
            {
                return;
            }

            using var pen = new Pen(color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(pen, displayPoints.ToArray());
        }

        private static void DrawPolyline(Graphics graphics, List<Point> points, Color color, int width)
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
            graphics.DrawLines(pen, points.ToArray());
        }

        private static void DrawTimedPolyline(
            Graphics graphics,
            List<TimedCanvasPoint> points,
            long startMs,
            long endMs,
            Color color,
            int width)
        {
            if (points.Count < 2 || endMs < startMs)
            {
                return;
            }

            var displayPoints = new List<Point>();
            foreach (var point in points)
            {
                if (point.TimeMs >= startMs && point.TimeMs <= endMs)
                {
                    displayPoints.Add(point.Point);
                }
            }

            displayPoints = DrawingPathOptimizer.SimplifyForDisplay(displayPoints);
            if (displayPoints.Count < 2)
            {
                return;
            }

            using var pen = new Pen(color, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(pen, displayPoints.ToArray());
        }

        private static int DistanceSquared(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private sealed record TimedEvent(int Index, MacroEvent Event);
        private sealed record TimedCanvasPoint(long TimeMs, Point Point);
        private sealed record MarkerHit(int EventIndex, Point Location);
        private sealed record ActivePlaybackMarker(MacroEventKind Kind, Point Point, Color Color, long StartMs);
        private sealed record EventTimeCanvasPreview(int StartIndex, int EndIndex, long StartMs, long EndMs, Point? StartPoint, Point? EndPoint);
        private readonly record struct FeedbackVisual(int Size, int Alpha, int Width);
        private sealed record CanvasMapping(Func<Point, Point> ToCanvas, Func<Point, Point> ToSource);
    }
}
