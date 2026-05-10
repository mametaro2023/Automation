using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AutomationTool;

public sealed class MacroTrimEditorForm : Form
{
    private readonly List<MacroEvent> _events;
    private readonly MacroEditorCanvas _canvas = new();
    private readonly MacroTimelineControl _timeline = new();
    private readonly TrimRangeControl _trimRange = new();
    private readonly Label _rangeLabel = new();
    private readonly Label _selectedLabel = new();
    private readonly NumericUpDown _waitBeforeBox = new();
    private readonly NumericUpDown _xBox = new();
    private readonly NumericUpDown _yBox = new();
    private readonly Button _deleteEventButton = new();
    private readonly Button _applyWaitButton = new();
    private readonly Button _applyPositionButton = new();
    private readonly Button _setStartPointButton = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
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
        _canvas.DisposeBackground();
        base.OnFormClosed(e);
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
            RowCount = 9,
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
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        _rangeLabel.Dock = DockStyle.Fill;
        _rangeLabel.ForeColor = Color.White;
        _rangeLabel.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_rangeLabel, 0, 0);
        panel.SetColumnSpan(_rangeLabel, 2);

        _timeline.Dock = DockStyle.Fill;
        panel.Controls.Add(_timeline, 0, 1);
        panel.SetColumnSpan(_timeline, 2);

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
        _trimRange.RangeChanged += (_, _) => RefreshCanvasAndRange(updateCanvas: !_trimRange.IsDragging);
        _trimRange.RangeChangeCompleted += (_, _) => RefreshCanvasAndRange(updateCanvas: true);
        _deleteEventButton.Click += (_, _) => DeleteSelectedEvent();
        _applyWaitButton.Click += (_, _) => ApplyWaitBefore();
        _applyPositionButton.Click += (_, _) => ApplyPosition();
        _setStartPointButton.Click += (_, _) => BeginSetStartPoint();
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
        RefreshCanvasAndRange(updateCanvas: true);
        RefreshSelectedDetails();
    }

    private void RefreshCanvasAndRange(bool updateCanvas)
    {
        var startMs = _trimRange.StartMs;
        var endMs = _trimRange.EndMs;
        if (updateCanvas)
        {
            _canvas.SetData(_events, startMs, endMs, _selectedEventIndex);
        }

        _timeline.SetData(_events, startMs, endMs, GetDuration(), _selectedEventIndex);
        _trimRange.SetRange(startMs, endMs, Math.Max(1, GetDuration()));
        _rangeLabel.Text = $"残す範囲: {startMs:N0} ms - {endMs:N0} ms / {GetDuration():N0} ms";
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

    private sealed class TrimRangeControl : Control
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
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint, true);
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

        public void SetRange(long startMs, long endMs, long durationMs)
        {
            durationMs = Math.Max(1, durationMs);
            startMs = Math.Clamp(startMs, 0, Math.Max(0, durationMs - 1));
            endMs = Math.Clamp(endMs, startMs + 1, durationMs);
            var changed = StartMs != startMs || EndMs != endMs || DurationMs != durationMs;
            StartMs = startMs;
            EndMs = endMs;
            DurationMs = durationMs;
            Invalidate();
            if (changed && !_suppressRangeChanged)
            {
                RangeChanged?.Invoke(this, EventArgs.Empty);
            }
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
            _suppressRangeChanged = true;
            try
            {
                switch (_dragMode)
                {
                    case DragMode.Start:
                        SetRange(Math.Min(time, EndMs - 1), EndMs, DurationMs);
                        break;
                    case DragMode.End:
                        SetRange(StartMs, Math.Max(time, StartMs + 1), DurationMs);
                        break;
                    case DragMode.Range:
                        var delta = XToTime(_dragStartX + (e.X - _dragStartX), bar) - XToTime(_dragStartX, bar);
                        var length = _dragEndMs - _dragStartMs;
                        var start = Math.Clamp(_dragStartMs + delta, 0, Math.Max(0, DurationMs - length));
                        SetRange(start, start + length, DurationMs);
                        break;
                }
            }
            finally
            {
                _suppressRangeChanged = false;
            }

            RangeChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragMode = DragMode.None;
            Capture = false;
            UpdateCursor(e.Location);
            RangeChangeCompleted?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var bar = GetBarBounds();
            var startX = TimeToX(StartMs, bar);
            var endX = TimeToX(EndMs, bar);
            var range = Rectangle.FromLTRB(startX, bar.Top, Math.Max(startX + 1, endX), bar.Bottom);

            using var outerBrush = new SolidBrush(Color.FromArgb(75, 10, 12, 16));
            using var barBrush = new SolidBrush(Color.FromArgb(75, 222, 226, 232));
            using var rangeBrush = new SolidBrush(Color.FromArgb(210, 95, 220, 255));
            using var handleBrush = new SolidBrush(Color.FromArgb(235, 235, 240, 245));
            using var textBrush = new SolidBrush(Color.FromArgb(220, 224, 230));
            using var borderPen = new Pen(Color.FromArgb(82, 88, 98));

            g.FillRectangle(outerBrush, bar);
            g.FillRectangle(barBrush, bar);
            g.FillRectangle(rangeBrush, range);
            g.DrawRectangle(borderPen, bar);
            DrawHandle(g, startX, bar, handleBrush);
            DrawHandle(g, endX, bar, handleBrush);

            var text = $"トリム: {StartMs:N0} ms - {EndMs:N0} ms";
            g.DrawString(text, Font, textBrush, bar.Left, 2);
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
            const int leftInset = 104;
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

        private static void DrawHandle(Graphics g, int x, Rectangle bar, Brush brush)
        {
            var points = new[]
            {
                new Point(x, bar.Top - 6),
                new Point(x - 6, bar.Top),
                new Point(x - 6, bar.Bottom),
                new Point(x + 6, bar.Bottom),
                new Point(x + 6, bar.Top)
            };
            g.FillPolygon(brush, points);
        }

        private enum DragMode
        {
            None,
            Start,
            End,
            Range
        }
    }

    private sealed class MacroTimelineControl : Control
    {
        private const int LabelWidth = 104;
        private const int RulerHeight = 24;
        private const int TrackHeight = 26;
        private const int Gap = 5;
        private readonly List<TimelineHit> _hits = new();
        private List<MacroEvent> _events = new();
        private long _startMs;
        private long _endMs;
        private long _durationMs = 1;
        private int? _selectedIndex;

        public MacroTimelineControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(18, 20, 24);
            Cursor = Cursors.Hand;
        }

        public event Action<int>? EventSelected;

        public void SetSelection(int? selectedIndex)
        {
            _selectedIndex = selectedIndex;
            Invalidate();
        }

        public void SetData(List<MacroEvent> events, long startMs, long endMs, long durationMs, int? selectedIndex)
        {
            _events = events;
            _startMs = startMs;
            _endMs = endMs;
            _durationMs = Math.Max(1, durationMs);
            _selectedIndex = selectedIndex;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var hit = _hits
                .OrderBy(item => DistanceSquared(item.Bounds, e.Location))
                .FirstOrDefault(item =>
                {
                    var bounds = item.Bounds;
                    bounds.Inflate(8, 8);
                    return bounds.Contains(e.Location);
                });
            if (hit is not null)
            {
                EventSelected?.Invoke(hit.EventIndex);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            _hits.Clear();

            var plot = GetPlotBounds();
            if (plot.Width <= 10 || plot.Height <= 10)
            {
                return;
            }

            DrawRuler(g, plot);
            DrawTrimRange(g, plot);
            DrawTracks(g, plot);
            DrawMouseMoveTrack(g, GetTrackBounds(plot, 0));
            DrawPairTrack(g, GetTrackBounds(plot, 1), MacroEventKind.MouseDown, MacroEventKind.MouseUp);
            DrawPairTrack(g, GetTrackBounds(plot, 2), MacroEventKind.KeyDown, MacroEventKind.KeyUp);
            DrawWheelTrack(g, GetTrackBounds(plot, 3));
            DrawSelectedPlayhead(g, plot);
        }

        private Rectangle GetPlotBounds()
        {
            return new Rectangle(LabelWidth, 4, Math.Max(1, Width - LabelWidth - 10), Math.Max(1, Height - 12));
        }

        private Rectangle GetTrackBounds(Rectangle plot, int trackIndex)
        {
            var y = plot.Top + RulerHeight + Gap + trackIndex * (TrackHeight + Gap);
            return new Rectangle(plot.Left, y, plot.Width, TrackHeight);
        }

        private void DrawRuler(Graphics g, Rectangle plot)
        {
            using var textBrush = new SolidBrush(Color.FromArgb(205, 210, 218));
            using var linePen = new Pen(Color.FromArgb(58, 62, 70));
            var ruler = new Rectangle(plot.Left, plot.Top, plot.Width, RulerHeight);
            g.DrawLine(linePen, ruler.Left, ruler.Bottom - 1, ruler.Right, ruler.Bottom - 1);
            var tickCount = Math.Clamp(plot.Width / 150, 3, 10);
            for (var i = 0; i <= tickCount; i++)
            {
                var time = _durationMs * i / tickCount;
                var x = TimeToX(time, plot);
                g.DrawLine(linePen, x, ruler.Bottom - 8, x, ruler.Bottom);
                var labelRect = i == tickCount
                    ? new Rectangle(Math.Max(plot.Left, x - 90), ruler.Top + 2, 88, 18)
                    : new Rectangle(x + 4, ruler.Top + 2, 90, 18);
                TextRenderer.DrawText(
                    g,
                    $"{time:N0} ms",
                    Font,
                    labelRect,
                    Color.FromArgb(205, 210, 218),
                    (i == tickCount ? TextFormatFlags.Right : TextFormatFlags.Left)
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawTracks(Graphics g, Rectangle plot)
        {
            var labels = new[] { "マウス移動", "マウスボタン", "キー入力", "ホイール" };
            using var gridPen = new Pen(Color.FromArgb(42, 45, 50));
            using var laneBrush = new SolidBrush(Color.FromArgb(25, 28, 33));
            for (var i = 0; i < labels.Length; i++)
            {
                var track = GetTrackBounds(plot, i);
                g.FillRectangle(laneBrush, track);
                g.DrawRectangle(gridPen, track);
                TextRenderer.DrawText(
                    g,
                    labels[i],
                    Font,
                    new Rectangle(8, track.Top, LabelWidth - 14, TrackHeight),
                    Color.FromArgb(220, 224, 230),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawTrimRange(Graphics g, Rectangle plot)
        {
            var left = TimeToX(_startMs, plot);
            var right = TimeToX(_endMs, plot);
            var trimRect = Rectangle.FromLTRB(left, plot.Top + RulerHeight, Math.Max(left + 1, right), plot.Bottom);
            using var trimBrush = new SolidBrush(Color.FromArgb(34, 235, 70, 72));
            using var trimPen = new Pen(Color.FromArgb(230, 235, 70, 72), 2F);
            g.FillRectangle(trimBrush, trimRect);
            g.DrawLine(trimPen, left, plot.Top + RulerHeight, left, plot.Bottom);
            g.DrawLine(trimPen, right, plot.Top + RulerHeight, right, plot.Bottom);
        }

        private void DrawMouseMoveTrack(Graphics g, Rectangle track)
        {
            var moveEvents = _events
                .Select((macroEvent, index) => new { macroEvent, index })
                .Where(item => item.macroEvent.Kind == MacroEventKind.MouseMove)
                .ToList();
            if (moveEvents.Count < 2)
            {
                return;
            }

            using var pen = new Pen(Color.FromArgb(150, 154, 160, 166), 2F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var centerY = track.Top + track.Height / 2;
            var lastX = TimeToX(moveEvents[0].macroEvent.TimeOffsetMs, GetPlotBounds());
            for (var i = 1; i < moveEvents.Count; i++)
            {
                var x = TimeToX(moveEvents[i].macroEvent.TimeOffsetMs, GetPlotBounds());
                if (x == lastX && i % 8 != 0)
                {
                    continue;
                }

                g.DrawLine(pen, lastX, centerY, x, centerY);
                lastX = x;
            }
        }

        private void DrawPairTrack(Graphics g, Rectangle track, MacroEventKind downKind, MacroEventKind upKind)
        {
            using var barBrush = new SolidBrush(downKind == MacroEventKind.MouseDown
                ? Color.FromArgb(205, 95, 220, 255)
                : Color.FromArgb(205, 255, 210, 92));
            using var markerBrush = new SolidBrush(Color.Gold);
            using var selectedBrush = new SolidBrush(Color.FromArgb(255, 95, 220, 255));
            var open = new Dictionary<string, (MacroEvent Event, int Index)>();
            foreach (var item in _events.Select((macroEvent, index) => new { macroEvent, index }))
            {
                var key = GetPairKey(item.macroEvent);
                if (item.macroEvent.Kind == downKind)
                {
                    open[key] = (item.macroEvent, item.index);
                    DrawMarker(g, track, item.macroEvent, item.index, markerBrush, selectedBrush);
                    DrawEventLabel(g, track, item.macroEvent);
                }
                else if (item.macroEvent.Kind == upKind)
                {
                    if (open.TryGetValue(key, out var start))
                    {
                        var x1 = TimeToX(start.Event.TimeOffsetMs, GetPlotBounds());
                        var x2 = TimeToX(item.macroEvent.TimeOffsetMs, GetPlotBounds());
                        var y = track.Top + track.Height / 2 - 5;
                        var rect = Rectangle.FromLTRB(Math.Min(x1, x2), y, Math.Max(x1, x2) + 1, y + 10);
                        g.FillRectangle(barBrush, rect);
                        var hitRect = rect;
                        hitRect.Inflate(0, 7);
                        _hits.Add(new TimelineHit(start.Index, hitRect));
                        open.Remove(key);
                    }

                    DrawMarker(g, track, item.macroEvent, item.index, markerBrush, selectedBrush);
                }
            }
        }

        private void DrawWheelTrack(Graphics g, Rectangle track)
        {
            using var markerBrush = new SolidBrush(Color.FromArgb(255, 190, 140));
            using var selectedBrush = new SolidBrush(Color.FromArgb(255, 95, 220, 255));
            foreach (var item in _events.Select((macroEvent, index) => new { macroEvent, index })
                         .Where(item => item.macroEvent.Kind == MacroEventKind.MouseWheel))
            {
                DrawMarker(g, track, item.macroEvent, item.index, markerBrush, selectedBrush);
                DrawEventLabel(g, track, item.macroEvent);
            }
        }

        private void DrawMarker(Graphics g, Rectangle track, MacroEvent macroEvent, int index, Brush markerBrush, Brush selectedBrush)
        {
            var x = TimeToX(macroEvent.TimeOffsetMs, GetPlotBounds());
            var selected = _selectedIndex == index;
            var size = selected ? 10 : 7;
            var rect = new Rectangle(x - size / 2, track.Top + track.Height / 2 - size / 2, size, size);
            g.FillEllipse(selected ? selectedBrush : markerBrush, rect);
            _hits.Add(new TimelineHit(index, rect));
        }

        private void DrawEventLabel(Graphics g, Rectangle track, MacroEvent macroEvent)
        {
            var text = GetShortEventLabel(macroEvent);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var x = TimeToX(macroEvent.TimeOffsetMs, GetPlotBounds());
            var rect = new Rectangle(
                Math.Min(Math.Max(track.Left, x + 8), Math.Max(track.Left, track.Right - 58)),
                track.Top + 1,
                56,
                Math.Max(12, track.Height / 2));
            TextRenderer.DrawText(
                g,
                text,
                Font,
                rect,
                Color.FromArgb(232, 236, 242),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawSelectedPlayhead(Graphics g, Rectangle plot)
        {
            if (_selectedIndex is null || _selectedIndex < 0 || _selectedIndex >= _events.Count)
            {
                return;
            }

            var selected = _events[_selectedIndex.Value];
            var x = TimeToX(selected.TimeOffsetMs, plot);
            using var pen = new Pen(Color.FromArgb(255, 95, 220, 255), 2F);
            g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
            TextRenderer.DrawText(
                g,
                $"{selected.TimeOffsetMs:N0} ms",
                Font,
                new Rectangle(Math.Min(x + 6, plot.Right - 90), plot.Top + 2, 86, 18),
                Color.FromArgb(95, 220, 255),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private int TimeToX(long timeMs, Rectangle plot)
        {
            var progress = Math.Clamp(timeMs / (double)Math.Max(1, _durationMs), 0.0, 1.0);
            return plot.Left + (int)Math.Round(progress * Math.Max(1, plot.Width - 1));
        }

        private static string GetPairKey(MacroEvent macroEvent)
        {
            return macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp
                ? $"mouse:{macroEvent.Button}"
                : $"key:{macroEvent.KeyCode}";
        }

        private static int DistanceSquared(Rectangle rect, Point point)
        {
            var cx = rect.Left + rect.Width / 2;
            var cy = rect.Top + rect.Height / 2;
            var dx = cx - point.X;
            var dy = cy - point.Y;
            return dx * dx + dy * dy;
        }

        private sealed record TimelineHit(int EventIndex, Rectangle Bounds);
    }

    private sealed class MacroEditorCanvas : Panel
    {
        private readonly List<MarkerHit> _markerHits = new();
        private List<MacroEvent> _events = new();
        private ScreenshotBackground? _background;
        private long _startMs;
        private long _endMs;
        private int? _selectedIndex;
        public MacroEditorCanvas()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(16, 18, 21);
            Cursor = Cursors.Default;
        }

        public event Action<int>? EventSelected;

        public void SetBackground(ScreenshotBackground? background)
        {
            _background?.Image.Dispose();
            _background = background;
            Invalidate();
        }

        public void DisposeBackground()
        {
            _background?.Image.Dispose();
            _background = null;
        }

        public void SetData(List<MacroEvent> events, long startMs, long endMs, int? selectedIndex)
        {
            _events = events;
            _startMs = startMs;
            _endMs = endMs;
            _selectedIndex = selectedIndex;
            Invalidate();
        }

        public void SetSelection(int? selectedIndex)
        {
            _selectedIndex = selectedIndex;
            Invalidate();
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

            var drawable = _events
                .Select((macroEvent, index) => new TimedEvent(index, macroEvent))
                .Where(item => IsDrawablePoint(item.Event))
                .ToList();

            if (drawable.Count == 0)
            {
                DrawEmpty(e.Graphics);
                return;
            }

            var mapping = CreateMapping(drawable.Select(item => new Point(item.Event.X, item.Event.Y)).ToList());
            DrawGrid(e.Graphics);
            DrawBackground(e.Graphics, mapping.ToCanvas);
            DrawPath(e.Graphics, drawable.Select(item => item.Event).ToList(), mapping.ToCanvas, Color.FromArgb(70, Color.White), 2);
            DrawPath(
                e.Graphics,
                drawable.Where(item => item.Event.TimeOffsetMs >= _startMs && item.Event.TimeOffsetMs <= _endMs)
                    .Select(item => item.Event)
                    .ToList(),
                mapping.ToCanvas,
                Color.FromArgb(235, 235, 70, 72),
                3);

            var pairRange = GetSelectedPairRange();
            if (pairRange is not null)
            {
                DrawPath(
                    e.Graphics,
                    _events.Skip(pairRange.Value.Start).Take(pairRange.Value.End - pairRange.Value.Start + 1).ToList(),
                    mapping.ToCanvas,
                    Color.FromArgb(245, 95, 220, 255),
                    5);
            }

            foreach (var item in _events.Select((macroEvent, index) => new TimedEvent(index, macroEvent))
                         .Where(item => IsEventMarker(item.Event)))
            {
                DrawMarker(e.Graphics, item, mapping.ToCanvas);
            }

            DrawSelectedCallout(e.Graphics, mapping.ToCanvas);
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

        private static int DistanceSquared(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private sealed record TimedEvent(int Index, MacroEvent Event);
        private sealed record MarkerHit(int EventIndex, Point Location);
        private sealed record CanvasMapping(Func<Point, Point> ToCanvas, Func<Point, Point> ToSource);
    }
}
