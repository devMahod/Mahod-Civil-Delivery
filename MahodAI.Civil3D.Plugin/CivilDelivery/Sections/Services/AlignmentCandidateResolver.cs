using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Computes actual CL-segment × alignment intersection evidence (plan §7.3).
    /// Every geometrically valid candidate is captured; selection policy lives in
    /// the pure <see cref="SectionPlanLogic"/> so it stays unit-tested.
    /// </summary>
    public sealed class AlignmentCandidateResolver
    {
        /// <summary>Station probe step for tangent estimation, meters.</summary>
        private const double TangentProbe = 0.05;

        public List<AlignmentCrossing> FindCrossings(
            CivilDocument civilDoc,
            Transaction tr,
            ClSourceRecord cl,
            ProjectProfile profile,
            List<DeliveryFinding> findings,
            StageLog? log = null)
        {
            var crossings = new List<AlignmentCrossing>();
            if (cl.WcsEndpoints.Length != 4) return crossings;

            var a3 = new Point3d(cl.WcsEndpoints[0], cl.WcsEndpoints[1], 0);
            var b3 = new Point3d(cl.WcsEndpoints[2], cl.WcsEndpoints[3], 0);
            if (a3.DistanceTo(b3) < 0.001) return crossings;

            var allowed = profile.Sections.Alignments.AllowedNames;
            var tolerance = profile.Sections.Cl.IntersectionToleranceM;

            foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
            {
                CivilDb.Alignment? alignment;
                try
                {
                    alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as CivilDb.Alignment;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Alignment inventory is unreadable at object {alignmentId}; PLAN was refused.", ex);
                }
                if (alignment == null)
                    throw new InvalidDataException(
                        $"Civil alignment id {alignmentId} did not open as an Alignment.");
                if (allowed.Count > 0 &&
                    !allowed.Any(n => ClInstructionReader.WildcardMatch(alignment.Name, n)))
                    continue;
                var geometry = AlignmentGeometryGuard.Read(alignment);
                if (geometry.IsEmpty)
                {
                    // 984 (06.10): IntersectWith on an empty alignment throws eDegenerateGeometry and refused PLAN.
                    AlignmentGeometryGuard.Report(findings, alignment.Name, profile.ProfileId, geometry);
                    continue;
                }

                try
                {
                    log?.Begin("resolver.intersect", $"alignment={alignment.Name}");
                    CollectCrossingsWithAlignment(alignment, a3, b3, tolerance, crossings);
                    log?.End("resolver.intersect", $"alignment={alignment.Name}");
                }
                catch (Exception ex)
                {
                    log?.Fail($"resolver.intersect:{alignment.Name}", ex);
                    throw new InvalidOperationException(
                        $"Cannot prove the crossing of alignment '{alignment.Name}' and CL {cl.SourceHandle}; PLAN was refused.",
                        ex);
                }
            }

            return crossings
                .OrderBy(c => c.AlignmentName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Station)
                .ToList();
        }

        private static void CollectCrossingsWithAlignment(
            CivilDb.Alignment alignment,
            Point3d a,
            Point3d b,
            double? toleranceM,
            List<AlignmentCrossing> crossings)
        {
            // Exact geometric intersection through the real Civil entity.
            var exact = IntersectSegment(alignment, a, b);
            foreach (var p in exact)
            {
                if (TryDescribe(alignment, p, gap: 0, out var crossing))
                    crossings.Add(crossing);
            }
            if (exact.Count > 0 || toleranceM is null or <= 0) return;

            // Drafting-gap path: extend the CL by the approved tolerance at both ends
            // and re-intersect. GapDistance records how far beyond the drawn segment
            // the crossing lies — evidence, not silent repair.
            var dir = (b - a).GetNormal();
            var aExt = a - dir * toleranceM.Value;
            var bExt = b + dir * toleranceM.Value;
            foreach (var p in IntersectSegment(alignment, aExt, bExt))
            {
                var gap = Math.Min(p.DistanceTo(a), p.DistanceTo(b));
                var onSegment = SectionMath.DistancePointToSegment(
                    new Pt2(p.X, p.Y),
                    new Pt2(a.X, a.Y),
                    new Pt2(b.X, b.Y)) < 1e-6;
                if (TryDescribe(alignment, p, onSegment ? 0 : gap, out var crossing))
                    crossings.Add(crossing);
            }
        }

        private static List<Point3d> IntersectSegment(CivilDb.Alignment alignment, Point3d a, Point3d b)
        {
            var points = new Point3dCollection();
            using (var probe = new Line(a, b))
            {
                alignment.IntersectWith(probe, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
            }

            var result = new List<Point3d>();
            foreach (Point3d p in points)
            {
                if (!result.Any(q => q.DistanceTo(p) < 1e-6))
                    result.Add(p);
            }
            return result;
        }

        private static bool TryDescribe(
            CivilDb.Alignment alignment, Point3d p, double gap, out AlignmentCrossing crossing)
        {
            crossing = null!;
            double station = 0, offset = 0;
            try
            {
                alignment.StationOffset(p.X, p.Y, ref station, ref offset);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not derive station/offset on alignment '{alignment.Name}'.", ex);
            }

            double tangentDeg;
            try
            {
                tangentDeg = TangentAt(alignment, station);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not derive tangent on alignment '{alignment.Name}'.", ex);
            }

            if (!double.IsFinite(station) || !double.IsFinite(offset) ||
                !double.IsFinite(tangentDeg) || !double.IsFinite(gap))
                throw new InvalidDataException(
                    $"Alignment '{alignment.Name}' returned non-finite crossing evidence.");

            crossing = new AlignmentCrossing
            {
                AlignmentName = alignment.Name,
                Point = new[] { p.X, p.Y },
                Station = Math.Round(station, 4),
                TangentDeg = Math.Round(tangentDeg, 6),
                GapDistance = Math.Round(gap, 4),
            };
            return true;
        }

        /// <summary>
        /// Tangent direction (degrees) at a station via symmetric on-alignment probes.
        /// Probe points come from the real alignment geometry, clamped to its range.
        /// </summary>
        internal static double TangentAt(CivilDb.Alignment alignment, double station)
        {
            double s0 = Math.Max(alignment.StartingStation, station - TangentProbe);
            double s1 = Math.Min(alignment.EndingStation, station + TangentProbe);
            if (s1 - s0 < 1e-6)
            {
                s0 = Math.Max(alignment.StartingStation, station - 2 * TangentProbe);
                s1 = Math.Min(alignment.EndingStation, station + 2 * TangentProbe);
            }

            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            alignment.PointLocation(s0, 0, ref x0, ref y0);
            alignment.PointLocation(s1, 0, ref x1, ref y1);
            return SectionMath.DirectionDeg(new Pt2(x0, y0), new Pt2(x1, y1));
        }
    }
}
