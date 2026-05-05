namespace AutomationTool;

public static class MacroTimingNormalizer
{
    public static List<MacroEvent> NormalizeFixedEventIntervals(
        IReadOnlyList<MacroEvent> sourceEvents,
        RecordingOptions options,
        int timeJitterMs = 0,
        Random? random = null)
    {
        var events = sourceEvents
            .Select(CloneEvent)
            .OrderBy(item => item.TimeOffsetMs)
            .ToList();
        if (events.Count == 0)
        {
            return events;
        }

        var markerIndexes = events
            .Select((macroEvent, index) => new { macroEvent, index })
            .Where(item => IsEventMarker(item.macroEvent))
            .Select(item => item.index)
            .ToList();
        if (markerIndexes.Count == 0)
        {
            RebaseToZero(events);
            return events;
        }

        var markerTimes = BuildMarkerTimes(events, markerIndexes, options, timeJitterMs, random);
        var normalized = new List<MacroEvent>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var macroEvent = CloneEvent(events[i]);
            macroEvent.TimeOffsetMs = markerTimes.TryGetValue(i, out var markerTime)
                ? markerTime
                : MapMoveTime(events, markerIndexes, markerTimes, i, options.EventIntervalMs);
            normalized.Add(macroEvent);
        }

        return normalized
            .OrderBy(item => item.TimeOffsetMs)
            .ThenBy(GetSortPriority)
            .ToList();
    }

    private static Dictionary<int, long> BuildMarkerTimes(
        IReadOnlyList<MacroEvent> events,
        IReadOnlyList<int> markerIndexes,
        RecordingOptions options,
        int timeJitterMs,
        Random? random)
    {
        var markerTimes = new Dictionary<int, long>();
        var intervalMs = Math.Max(1, options.EventIntervalMs);
        var holdMs = Math.Max(1, Math.Min(options.HoldDurationMs, intervalMs));
        long currentTime = ApplyJitter(intervalMs, timeJitterMs, random);
        markerTimes[markerIndexes[0]] = currentTime;

        for (var i = 1; i < markerIndexes.Count; i++)
        {
            var previous = events[markerIndexes[i - 1]];
            var current = events[markerIndexes[i]];
            var baseDelay = IsMatchingRelease(previous, current) ? holdMs : intervalMs;
            currentTime += ApplyJitter(baseDelay, timeJitterMs, random);
            markerTimes[markerIndexes[i]] = currentTime;
        }

        return markerTimes;
    }

    private static long MapMoveTime(
        IReadOnlyList<MacroEvent> events,
        IReadOnlyList<int> markerIndexes,
        IReadOnlyDictionary<int, long> markerTimes,
        int index,
        int trailingIntervalMs)
    {
        var previousMarker = markerIndexes.LastOrDefault(item => item < index, -1);
        var nextMarker = markerIndexes.FirstOrDefault(item => item > index, -1);
        var originalTime = events[index].TimeOffsetMs;

        if (previousMarker < 0 && nextMarker >= 0)
        {
            return MapTime(originalTime, 0, events[nextMarker].TimeOffsetMs, 0, markerTimes[nextMarker]);
        }

        if (previousMarker >= 0 && nextMarker >= 0)
        {
            return MapTime(
                originalTime,
                events[previousMarker].TimeOffsetMs,
                events[nextMarker].TimeOffsetMs,
                markerTimes[previousMarker],
                markerTimes[nextMarker]);
        }

        if (previousMarker >= 0)
        {
            var sourceEnd = Math.Max(events[^1].TimeOffsetMs, events[previousMarker].TimeOffsetMs + 1);
            return MapTime(
                originalTime,
                events[previousMarker].TimeOffsetMs,
                sourceEnd,
                markerTimes[previousMarker],
                markerTimes[previousMarker] + Math.Max(1, trailingIntervalMs));
        }

        return Math.Max(0, originalTime);
    }

    private static long MapTime(long value, long sourceStart, long sourceEnd, long targetStart, long targetEnd)
    {
        if (sourceEnd <= sourceStart)
        {
            return targetEnd;
        }

        var progress = Math.Clamp((value - sourceStart) / (double)(sourceEnd - sourceStart), 0.0, 1.0);
        return Math.Max(0, (long)Math.Round(targetStart + progress * (targetEnd - targetStart)));
    }

    private static long ApplyJitter(int baseDelayMs, int jitterMs, Random? random)
    {
        if (jitterMs <= 0 || random is null)
        {
            return baseDelayMs;
        }

        var jitter = random.Next(-jitterMs, jitterMs + 1);
        return Math.Max(1, baseDelayMs + jitter);
    }

    private static bool IsMatchingRelease(MacroEvent previous, MacroEvent current)
    {
        return previous.Kind switch
        {
            MacroEventKind.MouseDown => current.Kind == MacroEventKind.MouseUp && current.Button == previous.Button,
            MacroEventKind.KeyDown => current.Kind == MacroEventKind.KeyUp && current.KeyCode == previous.KeyCode,
            _ => false
        };
    }

    private static bool IsEventMarker(MacroEvent macroEvent)
    {
        return macroEvent.Kind is MacroEventKind.MouseDown
            or MacroEventKind.MouseUp
            or MacroEventKind.MouseWheel
            or MacroEventKind.KeyDown
            or MacroEventKind.KeyUp;
    }

    private static int GetSortPriority(MacroEvent macroEvent)
    {
        return macroEvent.Kind == MacroEventKind.MouseMove ? 0 : 1;
    }

    private static void RebaseToZero(List<MacroEvent> events)
    {
        var firstMs = events.Min(item => item.TimeOffsetMs);
        foreach (var macroEvent in events)
        {
            macroEvent.TimeOffsetMs = Math.Max(0, macroEvent.TimeOffsetMs - firstMs);
        }
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
}
