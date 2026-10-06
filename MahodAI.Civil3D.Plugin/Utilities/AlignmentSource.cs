using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities.CurveFitting;
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace MahodAI.Civil3D.Plugin.Utilities
{
    /// <summary>
    /// Resolves a target (alignment by name, polyline by handle, or a chain of AutoCAD
    /// Line/Arc entities — selected by the user or grouped on a layer) and samples its
    /// centerline as an ordered list of 2D points at roughly uniform arc-length spacing.
    /// The fitting pipeline does not care which source the points came from — it just
    /// needs an ordered <see cref="Pt2D"/> array. For the line-chain mode, the per-entity
    /// structure (Line / Arc with its native radius) is also exposed via
    /// <see cref="Result.Segments"/> so callers can reproduce engineer-drawn geometry 1:1
    /// without re-fitting.
    /// </summary>
    public static class AlignmentSource
    {
        public enum SegmentKind { Line, Arc }

        /// <summary>
        /// One segment of the chained line-chain source: a Line with two endpoints, or an
        /// Arc with its center, radius and signed sweep. Walk-direction-oriented — when
        /// the chaining algorithm reverses a segment, <see cref="Start"/>/<see cref="End"/>
        /// are swapped, <see cref="Samples"/> is reversed and <see cref="ArcSweepRad"/>
        /// is negated, so callers can iterate <see cref="Segments"/> in order without
        /// further bookkeeping.
        /// </summary>
        public sealed class ChainedSegment
        {
            public ObjectId Id;
            public SegmentKind Kind;
            public Pt2D Start;
            public Pt2D End;
            public Pt2D[] Samples = Array.Empty<Pt2D>();   // ordered, includes Start and End
            // Arc-only (defaults are zero/origin for Lines):
            public Pt2D ArcCenter;
            public double ArcRadius;
            public double ArcSweepRad;                     // signed: +CCW, -CW, in walk direction
        }

        public sealed class Result
        {
            public Pt2D[] Points { get; init; } = Array.Empty<Pt2D>();
            public ObjectId AlignmentId { get; init; }
            public ObjectId PolylineId { get; init; }
            public IReadOnlyList<ObjectId> LineIds { get; init; } = Array.Empty<ObjectId>();
            public string SourceKind { get; init; } = "";                // "alignment" | "polyline" | "lines"
            public string ResolvedName { get; init; } = "";
            // Populated only on the "lines" source path. Empty otherwise.
            public IReadOnlyList<ChainedSegment> Segments { get; init; } = Array.Empty<ChainedSegment>();
            public int LineCount { get; init; }
            public int ArcCount { get; init; }
        }

        // ─── Named-target resolution: alignment by name, polyline by handle ────────
        public static Result? Sample(
            Transaction tr,
            CivilDoc civilDoc,
            string targetName,
            double samplingStepM)
        {
            if (samplingStepM <= 0) samplingStepM = 1.0;

            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, targetName);
            if (alignment != null)
            {
                return new Result
                {
                    Points = SampleAlignment(alignment, samplingStepM),
                    AlignmentId = alignment.ObjectId,
                    SourceKind = "alignment",
                    ResolvedName = alignment.Name,
                };
            }

            // Polyline by handle.
            var db = HostApplicationServices.WorkingDatabase;
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return null;
            var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return null;

            foreach (ObjectId entId in btr)
            {
                var ent = tr.GetObject(entId, OpenMode.ForRead) as AcadEntity;
                if (ent == null) continue;
                if (ent is not Polyline && ent is not Polyline2d && ent is not Polyline3d) continue;
                if (!string.Equals(ent.Handle.ToString(), targetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new Result
                {
                    Points = SamplePolyline(tr, ent, samplingStepM),
                    PolylineId = entId,
                    SourceKind = "polyline",
                    ResolvedName = ent.Handle.ToString(),
                };
            }
            return null;
        }

        // ─── Line-chain resolution: from a list of selected entity ids ─────────────
        public static Result? SampleFromEntityIds(
            Transaction tr,
            IReadOnlyList<ObjectId> entityIds,
            double samplingStepM)
        {
            if (entityIds == null || entityIds.Count == 0) return null;

            // If exactly one entity and it's an Alignment / Polyline, dispatch to those paths.
            if (entityIds.Count == 1)
            {
                var ent = tr.GetObject(entityIds[0], OpenMode.ForRead);
                if (ent is CivilAlignment al)
                {
                    return new Result
                    {
                        Points = SampleAlignment(al, samplingStepM),
                        AlignmentId = al.ObjectId,
                        SourceKind = "alignment",
                        ResolvedName = al.Name,
                    };
                }
                if (ent is Polyline or Polyline2d or Polyline3d)
                {
                    var acent = (AcadEntity)ent;
                    return new Result
                    {
                        Points = SamplePolyline(tr, acent, samplingStepM),
                        PolylineId = entityIds[0],
                        SourceKind = "polyline",
                        ResolvedName = acent.Handle.ToString(),
                    };
                }
            }

            // Otherwise: collect Line + Arc entities (the products of an exploded alignment,
            // or engineer-drawn geometry built from straights and circular arcs) and chain
            // them end-to-end. Per-entity Arc.Radius / center / sweep are preserved on each
            // ChainedSegment so the caller can rebuild the alignment 1:1 without re-fitting.
            var segments = new List<ChainedSegment>();
            var sourceIds = new List<ObjectId>();
            int lineCount = 0;
            int arcCount = 0;
            foreach (var id in entityIds)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as AcadEntity;
                if (ent is Line line)
                {
                    var s = new Pt2D(line.StartPoint.X, line.StartPoint.Y);
                    var e = new Pt2D(line.EndPoint.X, line.EndPoint.Y);
                    segments.Add(new ChainedSegment
                    {
                        Id = id,
                        Kind = SegmentKind.Line,
                        Start = s,
                        End = e,
                        Samples = new[] { s, e },
                    });
                    sourceIds.Add(id);
                    lineCount++;
                }
                else if (ent is Arc arc)
                {
                    var samples = SampleArcEntity(arc, samplingStepM);
                    if (samples.Length >= 2)
                    {
                        // AutoCAD Arc is always traversed CCW from StartAngle to EndAngle.
                        // Sweep ∈ (0, 2π].
                        double sweep = arc.EndAngle - arc.StartAngle;
                        while (sweep <= 0) sweep += 2 * Math.PI;
                        segments.Add(new ChainedSegment
                        {
                            Id = id,
                            Kind = SegmentKind.Arc,
                            Start = samples[0],
                            End = samples[samples.Length - 1],
                            Samples = samples,
                            ArcCenter = new Pt2D(arc.Center.X, arc.Center.Y),
                            ArcRadius = arc.Radius,
                            ArcSweepRad = sweep,        // positive (CCW) in entity-natural direction
                        });
                        sourceIds.Add(id);
                        arcCount++;
                    }
                }
            }
            if (segments.Count == 0) return null;

            var ordered = ChainSegments(segments, tolerance: 0.01);
            if (ordered.Count == 0) return null;

            // Flatten ordered segments into a single point list, deduplicating at junctions.
            var pts = new List<Pt2D>();
            foreach (var seg in ordered)
            {
                int startSkip = pts.Count > 0 ? 1 : 0;     // skip duplicate junction point
                for (int k = startSkip; k < seg.Samples.Length; k++) pts.Add(seg.Samples[k]);
            }

            return new Result
            {
                Points = pts.ToArray(),
                LineIds = sourceIds,
                SourceKind = "lines",
                ResolvedName = $"{sourceIds.Count} entities",
                Segments = ordered,
                LineCount = lineCount,
                ArcCount = arcCount,
            };
        }

        // ─── Layer-based resolution: all Line entities on a given layer ────────────
        public static Result? SampleFromLayer(
            Transaction tr,
            string layerName,
            double samplingStepM)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return null;

            var db = HostApplicationServices.WorkingDatabase;
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return null;
            var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return null;

            var ids = new List<ObjectId>();
            foreach (ObjectId entId in btr)
            {
                var ent = tr.GetObject(entId, OpenMode.ForRead) as AcadEntity;
                if (ent == null) continue;
                if ((ent is Line || ent is Arc)
                    && string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                    ids.Add(entId);
            }
            if (ids.Count == 0) return null;

            var result = SampleFromEntityIds(tr, ids, samplingStepM);
            if (result != null && result.SourceKind == "lines")
            {
                return new Result
                {
                    Points = result.Points,
                    LineIds = result.LineIds,
                    SourceKind = "lines",
                    ResolvedName = $"layer '{layerName}' ({ids.Count} lines)",
                    Segments = result.Segments,
                    LineCount = result.LineCount,
                    ArcCount = result.ArcCount,
                };
            }
            return result;
        }

        // ─── Internals ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Densifies an AutoCAD Arc entity into ordered sample points along its perimeter,
        /// roughly at <paramref name="stepM"/> arc-length intervals. Samples include both
        /// endpoints and follow the arc's natural direction (StartPoint → EndPoint).
        /// </summary>
        private static Pt2D[] SampleArcEntity(Arc arc, double stepM)
        {
            double len = arc.Length;
            if (len <= 0)
            {
                return new[]
                {
                    new Pt2D(arc.StartPoint.X, arc.StartPoint.Y),
                    new Pt2D(arc.EndPoint.X, arc.EndPoint.Y),
                };
            }
            int n = Math.Max(2, (int)Math.Ceiling(len / Math.Max(0.1, stepM)) + 1);
            var pts = new Pt2D[n];
            for (int i = 0; i < n; i++)
            {
                double d = (n == 1) ? 0 : (double)i / (n - 1) * len;
                try
                {
                    var p = arc.GetPointAtDist(d);
                    pts[i] = new Pt2D(p.X, p.Y);
                }
                catch
                {
                    pts[i] = i == 0
                        ? new Pt2D(arc.StartPoint.X, arc.StartPoint.Y)
                        : new Pt2D(arc.EndPoint.X, arc.EndPoint.Y);
                }
            }
            return pts;
        }

        /// <summary>
        /// Orders a set of segments (Lines or densified Arcs) into a single end-to-end
        /// chain. Each segment is emitted in the direction it should be traversed —
        /// reversing its sample list when the chain walks it backwards. For Arc segments
        /// the signed <see cref="ChainedSegment.ArcSweepRad"/> is negated on reversal so
        /// it always reflects the walk direction (positive = CCW, negative = CW).
        /// Algorithm: build a node→edges adjacency on rounded endpoint coordinates, find
        /// a degree-1 node as the start (or fall back to any vertex for closed loops),
        /// and greedily walk preferring the neighbour that continues most-collinearly at
        /// each fork.
        /// </summary>
        internal static List<ChainedSegment> ChainSegments(IReadOnlyList<ChainedSegment> segs, double tolerance)
        {
            int n = segs.Count;
            if (n == 0) return new List<ChainedSegment>();

            long Key(Pt2D p) =>
                ((long)Math.Round(p.X / tolerance) * 73856093L)
                ^ ((long)Math.Round(p.Y / tolerance) * 19349663L);

            var adj = new Dictionary<long, List<(int idx, bool atStart)>>();
            void Add(long k, int idx, bool atStart)
            {
                if (!adj.TryGetValue(k, out var list)) { list = new(); adj[k] = list; }
                list.Add((idx, atStart));
            }
            for (int i = 0; i < n; i++)
            {
                Add(Key(segs[i].Start), i, true);
                Add(Key(segs[i].End), i, false);
            }

            int startIdx = 0;
            bool startFromStart = true;
            bool foundLeaf = false;
            foreach (var kvp in adj)
            {
                if (kvp.Value.Count == 1)
                {
                    startIdx = kvp.Value[0].idx;
                    startFromStart = kvp.Value[0].atStart;
                    foundLeaf = true;
                    break;
                }
            }
            if (!foundLeaf) { startIdx = 0; startFromStart = true; }

            var ordered = new List<ChainedSegment>(n);
            var visited = new bool[n];
            int cur = startIdx;
            bool fromStart = startFromStart;

            while (cur >= 0 && !visited[cur])
            {
                visited[cur] = true;
                var seg = segs[cur];
                if (fromStart)
                {
                    ordered.Add(seg);
                }
                else
                {
                    var reversed = new Pt2D[seg.Samples.Length];
                    for (int i = 0; i < seg.Samples.Length; i++)
                        reversed[i] = seg.Samples[seg.Samples.Length - 1 - i];
                    ordered.Add(new ChainedSegment
                    {
                        Id = seg.Id,
                        Kind = seg.Kind,
                        Start = seg.End,
                        End = seg.Start,
                        Samples = reversed,
                        ArcCenter = seg.ArcCenter,
                        ArcRadius = seg.ArcRadius,
                        ArcSweepRad = -seg.ArcSweepRad,    // walk-direction sweep flips on reverse
                    });
                }

                Pt2D nextPoint = fromStart ? seg.End : seg.Start;
                if (!adj.TryGetValue(Key(nextPoint), out var neighbours)) break;

                int nextIdx = -1;
                bool nextFromStart = true;
                Pt2D prevDir = new(
                    (fromStart ? seg.End.X - seg.Start.X : seg.Start.X - seg.End.X),
                    (fromStart ? seg.End.Y - seg.Start.Y : seg.Start.Y - seg.End.Y));
                double bestScore = double.NegativeInfinity;
                foreach (var (idx, atStart) in neighbours)
                {
                    if (idx == cur || visited[idx]) continue;
                    var cand = segs[idx];
                    Pt2D candDir = atStart
                        ? new(cand.End.X - cand.Start.X, cand.End.Y - cand.Start.Y)
                        : new(cand.Start.X - cand.End.X, cand.Start.Y - cand.End.Y);
                    double dot = prevDir.X * candDir.X + prevDir.Y * candDir.Y;
                    if (dot > bestScore) { bestScore = dot; nextIdx = idx; nextFromStart = atStart; }
                }
                cur = nextIdx;
                fromStart = nextFromStart;
            }
            return ordered;
        }

        private static Pt2D[] SampleAlignment(CivilAlignment alignment, double stepM)
        {
            double total = alignment.Length;
            if (total <= 0) return Array.Empty<Pt2D>();
            int n = Math.Max(2, (int)Math.Ceiling(total / stepM) + 1);

            var pts = new Pt2D[n];
            double startSta = alignment.StartingStation;
            for (int i = 0; i < n; i++)
            {
                double t = (n == 1) ? 0 : (double)i / (n - 1);
                double sta = startSta + t * total;
                double x = 0, y = 0;
                try { alignment.PointLocation(sta, 0, ref x, ref y); }
                catch { /* shouldn't happen for a valid alignment */ }
                pts[i] = new Pt2D(x, y);
            }
            return pts;
        }

        private static Pt2D[] SamplePolyline(Transaction tr, AcadEntity ent, double stepM)
        {
            var raw = new List<Pt2D>();
            switch (ent)
            {
                case Polyline pl:
                    {
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            var v = pl.GetPoint2dAt(i);
                            raw.Add(new Pt2D(v.X, v.Y));
                            if (i < pl.NumberOfVertices - 1
                                && pl.GetSegmentType(i) == SegmentType.Arc)
                            {
                                var arc = pl.GetArcSegment2dAt(i);
                                double p0 = arc.GetParameterOf(arc.StartPoint);
                                double p1 = arc.GetParameterOf(arc.EndPoint);
                                double len = arc.GetLength(p0, p1);
                                int k = Math.Max(1, (int)(len / stepM));
                                for (int s = 1; s < k; s++)
                                {
                                    double pp = p0 + (p1 - p0) * s / k;
                                    var pt = arc.EvaluatePoint(pp);
                                    raw.Add(new Pt2D(pt.X, pt.Y));
                                }
                            }
                        }
                        if (pl.Closed && pl.NumberOfVertices > 0) raw.Add(raw[0]);
                        break;
                    }
                case Polyline2d p2:
                    foreach (ObjectId vId in p2)
                    {
                        var v = tr.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                        if (v != null) raw.Add(new Pt2D(v.Position.X, v.Position.Y));
                    }
                    break;
                case Polyline3d p3:
                    foreach (ObjectId vId in p3)
                    {
                        var v = tr.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null) raw.Add(new Pt2D(v.Position.X, v.Position.Y));
                    }
                    break;
            }
            return raw.ToArray();
        }
    }
}
