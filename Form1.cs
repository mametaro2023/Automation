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
    private Button _previewButton = null!;
    private ListView _macroList = null!;
    private Label _summaryLabel = null!;
    private MenuStrip _menu = null!;
    private ToolStripMenuItem _systemThemeItem = null!;
    private ToolStripMenuItem _lightThemeItem = null!;
    private ToolStripMenuItem _darkThemeItem = null!;
    private TextBox _nameBox = null!;
    private TextBox _hotkeyBox = null!;
    private TextBox _emergencyHotkeyBox = null!;
    private TextBox _recordingStopHotkeyBox = null!;
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
    private ToolStripStatusLabel _statusLabel = null!;
    private SplitContainer _mainSplit = null!;
    private Control _advancedSettingsPanel = null!;
    private RowStyle _advancedSettingsRow = null!;
    private AppThemeMode _themeMode;
    private bool _darkThemeActive;
    private CancellationTokenSource? _countdownCts;
    private HotkeyGesture _emergencyStopHotkey = CreateDefaultEmergencyHotkey();
    private HotkeyGesture _recordingStopHotkey = CreateDefaultRecordingStopHotkey();
    private FormWindowState _windowStateBeforeRecording = FormWindowState.Normal;
    private bool _restoreWindowAfterRecording;
    private bool _updatingSelection;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPreferredAppModeDelegate(int appMode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushMenuThemesDelegate();

    public Form1()
    {
        InitializeComponent();
        _themeMode = AppSettingsStore.Load().ThemeMode;
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
        fileMenu.DropDownItems.Add("読込...", null, LoadButtonOnClick);
        fileMenu.DropDownItems.Add("出力...", null, SaveButtonOnClick);
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
        _menu.Items.Add(fileMenu);
        _menu.Items.Add(viewMenu);
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
        _deleteButton = CreateButton("削除", DeleteButtonOnClick, ButtonTone.Danger);
        topPanel.Controls.AddRange(new Control[]
        {
            _recordButton,
            _stopButton,
            _playButton,
            _previewButton,
            _editButton,
            _deleteButton
        });

        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 120,
            Panel2MinSize = 120,
            BorderStyle = BorderStyle.None,
            BackColor = UiBorder
        };
        root.Controls.Add(_mainSplit, 0, 2);
        _mainSplit.Panel1.BackColor = UiWindowBack;
        _mainSplit.Panel2.BackColor = UiWindowBack;

        _macroList = new ListView
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
        _macroList.Columns.Add("名前", 150);
        _macroList.Columns.Add("ショートカット", 110);
        _macroList.Columns.Add("件数", 54);
        _macroList.Columns.Add("時間", 70);
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
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 206));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        _advancedSettingsRow = new RowStyle(SizeType.Absolute, 0);
        rightPanel.RowStyles.Add(_advancedSettingsRow);
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        _mainSplit.Panel2.Controls.Add(rightPanel);

        var macroGroup = CreateGroup("マクロ");
        rightPanel.Controls.Add(macroGroup, 0, 0);
        AddLabeledControl(macroGroup, "名前", _nameBox = new TextBox(), 24, 105);
        _nameBox.TextChanged += NameBoxOnTextChanged;
        AddLabeledControl(macroGroup, "ショートカット", _hotkeyBox = new TextBox { ReadOnly = true }, 58, 105);
        _hotkeyBox.KeyDown += HotkeyBoxOnKeyDown;
        AddLabeledControl(macroGroup, "緊急停止", _emergencyHotkeyBox = new TextBox { ReadOnly = true }, 92, 105);
        _emergencyHotkeyBox.Text = _emergencyStopHotkey.ToString();
        _emergencyHotkeyBox.KeyDown += EmergencyHotkeyBoxOnKeyDown;
        AddLabeledControl(macroGroup, "記録停止", _recordingStopHotkeyBox = new TextBox { ReadOnly = true }, 126, 105);
        _recordingStopHotkeyBox.Text = _recordingStopHotkey.ToString();
        _recordingStopHotkeyBox.KeyDown += RecordingStopHotkeyBoxOnKeyDown;
        macroGroup.Controls.Add(new Label
        {
            Text = "入力欄を選択してキーを押す。Backspace/Deleteで解除。",
            Location = new Point(12, 164),
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
            Maximum = 500,
            Increment = 10,
            Value = 100
        }, 104, 118);

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
        AppSettingsStore.Save(new AppSettings { ThemeMode = _themeMode });
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
        StyleButton(_deleteButton, ButtonTone.Danger);
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
        _toolTip.SetToolTip(_hotkeyBox, "このマクロを再生するショートカットです。入力欄を選んでキーを押します。Backspace/Deleteで解除できます。");
        _toolTip.SetToolTip(_emergencyHotkeyBox, "記録待ち・記録中・再生中の処理を即座に止めるホットキーです。Backspace/Deleteで既定値に戻します。");
        _toolTip.SetToolTip(_recordingStopHotkeyBox, "記録中だけ使う停止キーです。停止キー自体は記録データから除外します。Backspace/Deleteで既定値に戻します。");
        _toolTip.SetToolTip(_recordingModeBox, "完全記録は操作時刻をそのまま残します。イベント間隔一定はクリックやキー入力の間隔を指定msへ整えます。");
        _toolTip.SetToolTip(_densityBox, "記録密度のプリセットです。軽量は記録/再生60Hz、標準は記録200Hz/再生は画面Hz、高精度は記録/再生1000Hzです。");
        _toolTip.SetToolTip(_eventIntervalBox, "イベント間隔一定で使う基準間隔です。押下/解放ペア以外の次イベントまでの時間になります。");
        _toolTip.SetToolTip(_holdDurationBox, "イベント間隔一定で使う押下から解放までの時間です。クリックやキー押下の長さを決めます。");
        _toolTip.SetToolTip(_countdownBox, "記録ボタンを押してから実際に記録開始するまでの待ち時間です。操作対象へ移動する余裕を作ります。");
        _toolTip.SetToolTip(_playbackSpeedBox, "再生全体の速度です。100%が記録時と同じ速度、200%は2倍速、50%は半分の速度です。");
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
        if (_player.IsPlaying || _recorder.IsRecording || IsCountingDown)
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
            _recorder.Start(options);
            SetStatus($"記録中: {options.Name} / {options.MousePollingRateHz}Hz");
            UpdateButtons();
        }
        catch (Exception ex)
        {
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

    private void EmergencyStopCurrentWork()
    {
        StopCurrentWork();
        _emergencyOverlay.ShowMessage();
        SetStatus("緊急停止しました。");
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
        if (macro is null || _recorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        var noise = GetNoiseSettings(macro);
        using var overlay = new PreviewOverlayForm(macro, noise, (int)_previewPathCountBox.Value, Screen.FromControl(this));
        overlay.ShowDialog(this);
    }

    private void EditButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null || _recorder.IsRecording || _player.IsPlaying || IsCountingDown)
        {
            return;
        }

        using var editor = new MacroTrimEditorForm(macro);
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        macro.Events = editor.TrimmedEvents;
        RefreshMacroList(macro.Id);
        RefreshSummary(macro);
        AutoSaveMacros();
        SetStatus("マクロをトリミングしました。");
    }

    private void DeleteButtonOnClick(object? sender, EventArgs e)
    {
        var macro = GetSelectedMacro();
        if (macro is null)
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

        _macros.Remove(macro);
        RefreshMacroList();
        RefreshHotkeys();
        AutoSaveMacros();
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
        SetStatus($"出力しました: {dialog.FileName}");
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
        AutoSaveMacros();
        SetStatus($"読み込みました: {dialog.FileName}");
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
                    Recording = options,
                    Noise = noise,
                    Events = events
                };
                _macros.Add(macro);
                RefreshMacroList(macro.Id);
                AutoSaveMacros();
                SetStatus($"{events.Count} 件のイベントを記録しました。");
            }
            else
            {
                SetStatus("記録を停止しました。イベントはありません。");
            }

            RefreshHotkeys();
            RestoreAfterRecording();
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
        if (_recorder.IsRecording || IsCountingDown)
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
                (int)_playbackSpeedBox.Value,
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
        using var back = new SolidBrush(selected ? UiSelectionBack : UiPanelBack);
        e.Graphics.FillRectangle(back, e.Bounds);

        var textColor = selected ? UiSelectionText : UiText;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        flags |= e.ColumnIndex >= 2 ? TextFormatFlags.Right : TextFormatFlags.Left;
        var textRect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", _macroList.Font, textRect, textColor, flags);
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
        AutoSaveMacros();
    }

    private void EmergencyHotkeyBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            return;
        }

        if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
        {
            _emergencyStopHotkey = CreateDefaultEmergencyHotkey();
        }
        else
        {
            _emergencyStopHotkey = new HotkeyGesture
            {
                Ctrl = e.Control,
                Alt = e.Alt,
                Shift = e.Shift,
                Key = e.KeyCode
            };
        }

        _emergencyHotkeyBox.Text = _emergencyStopHotkey.ToString();
        RefreshHotkeys();
    }

    private void RecordingStopHotkeyBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;

        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            return;
        }

        if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
        {
            _recordingStopHotkey = CreateDefaultRecordingStopHotkey();
        }
        else
        {
            _recordingStopHotkey = new HotkeyGesture
            {
                Ctrl = e.Control,
                Alt = e.Alt,
                Shift = e.Shift,
                Key = e.KeyCode
            };
        }

        _recordingStopHotkeyBox.Text = _recordingStopHotkey.ToString();
        RefreshHotkeys();
    }

    private void LoadDefaultMacros()
    {
        try
        {
            _macros.Clear();
            _macros.AddRange(MacroStore.LoadDefault());
            RefreshMacroList();
            SetStatus($"自動読み込み: {_macros.Count} 件");
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
            MacroStore.SaveDefault(_macros);
        }
        catch (Exception ex)
        {
            SetStatus($"自動保存失敗: {ex.Message}");
        }
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

    private void RefreshSummary(Macro? macro)
    {
        if (macro is null)
        {
            _summaryLabel.Text = "記録データの詳細は表示しません。\r\nプレビューで軌跡を確認できます。";
            return;
        }

        _summaryLabel.Text =
            $"名前: {macro.Name}\r\n" +
            $"イベント数: {macro.Events.Count}\r\n" +
            $"時間: {macro.DurationMs} ms\r\n" +
            $"記録方法: {GetRecordingModeText(macro.Recording)}\r\n\r\n" +
            "記録データの詳細は表示しません。\r\n" +
            "プレビューでは赤=完全再現、黄=ノイズ入りを描画します。";
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

    private void UpdateButtons()
    {
        var recording = _recorder.IsRecording;
        var playing = _player.IsPlaying;
        var countingDown = IsCountingDown;
        var selected = GetSelectedMacro() is not null;

        _recordButton.Enabled = !recording && !playing && !countingDown;
        _stopButton.Enabled = recording || playing || countingDown;
        _playButton.Enabled = selected && !recording && !playing && !countingDown;
        _editButton.Enabled = selected && !recording && !playing && !countingDown;
        _previewButton.Enabled = selected && !recording && !playing && !countingDown;
        _deleteButton.Enabled = selected && !recording && !playing && !countingDown;
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
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollNearestParent(this, e.Delta);
        }
    }

    private sealed class ScrollFriendlyComboBox : ComboBox
    {
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
