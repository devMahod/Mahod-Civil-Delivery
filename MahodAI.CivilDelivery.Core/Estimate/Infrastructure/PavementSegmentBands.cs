using System.Globalization;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;
using NetTopologySuite.LinearReferencing;
using NetTopologySuite.Operation.Buffer;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>
/// Where each segment of a pavement report lies on the plan. Reports define segments by cross-section numbers ("from
/// section 1000 to 1018"); the drawing carries those numbers as labels along its centre lines (HW-ALGN polylines with
/// HW-ALGN-SEC-NAME texts every 20 m on Mahod's plans, or Civil 3D alignments). Each label is projected onto its centre
/// line, numbers become stations (missing numbers interpolated between their neighbours), and a segment becomes the band
/// of plan within <c>halfWidth</c> of its centre line between its two stations, cut square at both ends. Where bands of
/// two segments overlap (junctions, the shared section between segments) the area goes to the earlier segment of the
/// table, so no square metre is priced twice. All coordinates are host drawing units.
/// </summary>
public static class PavementSegmentBands
{
    public sealed record Centreline(string Id, string Layer, IReadOnlyList<(double X, double Y)> Points);
    public sealed record SectionLabel(string Text, double X, double Y, string Layer);
    public sealed record Band(int Report, PavementReportSegment Segment, Geometry Shape, string CentrelineId, double FromStation, double ToStation);
    public sealed record Result(IReadOnlyList<Band> Bands, IReadOnlyList<string> Missing, IReadOnlyList<string> Findings, int LabelsOnLines);

    private static readonly GeometryFactory Factory = new();
    private static readonly Regex Number = new(@"^\D{0,8}?(\d{1,6})(?:\.0+)?\D{0,4}$", RegexOptions.CultureInvariant);

