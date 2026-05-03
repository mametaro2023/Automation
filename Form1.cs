namespace AutomationTool;

public partial class Form1 : Form
{
    private readonly InputRecorder _recorder = new();
    private readonly MacroPlayer _player = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly List<Macro> _macros = new();

    private Button _recordButton = null!;
    private Button _stopButton = null!;
    private Button _playButton = null!;
    private Button _deleteButton = null!;
    private Button _saveButton = null!;
    private Button _loadButton = null!;
    private ListView _macroList = null!;
    private ListView _eventList = null!;
    private TextBox _nameBox = null!;
    private TextBox _hotkeyBox = null!;
    private ComboBox _densityBox = null!;
    private NumericUpDown _pollingRateBox = null!;
    private NumericUpDown _countdownBox = null!;
    private NumericUpDown _coordNoiseBox = null!;
    private NumericUpDown _timeNoiseBox = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private SplitContainer _mainSplit = null!;
    private SplitContainer _rightSplit = null!;
    private CancellationTokenSource? _countdownCts;
    private bool _updatingSelection;

    public Form1()
    {
        InitializeComponent();
        BuildInterface();
        _recorder.EventRecorded += RecorderOnEventRecorded;
    }

    private bool IsCountingDown => _countdownCts is not null;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AdjustSplitters();
        RefreshHotkeys();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            var hotkeyId = m.WParam.ToInt32();
            if (hotkeyId == HotkeyManager.EmergencyStopId)
            {
                StopCurrentWork();
                return;
            }

