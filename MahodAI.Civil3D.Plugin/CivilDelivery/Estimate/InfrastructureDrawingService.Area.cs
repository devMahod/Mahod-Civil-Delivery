using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Region = Autodesk.AutoCAD.DatabaseServices.Region;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// A bill of quantities for a marked area only: the whole drawing is scanned once, then every measurement is cut by the
/// polygon the engineer drew — blocks count when they stand inside, lines keep the length inside (elevations
/// interpolated at the edge), hatches and closed outlines keep the area inside, solids keep the volume inside the
/// polygon's prism. Nothing in the source drawing changes; the area itself is never stored in it.
/// </summary>
internal sealed partial class InfrastructureDrawingService
{
    internal const string AreaLayer = "BOQ_AREA";
    /// <summary>The marked area, in host coordinates; null for the whole drawing.</summary>
    internal SpatialQuantityClip? Area { get; }
    /// <summary>Measurements cut at the edge of the area (the rest were wholly inside).</summary>
    internal int CutAtEdge { get; private set; }

    internal InfrastructureDrawingService ClipTo(SpatialQuantityClip area, ObjectId boundary = default)
    {
        if (Area != null) throw new InvalidOperationException("Clip the whole-drawing scan, not an area view");
        return new InfrastructureDrawingService(this, area, boundary);
    }

    private InfrastructureDrawingService(InfrastructureDrawingService whole, SpatialQuantityClip area, ObjectId boundary)
    {
        Drawing = whole.Drawing; DrawingHash = whole.DrawingHash; _metres = whole._metres; Area = area;
        var fallback = new List<string>();
        SpatialQuantityClip? flipped = null;
        foreach (var m in whole.Measurements.Where(m => boundary.IsNull || m.SourceId != boundary))   // the picked outline is the area, not a quantity
        {
            try
            {
                var clipped = Clip(m, area, ref flipped, fallback);
                if (clipped != null) Measurements.Add(clipped);
            }
            catch (System.Exception ex) when (ex is not OutOfMemoryException)
            {
                // A measurement that cannot be cut is never silently dropped or silently kept whole.
                var keep = Centre(m.Geometry) is { } c && area.Contains(c.X, c.Y);
                if (keep) Measurements.Add(m);
                fallback.Add($"{m.Line.Layer.Split('|').Last()} / {m.Line.Handle}: {ex.Message} — {(keep ? "נכלל כולו (מרכזו בתוך האזור)" : "לא נכלל (מרכזו מחוץ לאזור)")}");
            }
        }
        bool Inside(AreaIssue i) => i.At is not { } at || area.Contains(at.X, at.Y);
        var kept = Measurements.GroupBy(m => (m.Line.Source, m.Line.Handle)).ToDictionary(g => g.Key, g => g.Sum(m => m.Line.AreaSquareMetres));
        RecoveredAreas.AddRange(whole.RecoveredAreas.Where(r => kept.ContainsKey((r.Source, r.Handle)))
            .Select(r => r with { AreaSquareMetres = kept[(r.Source, r.Handle)] }));
        UnmeasuredAreas.AddRange(whole.UnmeasuredAreas.Where(Inside));
        Findings.Add($"כתב כמויות לאזור שסומן בלבד: {area.Vertices.Count} קודקודים, {area.Boundary.Area * _metres * _metres:N1} מ״ר בתוכנית. " +
                     $"{Measurements.Count:N0} מדידות בתוך האזור, {CutAtEdge:N0} מהן נחתכו בגבולו" +
                     (whole._near != null ? $" ({whole.SkippedOutsideArea:N0} עצמים שכל תחומם מחוץ לאזור לא נקראו). " : $" (מתוך {whole.Measurements.Count:N0} בשרטוט). ") +
                     "קווים נמדדים באורכם בתוך האזור, שטחים ונפחים בחלקם בתוכו, בלוקים לפי נקודת ההכנסה.");
        if (fallback.Count > 0)
            Findings.Add($"{fallback.Count} עצמים לא נחתכו במדויק בגבול האזור ונכללו לפי מרכזם — לבדיקה: " + string.Join(" · ", fallback.Take(20)) +
                         (fallback.Count > 20 ? $" · ועוד {fallback.Count - 20}" : ""));
        Findings.AddRange(whole.Findings);
    }

