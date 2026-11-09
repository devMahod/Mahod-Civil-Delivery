using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

internal sealed partial class InfrastructureDrawingService
{
    /// <summary>
    /// A layer's open lines as straight pieces (metres), read for face pairs (<see cref="FacePairs"/>): a wall or footing
    /// drawn as its two faces. Null when the layer has no open lines or too many pieces to pair in the window's time.
    /// </summary>
    internal FacePairs.Result? Faces(InfrastructureGroup g, double maxWidthM)
    {
        var pieces = new List<FacePairs.Segment>();
        foreach (var m in Measurements)
        {
            if (m.Line.Source != g.Source || m.Line.Layer != g.Layer || m.Line.GroupPart != g.GroupPart || m.Geometry is not Curve c || c.Closed) continue;
            try { Pieces(c, pieces); }
            catch (System.Exception) { return null; }
            if (pieces.Count > 4000) return null;
        }
        return pieces.Count < 2 ? null : FacePairs.Measure(pieces, 0.1, maxWidthM);
    }

    private void Pieces(Curve c, List<FacePairs.Segment> into)
    {
        void Add(Point3d a, Point3d b) => into.Add(new(a.X * _metres, a.Y * _metres, b.X * _metres, b.Y * _metres));
        if (c is Line line) { Add(line.StartPoint, line.EndPoint); return; }
        if (c is Polyline pl)
        {
            var n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
            for (var i = 0; i < n; i++)
            {
                if (pl.GetSegmentType(i) != SegmentType.Arc) { Add(pl.GetPoint3dAt(i), pl.GetPoint3dAt((i + 1) % pl.NumberOfVertices)); continue; }
                // An arc piece as chords of at most ~1 m, so a curved wall still pairs with its curved other face.
                var arc = pl.GetArcSegmentAt(i);
                var steps = Math.Clamp((int)Math.Ceiling(arc.GetLength(arc.GetParameterOf(arc.StartPoint), arc.GetParameterOf(arc.EndPoint), 1e-6) * _metres), 2, 64);
                var prev = pl.GetPoint3dAt(i);
                for (var k = 1; k <= steps; k++) { var p = pl.GetPointAtParameter(i + (double)k / steps); Add(prev, p); prev = p; }
            }
            return;
        }
        // Arcs, 2D/3D polylines, splines: chords along the length.
        var length = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
        var count = Math.Clamp((int)Math.Ceiling(length * _metres), 1, 200);
        var last = c.StartPoint;
        for (var k = 1; k <= count; k++) { var p = c.GetPointAtDist(length * k / count); Add(last, p); last = p; }
    }
}
