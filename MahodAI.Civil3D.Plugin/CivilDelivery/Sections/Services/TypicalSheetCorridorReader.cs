using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Shared;
using Layer = MahodAI.CivilDelivery.Shared.TypicalSheetGeometryLogic.Layer;
using Pt = MahodAI.CivilDelivery.Shared.TypicalSheetGeometryLogic.Pt;
using SectionGeometry = MahodAI.CivilDelivery.Shared.TypicalSheetGeometryLogic.SectionGeometry;
using SurfaceLink = MahodAI.CivilDelivery.Shared.TypicalSheetGeometryLogic.SurfaceLink;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The road at one station as the corridor built it, for the typical section (2026-11-02, the office's sheet
    /// DD-5001-02 draws the finished surface, the pavement layers and the existing ground): the applied assembly of the
    /// corridor's baseline on the pick's alignment nearest the station - its Top links (paved where the subassembly
    /// carries a pavement shape), its shapes as closed outlines, and the existing ground sampled across the same width.
    /// Offsets are the baseline's (right positive); levels are relative to the surface at the axis.
    /// </summary>
    internal static class TypicalSheetCorridorReader
    {
        private static readonly string[] PavementCodes = { "ASF", "ASPH", "PAVE", "AC-", "BIT" };

        public static SectionGeometry? Read(Transaction tr, string corridorName, string alignmentName, double station,
            TypicalSectionPick pick, string? groundSurfaceName, string? topSurfaceName = null)
        {
            var civil = CivilApplication.ActiveDocument;
            AppliedAssembly? applied = null;
            TinSurface? corridorTop = null;
            foreach (ObjectId id in civil.CorridorCollection)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Corridor corridor ||
                    !string.Equals(corridor.Name, corridorName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(topSurfaceName))
                    foreach (CorridorSurface cs in corridor.CorridorSurfaces)
                        if (string.Equals(cs.Name, topSurfaceName, StringComparison.OrdinalIgnoreCase) &&
                            !cs.SurfaceId.IsNull && tr.GetObject(cs.SurfaceId, OpenMode.ForRead) is TinSurface tin)
                        { corridorTop = tin; break; }
                foreach (Baseline baseline in corridor.Baselines)
                {
                    string name;
                    try { name = ((Alignment)tr.GetObject(baseline.AlignmentId, OpenMode.ForRead)).Name; }
                    catch { continue; }
                    if (!string.Equals(name, alignmentName, StringComparison.OrdinalIgnoreCase)) continue;
                    applied = Nearest(baseline, station);
                    if (applied != null) break;
                }
                if (applied != null) break;
            }
            if (applied == null) return null;

            // Offsets from the baseline, levels absolute (XYZ): the existing ground is sampled in absolute levels too.
            var surface = new List<SurfaceLink>();
            var layers = new List<Layer>();
            foreach (AppliedSubassembly sub in applied.GetAppliedSubassemblies())
            {
                var shapes = new List<Layer>();
                foreach (CalculatedShape shape in sub.Shapes)
                {
                    var outline = Outline(shape);
                    if (outline.Count >= 3) shapes.Add(new Layer(shape.CorridorCodes.FirstOrDefault() ?? "", outline));
                }
                var paved = shapes.Any(s => PavementCodes.Any(code => s.Code.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0));
                layers.AddRange(shapes);
                foreach (CalculatedLink link in sub.Links)
                {
                    if (!link.CorridorCodes.Any(c => string.Equals(c, "Top", StringComparison.OrdinalIgnoreCase))) continue;
                    var points = link.CalculatedPoints.Cast<CalculatedPoint>().Select(p => new Point3d(p.StationOffsetElevationToBaseline.X, p.StationOffsetElevationToBaseline.Y, p.XYZ.Z)).ToList();
                    for (var i = 1; i < points.Count; i++)
                        surface.Add(new SurfaceLink(new Pt(points[i - 1].Y, points[i - 1].Z), new Pt(points[i].Y, points[i].Z), paved));
                }
            }
            if (corridorTop != null)
            {
                try { ReadMissingTop(corridorTop, pick, surface); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { /* an unavailable TIN does not invalidate measured assembly links */ }
                catch (InvalidOperationException) { /* unbuilt surface */ }
            }
            if (surface.Count == 0) return null;

            // Levels relative to the surface at the axis (or its nearest point), so the block's 0 is the road at the axis.
            var reference = TypicalSheetGeometryLogic.LevelAt(surface, 0)
                            ?? surface.SelectMany(l => new[] { l.A, l.B }).OrderBy(p => Math.Abs(p.X)).First().Y;
            Pt Shift(Pt p) => new(p.X, p.Y - reference);
            surface = surface.Select(l => l with { A = Shift(l.A), B = Shift(l.B) }).ToList();
            layers = layers.Select(l => l with { Outline = l.Outline.Select(Shift).ToList() }).ToList();

            var ground = new List<Pt>();
            if (!string.IsNullOrEmpty(groundSurfaceName) && FindSurface(tr, civil, groundSurfaceName!) is { } eg)
            {
                var t = pick.TangentDeg * Math.PI / 180.0;
                var right = new Vector2d(Math.Sin(t), -Math.Cos(t));
                var minX = surface.Min(l => Math.Min(l.A.X, l.B.X)) - 6;
                var maxX = surface.Max(l => Math.Max(l.A.X, l.B.X)) + 6;
                for (var x = minX; x <= maxX + 1e-9; x += 0.5)
                {
                    try
                    {
                        var z = eg.FindElevationAtXY(pick.AxisX + right.X * x, pick.AxisY + right.Y * x);
                        ground.Add(new Pt(x, z - reference));
                    }
                    catch { /* outside the surface: a gap in the dashed line */ }
                }
            }
            return new SectionGeometry(surface, layers, ground);
        }

        // Fill only missing portions of this cut using visible triangles of this corridor's selected
        // finished surface. Keep assembly links and pavement shapes authoritative wherever present.
        private static void ReadMissingTop(TinSurface tin, TypicalSectionPick pick, List<SurfaceLink> surface)
        {
            var angle = pick.TangentDeg * Math.PI / 180;
            var dx = Math.Sin(angle);
            var dy = -Math.Cos(angle);
            var spans = TypicalSheetGeometryLogic.Coverage(surface);
            double start = -pick.HalfWidthM;
            foreach (var span in spans)
            {
                if (span.To <= start) continue;
                ReadGap(start, Math.Min(span.From, pick.HalfWidthM));
                start = Math.Max(start, span.To);
                if (start >= pick.HalfWidthM) break;
            }
            ReadGap(start, pick.HalfWidthM);

            void ReadGap(double from, double to)
            {
                for (var x = from + 1e-5; x < to; )
                {
                    TinSurfaceTriangle triangle;
                    try { triangle = tin.FindTriangleAtXY(pick.AxisX + dx * x, pick.AxisY + dy * x); }
                    catch (ArgumentException) { x += 0.25; continue; } // outside, or a hole
                    if (triangle == null || !triangle.IsVisible) { x += 0.25; continue; }
                    var vertices = new[] { triangle.Vertex1.Location, triangle.Vertex2.Location, triangle.Vertex3.Location };
                    var hits = new List<Pt>(3);
                    for (int i = 0; i < 3; i++)
                    {
                        var a = vertices[i]; var b = vertices[(i + 1) % 3];
                        double Side(Point3d v) => dx * (v.Y - pick.AxisY) - dy * (v.X - pick.AxisX);
                        double Offset(Point3d v) => dx * (v.X - pick.AxisX) + dy * (v.Y - pick.AxisY);
                        var sa = Side(a); var sb = Side(b);
                        if (Math.Abs(sa) < 1e-8) hits.Add(new Pt(Offset(a), a.Z));
                        if (sa * sb < 0)
                        {
                            var t = sa / (sa - sb);
                            hits.Add(new Pt(Offset(a) + t * (Offset(b) - Offset(a)), a.Z + t * (b.Z - a.Z)));
                        }
                    }
                    if (hits.Count < 2) { x += 0.25; continue; }
                    var left = hits.MinBy(p => p.X); var right = hits.MaxBy(p => p.X);
                    var lo = Math.Max(from, left.X); var hi = Math.Min(to, right.X);
                    if (hi - lo > 1e-7 && hi > x)
                    {
                        Pt At(double offset) => new(offset, left.Y + (offset - left.X) * (right.Y - left.Y) / (right.X - left.X));
                        surface.Add(new SurfaceLink(At(lo), At(hi), false));
                        x = hi + 1e-5;
                    }
                    else x += 0.25;
                }
            }
        }

        /// <summary>The applied assembly nearest the station on this baseline, within 25 m.</summary>
        private static AppliedAssembly? Nearest(Baseline baseline, double station)
        {
            AppliedAssembly? best = null;
            var bestDistance = 25.0;
            foreach (BaselineRegion region in baseline.BaselineRegions)
            {
                if (station < region.StartStation - TypicalCorridorChoiceLogic.RegionToleranceM ||
                    station > region.EndStation + TypicalCorridorChoiceLogic.RegionToleranceM) continue;
                foreach (AppliedAssembly applied in region.AppliedAssemblies)
                {
                    var first = applied.Points.Cast<CalculatedPoint>().FirstOrDefault();
                    if (first == null) continue;
                    var distance = Math.Abs(first.StationOffsetElevationToBaseline.X - station);
                    if (distance < bestDistance) { bestDistance = distance; best = applied; }
                }
            }
            return best;
        }

        /// <summary>A shape's outline: its links chained end to end, as offset / elevation.</summary>
        private static List<Pt> Outline(CalculatedShape shape)
        {
            var segments = new List<(Pt A, Pt B)>();
            foreach (CalculatedLink link in shape.CalculatedLinks)
            {
                var points = link.CalculatedPoints.Cast<CalculatedPoint>().Select(p => new Point3d(p.StationOffsetElevationToBaseline.X, p.StationOffsetElevationToBaseline.Y, p.XYZ.Z)).ToList();
                for (var i = 1; i < points.Count; i++)
                    segments.Add((new Pt(points[i - 1].Y, points[i - 1].Z), new Pt(points[i].Y, points[i].Z)));
            }
            var outline = new List<Pt>();
            if (segments.Count == 0) return outline;
            static bool Same(Pt a, Pt b) => Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4;
            var (start, next) = segments[0];
            segments.RemoveAt(0);
            outline.Add(start);
            var current = next;
            while (segments.Count > 0 && !Same(current, start))
            {
                outline.Add(current);
                var i = segments.FindIndex(s => Same(s.A, current) || Same(s.B, current));
                if (i < 0) break;
                var s = segments[i];
                segments.RemoveAt(i);
                current = Same(s.A, current) ? s.B : s.A;
            }
            return outline;
        }

        private static Autodesk.Civil.DatabaseServices.Surface? FindSurface(Transaction tr, CivilDocument civil, string name)
        {
            foreach (ObjectId id in civil.GetSurfaceIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Surface surface &&
                    string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase)) return surface;
            return null;
        }
    }
}