            if (_hotkeys.TryGetMacroId(hotkeyId, out var macroId))
            {
                var macro = _macros.FirstOrDefault(item => item.Id == macroId);
                if (macro is not null)
                {
                    _ = PlayMacroAsync(macro);
                }

                return;
            }
        }

        base.WndProc(ref m);
    }

    private void BuildInterface()
    {
        Text = "Automation Tool";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 600);
        ClientSize = new Size(1040, 640);
        Font = new Font("MS UI Gothic", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Resize += (_, _) => AdjustSplitters();

        Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(root);

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 7, 8, 5),
            WrapContents = false
        };
        root.Controls.Add(topPanel, 0, 0);

        _recordButton = CreateButton("記録", RecordButtonOnClick);
        _stopButton = CreateButton("停止", StopButtonOnClick);
        _playButton = CreateButton("再生", PlayButtonOnClick);
        _deleteButton = CreateButton("削除", DeleteButtonOnClick);
        _saveButton = CreateButton("保存", SaveButtonOnClick);
        _loadButton = CreateButton("読込", LoadButtonOnClick);
        topPanel.Controls.AddRange(new Control[]
        {
            _recordButton,
            _stopButton,
            _playButton,
            _deleteButton,
            _saveButton,
            _loadButton
        });

        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 120,
            Panel2MinSize = 120,
            BorderStyle = BorderStyle.Fixed3D
        };
        root.Controls.Add(_mainSplit, 0, 1);

        _macroList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false
        };
        _macroList.Columns.Add("名前", 150);
        _macroList.Columns.Add("ショートカット", 110);
        _macroList.Columns.Add("件数", 54);
        _macroList.Columns.Add("時間", 70);
        _macroList.SelectedIndexChanged += MacroListOnSelectedIndexChanged;
        _mainSplit.Panel1.Controls.Add(_macroList);

        _rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            Panel1MinSize = 100,
            Panel2MinSize = 100
        };
        _mainSplit.Panel2.Controls.Add(_rightSplit);

        var settingsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8)
        };
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _rightSplit.Panel1.Controls.Add(settingsPanel);

        var macroGroup = CreateGroup("マクロ");
        settingsPanel.Controls.Add(macroGroup, 0, 0);
        settingsPanel.SetRowSpan(macroGroup, 2);
        AddLabeledControl(macroGroup, "名前", _nameBox = new TextBox(), 24);
        _nameBox.TextChanged += NameBoxOnTextChanged;
        AddLabeledControl(macroGroup, "ショートカット", _hotkeyBox = new TextBox { ReadOnly = true }, 58);
        _hotkeyBox.KeyDown += HotkeyBoxOnKeyDown;
        macroGroup.Controls.Add(new Label
        {
            Text = "入力欄を選択してキーを押す（削除で解除）",
            Location = new Point(12, 94),
            Size = new Size(250, 32)
        });

        var recordGroup = CreateGroup("記録");
        settingsPanel.Controls.Add(recordGroup, 1, 0);
        _densityBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _densityBox.Items.AddRange(new object[] { "軽量", "標準", "高精度" });
        _densityBox.SelectedIndex = 1;
        _densityBox.SelectedIndexChanged += (_, _) => ApplyDensityDefaults();
        AddLabeledControl(recordGroup, "密度", _densityBox, 22);
        AddLabeledControl(recordGroup, "ポーリングHz", _pollingRateBox = new NumericUpDown
        {
            Minimum = 10,
            Maximum = 1000,
            Increment = 10,
            Value = 200
        }, 54);
        AddLabeledControl(recordGroup, "開始待ち秒", _countdownBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 60,
            Value = 3
        }, 86);

        var noiseGroup = CreateGroup("ノイズ");
        settingsPanel.Controls.Add(noiseGroup, 1, 1);
        AddLabeledControl(noiseGroup, "クリック座標(px)", _coordNoiseBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 20,
            Value = 2
        }, 28);
        AddLabeledControl(noiseGroup, "時間(%)", _timeNoiseBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 50,
            Value = 5
        }, 62);
        noiseGroup.Controls.Add(new Label
        {
            Text = "移動中の座標にはノイズを載せません。",
            Location = new Point(12, 94),
            Size = new Size(260, 22)
        });

        _eventList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true
        };
        _eventList.Columns.Add("時刻", 90);
        _eventList.Columns.Add("種別", 110);
        _eventList.Columns.Add("詳細", 380);
        _rightSplit.Panel2.Controls.Add(_eventList);

        _statusLabel = new ToolStripStatusLabel("待機中。");
        _statusStrip = new StatusStrip
        {
            Dock = DockStyle.Fill
        };
        _statusStrip.Items.Add(_statusLabel);
        root.Controls.Add(_statusStrip, 0, 2);

        UpdateButtons();
    }

    private void AdjustSplitters()
    {
        if (_mainSplit is not null && _mainSplit.Width > 0)
        {
            const int desiredLeft = 390;
            const int desiredLeftMin = 320;
            const int desiredRightMin = 520;
            var maxDistance = _mainSplit.Width - desiredRightMin - _mainSplit.SplitterWidth;
            if (maxDistance >= desiredLeftMin)
            {
                _mainSplit.SplitterDistance = Math.Clamp(desiredLeft, desiredLeftMin, maxDistance);
            }
        }

        if (_rightSplit is not null && _rightSplit.Height > 0)
        {
            const int desiredTop = 220;
            const int desiredTopMin = 190;
            const int desiredBottomMin = 180;
            var maxDistance = _rightSplit.Height - desiredBottomMin - _rightSplit.SplitterWidth;
            if (maxDistance >= desiredTopMin)
            {
                _rightSplit.SplitterDistance = Math.Clamp(desiredTop, desiredTopMin, maxDistance);
            }
        }
    }

    private static Button CreateButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(72, 24),
            Margin = new Padding(0, 0, 10, 0)
        };
        button.Click += click;
        return button;
    }

    private static GroupBox CreateGroup(string text)
    {
        return new GroupBox
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };
    }

    private static void AddLabeledControl(Control parent, string labelText, Control control, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = labelText,
            Location = new Point(12, y + 4),
            Size = new Size(105, 20)
        });
        control.Location = new Point(122, y);
        control.Size = new Size(Math.Max(90, parent.Width - 138), 23);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        parent.Controls.Add(control);
    }

    private void RecordButtonOnClick(object? sender, EventArgs e)
    {
        _ = StartRecordingWithCountdownAsync();
    }

    private async Task StartRecordingWithCountdownAsync()
    {
        if (_player.IsPlaying || _recorder.IsRecording || IsCountingDown)
        {
            return;
        }

        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;
        UpdateButtons();

        try
        {
            var countdown = (int)_countdownBox.Value;
            for (var remaining = countdown; remaining > 0; remaining--)
            {
                SetStatus($"記録開始まで {remaining} 秒。");
                await Task.Delay(1000, token);
            }

            StartRecording();
        }
        catch (OperationCanceledException)
        {
            SetStatus("記録開始をキャンセルしました。");
        }
        finally
        {
            _countdownCts?.Dispose();
            _countdownCts = null;
            UpdateButtons();
        }
    }

    private void StartRecording()
    {
        var options = GetRecordingOptions();
        _recorder.ShouldIgnoreMousePoint = null;
        try
        {
            _recorder.Start(options);
            _eventList.Items.Clear();
            SetStatus($"記録中: {options.Name} / {options.MousePollingRateHz}Hz");
            UpdateButtons();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "記録エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopButtonOnClick(object? sender, EventArgs e)
    {
        StopCurrentWork();
    }

    private void PlayButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is not null)
        {
            _ = PlayMacroAsync(macro);
        }
    }

    private void DeleteButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null)
        {
            return;
        }

        _macros.Remove(macro);
        RefreshMacroList();
        RefreshHotkeys();
        SetStatus("マクロを削除しました。");
    }

    private void SaveButtonOnClick(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "マクロファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            DefaultExt = "json",
            FileName = "macros.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        MacroStore.Save(dialog.FileName, _macros);
        SetStatus($"保存しました: {dialog.FileName}");
    }

    private void LoadButtonOnClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "マクロファイル (*.json)|*.json|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _macros.Clear();
        _macros.AddRange(MacroStore.Load(dialog.FileName));
        RefreshMacroList();
        RefreshHotkeys();
        SetStatus($"読み込みました: {dialog.FileName}");
    }

    private void StopCurrentWork()
    {
        if (_countdownCts is not null)
        {
            _countdownCts.Cancel();
            return;
        }

        if (_recorder.IsRecording)
        {
            var events = _recorder.Stop();
            RemoveTrailingToolWindowMouseEvents(events);
            if (events.Count > 0)
            {
                var macro = new Macro
                {
                    Name = string.IsNullOrWhiteSpace(_nameBox.Text)
                        ? $"マクロ {DateTime.Now:yyyyMMdd HHmmss}"
                        : _nameBox.Text.Trim(),
                    Recording = GetRecordingOptions(),
                    Events = events
                };
                _macros.Add(macro);
                RefreshMacroList(macro.Id);
                SetStatus($"{events.Count} 件のイベントを記録しました。");
            }
            else
            {
                SetStatus("記録を停止しました。イベントはありません。");
            }

            RefreshHotkeys();
        }
        else if (_player.IsPlaying)
        {
            _player.Stop();
        }

        UpdateButtons();
    }

    private void RemoveTrailingToolWindowMouseEvents(List<MacroEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        var toolBounds = RectangleToScreen(ClientRectangle);
        var lastTime = events[^1].TimeOffsetMs;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var macroEvent = events[i];
            if (!IsMouseEvent(macroEvent)
                || lastTime - macroEvent.TimeOffsetMs > 700
                || !toolBounds.Contains(macroEvent.X, macroEvent.Y))
            {
                break;
            }

            events.RemoveAt(i);
        }
    }

    private static bool IsMouseEvent(MacroEvent macroEvent)
    {
        return macroEvent.Kind is MacroEventKind.MouseMove
            or MacroEventKind.MouseDown
            or MacroEventKind.MouseUp
            or MacroEventKind.MouseWheel;
    }

    private async Task PlayMacroAsync(Macro macro)
    {
        if (_recorder.IsRecording || IsCountingDown)
        {
            return;
        }

        UpdateButtons();
        var noise = new NoiseSettings
        {
            CoordinateJitterPx = (int)_coordNoiseBox.Value,
            TimeJitterPercent = (int)_timeNoiseBox.Value
        };
        await _player.PlayAsync(macro, noise, SetStatus);
        UpdateButtons();
    }

    private void MacroListOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        _updatingSelection = true;
        _nameBox.Text = macro?.Name ?? "";
        _hotkeyBox.Text = macro?.Hotkey.ToString() ?? "";
        _updatingSelection = false;
        RefreshEventList(macro);
        UpdateButtons();
    }

    private void NameBoxOnTextChanged(object? sender, EventArgs e)
    {
        if (_updatingSelection)
        {
            return;
        }

        var macro = GetSelectedMacro();
        if (macro is null)
        {
            return;
        }

        macro.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "マクロ" : _nameBox.Text.Trim();
        RefreshMacroList(macro.Id);
    }

    private void HotkeyBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        var macro = GetSelectedMacro();
        if (macro is null)
        {
            return;
        }

        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            return;
        }

        if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
        {
            macro.Hotkey = new HotkeyGesture();
        }
        else
        {
            macro.Hotkey = new HotkeyGesture
            {
                Ctrl = e.Control,
                Alt = e.Alt,
                Shift = e.Shift,
                Key = e.KeyCode
            };
        }

        _hotkeyBox.Text = macro.Hotkey.ToString();
        RefreshMacroList(macro.Id);
        RefreshHotkeys();
    }

    private RecordingOptions GetRecordingOptions()
    {
        var options = _densityBox.SelectedIndex switch
        {
            0 => RecordingOptions.Lightweight(),
            2 => RecordingOptions.HighPrecision(),
            _ => RecordingOptions.Standard()
        };
        options.MousePollingRateHz = (int)_pollingRateBox.Value;
        return options;
    }

    private void ApplyDensityDefaults()
    {
        var rate = _densityBox.SelectedIndex switch
        {
            0 => 100,
            2 => 200,
            _ => 200
        };
        _pollingRateBox.Value = rate;
    }

    private Macro? GetSelectedMacro()
    {
        if (_macroList.SelectedItems.Count == 0)
        {
            return null;
        }

        return _macroList.SelectedItems[0].Tag as Macro;
    }

    private void RefreshMacroList(Guid? selectedId = null)
    {
        _macroList.BeginUpdate();
        _macroList.Items.Clear();
        foreach (var macro in _macros)
        {
            var item = new ListViewItem(macro.Name);
            item.SubItems.Add(macro.Hotkey.ToString());
            item.SubItems.Add(macro.Events.Count.ToString());
            item.SubItems.Add($"{macro.DurationMs} ms");
            item.Tag = macro;
            _macroList.Items.Add(item);
            if (selectedId == macro.Id)
            {
                item.Selected = true;
            }
        }

        _macroList.EndUpdate();
        if (_macroList.SelectedItems.Count == 0 && _macroList.Items.Count > 0 && selectedId is null)
        {
            _macroList.Items[0].Selected = true;
        }
    }

    private void RefreshEventList(Macro? macro)
    {
        _eventList.BeginUpdate();
        _eventList.Items.Clear();
        if (macro is not null)
        {
            foreach (var macroEvent in macro.Events)
            {
                AddEventListItem(macroEvent);
            }
        }

        _eventList.EndUpdate();
    }

    private void RecorderOnEventRecorded(MacroEvent macroEvent)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() => AddEventListItem(macroEvent));
    }

    private void AddEventListItem(MacroEvent macroEvent)
    {
        var item = new ListViewItem($"{macroEvent.TimeOffsetMs} ms");
        item.SubItems.Add(FormatEventKind(macroEvent.Kind));
        item.SubItems.Add(DescribeEvent(macroEvent));
        _eventList.Items.Add(item);
        if (_eventList.Items.Count > 0)
        {
            _eventList.EnsureVisible(_eventList.Items.Count - 1);
        }
    }

    private static string FormatEventKind(MacroEventKind kind)
    {
        return kind switch
        {
            MacroEventKind.MouseMove => "マウス移動",
            MacroEventKind.MouseDown => "マウス押下",
            MacroEventKind.MouseUp => "マウス解放",
            MacroEventKind.MouseWheel => "ホイール",
            MacroEventKind.KeyDown => "キー押下",
            MacroEventKind.KeyUp => "キー解放",
            _ => kind.ToString()
        };
    }

    private static string DescribeEvent(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseMove => $"X={macroEvent.X}, Y={macroEvent.Y}",
            MacroEventKind.MouseDown or MacroEventKind.MouseUp => $"{FormatButton(macroEvent.Button)} X={macroEvent.X}, Y={macroEvent.Y}",
            MacroEventKind.MouseWheel => $"量={macroEvent.WheelDelta} X={macroEvent.X}, Y={macroEvent.Y}",
            MacroEventKind.KeyDown or MacroEventKind.KeyUp => macroEvent.KeyCode.ToString(),
            _ => ""
        };
    }

    private static string FormatButton(RecordedMouseButton button)
    {
        return button switch
        {
            RecordedMouseButton.Left => "左",
            RecordedMouseButton.Right => "右",
            RecordedMouseButton.Middle => "中",
            RecordedMouseButton.XButton1 => "拡張1",
            RecordedMouseButton.XButton2 => "拡張2",
            _ => ""
        };
    }

    private void RefreshHotkeys()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            _hotkeys.RegisterAll(Handle, _macros);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void UpdateButtons()
    {
        var recording = _recorder.IsRecording;
        var playing = _player.IsPlaying;
        var countingDown = IsCountingDown;
        var selected = GetSelectedMacro() is not null;

        _recordButton.Enabled = !recording && !playing && !countingDown;
        _stopButton.Enabled = recording || playing || countingDown;
        _playButton.Enabled = selected && !recording && !playing && !countingDown;
        _deleteButton.Enabled = selected && !recording && !playing && !countingDown;
        _saveButton.Enabled = !recording && !playing && !countingDown;
        _loadButton.Enabled = !recording && !playing && !countingDown;
    }

    private void SetStatus(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(message));
            return;
        }

        _statusLabel.Text = message;
    }
}
