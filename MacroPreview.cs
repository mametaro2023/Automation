using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AutomationTool;

public sealed class MacroPreview
{
    public List<Point> PerfectPath { get; } = new();
    public List<TimedPreviewPoint> PerfectTimedPath { get; } = new();
    public List<List<Point>> NoisyPaths { get; } = new();
    public List<List<TimedPreviewPoint>> NoisyTimedPaths { get; } = new();
    public List<List<PreviewLearnedPathSegment>> LearnedPathSegments { get; } = new();
    public List<PreviewMarker> PerfectMarkers { get; } = new();
    public List<List<PreviewMarker>> NoisyMarkers { get; } = new();
    public List<PreviewInteractionSegment> InteractionSegments { get; } = new();
    public long DurationMs { get; set; }
}

public sealed class PreviewMarker
{
    public Point Location { get; init; }
    public long TimeMs { get; init; }
    public MacroEventKind Kind { get; init; }
    public string Text { get; init; } = "";
}

public sealed record TimedPreviewPoint(long TimeMs, Point Point);

public sealed record PreviewLearnedPathSegment(List<Point> Points);

public sealed record PreviewInteractionSegment(
    long StartMs,
    long EndMs,
    Point Start,
    Point End,
    string Text);

public static class MacroPreviewBuilder
{
    private const int StationaryRadiusPx = 2;
    private const long StationaryMinDurationMs = 10;
    private const int StationaryMinSamples = 3;

