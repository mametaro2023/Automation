using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace AutomationTool;

public partial class Form1 : Form
{
    private const string UiFontName = "Yu Gothic UI";
    private const int DwmUseImmersiveDarkMode = 20;
    private static Color UiWindowBack = Color.FromArgb(245, 246, 248);
    private static Color UiPanelBack = Color.White;
    private static Color UiChromeBack = Color.FromArgb(238, 241, 245);
    private static Color UiBorder = Color.FromArgb(205, 211, 218);
    private static Color UiText = Color.FromArgb(32, 36, 42);
    private static Color UiMutedText = Color.FromArgb(82, 90, 102);
    private static Color UiPrimary = Color.FromArgb(37, 99, 235);
    private static Color UiDanger = Color.FromArgb(190, 48, 48);
    private static Color UiDangerBorder = Color.FromArgb(218, 168, 168);
    private static Color UiHover = Color.FromArgb(248, 250, 252);
    private static Color UiPressed = Color.FromArgb(229, 233, 238);
    private static Color UiPrimaryHover = Color.FromArgb(29, 78, 216);
    private static Color UiPrimaryPressed = Color.FromArgb(30, 64, 175);
    private static Color UiSelectionBack = Color.FromArgb(37, 99, 235);
    private static Color UiSelectionText = Color.White;

    private readonly InputRecorder _recorder = new();
    private readonly InputRecorder _motionLearningRecorder = new();
    private readonly MacroPlayer _player = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly List<Macro> _macros = new();
    private readonly EmergencyStopOverlay _emergencyOverlay = new();
    private readonly CountdownSoundPlayer _countdownSound = new();
    private readonly ToolTip _toolTip = new()
    {
        AutoPopDelay = 12000,
        InitialDelay = 350,
        ReshowDelay = 100,
        ShowAlways = true
    };

    private Button _recordButton = null!;
    private Button _stopButton = null!;
    private Button _playButton = null!;
    private Button _editButton = null!;
    private Button _deleteButton = null!;
    private Button _duplicateButton = null!;
    private Button _previewButton = null!;
    private FixedColumnListView _macroList = null!;
    private Label _summaryLabel = null!;
    private MenuStrip _menu = null!;
    private ToolStripMenuItem _systemThemeItem = null!;
    private ToolStripMenuItem _lightThemeItem = null!;
    private ToolStripMenuItem _darkThemeItem = null!;
    private ToolStripMenuItem _learningStartItem = null!;
    private ToolStripMenuItem _learningStopItem = null!;
    private ToolStripMenuItem _learningClearItem = null!;
    private ToolStripMenuItem _learningCountItem = null!;
    private TextBox _nameBox = null!;
    private TextBox _hotkeyBox = null!;
    private TextBox _emergencyHotkeyBox = null!;
    private TextBox _recordingStopHotkeyBox = null!;
    private Button _hotkeySetButton = null!;
    private Button _emergencyHotkeySetButton = null!;
    private Button _recordingStopHotkeySetButton = null!;
    private ComboBox _recordingModeBox = null!;
    private ComboBox _densityBox = null!;
    private NumericUpDown _eventIntervalBox = null!;
    private NumericUpDown _holdDurationBox = null!;
    private NumericUpDown _countdownBox = null!;
    private Label _timeNoiseLabel = null!;
    private NumericUpDown _coordNoiseBox = null!;
    private NumericUpDown _timeNoiseBox = null!;
    private NumericUpDown _accelNoiseBox = null!;
    private NumericUpDown _trajectoryNoiseBox = null!;
    private NumericUpDown _previewPathCountBox = null!;
    private NumericUpDown _playbackSpeedBox = null!;
    private CheckBox _captureScreenshotBox = null!;
    private CheckBox _showScreenshotBox = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private SplitContainer _mainSplit = null!;
    private Control _advancedSettingsPanel = null!;
    private RowStyle _advancedSettingsRow = null!;
    private RecordingStatusOverlayForm? _recordingOverlay;
    private AppSettings _settings = new();
    private string _macroStorePath = MacroStore.DefaultPath;
    private HumanMotionProfile _motionProfile = new();
    private AppThemeMode _themeMode;
    private bool _darkThemeActive;
    private CancellationTokenSource? _countdownCts;
    private HotkeyGesture _emergencyStopHotkey = CreateDefaultEmergencyHotkey();
    private HotkeyGesture _recordingStopHotkey = CreateDefaultRecordingStopHotkey();
    private FormWindowState _windowStateBeforeRecording = FormWindowState.Normal;
    private bool _restoreWindowAfterRecording;
    private bool _updatingSelection;
    private bool _updatingMacroListColumns;
    private PendingScreenshot? _pendingScreenshot;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPreferredAppModeDelegate(int appMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushMenuThemesDelegate();

    public Form1()
    {
        InitializeComponent();
        _settings = AppSettingsStore.Load();
        _themeMode = _settings.ThemeMode;
        _macroStorePath = ResolveMacroStorePath(_settings.MacroFilePath);
        _motionProfile = HumanMotionStore.Load(_macroStorePath);
        ApplyProcessDarkMode(IsThemeModeDark(_themeMode));
        SystemEvents.UserPreferenceChanged += SystemEventsOnUserPreferenceChanged;
        BuildInterface();
        LoadDefaultMacros();
    }

    private bool IsCountingDown => _countdownCts is not null;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyTheme();
        AdjustSplitters();
        RefreshHotkeys();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowDarkMode();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEventsOnUserPreferenceChanged;
        HideRecordingOverlay();
        if (_motionLearningRecorder.IsRecording)
        {
            _motionLearningRecorder.Stop();
        }

        base.OnFormClosed(e);
    }

