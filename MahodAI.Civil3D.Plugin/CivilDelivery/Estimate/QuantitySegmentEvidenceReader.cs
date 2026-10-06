using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Bounded plan geometry read in the scan transaction beside the raw CAD properties: the chain of a LINE / LWPOLYLINE /
    /// POLYLINE2D / POLYLINE3D (at most <see cref="QuantityGeometryEvidence.MaxVertices"/> stored vertices, otherwise an
    /// over-limit status only; bulged segments tessellated), the tessellated chain of an ARC / CIRCLE, the insertion point
    /// of a block reference and the boundary centroid of a HATCH (mean of its line-edge / polyline-loop vertices). The
    /// tessellation itself is the unit-tested Core helper (<see cref="QuantityGeometryEvidence"/>), bounded by
    /// <see cref="QuantityGeometryEvidence.MaxTessellatedPoints"/>. Polylines count with a +Z normal or face-down (-Z: WCS
    /// vertices, mirrored bulges); arcs and circles only with +Z, like the reference; POLYLINE3D uses the XY of its vertices. Raw, untransformed entity coordinates in drawing
    /// units, like the other cad_* evidence. Read-only; a failure becomes a status value and never an exception, a finding,
    /// a quantity or a rule-key change.
    /// </summary>
    internal static class QuantitySegmentEvidenceReader
    {
        // b24 (Codex 12:04 A): hostMetresPerUnit is the host's resolved physical factor (a unit decision included), so arc
        // density is bounded in physical metres; an XREF entity (null) keeps its own database unit.
        internal static void Read(Entity entity, Transaction tr, IDictionary<string, string> values, double? hostMetresPerUnit = null)
        {
            try
            {
                switch (entity)
                {
                    case Line line:
                        PutChain(values, entity, hostMetresPerUnit, new List<(double X, double Y, double Bulge)>
                        {
                            (line.StartPoint.X, line.StartPoint.Y, 0.0), (line.EndPoint.X, line.EndPoint.Y, 0.0),
                        }, closed: false);
                        break;
                    case Polyline polyline:
                    {
                        // Face-down (normal -Z): GetPoint3dAt is WCS already; only the bulge sign mirrors.
                        var faceDown = IsFaceDown(polyline.Normal);
                        if (!IsPlan(polyline.Normal) && !faceDown)
                        {
                            values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.StatusNonPlanNormal;
                            break;
                        }
                        var count = polyline.NumberOfVertices;
                        if (count > QuantityGeometryEvidence.MaxVertices)
                        {
                            values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.OverLimit(count);
                            break;
                        }
                        var closed = polyline.Closed;
                        var segments = closed ? count : count - 1;
                        var vertices = new List<(double X, double Y, double Bulge)>(count);
                        for (var i = 0; i < count; i++)
                        {
                            var p = polyline.GetPoint3dAt(i);
                            // Only a native arc segment reads its bulge: the unused bulge of a coincident closing span
                            // (or of an open polyline's last vertex) is never asked for.
                            var bulge = i < segments && polyline.GetSegmentType(i) == SegmentType.Arc ? polyline.GetBulgeAt(i) : 0.0;
                            vertices.Add((p.X, p.Y, faceDown ? -bulge : bulge));
                        }
                        PutChain(values, entity, hostMetresPerUnit, vertices, closed);
                        break;
                    }
                    case Polyline2d polyline2d:
                    {
                        // Face-down: Vertex2d.Position is in the entity's OCS (X mirrored); VertexPosition is WCS.
                        var faceDown = IsFaceDown(polyline2d.Normal);
                        if (!IsPlan(polyline2d.Normal) && !faceDown)
                        {
                            values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.StatusNonPlanNormal;
                            break;
                        }
                        var vertices = new List<(double X, double Y, double Bulge)>();
                        foreach (ObjectId id in polyline2d)
                        {
                            if (vertices.Count >= QuantityGeometryEvidence.MaxVertices)
                            {
                                values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.OverLimit(vertices.Count + 1);
                                return;
                            }
                            if (tr.GetObject(id, OpenMode.ForRead) is not Vertex2d vertex || vertex.VertexType != Vertex2dType.SimpleVertex)
                            {
                                values[QuantityGeometryEvidence.RawSegmentsStatus] = "non-simple-vertex";
                                return;
                            }
                            var position = faceDown ? polyline2d.VertexPosition(vertex) : vertex.Position;
                            vertices.Add((position.X, position.Y, faceDown ? -vertex.Bulge : vertex.Bulge));
                        }
                        PutChain(values, entity, hostMetresPerUnit, vertices, polyline2d.Closed);
                        break;
                    }
                    case Polyline3d polyline3d:
                    {
                        // Plan length: the XY of the vertices (a broken Z must not lengthen the object).
                        var vertices = new List<(double X, double Y, double Bulge)>();
                        foreach (ObjectId id in polyline3d)
                        {
                            if (vertices.Count >= QuantityGeometryEvidence.MaxVertices)
                            {
                                values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.OverLimit(vertices.Count + 1);
                                return;
                            }
                            if (tr.GetObject(id, OpenMode.ForRead) is not PolylineVertex3d vertex || vertex.VertexType != Vertex3dType.SimpleVertex)
                            {
                                values[QuantityGeometryEvidence.RawSegmentsStatus] = "non-simple-vertex";
                                return;
                            }
                            vertices.Add((vertex.Position.X, vertex.Position.Y, 0.0));
                        }
                        PutChain(values, entity, hostMetresPerUnit, vertices, polyline3d.Closed);
                        break;
                    }
                    case Arc arc:
                    {
                        if (!IsPlan(arc.Normal))
                        {
                            values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.StatusNonPlanNormal;
                            break;
                        }
                        var center = arc.Center;
                        var start = arc.StartPoint;
                        var end = arc.EndPoint;
                        var status = QuantityGeometryEvidence.TessellateArc(center.X, center.Y, arc.Radius, arc.StartAngle, arc.EndAngle,
                            (start.X, start.Y), (end.X, end.Y), MetresPerUnit(entity, hostMetresPerUnit), out var points);
                        Put(values, status, points, closed: false);
                        break;
                    }
                    case Circle circle:
                    {
                        if (!IsPlan(circle.Normal))
                        {
                            values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.StatusNonPlanNormal;
                            break;
                        }
                        var center = circle.Center;
                        var status = QuantityGeometryEvidence.TessellateCircle(center.X, center.Y, circle.Radius,
                            MetresPerUnit(entity, hostMetresPerUnit), out var points);
                        Put(values, status, points, closed: true);
                        break;
                    }
                    case BlockReference block:
                    {
                        // One block drawn twice at one point counts once: the raw insertion point, in plan.
                        var position = block.Position;
                        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
                        {
                            values[QuantityGeometryEvidence.RawInsertPointStatus] = QuantityGeometryEvidence.StatusNonFinitePoint;
                            break;
                        }
                        values[QuantityGeometryEvidence.RawInsertPoint] = QuantityGeometryEvidence.FormatPoint(position.X, position.Y);
                        break;
                    }
                    case Hatch h:
                        ReadHatchCentroid(h, values);
                        break;
                }
            }
            catch (Exception ex)
            {
                var statusKey = entity switch
                {
                    Hatch => QuantityGeometryEvidence.RawHatchStatus,
                    BlockReference => QuantityGeometryEvidence.RawInsertPointStatus,
                    _ => QuantityGeometryEvidence.RawSegmentsStatus,
                };
                values[statusKey] = "unavailable:" + ex.GetType().Name;
            }
        }

        private static bool IsPlan(Vector3d normal) => QuantityGeometryEvidence.IsPlanNormal(normal.X, normal.Y, normal.Z);

        private static bool IsFaceDown(Vector3d normal) => QuantityGeometryEvidence.IsFaceDownPlanNormal(normal.X, normal.Y, normal.Z);

        /// <summary>
        /// Metres per raw unit of the entity's own database (the scale the BoQ adapter applies later), so arc density is
        /// bounded in metres whatever the drawing unit; unknown units count as metres, as the adapter assumes.
        /// </summary>
        private static double MetresPerUnit(Entity entity, double? hostMetresPerUnit) =>
            hostMetresPerUnit ?? DrawingUnitPolicy.Resolve(entity.Database.Insunits).LinearToMetres;

        private static void PutChain(IDictionary<string, string> values, Entity entity, double? hostMetresPerUnit,
            List<(double X, double Y, double Bulge)> vertices, bool closed)
        {
            // Classification chain: the stored vertices, bulges ignored — exactly what the reference classified.
            var raw = vertices.ConvertAll(v => (v.X, v.Y));
            var rawStatus = raw.Count < 2 ? QuantityGeometryEvidence.StatusFewerThanTwoVertices
                : raw.Exists(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y)) ? QuantityGeometryEvidence.StatusNonFiniteVertex
                : QuantityGeometryEvidence.StatusComplete;
            PutRaw(values, rawStatus, raw, closed);
            // Measuring chain: bulged segments tessellated. The unit scale only sets arc density.
            var metresPerUnit = vertices.Exists(v => v.Bulge != 0) ? MetresPerUnit(entity, hostMetresPerUnit) : 1.0;
            var status = QuantityGeometryEvidence.TessellatePolyline(vertices, closed, metresPerUnit, out var points);
            PutPlan(values, status, points, closed);
        }

        /// <summary>Arcs and circles: the tessellated chain is the only plan chain; classification never used them.</summary>
        private static void Put(IDictionary<string, string> values, string status, List<(double X, double Y)> points, bool closed)
        {
            PutRaw(values, status, points, closed);
            PutPlan(values, status, points, closed);
        }

        private static void PutRaw(IDictionary<string, string> values, string status, List<(double X, double Y)> points, bool closed)
        {
            if (status != QuantityGeometryEvidence.StatusComplete)
            {
                values[QuantityGeometryEvidence.RawSegmentsStatus] = status;
                return;
            }
            // A closed chain never repeats its first point: the consumer adds the closing chord itself.
            values[QuantityGeometryEvidence.RawSegments] = QuantityGeometryEvidence.FormatVertices(points);
            values[QuantityGeometryEvidence.RawSegmentsClosed] = closed ? "true" : "false";
            values[QuantityGeometryEvidence.RawSegmentsStatus] = QuantityGeometryEvidence.StatusComplete;
        }

        private static void PutPlan(IDictionary<string, string> values, string status, List<(double X, double Y)> points, bool closed)
        {
            if (status != QuantityGeometryEvidence.StatusComplete)
            {
                values[QuantityGeometryEvidence.RawPlanChainStatus] = status;
                return;
            }
            values[QuantityGeometryEvidence.RawPlanChain] = QuantityGeometryEvidence.FormatVertices(points);
            values[QuantityGeometryEvidence.RawPlanChainClosed] = closed ? "true" : "false";
            values[QuantityGeometryEvidence.RawPlanChainStatus] = QuantityGeometryEvidence.StatusComplete;
        }

        /// <summary>
        /// v4.5: every loop as a closed ring — lines, circular arcs and polyline bulges cut into max(2, ⌊sweep·16⌋) pieces
        /// (QuantityHatchLoops, the reference discretisation). An elliptical or spline edge publishes no rings, only why.
        /// </summary>
        private static void ReadHatchLoops(Hatch hatch, IDictionary<string, string> values)
        {
            var rings = new List<IReadOnlyList<(double X, double Y)>>();
            var total = 0;
            try
            {
                for (var i = 0; i < hatch.NumberOfLoops; i++)
                {
                    var loop = hatch.GetLoopAt(i);
                    var ring = new List<(double X, double Y)>();
                    if (loop.IsPolyline)
                    {
                        var vertices = new List<(double X, double Y, double Bulge)>();
                        foreach (BulgeVertex vertex in loop.Polyline) vertices.Add((vertex.Vertex.X, vertex.Vertex.Y, vertex.Bulge));
                        for (var j = 0; j < vertices.Count; j++)
                        {
                            var a = vertices[j];
                            var b = vertices[(j + 1) % vertices.Count];
                            if (j == vertices.Count - 1 && Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12) break;
                            QuantityHatchLoops.Append(ring, QuantityHatchLoops.BulgePoints((a.X, a.Y), (b.X, b.Y), a.Bulge));
                        }
                    }
                    else
                    {
                        foreach (Curve2d curve in loop.Curves)
                        {
                            switch (curve)
                            {
                                case LineSegment2d line:
                                    QuantityHatchLoops.Append(ring, new List<(double X, double Y)>
                                        { (line.StartPoint.X, line.StartPoint.Y), (line.EndPoint.X, line.EndPoint.Y) });
                                    break;
                                case CircularArc2d arc:
                                    var sweep = arc.EndAngle - arc.StartAngle;
                                    QuantityHatchLoops.Append(ring, QuantityHatchLoops.ArcPointsFromStart(arc.Center.X, arc.Center.Y, arc.Radius,
                                        arc.StartPoint.X, arc.StartPoint.Y, arc.IsClockWise ? -sweep : sweep));
                                    break;
                                default:
                                    values[QuantityGeometryEvidence.RawHatchLoopsStatus] = "unsupported-edge:" + curve.GetType().Name;
                                    return;
                            }
                        }
                    }
                    total += ring.Count;
                    if (total > QuantityHatchLoops.MaxPoints)
                    {
                        values[QuantityGeometryEvidence.RawHatchLoopsStatus] = "over-limit:" + total.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return;
                    }
                    rings.Add(ring);
                }
            }
            catch (Exception ex)
            {
                values[QuantityGeometryEvidence.RawHatchLoopsStatus] = "unavailable:" + ex.GetType().Name;
                return;
            }
            if (rings.Count == 0 || rings.Any(r => r.Count < 3 || r.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))))
            {
                values[QuantityGeometryEvidence.RawHatchLoopsStatus] = "no-ring";
                return;
            }
            values[QuantityGeometryEvidence.RawHatchLoops] = QuantityHatchLoops.Format(rings);
            values[QuantityGeometryEvidence.RawHatchLoopsStatus] = QuantityGeometryEvidence.StatusComplete;
        }

        private static void ReadHatchCentroid(Hatch hatch, IDictionary<string, string> values)
        {
            double sx = 0, sy = 0;
            var n = 0;
            // BoQ rules 2.3 (v4): the same points, kept (bounded) so the engine can find the closed polyline drawn with
            // the hatch's own vertices — its boundary, whose area stands in when the hatch area is not returned.
            var points = new List<(double X, double Y)>();
            var loops = hatch.NumberOfLoops;
            for (var i = 0; i < loops; i++)
            {
                var loop = hatch.GetLoopAt(i);
                if (loop.IsPolyline)
                {
                    foreach (BulgeVertex vertex in loop.Polyline)
                    {
                        sx += vertex.Vertex.X;
                        sy += vertex.Vertex.Y;
                        n++;
                        if (points.Count <= QuantityGeometryEvidence.MaxHatchBoundaryPoints) points.Add((vertex.Vertex.X, vertex.Vertex.Y));
                    }
                }
                else
                {
                    foreach (Curve2d curve in loop.Curves)
                    {
                        if (curve is not LineSegment2d segment) continue;
                        sx += segment.StartPoint.X + segment.EndPoint.X;
                        sy += segment.StartPoint.Y + segment.EndPoint.Y;
                        n += 2;
                        if (points.Count <= QuantityGeometryEvidence.MaxHatchBoundaryPoints)
                        {
                            points.Add((segment.StartPoint.X, segment.StartPoint.Y));
                            points.Add((segment.EndPoint.X, segment.EndPoint.Y));
                        }
                    }
                }
            }
            ReadHatchLoops(hatch, values);
            if (n == 0 || !double.IsFinite(sx) || !double.IsFinite(sy))
            {
                values[QuantityGeometryEvidence.RawHatchStatus] = "no-line-edges";
                return;
            }
            values[QuantityGeometryEvidence.RawHatchCentroid] = QuantityGeometryEvidence.FormatPoint(sx / n, sy / n);
            values[QuantityGeometryEvidence.RawHatchStatus] = QuantityGeometryEvidence.StatusCentroid;
            if (n > QuantityGeometryEvidence.MaxHatchBoundaryPoints)
                values[QuantityGeometryEvidence.RawHatchBoundaryPointsStatus] =
                    "over-limit:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">" +
                    QuantityGeometryEvidence.MaxHatchBoundaryPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else
            {
                values[QuantityGeometryEvidence.RawHatchBoundaryPoints] = QuantityGeometryEvidence.FormatVertices(points);
                values[QuantityGeometryEvidence.RawHatchBoundaryPointsStatus] = QuantityGeometryEvidence.StatusComplete;
            }
        }
    }
}
