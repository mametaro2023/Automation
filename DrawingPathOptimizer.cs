using System.Drawing.Drawing2D;

namespace AutomationTool;

internal static class DrawingPathOptimizer
{
    public static List<Point> SimplifyForDisplay(IEnumerable<Point> source, double minDistancePx = 1.25, double lineTolerancePx = 0.45)
    {
        var points = source.ToList();
        if (points.Count <= 2)
        {
            return points;
        }

        var distanceFiltered = new List<Point>(points.Count) { points[0] };
        var minDistanceSquared = minDistancePx * minDistancePx;
        for (var i = 1; i < points.Count - 1; i++)
        {
            if (DistanceSquared(distanceFiltered[^1], points[i]) >= minDistanceSquared)
            {
                distanceFiltered.Add(points[i]);
            }
        }

        if (distanceFiltered[^1] != points[^1])
        {
            distanceFiltered.Add(points[^1]);
        }

        if (distanceFiltered.Count <= 2)
        {
            return distanceFiltered;
        }

        var simplified = new List<Point>(distanceFiltered.Count) { distanceFiltered[0] };
        for (var i = 1; i < distanceFiltered.Count - 1; i++)
        {
            var previous = simplified[^1];
            var current = distanceFiltered[i];
            var next = distanceFiltered[i + 1];
            if (DistanceFromLine(current, previous, next) > lineTolerancePx)
            {
                simplified.Add(current);
            }
        }

        if (simplified[^1] != distanceFiltered[^1])
        {
            simplified.Add(distanceFiltered[^1]);
        }

        return simplified;
    }

    public static GraphicsPath CreateSmoothPath(IReadOnlyList<Point> points)
    {
        var path = new GraphicsPath();
        if (points.Count == 0)
        {
            return path;
        }

        var simplified = SimplifyForDisplay(points);
        if (simplified.Count == 1)
        {
            path.AddEllipse(simplified[0].X - 1, simplified[0].Y - 1, 2, 2);
            return path;
        }

        if (simplified.Count < 4)
        {
            path.AddLines(simplified.Select(point => new PointF(point.X, point.Y)).ToArray());
            return path;
        }

        path.StartFigure();
        for (var i = 0; i < simplified.Count - 1; i++)
        {
            var p0 = simplified[Math.Max(0, i - 1)];
            var p1 = simplified[i];
            var p2 = simplified[i + 1];
            var p3 = simplified[Math.Min(simplified.Count - 1, i + 2)];
            var c1 = new PointF(
                p1.X + (p2.X - p0.X) / 6f,
                p1.Y + (p2.Y - p0.Y) / 6f);
            var c2 = new PointF(
                p2.X - (p3.X - p1.X) / 6f,
                p2.Y - (p3.Y - p1.Y) / 6f);

            path.AddBezier(
                new PointF(p1.X, p1.Y),
                c1,
                c2,
                new PointF(p2.X, p2.Y));
        }

        return path;
    }

    private static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double DistanceFromLine(Point point, Point lineStart, Point lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        var denominator = Math.Sqrt(dx * dx + dy * dy);
        if (denominator <= 0.001)
        {
            return Math.Sqrt(DistanceSquared(point, lineStart));
        }

        return Math.Abs(dy * point.X - dx * point.Y + lineEnd.X * lineStart.Y - lineEnd.Y * lineStart.X) / denominator;
    }
}
