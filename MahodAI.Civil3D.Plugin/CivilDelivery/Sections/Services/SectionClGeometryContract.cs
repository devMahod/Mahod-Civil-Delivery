using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Shared discovery/PLAN CL shape proof. No fitted or curved legacy
/// polyline is reduced to its endpoints without examining its actual vertices.</summary>
internal static class SectionClGeometryContract
{
    internal readonly record struct Vertex(double X, double Y, double Z, double Bulge, bool Simple = true);
    internal sealed record Geometry(IReadOnlyList<Pt2> SourceVertices, IReadOnlyList<Pt2> WorldVertices,
        IReadOnlyList<double> Bulges, IReadOnlyList<Pt2> WorldSagittaVectors, bool Closed, int SourceVertexCount = 0);

    internal static IReadOnlyList<Vertex> ReadLegacyVertices<TId>(IEnumerable<TId> ids, Func<TId, Vertex> read)
    {
        var result = new List<Vertex>();
        foreach (var id in ids)
        {
            var vertex = read(id);
            if (!vertex.Simple) throw new NotSupportedException("CL legacy polyline has fitted/control vertices.");
            RequireFinite(vertex);
            result.Add(vertex);
        }
        return result;
    }

    internal static Geometry Read(Entity entity, Transaction transaction, Matrix3d transform)
    {
        var closed = ClInstructionReader.IsClosedPolyline(entity);
        if (closed) return new Geometry(Array.Empty<Pt2>(), Array.Empty<Pt2>(), Array.Empty<double>(), Array.Empty<Pt2>(), true,
            entity is Polyline closedLw ? closedLw.NumberOfVertices :
                entity is Polyline2d closedLegacy ? closedLegacy.Cast<ObjectId>().Count() : 0);
        var vertices = new List<Vertex>();
        var normal = Vector3d.ZAxis;
        switch (entity)
        {
            case Line line:
                Add(line.StartPoint, 0); Add(line.EndPoint, 0);
                break;
            case Polyline polyline:
                normal = polyline.Normal;
                // GetPoint3dAt returns drawing WCS, unlike GetPoint2dAt (OCS).
                // https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Methods_Autodesk_AutoCAD_DatabaseServices_Polyline.html
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                    Add(polyline.GetPoint3dAt(i), i + 1 < polyline.NumberOfVertices ? polyline.GetBulgeAt(i) : 0);
                break;
            case Polyline2d legacy:
                if (legacy.PolyType != Poly2dType.SimplePoly)
                    throw new NotSupportedException("CL legacy polyline is fitted, not SimplePoly.");
                normal = legacy.Normal;
                vertices.AddRange(ReadLegacyVertices(legacy.Cast<ObjectId>(), id =>
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is not Vertex2d vertex)
                        throw new InvalidOperationException("CL legacy vertex could not be read as Vertex2d.");
                    // Vertex2d.Position is OCS and does not contain the owning elevation.
                    // VertexPosition applies BOTH normal and elevation to return WCS.
                    // https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Polyline2d_VertexPosition_Vertex2d.html
                    var point = legacy.VertexPosition(vertex);
                    return new Vertex(point.X, point.Y, point.Z, vertex.Bulge,
                        vertex.VertexType == Vertex2dType.SimpleVertex);
                }));
                break;
            default: throw new InvalidOperationException("Unsupported CL entity type.");
        }

        var world = vertices.Select(v => new Point3d(v.X, v.Y, v.Z).TransformBy(transform))
            .Select(p =>
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
                    throw new InvalidOperationException("CL has non-finite transformed vertices.");
                return new Pt2(p.X, p.Y);
            }).ToArray();
        var sagittas = new List<Pt2>();
        for (var i = 0; i + 1 < vertices.Count; i++)
        {
            var a = vertices[i]; var b = vertices[i + 1];
            var chord = new Vector3d(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            if (!double.IsFinite(chord.Length)) throw new InvalidOperationException("CL has a non-finite source chord.");
            if (a.Bulge != 0 && chord.Length == 0)
                throw new InvalidOperationException("CL arc has a zero source chord.");
            var offset = new Vector3d(0, 0, 0);
            if (a.Bulge != 0 && chord.Length > 0)
                offset = (normal.CrossProduct(chord).GetNormal() * (a.Bulge * chord.Length / 2)).TransformBy(transform);
            if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || !double.IsFinite(offset.Z))
                throw new InvalidOperationException("CL has a non-finite sagitta displacement.");
            sagittas.Add(new Pt2(offset.X, offset.Y));
        }
        return new Geometry(vertices.Select(v => new Pt2(v.X, v.Y)).ToArray(), world,
            vertices.Take(Math.Max(0, vertices.Count - 1)).Select(v => v.Bulge).ToArray(), sagittas, false, vertices.Count);

        void Add(Point3d point, double bulge)
        {
            var vertex = new Vertex(point.X, point.Y, point.Z, bulge);
            RequireFinite(vertex); vertices.Add(vertex);
        }
    }

    internal static SectionClCandidateLogic.Result Analyze(Geometry geometry)
    {
        var points = geometry.WorldVertices;
        if (geometry.Closed)
            return new(SectionClCandidateLogic.Decision.IgnoreClosedNonInstruction, 0, double.NaN);
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)) ||
            geometry.Bulges.Count != Math.Max(0, points.Count - 1) ||
            geometry.WorldSagittaVectors.Count != geometry.Bulges.Count)
            return Invalid();
        var basic = SectionClCandidateLogic.Analyze(points.Select(p => (p.X, p.Y)).ToArray(),
            ClInstructionReader.StraightSagittaToleranceM, geometry.Closed);
        if (basic.Kind == SectionClCandidateLogic.Decision.Degenerate) return basic;
        var first = points[0]; var last = points[^1]; var chord = basic.ChordLength;
        var ux = (last.X - first.X) / chord; var uy = (last.Y - first.Y) / chord;
        double Deviation(Pt2 p) => Math.Abs(ux * (p.Y - first.Y) - uy * (p.X - first.X));
        var maximum = 0d;
        for (var i = 0; i < geometry.Bulges.Count; i++)
        {
            var bulge = geometry.Bulges[i]; var offset = geometry.WorldSagittaVectors[i];
            if (!double.IsFinite(bulge) || Math.Abs(bulge) > 1 ||
                !double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) return Invalid(chord);
            // A minor arc lies between its segment chord and midpoint sagitta.
            // Linear transforms preserve this enclosure. Add the endpoint deviation
            // from the TOTAL CL chord to the arc's projected displacement. This is a
            // conservative bound, not sampled geometry or an invented straightening.
            var bound = Math.Max(Deviation(points[i]), Deviation(points[i + 1])) +
                        Math.Abs(ux * offset.Y - uy * offset.X);
            if (!double.IsFinite(bound)) return Invalid(chord);
            maximum = Math.Max(maximum, bound);
        }
        return maximum <= ClInstructionReader.StraightSagittaToleranceM
            ? new(points.Count > 2 || geometry.Bulges.Any(b => b != 0)
                ? SectionClCandidateLogic.Decision.CollapseToChord : SectionClCandidateLogic.Decision.Straight, chord, maximum)
            : new(SectionClCandidateLogic.Decision.Degenerate, chord, maximum);
    }

    private static SectionClCandidateLogic.Result Invalid(double chord = 0) =>
        new(SectionClCandidateLogic.Decision.Degenerate, chord, double.NaN);

    private static void RequireFinite(Vertex vertex)
    {
        if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) ||
            !double.IsFinite(vertex.Z) || !double.IsFinite(vertex.Bulge))
            throw new InvalidOperationException("CL vertex or bulge is not finite.");
    }
}
