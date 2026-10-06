using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using NetTopologySuite.Geometries;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Two-channel guide discovery for the cost-discount field:
    /// • <b>Centerlines</b> (open polylines from the surface's BreaklinesDefinition):
    ///   the existing road. Strong cost discount on cells near these.
    /// • <b>Zones</b> (closed polylines in model space): a "preferred corridor"
    ///   the engineer marked. Moderate cost discount inside these polygons.
    ///
    /// Both channels filter by an inflated A↔B AABB to ignore far-away geometry,
    /// and degrade gracefully — any failure (missing surface, malformed entity,
    /// API mismatch) returns empty for that channel without taking down the
    /// rest of the routing pipeline.
    /// </summary>
    public static class GuideDiscovery
    {
        public sealed class DiscoveredGuides
        {
            public List<List<Pt2>> Centerlines { get; init; } = new();
            public List<List<Pt2>> Zones { get; init; } = new();
            public List<string> Warnings { get; init; } = new();
        }

        public static DiscoveredGuides Find(
            Transaction tr,
            ObjectId surfaceId,
            Envelope inflatedAabb,
            string? guideLayer = null,
            string? zoneLayer = null)
        {
            var result = new DiscoveredGuides();

            // ── Channel 1: surface breaklines = green road centerline ──────
            if (!surfaceId.IsNull)
            {
                try
                {
                    var surface = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface;
                    if (surface != null)
                    {
                        ReadBreaklinesIntoCenterlines(tr, surface, inflatedAabb, guideLayer, result);
                    }
                    else
                    {
                        result.Warnings.Add("Surface for guide discovery is not a TinSurface; breakline scan skipped.");
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Breakline scan failed: {ex.Message}");
                }
            }

            // ── Channel 2: model-space closed polylines = blue preferred zones ──
            try
            {
                ReadClosedPolylinesIntoZones(tr, inflatedAabb, zoneLayer, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Closed-polyline zone scan failed: {ex.Message}");
            }

            return result;
        }

        // ── Breaklines on the surface ──────────────────────────────────────
        private static void ReadBreaklinesIntoCenterlines(
            Transaction tr, TinSurface surface, Envelope aabb,
            string? guideLayer, DiscoveredGuides result)
        {
            // Civil 3D's BreaklinesDefinition is a collection of breakline sets;
            // each set has zero or more entity IDs pointing at the source
            // 3D polylines / polylines / feature lines / sometimes block refs.
            var bld = surface.BreaklinesDefinition;
            int count;
            try { count = bld.Count; } catch { return; }

            for (int i = 0; i < count; i++)
            {
                object? item;
                try { item = bld[i]; }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Breakline set {i}: indexer failed ({ex.Message}).");
                    continue;
                }
                if (item == null) continue;

                // The exact method/property exposing the entity IDs varies
                // between Civil 3D SDK versions (GetBreaklineIds /
                // GetBreaklinesIds / BreaklineIds / ObjectIds / GetEntities
                // have all appeared). Probe by reflection so we tolerate
                // SDK renames without a recompile.
                ObjectIdCollection? ids = ResolveBreaklineIds(item);
                if (ids == null)
                {
                    result.Warnings.Add(
                        $"Breakline set {i}: no enumerable entity-ID accessor on " +
                        $"{item.GetType().Name}. The breakline may have been imported " +
                        $"from a file with no drawing entities — Civil 3D stores the " +
                        $"raw points inside the surface itself in that case.");
                    continue;
                }

                foreach (ObjectId id in ids)
                {
                    var pts = ExtractPolylineGeometry(tr, id, Matrix3d.Identity);
                    if (pts == null || pts.Count < 2) continue;
                    if (guideLayer != null && !LayerMatches(tr, id, guideLayer)) continue;
                    if (!IntersectsAabb(pts, aabb)) continue;
                    result.Centerlines.Add(pts);
                }
            }
        }

        // Probe a SurfaceOperationAddBreakline-shaped object for an
        // ObjectIdCollection-returning member named one of the historical
        // accessors. Returns null if none is found.
        private static ObjectIdCollection? ResolveBreaklineIds(object item)
        {
            string[] candidateMembers = {
                "GetBreaklineIds", "GetBreaklinesIds", "GetEntityIds",
                "GetEntities", "BreaklineIds", "ObjectIds",
            };
            var t = item.GetType();
            foreach (var name in candidateMembers)
            {
                try
                {
                    var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                    if (m != null && typeof(ObjectIdCollection).IsAssignableFrom(m.ReturnType))
                    {
                        var v = m.Invoke(item, null);
                        if (v is ObjectIdCollection ids) return ids;
                    }
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && typeof(ObjectIdCollection).IsAssignableFrom(p.PropertyType))
                    {
                        var v = p.GetValue(item);
                        if (v is ObjectIdCollection ids) return ids;
                    }
                }
                catch { /* try next */ }
            }
            return null;
        }

        // ── Closed polylines from model space = preferred zones ────────────
        private static void ReadClosedPolylinesIntoZones(
            Transaction tr, Envelope aabb, string? zoneLayer, DiscoveredGuides result)
        {
            var db = HostApplicationServices.WorkingDatabase;
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return;
            var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return;

            foreach (ObjectId id in ms)
            {
                AcEntity? ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                catch { continue; }
                if (ent == null) continue;

                if (zoneLayer != null &&
                    !ent.Layer.Equals(zoneLayer, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<Pt2>? loop = null;
                if (ent is Polyline pl && pl.Closed && pl.NumberOfVertices >= 3)
                {
                    loop = new List<Pt2>(pl.NumberOfVertices);
                    for (int v = 0; v < pl.NumberOfVertices; v++)
                    {
                        var p = pl.GetPoint2dAt(v);
                        loop.Add(new Pt2(p.X, p.Y));
                    }
                }
                else if (ent is Polyline2d p2d && p2d.Closed)
                {
                    loop = ExtractPolyline2dVertices(tr, p2d);
                }
                else if (ent is FeatureLine fl)
                {
                    // Engineers draw the preferred road envelope as a CLOSED box
                    // made of feature lines (not a closed LWPOLYLINE), so the
                    // closed-polyline scan above misses it. Treat a closed
                    // feature-line loop as a preferred zone too. Closure is read
                    // from the SDK's Closed/IsClosed flag (reflection — the
                    // property name varies by version), with a geometric
                    // endpoints-meet fallback for loops drawn open.
                    bool closed = false;
                    try
                    {
                        var cp = fl.GetType().GetProperty("Closed")
                                 ?? fl.GetType().GetProperty("IsClosed");
                        if (cp != null && cp.PropertyType == typeof(bool))
                            closed = (bool)(cp.GetValue(fl) ?? false);
                    }
                    catch { /* fall through to geometric test */ }

                    var flPts = ExtractPolylineGeometry(tr, id, Matrix3d.Identity);
                    if (flPts != null && flPts.Count >= 3)
                    {
                        if (!closed && flPts.Count >= 4)
                        {
                            double dx = flPts[0].X - flPts[flPts.Count - 1].X;
                            double dy = flPts[0].Y - flPts[flPts.Count - 1].Y;
                            if ((dx * dx) + (dy * dy) < 0.25) closed = true; // ≤0.5 m
                        }
                        if (closed) loop = flPts;
                    }
                }

                if (loop == null || loop.Count < 3) continue;
                if (!IntersectsAabb(loop, aabb)) continue;

                // Drop tiny loops — likely parcels/buildings, not road zones.
                double perim = LoopPerimeter(loop);
                if (perim < 80.0) continue;

                result.Zones.Add(loop);
            }
        }

        /// <summary>
        /// Reads the engineer-drawn priority path: the LONGEST polyline (LWPOLYLINE / 2D / 3D)
        /// on <paramref name="layer"/>, returned as ordered world-XY vertices. This is the hard
        /// priority the road follows (see PriorityPathPlanner) — distinct from the soft closed-zone
        /// cost discount. Returns null when the layer holds no usable polyline.
        /// </summary>
        public static List<Pt2>? ReadPriorityPath(Transaction tr, string layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return null;
            var db = HostApplicationServices.WorkingDatabase;
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return null;
            var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return null;

            List<Pt2>? best = null;
            double bestLen = 0;
            foreach (ObjectId id in ms)
            {
                AcEntity? ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                catch { continue; }
                if (ent == null) continue;
                if (!ent.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase)) continue;

                var pts = ExtractPolylineGeometry(tr, id, Matrix3d.Identity);
                if (pts == null || pts.Count < 2) continue;
                double len = PolylineLength(pts);
                if (best == null || len > bestLen) { best = pts; bestLen = len; }
            }
            return best;
        }

        // ── Polyline-shape extraction across entity types ──────────────────
        private static List<Pt2>? ExtractPolylineGeometry(Transaction tr, ObjectId id, Matrix3d xform)
        {
            try
            {
                var ent = tr.GetObject(id, OpenMode.ForRead);
                switch (ent)
                {
                    case Polyline pl:
                        {
                            var list = new List<Pt2>(pl.NumberOfVertices);
                            for (int v = 0; v < pl.NumberOfVertices; v++)
                            {
                                var p = pl.GetPoint3dAt(v).TransformBy(xform);
                                list.Add(new Pt2(p.X, p.Y));
                            }
                            return list;
                        }
                    case Polyline2d p2d:
                        {
                            var list = ExtractPolyline2dVertices(tr, p2d);
                            if (list == null) return null;
                            return TransformList(list, xform);
                        }
                    case Polyline3d p3d:
                        {
                            var list = new List<Pt2>();
                            foreach (ObjectId vId in p3d)
                            {
                                var v = tr.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                                if (v == null) continue;
                                var p = v.Position.TransformBy(xform);
                                list.Add(new Pt2(p.X, p.Y));
                            }
                            return list;
                        }
                    case FeatureLine fl:
                        {
                            var list = new List<Pt2>();
                            try
                            {
                                // Civil 3D's FeatureLine exposes GetPoints() returning
                                // every vertex (PI + elevation + intermediate points).
                                // Some SDK versions expose an enum-typed overload too;
                                // the no-arg one is consistently available.
                                Point3dCollection pts = fl.GetPoints(
                                    Autodesk.Civil.FeatureLinePointType.AllPoints);
                                foreach (Point3d p in pts)
                                {
                                    var t = p.TransformBy(xform);
                                    list.Add(new Pt2(t.X, t.Y));
                                }
                            }
                            catch
                            {
                                // Fall back to the no-enum overload if the typed one
                                // isn't available in this SDK version.
                                try
                                {
                                    var m = fl.GetType().GetMethod("GetPoints", Type.EmptyTypes);
                                    if (m == null) return null;
                                    var v = m.Invoke(fl, null);
                                    if (v is Point3dCollection pts2)
                                    {
                                        foreach (Point3d p in pts2)
                                        {
                                            var t = p.TransformBy(xform);
                                            list.Add(new Pt2(t.X, t.Y));
                                        }
                                    }
                                }
                                catch { return null; }
                            }
                            return list;
                        }
                    case BlockReference blk:
                        // Walk into the block definition; combine the insert's
                        // transform with anything we picked up from the block.
                        return ExtractFromBlock(tr, blk, xform);
                    default:
                        return null;
                }
            }
            catch { return null; }
        }

        private static List<Pt2>? ExtractPolyline2dVertices(Transaction tr, Polyline2d p2d)
        {
            var list = new List<Pt2>();
            try
            {
                foreach (ObjectId vId in p2d)
                {
                    var v = tr.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                    if (v == null) continue;
                    var p = v.Position;
                    list.Add(new Pt2(p.X, p.Y));
                }
            }
            catch { return null; }
            return list.Count >= 2 ? list : null;
        }

        private static List<Pt2>? ExtractFromBlock(Transaction tr, BlockReference blk, Matrix3d outer)
        {
            try
            {
                var btr = tr.GetObject(blk.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return null;
                Matrix3d combined = outer * blk.BlockTransform;
                // A block can hold multiple polylines; concatenate the longest one.
                List<Pt2>? best = null;
                foreach (ObjectId childId in btr)
                {
                    var pts = ExtractPolylineGeometry(tr, childId, combined);
                    if (pts == null || pts.Count < 2) continue;
                    if (best == null || PolylineLength(pts) > PolylineLength(best)) best = pts;
                }
                return best;
            }
            catch { return null; }
        }

        private static List<Pt2> TransformList(List<Pt2> list, Matrix3d xform)
        {
            if (xform == Matrix3d.Identity) return list;
            var result = new List<Pt2>(list.Count);
            foreach (var p in list)
            {
                var t = new Point3d(p.X, p.Y, 0).TransformBy(xform);
                result.Add(new Pt2(t.X, t.Y));
            }
            return result;
        }

        // ── Geometry helpers ───────────────────────────────────────────────
        private static double PolylineLength(List<Pt2> pts)
        {
            double sum = 0;
            for (int i = 1; i < pts.Count; i++) sum += pts[i].DistanceTo(pts[i - 1]);
            return sum;
        }

        private static double LoopPerimeter(List<Pt2> loop)
        {
            double sum = PolylineLength(loop);
            if (loop.Count >= 2) sum += loop[0].DistanceTo(loop[^1]);
            return sum;
        }

        // ── Open corridor side-pair discovery (for RoadEvidenceExtractor) ──

        /// <summary>
        /// Finds the corridor when the engineers drew it as TWO OPEN parallel polylines instead
        /// of one closed box: among the top-level model-space open polylines / feature lines
        /// (length ≥ max(200 m, 25% of |AB|), inside the A↔B AABB) picks the pair whose mutual
        /// spacing is a plausible corridor width (15–400 m, roughly constant) AND that brackets
        /// both picks. Survey linework is not scanned — the blue corridor lines are drawn at the
        /// top level, which keeps this cheap and unambiguous.
        /// </summary>
        public static (List<Pt2>? SideA, List<Pt2>? SideB) FindOpenSidePair(
            Transaction tr, Envelope aabb, Pt2 a, Pt2 b, string? zoneLayer = null)
        {
            var candidates = new List<List<Pt2>>();
            try
            {
                var db = HostApplicationServices.WorkingDatabase;
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var ms = bt == null
                    ? null
                    : tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (ms == null) return (null, null);

                double minLen = Math.Max(200.0, 0.25 * a.DistanceTo(b));
                foreach (ObjectId id in ms)
                {
                    AcEntity? ent;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                    catch { continue; }
                    if (ent == null || ent is BlockReference) continue;
                    if (zoneLayer != null &&
                        !ent.Layer.Equals(zoneLayer, StringComparison.OrdinalIgnoreCase)) continue;

                    bool closed = false;
                    if (ent is Polyline pl) closed = pl.Closed;
                    else if (ent is Polyline2d p2) closed = p2.Closed;
                    else if (ent is Polyline3d p3) closed = p3.Closed;
                    if (closed) continue;             // closed loops go through the zone channel

                    var pts = ExtractPolylineGeometry(tr, id, Matrix3d.Identity);
                    if (pts == null || pts.Count < 2) continue;
                    if (PolylineLength(pts) < minLen) continue;
                    if (!IntersectsAabb(pts, aabb)) continue;
                    candidates.Add(pts);
                }
            }
            catch { return (null, null); }

            if (candidates.Count < 2) return (null, null);
            candidates.Sort((x, y) => PolylineLength(y).CompareTo(PolylineLength(x)));
            if (candidates.Count > 12) candidates.RemoveRange(12, candidates.Count - 12);

            List<Pt2>? bestA = null, bestB = null;
            double bestScore = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (!PairBracketsPicks(candidates[i], candidates[j], a, b,
                                           out double widthMed, out double widthSpread))
                        continue;
                    // Prefer tight, parallel corridors; a stable spacing beats raw size.
                    double score = widthSpread * 1000.0 + widthMed;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestA = candidates[i];
                        bestB = candidates[j];
                    }
                }
            return (bestA, bestB);
        }

        private static bool PairBracketsPicks(List<Pt2> s1, List<Pt2> s2, Pt2 a, Pt2 b,
                                              out double widthMed, out double widthSpread)
        {
            widthMed = 0;
            widthSpread = double.MaxValue;
            var widths = new List<double>();
            int samples = Math.Min(24, s1.Count);
            for (int k = 0; k < samples; k++)
            {
                var p = s1[(int)((long)k * (s1.Count - 1) / Math.Max(1, samples - 1))];
                double d = DistanceToChain(p, s2);
                if (d > 1e-6) widths.Add(d);
            }
            if (widths.Count < Math.Max(3, samples / 2)) return false;
            widths.Sort();
            widthMed = widths[widths.Count / 2];
            if (widthMed < 15.0 || widthMed > 400.0) return false;
            widthSpread = widths[(int)(widths.Count * 0.9)] / Math.Max(1.0, widths[(int)(widths.Count * 0.1)]);
            if (widthSpread > 5.0) return false;

            // Both picks must sit BETWEEN the sides: near both chains at complementary distances.
            foreach (var p in new[] { a, b })
            {
                double d1 = DistanceToChain(p, s1);
                double d2 = DistanceToChain(p, s2);
                if (d1 + d2 > widthMed * 1.6 + 60.0) return false;
            }
            return true;
        }

        private static double DistanceToChain(Pt2 p, List<Pt2> chain)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                Pt2 s = chain[i], e = chain[i + 1];
                double dx = e.X - s.X, dy = e.Y - s.Y;
                double l2 = dx * dx + dy * dy;
                double t = l2 < 1e-12 ? 0 : Math.Max(0.0, Math.Min(1.0, ((p.X - s.X) * dx + (p.Y - s.Y) * dy) / l2));
                double qx = s.X + t * dx, qy = s.Y + t * dy;
                double d = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
                if (d < best) best = d;
            }
            return best;
        }

        // ── Road-edge evidence collection (for RoadEvidenceExtractor) ─────

        /// <summary>
        /// Collects survey road-edge polylines (asphalt edge / kerb / shoulder linework) from
        /// model space AND from entities nested one level inside block references — Israeli
        /// survey bases usually arrive as one big block whose layers look like
        /// "R73-2021$0$11KAV-ASFALT". A chain is kept when its SHORT layer name (after the last
        /// '$') contains any of <paramref name="layerPatterns"/>, it touches the AABB, and — when
        /// a zone loop is given — at least <paramref name="insideMinFraction"/> of its vertices
        /// lie inside the loop (drops the parallel local roads outside the corridor).
        /// </summary>
        public static List<RoadEvidenceExtractor.EvidenceChain> CollectRoadEdges(
            Transaction tr,
            Envelope aabb,
            IReadOnlyList<string> layerPatterns,
            IReadOnlyList<Pt2>? insideLoop,
            double insideMinFraction = 0.3)
        {
            var chains = new List<RoadEvidenceExtractor.EvidenceChain>();
            if (layerPatterns == null || layerPatterns.Count == 0) return chains;

            var db = HostApplicationServices.WorkingDatabase;
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return chains;
            var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return chains;

            foreach (ObjectId id in ms)
            {
                AcEntity? ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                catch { continue; }
                if (ent == null) continue;

                if (ent is BlockReference br)
                {
                    BlockTableRecord? btr = null;
                    try { btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord; }
                    catch { }
                    if (btr == null) continue;
                    Matrix3d xf;
                    try { xf = br.BlockTransform; } catch { xf = Matrix3d.Identity; }
                    foreach (ObjectId cid in btr)
                        TryCollectRoadEdge(tr, cid, xf, aabb, layerPatterns, insideLoop, insideMinFraction, chains);
                }
                else
                {
                    TryCollectRoadEdge(tr, id, Matrix3d.Identity, aabb, layerPatterns, insideLoop, insideMinFraction, chains);
                }
            }
            return chains;
        }

        private static void TryCollectRoadEdge(
            Transaction tr, ObjectId id, Matrix3d xf, Envelope aabb,
            IReadOnlyList<string> layerPatterns, IReadOnlyList<Pt2>? insideLoop,
            double insideMinFraction, List<RoadEvidenceExtractor.EvidenceChain> chains)
        {
            try
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity;
                if (ent == null || ent is BlockReference) return;   // one nesting level only

                string layer = ent.Layer ?? "";
                int cut = layer.LastIndexOf('$');
                string shortLayer = cut >= 0 ? layer.Substring(cut + 1) : layer;
                bool match = false;
                foreach (var pat in layerPatterns)
                {
                    if (!string.IsNullOrWhiteSpace(pat) &&
                        shortLayer.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                    { match = true; break; }
                }
                if (!match) return;

                var pts = ExtractPolylineGeometry(tr, id, xf);
                if (pts == null || pts.Count < 2) return;
                if (!IntersectsAabb(pts, aabb)) return;

                if (insideLoop != null)
                {
                    int inside = 0;
                    foreach (var p in pts)
                        if (RoadEvidenceExtractor.PointInLoop(insideLoop, p)) inside++;
                    if (inside < pts.Count * insideMinFraction) return;
                }

                chains.Add(new RoadEvidenceExtractor.EvidenceChain(shortLayer, pts));
            }
            catch { /* one bad entity never kills the scan */ }
        }

        private static bool IntersectsAabb(List<Pt2> pts, Envelope aabb)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return !(maxX < aabb.MinX || minX > aabb.MaxX ||
                     maxY < aabb.MinY || minY > aabb.MaxY);
        }

        private static bool LayerMatches(Transaction tr, ObjectId id, string layer)
        {
            try
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity;
                return ent != null && ent.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
