using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AutomationTool;

public sealed class MacroTrimEditorForm : Form
{
    private readonly List<MacroEvent> _events;
    private readonly MacroEditorCanvas _canvas = new();
    private readonly ListView _eventList = new();
    private readonly TrackBar _startTrack;
    private readonly TrackBar _endTrack;
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
    private bool _updatingSelection;
    private int? _selectedEventIndex;

    public MacroTrimEditorForm(Macro macro)
    {
        _events = macro.Events
            .Select(CloneEvent)
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();

        Text = "マクロ編集";
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1100, 720);
        Font = new Font("Yu Gothic UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.White;
        KeyPreview = true;

        var duration = Math.Max(1, (int)Math.Min(int.MaxValue, GetDuration()));
        _startTrack = CreateTrackBar(duration);
        _endTrack = CreateTrackBar(duration);
        _endTrack.Value = duration;

        BuildInterface();
        WireEvents();
        RefreshEditor();
    }

    public List<MacroEvent> TrimmedEvents { get; private set; } = new();

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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
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
            Text = "×または一覧で選択。開始地点修正はボタン後に軌道上を左クリック。Ctrl+Z / Ctrl+Y 対応",
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
            RowCount = 2,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(29, 32, 37)
        };
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        mainSplit.Panel2.Controls.Add(side);
        Shown += (_, _) => LayoutMainSplit(mainSplit);

        ConfigureEventList();
        side.Controls.Add(_eventList, 0, 0);
        side.Controls.Add(CreatePropertyPanel(), 0, 1);

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

    private void ConfigureEventList()
    {
        _eventList.Dock = DockStyle.Fill;
        _eventList.View = View.Details;
        _eventList.FullRowSelect = true;
        _eventList.HideSelection = false;
        _eventList.MultiSelect = false;
        _eventList.BackColor = Color.FromArgb(245, 245, 245);
        _eventList.ForeColor = Color.Black;
        _eventList.Columns.Add("時刻", 72);
        _eventList.Columns.Add("種別", 78);
        _eventList.Columns.Add("詳細", 130);
        _eventList.Columns.Add("座標", 84);
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
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.FromArgb(34, 37, 43)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(CreateBottomLabel("開始"), 0, 0);
        panel.Controls.Add(_startTrack, 1, 0);
        panel.Controls.Add(CreateBottomLabel("終了"), 0, 1);
        panel.Controls.Add(_endTrack, 1, 1);

        _rangeLabel.Dock = DockStyle.Fill;
        _rangeLabel.ForeColor = Color.White;
        _rangeLabel.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_rangeLabel, 0, 2);
        panel.SetColumnSpan(_rangeLabel, 2);

        var help = new Label
        {
            Text = "灰: 全体 / 赤: 残す範囲 / 水色: 選択ペア / 黄×: 入力",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(210, 215, 222),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(help, 2, 0);
        panel.SetRowSpan(help, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        panel.Controls.Add(buttons, 3, 0);
        panel.SetRowSpan(buttons, 3);

        _okButton.Text = "適用";
        _okButton.Size = new Size(84, 28);
        buttons.Controls.Add(_okButton);

        _cancelButton.Text = "キャンセル";
        _cancelButton.Size = new Size(92, 28);
        buttons.Controls.Add(_cancelButton);

        return panel;
    }

    private static Label CreateBottomLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(220, 224, 230),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void WireEvents()
    {
        _canvas.EventSelected += SelectEvent;
        _eventList.SelectedIndexChanged += EventListOnSelectedIndexChanged;
        _startTrack.ValueChanged += TrackOnValueChanged;
        _endTrack.ValueChanged += TrackOnValueChanged;
        _deleteEventButton.Click += (_, _) => DeleteSelectedEvent();
        _applyWaitButton.Click += (_, _) => ApplyWaitBefore();
        _applyPositionButton.Click += (_, _) => ApplyPosition();
        _setStartPointButton.Click += (_, _) => BeginSetStartPoint();
        _okButton.Click += OkButtonOnClick;
        _cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
    }

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

    private static void ConfigureNumber(NumericUpDown box, int minimum, int maximum, int increment)
    {
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.Increment = increment;
        box.ThousandsSeparator = true;
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

        RefreshCanvasAndRange();
    }

    private void EventListOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_updatingSelection || _eventList.SelectedItems.Count == 0)
        {
            return;
        }

        if (_eventList.SelectedItems[0].Tag is int eventIndex)
        {
            SelectEvent(eventIndex);
        }
    }

    private void SelectEvent(int eventIndex)
    {
        if (eventIndex < 0 || eventIndex >= _events.Count)
        {
            return;
        }

        _selectedEventIndex = eventIndex;
        _canvas.SetSelection(eventIndex);
        RefreshEventSelection();
        RefreshSelectedDetails();
    }

    private void RefreshEditor()
    {
        UpdateTrackRange();
        RefreshEventList();
        RefreshCanvasAndRange();
        RefreshSelectedDetails();
    }

    private void RefreshCanvasAndRange()
    {
        var startMs = _startTrack.Value;
        var endMs = _endTrack.Value;
        _canvas.SetData(_events, startMs, endMs, _selectedEventIndex);
        _rangeLabel.Text = $"残す範囲: {startMs:N0} ms - {endMs:N0} ms / {GetDuration():N0} ms";
    }

    private void RefreshEventList()
    {
        _updatingSelection = true;
        _eventList.BeginUpdate();
        _eventList.Items.Clear();
        foreach (var item in _events.Select((macroEvent, index) => new { macroEvent, index })
                     .Where(item => IsEventMarker(item.macroEvent)))
        {
            var macroEvent = item.macroEvent;
            var row = new ListViewItem($"{macroEvent.TimeOffsetMs:N0}");
            row.SubItems.Add(GetKindText(macroEvent));
            row.SubItems.Add(GetDetailText(macroEvent));
            row.SubItems.Add($"{macroEvent.X}, {macroEvent.Y}");
            row.Tag = item.index;
            _eventList.Items.Add(row);

            if (_selectedEventIndex == item.index)
            {
                row.Selected = true;
                row.EnsureVisible();
            }
        }

        _eventList.EndUpdate();
        _updatingSelection = false;
    }

    private void RefreshEventSelection()
    {
        _updatingSelection = true;
        foreach (ListViewItem item in _eventList.Items)
        {
            item.Selected = item.Tag is int eventIndex && eventIndex == _selectedEventIndex;
            if (item.Selected)
            {
                item.EnsureVisible();
            }
        }

        _updatingSelection = false;
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

        var selectedWasInsideTrim = _events[index].TimeOffsetMs <= _endTrack.Value;
        var adjustedEndMs = selectedWasInsideTrim
            ? _endTrack.Value + delta
            : (long?)null;

        SaveUndoState();
        for (var i = index; i < _events.Count; i++)
        {
            _events[i].TimeOffsetMs = Math.Max(0, _events[i].TimeOffsetMs + delta);
        }

        if (adjustedEndMs is not null)
        {
            UpdateTrackRange();
            _endTrack.Value = Math.Clamp(
                (int)Math.Min(int.MaxValue, Math.Max(_startTrack.Value + 1L, adjustedEndMs.Value)),
                Math.Min(_endTrack.Maximum, _startTrack.Value + 1),
                _endTrack.Maximum);
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

        using var selector = new StartPointSelectionOverlayForm(_events, new Point(firstDrawable.X, firstDrawable.Y));
        if (selector.ShowDialog(this) == DialogResult.OK)
        {
            ApplyStartPoint(selector.SelectedStartPoint);
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
        var duration = Math.Max(1, (int)Math.Min(int.MaxValue, GetDuration()));
        SetTrackMaximum(_startTrack, duration);
        SetTrackMaximum(_endTrack, duration);
        if (_endTrack.Value == 0 || _endTrack.Value > duration)
        {
            _endTrack.Value = duration;
        }

        if (_startTrack.Value >= _endTrack.Value)
        {
            _startTrack.Value = Math.Max(0, _endTrack.Value - 1);
        }
    }

    private static void SetTrackMaximum(TrackBar trackBar, int maximum)
    {
        if (trackBar.Value > maximum)
        {
            trackBar.Value = maximum;
        }

        trackBar.Maximum = maximum;
        trackBar.TickFrequency = Math.Max(1, maximum / 10);
        trackBar.SmallChange = Math.Max(1, maximum / 100);
        trackBar.LargeChange = Math.Max(1, maximum / 20);
    }

    private void OkButtonOnClick(object? sender, EventArgs e)
    {
        var startMs = _startTrack.Value;
        var endMs = _endTrack.Value;
        var edited = _events
            .Where(item => item.TimeOffsetMs >= startMs && item.TimeOffsetMs <= endMs)
            .Select(item => CloneWithOffset(item, startMs))
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();

        if (edited.Count == 0)
        {
            MessageBox.Show(this, "残すイベントがありません。", "マクロ編集", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        TrimmedEvents = edited;
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
            _startTrack.Value,
            _endTrack.Value);
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _events.Clear();
        _events.AddRange(snapshot.Events.Select(CloneEvent));
        UpdateTrackRange();
        _startTrack.Value = Math.Min(_startTrack.Maximum, snapshot.StartMs);
        var minEnd = Math.Min(_endTrack.Maximum, _startTrack.Value + 1);
        _endTrack.Value = Math.Clamp(snapshot.EndMs, minEnd, _endTrack.Maximum);
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

    private static MacroEvent CloneWithOffset(MacroEvent source, long offsetMs)
    {
        var clone = CloneEvent(source);
        clone.TimeOffsetMs = Math.Max(0, clone.TimeOffsetMs - offsetMs);
        return clone;
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

    private sealed record EditorSnapshot(List<MacroEvent> Events, int? SelectedIndex, int StartMs, int EndMs);

    private sealed class MacroEditorCanvas : Panel
    {
        private readonly List<MarkerHit> _markerHits = new();
        private List<MacroEvent> _events = new();
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
