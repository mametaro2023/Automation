using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class MacroPlayer : IDisposable
{
    private const int EndpointSnapDistancePx = 8;

    private readonly Random _random = new();
    private CancellationTokenSource? _cts;
    private bool _timerResolutionRaised;

    public bool IsPlaying { get; private set; }

    public async Task PlayAsync(
        Macro macro,
        NoiseSettings noise,
        int speedPercent,
        Screen? playbackScreen = null,
        Action<string>? status = null)
    {
        if (IsPlaying || macro.Events.Count == 0)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsPlaying = true;
        var playbackRate = ResolvePlaybackRate(macro, playbackScreen);
        status?.Invoke($"再生中: {macro.Name} / 最大{playbackRate.Hertz}Hz");

        try
        {
            RaiseTimerResolution();
            var timeline = BuildTimeline(macro, noise, speedPercent, playbackRate.FrameIntervalMs);
            await Task.Run(() => RunTimeline(timeline, token), token);
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
            RestoreTimerResolution();
            status?.Invoke("待機中。");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private List<PlaybackAction> BuildTimeline(Macro macro, NoiseSettings noise, int speedPercent, double frameIntervalMs)
    {
        var timedEvents = BuildTimedEvents(macro, noise, speedPercent);
        var actions = new List<PlaybackAction>(timedEvents.Count * 2);
        var currentPosition = Cursor.Position;
        var currentRecordedPosition = currentPosition;
        var currentTimeMs = 0.0;
        var pressedPositions = new Dictionary<RecordedMouseButton, Point>();
        var movedWhilePressed = new HashSet<RecordedMouseButton>();
        var plannedEventPoints = new Dictionary<MacroEvent, Point>();

        for (var i = 0; i < timedEvents.Count; i++)
        {
            var timedEvent = timedEvents[i];
            var macroEvent = timedEvent.Event;

            if (macroEvent.Kind == MacroEventKind.MouseMove)
            {
                var run = new List<TimedMacroEvent>();
                while (i < timedEvents.Count && timedEvents[i].Event.Kind == MacroEventKind.MouseMove)
                {
                    run.Add(timedEvents[i]);
                    i++;
                }

                i--;
                var stationaryRun = IsStationaryRun(run, currentRecordedPosition);
                var nextEvent = i + 1 < timedEvents.Count ? timedEvents[i + 1].Event : null;
                var plannedEndPoint = PlanNextEventPoint(
                    nextEvent,
                    run[^1].Event,
                    currentPosition,
                    stationaryRun,
                    pressedPositions,
                    movedWhilePressed,
                    noise,
                    plannedEventPoints);
                currentPosition = AddMouseRun(
                    actions,
                    currentPosition,
                    currentTimeMs,
                    run,
                    noise,
                    pressedPositions.Count > 0,
                    frameIntervalMs,
                    stationaryRun,
                    plannedEndPoint);
                currentRecordedPosition = new Point(run[^1].Event.X, run[^1].Event.Y);
                currentTimeMs = run[^1].TimeMs;

                if (!stationaryRun)
                {
                    foreach (var button in pressedPositions.Keys)
                    {
                        movedWhilePressed.Add(button);
                    }
                }

                continue;
            }

            currentTimeMs = Math.Max(currentTimeMs, timedEvent.TimeMs);
            switch (macroEvent.Kind)
            {
                case MacroEventKind.MouseDown:
                    var downPoint = GetEventPoint(macroEvent, noise, plannedEventPoints);
                    actions.Add(PlaybackAction.MouseButton(currentTimeMs, downPoint, macroEvent.Button, true));
                    currentPosition = downPoint;
                    currentRecordedPosition = new Point(macroEvent.X, macroEvent.Y);
                    pressedPositions[macroEvent.Button] = downPoint;
                    movedWhilePressed.Remove(macroEvent.Button);
                    break;
                case MacroEventKind.MouseUp:
                    Point upPoint;
                    if (pressedPositions.TryGetValue(macroEvent.Button, out var downPosition)
                        && !movedWhilePressed.Contains(macroEvent.Button))
                    {
                        upPoint = downPosition;
                    }
                    else
                    {
                        upPoint = GetEventPoint(macroEvent, noise, plannedEventPoints);
                    }

                    actions.Add(PlaybackAction.MouseButton(currentTimeMs, upPoint, macroEvent.Button, false));
                    currentPosition = upPoint;
                    currentRecordedPosition = new Point(macroEvent.X, macroEvent.Y);
                    pressedPositions.Remove(macroEvent.Button);
                    movedWhilePressed.Remove(macroEvent.Button);
                    break;
                case MacroEventKind.MouseWheel:
                    var wheelPoint = new Point(macroEvent.X, macroEvent.Y);
                    actions.Add(PlaybackAction.MouseWheel(currentTimeMs, wheelPoint, macroEvent.WheelDelta));
                    currentPosition = wheelPoint;
                    currentRecordedPosition = wheelPoint;
                    break;
                case MacroEventKind.KeyDown:
                case MacroEventKind.KeyUp:
                    var keyPoint = GetKeySnapPoint(macroEvent, currentPosition);
                    if (keyPoint is not null)
                    {
                        currentPosition = keyPoint.Value;
                        currentRecordedPosition = keyPoint.Value;
                    }

                    actions.Add(PlaybackAction.Key(currentTimeMs, keyPoint, macroEvent.KeyCode, macroEvent.Kind == MacroEventKind.KeyDown));
                    break;
            }
        }

        return actions
            .OrderBy(action => action.TimeMs)
            .ThenBy(action => action.Priority)
            .ToList();
    }

    private List<TimedMacroEvent> BuildTimedEvents(Macro macro, NoiseSettings noise, int speedPercent)
    {
        var speed = Math.Clamp(speedPercent, 10, 500) / 100.0;
        var fixedIntervalMode = macro.Recording?.TimingMode == RecordingTimingMode.FixedEventInterval;
        var events = fixedIntervalMode
            ? MacroTimingNormalizer.NormalizeFixedEventIntervals(
                macro.Events,
                macro.Recording ?? RecordingOptions.Standard(),
                noise.TimeJitterMs,
                _random)
            : macro.Events.OrderBy(e => e.TimeOffsetMs).ToList();
        var timedEvents = new List<TimedMacroEvent>(events.Count);
        long previousOriginalMs = 0;
        double playbackTimeMs = 0;

        foreach (var macroEvent in events)
        {
            var delay = macroEvent.TimeOffsetMs - previousOriginalMs;
            previousOriginalMs = macroEvent.TimeOffsetMs;
            playbackTimeMs += (fixedIntervalMode ? delay : ApplyTimeJitter(delay, noise.TimeJitterPercent)) / speed;
            timedEvents.Add(new TimedMacroEvent(playbackTimeMs, macroEvent));
        }

        return timedEvents;
    }

    private Point AddMouseRun(
        List<PlaybackAction> actions,
        Point startPosition,
        double startTimeMs,
        List<TimedMacroEvent> run,
        NoiseSettings noise,
        bool isDragging,
        double frameIntervalMs,
        bool stationaryRun,
        Point? endPointOverride)
    {
        if (run.Count == 0)
        {
            return startPosition;
        }

        if (stationaryRun)
        {
            return startPosition;
        }

        var samples = new List<TimedPoint>(run.Count + 1)
        {
            new(startTimeMs, startPosition)
        };
        samples.AddRange(run.Select(item => new TimedPoint(item.TimeMs, new Point(item.Event.X, item.Event.Y))));
        if (endPointOverride is not null)
        {
            samples[^1] = samples[^1] with { Point = endPointOverride.Value };
        }

        var endTimeMs = samples[^1].TimeMs;
        if (endTimeMs <= startTimeMs)
        {
            AddMouseMoveAction(actions, endTimeMs, samples[^1].Point);
            return samples[^1].Point;
        }

        var profile = CreateMotionProfile(samples, noise, isDragging);
        var nextFrame = Math.Ceiling((startTimeMs + 0.001) / frameIntervalMs) * frameIntervalMs;
        for (var timeMs = nextFrame; timeMs < endTimeMs; timeMs += frameIntervalMs)
        {
            AddMouseMoveAction(actions, timeMs, InterpolateNatural(samples, timeMs, profile));
        }

        AddMouseMoveAction(actions, endTimeMs, samples[^1].Point);
        return samples[^1].Point;
    }

    private static bool IsStationaryRun(IReadOnlyList<TimedMacroEvent> run, Point previousRecordedPosition)
    {
        var first = previousRecordedPosition;
        return run.All(item => item.Event.X == first.X && item.Event.Y == first.Y);
    }

    private Point? PlanNextEventPoint(
        MacroEvent? nextEvent,
        MacroEvent lastRunEvent,
        Point currentPosition,
        bool stationaryRun,
        IReadOnlyDictionary<RecordedMouseButton, Point> pressedPositions,
        IReadOnlySet<RecordedMouseButton> movedWhilePressed,
        NoiseSettings noise,
        Dictionary<MacroEvent, Point> plannedEventPoints)
    {
        if (nextEvent is null
            || nextEvent.X != lastRunEvent.X
            || nextEvent.Y != lastRunEvent.Y)
        {
            return null;
        }

        if (nextEvent.Kind == MacroEventKind.MouseDown)
        {
            var point = stationaryRun ? currentPosition : CreateClickPoint(nextEvent, noise);
            plannedEventPoints[nextEvent] = point;
            return point;
        }

        if (nextEvent.Kind == MacroEventKind.MouseUp)
        {
            if (pressedPositions.TryGetValue(nextEvent.Button, out var downPosition)
                && (stationaryRun || !movedWhilePressed.Contains(nextEvent.Button)))
            {
                plannedEventPoints[nextEvent] = downPosition;
                return downPosition;
            }

            var point = CreateClickPoint(nextEvent, noise);
            plannedEventPoints[nextEvent] = point;
            return point;
        }

        return null;
    }

    private static void AddMouseMoveAction(List<PlaybackAction> actions, double timeMs, Point point)
    {
        if (actions.LastOrDefault() is { Kind: PlaybackActionKind.MouseMove } previous
            && previous.Point == point)
        {
            return;
        }

        actions.Add(PlaybackAction.MouseMove(timeMs, point));
    }

    private MotionProfile CreateMotionProfile(List<TimedPoint> samples, NoiseSettings noise, bool isDragging)
    {
        var start = samples[0].Point;
        var end = samples[^1].Point;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var accelerationStrength = Math.Clamp(noise.AccelerationJitterPercent, 0, 50) / 100.0;

        var normalX = distance > 0 ? -dy / distance : 0.0;
        var normalY = distance > 0 ? dx / distance : 0.0;
        var maxCurve = isDragging ? 10.0 : 60.0;
        var curveScale = isDragging ? 0.35 : 1.0;
        var distanceScale = Math.Clamp(distance / 350.0, 0.25, 1.65);
        var amplitude = distance < 24
            ? 0.0
            : Math.Min(maxCurve, noise.TrajectoryJitterPx * distanceScale * curveScale);

        return new MotionProfile
        {
            StartTimeMs = samples[0].TimeMs,
            EndTimeMs = samples[^1].TimeMs,
            AccelerationBias = (_random.NextDouble() * 2.0 - 1.0) * accelerationStrength,
            CurveA = RandomSigned(amplitude * 0.55, amplitude),
            CurveB = RandomSigned(amplitude * 0.18, amplitude * 0.45),
            NormalX = normalX,
            NormalY = normalY
        };
    }

    private double RandomSigned(double minMagnitude, double maxMagnitude)
    {
        if (maxMagnitude <= 0)
        {
            return 0;
        }

        var sign = _random.Next(0, 2) == 0 ? -1.0 : 1.0;
        return sign * (minMagnitude + _random.NextDouble() * Math.Max(0.0, maxMagnitude - minMagnitude));
    }

    private static Point InterpolateNatural(List<TimedPoint> samples, double timeMs, MotionProfile profile)
    {
        var progress = Math.Clamp(
            (timeMs - profile.StartTimeMs) / Math.Max(0.001, profile.EndTimeMs - profile.StartTimeMs),
            0.0,
            1.0);
        var sourceTime = profile.StartTimeMs + profile.Ease(progress) * (profile.EndTimeMs - profile.StartTimeMs);
        var basePoint = InterpolateRecordedPath(samples, sourceTime);
        var envelope = Math.Pow(Math.Sin(Math.PI * progress), 1.25);
        var sideOffset = envelope
            * (Math.Sin(Math.PI * progress) * profile.CurveA
                + Math.Sin(Math.PI * 2.0 * progress) * profile.CurveB);

        return new Point(
            (int)Math.Round(basePoint.X + profile.NormalX * sideOffset),
            (int)Math.Round(basePoint.Y + profile.NormalY * sideOffset));
    }

    private static Point InterpolateRecordedPath(List<TimedPoint> samples, double timeMs)
    {
        if (samples.Count == 1)
        {
            return samples[0].Point;
        }

        var index = 0;
        while (index < samples.Count - 2 && samples[index + 1].TimeMs < timeMs)
        {
            index++;
        }

        var previous = samples[Math.Max(0, index - 1)];
        var current = samples[index];
        var next = samples[Math.Min(samples.Count - 1, index + 1)];
        var afterNext = samples[Math.Min(samples.Count - 1, index + 2)];
        var span = Math.Max(0.001, next.TimeMs - current.TimeMs);
        var t = Math.Clamp((timeMs - current.TimeMs) / span, 0.0, 1.0);

        return new Point(
            (int)Math.Round(Catmull(previous.Point.X, current.Point.X, next.Point.X, afterNext.Point.X, t)),
            (int)Math.Round(Catmull(previous.Point.Y, current.Point.Y, next.Point.Y, afterNext.Point.Y, t)));
    }

    private static double Catmull(double p0, double p1, double p2, double p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5 * ((2.0 * p1)
            + (-p0 + p2) * t
            + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2
            + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
    }

    private static double SmoothStep(double t)
    {
        return t * t * (3.0 - 2.0 * t);
    }

    private Point GetEventPoint(MacroEvent macroEvent, NoiseSettings noise, Dictionary<MacroEvent, Point> plannedEventPoints)
    {
        if (plannedEventPoints.Remove(macroEvent, out var point))
        {
            return point;
        }

        return CreateClickPoint(macroEvent, noise);
    }

    private Point CreateClickPoint(MacroEvent macroEvent, NoiseSettings noise)
    {
        var x = macroEvent.X;
        var y = macroEvent.Y;
        var maxJitter = Math.Min(noise.CoordinateJitterPx, 2);
        if (maxJitter > 0)
        {
            x += _random.Next(-maxJitter, maxJitter + 1);
            y += _random.Next(-maxJitter, maxJitter + 1);
        }

        return new Point(x, y);
    }

    private static Point? GetKeySnapPoint(MacroEvent macroEvent, Point currentPosition)
    {
        if (macroEvent.X == 0 && macroEvent.Y == 0)
        {
            return null;
        }

        var dx = currentPosition.X - macroEvent.X;
        var dy = currentPosition.Y - macroEvent.Y;
        return dx * dx + dy * dy > EndpointSnapDistancePx * EndpointSnapDistancePx
            ? new Point(macroEvent.X, macroEvent.Y)
            : null;
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

    private static PlaybackRate ResolvePlaybackRate(Macro macro, Screen? playbackScreen)
    {
        var recording = macro.Recording ?? RecordingOptions.Standard();
        var densityName = recording.Name ?? "";
        if (recording.MousePollingRateHz <= 60 || densityName.Contains("軽量", StringComparison.Ordinal))
        {
            return CreatePlaybackRate(60);
        }

        if (recording.MousePollingRateHz >= 1000 || densityName.Contains("高精度", StringComparison.Ordinal))
        {
            return CreatePlaybackRate(1000);
        }

        return CreatePlaybackRate(GetDisplayRefreshRate(playbackScreen) ?? 60);
    }

    private static PlaybackRate CreatePlaybackRate(int hertz)
    {
        var clampedHz = Math.Clamp(hertz, 30, 1000);
        return new PlaybackRate(clampedHz, 1000.0 / clampedHz);
    }

    private static int? GetDisplayRefreshRate(Screen? screen)
    {
        if (screen is null)
        {
            return null;
        }

        var devMode = new NativeMethods.DevMode
        {
            DmDeviceName = new string('\0', 32),
            DmFormName = new string('\0', 32),
            DmSize = (ushort)Marshal.SizeOf<NativeMethods.DevMode>()
        };
        if (!NativeMethods.EnumDisplaySettings(screen.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
        {
            return null;
        }

        var hertz = (int)devMode.DmDisplayFrequency;
        return hertz is >= 30 and <= 1000 ? hertz : null;
    }

    private static void RunTimeline(IReadOnlyList<PlaybackAction> timeline, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < timeline.Count; i++)
        {
            var action = timeline[i];
            if (action.Kind == PlaybackActionKind.MouseMove)
            {
                i = SkipStaleMouseMoves(timeline, i, stopwatch.Elapsed.TotalMilliseconds);
                action = timeline[i];
            }

            WaitUntil(stopwatch, action.TimeMs, token);
            Execute(action);
        }
    }

    private static int SkipStaleMouseMoves(IReadOnlyList<PlaybackAction> timeline, int index, double elapsedMs)
    {
        var latestIndex = index;
        while (latestIndex + 1 < timeline.Count
            && timeline[latestIndex + 1].Kind == PlaybackActionKind.MouseMove
            && timeline[latestIndex + 1].TimeMs <= elapsedMs)
        {
            latestIndex++;
        }

        return latestIndex;
    }

    private static void WaitUntil(Stopwatch stopwatch, double targetMs, CancellationToken token)
    {
        var targetTicks = (long)Math.Round(targetMs * Stopwatch.Frequency / 1000.0);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remainingTicks = targetTicks - stopwatch.ElapsedTicks;
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 5.0)
            {
                Thread.Sleep(Math.Max(1, (int)remainingMs - 2));
            }
            else if (remainingMs > 1.0)
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
            _timerResolutionRaised = TimeBeginPeriodNative(1) == 0;
        }
    }

    private void RestoreTimerResolution()
    {
        if (_timerResolutionRaised)
        {
            TimeEndPeriodNative(1);
            _timerResolutionRaised = false;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriodNative(uint periodMs);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    private static extern uint TimeEndPeriodNative(uint periodMs);

    private static void Execute(PlaybackAction action)
    {
        switch (action.Kind)
        {
            case PlaybackActionKind.MouseMove:
                MoveMouseExact(action.Point.X, action.Point.Y);
                break;
            case PlaybackActionKind.MouseDown:
                MoveMouseExact(action.Point.X, action.Point.Y);
                SendMouseButton(action.Button, true);
                break;
            case PlaybackActionKind.MouseUp:
                MoveMouseExact(action.Point.X, action.Point.Y);
                SendMouseButton(action.Button, false);
                break;
            case PlaybackActionKind.MouseWheel:
                MoveMouseExact(action.Point.X, action.Point.Y);
                SendMouseWheel(action.WheelDelta);
                break;
            case PlaybackActionKind.KeyDown:
                if (action.Point is { X: not 0 } or { Y: not 0 })
                {
                    MoveMouseExact(action.Point.X, action.Point.Y);
                }

                SendKey(action.KeyCode, true);
                break;
            case PlaybackActionKind.KeyUp:
                if (action.Point is { X: not 0 } or { Y: not 0 })
                {
                    MoveMouseExact(action.Point.X, action.Point.Y);
                }

                SendKey(action.KeyCode, false);
                break;
        }
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

    private sealed record TimedMacroEvent(double TimeMs, MacroEvent Event);
    private sealed record TimedPoint(double TimeMs, Point Point);
    private sealed record PlaybackRate(int Hertz, double FrameIntervalMs);

    private sealed class MotionProfile
    {
        public double StartTimeMs { get; init; }
        public double EndTimeMs { get; init; }
        public double AccelerationBias { get; init; }
        public double CurveA { get; init; }
        public double CurveB { get; init; }
        public double NormalX { get; init; }
        public double NormalY { get; init; }

        public double Ease(double progress)
        {
            var baseCurve = SmoothStep(progress);
            var accelerationCurve = AccelerationBias >= 0
                ? Math.Pow(progress, 1.0 + AccelerationBias)
                : 1.0 - Math.Pow(1.0 - progress, 1.0 - AccelerationBias);
            return Math.Clamp(baseCurve * 0.8 + accelerationCurve * 0.2, 0.0, 1.0);
        }
    }

    private enum PlaybackActionKind
    {
        MouseMove,
        MouseDown,
        MouseUp,
        MouseWheel,
        KeyDown,
        KeyUp
    }

    private sealed class PlaybackAction
    {
        public double TimeMs { get; private init; }
        public PlaybackActionKind Kind { get; private init; }
        public Point Point { get; private init; }
        public RecordedMouseButton Button { get; private init; }
        public int WheelDelta { get; private init; }
        public Keys KeyCode { get; private init; }
        public int Priority => Kind == PlaybackActionKind.MouseMove ? 0 : 1;

        public static PlaybackAction MouseMove(double timeMs, Point point) => new()
        {
            TimeMs = timeMs,
            Kind = PlaybackActionKind.MouseMove,
            Point = point
        };

        public static PlaybackAction MouseButton(double timeMs, Point point, RecordedMouseButton button, bool down) => new()
        {
            TimeMs = timeMs,
            Kind = down ? PlaybackActionKind.MouseDown : PlaybackActionKind.MouseUp,
            Point = point,
            Button = button
        };

        public static PlaybackAction MouseWheel(double timeMs, Point point, int delta) => new()
        {
            TimeMs = timeMs,
            Kind = PlaybackActionKind.MouseWheel,
            Point = point,
            WheelDelta = delta
        };

        public static PlaybackAction Key(double timeMs, Point? point, Keys keyCode, bool down) => new()
        {
            TimeMs = timeMs,
            Kind = down ? PlaybackActionKind.KeyDown : PlaybackActionKind.KeyUp,
            Point = point ?? Point.Empty,
            KeyCode = keyCode
        };
    }
}
