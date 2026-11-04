using System;
using System.Collections.Generic;
using System.Linq;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A road axis drawn as plan linework, for drawings with no Civil 3D alignment at all (2026-11-02, MAAKAF DD-500X):
/// the office's sheet drawings carry the design in XREFs, the axes as polylines on AX-* / CL / CENTERLINE layers. The
/// typical-sections picker snaps to these exactly as it snaps to an alignment: station = length along the polyline
/// from its first vertex, offset = signed distance from it (positive to the right of the drawn direction, as Civil's).
/// Pure geometry on a vertex chain in world coordinates; the caller tessellates arcs before handing the chain in.
/// </summary>
public sealed class PlanAxis
{
    private readonly P2[] _vertices;
    private readonly double[] _stations;

    /// <param name="startStation">The station of the first vertex: 0 for a drawn polyline, the alignment's own
    /// stationing for an exploded alignment (<see cref="ExplodedAlignmentLogic"/>).</param>
    public PlanAxis(string name, IReadOnlyList<P2> vertices, double startStation = 0)
    {
        StartStation = double.IsFinite(startStation) ? startStation : 0;
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("An axis needs a name.", nameof(name)) : name;
        if (vertices == null) throw new ArgumentNullException(nameof(vertices));
        var clean = new List<P2>();
        foreach (var v in vertices)
        {
            if (!double.IsFinite(v.X) || !double.IsFinite(v.Y)) continue;
            if (clean.Count > 0 && Distance(clean[^1], v) < 1e-9) continue;
            clean.Add(v);
        }
        if (clean.Count < 2) throw new ArgumentException("An axis needs two distinct vertices.", nameof(vertices));
        _vertices = clean.ToArray();
        _stations = new double[_vertices.Length];
        for (var i = 1; i < _vertices.Length; i++) _stations[i] = _stations[i - 1] + Distance(_vertices[i - 1], _vertices[i]);
    }

    public string Name { get; }

    /// <summary>The station of the first vertex.</summary>
    public double StartStation { get; }

    public double Length => _stations[^1];

    public IReadOnlyList<P2> Vertices => _vertices;

    /// <summary>
    /// The nearest point of the axis to <paramref name="point"/>: its station, the signed offset of the point (right
    /// positive) and the axis direction there in degrees. False when the nearest point is an end of the axis and the
    /// point lies beyond it (a click past the end of a road is not on that road), like an alignment's StationOffset.
    /// </summary>
    public bool TryStationOffset(P2 point, out double station, out double offset, out double tangentDeg)
    {
        station = offset = tangentDeg = double.NaN;
        var best = double.PositiveInfinity;
        for (var i = 0; i < _vertices.Length - 1; i++)
        {
            var a = _vertices[i];
            var b = _vertices[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSq = dx * dx + dy * dy;
            var t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSq;
            var beyond = (i == 0 && t < -1e-9) || (i == _vertices.Length - 2 && t > 1 + 1e-9);
            var clamped = Math.Clamp(t, 0.0, 1.0);
            var px = a.X + clamped * dx;
            var py = a.Y + clamped * dy;
            var distance = Math.Sqrt((point.X - px) * (point.X - px) + (point.Y - py) * (point.Y - py));
            if (distance >= best) continue;
            best = distance;
            if (beyond) { station = double.NaN; continue; }
            var length = Math.Sqrt(lengthSq);
            station = StartStation + _stations[i] + clamped * length;
            // Cross product sign: positive when the point is LEFT of the drawn direction; Civil's offset is right-positive.
            var cross = dx * (point.Y - a.Y) - dy * (point.X - a.X);
            offset = cross > 0 ? -distance : distance;
            tangentDeg = NormalizeDeg(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        }
        return double.IsFinite(station);
    }

    /// <summary>The plan point at a station and signed offset (right positive), the station clamped to the axis.</summary>
    public P2 PointAt(double station, double offset)
    {
        var (a, b, t) = SegmentAt(station - StartStation);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var x = a.X + t * dx;
        var y = a.Y + t * dy;
        // Right of the direction (dx, dy) is (dy, -dx).
        return new P2(x + offset * dy / length, y - offset * dx / length);
    }

    /// <summary>The axis direction at a station, degrees in [0, 360).</summary>
    public double TangentDegAt(double station)
    {
        var (a, b, _) = SegmentAt(station - StartStation);
        return NormalizeDeg(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI);
    }

    private (P2 A, P2 B, double T) SegmentAt(double station)
    {
        var s = Math.Clamp(double.IsFinite(station) ? station : 0.0, 0.0, Length);
        for (var i = 0; i < _vertices.Length - 1; i++)
        {
            if (s > _stations[i + 1] && i < _vertices.Length - 2) continue;
            var span = _stations[i + 1] - _stations[i];
            return (_vertices[i], _vertices[i + 1], span > 0 ? (s - _stations[i]) / span : 0.0);
        }
        return (_vertices[^2], _vertices[^1], 1.0);
    }

    private static double Distance(P2 a, P2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double NormalizeDeg(double deg)
    {
        var d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    /// <summary>
    /// A layer an axis polyline stands on, by the leaf name (an XREF prefix "GM|" removed): the office's AX-* axes
    /// (AX-40-st-01-1 in the CL XREF), a plain "CL", "CENTERLINE*". Not the CL_* family MahodCivilNet's EV drawings
    /// use for section ticks, slopes and elevations (CL_Sect, CL_Slope, CL_Elev_Top), which are not axes.
    /// </summary>
    public static bool IsAxisLayer(string? layer)
    {
        var leaf = LayerLeaf(layer);
        if (leaf.Length == 0) return false;
        if (leaf.StartsWith("CL_", StringComparison.OrdinalIgnoreCase)) return false;
        return leaf.Equals("CL", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("AX-", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("AX_", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("CENTERLINE", StringComparison.OrdinalIgnoreCase)
               || leaf.StartsWith("CENTRELINE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The layer's own name without the XREF chain ("A|B|HW-TRWY" → "HW-TRWY").</summary>
    public static string LayerLeaf(string? layer)
    {
        var text = (layer ?? string.Empty).Trim();
        var bar = text.LastIndexOf('|');
        return bar >= 0 ? text[(bar + 1)..] : text;
    }
}