    public static MacroPreview Build(
        Macro macro,
        NoiseSettings noise,
        int variantCount,
        HumanMotionProfile? motionProfile = null)
    {
        var preview = new MacroPreview();
        var events = macro.GetPlaybackEvents();
        preview.DurationMs = events.Count == 0 ? 0 : events[^1].TimeOffsetMs;
        var count = Math.Clamp(variantCount, 1, 10);
        foreach (var macroEvent in events.Where(e => e.Kind == MacroEventKind.MouseMove))
        {
            var point = new Point(macroEvent.X, macroEvent.Y);
            preview.PerfectPath.Add(point);
            preview.PerfectTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, point));
        }

        foreach (var macroEvent in events.Where(e => e.Kind != MacroEventKind.MouseMove))
        {
            if (TryGetPoint(macroEvent, out var point))
            {
                preview.PerfectMarkers.Add(CreateMarker(point, macroEvent));
            }
        }

        BuildInteractionSegments(preview, events);

        var baseSeed = Environment.TickCount ^ macro.Id.GetHashCode();
        for (var i = 0; i < count; i++)
        {
            BuildNoisyVariant(preview, events, noise, motionProfile, new Random(baseSeed + i * 7919));
        }

        return preview;
    }

    private static void BuildNoisyVariant(
        MacroPreview preview,
        List<MacroEvent> events,
        NoiseSettings noise,
        HumanMotionProfile? motionProfile,
        Random random)
    {
        var noisyPath = new List<Point>();
        var noisyTimedPath = new List<TimedPreviewPoint>();
        var learnedSegments = new List<PreviewLearnedPathSegment>();
        var noisyMarkers = new List<PreviewMarker>();
        var anchor = Point.Empty;
        var hasAnchor = false;
        var recordedAnchor = Point.Empty;
        var hasRecordedAnchor = false;
        var segment = new List<MacroEvent>();
        var pressedPositions = new Dictionary<RecordedMouseButton, Point>();
        var movedWhilePressed = new HashSet<RecordedMouseButton>();

        foreach (var macroEvent in events)
        {
            if (macroEvent.Kind == MacroEventKind.MouseMove)
            {
                segment.Add(macroEvent);
                continue;
            }

            var flushResult = FlushMoveSegment(
                noisyPath,
                noisyTimedPath,
                segment,
                ref anchor,
                ref hasAnchor,
                ref recordedAnchor,
                ref hasRecordedAnchor,
                noise,
                motionProfile,
                pressedPositions.Count > 0,
                learnedSegments,
                random);
            if (flushResult.HadMovement)
            {
                foreach (var button in pressedPositions.Keys)
                {
                    movedWhilePressed.Add(button);
                }
            }

            segment.Clear();

            if (TryGetPoint(macroEvent, out var point))
            {
                var noisyPoint = CreateNoisyEventPoint(macroEvent, point, anchor, hasAnchor && flushResult.TrailingStationary, pressedPositions, movedWhilePressed, noise, random);
                noisyMarkers.Add(CreateMarker(noisyPoint, macroEvent));
                noisyPath.Add(noisyPoint);
                noisyTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, noisyPoint));
                anchor = noisyPoint;
                hasAnchor = true;
                recordedAnchor = point;
                hasRecordedAnchor = true;
            }
        }

        FlushMoveSegment(
            noisyPath,
            noisyTimedPath,
            segment,
            ref anchor,
            ref hasAnchor,
            ref recordedAnchor,
            ref hasRecordedAnchor,
            noise,
            motionProfile,
            pressedPositions.Count > 0,
            learnedSegments,
            random);
        preview.NoisyPaths.Add(noisyPath);
        preview.NoisyTimedPaths.Add(noisyTimedPath);
        preview.LearnedPathSegments.Add(learnedSegments);
        preview.NoisyMarkers.Add(noisyMarkers);
    }

    private static FlushMoveResult FlushMoveSegment(
        List<Point> noisyPath,
        List<TimedPreviewPoint> noisyTimedPath,
        List<MacroEvent> segment,
        ref Point anchor,
        ref bool hasAnchor,
        ref Point recordedAnchor,
        ref bool hasRecordedAnchor,
        NoiseSettings noise,
        HumanMotionProfile? motionProfile,
        bool isDragging,
        List<PreviewLearnedPathSegment> learnedSegments,
        Random random)
    {
        if (segment.Count == 0)
        {
            return new FlushMoveResult(true, false);
        }

        var moveSegments = SplitMoveSegment(segment, hasRecordedAnchor ? recordedAnchor : new Point(segment[0].X, segment[0].Y));
        var trailingStationary = false;
        var hadMovement = false;
        foreach (var moveSegment in moveSegments)
        {
            if (moveSegment.IsStationary)
            {
                var holdPoint = hasAnchor ? anchor : new Point(moveSegment.Events[0].X, moveSegment.Events[0].Y);
                foreach (var macroEvent in moveSegment.Events)
                {
                    noisyPath.Add(holdPoint);
                    noisyTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, holdPoint));
                }

                anchor = holdPoint;
                hasAnchor = true;
                recordedAnchor = new Point(moveSegment.Events[^1].X, moveSegment.Events[^1].Y);
                hasRecordedAnchor = true;
                trailingStationary = true;
                continue;
            }

            DrawMovingSegment(noisyPath, noisyTimedPath, moveSegment.Events, ref anchor, ref hasAnchor, noise, motionProfile, isDragging, learnedSegments, random);
            recordedAnchor = new Point(moveSegment.Events[^1].X, moveSegment.Events[^1].Y);
            hasRecordedAnchor = true;
            trailingStationary = false;
            hadMovement = true;
        }

        return new FlushMoveResult(trailingStationary, hadMovement);
    }

    private static void DrawMovingSegment(
        List<Point> noisyPath,
        List<TimedPreviewPoint> noisyTimedPath,
        List<MacroEvent> segment,
        ref Point anchor,
        ref bool hasAnchor,
        NoiseSettings noise,
        HumanMotionProfile? motionProfile,
        bool isDragging,
        List<PreviewLearnedPathSegment> learnedSegments,
        Random random)
    {
        var start = hasAnchor ? anchor : new Point(segment[0].X, segment[0].Y);
        var end = new Point(segment[^1].X, segment[^1].Y);
        if (HumanMotionPathGenerator.TryCreatePath(
            motionProfile,
            start,
            end,
            segment[0].TimeOffsetMs,
            segment[^1].TimeOffsetMs,
            8.0,
            isDragging,
            noise.LearnedTrajectoryTolerancePx,
            segment.Select(item => new HumanMotionReferencePoint(item.TimeOffsetMs, new Point(item.X, item.Y))).ToList(),
            random,
            out var learnedPath))
        {
            var learnedPoints = new List<Point> { start };
            foreach (var point in learnedPath)
            {
                noisyPath.Add(point.Point);
                noisyTimedPath.Add(new TimedPreviewPoint((long)Math.Round(point.TimeMs), point.Point));
                learnedPoints.Add(point.Point);
            }

            if (learnedPoints.Count >= 2)
            {
                learnedSegments.Add(new PreviewLearnedPathSegment(learnedPoints));
            }

            anchor = end;
            hasAnchor = true;
            return;
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var normalX = length > 0 ? -dy / length : 0;
        var normalY = length > 0 ? dx / length : 0;
        var distanceScale = Math.Clamp(length / 350.0, 0.25, 1.65);
        var amplitude = length < 24
            ? 0.0
            : Math.Min(60.0, noise.TrajectoryJitterPx * distanceScale);
        var waveA = RandomSigned(random, amplitude * 0.55, amplitude);
        var waveB = RandomSigned(random, amplitude * 0.18, amplitude * 0.45);

        for (var i = 0; i < segment.Count; i++)
        {
            var t = segment.Count == 1 ? 1.0 : i / (double)(segment.Count - 1);
            var macroEvent = segment[i];
            var sideOffset = Math.Sin(Math.PI * t) * waveA + Math.Sin(Math.PI * 2.0 * t) * waveB;
            noisyPath.Add(new Point(
                (int)Math.Round(macroEvent.X + normalX * sideOffset),
                (int)Math.Round(macroEvent.Y + normalY * sideOffset)));
            noisyTimedPath.Add(new TimedPreviewPoint(macroEvent.TimeOffsetMs, noisyPath[^1]));
        }

        if (noisyPath.Count > 0)
        {
            anchor = noisyPath[^1];
            hasAnchor = true;
        }
    }

    private static List<PreviewMoveSegment> SplitMoveSegment(IReadOnlyList<MacroEvent> segment, Point previousRecordedPosition)
    {
        var segments = new List<PreviewMoveSegment>();
        var index = 0;
        var anchor = previousRecordedPosition;
        while (index < segment.Count)
        {
            if (TryFindStationarySpan(segment, index, anchor, out var stationaryEnd))
            {
                segments.Add(new PreviewMoveSegment(true, segment.Skip(index).Take(stationaryEnd - index + 1).ToList()));
                anchor = new Point(segment[stationaryEnd].X, segment[stationaryEnd].Y);
                index = stationaryEnd + 1;
                continue;
            }

            var movingStart = index;
            index++;
            while (index < segment.Count
                && !TryFindStationarySpan(
                    segment,
                    index,
                    new Point(segment[index - 1].X, segment[index - 1].Y),
                    out _))
            {
                index++;
            }

            segments.Add(new PreviewMoveSegment(false, segment.Skip(movingStart).Take(index - movingStart).ToList()));
            anchor = new Point(segment[index - 1].X, segment[index - 1].Y);
        }

        return segments;
    }

    private static bool TryFindStationarySpan(
        IReadOnlyList<MacroEvent> segment,
        int startIndex,
        Point anchor,
        out int endIndex)
    {
        endIndex = startIndex - 1;
        var startPoint = new Point(segment[startIndex].X, segment[startIndex].Y);
        var stationaryAnchor = DistanceSquared(startPoint, anchor) <= StationaryRadiusPx * StationaryRadiusPx
            ? anchor
            : startPoint;

        for (var i = startIndex; i < segment.Count; i++)
        {
            var point = new Point(segment[i].X, segment[i].Y);
            if (DistanceSquared(point, stationaryAnchor) > StationaryRadiusPx * StationaryRadiusPx)
            {
                break;
            }

            endIndex = i;
        }

        var count = endIndex - startIndex + 1;
        if (count <= 0)
        {
            return false;
        }

        var durationMs = segment[endIndex].TimeOffsetMs - segment[startIndex].TimeOffsetMs;
        return count >= StationaryMinSamples || durationMs >= StationaryMinDurationMs;
    }

    private static int DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double RandomSigned(Random random, double minMagnitude, double maxMagnitude)
    {
        if (maxMagnitude <= 0)
        {
            return 0;
        }

        var sign = random.Next(0, 2) == 0 ? -1.0 : 1.0;
        return sign * (minMagnitude + random.NextDouble() * Math.Max(0.0, maxMagnitude - minMagnitude));
    }

    private static Point CreateNoisyEventPoint(
        MacroEvent macroEvent,
        Point recordedPoint,
        Point stationaryAnchor,
        bool useStationaryAnchor,
        Dictionary<RecordedMouseButton, Point> pressedPositions,
        HashSet<RecordedMouseButton> movedWhilePressed,
        NoiseSettings noise,
        Random random)
    {
        if (macroEvent.Kind == MacroEventKind.MouseDown)
        {
            var point = useStationaryAnchor ? stationaryAnchor : CreateNoisyAnchor(recordedPoint, macroEvent, noise, random);
            pressedPositions[macroEvent.Button] = point;
            movedWhilePressed.Remove(macroEvent.Button);
            return point;
        }

        if (macroEvent.Kind == MacroEventKind.MouseUp)
        {
            if (pressedPositions.TryGetValue(macroEvent.Button, out var downPoint)
                && !movedWhilePressed.Contains(macroEvent.Button))
            {
                pressedPositions.Remove(macroEvent.Button);
                movedWhilePressed.Remove(macroEvent.Button);
                return downPoint;
            }

            var point = useStationaryAnchor ? stationaryAnchor : CreateNoisyAnchor(recordedPoint, macroEvent, noise, random);
            pressedPositions.Remove(macroEvent.Button);
            movedWhilePressed.Remove(macroEvent.Button);
            return point;
        }

        return CreateNoisyAnchor(recordedPoint, macroEvent, noise, random);
    }

    private static bool TryGetPoint(MacroEvent macroEvent, out Point point)
    {
        if (macroEvent.Kind is MacroEventKind.MouseDown
            or MacroEventKind.MouseUp
            or MacroEventKind.MouseWheel
            or MacroEventKind.KeyDown
            or MacroEventKind.KeyUp
            or MacroEventKind.MouseMove)
        {
            point = new Point(macroEvent.X, macroEvent.Y);
            return macroEvent.X != 0 || macroEvent.Y != 0;
        }

        point = Point.Empty;
        return false;
    }

    private static Point CreateNoisyAnchor(Point point, MacroEvent macroEvent, NoiseSettings noise, Random random)
    {
        var maxJitter = macroEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.MouseUp
            ? Math.Min(noise.CoordinateJitterPx, 2)
            : 0;

        if (maxJitter <= 0)
        {
            return point;
        }

        return new Point(
            point.X + random.Next(-maxJitter, maxJitter + 1),
            point.Y + random.Next(-maxJitter, maxJitter + 1));
    }

    private static PreviewMarker CreateMarker(Point point, MacroEvent macroEvent)
    {
        return new PreviewMarker
        {
            Location = point,
            TimeMs = macroEvent.TimeOffsetMs,
            Kind = macroEvent.Kind,
            Text = GetEventText(macroEvent)
        };
    }

    private static void BuildInteractionSegments(MacroPreview preview, List<MacroEvent> events)
    {
        for (var i = 0; i < events.Count; i++)
        {
            var start = events[i];
            if (start.Kind == MacroEventKind.MouseDown)
            {
                var endIndex = FindForward(events, i, item => item.Kind == MacroEventKind.MouseUp && item.Button == start.Button);
                if (endIndex is not null && TryGetPoint(start, out var startPoint) && TryGetPoint(events[endIndex.Value], out var endPoint))
                {
                    preview.InteractionSegments.Add(new PreviewInteractionSegment(
                        start.TimeOffsetMs,
                        events[endIndex.Value].TimeOffsetMs,
                        startPoint,
                        endPoint,
                        GetMouseButtonText(start.Button)));
                }
            }
            else if (start.Kind == MacroEventKind.KeyDown)
            {
                var endIndex = FindForward(events, i, item => item.Kind == MacroEventKind.KeyUp && item.KeyCode == start.KeyCode);
                if (endIndex is not null && TryGetPoint(start, out var startPoint) && TryGetPoint(events[endIndex.Value], out var endPoint))
                {
                    preview.InteractionSegments.Add(new PreviewInteractionSegment(
                        start.TimeOffsetMs,
                        events[endIndex.Value].TimeOffsetMs,
                        startPoint,
                        endPoint,
                        GetKeyText(start.KeyCode)));
                }
            }
        }
    }

    private static int? FindForward(IReadOnlyList<MacroEvent> events, int index, Func<MacroEvent, bool> predicate)
    {
        for (var i = index + 1; i < events.Count; i++)
        {
            if (predicate(events[i]))
            {
                return i;
            }
        }

        return null;
    }

    private static string GetEventText(MacroEvent macroEvent)
    {
        return macroEvent.Kind switch
        {
            MacroEventKind.MouseDown => $"{GetMouseButtonText(macroEvent.Button)} ↓",
            MacroEventKind.MouseUp => $"{GetMouseButtonText(macroEvent.Button)} ↑",
            MacroEventKind.KeyDown => $"{GetKeyText(macroEvent.KeyCode)} ↓",
            MacroEventKind.KeyUp => $"{GetKeyText(macroEvent.KeyCode)} ↑",
            MacroEventKind.MouseWheel => macroEvent.WheelDelta >= 0 ? "Wheel +" : "Wheel -",
            _ => ""
        };
    }

    private static string GetMouseButtonText(RecordedMouseButton button)
    {
        return button switch
        {
            RecordedMouseButton.Left => "Left",
            RecordedMouseButton.Right => "Right",
            RecordedMouseButton.Middle => "Middle",
            RecordedMouseButton.XButton1 => "X1",
            RecordedMouseButton.XButton2 => "X2",
            _ => "Mouse"
        };
    }

    private static string GetKeyText(Keys key)
    {
        return key switch
        {
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "Ctrl",
            Keys.Menu or Keys.LMenu or Keys.RMenu => "Alt",
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "Shift",
            Keys.Escape => "Esc",
            Keys.Space => "Space",
            Keys.Return => "Enter",
            Keys.Back => "Backspace",
            Keys.Capital => "CapsLock",
            >= Keys.A and <= Keys.Z => key.ToString(),
            >= Keys.F1 and <= Keys.F24 => key.ToString(),
            >= Keys.D0 and <= Keys.D9 => ((int)(key - Keys.D0)).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => $"Num{(int)(key - Keys.NumPad0)}",
            _ => key.ToString()
        };
    }

    private sealed record PreviewMoveSegment(bool IsStationary, List<MacroEvent> Events);
    private sealed record FlushMoveResult(bool TrailingStationary, bool HadMovement);
}