    /// <summary>Section number of a label ("1000", "חתך 1000", "1000.00"), or null.</summary>
    public static int? SectionNumber(string text)
    {
        var m = Number.Match((text ?? "").Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public static Result Build(IReadOnlyList<Centreline> centrelines, IReadOnlyList<SectionLabel> labels,
        IEnumerable<(int Report, PavementReportSegment Segment)> segments, double halfWidth, double maxLabelOffset)
    {
        var findings = new List<string>();
        var missing = new List<string>();
        var lines = centrelines.Where(c => c.Points.Count >= 2)
            .Select(c => (c.Id, Line: Factory.CreateLineString(c.Points.Select(p => new Coordinate(p.X, p.Y)).ToArray())))
            .Where(c => c.Line.Length > 0).ToArray();
        // A label counts for every centre line within maxLabelOffset: at a junction the first section of a side road sits on
        // the main road's axis too (live 2026-11-09: section 700 was 0.2 m from road 2000's axis and 3.3 m from its own).
        // The series below decide where it belongs. A number labelled twice near one line keeps the nearer label.
        var perLine = new Dictionary<string, Dictionary<int, (double Station, double Distance)>>();
        var onLines = 0;
        foreach (var label in labels)
        {
            if (SectionNumber(label.Text) is not { } number) continue;
            var point = Factory.CreatePoint(new Coordinate(label.X, label.Y));
            var near = lines.Select(l => (l.Id, l.Line, Distance: l.Line.Distance(point))).Where(l => l.Distance <= maxLabelOffset).ToArray();
            if (near.Length > 0) onLines++;
            foreach (var line in near)
            {
                if (!perLine.TryGetValue(line.Id, out var map)) perLine[line.Id] = map = new();
                if (map.TryGetValue(number, out var previous) && previous.Distance <= line.Distance) continue;
                map[number] = (new LengthIndexedLine(line.Line).Project(point.Coordinate), line.Distance);
            }
        }
        // A line also catches a few labels of the roads that cross it (live 2026-11-09: the 1000 axis carried 406, 600, 2117
        // and 3062 at its junctions). A series is a chain of labels that keeps one direction along the line in steps of at
        // most MaxSectionGap; a lone label is never a series, so a stray never becomes a station.
        var runs = new List<Run>();
        foreach (var (id, map) in perLine)
        {
            var left = map.OrderBy(kv => kv.Value.Station).Select(kv => (Number: kv.Key, kv.Value.Station)).ToList();
            double Offset(int number) => map[number].Distance;
            // Series after series: the longest chain whose numbers step by at most MaxSectionGap in one direction along the line.
            while (true)
            {
                var rising = Chain(left, +1); var falling = Chain(left, -1);
                var chain = rising.Count >= falling.Count ? rising : falling;
                if (chain.Count < 2) break;
                runs.Add(new Run(id, chain.OrderBy(p => p.Number).ToArray(), chain.Average(p => Offset(p.Number))));
                foreach (var p in chain) left.Remove(p);
            }
            var dropped = left.Count;
            if (dropped > 0) findings.Add($"קו הציר {id}: {dropped} מספרי חתך של דרכים אחרות (צמתים) לא נלקחו");
        }

        var bands = new List<Band>();
        Geometry? claimed = null;
        double overlap = 0;
        foreach (var (report, segment) in segments)
        {
            var label = "מקטע " + (segment.Segment ?? "?");
            if (SectionNumber(segment.FromSection ?? "") is not { } from || SectionNumber(segment.ToSection ?? "") is not { } to)
            {
                missing.Add($"{label}: אין בדו״ח מספרי חתך התחלה וסוף");
                continue;
            }
            // The series that holds both numbers: labelled ends first, then the one written closest to its axis (a parallel
            // carriageway's axis carries the same numbers a few metres off), then the longest (interpolated only inside it).
            var host = runs.Select(r => (Run: r, From: r.StationOf(from), To: r.StationOf(to)))
                .Where(h => h.From != null && h.To != null)
                .OrderByDescending(h => h.Run.Has(from) && h.Run.Has(to)).ThenBy(h => Math.Round(h.Run.MeanOffset, 1)).ThenByDescending(h => h.Run.Points.Length).FirstOrDefault();
            if (host.Run == null)
            {
                var known = runs.SelectMany(r => r.Points.Select(p => p.Number)).ToHashSet();
                missing.Add($"{label} ({from}–{to}): " + (known.Contains(from) || known.Contains(to)
                    ? "החתכים אינם על אותו קו ציר"
                    : "מספרי החתכים לא נמצאו על קווי הציר בשרטוט"));
                continue;
            }
            var line = lines.First(l => l.Id == host.Run.Line).Line;
            double a = Math.Min(host.From!.Value, host.To!.Value), b = Math.Max(host.From!.Value, host.To!.Value);
            if (b - a <= 1e-6) { missing.Add($"{label} ({from}–{to}): אורך אפס על קו הציר"); continue; }
            var piece = new LengthIndexedLine(line).ExtractLine(a, b);
            var shape = piece.Buffer(halfWidth, new BufferParameters { EndCapStyle = EndCapStyle.Flat, QuadrantSegments = 4 });
            if (claimed != null)
            {
                var before = shape.Area;
                shape = shape.Difference(claimed);
                overlap += before - shape.Area;
            }
            claimed = claimed == null ? shape : claimed.Union(shape);
            if (shape.IsEmpty) { missing.Add($"{label} ({from}–{to}): כל הרצועה כבר שייכת למקטעים קודמים"); continue; }
            bands.Add(new Band(report, segment, shape, host.Run.Line, a, b));
        }
        if (overlap > 1e-6) findings.Add($"רצועות מקטעים חופפות (צמתים / חתך משותף): שטח החפיפה שויך למקטע הקודם בטבלת הדו״ח");
        return new Result(bands, missing, findings, onLines);
    }

    /// <summary>Section numbers further apart than this are not one road's series (Mahod labels every section, 20 m).</summary>
    private const int MaxSectionGap = 10;

    /// <summary>One road's section numbers along one centre line, ascending by number.</summary>
    private sealed record Run(string Line, (int Number, double Station)[] Points, double MeanOffset)
    {
        public bool Has(int number) => Points.Any(p => p.Number == number);
        /// <summary>Station of a number: its own label, or linear between the labelled numbers around it.</summary>
        public double? StationOf(int number)
        {
            for (var i = 0; i < Points.Length; i++)
            {
                if (Points[i].Number == number) return Points[i].Station;
                if (i + 1 < Points.Length && Points[i].Number < number && number < Points[i + 1].Number)
                    return Points[i].Station + (Points[i + 1].Station - Points[i].Station) * (number - Points[i].Number) / (double)(Points[i + 1].Number - Points[i].Number);
            }
            return null;
        }
    }

    /// <summary>The longest chain (in station order) whose numbers rise (<paramref name="sign"/> +1) or fall (−1) by 1…
    /// <see cref="MaxSectionGap"/> from one label to the next: one road's labels, never a crossing road's stray number.</summary>
    private static List<(int Number, double Station)> Chain(IReadOnlyList<(int Number, double Station)> byStation, int sign)
    {
        var n = byStation.Count;
        var length = new int[n]; var previous = new int[n];
        var best = -1;
        for (var i = 0; i < n; i++)
        {
            length[i] = 1; previous[i] = -1;
            for (var j = 0; j < i; j++)
            {
                var step = sign * (byStation[i].Number - byStation[j].Number);
                if (step > 0 && step <= MaxSectionGap && length[j] + 1 > length[i]) { length[i] = length[j] + 1; previous[i] = j; }
            }
            if (best < 0 || length[i] > length[best]) best = i;
        }
        var chain = new List<(int Number, double Station)>();
        for (var k = best; k >= 0; k = previous[k]) chain.Add(byStation[k]);
        chain.Reverse();
        return chain;
    }

    /// <summary>The band outline as vertex lists (shell first, then holes) per polygon, for clipping and drawing.</summary>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Rings(Geometry shape)
    {
        var rings = new List<IReadOnlyList<(double X, double Y)>>();
        for (var i = 0; i < shape.NumGeometries; i++)
            if (shape.GetGeometryN(i) is Polygon p && !p.IsEmpty)
            {
                rings.Add(p.Shell.Coordinates.Select(c => (c.X, c.Y)).ToArray());
                foreach (var hole in p.Holes) rings.Add(hole.Coordinates.Select(c => (c.X, c.Y)).ToArray());
            }
        return rings;
    }
}
