using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class HumanMotionProfile
{
    public int Version { get; set; } = 1;
    public List<HumanMotionSample> MoveSamples { get; set; } = new();
    public List<HumanHoldSample> MouseHoldSamples { get; set; } = new();
    public List<HumanHoldSample> KeyHoldSamples { get; set; } = new();

    public int TotalSampleCount => MoveSamples.Count + MouseHoldSamples.Count + KeyHoldSamples.Count;
}

public sealed class HumanMotionSample
{
    public bool IsDragging { get; set; }
    public double DistancePx { get; set; }
    public long DurationMs { get; set; }
    public List<NormalizedMotionPoint> Points { get; set; } = new();
}

public sealed class NormalizedMotionPoint
{
    public double T { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class HumanHoldSample
{
    public long DurationMs { get; set; }
}

public readonly record struct HumanMotionPathPoint(double TimeMs, Point Point);

public readonly record struct HumanMotionReferencePoint(double TimeMs, Point Point);

public static class HumanMotionStore
{
    private const string FileName = "human-motion-profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetPath(string macroStorePath)
    {
        var directory = Path.GetDirectoryName(macroStorePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = MacroStore.DefaultDirectory;
        }

        return Path.Combine(directory, FileName);
    }

    public static HumanMotionProfile Load(string macroStorePath)
    {
        var path = GetPath(macroStorePath);
        try
        {
            if (!File.Exists(path))
            {
                return new HumanMotionProfile();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HumanMotionProfile>(json, JsonOptions) ?? new HumanMotionProfile();
        }
        catch
        {
            return new HumanMotionProfile();
        }
    }

    public static void Save(string macroStorePath, HumanMotionProfile profile)
    {
        var path = GetPath(macroStorePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
    }
}

public static class HumanMotionProfileBuilder
{
    private const int StationaryRadiusPx = 2;
    private const int MinMoveDistancePx = 24;
    private const int MinMoveDurationMs = 30;
    private const int MaxMoveDurationMs = 6000;
    private const int MaxMoveSamples = 1000;
    private const int MaxHoldSamples = 1000;
    private const int MaxPointsPerSample = 72;

    public static int AddSession(HumanMotionProfile profile, IReadOnlyList<MacroEvent> rawEvents)
    {
        var before = profile.TotalSampleCount;
        var events = rawEvents
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();

        AddMoveSamples(profile, events);
        AddHoldSamples(profile, events);
        Trim(profile);
        return profile.TotalSampleCount - before;
    }

    private static void AddMoveSamples(HumanMotionProfile profile, IReadOnlyList<MacroEvent> events)
    {
        var pressedButtons = new HashSet<RecordedMouseButton>();
        var previousPoint = (Point?)null;
        var runAnchor = (Point?)null;
        var run = new List<MacroEvent>();
        var runDragging = false;

        foreach (var macroEvent in events)
        {
            if (macroEvent.Kind == MacroEventKind.MouseMove)
            {
                if (run.Count == 0)
                {
                    runDragging = pressedButtons.Count > 0;
                    runAnchor = previousPoint ?? new Point(macroEvent.X, macroEvent.Y);
                }

                run.Add(macroEvent);
                previousPoint = new Point(macroEvent.X, macroEvent.Y);
                continue;
            }

            FlushRun(profile, run, runAnchor, runDragging);
            run.Clear();
            runAnchor = null;

            if (macroEvent.Kind == MacroEventKind.MouseDown)
            {
                pressedButtons.Add(macroEvent.Button);
            }
            else if (macroEvent.Kind == MacroEventKind.MouseUp)
            {
                pressedButtons.Remove(macroEvent.Button);
            }

            if (HasPoint(macroEvent))
            {
                previousPoint = new Point(macroEvent.X, macroEvent.Y);
            }
        }

        FlushRun(profile, run, runAnchor, runDragging);
    }

    private static void FlushRun(
        HumanMotionProfile profile,
        IReadOnlyList<MacroEvent> run,
        Point? previousPoint,
        bool isDragging)
    {
        if (run.Count < 3)
        {
            return;
        }

        var index = 0;
        var anchor = previousPoint ?? new Point(run[0].X, run[0].Y);
        while (index < run.Count)
        {
            while (index < run.Count && DistanceSquared(new Point(run[index].X, run[index].Y), anchor) <= StationaryRadiusPx * StationaryRadiusPx)
            {
                anchor = new Point(run[index].X, run[index].Y);
                index++;
            }

            var movingStart = index;
            if (movingStart >= run.Count)
            {
                break;
            }

            index++;
            while (index < run.Count)
            {
                var current = new Point(run[index].X, run[index].Y);
                var previous = new Point(run[index - 1].X, run[index - 1].Y);
                if (DistanceSquared(current, previous) <= StationaryRadiusPx * StationaryRadiusPx)
                {
                    break;
                }

                index++;
            }

            var segment = run.Skip(movingStart).Take(index - movingStart).ToList();
            if (TryCreateSample(segment, isDragging, out var sample))
            {
                profile.MoveSamples.Add(sample);
            }

            anchor = new Point(run[Math.Min(index, run.Count) - 1].X, run[Math.Min(index, run.Count) - 1].Y);
        }
    }

    private static bool TryCreateSample(IReadOnlyList<MacroEvent> segment, bool isDragging, out HumanMotionSample sample)
    {
        sample = new HumanMotionSample();
        if (segment.Count < 3)
        {
            return false;
        }

        var start = new Point(segment[0].X, segment[0].Y);
        var end = new Point(segment[^1].X, segment[^1].Y);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var duration = segment[^1].TimeOffsetMs - segment[0].TimeOffsetMs;
        if (distance < MinMoveDistancePx || duration < MinMoveDurationMs || duration > MaxMoveDurationMs)
        {
            return false;
        }

        var unitX = dx / distance;
        var unitY = dy / distance;
        var normalX = -unitY;
        var normalY = unitX;
        var points = Downsample(segment, MaxPointsPerSample)
            .Select(item =>
            {
                var relX = item.X - start.X;
                var relY = item.Y - start.Y;
                return new NormalizedMotionPoint
                {
                    T = Math.Clamp((item.TimeOffsetMs - segment[0].TimeOffsetMs) / (double)Math.Max(1, duration), 0.0, 1.0),
                    X = (relX * unitX + relY * unitY) / distance,
                    Y = (relX * normalX + relY * normalY) / distance
                };
            })
            .ToList();

        points[0] = new NormalizedMotionPoint { T = 0.0, X = 0.0, Y = 0.0 };
        points[^1] = new NormalizedMotionPoint { T = 1.0, X = 1.0, Y = 0.0 };
        sample = new HumanMotionSample
        {
            IsDragging = isDragging,
            DistancePx = distance,
            DurationMs = duration,
            Points = points
        };
        return true;
    }

    private static List<MacroEvent> Downsample(IReadOnlyList<MacroEvent> source, int maxPoints)
    {
        if (source.Count <= maxPoints)
        {
            return source.ToList();
        }

        var result = new List<MacroEvent>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            var sourceIndex = (int)Math.Round(i * (source.Count - 1) / (double)(maxPoints - 1));
            result.Add(source[sourceIndex]);
        }

        return result;
    }

    private static void AddHoldSamples(HumanMotionProfile profile, IReadOnlyList<MacroEvent> events)
    {
        var mouseDown = new Dictionary<RecordedMouseButton, long>();
        var keyDown = new Dictionary<Keys, long>();
        foreach (var macroEvent in events)
        {
            switch (macroEvent.Kind)
            {
                case MacroEventKind.MouseDown:
                    mouseDown[macroEvent.Button] = macroEvent.TimeOffsetMs;
                    break;
                case MacroEventKind.MouseUp:
                    if (mouseDown.Remove(macroEvent.Button, out var mouseStart))
                    {
                        AddHold(profile.MouseHoldSamples, macroEvent.TimeOffsetMs - mouseStart);
                    }

                    break;
                case MacroEventKind.KeyDown:
                    keyDown[macroEvent.KeyCode] = macroEvent.TimeOffsetMs;
                    break;
                case MacroEventKind.KeyUp:
                    if (keyDown.Remove(macroEvent.KeyCode, out var keyStart))
                    {
                        AddHold(profile.KeyHoldSamples, macroEvent.TimeOffsetMs - keyStart);
                    }

                    break;
            }
        }
    }

    private static void AddHold(List<HumanHoldSample> samples, long durationMs)
    {
        if (durationMs is >= 15 and <= 5000)
        {
            samples.Add(new HumanHoldSample { DurationMs = durationMs });
        }
    }

    private static void Trim(HumanMotionProfile profile)
    {
        if (profile.MoveSamples.Count > MaxMoveSamples)
        {
            profile.MoveSamples.RemoveRange(0, profile.MoveSamples.Count - MaxMoveSamples);
        }

        if (profile.MouseHoldSamples.Count > MaxHoldSamples)
        {
            profile.MouseHoldSamples.RemoveRange(0, profile.MouseHoldSamples.Count - MaxHoldSamples);
        }

        if (profile.KeyHoldSamples.Count > MaxHoldSamples)
        {
            profile.KeyHoldSamples.RemoveRange(0, profile.KeyHoldSamples.Count - MaxHoldSamples);
        }
    }

    private static bool HasPoint(MacroEvent macroEvent)
    {
        return macroEvent.X != 0 || macroEvent.Y != 0;
    }

    private static int DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

public static class HumanMotionPathGenerator
{
    public static bool TryCreatePath(
        HumanMotionProfile? profile,
        Point start,
        Point end,
        double startTimeMs,
        double endTimeMs,
        double frameIntervalMs,
        bool isDragging,
        int maxReferenceDeviationPx,
        IReadOnlyList<HumanMotionReferencePoint>? referencePath,
        Random random,
        out List<HumanMotionPathPoint> path)
    {
        path = new List<HumanMotionPathPoint>();
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var duration = endTimeMs - startTimeMs;
        if (profile is null
            || profile.MoveSamples.Count == 0
            || distance < 24.0
            || duration < 15.0
            || frameIntervalMs <= 0)
        {
            return false;
        }

        var unitX = dx / distance;
        var unitY = dy / distance;
        var normalX = -unitY;
        var normalY = unitX;
        foreach (var sample in SelectSamples(profile, distance, duration, isDragging, random))
        {
            foreach (var sideSign in GetSideSigns(random))
            {
                path = CreatePath(
                    sample,
                    start,
                    end,
                    startTimeMs,
                    endTimeMs,
                    frameIntervalMs,
                    distance,
                    unitX,
                    unitY,
                    normalX,
                    normalY,
                    sideSign,
                    0.85 + random.NextDouble() * 0.3);
                if (PathFitsReference(path, referencePath, maxReferenceDeviationPx))
                {
                    return true;
                }
            }
        }

        path.Clear();
        return false;
    }

    private static List<HumanMotionPathPoint> CreatePath(
        HumanMotionSample sample,
        Point start,
        Point end,
        double startTimeMs,
        double endTimeMs,
        double frameIntervalMs,
        double distance,
        double unitX,
        double unitY,
        double normalX,
        double normalY,
        double sideSign,
        double sideScale)
    {
        var path = new List<HumanMotionPathPoint>();
        var duration = endTimeMs - startTimeMs;
        var nextFrame = Math.Ceiling((startTimeMs + 0.001) / frameIntervalMs) * frameIntervalMs;
        for (var timeMs = nextFrame; timeMs < endTimeMs; timeMs += frameIntervalMs)
        {
            var progress = Math.Clamp((timeMs - startTimeMs) / Math.Max(0.001, duration), 0.0, 1.0);
            path.Add(new HumanMotionPathPoint(timeMs, WarpPoint(sample, progress, start, distance, unitX, unitY, normalX, normalY, sideSign, sideScale)));
        }

        path.Add(new HumanMotionPathPoint(endTimeMs, end));
        return path;
    }

    private static List<HumanMotionSample> SelectSamples(
        HumanMotionProfile profile,
        double distance,
        double duration,
        bool isDragging,
        Random random)
    {
        var candidates = profile.MoveSamples
            .Where(item => item.Points.Count >= 2 && item.DistancePx > 0 && item.DurationMs > 0)
            .Where(item => item.IsDragging == isDragging)
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = profile.MoveSamples
                .Where(item => item.Points.Count >= 2 && item.DistancePx > 0 && item.DurationMs > 0)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<HumanMotionSample>();
        }

        return candidates
            .Select(item => new
            {
                Sample = item,
                Score = Math.Abs(Math.Log(Math.Max(0.001, distance / item.DistancePx)))
                    + Math.Abs(Math.Log(Math.Max(0.001, duration / item.DurationMs))) * 0.45
                    + (item.IsDragging == isDragging ? 0.0 : 0.8)
            })
            .OrderBy(item => item.Score)
            .ThenBy(_ => random.Next())
            .Take(Math.Min(12, candidates.Count))
            .Select(item => item.Sample)
            .ToList();
    }

    private static double[] GetSideSigns(Random random)
    {
        return random.Next(0, 2) == 0
            ? new[] { -1.0, 1.0 }
            : new[] { 1.0, -1.0 };
    }

    private static bool PathFitsReference(
        IReadOnlyList<HumanMotionPathPoint> path,
        IReadOnlyList<HumanMotionReferencePoint>? referencePath,
        int maxReferenceDeviationPx)
    {
        if (referencePath is null || referencePath.Count < 2)
        {
            return true;
        }

        if (maxReferenceDeviationPx <= 0)
        {
            return false;
        }

        var maxSquared = maxReferenceDeviationPx * maxReferenceDeviationPx;
        foreach (var point in path)
        {
            var reference = InterpolateReference(referencePath, point.TimeMs);
            var dx = point.Point.X - reference.X;
            var dy = point.Point.Y - reference.Y;
            if (dx * dx + dy * dy > maxSquared)
            {
                return false;
            }
        }

        return true;
    }

    private static Point InterpolateReference(IReadOnlyList<HumanMotionReferencePoint> referencePath, double timeMs)
    {
        if (timeMs <= referencePath[0].TimeMs)
        {
            return referencePath[0].Point;
        }

        for (var i = 1; i < referencePath.Count; i++)
        {
            var next = referencePath[i];
            if (next.TimeMs < timeMs)
            {
                continue;
            }

            var previous = referencePath[i - 1];
            var span = Math.Max(0.001, next.TimeMs - previous.TimeMs);
            var t = Math.Clamp((timeMs - previous.TimeMs) / span, 0.0, 1.0);
            return new Point(
                (int)Math.Round(previous.Point.X + (next.Point.X - previous.Point.X) * t),
                (int)Math.Round(previous.Point.Y + (next.Point.Y - previous.Point.Y) * t));
        }

        return referencePath[^1].Point;
    }

    private static Point WarpPoint(
        HumanMotionSample sample,
        double progress,
        Point start,
        double distance,
        double unitX,
        double unitY,
        double normalX,
        double normalY,
        double sideSign,
        double sideScale)
    {
        var point = Interpolate(sample.Points, progress);
        var forward = point.X * distance;
        var side = point.Y * distance * sideSign * sideScale;
        return new Point(
            (int)Math.Round(start.X + unitX * forward + normalX * side),
            (int)Math.Round(start.Y + unitY * forward + normalY * side));
    }

    private static NormalizedMotionPoint Interpolate(IReadOnlyList<NormalizedMotionPoint> points, double progress)
    {
        if (progress <= 0)
        {
            return points[0];
        }

        if (progress >= 1)
        {
            return points[^1];
        }

        var index = 0;
        while (index < points.Count - 2 && points[index + 1].T < progress)
        {
            index++;
        }

        var current = points[index];
        var next = points[Math.Min(points.Count - 1, index + 1)];
        var span = Math.Max(0.0001, next.T - current.T);
        var t = Math.Clamp((progress - current.T) / span, 0.0, 1.0);
        return new NormalizedMotionPoint
        {
            T = progress,
            X = current.X + (next.X - current.X) * t,
            Y = current.Y + (next.Y - current.Y) * t
        };
    }
}
