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
    public HotkeyGesture Hotkey { get; set; } = new();
    public RecordingOptions Recording { get; set; } = RecordingOptions.Standard();
    public List<MacroEvent> Events { get; set; } = new();

    [JsonIgnore]
    public long DurationMs => Events.Count == 0 ? 0 : Events[^1].TimeOffsetMs;
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
    public string Name { get; set; } = "Standard";
    public int MoveMinIntervalMs { get; set; } = 8;
    public int MoveMinDistancePx { get; set; } = 2;
    public int DragMoveMinIntervalMs { get; set; } = 4;
    public int DragMoveMinDistancePx { get; set; } = 1;

    public static RecordingOptions Lightweight() => new()
    {
        Name = "Lightweight",
        MoveMinIntervalMs = 16,
        MoveMinDistancePx = 4,
        DragMoveMinIntervalMs = 8,
        DragMoveMinDistancePx = 2
    };

    public static RecordingOptions Standard() => new();

    public static RecordingOptions HighPrecision() => new()
    {
        Name = "High Precision",
        MoveMinIntervalMs = 2,
        MoveMinDistancePx = 1,
        DragMoveMinIntervalMs = 1,
        DragMoveMinDistancePx = 1
    };
}

public sealed class NoiseSettings
{
    public int CoordinateJitterPx { get; set; } = 2;
    public int TimeJitterPercent { get; set; } = 5;
}

public sealed class MacroFile
{
    public int Version { get; set; } = 1;
    public List<Macro> Macros { get; set; } = new();
}
