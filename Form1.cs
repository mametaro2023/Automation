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
    private NumericUpDown _coordNoiseBox = null!;
    private NumericUpDown _timeNoiseBox = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private bool _updatingSelection;

    public Form1()
    {
        InitializeComponent();
        BuildInterface();
        _recorder.EventRecorded += RecorderOnEventRecorded;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
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
                var macro = _macros.FirstOrDefault(macro => macro.Id == macroId);
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
        MinimumSize = new Size(860, 560);
        ClientSize = new Size(960, 620);
        Font = new Font("MS UI Gothic", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40
        };
        Controls.Add(topPanel);

        _recordButton = CreateButton("Record", 8, 8, RecordButtonOnClick);
        _stopButton = CreateButton("Stop", 88, 8, StopButtonOnClick);
        _playButton = CreateButton("Play", 168, 8, PlayButtonOnClick);
        _deleteButton = CreateButton("Delete", 248, 8, DeleteButtonOnClick);
        _saveButton = CreateButton("Save", 344, 8, SaveButtonOnClick);
        _loadButton = CreateButton("Load", 424, 8, LoadButtonOnClick);
        topPanel.Controls.AddRange(new Control[]
        {
            _recordButton,
            _stopButton,
            _playButton,
            _deleteButton,
            _saveButton,
            _loadButton
        });

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 360,
            BorderStyle = BorderStyle.Fixed3D
        };
        Controls.Add(mainSplit);

        _macroList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false
        };
        _macroList.Columns.Add("Name", 150);
        _macroList.Columns.Add("Hotkey", 110);
        _macroList.Columns.Add("Events", 60);
        _macroList.Columns.Add("Duration", 70);
        _macroList.SelectedIndexChanged += MacroListOnSelectedIndexChanged;
        mainSplit.Panel1.Controls.Add(_macroList);

        var rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 170
        };
        mainSplit.Panel2.Controls.Add(rightSplit);

        var settingsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        rightSplit.Panel1.Controls.Add(settingsPanel);

        var macroGroup = new GroupBox
        {
            Text = "Macro",
            Location = new Point(8, 8),
            Size = new Size(270, 130)
        };
        settingsPanel.Controls.Add(macroGroup);

        macroGroup.Controls.Add(new Label
        {
            Text = "Name",
            Location = new Point(10, 24),
            AutoSize = true
        });
        _nameBox = new TextBox
        {
            Location = new Point(78, 20),
            Width = 170
        };
        _nameBox.TextChanged += NameBoxOnTextChanged;
        macroGroup.Controls.Add(_nameBox);

        macroGroup.Controls.Add(new Label
        {
            Text = "Shortcut",
            Location = new Point(10, 56),
            AutoSize = true
        });
        _hotkeyBox = new TextBox
        {
            Location = new Point(78, 52),
            Width = 170,
            ReadOnly = true
        };
        _hotkeyBox.KeyDown += HotkeyBoxOnKeyDown;
        macroGroup.Controls.Add(_hotkeyBox);

        macroGroup.Controls.Add(new Label
        {
            Text = "Focus this box and press shortcut keys.",
            Location = new Point(10, 88),
            AutoSize = true
        });

        var recordGroup = new GroupBox
        {
            Text = "Recording",
            Location = new Point(292, 8),
            Size = new Size(180, 130)
        };
        settingsPanel.Controls.Add(recordGroup);
        recordGroup.Controls.Add(new Label
        {
            Text = "Density",
            Location = new Point(10, 26),
            AutoSize = true
        });
        _densityBox = new ComboBox
        {
            Location = new Point(72, 22),
            Width = 90,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _densityBox.Items.AddRange(new object[] { "Light", "Standard", "High" });
        _densityBox.SelectedIndex = 1;
        recordGroup.Controls.Add(_densityBox);

        recordGroup.Controls.Add(new Label
        {
            Text = "Esc is not global. Emergency: Ctrl+Alt+Pause",
            Location = new Point(10, 62),
            Size = new Size(156, 45)
        });

        var noiseGroup = new GroupBox
        {
            Text = "Noise",
            Location = new Point(486, 8),
            Size = new Size(210, 130)
        };
        settingsPanel.Controls.Add(noiseGroup);
        noiseGroup.Controls.Add(new Label
        {
            Text = "Coord px",
            Location = new Point(10, 28),
            AutoSize = true
        });
        _coordNoiseBox = new NumericUpDown
        {
            Location = new Point(110, 24),
            Width = 70,
            Minimum = 0,
            Maximum = 20,
            Value = 2
        };
        noiseGroup.Controls.Add(_coordNoiseBox);

        noiseGroup.Controls.Add(new Label
        {
            Text = "Time %",
            Location = new Point(10, 62),
            AutoSize = true
        });
        _timeNoiseBox = new NumericUpDown
        {
            Location = new Point(110, 58),
            Width = 70,
            Minimum = 0,
            Maximum = 50,
            Value = 5
        };
        noiseGroup.Controls.Add(_timeNoiseBox);

        _eventList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true
        };
        _eventList.Columns.Add("Time", 90);
        _eventList.Columns.Add("Type", 110);
        _eventList.Columns.Add("Detail", 360);
        rightSplit.Panel2.Controls.Add(_eventList);

        _statusLabel = new ToolStripStatusLabel("Ready.");
        _statusStrip = new StatusStrip();
        _statusStrip.Items.Add(_statusLabel);
        Controls.Add(_statusStrip);

        UpdateButtons();
    }

    private Button CreateButton(string text, int x, int y, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(72, 24)
        };
        button.Click += click;
        return button;
    }

    private void RecordButtonOnClick(object? sender, EventArgs e)
    {
        if (_player.IsPlaying)
        {
            return;
        }

        var options = GetRecordingOptions();
        _recorder.ShouldIgnoreMousePoint = p => RectangleToScreen(ClientRectangle).Contains(p);
        try
        {
            _recorder.Start(options);
            _eventList.Items.Clear();
            SetStatus($"Recording: {options.Name}");
            UpdateButtons();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Recording Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        SetStatus("Macro deleted.");
    }

    private void SaveButtonOnClick(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Macro files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = "macros.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        MacroStore.Save(dialog.FileName, _macros);
        SetStatus($"Saved: {dialog.FileName}");
    }

    private void LoadButtonOnClick(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Macro files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _macros.Clear();
        _macros.AddRange(MacroStore.Load(dialog.FileName));
        RefreshMacroList();
        RefreshHotkeys();
        SetStatus($"Loaded: {dialog.FileName}");
    }

    private void StopCurrentWork()
    {
        if (_recorder.IsRecording)
        {
            var events = _recorder.Stop();
            if (events.Count > 0)
            {
                var macro = new Macro
                {
                    Name = string.IsNullOrWhiteSpace(_nameBox.Text)
                        ? $"Macro {DateTime.Now:yyyyMMdd HHmmss}"
                        : _nameBox.Text.Trim(),
                    Recording = GetRecordingOptions(),
                    Events = events
                };
                _macros.Add(macro);
                RefreshMacroList(macro.Id);
                SetStatus($"Recorded {events.Count} events.");
            }
            else
            {
                SetStatus("Recording stopped. No events captured.");
            }

            RefreshHotkeys();
        }
        else if (_player.IsPlaying)
        {
            _player.Stop();
        }

        UpdateButtons();
    }

    private async Task PlayMacroAsync(Macro macro)
    {
        if (_recorder.IsRecording)
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

        macro.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "New Macro" : _nameBox.Text.Trim();
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
        return _densityBox.SelectedIndex switch
        {
            0 => RecordingOptions.Lightweight(),
            2 => RecordingOptions.HighPrecision(),
            _ => RecordingOptions.Standard()
        };
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
        item.SubItems.Add(macroEvent.Kind.ToString());
        item.SubItems.Add(DescribeEvent(macroEvent));
        _eventList.Items.Add(item);
        if (_eventList.Items.Count > 0)
        {
            _eventList.EnsureVisible(_eventList.Items.Count - 1);
        }
    }

    private static string DescribeEvent(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseMove => $"X={macroEvent.X}, Y={macroEvent.Y}",
            MacroEventKind.MouseDown or MacroEventKind.MouseUp => $"{macroEvent.Button} at X={macroEvent.X}, Y={macroEvent.Y}",
            MacroEventKind.MouseWheel => $"Delta={macroEvent.WheelDelta} at X={macroEvent.X}, Y={macroEvent.Y}",
            MacroEventKind.KeyDown or MacroEventKind.KeyUp => macroEvent.KeyCode.ToString(),
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
        var selected = GetSelectedMacro() is not null;

        _recordButton.Enabled = !recording && !playing;
        _stopButton.Enabled = recording || playing;
        _playButton.Enabled = selected && !recording && !playing;
        _deleteButton.Enabled = selected && !recording && !playing;
        _saveButton.Enabled = !recording && !playing;
        _loadButton.Enabled = !recording && !playing;
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
