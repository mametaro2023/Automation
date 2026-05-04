using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class InputRecorder : IDisposable
{
    private readonly NativeMethods.HookProc _mouseProc;
    private readonly NativeMethods.HookProc _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private readonly Stopwatch _clock = new();
    private readonly object _eventsLock = new();
    private RecordingOptions _options = RecordingOptions.Standard();
    private Point? _lastMovePoint;
    private long _lastMoveMs;
    private bool _dragging;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _timerResolutionRaised;

    public InputRecorder()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public event Action<MacroEvent>? EventRecorded;

    public Func<Point, bool>? ShouldIgnoreMousePoint { get; set; }
    public bool IsRecording { get; private set; }
    public List<MacroEvent> Events { get; } = new();

    public void Start(RecordingOptions options)
    {
        if (IsRecording)
        {
            return;
        }

        _options = options;
        Events.Clear();
        _lastMovePoint = null;
        _lastMoveMs = 0;
        _dragging = false;
        _clock.Restart();
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            Stop();
            throw new InvalidOperationException("Failed to install low-level input hooks.");
        }

        IsRecording = true;
        StartMousePolling();
    }

    public List<MacroEvent> Stop()
    {
        var pollingTask = _pollingTask;
        _pollingCts?.Cancel();
        try
        {
            pollingTask?.Wait(150);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException or TaskCanceledException))
        {
        }

        _pollingCts?.Dispose();
        _pollingCts = null;
        _pollingTask = null;
        RestoreTimerResolution();

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (IsRecording)
        {
            _clock.Stop();
        }

        IsRecording = false;
        lock (_eventsLock)
        {
            return Events.OrderBy(e => e.TimeOffsetMs).ToList();
        }
    }

    private void StartMousePolling()
    {
        _pollingCts = new CancellationTokenSource();
        var token = _pollingCts.Token;
        var pollingHz = Math.Clamp(_options.MousePollingRateHz, 1, 1000);
        var intervalTicks = Math.Max(1, Stopwatch.Frequency / pollingHz);
        RaiseTimerResolution();

        _pollingTask = Task.Run(() =>
        {
            try
            {
                var nextTick = Stopwatch.GetTimestamp();
                while (!token.IsCancellationRequested)
                {
                    var timestampMs = TicksToMilliseconds(_clock.ElapsedTicks);
                    var point = Cursor.Position;
                    if (ShouldIgnoreMousePoint?.Invoke(point) != true)
                    {
                        RecordMoveIfNeeded(point, timestampMs);
                    }

                    nextTick += intervalTicks;
                    WaitUntil(nextTick, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MouseHookStruct>(lParam);
            if ((info.Flags & NativeMethods.LLMHF_INJECTED) == 0)
            {
                var point = new Point(info.Pt.X, info.Pt.Y);
                if (ShouldIgnoreMousePoint?.Invoke(point) != true)
                {
                    HandleMouseEvent(wParam.ToInt32(), info, point);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KeyboardHookStruct>(lParam);
            if ((info.Flags & NativeMethods.LLKHF_INJECTED) == 0)
            {
                var message = wParam.ToInt32();
                var point = Cursor.Position;
                if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    Record(new MacroEvent
                    {
                        Kind = MacroEventKind.KeyDown,
                        KeyCode = (Keys)info.VkCode,
                        X = point.X,
                        Y = point.Y
                    });
                }
                else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    Record(new MacroEvent
                    {
                        Kind = MacroEventKind.KeyUp,
                        KeyCode = (Keys)info.VkCode,
                        X = point.X,
                        Y = point.Y
                    });
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void HandleMouseEvent(int message, NativeMethods.MouseHookStruct info, Point point)
    {
        switch (message)
        {
            case NativeMethods.WM_MOUSEMOVE:
                break;
            case NativeMethods.WM_LBUTTONDOWN:
                _dragging = true;
                RecordButton(MacroEventKind.MouseDown, RecordedMouseButton.Left, point);
                break;
            case NativeMethods.WM_LBUTTONUP:
                RecordButton(MacroEventKind.MouseUp, RecordedMouseButton.Left, point);
                _dragging = false;
                break;
            case NativeMethods.WM_RBUTTONDOWN:
                _dragging = true;
                RecordButton(MacroEventKind.MouseDown, RecordedMouseButton.Right, point);
                break;
            case NativeMethods.WM_RBUTTONUP:
                RecordButton(MacroEventKind.MouseUp, RecordedMouseButton.Right, point);
                _dragging = false;
                break;
            case NativeMethods.WM_MBUTTONDOWN:
                _dragging = true;
                RecordButton(MacroEventKind.MouseDown, RecordedMouseButton.Middle, point);
                break;
            case NativeMethods.WM_MBUTTONUP:
                RecordButton(MacroEventKind.MouseUp, RecordedMouseButton.Middle, point);
                _dragging = false;
                break;
            case NativeMethods.WM_XBUTTONDOWN:
                _dragging = true;
                RecordButton(MacroEventKind.MouseDown, GetXButton(info.MouseData), point);
                break;
            case NativeMethods.WM_XBUTTONUP:
                RecordButton(MacroEventKind.MouseUp, GetXButton(info.MouseData), point);
                _dragging = false;
                break;
            case NativeMethods.WM_MOUSEWHEEL:
                Record(new MacroEvent
                {
                    Kind = MacroEventKind.MouseWheel,
                    X = point.X,
                    Y = point.Y,
                    WheelDelta = (short)((info.MouseData >> 16) & 0xffff)
                });
                break;
        }
    }

    private void RecordMoveIfNeeded(Point point, long timestampMs)
    {
        var minDistance = _dragging ? _options.DragMoveMinDistancePx : _options.MoveMinDistancePx;

        if (_lastMovePoint is null)
        {
            RecordMove(point, timestampMs);
            return;
        }

        var dx = point.X - _lastMovePoint.Value.X;
        var dy = point.Y - _lastMovePoint.Value.Y;
        var distanceSquared = dx * dx + dy * dy;
        if (distanceSquared >= minDistance * minDistance)
        {
            RecordMove(point, timestampMs);
        }
    }

    private void RecordMove(Point point, long timestamp)
    {
        _lastMovePoint = point;
        _lastMoveMs = timestamp;
        Record(new MacroEvent
        {
            Kind = MacroEventKind.MouseMove,
            X = point.X,
            Y = point.Y
        }, timestamp);
    }

    private void RecordButton(MacroEventKind kind, RecordedMouseButton button, Point point)
    {
        Record(new MacroEvent
        {
            Kind = kind,
            Button = button,
            X = point.X,
            Y = point.Y
        });
    }

    private void Record(MacroEvent macroEvent, long? timestampMs = null)
    {
        macroEvent.TimeOffsetMs = timestampMs ?? _clock.ElapsedMilliseconds;
        lock (_eventsLock)
        {
            Events.Add(macroEvent);
        }

        EventRecorded?.Invoke(macroEvent);
    }

    private static RecordedMouseButton GetXButton(int mouseData)
    {
        var xButton = (mouseData >> 16) & 0xffff;
        return xButton == 2 ? RecordedMouseButton.XButton2 : RecordedMouseButton.XButton1;
    }

    private static long TicksToMilliseconds(long ticks)
    {
        return ticks * 1000 / Stopwatch.Frequency;
    }

    private static void WaitUntil(long targetTicks, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                Thread.Sleep(Math.Max(1, (int)remainingMs - 1));
            }
            else if (remainingMs > 0.5)
            {
                Thread.Sleep(0);
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
    }

    private void RaiseTimerResolution()
    {
        if (!_timerResolutionRaised)
        {
            _timerResolutionRaised = TimeBeginPeriod(1) == 0;
        }
    }

    private void RestoreTimerResolution()
    {
        if (_timerResolutionRaised)
        {
            TimeEndPeriod(1);
            _timerResolutionRaised = false;
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint TimeBeginPeriod(uint periodMs);

    [DllImport("winmm.dll")]
    private static extern uint TimeEndPeriod(uint periodMs);

    public void Dispose()
    {
        Stop();
    }
}