    private Measured? Clip(Measured m, SpatialQuantityClip area, ref SpatialQuantityClip? flipped, List<string> fallback)
    {
        if (m.Geometry is DBPoint point)
            return area.Contains(point.Position.X, point.Position.Y) ? m : null;

        SpatialQuantityClip.Relation relation;
        try
        {
            var e = m.Geometry.GeometricExtents;
            relation = area.Classify(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { relation = SpatialQuantityClip.Relation.Crossing; }   // no extents: cut it exactly
        if (relation == SpatialQuantityClip.Relation.Outside) return null;
        if (relation == SpatialQuantityClip.Relation.Inside) return m;

        var cut = m.Geometry switch
        {
            Hatch hatch => ClipHatch(m, hatch, area, ref flipped),
            Solid3d solid => ClipSolid(m, solid, area),
            Curve curve => ClipCurve(m, curve, area),
            _ => throw new InvalidOperationException("סוג שאינו נחתך: " + m.Geometry.GetType().Name),
        };
        if (cut != null) CutAtEdge++;
        return cut;
    }

    private Measured? ClipHatch(Measured m, Hatch hatch, SpatialQuantityClip area, ref SpatialQuantityClip? flipped)
    {
        // Hatch loops are in the hatch's own plane. Plan hatches (normal ±Z) are cut in that plane; a hatch drawn in a
        // vertical plane has no plan area to cut.
        var normal = hatch.Normal;
        if (Math.Abs(normal.Z) < 1 - 1e-9) throw new InvalidOperationException("Hatch שאינו במישור התוכנית");
        var clip = area;
        if (normal.Z < 0)
        {
            var toPlane = Matrix3d.WorldToPlane(normal);
            flipped ??= new SpatialQuantityClip(area.Vertices.Select(v => { var p = new Point3d(v.X, v.Y, 0).TransformBy(toPlane); return (p.X, p.Y); }));
            clip = flipped;
        }
        var shape = m.RecoveredShape;
        if (shape == null)
        {
            var tolerance = 0.001 / _metres;
            var loops = new List<IReadOnlyList<(double X, double Y)>>();
            for (var i = 0; i < hatch.NumberOfLoops; i++) loops.Add(LoopPoints(hatch.GetLoopAt(i), tolerance));
            if (!HatchBoundaryArea.TryMeasure(loops, hatch.HatchStyle.ToString(), out var measured, out var refusal))
                throw new InvalidOperationException(refusal);
            shape = measured!.Shape;
        }
        var inside = clip.ClipShape(shape);
        var expected = inside.Area * _metres * _metres;
        if (!(expected >= 0.0001)) return null;
        var (area2, skip) = ShapeArea(inside, normal, hatch.Elevation, expected);
        // The audit DWG builds this region itself: no native clone of the source (which would add the whole hatch and,
        // without SourceDatabase cleared, left the region out — 6 hatches missing from the audit check, live 2026-11-09).
        return m with
        {
            Line = m.Line with { AreaSquareMetres = area2 },
            RecoveredShape = inside, RecoveredNormal = normal, RecoveredElevation = hatch.Elevation,
            ExportSkipReason = m.ExportSkipReason ?? skip,
            SourceDatabase = null, SourceTransform = Matrix3d.Identity,
        };
    }

    /// <summary>The audit DWG must carry the same area as the bill: the region's area when AutoCAD builds it, else the
    /// computed area with the shape listed as not copied.</summary>
    private (double Area, string? Skip) ShapeArea(NetTopologySuite.Geometries.Geometry shape, Vector3d normal, double elevation, double expected)
    {
        var region = BuildRegion(shape, normal, elevation);
        var area = region == null ? double.NaN : region.Area * _metres * _metres;
        region?.Dispose();
        return double.IsFinite(area) && Math.Abs(area - expected) <= Math.Max(1e-6, expected * 1e-6)
            ? (area, null)
            : (expected, "שטח שנחתך בגבול האזור ואינו ניתן לשחזור כאזור בשרטוט");
    }

    private Measured? ClipSolid(Measured m, Solid3d solid, SpatialQuantityClip area)
    {
        var copy = (Solid3d)solid.Clone();
        try
        {
            CutByPrism(copy, area);
            var volume = copy.IsNull ? 0 : copy.MassProperties.Volume * Math.Pow(_metres, 3);
            if (!double.IsFinite(volume) || volume <= 1e-9) return null;
            // The cut copy only measures. A solid built in memory belongs to the host drawing (eWrongDatabase on append,
            // live 2026-11-09), so the audit DWG clones the source natively and repeats the cut there.
            return m with { Line = m.Line with { VolumeCubicMetres = volume }, CutByArea = true };
        }
        finally { copy.Dispose(); }
    }

    /// <summary>Keeps the part of <paramref name="solid"/> inside the area's vertical prism (host coordinates).</summary>
    private static void CutByPrism(Solid3d solid, SpatialQuantityClip area)
    {
        var e = solid.GeometricExtents;
        var bottom = e.MinPoint.Z - 1; var height = e.MaxPoint.Z - e.MinPoint.Z + 2;
        using var outline = new Polyline(area.Vertices.Count) { Elevation = bottom };
        for (var i = 0; i < area.Vertices.Count; i++) outline.AddVertexAt(i, new Point2d(area.Vertices[i].X, area.Vertices[i].Y), 0, 0, 0);
        outline.Closed = true;
        var regions = Region.CreateFromCurves(new DBObjectCollection { outline });
        try
        {
            if (regions.Count != 1) throw new InvalidOperationException("לא ניתן לבנות את מנסרת האזור");
            using var prism = new Solid3d();
            prism.Extrude((Region)regions[0], height, 0);
            solid.BooleanOperation(BooleanOperationType.BoolIntersect, prism);
        }
        finally { foreach (DBObject r in regions) r.Dispose(); }
    }

    private Measured? ClipCurve(Measured m, Curve curve, SpatialQuantityClip area)
    {
        // A counted outline (a manhole drawn as a closed curve = 1 unit) is not a fraction: it counts where its centre is.
        if (m.Line.CountUnits > 0)
            return Centre(curve) is { } c && area.Contains(c.X, c.Y) ? m : null;
        var path = Tessellate(curve, 0.0001 / _metres);
        var pieces = area.ClipPath(path);
        var length = pieces.Sum(SpatialQuantityClip.Length3d) * _metres;
        NetTopologySuite.Geometries.Geometry? shape = null;
        double clippedArea = 0;
        string? skip = null;
        if (m.Line.AreaSquareMetres > 0 && curve.Closed)
        {
            shape = area.ClipRing(path.Select(p => (p.X, p.Y)).ToArray());
            var expected = shape.Area * _metres * _metres;
            if (expected >= 0.0001) (clippedArea, skip) = ShapeArea(shape, Vector3d.ZAxis, path[0].Z, expected);
            else shape = null;
        }
        if (length <= 1e-9 && clippedArea <= 0) return null;
        var runs = pieces.Select(p => (IReadOnlyList<Point3d>)p.Select(q => new Point3d(q.X, q.Y, q.Z)).ToArray()).ToArray();
        return m with
        {
            Line = m.Line with { LengthMetres = length, AreaSquareMetres = clippedArea },
            Pieces = runs, RecoveredShape = shape, RecoveredNormal = Vector3d.ZAxis, RecoveredElevation = shape == null ? 0 : path[0].Z,
            ExportSkipReason = m.ExportSkipReason ?? skip,
        };
    }

    /// <summary>The curve as a 3D point run: straight parts exact, arcs and ellipses to <paramref name="tolerance"/> chord error.</summary>
    private static IReadOnlyList<(double X, double Y, double Z)> Tessellate(Curve curve, double tolerance)
    {
        var points = new List<(double X, double Y, double Z)>();
        void Add(Point3d p) => points.Add((p.X, p.Y, p.Z));
        switch (curve)
        {
            case Line line:
                Add(line.StartPoint); Add(line.EndPoint);
                break;
            case Polyline pl:
                var segments = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                for (var i = 0; i < segments; i++)
                {
                    Add(pl.GetPoint3dAt(i));
                    var bulge = pl.GetBulgeAt(i);
                    if (Math.Abs(bulge) < 1e-12 || pl.GetSegmentType(i) != SegmentType.Arc) continue;
                    var n = ArcSegments(pl.GetArcSegmentAt(i).Radius, 4 * Math.Atan(Math.Abs(bulge)), tolerance);
                    for (var k = 1; k < n; k++) Add(pl.GetPointAtParameter(i + (double)k / n));
                }
                Add(pl.Closed ? pl.GetPoint3dAt(0) : pl.GetPoint3dAt(pl.NumberOfVertices - 1));
                break;
            case Polyline3d p3:
                // A simple 3D polyline's parameter is its vertex index; the copy is not in a database, so no vertex ids.
                for (var i = 0; i <= (int)Math.Round(p3.EndParam); i++) Add(p3.GetPointAtParameter(Math.Min(i, p3.EndParam)));
                break;
            default:
                // Arc, circle, ellipse: even steps of their parameter (the angle) — chord error bounded by the radius.
                var radius = curve switch { Arc a => a.Radius, Circle c => c.Radius, Ellipse el => el.MajorRadius, _ => 0 };
                if (!(radius > 0)) throw new InvalidOperationException("עקומה שאינה נחתכת: " + curve.GetType().Name);
                var start = curve.StartParam; var end = curve.EndParam;
                var steps = ArcSegments(radius, Math.Abs(end - start), tolerance);
                for (var k = 0; k <= steps; k++) Add(curve.GetPointAtParameter(start + (end - start) * k / steps));
                break;
        }
        return points;
    }

    private static (double X, double Y)? Centre(Entity entity) => CentreOf(entity);
}