public sealed class PreviewOverlayForm : Form
{
    private const int SeekAnimationMs = 160;
    private const int HudVisibleMs = 1800;
    private const int EventAnimationMs = 260;

    private readonly MacroPreview _preview;
    private readonly Rectangle _virtualBounds;
    private readonly Rectangle _transportBounds;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly PreviewSoundPlayer _soundPlayer = new();
    private readonly Font _markerFont = new("MS UI Gothic", 9F, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _helpFont = new("MS UI Gothic", 10F, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _seekClock = new();
    private readonly Stopwatch _hudClock = new();
    private readonly Stopwatch _postRollClock = new();
    private bool _isPlaying;
    private bool _isSeeking;
    private bool _isPostRolling;
    private bool _showHud;
    private long _currentMs;
    private long _visualMs;
    private long _seekStartMs;
    private long _seekTargetMs;
    private long _lastSoundTimeMs;

    public PreviewOverlayForm(
        Macro macro,
        NoiseSettings noise,
        int variantCount,
        Screen transportScreen,
        HumanMotionProfile? motionProfile = null)
    {
        _preview = MacroPreviewBuilder.Build(macro, noise, variantCount, motionProfile);
        _virtualBounds = GetVirtualBounds();
        _transportBounds = transportScreen.WorkingArea;

        StartPosition = FormStartPosition.Manual;
        Bounds = _virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.86;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Default;
        Text = "プレビュー";
        _timer.Interval = 16;
        _timer.Tick += TimerOnTick;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        ShowHud();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandlePreviewShortcut(e.KeyData))
        {
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        return HandlePreviewShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandlePreviewShortcut(Keys keyData)
    {
        var keyCode = keyData & Keys.KeyCode;
        switch (keyCode)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Space:
                TogglePreviewPlayback();
                return true;
            case Keys.Left:
                SeekRelative(-GetKeyboardSeekStep(keyData));
                return true;
            case Keys.Right:
                SeekRelative(GetKeyboardSeekStep(keyData));
                return true;
            case Keys.Home:
            case Keys.D1:
            case Keys.NumPad1:
                SeekTo(0);
                return true;
            case Keys.End:
            case Keys.D0:
            case Keys.NumPad0:
                SeekTo(_preview.DurationMs);
                return true;
            default:
                return false;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var steps = e.Delta / SystemInformation.MouseWheelScrollDelta;
        if (steps != 0)
        {
            SeekRelative(steps * 200);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        DrawPath(e.Graphics, _preview.PerfectPath, Color.Red, 1, 42);
        foreach (var noisyPath in _preview.NoisyPaths)
        {
            DrawPath(e.Graphics, noisyPath, Color.Gold, 1, 40);
        }

        foreach (var learnedVariant in _preview.LearnedPathSegments)
        {
            foreach (var segment in learnedVariant)
            {
                DrawPath(e.Graphics, segment.Points, Color.Magenta, 2, 150);
            }
        }

        var progressPath = GetProgressPath(_preview.PerfectTimedPath, _currentMs);
        DrawPath(e.Graphics, progressPath, Color.Red, 5, 245);
        DrawActiveInteractionSegment(e.Graphics);
        DrawCurrentPoint(e.Graphics, progressPath, Color.Red);

        DrawMarkers(e.Graphics, _preview.PerfectMarkers, Color.Red, true, true);
        foreach (var markers in _preview.NoisyMarkers)
        {
            DrawMarkers(e.Graphics, markers, Color.Gold, false, false);
        }

        DrawLegend(e.Graphics);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _soundPlayer.Dispose();
            _markerFont.Dispose();
            _helpFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int GetKeyboardSeekStep(Keys keyData)
    {
        if ((keyData & Keys.Control) == Keys.Control)
        {
            return 100;
        }

        return (keyData & Keys.Shift) == Keys.Shift ? 5000 : 1000;
    }

    private void TogglePreviewPlayback()
    {
        if (_isPlaying)
        {
            PausePreviewPlayback();
            return;
        }

        if (_currentMs >= _preview.DurationMs)
        {
            _currentMs = 0;
            _visualMs = _currentMs;
            _isSeeking = false;
            _isPostRolling = false;
        }

        StartPreviewPlayback();
    }

    private void StartPreviewPlayback()
    {
        if (_preview.DurationMs <= 0)
        {
            return;
        }

        _isPlaying = true;
        _isPostRolling = false;
        _visualMs = _currentMs;
        _lastSoundTimeMs = _currentMs;
        _clock.Restart();
        EnsureTimerRunning();
        ShowHud();
    }

    private void PausePreviewPlayback()
    {
        _isPlaying = false;
        _clock.Reset();
        StopTimerIfIdle();
        ShowHud();
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        if (_isSeeking)
        {
            var progress = Math.Clamp(_seekClock.ElapsedMilliseconds / (double)SeekAnimationMs, 0.0, 1.0);
            var eased = SmoothStep(progress);
            _currentMs = (long)Math.Round(_seekStartMs + (_seekTargetMs - _seekStartMs) * eased);
            _visualMs = _currentMs;
            if (progress >= 1.0)
            {
                _currentMs = _seekTargetMs;
                _visualMs = _currentMs;
                _isSeeking = false;
                if (_isPlaying)
                {
                    _lastSoundTimeMs = _currentMs;
                    _clock.Restart();
                }
            }
        }

        if (_isPlaying && !_isSeeking)
        {
            var previousMs = _currentMs;
            var elapsed = _clock.ElapsedMilliseconds;
            _clock.Restart();
            var next = _currentMs + elapsed;
            if (next >= _preview.DurationMs)
            {
                next = _preview.DurationMs;
                _isPlaying = false;
                _clock.Reset();
                StartPostRoll();
            }

            _currentMs = next;
            _visualMs = _currentMs;
            PlaySoundsBetween(previousMs, _currentMs);
        }

        if (_isPostRolling)
        {
            _visualMs = Math.Min(_preview.DurationMs + EventAnimationMs, _preview.DurationMs + _postRollClock.ElapsedMilliseconds);
            if (_visualMs >= _preview.DurationMs + EventAnimationMs)
            {
                _isPostRolling = false;
            }
        }

        if (_showHud && _hudClock.ElapsedMilliseconds >= HudVisibleMs)
        {
            _showHud = false;
        }

        Invalidate();
        StopTimerIfIdle();
    }

    private void SeekRelative(long deltaMs)
    {
        SeekTo(_currentMs + deltaMs);
    }

    private void SeekTo(long timeMs)
    {
        var target = Math.Clamp(timeMs, 0, _preview.DurationMs);
        if (target == _currentMs && !_isSeeking)
        {
            ShowHud();
            return;
        }

        _seekStartMs = _currentMs;
        _seekTargetMs = target;
        _isSeeking = true;
        _isPostRolling = false;
        _seekClock.Restart();
        EnsureTimerRunning();
        ShowHud();
    }

    private void EnsureTimerRunning()
    {
        if (!_timer.Enabled)
        {
            _timer.Start();
        }
    }

    private void StopTimerIfIdle()
    {
        if (!_isPlaying && !_isSeeking && !_isPostRolling && !_showHud)
        {
            _timer.Stop();
        }
    }

    private void ShowHud()
    {
        _showHud = true;
        _hudClock.Restart();
        EnsureTimerRunning();
        Invalidate();
    }

    private void PlaySoundsBetween(long fromMs, long toMs)
    {
        if (toMs < fromMs)
        {
            _lastSoundTimeMs = toMs;
            return;
        }

        var startMs = Math.Max(fromMs, _lastSoundTimeMs);
        foreach (var marker in _preview.PerfectMarkers.Where(item => item.TimeMs > startMs && item.TimeMs <= toMs))
        {
            _soundPlayer.Play(marker.Kind);
        }

        _lastSoundTimeMs = toMs;
    }

    private void StartPostRoll()
    {
        _isPostRolling = _preview.PerfectMarkers.Any(item =>
            item.TimeMs >= _preview.DurationMs - EventAnimationMs
            && item.TimeMs <= _preview.DurationMs);
        if (_isPostRolling)
        {
            _visualMs = _currentMs;
            _postRollClock.Restart();
            EnsureTimerRunning();
        }
    }

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private void DrawCurrentPoint(Graphics graphics, List<Point> progressPath, Color color)
    {
        if (progressPath.Count == 0)
        {
            return;
        }

        var point = ToLocal(progressPath[^1]);
        using var fill = new SolidBrush(Color.FromArgb(245, color));
        using var outline = new Pen(Color.FromArgb(230, Color.Black), 2);
        var rect = new Rectangle(point.X - 5, point.Y - 5, 10, 10);
        graphics.FillEllipse(fill, rect);
        graphics.DrawEllipse(outline, rect);
    }

    private static List<Point> GetProgressPath(List<TimedPreviewPoint> path, long timeMs)
    {
        return path
            .Where(point => point.TimeMs <= timeMs)
            .Select(point => point.Point)
            .ToList();
    }

    private void DrawPath(Graphics graphics, List<Point> points, Color color, int width, int alpha = 220)
    {
        if (points.Count < 2)
        {
            return;
        }

        using var pen = new Pen(Color.FromArgb(alpha, color), width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var displayPoints = DrawingPathOptimizer.SimplifyForDisplay(points.Select(ToLocal));
        if (displayPoints.Count >= 2)
        {
            graphics.DrawLines(pen, displayPoints.ToArray());
        }
    }

    private void DrawActiveInteractionSegment(Graphics graphics)
    {
        var segment = _preview.InteractionSegments
            .FirstOrDefault(item => _currentMs >= item.StartMs && _currentMs <= item.EndMs);
        if (segment is null)
        {
            return;
        }

        var points = GetSegmentPath(_preview.PerfectTimedPath, segment);
        DrawPath(graphics, points, Color.FromArgb(95, 220, 255), 6, 245);
    }

    private static List<Point> GetSegmentPath(
        List<TimedPreviewPoint> path,
        PreviewInteractionSegment segment)
    {
        var points = new List<Point> { segment.Start };
        points.AddRange(path
            .Where(item => item.TimeMs > segment.StartMs && item.TimeMs < segment.EndMs)
            .Select(item => item.Point));
        points.Add(segment.End);
        return points;
    }

    private static Point? InterpolatePoint(List<TimedPreviewPoint> path, long timeMs)
    {
        if (path.Count == 0)
        {
            return null;
        }

        var previous = path[0];
        foreach (var next in path.Skip(1))
        {
            if (next.TimeMs < timeMs)
            {
                previous = next;
                continue;
            }

            var span = Math.Max(1, next.TimeMs - previous.TimeMs);
            var t = Math.Clamp((timeMs - previous.TimeMs) / (double)span, 0.0, 1.0);
            return new Point(
                (int)Math.Round(previous.Point.X + (next.Point.X - previous.Point.X) * t),
                (int)Math.Round(previous.Point.Y + (next.Point.Y - previous.Point.Y) * t));
        }

        return path[^1].Point;
    }

    private void DrawMarkers(Graphics graphics, List<PreviewMarker> markers, Color color, bool showText, bool animate)
    {
        foreach (var marker in markers)
        {
            var point = ToLocal(marker.Location);
            var visual = animate
                ? GetMarkerVisual(marker)
                : new MarkerVisual(7, 70, 70);
            using var pen = new Pen(Color.FromArgb(visual.MarkerAlpha, color), visual.Width);
            DrawCrossMarker(graphics, pen, point, marker.Kind, visual.Size);

            if (showText && !string.IsNullOrEmpty(marker.Text))
            {
                using var textBrush = new SolidBrush(Color.FromArgb(visual.TextAlpha, Color.White));
                using var outlineBrush = new SolidBrush(Color.FromArgb(visual.TextAlpha, Color.Black));
                DrawOutlinedText(graphics, marker.Text, point.X + 12, point.Y + 5, textBrush, outlineBrush);
            }
        }
    }

    private MarkerVisual GetMarkerVisual(PreviewMarker marker)
    {
        var delta = _visualMs - marker.TimeMs;
        var isRelease = marker.Kind is MacroEventKind.MouseUp or MacroEventKind.KeyUp;
        var baseAlpha = _visualMs >= marker.TimeMs
            ? isRelease ? 55 : 150
            : 55;
        var textAlpha = _visualMs >= marker.TimeMs
            ? isRelease ? 95 : 220
            : 90;
        var baseSize = marker.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? 9 : 7;
        var width = _visualMs >= marker.TimeMs ? 2 : 1;

        if (marker.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown
            && delta >= 0 && delta <= EventAnimationMs)
        {
            var progress = delta / (double)EventAnimationMs;
            var size = (int)Math.Round(Lerp(24, baseSize, SmoothStep(progress)));
            var alpha = (int)Math.Round(Lerp(45, 255, SmoothStep(progress)));
            return new MarkerVisual(size, alpha, alpha, 3);
        }

        if (marker.Kind is MacroEventKind.MouseUp or MacroEventKind.KeyUp
            && delta >= 0 && delta <= EventAnimationMs)
        {
            var progress = delta / (double)EventAnimationMs;
            var size = (int)Math.Round(Lerp(baseSize, 24, SmoothStep(progress)));
            var alpha = (int)Math.Round(Lerp(255, 30, SmoothStep(progress)));
            return new MarkerVisual(size, alpha, alpha, 3);
        }

        return new MarkerVisual(baseSize, baseAlpha, textAlpha, width);
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private static void DrawCrossMarker(Graphics graphics, Pen pen, Point point, MacroEventKind kind, int? overrideSize = null)
    {
        var size = overrideSize ?? (kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? 9 : 7);
        graphics.DrawLine(pen, point.X - size, point.Y - size, point.X + size, point.Y + size);
        graphics.DrawLine(pen, point.X - size, point.Y + size, point.X + size, point.Y - size);

    }

    private void DrawOutlinedText(
        Graphics graphics,
        string text,
        int x,
        int y,
        Brush textBrush,
        Brush outlineBrush)
    {
        for (var ox = -1; ox <= 1; ox++)
        {
            for (var oy = -1; oy <= 1; oy++)
            {
                if (ox == 0 && oy == 0)
                {
                    continue;
                }

                graphics.DrawString(text, _markerFont, outlineBrush, x + ox, y + oy);
            }
        }

        graphics.DrawString(text, _markerFont, textBrush, x, y);
    }

    private void DrawLegend(Graphics graphics)
    {
        if (!_showHud)
        {
            return;
        }

        var remaining = Math.Clamp((HudVisibleMs - _hudClock.ElapsedMilliseconds) / 500.0, 0.0, 1.0);
        var alphaScale = _hudClock.ElapsedMilliseconds > HudVisibleMs - 500 ? remaining : 1.0;
        var bgAlpha = (int)Math.Round(190 * alphaScale);
        var textAlpha = (int)Math.Round(245 * alphaScale);
        var mutedAlpha = (int)Math.Round(200 * alphaScale);
        using var whiteBrush = new SolidBrush(Color.FromArgb(textAlpha, Color.White));
        using var mutedBrush = new SolidBrush(Color.FromArgb(mutedAlpha, Color.Gainsboro));
        using var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, Color.Black));
        var lines = new[]
        {
            $"{(_isPlaying ? "再生中" : "停止中")}  {_currentMs:N0} / {_preview.DurationMs:N0} ms",
            "赤: 完全再現  黄: 通常ノイズ  マゼンタ: 学習サンプル",
            "Home または 1 = 先頭   End または 0 = 最後   Esc = 終了"
        };
        var width = 610;
        var height = 76;
        var screenBox = new Rectangle(
            _transportBounds.Right - width - 18,
            _transportBounds.Bottom - height - 18,
            width,
            height);
        var box = ToLocalRectangle(screenBox);
        graphics.FillRectangle(bgBrush, box);
        graphics.DrawString(lines[0], _helpFont, whiteBrush, box.Left + 14, box.Top + 10);
        graphics.DrawString(lines[1], _helpFont, mutedBrush, box.Left + 14, box.Top + 32);
        graphics.DrawString(lines[2], _helpFont, mutedBrush, box.Left + 14, box.Top + 52);
    }

    private Point ToLocal(Point screenPoint)
    {
        return new Point(screenPoint.X - _virtualBounds.Left, screenPoint.Y - _virtualBounds.Top);
    }

    private Rectangle ToLocalRectangle(Rectangle screenRectangle)
    {
        return new Rectangle(
            screenRectangle.Left - _virtualBounds.Left,
            screenRectangle.Top - _virtualBounds.Top,
            screenRectangle.Width,
            screenRectangle.Height);
    }

    private static Rectangle GetVirtualBounds()
    {
        var left = SystemInformation.VirtualScreen.Left;
        var top = SystemInformation.VirtualScreen.Top;
        var right = SystemInformation.VirtualScreen.Right;
        var bottom = SystemInformation.VirtualScreen.Bottom;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private readonly record struct MarkerVisual(int Size, int MarkerAlpha, int TextAlpha, int Width = 2);
}

internal sealed class PreviewSoundPlayer : IDisposable
{
    private readonly Dictionary<MacroEventKind, List<WaveSound>> _sounds = new();
    private readonly List<WavePlayback> _activePlaybacks = new();
    private readonly Random _random = new();
    private readonly object _lock = new();

    public PreviewSoundPlayer()
    {
        var soundsPath = ResolveSoundsPath();
        AddFiles(MacroEventKind.MouseDown, soundsPath, "click_on-*.wav");
        AddFiles(MacroEventKind.MouseUp, soundsPath, "click_off-*.wav");
        AddFiles(MacroEventKind.KeyDown, soundsPath, "keyboard_on-*.wav");
        AddFiles(MacroEventKind.KeyUp, soundsPath, "keyboard_off-*.wav");
    }

    public void Play(MacroEventKind kind)
    {
        if (!_sounds.TryGetValue(kind, out var sounds) || sounds.Count == 0)
        {
            return;
        }

        var sound = sounds[_random.Next(sounds.Count)];
        try
        {
            var playback = new WavePlayback(sound, RemovePlayback);
            lock (_lock)
            {
                _activePlaybacks.Add(playback);
            }

            try
            {
                playback.Play();
            }
            catch
            {
                RemovePlayback(playback);
                throw;
            }
        }
        catch
        {
            // Preview audio is optional; drawing must keep working even if a file cannot play.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var playback in _activePlaybacks.ToList())
            {
                playback.Dispose();
            }

            _activePlaybacks.Clear();
        }
    }

    private void AddFiles(MacroEventKind kind, string soundsPath, string pattern)
    {
        if (!Directory.Exists(soundsPath))
        {
            return;
        }

        var files = Directory.GetFiles(soundsPath, pattern)
            .OrderBy(item => item)
            .ToList();
        var sounds = files
            .Select(WaveSound.TryLoad)
            .Where(sound => sound is not null)
            .Cast<WaveSound>()
            .ToList();
        if (sounds.Count > 0)
        {
            _sounds[kind] = sounds;
        }
    }

    private void RemovePlayback(WavePlayback playback)
    {
        lock (_lock)
        {
            _activePlaybacks.Remove(playback);
        }

        playback.Dispose();
    }

    private static string ResolveSoundsPath()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "sounds");
        if (Directory.Exists(outputPath))
        {
            return outputPath;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "sounds");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return outputPath;
    }

    private sealed class WaveSound
    {
        private WaveSound(WaveFormat format, byte[] data)
        {
            Format = format;
            Data = data;
            DurationMs = Math.Max(1, (int)Math.Ceiling(data.Length * 1000.0 / Math.Max(1, format.AvgBytesPerSec)));
        }

        public WaveFormat Format { get; }
        public byte[] Data { get; }
        public int DurationMs { get; }

        public static WaveSound? TryLoad(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                if (new string(reader.ReadChars(4)) != "RIFF")
                {
                    return null;
                }

                _ = reader.ReadInt32();
                if (new string(reader.ReadChars(4)) != "WAVE")
                {
                    return null;
                }

                WaveFormat? format = null;
                byte[]? data = null;
                while (stream.Position + 8 <= stream.Length)
                {
                    var chunkId = new string(reader.ReadChars(4));
                    var chunkSize = reader.ReadInt32();
                    var chunkStart = stream.Position;
                    if (chunkId == "fmt ")
                    {
                        format = new WaveFormat
                        {
                            FormatTag = reader.ReadUInt16(),
                            Channels = reader.ReadUInt16(),
                            SamplesPerSec = reader.ReadUInt32(),
                            AvgBytesPerSec = reader.ReadUInt32(),
                            BlockAlign = reader.ReadUInt16(),
                            BitsPerSample = reader.ReadUInt16(),
                            CbSize = chunkSize > 16 ? reader.ReadUInt16() : (ushort)0
                        };
                    }
                    else if (chunkId == "data")
                    {
                        data = reader.ReadBytes(chunkSize);
                    }

                    stream.Position = chunkStart + chunkSize + (chunkSize % 2);
                }

                if (format is null || data is null || data.Length == 0)
                {
                    return null;
                }

                return new WaveSound(format.Value, data);
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed class WavePlayback : IDisposable
    {
        private readonly WaveSound _sound;
        private readonly Action<WavePlayback> _completed;
        private readonly GCHandle _dataHandle;
        private readonly IntPtr _headerPtr;
        private IntPtr _waveOut;
        private bool _prepared;
        private bool _disposed;

        public WavePlayback(WaveSound sound, Action<WavePlayback> completed)
        {
            _sound = sound;
            _completed = completed;
            _dataHandle = GCHandle.Alloc(sound.Data, GCHandleType.Pinned);
            _headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        }

        public void Play()
        {
            var format = _sound.Format;
            var result = waveOutOpen(out _waveOut, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutOpen failed: {result}");
            }

            var header = new WaveHeader
            {
                Data = _dataHandle.AddrOfPinnedObject(),
                BufferLength = _sound.Data.Length
            };
            Marshal.StructureToPtr(header, _headerPtr, false);

            result = waveOutPrepareHeader(_waveOut, _headerPtr, Marshal.SizeOf<WaveHeader>());
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutPrepareHeader failed: {result}");
            }

            _prepared = true;
            result = waveOutWrite(_waveOut, _headerPtr, Marshal.SizeOf<WaveHeader>());
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutWrite failed: {result}");
            }

            _ = CompleteLaterAsync();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
                if (_prepared)
                {
                    waveOutUnprepareHeader(_waveOut, _headerPtr, Marshal.SizeOf<WaveHeader>());
                }

                waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
            }

            if (_dataHandle.IsAllocated)
            {
                _dataHandle.Free();
            }

            Marshal.FreeHGlobal(_headerPtr);
        }

        private async Task CompleteLaterAsync()
        {
            await Task.Delay(_sound.DurationMs + 80).ConfigureAwait(false);
            _completed(this);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(
        out IntPtr waveOut,
        int deviceId,
        ref WaveFormat format,
        IntPtr callback,
        IntPtr instance,
        int flags);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr waveHeader, int size);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutWrite(IntPtr waveOut, IntPtr waveHeader, int size);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutUnprepareHeader(IntPtr waveOut, IntPtr waveHeader, int size);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutClose(IntPtr waveOut);
}