    private void SystemEventsOnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_themeMode != AppThemeMode.System || IsDisposed)
        {
            return;
        }

        if (IsHandleCreated)
        {
            BeginInvoke((Action)ApplyTheme);
        }
        else
        {
            ApplyTheme();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            var hotkeyId = m.WParam.ToInt32();
            if (hotkeyId == HotkeyManager.EmergencyStopId)
            {
                EmergencyStopCurrentWork();
                return;
            }

            if (hotkeyId == HotkeyManager.RecordingStopId)
            {
                if (_recorder.IsRecording || IsCountingDown)
                {
                    StopCurrentWork();
                }

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
        SetWindowIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 680);
        ClientSize = new Size(1040, 720);
        Font = new Font(UiFontName, 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = UiWindowBack;
        ForeColor = UiText;
        Resize += (_, _) => AdjustSplitters();

        Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiWindowBack
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(root);

        _emergencyOverlay.Visible = false;
        _emergencyOverlay.Bounds = ClientRectangle;
        _emergencyOverlay.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_emergencyOverlay);
        _emergencyOverlay.BringToFront();

        _menu = new MenuStrip
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 2, 0, 2),
            BackColor = UiWindowBack,
            ForeColor = UiText,
            RenderMode = ToolStripRenderMode.System
        };
        var fileMenu = new ToolStripMenuItem("ファイル");
        fileMenu.DropDownItems.Add("保存場所を表示", null, ShowMacroStorageLocationOnClick);
        fileMenu.DropDownItems.Add("保存場所を変更...", null, ChangeMacroStorageLocationOnClick);
        var viewMenu = new ToolStripMenuItem("表示");
        var themeMenu = new ToolStripMenuItem("テーマ");
        _systemThemeItem = new ToolStripMenuItem("システム設定", null, (_, _) => SetThemeMode(AppThemeMode.System))
        {
            CheckOnClick = false
        };
        _lightThemeItem = new ToolStripMenuItem("ライト", null, (_, _) => SetThemeMode(AppThemeMode.Light))
        {
            CheckOnClick = false
        };
        _darkThemeItem = new ToolStripMenuItem("ダーク", null, (_, _) => SetThemeMode(AppThemeMode.Dark))
        {
            CheckOnClick = false
        };
        themeMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _systemThemeItem,
            _lightThemeItem,
            _darkThemeItem
        });
        viewMenu.DropDownItems.Add(themeMenu);
        var learningMenu = new ToolStripMenuItem("学習");
        _learningStartItem = new ToolStripMenuItem("軌道学習を開始", null, StartMotionLearningOnClick);
        _learningStopItem = new ToolStripMenuItem("軌道学習を停止", null, StopMotionLearningOnClick);
        _learningClearItem = new ToolStripMenuItem("学習サンプルを削除", null, ClearMotionLearningOnClick);
        _learningCountItem = new ToolStripMenuItem("サンプル: 0 件")
        {
            Enabled = false
        };
        learningMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _learningStartItem,
            _learningStopItem,
            new ToolStripSeparator(),
            _learningClearItem,
            _learningCountItem
        });
        _menu.Items.Add(fileMenu);
        _menu.Items.Add(viewMenu);
        _menu.Items.Add(learningMenu);
        MainMenuStrip = _menu;
        root.Controls.Add(_menu, 0, 0);

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 8, 10, 6),
            WrapContents = false,
            BackColor = UiChromeBack
        };
        root.Controls.Add(topPanel, 0, 1);

        _recordButton = CreateButton("記録", RecordButtonOnClick, ButtonTone.Primary);
        _stopButton = CreateButton("停止", StopButtonOnClick, ButtonTone.Danger);
        _playButton = CreateButton("再生", PlayButtonOnClick);
        _previewButton = CreateButton("プレビュー", PreviewButtonOnClick);
        _editButton = CreateButton("編集", EditButtonOnClick);
        _duplicateButton = CreateButton("複製", DuplicateButtonOnClick);
        _deleteButton = CreateButton("削除", DeleteButtonOnClick, ButtonTone.Danger);
        topPanel.Controls.AddRange(new Control[]
        {
            _recordButton,
            _stopButton,
            _playButton,
            _previewButton,
            _editButton,
            _duplicateButton,
            _deleteButton
        });

        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 120,
            Panel2MinSize = 120,
            IsSplitterFixed = true,
            BorderStyle = BorderStyle.None,
            BackColor = UiBorder
        };
        root.Controls.Add(_mainSplit, 0, 2);
        _mainSplit.Panel1.BackColor = UiWindowBack;
        _mainSplit.Panel2.BackColor = UiWindowBack;

        _macroList = new FixedColumnListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiPanelBack,
            ForeColor = UiText,
            GridLines = false,
            Font = new Font(UiFontName, 9F, FontStyle.Regular, GraphicsUnit.Point),
            OwnerDraw = true
        };
        _macroList.DrawColumnHeader += MacroListOnDrawColumnHeader;
        _macroList.DrawSubItem += MacroListOnDrawSubItem;
        _macroList.Resize += (_, _) => UpdateMacroListColumnWidths();
        _macroList.ColumnWidthChanging += MacroListOnColumnWidthChanging;
        _macroList.MouseClick += MacroListOnMouseClick;
        _macroList.KeyDown += MacroListOnKeyDown;
        _macroList.Columns.Add("名前", 150);
        _macroList.Columns.Add("ショートカット", 100);
        _macroList.Columns.Add("件数", 45);
        _macroList.Columns.Add("時間", 88);
        _macroList.Columns.Add("", 32);
        UpdateMacroListColumnWidths();
        _macroList.SelectedIndexChanged += MacroListOnSelectedIndexChanged;
        _mainSplit.Panel1.Controls.Add(_macroList);

        _mainSplit.Panel2.AutoScroll = true;
        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10),
            BackColor = UiWindowBack
        };
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 232));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 202));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _advancedSettingsRow = new RowStyle(SizeType.Absolute, 0);
        rightPanel.RowStyles.Add(_advancedSettingsRow);
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        _mainSplit.Panel2.Controls.Add(rightPanel);

        var macroGroup = CreateGroup("マクロ");
        rightPanel.Controls.Add(macroGroup, 0, 0);
        AddLabeledControl(macroGroup, "名前", _nameBox = new TextBox(), 24, 105);
        _nameBox.TextChanged += NameBoxOnTextChanged;
        AddLabeledControlWithButton(
            macroGroup,
            "ショートカット",
            _hotkeyBox = new TextBox { ReadOnly = true, TabStop = false },
            _hotkeySetButton = CreateInlineButton("設定", MacroHotkeySetButtonOnClick),
            58,
            105);
        AddLabeledControlWithButton(
            macroGroup,
            "緊急停止",
            _emergencyHotkeyBox = new TextBox { ReadOnly = true, TabStop = false },
            _emergencyHotkeySetButton = CreateInlineButton("設定", EmergencyHotkeySetButtonOnClick),
            92,
            105);
        _emergencyHotkeyBox.Text = _emergencyStopHotkey.ToString();
        AddLabeledControlWithButton(
            macroGroup,
            "記録停止",
            _recordingStopHotkeyBox = new TextBox { ReadOnly = true, TabStop = false },
            _recordingStopHotkeySetButton = CreateInlineButton("設定", RecordingStopHotkeySetButtonOnClick),
            126,
            105);
        _recordingStopHotkeyBox.Text = _recordingStopHotkey.ToString();
        macroGroup.Controls.Add(new Label
        {
            Text = "設定ボタンでショートカットを変更。Backspace/Deleteで解除。",
            Location = new Point(12, 166),
            Size = new Size(500, 24),
            ForeColor = UiMutedText,
            BackColor = Color.Transparent
        });

        var recordGroup = CreateGroup("記録・再生");
        rightPanel.Controls.Add(recordGroup, 0, 1);
        _recordingModeBox = new ScrollFriendlyComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _recordingModeBox.Items.AddRange(new object[] { "通常", "イベント重視" });
        _recordingModeBox.SelectedIndex = 0;
        _recordingModeBox.SelectedIndexChanged += (_, _) => RecordingModeOnChanged();
        AddLabeledControl(recordGroup, "記録方式", _recordingModeBox, 20, 118);
        _densityBox = new ScrollFriendlyComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _densityBox.Items.AddRange(new object[] { "軽量", "標準", "高精度" });
        _densityBox.SelectedIndex = 1;
        AddLabeledControl(recordGroup, "密度", _densityBox, 48, 118);
        AddLabeledControl(recordGroup, "開始待ち秒", _countdownBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 0,
            Maximum = 60,
            Value = 3
        }, 76, 118);
        AddLabeledControl(recordGroup, "再生速度(%)", _playbackSpeedBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 10,
            Maximum = 2000,
            Increment = 10,
            Value = 100
        }, 104, 118);
        _playbackSpeedBox.ValueChanged += PlaybackSpeedBoxOnValueChanged;
        _captureScreenshotBox = new CheckBox
        {
            Text = "記録時にスクリーンショットを保存",
            AutoSize = true,
            Checked = _settings.CaptureScreenshots,
            Location = new Point(123, 136),
            ForeColor = UiMutedText,
            BackColor = Color.Transparent
        };
        _captureScreenshotBox.CheckedChanged += (_, _) =>
        {
            _settings.CaptureScreenshots = _captureScreenshotBox.Checked;
            AppSettingsStore.Save(_settings);
        };
        recordGroup.Controls.Add(_captureScreenshotBox);
        _showScreenshotBox = new CheckBox
        {
            Text = "編集画面で背景表示",
            AutoSize = true,
            Checked = _settings.ShowEditorScreenshots,
            Location = new Point(123, 162),
            ForeColor = UiMutedText,
            BackColor = Color.Transparent
        };
        _showScreenshotBox.CheckedChanged += (_, _) =>
        {
            _settings.ShowEditorScreenshots = _showScreenshotBox.Checked;
            AppSettingsStore.Save(_settings);
        };
        recordGroup.Controls.Add(_showScreenshotBox);

        var advancedToggle = new CheckBox
        {
            Text = "詳細設定を表示",
            AutoSize = true,
            Dock = DockStyle.Left,
            Margin = new Padding(8, 4, 4, 0),
            ForeColor = UiMutedText,
            BackColor = UiWindowBack
        };
        advancedToggle.CheckedChanged += (_, _) => ToggleAdvancedSettings(advancedToggle.Checked);
        rightPanel.Controls.Add(advancedToggle, 0, 2);

        _advancedSettingsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Visible = false,
            BackColor = UiWindowBack
        };
        rightPanel.Controls.Add(_advancedSettingsPanel, 0, 3);

        var detailGroup = CreateGroup("詳細設定");
        _advancedSettingsPanel.Controls.Add(detailGroup);
        AddLabeledControl(detailGroup, "イベント間隔(ms)", _eventIntervalBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 10,
            Maximum = 600000,
            Increment = 10,
            Value = 200,
            ThousandsSeparator = true
        }, 20, 118);
        AddLabeledControl(detailGroup, "押下時間(ms)", _holdDurationBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 1,
            Maximum = 600000,
            Increment = 10,
            Value = 60,
            ThousandsSeparator = true
        }, 48, 118);
        AddLabeledControl(detailGroup, "クリック座標(px)", _coordNoiseBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 0,
            Maximum = 20,
            Value = 2
        }, 76, 118);
        _timeNoiseLabel = AddLabeledControl(detailGroup, "時間(%)", _timeNoiseBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 0,
            Maximum = 50,
            Value = 5
        }, 104, 118);
        AddLabeledControl(detailGroup, "軌道ブレ(px)", _trajectoryNoiseBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 0,
            Maximum = 80,
            Value = 16
        }, 132, 118);
        AddLabeledControl(detailGroup, "加速度(%)", _accelNoiseBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 0,
            Maximum = 50,
            Value = 12
        }, 160, 118);
        AddLabeledControl(detailGroup, "プレビュー数", _previewPathCountBox = new ScrollFriendlyNumericUpDown
        {
            Minimum = 1,
            Maximum = 10,
            Value = 3
        }, 188, 118);
        WireNoiseSettingChanges();

        _summaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiPanelBack,
            ForeColor = UiMutedText,
            Padding = new Padding(10),
            Text = "マクロが選択されていません。"
        };
        rightPanel.Controls.Add(_summaryLabel, 0, 4);

        ConfigureParameterTooltips();
        RecordingModeOnChanged();

        _statusLabel = new ToolStripStatusLabel("待機中。");
        var statusStrip = new StatusStrip
        {
            Dock = DockStyle.Fill,
            BackColor = UiWindowBack,
            ForeColor = UiMutedText,
            SizingGrip = true
        };
        statusStrip.Items.Add(_statusLabel);
        root.Controls.Add(statusStrip, 0, 3);

        ApplyTheme();
        ToggleAdvancedSettings(false);
        UpdateButtons();
    }

    private void SetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AutomationApp.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
                return;
            }

            var executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (executableIcon is not null)
            {
                Icon = executableIcon;
            }
        }
        catch
        {
            // アイコン設定に失敗しても操作ツール本体の動作は継続する。
        }
    }

    private void AdjustSplitters()
    {
        if (_mainSplit is not null && _mainSplit.Width > 0)
        {
            const int desiredLeft = 420;
            const int desiredLeftMin = 420;
            const int desiredRightMin = 520;
            var maxDistance = _mainSplit.Width - desiredRightMin - _mainSplit.SplitterWidth;
            if (maxDistance >= desiredLeftMin)
            {
                _mainSplit.SplitterDistance = Math.Clamp(desiredLeft, desiredLeftMin, maxDistance);
            }
        }

    }

    private enum ButtonTone
    {
        Normal,
        Primary,
        Danger
    }

    private static Button CreateButton(string text, EventHandler click, ButtonTone tone = ButtonTone.Normal)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(84, 26),
            Margin = new Padding(0, 0, 10, 0)
        };
        StyleButton(button, tone);
        button.Click += click;
        return button;
    }

    private static void StyleButton(Button button, ButtonTone tone)
    {
        var backColor = tone switch
        {
            ButtonTone.Primary => UiPrimary,
            _ => UiPanelBack
        };
        var foreColor = tone switch
        {
            ButtonTone.Primary => Color.White,
            ButtonTone.Danger => UiDanger,
            _ => UiText
        };
        var borderColor = tone switch
        {
            ButtonTone.Primary => UiPrimary,
            ButtonTone.Danger => UiDangerBorder,
            _ => UiBorder
        };

        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = tone == ButtonTone.Primary
            ? UiPrimaryHover
            : UiHover;
        button.FlatAppearance.MouseDownBackColor = tone == ButtonTone.Primary
            ? UiPrimaryPressed
            : UiPressed;
    }

    private static GroupBox CreateGroup(string text)
    {
        return new SectionGroupBox
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 4, 4, 8),
            Padding = new Padding(10, 22, 10, 10),
            BackColor = UiPanelBack,
            ForeColor = UiText
        };
    }

    private static Label AddLabeledControl(Control parent, string labelText, Control control, int y, int labelWidth)
    {
        var label = new Label
        {
            Text = labelText,
            Location = new Point(12, y + 4),
            Size = new Size(labelWidth, 20),
            ForeColor = UiMutedText,
            BackColor = Color.Transparent
        };
        parent.Controls.Add(label);
        control.Location = new Point(18 + labelWidth, y);
        control.Size = new Size(Math.Max(80, parent.Width - labelWidth - 36), 23);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        StyleInputControl(control);
        parent.Controls.Add(control);
        return label;
    }

    private static Label AddLabeledControlWithButton(
        Control parent,
        string labelText,
        Control control,
        Button button,
        int y,
        int labelWidth)
    {
        var label = AddLabeledControl(parent, labelText, control, y, labelWidth);
        const int buttonWidth = 58;
        button.Location = new Point(parent.Width - buttonWidth - 12, y - 1);
        button.Size = new Size(buttonWidth, 25);
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        control.Width = Math.Max(80, parent.Width - labelWidth - buttonWidth - 48);
        parent.Controls.Add(button);
        return label;
    }

    private static Button CreateInlineButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            Width = 58,
            Height = 25,
            Margin = new Padding(0)
        };
        button.Click += onClick;
        StyleButton(button, ButtonTone.Normal);
        return button;
    }

    private static void StyleInputControl(Control control)
    {
        control.BackColor = UiPanelBack;
        control.ForeColor = UiText;

        switch (control)
        {
            case TextBox textBox:
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox comboBox:
                comboBox.FlatStyle = FlatStyle.Flat;
                break;
            case NumericUpDown numericUpDown:
                numericUpDown.BorderStyle = BorderStyle.FixedSingle;
                break;
        }
    }

    private void SetThemeMode(AppThemeMode themeMode)
    {
        _themeMode = themeMode;
        _settings.ThemeMode = _themeMode;
        AppSettingsStore.Save(_settings);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        _darkThemeActive = IsThemeModeDark(_themeMode);
        ApplyProcessDarkMode(_darkThemeActive);
        ApplyPalette(_darkThemeActive);
        ApplyThemeToControl(this);
        StyleButton(_recordButton, ButtonTone.Primary);
        StyleButton(_stopButton, ButtonTone.Danger);
        StyleButton(_playButton, ButtonTone.Normal);
        StyleButton(_previewButton, ButtonTone.Normal);
        StyleButton(_editButton, ButtonTone.Normal);
        StyleButton(_duplicateButton, ButtonTone.Normal);
        StyleButton(_deleteButton, ButtonTone.Danger);
        StyleButton(_hotkeySetButton, ButtonTone.Normal);
        StyleButton(_emergencyHotkeySetButton, ButtonTone.Normal);
        StyleButton(_recordingStopHotkeySetButton, ButtonTone.Normal);
        if (_captureScreenshotBox is not null)
        {
            _captureScreenshotBox.ForeColor = UiMutedText;
            _captureScreenshotBox.BackColor = Color.Transparent;
        }

        if (_showScreenshotBox is not null)
        {
            _showScreenshotBox.ForeColor = UiMutedText;
            _showScreenshotBox.BackColor = Color.Transparent;
        }

        UpdateThemeMenuChecks();
        ApplyWindowDarkMode();
        Invalidate(true);
    }

    private static void ApplyPalette(bool dark)
    {
        if (dark)
        {
            UiWindowBack = Color.FromArgb(19, 20, 20);
            UiPanelBack = Color.FromArgb(30, 31, 32);
            UiChromeBack = Color.FromArgb(22, 23, 24);
            UiBorder = Color.FromArgb(48, 50, 52);
            UiText = Color.FromArgb(232, 234, 237);
            UiMutedText = Color.FromArgb(154, 160, 166);
            UiPrimary = Color.FromArgb(42, 63, 92);
            UiDanger = Color.FromArgb(242, 139, 130);
            UiDangerBorder = Color.FromArgb(92, 52, 51);
            UiHover = Color.FromArgb(39, 40, 41);
            UiPressed = Color.FromArgb(47, 49, 51);
            UiPrimaryHover = Color.FromArgb(48, 73, 108);
            UiPrimaryPressed = Color.FromArgb(37, 55, 82);
            UiSelectionBack = Color.FromArgb(43, 64, 92);
            UiSelectionText = Color.FromArgb(232, 234, 237);
            return;
        }

        UiWindowBack = Color.FromArgb(245, 246, 248);
        UiPanelBack = Color.White;
        UiChromeBack = Color.FromArgb(238, 241, 245);
        UiBorder = Color.FromArgb(205, 211, 218);
        UiText = Color.FromArgb(32, 36, 42);
        UiMutedText = Color.FromArgb(82, 90, 102);
        UiPrimary = Color.FromArgb(37, 99, 235);
        UiDanger = Color.FromArgb(190, 48, 48);
        UiDangerBorder = Color.FromArgb(218, 168, 168);
        UiHover = Color.FromArgb(248, 250, 252);
        UiPressed = Color.FromArgb(229, 233, 238);
        UiPrimaryHover = Color.FromArgb(29, 78, 216);
        UiPrimaryPressed = Color.FromArgb(30, 64, 175);
        UiSelectionBack = Color.FromArgb(37, 99, 235);
        UiSelectionText = Color.White;
    }

    private void ApplyThemeToControl(Control control)
    {
        if (control is EmergencyStopOverlay)
        {
            return;
        }

        switch (control)
        {
            case Form form:
                form.BackColor = UiWindowBack;
                form.ForeColor = UiText;
                break;
            case MenuStrip menuStrip:
                menuStrip.BackColor = UiWindowBack;
                menuStrip.ForeColor = UiText;
                ApplyThemeToToolStripItems(menuStrip.Items);
                break;
            case StatusStrip statusStrip:
                statusStrip.BackColor = UiWindowBack;
                statusStrip.ForeColor = UiMutedText;
                ApplyThemeToToolStripItems(statusStrip.Items);
                break;
            case ListView listView:
                listView.BackColor = UiPanelBack;
                listView.ForeColor = UiText;
                ApplyNativeControlTheme(listView);
                break;
            case SectionGroupBox groupBox:
                groupBox.BackColor = UiPanelBack;
                groupBox.ForeColor = UiText;
                break;
            case FlowLayoutPanel:
                control.BackColor = UiChromeBack;
                control.ForeColor = UiText;
                break;
            case SplitContainer splitContainer:
                splitContainer.BackColor = UiBorder;
                break;
            case SplitterPanel:
            case TableLayoutPanel:
            case Panel:
                control.BackColor = UiWindowBack;
                control.ForeColor = UiText;
                ApplyNativeControlTheme(control);
                break;
            case TextBox:
            case ComboBox:
            case NumericUpDown:
                StyleInputControl(control);
                ApplyNativeControlTheme(control);
                break;
            case Label:
            case CheckBox:
                control.BackColor = Color.Transparent;
                control.ForeColor = UiMutedText;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyThemeToControl(child);
        }
    }

    private static void ApplyThemeToToolStripItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = UiChromeBack;
            item.ForeColor = UiText;
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.DropDown.BackColor = UiChromeBack;
                menuItem.DropDown.ForeColor = UiText;
                ApplyThemeToToolStripItems(menuItem.DropDownItems);
            }
        }
    }

    private static void ApplyNativeControlTheme(Control control)
    {
        if (!control.IsHandleCreated)
        {
            return;
        }

        try
        {
            _ = NativeMethods.SetWindowTheme(
                control.Handle,
                UiWindowBack.GetBrightness() < 0.3F ? "DarkMode_Explorer" : "Explorer",
                null);
        }
        catch
        {
            // Native control theming is best-effort.
        }
    }

    private void UpdateThemeMenuChecks()
    {
        if (_systemThemeItem is null)
        {
            return;
        }

        _systemThemeItem.Checked = _themeMode == AppThemeMode.System;
        _lightThemeItem.Checked = _themeMode == AppThemeMode.Light;
        _darkThemeItem.Checked = _themeMode == AppThemeMode.Dark;
    }

    private void ApplyWindowDarkMode()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            var enabled = _darkThemeActive ? 1 : 0;
            _ = NativeMethods.DwmSetWindowAttribute(
                Handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                Marshal.SizeOf<int>());
        }
        catch
        {
            // Title bar theming is best-effort on older Windows builds.
        }
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsThemeModeDark(AppThemeMode themeMode)
    {
        return themeMode == AppThemeMode.Dark
            || (themeMode == AppThemeMode.System && IsSystemDarkTheme());
    }

    private static void ApplyProcessDarkMode(bool dark)
    {
        try
        {
            var uxtheme = NativeMethods.GetModuleHandle("uxtheme.dll");
            if (uxtheme == IntPtr.Zero)
            {
                return;
            }

            var setPreferredAppMode = NativeMethods.GetProcAddressByOrdinal(uxtheme, 135);
            if (setPreferredAppMode != IntPtr.Zero)
            {
                var setMode = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(setPreferredAppMode);
                _ = setMode(dark ? 2 : 3);
            }

            var flushMenuThemes = NativeMethods.GetProcAddressByOrdinal(uxtheme, 136);
            if (flushMenuThemes != IntPtr.Zero)
            {
                Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flushMenuThemes)();
            }
        }
        catch
        {
            // Native process dark mode is best-effort.
        }
    }

    private void ToggleAdvancedSettings(bool visible)
    {
        if (_advancedSettingsPanel is null || _advancedSettingsRow is null)
        {
            return;
        }

        _advancedSettingsPanel.Visible = visible;
        _advancedSettingsRow.Height = visible ? 260 : 0;
        _advancedSettingsPanel.Parent?.PerformLayout();
    }

    private void ConfigureParameterTooltips()
    {
        _toolTip.SetToolTip(_nameBox, "マクロ一覧に表示する名前です。動作には影響しません。");
        _toolTip.SetToolTip(_hotkeyBox, "このマクロを再生するショートカットです。右側の設定ボタンから変更します。");
        _toolTip.SetToolTip(_hotkeySetButton, "ショートカット設定画面を開きます。Backspace/Deleteで解除、Escでキャンセルできます。");
        _toolTip.SetToolTip(_emergencyHotkeyBox, "記録待ち・記録中・再生中の処理を即座に止めるホットキーです。右側の設定ボタンから変更します。");
        _toolTip.SetToolTip(_emergencyHotkeySetButton, "緊急停止キーの設定画面を開きます。Backspace/Deleteで既定値に戻します。");
        _toolTip.SetToolTip(_recordingStopHotkeyBox, "記録中だけ使う停止キーです。停止キー自体は記録データから除外します。");
        _toolTip.SetToolTip(_recordingStopHotkeySetButton, "記録停止キーの設定画面を開きます。Backspace/Deleteで既定値に戻します。");
        _toolTip.SetToolTip(_recordingModeBox, "完全記録は操作時刻をそのまま残します。イベント間隔一定はクリックやキー入力の間隔を指定msへ整えます。");
        _toolTip.SetToolTip(_densityBox, "記録密度のプリセットです。軽量は記録/再生60Hz、標準は記録200Hz/再生は画面Hz、高精度は記録/再生1000Hzです。");
        _toolTip.SetToolTip(_eventIntervalBox, "イベント間隔一定で使う基準間隔です。押下/解放ペア以外の次イベントまでの時間になります。");
        _toolTip.SetToolTip(_holdDurationBox, "イベント間隔一定で使う押下から解放までの時間です。クリックやキー押下の長さを決めます。");
        _toolTip.SetToolTip(_countdownBox, "記録ボタンを押してから実際に記録開始するまでの待ち時間です。操作対象へ移動する余裕を作ります。");
        _toolTip.SetToolTip(_playbackSpeedBox, "選択中マクロの再生速度です。100%が記録時と同じ速度、200%は2倍速、50%は半分の速度です。");
        _toolTip.SetToolTip(_captureScreenshotBox, "記録開始直前の仮想デスクトップ全体を保存し、編集画面の背景に使います。");
        _toolTip.SetToolTip(_showScreenshotBox, "保存済みスクリーンショットを編集画面の軌道背景として半透明表示します。");
        _toolTip.SetToolTip(_coordNoiseBox, "クリック押下/解放の座標に加える小さな揺れです。重要点なので大きくしすぎないでください。");
        _toolTip.SetToolTip(_timeNoiseBox, "クリックやキー入力の間隔に加える時間揺れです。機械的な一定間隔を避けます。");
        _toolTip.SetToolTip(_trajectoryNoiseBox, "クリック以外のマウス軌道に加える曲がり具合です。大きいほど毎回違う軌道になります。");
        _toolTip.SetToolTip(_accelNoiseBox, "マウス移動中の加速・減速の偏りです。人間らしい速度変化を作ります。");
        _toolTip.SetToolTip(_previewPathCountBox, "プレビューで表示するノイズ入り軌道の本数です。多いほど揺れ幅を確認できますが画面は混みます。");
    }

    private void RecordButtonOnClick(object? sender, EventArgs e)
    {
        _ = StartRecordingWithCountdownAsync();
    }

    private async Task StartRecordingWithCountdownAsync()
    {
        if (_player.IsPlaying || _recorder.IsRecording || _motionLearningRecorder.IsRecording || IsCountingDown)
        {
            return;
        }

        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;
        var countdownScreen = Screen.FromControl(this);
        var countdownOverlay = new CountdownOverlayForm(countdownScreen);
        UpdateButtons();

        try
        {
            MinimizeForRecording();
            var countdown = (int)_countdownBox.Value;
            for (var remaining = countdown; remaining > 0; remaining--)
            {
                SetStatus($"記録開始まで {remaining} 秒。");
                countdownOverlay.ShowCountdown(remaining);
                _countdownSound.PlayTick();
                await Task.Delay(1000, token);
            }

            _countdownSound.PlayStart();
            countdownOverlay.ShowStart();
            await Task.Delay(250, token);
            countdownOverlay.Hide();
            StartRecording();
        }
        catch (OperationCanceledException)
        {
            SetStatus("記録開始をキャンセルしました。");
            RestoreAfterRecording();
        }
        finally
        {
            countdownOverlay.Close();
            countdownOverlay.Dispose();
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
            _pendingScreenshot = _settings.CaptureScreenshots ? CaptureRecordingScreenshot() : null;
            _recorder.Start(options);
            ShowRecordingOverlay();
            SetStatus($"記録中: {options.Name} / {options.MousePollingRateHz}Hz");
            UpdateButtons();
        }
        catch (Exception ex)
        {
            DeletePendingScreenshot();
            HideRecordingOverlay();
            RestoreAfterRecording();
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "記録エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void MinimizeForRecording()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            _restoreWindowAfterRecording = false;
            return;
        }

        _windowStateBeforeRecording = WindowState;
        _restoreWindowAfterRecording = true;
        WindowState = FormWindowState.Minimized;
    }

    private void RestoreAfterRecording()
    {
        if (!_restoreWindowAfterRecording)
        {
            return;
        }

        WindowState = _windowStateBeforeRecording == FormWindowState.Minimized
            ? FormWindowState.Normal
            : _windowStateBeforeRecording;
        Show();
        Activate();
        _restoreWindowAfterRecording = false;
    }

    private void StopButtonOnClick(object? sender, EventArgs e)
    {
        StopCurrentWork();
    }

    private void StartMotionLearningOnClick(object? sender, EventArgs e)
    {
        if (_recorder.IsRecording || _motionLearningRecorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        try
        {
            _motionLearningRecorder.ShouldIgnoreMousePoint = null;
            _motionLearningRecorder.Start(CreateMotionLearningOptions());
            SetStatus("軌道学習中です。停止すると抽象サンプルとして保存します。");
        }
        catch (Exception ex)
        {
            SetStatus($"軌道学習を開始できません: {ex.Message}");
            MessageBox.Show(this, ex.Message, "軌道学習", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private void StopMotionLearningOnClick(object? sender, EventArgs e)
    {
        StopMotionLearning();
        UpdateButtons();
    }

    private void StopMotionLearning()
    {
        if (!_motionLearningRecorder.IsRecording)
        {
            return;
        }

        try
        {
            var events = _motionLearningRecorder.Stop();
            var added = HumanMotionProfileBuilder.AddSession(_motionProfile, events);
            HumanMotionStore.Save(_macroStorePath, _motionProfile);
            SetStatus(added > 0
                ? $"軌道学習を保存しました: +{added} 件 / 合計 {_motionProfile.TotalSampleCount} 件"
                : "軌道学習を停止しました。保存できるサンプルはありませんでした。");
        }
        catch (Exception ex)
        {
            SetStatus($"軌道学習の保存に失敗: {ex.Message}");
            MessageBox.Show(this, ex.Message, "軌道学習", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearMotionLearningOnClick(object? sender, EventArgs e)
    {
        if (_motionLearningRecorder.IsRecording)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "保存済みの軌道学習サンプルをすべて削除しますか？",
            "軌道学習",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        _motionProfile = new HumanMotionProfile();
        HumanMotionStore.Save(_macroStorePath, _motionProfile);
        SetStatus("軌道学習サンプルを削除しました。");
        UpdateButtons();
    }

    private void EmergencyStopCurrentWork()
    {
        StopCurrentWork();
        HideRecordingOverlay();
        _emergencyOverlay.ShowMessage();
        SetStatus("緊急停止しました。");
    }

    private void ShowRecordingOverlay()
    {
        HideRecordingOverlay();
        var screen = Screen.FromControl(this);
        _recordingOverlay = new RecordingStatusOverlayForm(screen, $"{_recordingStopHotkey}で記録を停止");
        _recordingOverlay.Show();
    }

    private void HideRecordingOverlay()
    {
        if (_recordingOverlay is null)
        {
            return;
        }

        _recordingOverlay.Close();
        _recordingOverlay.Dispose();
        _recordingOverlay = null;
    }

    private void PlayButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is not null)
        {
            _ = PlayMacroAsync(macro);
        }
    }

    private void PreviewButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null || _recorder.IsRecording || _motionLearningRecorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        var noise = GetNoiseSettings(macro);
        using var overlay = new PreviewOverlayForm(macro, noise, (int)_previewPathCountBox.Value, Screen.FromControl(this), _motionProfile);
        overlay.ShowDialog(this);
    }

    private void EditButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null || _recorder.IsRecording || _motionLearningRecorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        using var editor = new MacroTrimEditorForm(macro, _settings.ShowEditorScreenshots, ResolveScreenshotPath(macro));
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        macro.Events = editor.EditedEvents;
        macro.TrimStartMs = editor.TrimStartMs;
        macro.TrimEndMs = editor.TrimEndMs;
        RefreshMacroList(macro.Id);
        RefreshSummary(macro);
        AutoSaveMacros();
        SetStatus("マクロをトリミングしました。");
    }

    private PendingScreenshot? CaptureRecordingScreenshot()
    {
        try
        {
            var bounds = GetVirtualScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            var directory = GetScreenshotDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return new PendingScreenshot(path, bounds);
        }
        catch (Exception ex)
        {
            SetStatus($"スクリーンショット保存失敗: {ex.Message}");
            return null;
        }
    }

    private void ApplyPendingScreenshot(Macro macro)
    {
        if (_pendingScreenshot is null)
        {
            return;
        }

        macro.ScreenshotPath = GetRelativeScreenshotPath(_pendingScreenshot.Path);
        macro.ScreenshotX = _pendingScreenshot.Bounds.X;
        macro.ScreenshotY = _pendingScreenshot.Bounds.Y;
        macro.ScreenshotWidth = _pendingScreenshot.Bounds.Width;
        macro.ScreenshotHeight = _pendingScreenshot.Bounds.Height;
        _pendingScreenshot = null;
    }

    private void DeletePendingScreenshot()
    {
        if (_pendingScreenshot is null)
        {
            return;
        }

        TryDeleteFile(_pendingScreenshot.Path);
        _pendingScreenshot = null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // スクリーンショットの削除失敗はマクロ操作を妨げない。
        }
    }

    private string GetScreenshotDirectory()
    {
        var macroDirectory = Path.GetDirectoryName(_macroStorePath);
        return Path.Combine(
            string.IsNullOrWhiteSpace(macroDirectory) ? MacroStore.DefaultDirectory : macroDirectory,
            "screenshots");
    }

    private string GetRelativeScreenshotPath(string path)
    {
        var macroDirectory = Path.GetDirectoryName(_macroStorePath);
        if (string.IsNullOrWhiteSpace(macroDirectory))
        {
            return path;
        }

        return Path.GetRelativePath(macroDirectory, path);
    }

    private void MigrateScreenshotsForStorePath(string newMacroStorePath)
    {
        var currentMacroDirectory = Path.GetDirectoryName(_macroStorePath);
        if (string.IsNullOrWhiteSpace(currentMacroDirectory))
        {
            currentMacroDirectory = MacroStore.DefaultDirectory;
        }

        var newMacroDirectory = Path.GetDirectoryName(newMacroStorePath);
        if (string.IsNullOrWhiteSpace(newMacroDirectory))
        {
            newMacroDirectory = MacroStore.DefaultDirectory;
        }

        var newScreenshotDirectory = Path.Combine(newMacroDirectory, "screenshots");
        Directory.CreateDirectory(newScreenshotDirectory);

        foreach (var macro in _macros)
        {
            if (string.IsNullOrWhiteSpace(macro.ScreenshotPath))
            {
                continue;
            }

            var sourcePath = Path.IsPathRooted(macro.ScreenshotPath)
                ? macro.ScreenshotPath
                : Path.GetFullPath(Path.Combine(currentMacroDirectory, macro.ScreenshotPath));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var extension = Path.GetExtension(sourcePath);
            var destinationPath = Path.Combine(
                newScreenshotDirectory,
                $"{Guid.NewGuid():N}{(string.IsNullOrWhiteSpace(extension) ? ".png" : extension)}");

            if (!string.Equals(
                    Path.GetFullPath(sourcePath),
                    Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }

            macro.ScreenshotPath = Path.GetRelativePath(newMacroDirectory, destinationPath);
        }
    }

    private string ResolveScreenshotPath(Macro macro)
    {
        if (string.IsNullOrWhiteSpace(macro.ScreenshotPath))
        {
            return "";
        }

        if (Path.IsPathRooted(macro.ScreenshotPath))
        {
            return macro.ScreenshotPath;
        }

        var macroDirectory = Path.GetDirectoryName(_macroStorePath);
        return Path.GetFullPath(Path.Combine(
            string.IsNullOrWhiteSpace(macroDirectory) ? MacroStore.DefaultDirectory : macroDirectory,
            macro.ScreenshotPath));
    }

    private void CopyScreenshotForDuplicate(Macro source, Macro copy)
    {
        var sourcePath = ResolveScreenshotPath(source);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            copy.ScreenshotPath = null;
            copy.ScreenshotWidth = 0;
            copy.ScreenshotHeight = 0;
            return;
        }

        try
        {
            var directory = GetScreenshotDirectory();
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(sourcePath);
            var copyPath = Path.Combine(directory, $"{Guid.NewGuid():N}{(string.IsNullOrWhiteSpace(extension) ? ".png" : extension)}");
            File.Copy(sourcePath, copyPath, overwrite: false);
            copy.ScreenshotPath = GetRelativeScreenshotPath(copyPath);
        }
        catch
        {
            copy.ScreenshotPath = source.ScreenshotPath;
        }
    }

    private static Rectangle GetVirtualScreenBounds()
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new Rectangle(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    private void DeleteButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null || _recorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"マクロ '{macro.Name}' を削除しますか？",
            "削除確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        TryDeleteFile(ResolveScreenshotPath(macro));
        _macros.Remove(macro);
        RefreshMacroList();
        RefreshHotkeys();
        AutoSaveMacros();
        SetStatus("マクロを削除しました。");
    }

    private void DuplicateButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null || _recorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        var copy = CloneMacro(macro);
        copy.Id = Guid.NewGuid();
        copy.Name = CreateCopyName(macro.Name);
        copy.IsEnabled = false;
        CopyScreenshotForDuplicate(macro, copy);
        _macros.Add(copy);
        RefreshMacroList(copy.Id);
        RefreshSummary(copy);
        RefreshHotkeys();
        AutoSaveMacros();
        SetStatus("マクロを複製しました。複製したマクロは無効状態です。");
    }

    private void ShowMacroStorageLocationOnClick(object? sender, EventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_macroStorePath) ?? MacroStore.DefaultDirectory;
            Directory.CreateDirectory(directory);
            var argument = File.Exists(_macroStorePath)
                ? $"/select,\"{_macroStorePath}\""
                : $"\"{directory}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = argument,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"保存場所を開けません: {ex.Message}");
        }
    }

    private void ChangeMacroStorageLocationOnClick(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "マクロファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            DefaultExt = "json",
            FileName = Path.GetFileName(_macroStorePath),
            InitialDirectory = Path.GetDirectoryName(_macroStorePath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var selectedPath = Path.GetFullPath(dialog.FileName);
        if (string.Equals(selectedPath, _macroStorePath, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"現在の保存場所: {_macroStorePath}");
            return;
        }

        try
        {
            if (File.Exists(selectedPath))
            {
                var result = MessageBox.Show(
                    this,
                    "選択したファイルは既に存在します。\r\n\r\nはい: そのファイルを読み込んで保存場所にする\r\nいいえ: 現在のマクロをそのファイルへ保存して保存場所にする",
                    "マクロ保存場所の変更",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (result == DialogResult.Cancel)
                {
                    return;
                }

                if (result == DialogResult.Yes)
                {
                    var loaded = MacroStore.Load(selectedPath);
                    SetMacroStorePath(selectedPath);
                    _macros.Clear();
                    _macros.AddRange(loaded);
                    var disabledDuplicates = DisableDuplicateEnabledHotkeys();
                    RefreshMacroList();
                    RefreshHotkeys();
                    if (disabledDuplicates)
                    {
                        AutoSaveMacros();
                    }

                    SetStatus(disabledDuplicates
                        ? $"保存場所を変更し、読み込みました。ショートカット重複のため一部マクロを無効化しました: {_macroStorePath}"
                        : $"保存場所を変更し、読み込みました: {_macroStorePath}");
                    return;
                }

                MigrateScreenshotsForStorePath(selectedPath);
                SetMacroStorePath(selectedPath);
                MacroStore.Save(_macroStorePath, _macros);
                SetStatus($"保存場所を変更し、現在のマクロを保存しました: {_macroStorePath}");
                return;
            }

            MigrateScreenshotsForStorePath(selectedPath);
            SetMacroStorePath(selectedPath);
            MacroStore.Save(_macroStorePath, _macros);
            SetStatus($"保存場所を変更しました: {_macroStorePath}");
        }
        catch (Exception ex)
        {
            SetStatus($"保存場所の変更に失敗: {ex.Message}");
            MessageBox.Show(this, ex.Message, "保存場所の変更", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopCurrentWork()
    {
        if (_countdownCts is not null)
        {
            _countdownCts.Cancel();
            RestoreAfterRecording();
            return;
        }

        if (_recorder.IsRecording)
        {
            var events = _recorder.Stop();
            RemoveTrailingRecordingStopHotkeyEvents(events);
            RemoveTrailingToolWindowMouseEvents(events);
            if (events.Count > 0)
            {
                var options = GetRecordingOptions();
                var noise = GetNoiseSettings();
                if (options.TimingMode == RecordingTimingMode.FixedEventInterval)
                {
                    events = MacroTimingNormalizer.NormalizeFixedEventIntervals(events, options);
                }

                var macro = new Macro
                {
                    Name = string.IsNullOrWhiteSpace(_nameBox.Text)
                        ? $"マクロ {DateTime.Now:yyyyMMdd HHmmss}"
                        : _nameBox.Text.Trim(),
                    PlaybackSpeedPercent = (int)_playbackSpeedBox.Value,
                    Recording = options,
                    Noise = noise,
                    Events = events
                };
                ApplyPendingScreenshot(macro);
                _macros.Add(macro);
                RefreshMacroList(macro.Id);
                AutoSaveMacros();
                SetStatus($"{events.Count} 件のイベントを記録しました。");
            }
            else
            {
                DeletePendingScreenshot();
                SetStatus("記録を停止しました。イベントはありません。");
            }

            RefreshHotkeys();
            HideRecordingOverlay();
            RestoreAfterRecording();
        }
        else if (_motionLearningRecorder.IsRecording)
        {
            StopMotionLearning();
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

    private void RemoveTrailingRecordingStopHotkeyEvents(List<MacroEvent> events)
    {
        if (events.Count == 0 || _recordingStopHotkey.IsEmpty)
        {
            return;
        }

        var hotkeyKeys = GetGestureKeys(_recordingStopHotkey);
        var lastTime = events[^1].TimeOffsetMs;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var macroEvent = events[i];
            if (lastTime - macroEvent.TimeOffsetMs > 1200)
            {
                break;
            }

            if (macroEvent.Kind is (MacroEventKind.KeyDown or MacroEventKind.KeyUp)
                && hotkeyKeys.Contains(macroEvent.KeyCode))
            {
                events.RemoveAt(i);
            }
        }
    }

    private static HashSet<Keys> GetGestureKeys(HotkeyGesture gesture)
    {
        var keys = new HashSet<Keys> { gesture.Key };
        if (gesture.Ctrl)
        {
            keys.Add(Keys.ControlKey);
            keys.Add(Keys.LControlKey);
            keys.Add(Keys.RControlKey);
        }

        if (gesture.Alt)
        {
            keys.Add(Keys.Menu);
            keys.Add(Keys.LMenu);
            keys.Add(Keys.RMenu);
        }

        if (gesture.Shift)
        {
            keys.Add(Keys.ShiftKey);
            keys.Add(Keys.LShiftKey);
            keys.Add(Keys.RShiftKey);
        }

        if (gesture.Win)
        {
            keys.Add(Keys.LWin);
            keys.Add(Keys.RWin);
        }

        return keys;
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
        if (_recorder.IsRecording || _motionLearningRecorder.IsRecording || IsCountingDown)
        {
            return;
        }

        UpdateButtons();
        var noise = GetNoiseSettings(macro);
        try
        {
            await _player.PlayAsync(
                macro,
                noise,
                macro.PlaybackSpeedPercent,
                _motionProfile,
                Screen.FromControl(this),
                SetStatus);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private void MacroListOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        _updatingSelection = true;
        _nameBox.Text = macro?.Name ?? "";
        _hotkeyBox.Text = macro?.Hotkey.ToString() ?? "";
        _playbackSpeedBox.Value = Math.Clamp(
            macro?.PlaybackSpeedPercent ?? 100,
            (int)_playbackSpeedBox.Minimum,
            (int)_playbackSpeedBox.Maximum);
        SetRecordingControls(macro?.Recording ?? RecordingOptions.Standard());
        SetNoiseControls(macro?.Noise ?? new NoiseSettings());
        _updatingSelection = false;
        RefreshSummary(macro);
        UpdateButtons();
    }

    private void MacroListOnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(UiPanelBack);
        using var border = new Pen(UiBorder);
        e.Graphics.FillRectangle(back, e.Bounds);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);

        var textRect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? "",
            _macroList.Font,
            textRect,
            UiText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void MacroListOnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var selected = e.Item?.Selected ?? false;
        var macro = e.Item?.Tag as Macro;
        var enabled = macro?.IsEnabled ?? true;
        using var back = new SolidBrush(selected ? UiSelectionBack : UiPanelBack);
        e.Graphics.FillRectangle(back, e.Bounds);

        if (e.ColumnIndex == 4)
        {
            DrawMacroEnabledCheck(e.Graphics, e.Bounds, selected, enabled);
            return;
        }

        var textColor = selected ? UiSelectionText : enabled ? UiText : UiMutedText;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        flags |= e.ColumnIndex >= 2 ? TextFormatFlags.Right : TextFormatFlags.Left;
        var textRect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", _macroList.Font, textRect, textColor, flags);
    }

    private void DrawMacroEnabledCheck(Graphics graphics, Rectangle bounds, bool selected, bool enabled)
    {
        var boxSize = 14;
        var box = new Rectangle(
            bounds.Left + (bounds.Width - boxSize) / 2,
            bounds.Top + (bounds.Height - boxSize) / 2,
            boxSize,
            boxSize);
        using var border = new Pen(enabled ? UiPrimary : UiBorder);
        using var fill = new SolidBrush(enabled ? UiPrimary : selected ? UiSelectionBack : UiPanelBack);
        graphics.FillRectangle(fill, box);
        graphics.DrawRectangle(border, box);

        if (!enabled)
        {
            return;
        }

        using var check = new Pen(Color.White, 2F)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        graphics.DrawLines(check, new[]
        {
            new Point(box.Left + 3, box.Top + 7),
            new Point(box.Left + 6, box.Top + 10),
            new Point(box.Right - 3, box.Top + 4)
        });
    }

    private void MacroListOnColumnWidthChanging(object? sender, ColumnWidthChangingEventArgs e)
    {
        if (_updatingMacroListColumns)
        {
            return;
        }

        e.Cancel = true;
        e.NewWidth = e.ColumnIndex >= 0 && e.ColumnIndex < _macroList.Columns.Count
            ? _macroList.Columns[e.ColumnIndex].Width
            : e.NewWidth;
    }

    private void UpdateMacroListColumnWidths()
    {
        if (_macroList is null || _macroList.Columns.Count < 5)
        {
            return;
        }

        var countWidth = 42;
        var enabledWidth = 30;
        var availableWidth = Math.Max(0, _macroList.ClientSize.Width - 5);
        var shortcutWidth = availableWidth >= 380 ? 92 : 78;
        var timeWidth = availableWidth >= 380 ? 78 : 68;
        var nameWidth = availableWidth - shortcutWidth - countWidth - timeWidth - enabledWidth;
        if (nameWidth < 90)
        {
            var shortage = 90 - nameWidth;
            var shortcutReduction = Math.Min(shortage, Math.Max(0, shortcutWidth - 58));
            shortcutWidth -= shortcutReduction;
            shortage -= shortcutReduction;
            var timeReduction = Math.Min(shortage, Math.Max(0, timeWidth - 58));
            timeWidth -= timeReduction;
            shortage -= timeReduction;
            nameWidth = Math.Max(60, 90 - shortage);
        }

        var overflow = nameWidth + shortcutWidth + countWidth + timeWidth + enabledWidth - availableWidth;
        if (overflow > 0)
        {
            var nameReduction = Math.Min(overflow, Math.Max(0, nameWidth - 30));
            nameWidth -= nameReduction;
            overflow -= nameReduction;
            var shortcutReduction = Math.Min(overflow, Math.Max(0, shortcutWidth - 1));
            shortcutWidth -= shortcutReduction;
            overflow -= shortcutReduction;
            var timeReduction = Math.Min(overflow, Math.Max(0, timeWidth - 40));
            timeWidth -= timeReduction;
            overflow -= timeReduction;
            var countReduction = Math.Min(overflow, Math.Max(0, countWidth - 25));
            countWidth -= countReduction;
            overflow -= countReduction;
            var enabledReduction = Math.Min(overflow, Math.Max(0, enabledWidth - 24));
            enabledWidth -= enabledReduction;
            overflow -= enabledReduction;
            timeWidth = Math.Max(0, timeWidth - overflow);
        }

        _updatingMacroListColumns = true;
        try
        {
            _macroList.BeginUpdate();
            _macroList.Columns[0].Width = nameWidth;
            _macroList.Columns[1].Width = shortcutWidth;
            _macroList.Columns[2].Width = countWidth;
            _macroList.Columns[3].Width = timeWidth;
            _macroList.Columns[4].Width = enabledWidth;
            _macroList.EndUpdate();
        }
        finally
        {
            _updatingMacroListColumns = false;
        }
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
        AutoSaveMacros();
    }

    private void MacroListOnMouseClick(object? sender, MouseEventArgs e)
    {
        var hit = _macroList.HitTest(e.Location);
        if (hit.Item?.Tag is not Macro macro || hit.Item.SubItems.IndexOf(hit.SubItem) != 4)
        {
            return;
        }

        ToggleMacroEnabled(macro);
    }

    private void MacroListOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete)
        {
            e.SuppressKeyPress = true;
            DeleteButtonOnClick(sender, EventArgs.Empty);
            return;
        }

        if (e.KeyCode != Keys.Space)
        {
            return;
        }

        var macro = GetSelectedMacro();
        if (macro is null)
        {
            return;
        }

        e.SuppressKeyPress = true;
        ToggleMacroEnabled(macro);
    }

    private void ToggleMacroEnabled(Macro macro)
    {
        var conflicts = GetEnabledHotkeyConflicts(macro, macro.Hotkey);
        if (!macro.IsEnabled && conflicts.Count > 0)
        {
            var conflictNames = string.Join(", ", conflicts.Select(item => item.Name));
            var result = MessageBox.Show(
                this,
                $"同じショートカットを使っている有効マクロがあります。\r\n\r\n既存: {conflictNames}\r\n\r\n既存マクロを無効化して、このマクロを有効化しますか？",
                "ショートカット重複",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                SetStatus("マクロの有効化を取り消しました。");
                RefreshMacroList(macro.Id);
                return;
            }

            foreach (var conflict in conflicts)
            {
                conflict.IsEnabled = false;
            }
        }

        macro.IsEnabled = !macro.IsEnabled;
        RefreshMacroList(macro.Id);
        RefreshSummary(macro);
        RefreshHotkeys();
        AutoSaveMacros();
    }

    private void PlaybackSpeedBoxOnValueChanged(object? sender, EventArgs e)
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

        macro.PlaybackSpeedPercent = (int)_playbackSpeedBox.Value;
        RefreshSummary(macro);
        AutoSaveMacros();
    }

    private void MacroHotkeySetButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null)
        {
            return;
        }

        var captured = CaptureHotkey("ショートカット設定", "このマクロを再生するキーを押してください。", macro.Hotkey);
        if (captured is null)
        {
            return;
        }

        var previousHotkey = macro.Hotkey;
        var newHotkey = captured;
        var conflicts = GetEnabledHotkeyConflicts(macro, newHotkey);
        if (macro.IsEnabled && conflicts.Count > 0)
        {
            var conflictNames = string.Join(", ", conflicts.Select(item => item.Name));
            var result = MessageBox.Show(
                this,
                $"同じショートカットを使っている有効マクロがあります。\r\n\r\n既存: {conflictNames}\r\n\r\n既存マクロを無効化して登録しますか？",
                "ショートカット重複",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                macro.Hotkey = previousHotkey;
                _hotkeyBox.Text = macro.Hotkey.ToString();
                SetStatus("ショートカット登録を取り消しました。");
                return;
            }

            foreach (var conflict in conflicts)
            {
                conflict.IsEnabled = false;
            }
        }

        macro.Hotkey = newHotkey;
        _hotkeyBox.Text = macro.Hotkey.ToString();
        RefreshMacroList(macro.Id);
        RefreshSummary(macro);
        RefreshHotkeys();
        AutoSaveMacros();
    }

    private void EmergencyHotkeySetButtonOnClick(object? sender, EventArgs e)
    {
        var captured = CaptureHotkey("緊急停止キー設定", "緊急停止に使うキーを押してください。", _emergencyStopHotkey);
        if (captured is null)
        {
            return;
        }

        _emergencyStopHotkey = captured.IsEmpty ? CreateDefaultEmergencyHotkey() : captured;
        _emergencyHotkeyBox.Text = _emergencyStopHotkey.ToString();
        RefreshHotkeys();
        AutoSaveMacros();
    }

    private void RecordingStopHotkeySetButtonOnClick(object? sender, EventArgs e)
    {
        var captured = CaptureHotkey("記録停止キー設定", "記録停止に使うキーを押してください。", _recordingStopHotkey);
        if (captured is null)
        {
            return;
        }

        _recordingStopHotkey = captured.IsEmpty ? CreateDefaultRecordingStopHotkey() : captured;
        _recordingStopHotkeyBox.Text = _recordingStopHotkey.ToString();
        RefreshHotkeys();
        AutoSaveMacros();
    }

    private HotkeyGesture? CaptureHotkey(string title, string instruction, HotkeyGesture current)
    {
        _hotkeys.UnregisterAll();
        try
        {
            using var dialog = new HotkeyCaptureDialog(title, instruction, current);
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.CapturedHotkey : null;
        }
        finally
        {
            RefreshHotkeys();
        }
    }

    private void LoadDefaultMacros()
    {
        try
        {
            _macros.Clear();
            var disabledDuplicates = false;
            if (File.Exists(_macroStorePath))
            {
                _macros.AddRange(MacroStore.Load(_macroStorePath));
                disabledDuplicates = DisableDuplicateEnabledHotkeys();
                if (disabledDuplicates)
                {
                    AutoSaveMacros();
                }
            }

            RefreshMacroList();
            SetStatus(disabledDuplicates
                ? $"自動読み込み: {_macros.Count} 件 / 重複ショートカットを無効化 / {_macroStorePath}"
                : $"自動読み込み: {_macros.Count} 件 / {_macroStorePath}");
        }
        catch (Exception ex)
        {
            SetStatus($"自動読み込み失敗: {ex.Message}");
        }
    }

    private void AutoSaveMacros()
    {
        try
        {
            MacroStore.Save(_macroStorePath, _macros);
        }
        catch (Exception ex)
        {
            SetStatus($"自動保存失敗: {ex.Message}");
        }
    }

    private void SetMacroStorePath(string path)
    {
        _macroStorePath = Path.GetFullPath(path);
        _settings.MacroFilePath = IsDefaultMacroStorePath(_macroStorePath) ? null : _macroStorePath;
        _motionProfile = HumanMotionStore.Load(_macroStorePath);
        AppSettingsStore.Save(_settings);
        UpdateMotionLearningMenu();
    }

    private static string ResolveMacroStorePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return MacroStore.DefaultPath;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return MacroStore.DefaultPath;
        }
    }

    private static bool IsDefaultMacroStorePath(string path)
    {
        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(MacroStore.DefaultPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private RecordingOptions GetRecordingOptions()
    {
        var options = _densityBox.SelectedIndex switch
        {
            0 => RecordingOptions.Lightweight(),
            2 => RecordingOptions.HighPrecision(),
            _ => RecordingOptions.Standard()
        };
        options.TimingMode = _recordingModeBox.SelectedIndex == 1
            ? RecordingTimingMode.FixedEventInterval
            : RecordingTimingMode.Complete;
        options.EventIntervalMs = (int)_eventIntervalBox.Value;
        options.HoldDurationMs = (int)Math.Min(_holdDurationBox.Value, _eventIntervalBox.Value);
        return options;
    }

    private static RecordingOptions CreateMotionLearningOptions()
    {
        return new RecordingOptions
        {
            Name = "軌道学習",
            MousePollingRateHz = 200,
            MoveMinDistancePx = 1,
            DragMoveMinDistancePx = 1,
            TimingMode = RecordingTimingMode.Complete
        };
    }

    private NoiseSettings GetNoiseSettings(Macro? macro = null)
    {
        return macro?.Noise ?? new NoiseSettings
        {
            CoordinateJitterPx = (int)_coordNoiseBox.Value,
            TimeJitterPercent = _recordingModeBox.SelectedIndex == 1 ? 0 : (int)_timeNoiseBox.Value,
            TimeJitterMs = _recordingModeBox.SelectedIndex == 1 ? (int)_timeNoiseBox.Value : 20,
            AccelerationJitterPercent = (int)_accelNoiseBox.Value,
            TrajectoryJitterPx = (int)_trajectoryNoiseBox.Value
        };
    }

    private void SetNoiseControls(NoiseSettings noise)
    {
        _coordNoiseBox.Value = Math.Clamp(noise.CoordinateJitterPx, (int)_coordNoiseBox.Minimum, (int)_coordNoiseBox.Maximum);
        var macro = GetSelectedMacro();
        var timeValue = macro?.Recording?.TimingMode == RecordingTimingMode.FixedEventInterval
            ? noise.TimeJitterMs
            : noise.TimeJitterPercent;
        _timeNoiseBox.Value = Math.Clamp(timeValue, (int)_timeNoiseBox.Minimum, (int)_timeNoiseBox.Maximum);
        _accelNoiseBox.Value = Math.Clamp(noise.AccelerationJitterPercent, (int)_accelNoiseBox.Minimum, (int)_accelNoiseBox.Maximum);
        _trajectoryNoiseBox.Value = Math.Clamp(noise.TrajectoryJitterPx, (int)_trajectoryNoiseBox.Minimum, (int)_trajectoryNoiseBox.Maximum);
    }

    private void SetRecordingControls(RecordingOptions recording)
    {
        _recordingModeBox.SelectedIndex = recording.TimingMode == RecordingTimingMode.FixedEventInterval ? 1 : 0;
        _densityBox.SelectedIndex = recording.MousePollingRateHz switch
        {
            <= 60 => 0,
            >= 1000 => 2,
            _ => 1
        };
        _eventIntervalBox.Value = Math.Clamp(recording.EventIntervalMs, (int)_eventIntervalBox.Minimum, (int)_eventIntervalBox.Maximum);
        _holdDurationBox.Value = Math.Clamp(recording.HoldDurationMs, (int)_holdDurationBox.Minimum, (int)_holdDurationBox.Maximum);
    }

    private void RecordingModeOnChanged()
    {
        var fixedIntervalMode = _recordingModeBox.SelectedIndex == 1;
        _eventIntervalBox.Enabled = fixedIntervalMode;
        _holdDurationBox.Enabled = fixedIntervalMode;
        _timeNoiseLabel.Text = fixedIntervalMode ? "時間(ms)" : "時間(%)";
        _timeNoiseBox.Maximum = fixedIntervalMode ? 500 : 50;
        _timeNoiseBox.Increment = fixedIntervalMode ? 5 : 1;
        _timeNoiseBox.Value = fixedIntervalMode
            ? Math.Clamp(_timeNoiseBox.Value == 5 ? 20 : _timeNoiseBox.Value, _timeNoiseBox.Minimum, _timeNoiseBox.Maximum)
            : Math.Clamp(_timeNoiseBox.Value > 50 ? 5 : _timeNoiseBox.Value, _timeNoiseBox.Minimum, _timeNoiseBox.Maximum);
        _toolTip.SetToolTip(_timeNoiseBox, fixedIntervalMode
            ? "イベント間隔一定で使う時間揺れです。指定間隔に対して±msで変動します。"
            : "クリックやキー入力の間隔に加える時間揺れです。機械的な一定間隔を避けます。");
    }

    private void WireNoiseSettingChanges()
    {
        _coordNoiseBox.ValueChanged += NoiseSettingOnValueChanged;
        _timeNoiseBox.ValueChanged += NoiseSettingOnValueChanged;
        _accelNoiseBox.ValueChanged += NoiseSettingOnValueChanged;
        _trajectoryNoiseBox.ValueChanged += NoiseSettingOnValueChanged;
    }

    private void NoiseSettingOnValueChanged(object? sender, EventArgs e)
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

        macro.Noise = GetNoiseSettings();
        AutoSaveMacros();
    }

    private Macro? GetSelectedMacro()
    {
        if (_macroList.SelectedItems.Count == 0)
        {
            return null;
        }

        return _macroList.SelectedItems[0].Tag as Macro;
    }

    private static Macro CloneMacro(Macro source)
    {
        return new Macro
        {
            Id = source.Id,
            Name = source.Name,
            IsEnabled = source.IsEnabled,
            PlaybackSpeedPercent = source.PlaybackSpeedPercent,
            TrimStartMs = source.TrimStartMs,
            TrimEndMs = source.TrimEndMs,
            ScreenshotPath = source.ScreenshotPath,
            ScreenshotX = source.ScreenshotX,
            ScreenshotY = source.ScreenshotY,
            ScreenshotWidth = source.ScreenshotWidth,
            ScreenshotHeight = source.ScreenshotHeight,
            Hotkey = CloneHotkey(source.Hotkey),
            Recording = CloneRecording(source.Recording),
            Noise = CloneNoise(source.Noise),
            Events = source.Events.Select(CloneEvent).ToList()
        };
    }

    private string CreateCopyName(string originalName)
    {
        var baseName = $"{originalName} コピー";
        var name = baseName;
        var index = 2;
        while (_macros.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {index}";
            index++;
        }

        return name;
    }

    private static HotkeyGesture CloneHotkey(HotkeyGesture source)
    {
        return new HotkeyGesture
        {
            Ctrl = source.Ctrl,
            Alt = source.Alt,
            Shift = source.Shift,
            Win = source.Win,
            Key = source.Key
        };
    }

    private static RecordingOptions CloneRecording(RecordingOptions source)
    {
        return new RecordingOptions
        {
            Name = source.Name,
            MousePollingRateHz = source.MousePollingRateHz,
            MoveMinDistancePx = source.MoveMinDistancePx,
            DragMoveMinDistancePx = source.DragMoveMinDistancePx,
            TimingMode = source.TimingMode,
            EventIntervalMs = source.EventIntervalMs,
            HoldDurationMs = source.HoldDurationMs
        };
    }

    private static NoiseSettings CloneNoise(NoiseSettings source)
    {
        return new NoiseSettings
        {
            CoordinateJitterPx = source.CoordinateJitterPx,
            TimeJitterPercent = source.TimeJitterPercent,
            TimeJitterMs = source.TimeJitterMs,
            AccelerationJitterPercent = source.AccelerationJitterPercent,
            TrajectoryJitterPx = source.TrajectoryJitterPx
        };
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

    private void RefreshMacroList(Guid? selectedId = null)
    {
        _macroList.BeginUpdate();
        _macroList.Items.Clear();
        foreach (var macro in _macros)
        {
            var playbackEvents = macro.GetPlaybackEvents();
            var item = new ListViewItem(macro.Name);
            item.SubItems.Add(macro.Hotkey.ToString());
            item.SubItems.Add(playbackEvents.Count.ToString());
            item.SubItems.Add($"{macro.DurationMs} ms");
            item.SubItems.Add(macro.IsEnabled ? "有効" : "無効");
            item.Tag = macro;
            _macroList.Items.Add(item);
            if (selectedId == macro.Id)
            {
                item.Selected = true;
            }
        }

        _macroList.EndUpdate();
        UpdateMacroListColumnWidths();
        if (_macroList.SelectedItems.Count == 0 && _macroList.Items.Count > 0 && selectedId is null)
        {
            _macroList.Items[0].Selected = true;
        }
    }

    private void RefreshSummary(Macro? macro)
    {
        if (macro is null)
        {
            _summaryLabel.Text = "マクロが選択されていません。";
            return;
        }

        _summaryLabel.Text =
            $"名前: {macro.Name}\r\n" +
            $"状態: {(macro.IsEnabled ? "有効" : "無効")}\r\n" +
            $"イベント数: {macro.GetPlaybackEvents().Count} / 元 {macro.Events.Count}\r\n" +
            $"時間: {macro.DurationMs} ms\r\n" +
            $"再生速度: {macro.PlaybackSpeedPercent}%\r\n" +
            $"記録方法: {GetRecordingModeText(macro.Recording)}";
    }

    private static string GetRecordingModeText(RecordingOptions recording)
    {
        return recording.TimingMode == RecordingTimingMode.FixedEventInterval
            ? $"イベント間隔一定 / {recording.EventIntervalMs} ms / 押下 {recording.HoldDurationMs} ms"
            : "完全記録";
    }

    private void RefreshHotkeys()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            _hotkeys.RegisterAll(Handle, _macros, _emergencyStopHotkey, _recordingStopHotkey);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private bool DisableDuplicateEnabledHotkeys()
    {
        var seen = new List<Macro>();
        var changed = false;
        foreach (var macro in _macros)
        {
            if (!macro.IsEnabled || macro.Hotkey.IsEmpty)
            {
                continue;
            }

            if (seen.Any(item => AreSameHotkey(item.Hotkey, macro.Hotkey)))
            {
                macro.IsEnabled = false;
                changed = true;
                continue;
            }

            seen.Add(macro);
        }

        return changed;
    }

    private List<Macro> GetEnabledHotkeyConflicts(Macro target, HotkeyGesture hotkey)
    {
        return _macros.Where(item =>
            item.Id != target.Id
            && item.IsEnabled
            && AreSameHotkey(item.Hotkey, hotkey))
            .ToList();
    }

    private static bool AreSameHotkey(HotkeyGesture left, HotkeyGesture right)
    {
        return !left.IsEmpty
            && !right.IsEmpty
            && left.Ctrl == right.Ctrl
            && left.Alt == right.Alt
            && left.Shift == right.Shift
            && left.Win == right.Win
            && left.Key == right.Key;
    }

    private void UpdateButtons()
    {
        var recording = _recorder.IsRecording;
        var learning = _motionLearningRecorder.IsRecording;
        var playing = _player.IsPlaying;
        var countingDown = IsCountingDown;
        var selected = GetSelectedMacro() is not null;

        var busy = recording || learning || playing || countingDown;
        _recordButton.Enabled = !busy;
        _stopButton.Enabled = recording || learning || playing || countingDown;
        _playButton.Enabled = selected && !busy;
        _editButton.Enabled = selected && !busy;
        _previewButton.Enabled = selected && !busy;
        _duplicateButton.Enabled = selected && !busy;
        _deleteButton.Enabled = selected && !busy;
        _hotkeySetButton.Enabled = selected && !busy;
        _emergencyHotkeySetButton.Enabled = !busy;
        _recordingStopHotkeySetButton.Enabled = !busy;
        UpdateMotionLearningMenu();
    }

    private void UpdateMotionLearningMenu()
    {
        if (_learningStartItem is null)
        {
            return;
        }

        var recording = _recorder.IsRecording;
        var learning = _motionLearningRecorder.IsRecording;
        var playing = _player.IsPlaying;
        var countingDown = IsCountingDown;
        _learningStartItem.Enabled = !recording && !learning && !playing && !countingDown;
        _learningStopItem.Enabled = learning;
        _learningClearItem.Enabled = !learning && _motionProfile.TotalSampleCount > 0;
        _learningCountItem.Text = $"サンプル: {_motionProfile.TotalSampleCount} 件";
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

    private static HotkeyGesture CreateDefaultEmergencyHotkey()
    {
        return new HotkeyGesture
        {
            Ctrl = true,
            Alt = true,
            Key = Keys.Pause
        };
    }

    private static HotkeyGesture CreateDefaultRecordingStopHotkey()
    {
        return new HotkeyGesture
        {
            Ctrl = true,
            Alt = true,
            Key = Keys.End
        };
    }

    private sealed class SectionGroupBox : GroupBox
    {
        public SectionGroupBox()
        {
            SetStyle(ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            var borderRect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var borderPen = new Pen(UiBorder);
            e.Graphics.DrawRectangle(borderPen, borderRect);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                new Point(14, 6),
                UiText,
                TextFormatFlags.NoPadding);
        }
    }

    private sealed class ScrollFriendlyNumericUpDown : NumericUpDown
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_MOUSEWHEEL)
            {
                ScrollNearestParent(this, GetWheelDelta(m.WParam));
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollNearestParent(this, e.Delta);
        }
    }

    private sealed class ScrollFriendlyComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_MOUSEWHEEL && !DroppedDown)
            {
                ScrollNearestParent(this, GetWheelDelta(m.WParam));
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (DroppedDown)
            {
                base.OnMouseWheel(e);
                return;
            }

            ScrollNearestParent(this, e.Delta);
        }
    }

    private static int GetWheelDelta(IntPtr wParam)
    {
        return unchecked((short)(((long)wParam >> 16) & 0xFFFF));
    }

    private static void ScrollNearestParent(Control source, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        var parent = source.Parent;
        while (parent is not null)
        {
            if (parent is ScrollableControl { AutoScroll: true } scrollable
                && scrollable.VerticalScroll.Visible)
            {
                var wheelSteps = Math.Max(1, Math.Abs(delta) / SystemInformation.MouseWheelScrollDelta);
                var wheelLines = SystemInformation.MouseWheelScrollLines <= 0
                    ? 3
                    : SystemInformation.MouseWheelScrollLines;
                var distance = wheelSteps * wheelLines * Math.Max(1, scrollable.VerticalScroll.SmallChange);
                var nextValue = scrollable.VerticalScroll.Value + (delta > 0 ? -distance : distance);
                var maxValue = Math.Max(
                    scrollable.VerticalScroll.Minimum,
                    scrollable.VerticalScroll.Maximum - scrollable.VerticalScroll.LargeChange + 1);
                scrollable.VerticalScroll.Value = Math.Clamp(
                    nextValue,
                    scrollable.VerticalScroll.Minimum,
                    maxValue);
                scrollable.PerformLayout();
                return;
            }

            parent = parent.Parent;
        }
    }

    private sealed class FixedColumnListView : ListView
    {
        private const int WM_SETCURSOR = 0x0020;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is WM_SETCURSOR or WM_LBUTTONDOWN or WM_LBUTTONDBLCLK
                && IsOnColumnDivider(PointToClient(Cursor.Position)))
            {
                return;
            }

            base.WndProc(ref m);
        }

        private bool IsOnColumnDivider(Point point)
        {
            if (View != View.Details || Columns.Count == 0 || point.Y > 28)
            {
                return false;
            }

            var x = 0;
            foreach (ColumnHeader column in Columns)
            {
                x += column.Width;
                if (Math.Abs(point.X - x) <= 4)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class HotkeyCaptureDialog : Form
    {
        private readonly Label _currentLabel = new();

        public HotkeyCaptureDialog(string title, string instruction, HotkeyGesture current)
        {
            CapturedHotkey = CloneHotkey(current);
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            ClientSize = new Size(420, 170);
            Font = new Font(UiFontName, 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = UiPanelBack;
            ForeColor = UiText;

            Controls.Add(new Label
            {
                Text = title,
                Location = new Point(18, 16),
                Size = new Size(380, 24),
                Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
                ForeColor = UiText,
                BackColor = Color.Transparent
            });
            Controls.Add(new Label
            {
                Text = instruction + "\r\nBackspace/Delete: 解除   Esc: キャンセル",
                Location = new Point(18, 48),
                Size = new Size(380, 42),
                ForeColor = UiMutedText,
                BackColor = Color.Transparent
            });

            _currentLabel.Location = new Point(18, 102);
            _currentLabel.Size = new Size(380, 24);
            _currentLabel.ForeColor = UiText;
            _currentLabel.BackColor = Color.Transparent;
            Controls.Add(_currentLabel);

            var cancelButton = CreateInlineButton("キャンセル", (_, _) => DialogResult = DialogResult.Cancel);
            cancelButton.Location = new Point(ClientSize.Width - 104, ClientSize.Height - 38);
            cancelButton.Size = new Size(86, 26);
            cancelButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(cancelButton);

            UpdateCurrentLabel();
        }

        public HotkeyGesture CapturedHotkey { get; private set; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return;
            }

            if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
            {
                return;
            }

            if (e.KeyCode is Keys.Back or Keys.Delete)
            {
                CapturedHotkey = new HotkeyGesture();
                DialogResult = DialogResult.OK;
                return;
            }

            CapturedHotkey = new HotkeyGesture
            {
                Ctrl = e.Control,
                Alt = e.Alt,
                Shift = e.Shift,
                Key = e.KeyCode
            };
            DialogResult = DialogResult.OK;
        }

        private void UpdateCurrentLabel()
        {
            var text = CapturedHotkey.IsEmpty ? "現在: 未設定" : $"現在: {CapturedHotkey}";
            _currentLabel.Text = text;
        }
    }

    private sealed record PendingScreenshot(string Path, Rectangle Bounds);

    private sealed class EmergencyStopOverlay : Control
    {
        private readonly System.Windows.Forms.Timer _timer = new();
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private readonly Font _font = new("Yu Gothic UI", 34F, FontStyle.Bold, GraphicsUnit.Point);

        public EmergencyStopOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Enabled = false;
            _timer.Interval = 16;
            _timer.Tick += (_, _) =>
            {
                if (_clock.ElapsedMilliseconds >= 1600)
                {
                    _timer.Stop();
                    Visible = false;
                    return;
                }

                Invalidate();
            };
        }

        public void ShowMessage()
        {
            Visible = true;
            BringToFront();
            _clock.Restart();
            _timer.Start();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var progress = Math.Clamp(_clock.ElapsedMilliseconds / 1600.0, 0.0, 1.0);
            var alpha = progress < 0.18
                ? (int)Math.Round(255 * (progress / 0.18))
                : (int)Math.Round(255 * Math.Max(0.0, 1.0 - (progress - 0.18) / 0.82));
            using var brush = new SolidBrush(Color.FromArgb(alpha, Color.Red));
            using var outline = new SolidBrush(Color.FromArgb(Math.Min(210, alpha), Color.Black));
            const string text = "緊急停止しました";
            var size = e.Graphics.MeasureString(text, _font);
            var x = (Width - size.Width) / 2F;
            var y = (Height - size.Height) / 2F;
            e.Graphics.DrawString(text, _font, outline, x + 2, y + 2);
            e.Graphics.DrawString(text, _font, brush, x, y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _font.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
