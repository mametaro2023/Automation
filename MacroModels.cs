using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace AutomationTool;

public enum MacroEventKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    KeyDown,
    KeyUp
}

public enum RecordedMouseButton
{
    None,
    Left,
    Right,
    Middle,
    XButton1,
    XButton2
}

public enum RecordingTimingMode
{
    Complete,
    FixedEventInterval
}

public sealed class MacroEvent
{
    public MacroEventKind Kind { get; set; }
    public long TimeOffsetMs { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public RecordedMouseButton Button { get; set; }
    public int WheelDelta { get; set; }
    public Keys KeyCode { get; set; }
}

public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Macro";
    public bool IsEnabled { get; set; } = true;
    public int PlaybackSpeedPercent { get; set; } = 100;
    public long TrimStartMs { get; set; }
    public long? TrimEndMs { get; set; }
    public string? ScreenshotPath { get; set; }
    public int ScreenshotX { get; set; }
    public int ScreenshotY { get; set; }
    public int ScreenshotWidth { get; set; }
    public int ScreenshotHeight { get; set; }
    public HotkeyGesture Hotkey { get; set; } = new();
    public RecordingOptions Recording { get; set; } = RecordingOptions.Standard();
    public NoiseSettings Noise { get; set; } = new();
    public List<MacroEvent> Events { get; set; } = new();

    [JsonIgnore]
    public long RawDurationMs => Events.Count == 0 ? 0 : Events.Max(item => item.TimeOffsetMs);

    [JsonIgnore]
    public long DurationMs
    {
        get
        {
            if (Events.Count == 0)
            {
                return 0;
            }

            var startMs = Math.Clamp(TrimStartMs, 0, RawDurationMs);
            var endMs = Math.Clamp(TrimEndMs ?? RawDurationMs, startMs, RawDurationMs);
            return Math.Max(0, endMs - startMs);
        }
    }

    public List<MacroEvent> GetPlaybackEvents()
    {
        if (Events.Count == 0)
        {
            return new List<MacroEvent>();
        }

        var rawDuration = RawDurationMs;
        var startMs = Math.Clamp(TrimStartMs, 0, rawDuration);
        var endMs = Math.Clamp(TrimEndMs ?? rawDuration, startMs, rawDuration);
        return Events
            .Where(item => item.TimeOffsetMs >= startMs && item.TimeOffsetMs <= endMs)
            .OrderBy(item => item.TimeOffsetMs)
            .Select(item => CloneWithOffset(item, startMs))
            .ToList();
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
}

public sealed class HotkeyGesture
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public Keys Key { get; set; } = Keys.None;

    [JsonIgnore]
    public bool IsEmpty => Key == Keys.None;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "";
        }

        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}

public sealed class RecordingOptions
{
    public string Name { get; set; } = "標準";
    public int MousePollingRateHz { get; set; } = 200;
    public int MoveMinDistancePx { get; set; } = 2;
    public int DragMoveMinDistancePx { get; set; } = 1;
    public RecordingTimingMode TimingMode { get; set; } = RecordingTimingMode.Complete;
    public int EventIntervalMs { get; set; } = 200;
    public int HoldDurationMs { get; set; } = 60;

    public static RecordingOptions Lightweight() => new()
    {
        Name = "軽量",
        MousePollingRateHz = 60,
        MoveMinDistancePx = 4,
        DragMoveMinDistancePx = 2
    };

    public static RecordingOptions Standard() => new();

    public static RecordingOptions HighPrecision() => new()
    {
        Name = "高精度",
        MousePollingRateHz = 1000,
        MoveMinDistancePx = 1,
        DragMoveMinDistancePx = 1
    };
}

public sealed class NoiseSettings
{
    public int CoordinateJitterPx { get; set; } = 2;
    public int TimeJitterPercent { get; set; } = 5;
    public int TimeJitterMs { get; set; } = 20;
    public int AccelerationJitterPercent { get; set; } = 12;
    public int TrajectoryJitterPx { get; set; } = 16;
}

public sealed class MacroFile
{
    public int Version { get; set; } = 1;
    public List<Macro> Macros { get; set; } = new();
}
