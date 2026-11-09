using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Plan shapes of the measured surfaces, for the pavement report: each hatch or closed outline as a polygon in world XY
/// (host units), so the area of a surface inside a report segment is an exact intersection, not a share of its extents.
/// In an area view the shapes are the parts inside the marked area, so the two cuts compose.
/// </summary>
internal sealed partial class InfrastructureDrawingService
{
    internal sealed record SurfaceShape(InfrastructureLine Line, Geometry Shape);

    private static readonly GeometryFactory PlanFactory = new();

    /// <summary>Shapes of the measurements <paramref name="want"/> selects; what cannot be shaped is named in <paramref name="failures"/>.</summary>
    internal IReadOnlyList<SurfaceShape> PlanShapes(Func<InfrastructureLine, bool> want, List<string> failures)
    {
        var shapes = new List<SurfaceShape>();
        foreach (var m in Measurements.Where(m => m.Line.AreaSquareMetres > 0 && want(m.Line)))
        {
            try
            {
                var shape = PlanShape(m);
                if (shape != null && !shape.IsEmpty && shape.Area > 0) shapes.Add(new(m.Line, shape));
                else failures.Add($"{m.Line.Layer.Split('|').Last()} / {m.Line.Handle}: אין צורה בתוכנית");
            }
            catch (System.Exception ex) when (ex is not OutOfMemoryException)
            { failures.Add($"{m.Line.Layer.Split('|').Last()} / {m.Line.Handle}: {ex.Message}"); }
        }
        return shapes;
    }

    private Geometry? PlanShape(Measured m)
    {
        // A recovered or area-cut shape is in its hatch's plane (the cut was made there); a curve's is already world XY.
        if (m.RecoveredShape is { } recovered) return ToWorld(recovered, m.RecoveredNormal, m.RecoveredElevation);
        if (m.Geometry is Hatch hatch)
        {
            var loops = new List<IReadOnlyList<(double X, double Y)>>();
            for (var i = 0; i < hatch.NumberOfLoops; i++) loops.Add(LoopPoints(hatch.GetLoopAt(i), 0.001 / _metres));
            if (!HatchBoundaryArea.TryMeasure(loops, hatch.HatchStyle.ToString(), out var measured, out var refusal))
                throw new InvalidOperationException(refusal);
            return ToWorld(measured!.Shape, hatch.Normal, hatch.Elevation);
        }
        if (m.Geometry is Curve { Closed: true } curve)
        {
            var path = Tessellate(curve, 0.001 / _metres);
            if (path.Count < 3) return null;
            var ring = path.Select(p => new Coordinate(p.X, p.Y)).ToList();
            if (!ring[0].Equals2D(ring[^1])) ring.Add(ring[0].Copy());
            return GeometryFixer.Fix(PlanFactory.CreatePolygon(ring.ToArray()));
        }
        return null;
    }

    /// <summary>A shape drawn in a plane with this normal and elevation, as world XY. A plan hatch (normal +Z) is unchanged;
    /// a mirrored one (normal −Z, from a mirrored XREF) has its plane X flipped.</summary>
    private static Geometry ToWorld(Geometry shape, Vector3d normal, double elevation)
    {
        if (normal.IsCodirectionalTo(Vector3d.ZAxis)) return shape;
        if (Math.Abs(normal.Z) < 1 - 1e-9) throw new InvalidOperationException("Hatch שאינו במישור התוכנית");
        var toWorld = Matrix3d.PlaneToWorld(normal);
        var copy = shape.Copy();
        copy.Apply(new PlaneFilter(toWorld, elevation));
        copy.GeometryChanged();
        return GeometryFixer.Fix(copy);
    }

    private sealed class PlaneFilter : ICoordinateSequenceFilter
    {
        private readonly Matrix3d _toWorld; private readonly double _elevation;
        public PlaneFilter(Matrix3d toWorld, double elevation) { _toWorld = toWorld; _elevation = elevation; }
        public void Filter(CoordinateSequence seq, int i)
        {
            var p = new Point3d(seq.GetX(i), seq.GetY(i), _elevation).TransformBy(_toWorld);
            seq.SetX(i, p.X); seq.SetY(i, p.Y);
        }
        public bool Done => false;
        public bool GeometryChanged => true;
    }
}
