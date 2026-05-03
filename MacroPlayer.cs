using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class MacroPlayer : IDisposable
{
    private readonly Random _random = new();
    private readonly Dictionary<RecordedMouseButton, Point> _pressedButtonPositions = new();
    private readonly HashSet<RecordedMouseButton> _movedWhilePressed = new();
    private CancellationTokenSource? _cts;

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
            long previous = 0;
            _pressedButtonPositions.Clear();
            _movedWhilePressed.Clear();
            foreach (var macroEvent in macro.Events.OrderBy(e => e.TimeOffsetMs))
            {
                token.ThrowIfCancellationRequested();

                var delay = macroEvent.TimeOffsetMs - previous;
                previous = macroEvent.TimeOffsetMs;
                delay = ApplyTimeJitter(delay, noise.TimeJitterPercent);
                if (delay > 0)
                {
                    await Task.Delay((int)Math.Min(delay, int.MaxValue), token);
                }

                Dispatch(macroEvent, noise);
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

    private long ApplyTimeJitter(long delay, int percent)
    {
        if (delay <= 0 || percent <= 0)
        {
            return delay;
        }

        var factor = 1.0 + ((_random.NextDouble() * 2.0 - 1.0) * percent / 100.0);
        return Math.Max(0, (long)Math.Round(delay * factor));
    }

    private void Dispatch(MacroEvent macroEvent, NoiseSettings noise)
    {
        switch (macroEvent.Kind)
        {
            case MacroEventKind.MouseMove:
                MoveMouseExact(macroEvent.X, macroEvent.Y);
                foreach (var button in _pressedButtonPositions.Keys)
                {
                    _movedWhilePressed.Add(button);
                }
                break;
            case MacroEventKind.MouseDown:
                var downPoint = MoveMouse(macroEvent.X, macroEvent.Y, noise);
                _pressedButtonPositions[macroEvent.Button] = downPoint;
                _movedWhilePressed.Remove(macroEvent.Button);
                SendMouseButton(macroEvent.Button, true);
                break;
            case MacroEventKind.MouseUp:
                if (_pressedButtonPositions.TryGetValue(macroEvent.Button, out var downPosition)
                    && !_movedWhilePressed.Contains(macroEvent.Button))
                {
                    MoveMouseExact(downPosition.X, downPosition.Y);
                }
                else
                {
                    MoveMouse(macroEvent.X, macroEvent.Y, noise);
                }

                SendMouseButton(macroEvent.Button, false);
                _pressedButtonPositions.Remove(macroEvent.Button);
                _movedWhilePressed.Remove(macroEvent.Button);
                break;
            case MacroEventKind.MouseWheel:
                MoveMouseExact(macroEvent.X, macroEvent.Y);
                SendMouseWheel(macroEvent.WheelDelta);
                break;
            case MacroEventKind.KeyDown:
                SendKey(macroEvent.KeyCode, true);
                break;
            case MacroEventKind.KeyUp:
                SendKey(macroEvent.KeyCode, false);
                break;
        }
    }

    private Point MoveMouse(int x, int y, NoiseSettings noise)
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
