using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tools.Creation;

/// <summary>Host-free acceptance of actual creation readback, never of a queued command or object ID alone.</summary>
internal static class CreationReadbackPolicy
{
    internal const double Tolerance = 0.000001;
    internal const int MaximumSampleLines = 10000; // operational refusal; never truncate a requested range
    internal readonly record struct Point(double X, double Y);
    internal readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY);
    internal sealed record SampleProof(string Handle, double Station, IReadOnlyList<Point> Vertices, Bounds Extents, bool ParentExact);

    internal static IReadOnlyList<double> Stations(double alignmentStart, double alignmentEnd,
        double start, double end, double interval, double left, double right)
    {
        if (new[] { alignmentStart, alignmentEnd, start, end, interval, left, right }.Any(v => !double.IsFinite(v)) ||
            alignmentEnd <= alignmentStart || start < alignmentStart || end > alignmentEnd || start >= end ||
            interval <= 0 || left <= 0 || right <= 0)
            throw new ArgumentException("Finite stations within the alignment and positive interval/extents are required.");
        var steps = Math.Floor((end - start) / interval);
        if (!double.IsFinite(steps) || steps + 1 > MaximumSampleLines)
            throw new ArgumentException($"Requested range exceeds the {MaximumSampleLines} sample-line operational limit; choose an explicit smaller range.");
        var stations = new List<double> { start };
        for (var i = 1; i <= (int)steps; i++)
        {
            var station = start + i * interval;
            if (station >= end) break;
            if (station <= stations[^1]) throw new ArgumentException("Interval is below numeric station resolution.");
            stations.Add(station);
        }
        stations.Add(end);
        if (stations.Count > MaximumSampleLines) throw new ArgumentException("Requested range exceeds the sample-line operational limit.");
        return stations;
    }

    internal static void RequireSample(double station, Point left, Point center, Point right, SampleProof actual)
    {
        if (string.IsNullOrWhiteSpace(actual.Handle) || !actual.ParentExact ||
            !Near(actual.Station, station) || !Valid(left) || !Valid(center) || !Valid(right) ||
            Distance(left, center) <= 0 || Distance(center, right) <= 0 ||
            actual.Vertices.Count < 3 || actual.Vertices.Any(p => !Valid(p)) ||
            !actual.Vertices.Any(p => Near(p, center)) ||
            !actual.Vertices.Any(p => Near(p, left)) || !actual.Vertices.Any(p => Near(p, right)) ||
            actual.Vertices.Any(p => !OnSegment(p, left, right)) || !Contains(actual.Extents, left) || !Contains(actual.Extents, right))
            throw new InvalidOperationException($"Sample line readback is incomplete or differs at station {station:R} ({actual.Handle}).");
    }

    internal static void RequireGroup(IReadOnlyList<double> stations, IReadOnlyList<SampleProof> lines, IReadOnlyList<string> groupHandles)
    {
        if (stations.Count == 0 || lines.Count != stations.Count || groupHandles.Count != stations.Count ||
            lines.Select(line => line.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != stations.Count ||
            groupHandles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stations.Count ||
            lines.Any(line => !groupHandles.Contains(line.Handle, StringComparer.OrdinalIgnoreCase)) ||
            stations.Where((station, i) => !Near(station, lines[i].Station)).Any())
            throw new InvalidOperationException("Created group does not contain exactly every requested sample line.");
    }

    internal static string SelectSource(IReadOnlyList<string> candidates, string? requestedHandle)
    {
        if (requestedHandle != null && (requestedHandle.Length == 0 || requestedHandle.Any(c => !Uri.IsHexDigit(c))))
            throw new ArgumentException("polyline_handle must be one hexadecimal model-space entity handle.");
        var matches = candidates.Where(handle => requestedHandle == null ||
            string.Equals(handle, requestedHandle, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(
            $"Expected one source polyline on the specified layer; found {matches.Length}. Specify its polyline_handle; no first-match selection is allowed.");
        return matches[0];
    }

    internal static void RequireAlignment(double sourceLength, double actualLength, double start, double end, int entities,
        IReadOnlyList<Point> expected, IReadOnlyList<Point> actual, Bounds bounds, bool sourcePreserved)
    {
        if (!sourcePreserved || entities <= 0 || !double.IsFinite(sourceLength) || sourceLength <= 0 ||
            !Near(actualLength, sourceLength) || !Near(end - start, sourceLength) || !double.IsFinite(start) ||
            expected.Count < 2 || actual.Count != expected.Count ||
            expected.Where((point, i) => !Near(point, actual[i]) || !Contains(bounds, point)).Any())
            throw new InvalidOperationException("Alignment readback is empty or differs from the selected source geometry/stations/extents.");
    }

    internal static bool Valid(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= Tolerance;
    private static bool Near(Point a, Point b) => Valid(a) && Valid(b) && Distance(a, b) <= Tolerance;
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static bool OnSegment(Point p, Point a, Point b)
    {
        var length = Distance(a,b);
        if (!double.IsFinite(length) || length <= 0) return false;
        var projection = ((p.X-a.X)*(b.X-a.X)+(p.Y-a.Y)*(b.Y-a.Y))/length;
        return projection >= -Tolerance && projection <= length+Tolerance &&
            Math.Abs((p.X-a.X)*(b.Y-a.Y)-(p.Y-a.Y)*(b.X-a.X))/length <= Tolerance;
    }
    private static bool Contains(Bounds b, Point p) => Valid(new(b.MinX,b.MinY)) && Valid(new(b.MaxX,b.MaxY)) &&
        b.MaxX >= b.MinX && b.MaxY >= b.MinY && (b.MaxX > b.MinX || b.MaxY > b.MinY) &&
        p.X >= b.MinX-Tolerance && p.X <= b.MaxX+Tolerance && p.Y >= b.MinY-Tolerance && p.Y <= b.MaxY+Tolerance;
}
