using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Earthworks in the bill of quantities: the rows the topographic analysis worked out (<see cref="EarthworksReader"/>) join
/// the bill under "new works", each on its NTI chapter 51 item; and the road outlines the bill prices a pavement for, with
/// that structure's depth, so cut and fill under a road are measured to the subgrade.
/// </summary>
internal sealed partial class InfrastructureBoqWindow
{
    private List<InfrastructureGroup> _earthDerived = new();
    private EarthworksReader.Result? _earth;

    internal EarthworksReader.Result? EarthworksResult => _earth;

    /// <summary>The earthworks rows of this read. An engineer's own item for a row stays; the rest take their NTI item.</summary>
    internal void SetEarthworks(EarthworksReader.Result? result)
    {
        _earth = result;
        _groups.RemoveAll(g => g.Source == Earthworks.DerivedSource);
        foreach (var gone in _earthDerived) _excluded.Remove(gone);
        _earthDerived = result?.Rows is { Count: > 0 } rows ? Earthworks.DerivedGroups(rows, result.Basis, result.Defaults).ToList() : new();
        if (_earthDerived.Count == 0) return;
        try { InfrastructureReviewStore.RestoreByLayer(_reviewPath, _scan.DrawingHash, _books.Select(b => b.Catalog).ToArray(), _earthDerived, _excluded); }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        var book = _books[0].Catalog;
        foreach (var (g, row) in _earthDerived.Zip(result!.Rows))
        {
            if (g.Approved) continue;
            if (book.Items.TryGetValue(row.Code, out var item) && item.Unit.SameUnit(Units.Parse(g.MeasurementUnit))) g.Assign(book, row.Code, automatic: true);
        }
        _groups.AddRange(_earthDerived);
    }

    /// <summary>
    /// New road surfaces with a pavement structure, as outlines (metres) with the structure's depth: what the earthworks
    /// lower the design by. The depth is the bill's own structure (report, typed or road-type defaults), averaged per surface.
    /// </summary>
    internal IReadOnlyList<(IReadOnlyList<IReadOnlyList<(double X, double Y)>> Rings, double DepthM)> PavementFootprints()
    {
        var result = new List<(IReadOnlyList<IReadOnlyList<(double X, double Y)>>, double)>();
        var layers = _plan?.Layers ?? Array.Empty<RoadPavement.LayerQuantity>();
        if (layers.Count == 0) return result;
        var areas = RoadPavement.Surfaces(_groups, _excluded).Where(s => s.Disposition == InfrastructureDisposition.New)
            .GroupBy(s => s.Surface).ToDictionary(g => g.Key, g => g.Sum(s => s.AreaSquareMetres));
        var depth = layers.Where(l => l.Surface != RoadSurfaceKind.Milling).GroupBy(l => l.Surface)
            .ToDictionary(g => g.Key, g => areas.TryGetValue(g.Key, out var a) && a > 0 ? g.Sum(l => l.AreaSquareMetres * l.Layer.ThicknessCm * l.Layer.Times) / 100d / a : 0);
        var f = _scan.MetresPerHostUnit;
        var bySource = _scan.Measurements.GroupBy(m => (m.Line.Source, m.Line.Layer, m.Line.GroupPart)).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var g in Active.Where(g => g.Disposition == InfrastructureDisposition.New && g.Surface != RoadSurfaceKind.None && g.AreaSquareMetres > 0 &&
                                            g.Source != RoadPavement.DerivedSource && depth.GetValueOrDefault(g.Surface) > 0))
        {
            if (!bySource.TryGetValue((g.Source, g.Layer, g.GroupPart), out var measured)) continue;
            foreach (var m in measured)
            {
                var rings = Rings(m, f);
                if (rings.Count > 0) result.Add((rings, depth[g.Surface]));
            }
        }
        return result;
    }

    /// <summary>The outline of a measured area as rings in metres: a recovered shape, a hatch's loops, a closed curve.</summary>
    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Rings(InfrastructureDrawingService.Measured m, double f)
    {
        var rings = new List<IReadOnlyList<(double X, double Y)>>();
        try
        {
            if (m.RecoveredShape is { } shape)
            {
                for (var i = 0; i < shape.NumGeometries; i++)
                    if (shape.GetGeometryN(i) is NetTopologySuite.Geometries.Polygon p)
                        foreach (var r in new[] { p.ExteriorRing }.Concat(p.InteriorRings))
                            rings.Add(r.Coordinates.Select(c => (c.X * f, c.Y * f)).ToArray());
                return rings;
            }
            switch (m.Geometry)
            {
                case Hatch h:
                    for (var i = 0; i < h.NumberOfLoops; i++)
                    {
                        var loop = h.GetLoopAt(i);
                        var pts = new List<(double, double)>();
                        if (loop.IsPolyline)
                        {
                            var v = loop.Polyline;
                            for (var k = 0; k < v.Count; k++)
                            {
                                var a = v[k].Vertex; var b = v[(k + 1) % v.Count].Vertex; var bulge = v[k].Bulge;
                                pts.Add((a.X * f, a.Y * f));
                                if (Math.Abs(bulge) > 1e-9)
                                {
                                    var arc = new CircularArc2d(a, b, bulge, false);
                                    foreach (var s in arc.GetSamplePoints(8).Skip(1).SkipLast(1)) pts.Add((s.X * f, s.Y * f));
                                }
                            }
                        }
                        else
                            foreach (Curve2d c in loop.Curves)
                                foreach (var s in c.GetSamplePoints(c is LineSegment2d ? 2 : 12).SkipLast(1)) pts.Add((s.X * f, s.Y * f));
                        if (pts.Count >= 3) rings.Add(pts);
                    }
                    break;
                case Curve c when c.Closed:
                    var n = Math.Max(3, (int)Math.Round(c.EndParam) * 4);
                    var ring = new List<(double, double)>(n);
                    for (var i = 0; i < n; i++) { var p = c.GetPointAtParameter(c.StartParam + (c.EndParam - c.StartParam) * i / n); ring.Add((p.X * f, p.Y * f)); }
                    rings.Add(ring);
                    break;
            }
        }
        catch (System.Exception) { rings.Clear(); }   // an outline that cannot be read is left out of the box-out, never guessed
        return rings;
    }
}
