using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class MacroPlayer : IDisposable
{
    private const int PlaybackFrameIntervalMs = 4;
    private const int EndpointSnapDistancePx = 8;

    private readonly Random _random = new();
    private readonly Dictionary<RecordedMouseButton, Point> _pressedButtonPositions = new();
    private readonly HashSet<RecordedMouseButton> _movedWhilePressed = new();
    private CancellationTokenSource? _cts;
    private Point? _currentMousePosition;

    public bool IsPlaying { get; private set; }

    public async Task PlayAsync(Macro macro, NoiseSettings noise, Action<string>? status = null)
    {
        if (IsPlaying || macro.Events.Count == 0)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsPlaying = true;
        status?.Invoke($"再生中: {macro.Name}");

        try
        {
            var events = macro.Events.OrderBy(e => e.TimeOffsetMs).ToList();
            long previous = 0;
            _pressedButtonPositions.Clear();
            _movedWhilePressed.Clear();
            _currentMousePosition = Cursor.Position;

            foreach (var macroEvent in events)
            {
                token.ThrowIfCancellationRequested();

                var delay = ApplyTimeJitter(macroEvent.TimeOffsetMs - previous, noise.TimeJitterPercent);
                previous = macroEvent.TimeOffsetMs;

                if (macroEvent.Kind == MacroEventKind.MouseMove)
                {
                    await SmoothMoveToAsync(
                        new Point(macroEvent.X, macroEvent.Y),
                        delay,
                        noise.AccelerationJitterPercent,
                        token);

                    foreach (var button in _pressedButtonPositions.Keys)
                    {
                        _movedWhilePressed.Add(button);
                    }
                }
                else
                {
                    await PreciseDelayAsync(delay, token);
                    DispatchInstantEvent(macroEvent, noise);
                }
            }
        }
        catch (OperationCanceledException)
        {
            status?.Invoke("再生を停止しました。");
        }
        finally
        {
            IsPlaying = false;
            _cts.Dispose();
            _cts = null;
            status?.Invoke("待機中。");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task SmoothMoveToAsync(
        Point target,
        long durationMs,
        int accelerationJitterPercent,
        CancellationToken token)
    {
        var start = _currentMousePosition ?? Cursor.Position;
        if (durationMs <= PlaybackFrameIntervalMs)
        {
            MoveMouseExact(target.X, target.Y);
            _currentMousePosition = target;
            return;
        }

        var curve = CreateMotionCurve(accelerationJitterPercent);
        var stopwatch = Stopwatch.StartNew();
        var lastPoint = start;

        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            token.ThrowIfCancellationRequested();

            var progress = Math.Clamp(stopwatch.ElapsedMilliseconds / (double)durationMs, 0.0, 1.0);
            var eased = curve(progress);
            var next = new Point(
                (int)Math.Round(start.X + (target.X - start.X) * eased),
                (int)Math.Round(start.Y + (target.Y - start.Y) * eased));

            if (next != lastPoint)
            {
                MoveMouseExact(next.X, next.Y);
                lastPoint = next;
            }

            await Task.Delay(PlaybackFrameIntervalMs, token);
        }

        MoveMouseExact(target.X, target.Y);
        _currentMousePosition = target;
    }

    private Func<double, double> CreateMotionCurve(int accelerationJitterPercent)
    {
        if (accelerationJitterPercent <= 0)
        {
            return SmoothStep;
        }

        var jitter = Math.Clamp(accelerationJitterPercent, 0, 50) / 100.0;
        var accelerationBias = (_random.NextDouble() * 2.0 - 1.0) * jitter;
        var midPush = (_random.NextDouble() * 2.0 - 1.0) * jitter * 0.35;

        return t =>
        {
            var baseCurve = SmoothStep(t);
            var accelerationCurve = accelerationBias >= 0
                ? Math.Pow(t, 1.0 + accelerationBias)
                : 1.0 - Math.Pow(1.0 - t, 1.0 - accelerationBias);
            var blended = baseCurve * 0.72 + accelerationCurve * 0.28;
            var middleOnly = Math.Sin(Math.PI * t) * midPush;
            return Math.Clamp(blended + middleOnly, 0.0, 1.0);
        };
    }

    private static double SmoothStep(double t)
    {
        return t * t * (3.0 - 2.0 * t);
    }

    private static async Task PreciseDelayAsync(long delayMs, CancellationToken token)
    {
        if (delayMs <= 0)
        {
            return;
        }

        if (delayMs > 8)
        {
            await Task.Delay((int)Math.Min(delayMs - 4, int.MaxValue), token);
        }

        var stopwatch = Stopwatch.StartNew();
        var remaining = Math.Min(delayMs, 4);
        while (stopwatch.ElapsedMilliseconds < remaining)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private long ApplyTimeJitter(long delay, int percent)
    {
        if (delay <= 0 || percent <= 0)
        {
            return delay;
        }

        var factor = 1.0 + ((_random.NextDouble() * 2.0 - 1.0) * percent / 100.0);
        return Math.Max(0, (long)Math.Round(delay * factor));
    }

    private void DispatchInstantEvent(MacroEvent macroEvent, NoiseSettings noise)
    {
        switch (macroEvent.Kind)
        {
            case MacroEventKind.MouseDown:
                var downPoint = MoveMouseWithClickJitter(macroEvent.X, macroEvent.Y, noise);
                _currentMousePosition = downPoint;
                _pressedButtonPositions[macroEvent.Button] = downPoint;
                _movedWhilePressed.Remove(macroEvent.Button);
                SendMouseButton(macroEvent.Button, true);
                break;
            case MacroEventKind.MouseUp:
                if (_pressedButtonPositions.TryGetValue(macroEvent.Button, out var downPosition)
                    && !_movedWhilePressed.Contains(macroEvent.Button))
                {
                    MoveMouseExact(downPosition.X, downPosition.Y);
                    _currentMousePosition = downPosition;
                }
                else
                {
                    var upPoint = MoveMouseWithClickJitter(macroEvent.X, macroEvent.Y, noise);
                    _currentMousePosition = upPoint;
                }

                SendMouseButton(macroEvent.Button, false);
                _pressedButtonPositions.Remove(macroEvent.Button);
                _movedWhilePressed.Remove(macroEvent.Button);
                break;
            case MacroEventKind.MouseWheel:
                MoveMouseExact(macroEvent.X, macroEvent.Y);
                _currentMousePosition = new Point(macroEvent.X, macroEvent.Y);
                SendMouseWheel(macroEvent.WheelDelta);
                break;
            case MacroEventKind.KeyDown:
                SnapToKeyPositionIfNeeded(macroEvent);
                SendKey(macroEvent.KeyCode, true);
                break;
            case MacroEventKind.KeyUp:
                SnapToKeyPositionIfNeeded(macroEvent);
                SendKey(macroEvent.KeyCode, false);
                break;
        }
    }

    private void SnapToKeyPositionIfNeeded(MacroEvent macroEvent)
    {
        if (macroEvent.X == 0 && macroEvent.Y == 0)
        {
            return;
        }

        var current = _currentMousePosition ?? Cursor.Position;
        var dx = current.X - macroEvent.X;
        var dy = current.Y - macroEvent.Y;
        if (dx * dx + dy * dy > EndpointSnapDistancePx * EndpointSnapDistancePx)
        {
            MoveMouseExact(macroEvent.X, macroEvent.Y);
            _currentMousePosition = new Point(macroEvent.X, macroEvent.Y);
        }
    }

    private Point MoveMouseWithClickJitter(int x, int y, NoiseSettings noise)
    {
        if (noise.CoordinateJitterPx > 0)
        {
            x += _random.Next(-noise.CoordinateJitterPx, noise.CoordinateJitterPx + 1);
            y += _random.Next(-noise.CoordinateJitterPx, noise.CoordinateJitterPx + 1);
        }

        MoveMouseExact(x, y);
        return new Point(x, y);
    }

    private static void MoveMouseExact(int x, int y)
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

        var normalizedX = (int)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1));
        var normalizedY = (int)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1));

        SendMouse(new NativeMethods.MouseInput
        {
            Dx = Math.Clamp(normalizedX, 0, 65535),
            Dy = Math.Clamp(normalizedY, 0, 65535),
            DwFlags = NativeMethods.MOUSEEVENTF_MOVE
                | NativeMethods.MOUSEEVENTF_ABSOLUTE
                | NativeMethods.MOUSEEVENTF_VIRTUALDESK
        });
    }

    private static void SendMouseButton(RecordedMouseButton button, bool down)
    {
        var flags = button switch
        {
            RecordedMouseButton.Left => down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP,
            RecordedMouseButton.Right => down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP,
            RecordedMouseButton.Middle => down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP,
            RecordedMouseButton.XButton1 or RecordedMouseButton.XButton2 => down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP,
            _ => 0u
        };

        if (flags == 0)
        {
            return;
        }

        SendMouse(new NativeMethods.MouseInput
        {
            DwFlags = flags,
            MouseData = button == RecordedMouseButton.XButton2 ? 2 : button == RecordedMouseButton.XButton1 ? 1 : 0
        });
    }

    private static void SendMouseWheel(int delta)
    {
        SendMouse(new NativeMethods.MouseInput
        {
            DwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
            MouseData = delta
        });
    }

    private static void SendMouse(NativeMethods.MouseInput mouseInput)
    {
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion { Mi = mouseInput }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.Input>());
    }

    private static void SendKey(Keys key, bool down)
    {
        if (key == Keys.None)
        {
            return;
        }

        var input = new NativeMethods.Input
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeyboardInput
                {
                    WVk = (ushort)key,
                    DwFlags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.Input>());
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
