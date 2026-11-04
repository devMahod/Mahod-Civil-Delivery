using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Shared;
using Crossing = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Crossing;
using DraftedSection = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.DraftedSection;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;
using PlacedObject = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.PlacedObject;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Typical sections on a DRAFTED drawing (2026-11-02, MAAKAF DD-500X): the sheet carries the design only as XREF
    /// linework, so there is no corridor for the firm's section program to cut. Here the axes are the plan's AX-* / CL
    /// polylines, the section is the plan cut along the marked line (host and every XREF, nested ones too) and the
    /// block is drawn from what the cut found (<see cref="DraftedTypicalSectionLogic"/>): road at grade, curbs,
    /// sidewalks and gardens raised, the width chain and the strip names below the ground line, fences, barriers, trees,
    /// poles and signs above it, work zones as a frame, the axis in red, a title. One block per pick, 1:1 in metres
    /// like the firm's MSV_* blocks, so it plots at 1:200 beside them.
    /// </summary>
    internal static class DraftedTypicalSectionService
    {
        public const string BlockPrefix = "MTS_";
        public const string SectionLayer = "MHD-TYPICAL-SECTIONS";

        private const string RoadLayer = "MHD-TYP-ROAD";
        private const string AsphaltLayer = "MHD-TYP-ASPH";
        private const string GreenLayer = "MHD-TYP-GREEN";
        private const string AxisLayer = "MHD-TYP-AXIS";
        private const string ChainLayer = "MHD-TYP-DIM";
        private const string ObjectLayer = "MHD-TYP-OBJ";
        private const string WorkLayer = "MHD-TYP-WORK";

        /// <summary>
        /// A block on the plan this close to the section line is drawn in it (a tree, a pole, a sign). A typical section
        /// stands for the stretch around it, as the office draws one tree where a row of them lines the road; 2 m found
        /// none of MAAKAF's 669 surveyed trees at the engineer's four marks, while 8M has two 5–6.5 m along the road.
        /// </summary>
        public const double ObjectReachM = 8.0;

        /// <summary>Axis polylines shorter than this are labels or arrows on an axis layer, not a road.</summary>
        public const double MinAxisLengthM = 30.0;

        private const int MaxDepth = 4;
        private const double TextHeight = 0.4;
        private const double RaisedM = 0.15;

        // ------------------------------------------------------------------------------------------------ walking

        /// <summary>Every entity of model space and of the blocks and XREFs it inserts, with its world transform and
        /// the layer it shows on (an entity on layer 0 inside a block takes the insert's layer, as AutoCAD draws it).</summary>
        private static void Walk(Transaction tr, BlockTableRecord owner, Matrix3d toWcs, string? inheritedLayer, int depth,
            Action<Entity, Matrix3d, string> visit)
        {
            foreach (ObjectId id in owner)
            {
                Entity? entity;
                try { entity = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                if (entity == null) continue;
                var layer = inheritedLayer != null && entity.Layer == "0" ? inheritedLayer : entity.Layer;
                visit(entity, toWcs, layer);
                if (entity is not BlockReference reference || depth >= MaxDepth) continue;
                BlockTableRecord inner;
                try { inner = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead); }
                catch { continue; }
                if (inner.IsLayout || (inner.IsFromExternalReference && !inner.IsResolved)) continue;
                if (inner.Name.StartsWith(BlockPrefix, StringComparison.OrdinalIgnoreCase) ||
                    inner.Name.StartsWith(FirmSectionAdapter.SectionBlockNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                Walk(tr, inner, toWcs * reference.BlockTransform, layer, depth + 1, visit);
            }
        }

        private static BlockTableRecord ModelSpace(Transaction tr, Database db) =>
            (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        // --------------------------------------------------------------------------------------------------- axes

        /// <summary>
        /// The plan's road axes (<see cref="PlanAxis.IsAxisLayer"/>) in world coordinates, arcs sampled every metre.
        /// Two polylines on one layer are told apart by a number (AX-40-st-01-1, AX-40-st-01-1 #2).
        /// </summary>
        public static List<PlanAxis> CollectPlanAxes(Transaction tr, Database db, IReadOnlyList<string> allowed)
        {
            var found = new List<(string Name, List<P2> Vertices)>();
            Walk(tr, ModelSpace(tr, db), Matrix3d.Identity, null, 0, (entity, toWcs, layer) =>
            {
                if (entity is not Curve curve || entity is BlockReference) return;
                if (!PlanAxis.IsAxisLayer(layer)) return;
                var vertices = Sample(curve, toWcs);
                if (vertices.Count < 2) return;
                var length = 0.0;
                for (var i = 1; i < vertices.Count; i++)
                    length += Math.Sqrt(Math.Pow(vertices[i].X - vertices[i - 1].X, 2) + Math.Pow(vertices[i].Y - vertices[i - 1].Y, 2));
                if (length < MinAxisLengthM) return;
                found.Add((PlanAxis.LayerLeaf(layer), vertices));
            });

            // The office's real axes are AX-* (the CL XREF); a GM/SM 'cl' or CENTERLINE line is a short lane arrow or a
            // label leader. When any AX-* axis exists, only those count (MAAKAF: 27 'cl' pieces of 60-71 m beside AX-40).
            if (found.Any(f => f.Name.StartsWith("AX", StringComparison.OrdinalIgnoreCase)))
                found = found.Where(f => f.Name.StartsWith("AX", StringComparison.OrdinalIgnoreCase)).ToList();
            var axes = new List<PlanAxis>();
            // No drawn axis at all: the alignments may have been exploded onto their layer (2026-11-04, a TR plan).
            if (found.Count == 0)
            {
                foreach (var axis in CollectExplodedAlignments(tr, db))
                {
                    if (allowed.Count > 0 && !allowed.Any(pattern => ClInstructionReader.WildcardMatch(axis.Name, pattern))) continue;
                    try { axes.Add(new PlanAxis(axis.Name, axis.Vertices, axis.StartStation)); }
                    catch (ArgumentException) { /* degenerate */ }
                }
                return axes;
            }
            foreach (var group in found.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var index = 0;
                foreach (var (name, vertices) in group)
                {
                    var unique = ++index == 1 ? name : FormattableString.Invariant($"{name} #{index}");
                    if (allowed.Count > 0 && !allowed.Any(pattern => ClInstructionReader.WildcardMatch(unique, pattern))) continue;
                    try { axes.Add(new PlanAxis(unique, vertices)); }
                    catch (ArgumentException) { /* a degenerate polyline is no axis */ }
                }
            }
            return axes;
        }

        /// <summary>
        /// Exploded Civil alignments (<see cref="ExplodedAlignmentLogic"/>): the axis pieces (polylines and arcs on an
        /// alignment layer; its Lines are the curves' tangent construction lines), the station ticks (AeccTickLine
        /// blocks) paired with their printed values, and the curve labels with the PI their leader points at.
        /// </summary>
        public static List<ExplodedAlignmentLogic.Axis> CollectExplodedAlignments(Transaction tr, Database db)
        {
            var pieces = new List<IReadOnlyList<P2>>();
            var lines = new List<(P2 A, P2 B)>();
            var ticks = new List<(P2 At, string Layer)>();
            var tickTexts = new List<(P2 At, double Value, string Layer)>();
            var tickLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labelTexts = new List<(P2 At, string Name, double Station)>();
            var leaders = new List<(P2 Tip, P2 Tail)>();
            string? layerName = null;
            Walk(tr, ModelSpace(tr, db), Matrix3d.Identity, null, 0, (entity, toWcs, layer) =>
            {
                switch (entity)
                {
                    case Leader leader when leader.NumVertices >= 2:
                        var tip = leader.VertexAt(0).TransformBy(toWcs);
                        var tail = leader.VertexAt(leader.NumVertices - 1).TransformBy(toWcs);
                        leaders.Add((new P2(tip.X, tip.Y), new P2(tail.X, tail.Y)));
                        return;
                    case BlockReference reference:
                        string name;
                        try { name = ((BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead)).Name; }
                        catch { return; }
                        if (name.StartsWith("AeccTickLine", StringComparison.OrdinalIgnoreCase))
                        {
                            var p = reference.Position.TransformBy(toWcs);
                            ticks.Add((new P2(p.X, p.Y), layer));
                            tickLayers.Add(layer);
                        }
                        return;
                    case DBText text:
                        AddText(text.TextString, text.Position.TransformBy(toWcs), layer);
                        return;
                    case MText mtext:
                        AddText(mtext.Text, mtext.Location.TransformBy(toWcs), layer);
                        return;
                    case Line line when ExplodedAlignmentLogic.IsAlignmentLayer(layer):
                        var a = line.StartPoint.TransformBy(toWcs);
                        var b = line.EndPoint.TransformBy(toWcs);
                        lines.Add((new P2(a.X, a.Y), new P2(b.X, b.Y)));
                        return;
                    case Curve curve when ExplodedAlignmentLogic.IsAlignmentLayer(layer):
                        var vertices = Sample(curve, toWcs);
                        if (vertices.Count >= 2) pieces.Add(vertices);
                        layerName ??= PlanAxis.LayerLeaf(layer);
                        return;
                }
            });
            if (pieces.Count == 0) return new List<ExplodedAlignmentLogic.Axis>();

            void AddText(string? text, Point3d at, string layer)
            {
                var p = new P2(at.X, at.Y);
                if (ExplodedAlignmentLogic.TickValue(text) is { } value) tickTexts.Add((p, value, layer));
                else if (ExplodedAlignmentLogic.ParseLabel(text) is { } label) labelTexts.Add((p, label.Name, label.Station));
            }
            static double Dist(P2 a, P2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

            // A tick and its value are each other's nearest (two roads side by side print their values close together).
            // A tick's value is the text on its own layer (a "3.50" width beside a tick is not a station; TR-M34's section
            // numbers stand on their own ticks on HW-ALGN-SEC-NAME-C, the stations on HW-ALGN-SEC-NAME), and the two are
            // each other's nearest (two roads side by side print their values close together).
            tickTexts.RemoveAll(t => !tickLayers.Contains(t.Layer));
            var pairedTicks = new List<ExplodedAlignmentLogic.Tick>();
            foreach (var group in ticks.GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase))
            {
                var layerTicks = group.Select(t => t.At).ToList();
                var layerTexts = tickTexts.Where(t => string.Equals(t.Layer, group.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (layerTexts.Count == 0) continue;
                foreach (var tick in layerTicks)
                {
                    var text = layerTexts.OrderBy(t => Dist(t.At, tick)).First();
                    if (Dist(text.At, tick) > 30) continue;
                    var nearestTick = layerTicks.OrderBy(t => Dist(t, text.At)).First();
                    if (Dist(nearestTick, tick) > 1e-6) continue;
                    pairedTicks.Add(new ExplodedAlignmentLogic.Tick(tick, text.Value));
                }
            }
            // A label's PI is the tip of the leader whose tail is nearest its text; the tangent lines from the PI touch the axis.
            var labels = new List<ExplodedAlignmentLogic.Label>();
            foreach (var (at, name, station) in labelTexts)
            {
                if (leaders.Count == 0) break;
                var leader = leaders.OrderBy(l => Dist(l.Tail, at)).First();
                if (Dist(leader.Tail, at) > 30) continue;
                var touches = lines
                    .Select(l => Dist(l.A, leader.Tip) < 0.05 ? l.B : Dist(l.B, leader.Tip) < 0.05 ? l.A : (P2?)null)
                    .Where(p => p != null).Select(p => p!.Value).ToList();
                labels.Add(new ExplodedAlignmentLogic.Label(name, station, leader.Tip, touches));
            }
            return ExplodedAlignmentLogic.Build(pieces, pairedTicks, labels, layerName ?? "ALGN", MinAxisLengthM);
        }

        /// <summary>How far along the road a dash may stand from the cut and still be its line (half the longest gap).</summary>
        public const double DashReachM = 6.0;

        /// <summary>
        /// The offset of a painted dash that stands beside the cut: short (at most <see cref="DashMaxM"/>), running with
        /// the road, its nearest end within <see cref="DashReachM"/> along the road and within the section's width.
        /// Null for anything else - a line that merely ends near the cut is not on it.
        /// </summary>
        private static double? DashOffset(Curve curve, Matrix3d toWcs, Point3d axis, Vector3d chordDir, Vector3d right, double chordLength)
        {
            double length;
            try { length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam); }
            catch { return null; }
            if (!(length > 0) || length > 2 * DashMaxM + 1) return null;
            // The road runs across the chord: its direction is the chord's normal.
            var road = new Vector3d(-chordDir.Y, chordDir.X, 0);
            var points = new List<Point3d>();
            for (var i = 0; i <= 8; i++)
            {
                try { points.Add(curve.GetPointAtDist(length * i / 8).TransformBy(toWcs)); }
                catch { return null; }
            }
            double Along(Point3d p) => (new Point3d(p.X, p.Y, 0) - axis).DotProduct(road);
            double Across(Point3d p) => (new Point3d(p.X, p.Y, 0) - axis).DotProduct(right);
            var spanAlong = points.Max(Along) - points.Min(Along);
            var spanAcross = points.Max(Across) - points.Min(Across);
            if (spanAlong > DashMaxM || spanAlong < 2 * spanAcross) return null;
            var nearest = points.OrderBy(p => Math.Abs(Along(p))).First();
            if (Math.Abs(Along(nearest)) > DashReachM) return null;
            var offset = Across(nearest);
            return Math.Abs(offset) <= chordLength / 2 ? offset : null;
        }

        /// <summary>The longest painted dash read as a stroke of a broken line.</summary>
        public const double DashMaxM = 6.5;

        private static List<P2> Sample(Curve curve, Matrix3d toWcs)
        {
            var points = new List<P2>();
            try
            {
                var start = curve.GetDistanceAtParameter(curve.StartParam);
                var end = curve.GetDistanceAtParameter(curve.EndParam);
                var length = end - start;
                if (!(length > 0)) return points;
                var steps = (int)Math.Min(20000, Math.Max(1, Math.Ceiling(length)));
                for (var i = 0; i <= steps; i++)
                {
                    var p = curve.GetPointAtDist(Math.Min(length, start + length * i / steps)).TransformBy(toWcs);
                    points.Add(new P2(p.X, p.Y));
                }
            }
            catch { points.Clear(); }
            return points;
        }

        // ---------------------------------------------------------------------------------------------------- cut

        /// <summary>
        /// The plan along one pick's chord: every classified line it crosses (signed offset from the axis, right
        /// positive) and every tree, pole or sign standing within <see cref="ObjectReachM"/> of it.
        /// </summary>
        /// <param name="corridorEdges">Read "&lt;corridor&gt;+&lt;piece&gt;+W" lines as pavement edges: on drafted drawings,
        /// where no corridor is in the drawing to read them from (<see cref="DraftedTypicalSectionLogic.IsCorridorEdgeLayer"/>).</param>
        public static (List<Crossing> Crossings, List<PlacedObject> Objects) Cut(Transaction tr, Database db, TypicalSectionPick pick,
            bool corridorEdges = false)
        {
            var a = new Point3d(pick.AX, pick.AY, 0);
            var b = new Point3d(pick.BX, pick.BY, 0);
            var t = pick.TangentDeg * Math.PI / 180.0;
            var right = new Vector3d(Math.Sin(t), -Math.Cos(t), 0);
            var axis = new Point3d(pick.AxisX, pick.AxisY, 0);
            double OffsetOf(Point3d p) => (new Point3d(p.X, p.Y, 0) - axis).DotProduct(right);
            var along = (b - a).GetNormal();
            var chordLength = a.DistanceTo(b);

            var crossings = new List<Crossing>();
            var objects = new List<PlacedObject>();
            var chordCache = new Dictionary<Matrix3d, Line>();
            // ~40 patterns per entity over a large XREF set (6403: 15,944 host polylines + survey XREFs) timed out the
            // code runner; a layer is classified once.
            var layerKinds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var objectKinds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Walk(tr, ModelSpace(tr, db), Matrix3d.Identity, null, 0, (entity, toWcs, layer) =>
                {
                    if (entity is BlockReference reference)
                    {
                        string name;
                        try { name = ((BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead)).Name; }
                        catch { return; }
                        var objectKey = layer + "/" + name;
                        if (!objectKinds.TryGetValue(objectKey, out var objectKind))
                            objectKinds[objectKey] = objectKind = DraftedTypicalSectionLogic.ClassifyObject(layer, name);
                        if (objectKind == null) return;
                        var p = reference.Position.TransformBy(toWcs);
                        var flat = new Point3d(p.X, p.Y, 0);
                        var s = (flat - a).DotProduct(along);
                        if (s < 0 || s > chordLength) return;
                        var distance = Math.Abs((flat - a).DotProduct(new Vector3d(-along.Y, along.X, 0)));
                        if (distance > ObjectReachM) return;
                        objects.Add(new PlacedObject(OffsetOf(flat), objectKind, PlanAxis.LayerLeaf(layer), PlanAxis.LayerLeaf(name)));
                        return;
                    }
                    if (entity is not Curve curve) return;
                    if (!layerKinds.TryGetValue(layer, out var kind))
                        layerKinds[layer] = kind = DraftedTypicalSectionLogic.ClassifyLayer(layer) ??
                            (corridorEdges && DraftedTypicalSectionLogic.IsCorridorEdgeLayer(layer) ? DraftedTypicalSectionLogic.Kind.Edge : null);
                    if (kind == null) return;
                    if (!chordCache.TryGetValue(toWcs, out var chord))
                    {
                        var toLocal = toWcs.Inverse();
                        chordCache[toWcs] = chord = new Line(a.TransformBy(toLocal), b.TransformBy(toLocal));
                    }
                    var hits = new Point3dCollection();
                    try { chord.IntersectWith(curve, Intersect.OnBothOperands, hits, IntPtr.Zero, IntPtr.Zero); }
                    catch { return; }
                    var source = layer.Contains('|') ? layer[..layer.IndexOf('|')] : "host";
                    // A broken lane line drawn dash by dash (2026-11-04, TR-M34: 801 3-3 as separate strokes): a cut
                    // between two dashes crosses none, so the nearest dash along the road stands for the line.
                    if (hits.Count == 0 && kind is DraftedTypicalSectionLogic.Kind.Marking or DraftedTypicalSectionLogic.Kind.EdgeLine or DraftedTypicalSectionLogic.Kind.HovLine &&
                        DashOffset(curve, toWcs, axis, along, right, chordLength) is { } dash)
                    {
                        crossings.Add(new Crossing(dash, kind, PlanAxis.LayerLeaf(layer), source));
                        return;
                    }
                    foreach (Point3d hit in hits)
                        crossings.Add(new Crossing(OffsetOf(hit.TransformBy(toWcs)), kind, PlanAxis.LayerLeaf(layer), source));
                });
            }
            finally
            {
                foreach (var chord in chordCache.Values) chord.Dispose();
            }

            // A survey draws a tree twice (symbol and its "-ut" copy): one object per kind and spot.
            var merged = new List<PlacedObject>();
            foreach (var o in objects.OrderBy(o => o.OffsetM))
                if (!merged.Any(m => m.Kind == o.Kind && Math.Abs(m.OffsetM - o.OffsetM) < 0.3)) merged.Add(o);
            return (crossings, merged);
        }

        // --------------------------------------------------------------------------------------------------- draw

        private const string GroundLayer = "MHD-TYP-EG";
        private const string PavementLayer = "MHD-TYP-PAVE";
        private const string VehicleLayer = "MHD-TYP-VEH";
        private const string RowLayer = "MHD-TYP-ROW";

        /// <summary>Every layer a plan-drawn section draws on: the A0 sheet keeps them visible in its viewport.</summary>
        internal static readonly string[] OwnLayers =
        {
            RoadLayer, AsphaltLayer, GreenLayer, AxisLayer, ChainLayer, ObjectLayer, WorkLayer, GroundLayer, PavementLayer,
            VehicleLayer, RowLayer, TypicalSectionContracts.NamesLayer,
        };

        /// <summary>Height of the width chain above the highest point of the road (the office's sheet: ~6.5 m at 1:100).</summary>
        private const double ChainAboveM = 6.5;

        /// <summary>Gap between the firm's section and the typical section drawn above it.</summary>
        private const double OverlayGapM = 3.0;

        /// <summary>Gap between two stacked panels.</summary>
        private const double PanelGapM = 4.0;

        private static bool IsLane(string? name) => name is "נתיב" or "נתיב זמני";

        /// <summary>
        /// Draws the typical section the way the office draws one (2026-11-02, their sheet MHD-HW-000-IC_00000-DD-5001-02):
        /// the road - from the corridor when <paramref name="design"/> is given (finished surface, pavement layers hatched,
        /// the existing ground dashed), else schematic from the plan - and over it the width chain with a line down to the
        /// road at every break, the strip names under the chain, the median as one dimension over its own small chain, a
        /// direction arrow and a car in every lane the plan proves, the cross slope of each carriageway and the ratio of each
        /// side slope, the centreline, the right-of-way lines, poles, trees, fences and barriers, the pavement layers called
        /// out under the road, and the title. One panel per stage, stacked downward; one block per pick, 1:1 in metres.
        /// The axis at <paramref name="originX"/>; the first panel's road at the axis on <paramref name="originY"/> - or,
        /// with <paramref name="overlay"/>, the whole stack's lowest point <see cref="OverlayGapM"/> above it (a reference
        /// created in this transaction reports no extents to place it by afterwards).
        /// </summary>
        public static ObjectId Draw(Transaction tr, Database db, IReadOnlyList<DraftedTypicalSectionLogic.Panel> panels,
            TypicalSectionPick pick, double originX, double originY, TypicalSheetGeometryLogic.SectionGeometry? design = null,
            bool overlay = false, int designOrder = 1)
        {
            var styleId = FirmSectionBlockReader.EnsureTrueTypeStyle(tr, db);
            var layers = new Dictionary<string, ObjectId>
            {
                [RoadLayer] = TypicalSectionPicker.EnsureLayer(tr, db, RoadLayer, 7),
                [AsphaltLayer] = TypicalSectionPicker.EnsureLayer(tr, db, AsphaltLayer, 8),
                [GreenLayer] = TypicalSectionPicker.EnsureLayer(tr, db, GreenLayer, 3),
                [AxisLayer] = TypicalSectionPicker.EnsureLayer(tr, db, AxisLayer, 1),
                [ChainLayer] = TypicalSectionPicker.EnsureLayer(tr, db, ChainLayer, 7),
                [ObjectLayer] = TypicalSectionPicker.EnsureLayer(tr, db, ObjectLayer, 8),
                [WorkLayer] = TypicalSectionPicker.EnsureLayer(tr, db, WorkLayer, 30),
                [GroundLayer] = TypicalSectionPicker.EnsureLayer(tr, db, GroundLayer, 32),
                [PavementLayer] = TypicalSectionPicker.EnsureLayer(tr, db, PavementLayer, 8),
                [VehicleLayer] = TypicalSectionPicker.EnsureLayer(tr, db, VehicleLayer, 8),
                [RowLayer] = TypicalSectionPicker.EnsureLayer(tr, db, RowLayer, 1),
                [TypicalSectionContracts.NamesLayer] = TypicalSectionPicker.EnsureLayer(tr, db, TypicalSectionContracts.NamesLayer, 7),
            };
            var dashed = EnsureLinetype(tr, db, "DASHED");
            var center = EnsureLinetype(tr, db, "CENTER");

            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            var definition = new BlockTableRecord { Name = UniqueName(table, pick), Origin = Point3d.Origin };
            var definitionId = table.Add(definition);
            tr.AddNewlyCreatedDBObject(definition, true);

            var dy = 0.0; // the current panel's road-at-the-axis level inside the block
            T Add<T>(T e, string layer) where T : Entity
            {
                e.LayerId = layers[layer];
                e.ColorIndex = 256;
                definition.AppendEntity(e);
                tr.AddNewlyCreatedDBObject(e, true);
                return e;
            }
            Line Seg(double x1, double y1, double x2, double y2, string layer) =>
                Add(new Line(new Point3d(x1, y1 + dy, 0), new Point3d(x2, y2 + dy, 0)), layer);
            Polyline Poly(string layer, bool closed, IEnumerable<(double X, double Y)> points)
            {
                var pl = new Polyline { Closed = closed };
                var i = 0;
                foreach (var (x, y) in points) pl.AddVertexAt(i++, new Point2d(x, y + dy), 0, 0, 0);
                return Add(pl, layer);
            }
            void Text(string value, double x, double y, double height, string layer, double rotation = 0,
                AttachmentPoint justify = AttachmentPoint.BottomCenter)
            {
                var anchor = new Point3d(x, y + dy, 0);
                var text = Add(new DBText
                {
                    TextString = value, Height = height, TextStyleId = styleId, Rotation = rotation,
                    Position = anchor, Justify = justify, AlignmentPoint = anchor,
                }, layer);
                text.AdjustAlignment(db);
            }
            // Hebrew mixed with numbers or Latin, laid out right to left as the office's text reads, one DBText per script:
            // a DBText of both came out scrambled and an MText dropped the space between them (live 2026-11-02).
            void Note(string value, double x, double y, double height, string layer, AttachmentPoint attach = AttachmentPoint.BottomCenter)
            {
                var runs = TypicalSheetText.Runs(value);
                var gap = height * 0.35;
                var texts = new List<(DBText Text, double Width)>();
                foreach (var run in runs)
                {
                    var text = Add(new DBText { TextString = run, Height = height, TextStyleId = styleId, Position = Point3d.Origin }, layer);
                    var e = text.GeometricExtents;
                    texts.Add((text, e.MaxPoint.X - e.MinPoint.X));
                }
                var total = texts.Sum(t => t.Width) + gap * Math.Max(0, texts.Count - 1);
                var right = attach switch
                {
                    AttachmentPoint.BottomLeft or AttachmentPoint.TopLeft or AttachmentPoint.MiddleLeft => x + total,
                    AttachmentPoint.BottomRight or AttachmentPoint.TopRight or AttachmentPoint.MiddleRight => x,
                    _ => x + total / 2,
                };
                var baseY = attach is AttachmentPoint.TopLeft or AttachmentPoint.TopCenter or AttachmentPoint.TopRight ? y - height : y;
                foreach (var (text, width) in texts)
                {
                    right -= width;
                    text.Position = new Point3d(right, baseY + dy, 0);
                    right -= gap;
                }
            }
            void Fill(Polyline outline, string pattern, double scale, short color)
            {
                try
                {
                    var hatch = Add(new Hatch(), PavementLayer);
                    hatch.ColorIndex = color;
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                    hatch.PatternScale = scale;
                    hatch.Associative = false;
                    hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { outline.ObjectId });
                    hatch.EvaluateHatch(true);
                }
                catch { /* the outline alone still shows the layer */ }
            }
            static string Width(double w) => w.ToString("0.00", CultureInfo.InvariantCulture);

            // ---- layout: every panel's level function, extents and height first, so the panels stack without overlap.
            var plans = new List<(DraftedTypicalSectionLogic.Panel Panel, TypicalSheetGeometryLogic.SectionGeometry? Geometry,
                Func<double, double> Level, double MinX, double MaxX, double Top, double Low, double ChainY, double TitleTop, double Bottom)>();
            foreach (var panel in panels)
            {
                var section = panel.Section;
                var geometry = panel.Order == designOrder ? design : null;
                Func<double, double> level;
                if (geometry != null)
                {
                    var g = geometry;
                    double EdgeLevel(double x)
                    {
                        var nearest = g.Surface.SelectMany(l => new[] { l.A, l.B }).OrderBy(p => Math.Abs(p.X - x)).First();
                        return nearest.Y;
                    }
                    level = x => TypicalSheetGeometryLogic.LevelAt(g.Surface, x) ?? EdgeLevel(x);
                }
                else
                {
                    level = x =>
                    {
                        var strip = section.Strips.FirstOrDefault(s => x >= s.FromM - 1e-9 && x <= s.ToM + 1e-9);
                        return strip?.SurfaceKind == "raised" ? RaisedM : 0.0;
                    };
                }
                var xs = section.Breaks.Select(b => b.OffsetM)
                    .Concat(section.Objects.Select(o => o.OffsetM)).Concat(section.Fences.Select(f => f.OffsetM)).Append(0.0).ToList();
                if (geometry != null) { xs.Add(geometry.MinX); xs.Add(geometry.MaxX); }
                var minX = xs.Min() - 1.0;
                var maxX = xs.Max() + 1.0;
                var top = double.NegativeInfinity;
                var low = double.PositiveInfinity;
                for (var x = minX; x <= maxX + 1e-9; x += 0.25)
                {
                    var y = level(x);
                    top = Math.Max(top, y);
                    low = Math.Min(low, y);
                }
                var calloutRows = 0;
                if (geometry != null)
                {
                    low = Math.Min(low, geometry.Ground.Select(p => p.Y).DefaultIfEmpty(low).Min());
                    low = Math.Min(low, geometry.Layers.SelectMany(l => l.Outline).Select(p => p.Y).DefaultIfEmpty(low).Min());
                    calloutRows = geometry.Layers.Select(l => l.Code).Distinct().Count();
                }
                var chainY = top + ChainAboveM;
                var titleTop = chainY + (plans.Count == 0 ? 6.0 : 2.5);
                var bottom = low - 1.5 - calloutRows * 0.7;
                plans.Add((panel, geometry, level, minX, maxX, top, low, chainY, titleTop, bottom));
            }

            var lowest = 0.0;
            for (var index = 0; index < plans.Count; index++)
            {
                var (panel, geometry, level, minX, maxX, _, low, chainY, titleTop, _) = plans[index];
                dy = index == 0 ? 0 : dy + plans[index - 1].Bottom - PanelGapM - titleTop;
                lowest = Math.Min(lowest, dy + plans[index].Bottom);
                var section = panel.Section;

                // ---- the road
                if (geometry != null)
                {
                    foreach (var layer in geometry.Layers)
                    {
                        var outline = Poly(PavementLayer, true, layer.Outline.Select(p => (p.X, p.Y)));
                        var (pattern, scale, color) = HatchFor(layer.Code);
                        Fill(outline, pattern, scale, color);
                    }
                    foreach (var link in geometry.Surface)
                        Seg(link.A.X, link.A.Y, link.B.X, link.B.Y, link.Paved ? RoadLayer : GreenLayer).LineWeight = LineWeight.LineWeight040;
                    // The existing ground, dashed, broken where the surface has no level.
                    var run = new List<(double, double)>();
                    void FlushGround()
                    {
                        if (run.Count >= 2)
                        {
                            var ground = Poly(GroundLayer, false, run);
                            ground.LineWeight = LineWeight.LineWeight025;
                            if (!dashed.IsNull) { ground.LinetypeId = dashed; ground.LinetypeScale = 0.05; }
                        }
                        run.Clear();
                    }
                    for (var i = 0; i < geometry.Ground.Count; i++)
                    {
                        if (i > 0 && geometry.Ground[i].X - geometry.Ground[i - 1].X > 0.75) FlushGround();
                        run.Add((geometry.Ground[i].X, geometry.Ground[i].Y));
                    }
                    FlushGround();
                }
                else
                {
                    for (var i = 0; i < section.Strips.Count; i++)
                    {
                        var strip = section.Strips[i];
                        var y = strip.SurfaceKind == "raised" ? RaisedM : 0.0;
                        Seg(strip.FromM, y, strip.ToM, y, strip.Name is "גינון" ? GreenLayer : RoadLayer);
                        if (strip.SurfaceKind == "road")
                            Fill(Poly(AsphaltLayer, true, new[] { (strip.FromM, 0.0), (strip.ToM, 0.0), (strip.ToM, -0.25), (strip.FromM, -0.25) }),
                                "ANSI31", 0.02, 8);
                        if (i + 1 < section.Strips.Count)
                        {
                            var next = section.Strips[i + 1].SurfaceKind == "raised" ? RaisedM : 0.0;
                            if (Math.Abs(next - y) > 1e-6) Seg(strip.ToM, y, strip.ToM, next, RoadLayer);
                        }
                    }
                    var reach = section.Objects.Select(o => o.OffsetM).Concat(section.Fences.Select(f => f.OffsetM))
                        .Concat(section.Barriers.Select(b => b.OffsetM)).ToList();
                    if (reach.Count > 0)
                    {
                        var groundMin = Math.Min(section.MinM, reach.Min() - 1.0);
                        var groundMax = Math.Max(section.MaxM, reach.Max() + 1.0);
                        if (groundMin < section.MinM - 1e-6) Seg(groundMin, 0, section.MinM, 0, GreenLayer);
                        if (groundMax > section.MaxM + 1e-6) Seg(section.MaxM, 0, groundMax, 0, GreenLayer);
                    }
                    foreach (var curb in section.Breaks.Where(b => b.Kind == DraftedTypicalSectionLogic.Kind.Curb))
                        Poly(RoadLayer, true, new[] { (curb.OffsetM - 0.075, -0.25), (curb.OffsetM + 0.075, -0.25),
                            (curb.OffsetM + 0.075, RaisedM), (curb.OffsetM - 0.075, RaisedM) });
                }

                // ---- work zones, fences, barriers and what stands on the road
                foreach (var zone in section.Zones)
                {
                    var y0 = Math.Max(level(zone.FromM), level(zone.ToM));
                    Poly(WorkLayer, true, new[] { (zone.FromM, y0 + 0.05), (zone.ToM, y0 + 0.05), (zone.ToM, y0 + 1.2), (zone.FromM, y0 + 1.2) });
                    Text(zone.Name, (zone.FromM + zone.ToM) / 2, y0 + 0.45, TextHeight * 0.8, WorkLayer);
                }
                foreach (var fence in section.Fences)
                {
                    var y0 = level(fence.OffsetM);
                    Seg(fence.OffsetM, y0, fence.OffsetM, y0 + 1.8, ObjectLayer);
                    for (var y = 0.4; y < 1.8; y += 0.45) Seg(fence.OffsetM - 0.15, y0 + y, fence.OffsetM + 0.15, y0 + y + 0.3, ObjectLayer);
                    Text("גדר", fence.OffsetM, y0 + 1.95, TextHeight * 0.7, ObjectLayer);
                }
                foreach (var barrier in section.Barriers)
                {
                    var x = barrier.OffsetM;
                    var y0 = level(x);
                    Poly(ObjectLayer, true, new[] { (x - 0.3, y0), (x + 0.3, y0), (x + 0.12, y0 + 0.3), (x + 0.08, y0 + 0.81),
                        (x - 0.08, y0 + 0.81), (x - 0.12, y0 + 0.3) });
                }
                foreach (var placed in section.Objects)
                {
                    var x = placed.OffsetM;
                    var y0 = level(x);
                    switch (placed.Kind)
                    {
                        case DraftedTypicalSectionLogic.ObjectKind.Tree:
                            Seg(x, y0, x, y0 + 2.5, GreenLayer);
                            Add(new Circle(new Point3d(x, y0 + 4.0 + dy, 0), Vector3d.ZAxis, 1.5), GreenLayer);
                            var fate = placed.Layer.IndexOf("KRITA", StringComparison.OrdinalIgnoreCase) >= 0 ? "עץ לכריתה"
                                : placed.Layer.IndexOf("DEAD", StringComparison.OrdinalIgnoreCase) >= 0 ? "עץ יבש"
                                : placed.Layer.IndexOf("SHIMUR", StringComparison.OrdinalIgnoreCase) >= 0 ? "עץ לשימור" : "עץ";
                            Text(fate, x, y0 + 5.7, TextHeight * 0.7, GreenLayer);
                            break;
                        case DraftedTypicalSectionLogic.ObjectKind.Light:
                            // The office's pole: a mast with its arm bent over the road.
                            var toward = x >= 0 ? -1.0 : 1.0;
                            Seg(x, y0, x, y0 + 8.4, ObjectLayer);
                            Poly(ObjectLayer, false, new[] { (x, y0 + 8.4), (x + toward * 0.25, y0 + 8.9), (x + toward * 0.8, y0 + 9.2), (x + toward * 1.8, y0 + 9.3) });
                            Poly(ObjectLayer, true, new[] { (x + toward * 1.5, y0 + 9.2), (x + toward * 2.1, y0 + 9.2), (x + toward * 2.1, y0 + 9.35), (x + toward * 1.5, y0 + 9.35) });
                            break;
                        case DraftedTypicalSectionLogic.ObjectKind.Sign:
                            Seg(x, y0, x, y0 + 2.2, ObjectLayer);
                            Poly(ObjectLayer, true, new[] { (x - 0.4, y0 + 2.2), (x + 0.4, y0 + 2.2), (x + 0.4, y0 + 2.8), (x - 0.4, y0 + 2.8) });
                            Text("תמרור", x, y0 + 2.95, TextHeight * 0.7, ObjectLayer);
                            break;
                    }
                }

                // ---- a car in every lane the plan proves, and its direction (right-hand traffic: the right side runs up-station)
                foreach (var lane in section.Strips.Where(s => IsLane(s.Name)))
                {
                    var x = (lane.FromM + lane.ToM) / 2;
                    DrawCar(x, level(x));
                    var up = x > 0;
                    var ya = chainY - 3.6;
                    Seg(x, ya - 0.6, x, ya + 0.6, ChainLayer);
                    var tip = up ? ya + 0.9 : ya - 0.9;
                    var bse = up ? ya + 0.45 : ya - 0.45;
                    Add(new Solid(new Point3d(x, tip + dy, 0), new Point3d(x - 0.22, bse + dy, 0), new Point3d(x + 0.22, bse + dy, 0)), ChainLayer);
                }

                // ---- slopes, from the corridor
                if (geometry != null)
                {
                    var pavedStrips = section.Strips
                        .Where(s => IsLane(s.Name) || s.Name is "מיסעה" or "שול" || TypicalSheetGeometryLogic.IsPaved(geometry.Surface, s.FromM, s.ToM))
                        .Select(s => (s.FromM, s.ToM)).ToList();
                    foreach (var slope in TypicalSheetGeometryLogic.CrossSlopes(geometry.Surface, pavedStrips))
                    {
                        if (slope.ToX - slope.FromX < 2.0) continue;
                        // Near the carriageway's low end, over the car roofs (a car stands in every lane).
                        var x1 = slope.DownToRight ? slope.ToX - 0.15 : slope.FromX + 0.15;
                        var x0 = slope.DownToRight ? x1 - 1.2 : x1 + 1.2;
                        var y0 = level(x0) + 1.8;
                        var y1 = level(x1) + 1.8;
                        Seg(x0, y0, x1, y1, ChainLayer);
                        var head = slope.DownToRight ? -0.25 : 0.25;
                        Seg(x1, y1, x1 + head, y1 + 0.1, ChainLayer);
                        Seg(x1, y1, x1 + head, y1 - 0.1, ChainLayer);
                        Text(slope.Text, (x0 + x1) / 2, Math.Max(y0, y1) + 0.12, TextHeight * 0.75, ChainLayer);
                    }
                    var pavedMin = pavedStrips.Count > 0 ? pavedStrips.Min(s => s.FromM) : 0;
                    var pavedMax = pavedStrips.Count > 0 ? pavedStrips.Max(s => s.ToM) : 0;
                    foreach (var side in TypicalSheetGeometryLogic.SideSlopes(geometry.Surface, pavedMin, pavedMax))
                    {
                        var nx = -Math.Sin(side.AngleRad) * 0.35;
                        var ny = Math.Cos(side.AngleRad) * 0.35;
                        Text(side.Text, side.At.X + nx, side.At.Y + ny, TextHeight * 0.75, ChainLayer, side.AngleRad);
                    }
                }

                // ---- the width chain: one dimension per strip, the median as one over its own small chain
                var groupInner = new HashSet<double>(section.Groups.SelectMany(g =>
                    section.Breaks.Select(b => b.OffsetM).Where(o => o > g.FromM + 1e-6 && o < g.ToM - 1e-6)));
                var chainBreaks = section.Breaks.Select(b => b.OffsetM).Where(o => !groupInner.Contains(o)).OrderBy(o => o).ToList();
                if (chainBreaks.Count >= 2)
                {
                    Seg(chainBreaks[0], chainY, chainBreaks[^1], chainY, ChainLayer);
                    foreach (var x in chainBreaks)
                    {
                        Seg(x - 0.12, chainY - 0.12, x + 0.12, chainY + 0.12, ChainLayer);
                        Seg(x, chainY + 0.3, x, level(x) + 0.25, ChainLayer);
                    }
                    for (var i = 0; i + 1 < chainBreaks.Count; i++)
                    {
                        var from = chainBreaks[i];
                        var to = chainBreaks[i + 1];
                        var mid = (from + to) / 2;
                        var width = to - from;
                        var group = section.Groups.FirstOrDefault(g => Math.Abs(g.FromM - from) < 1e-6 && Math.Abs(g.ToM - to) < 1e-6);
                        var name = group?.Name ?? section.Strips.FirstOrDefault(s => Math.Abs(s.FromM - from) < 1e-6)?.Name;
                        var narrow = width < 1.0;
                        Text(Width(width), mid, narrow ? chainY + 0.12 : chainY + 0.1, TextHeight * 0.8, ChainLayer,
                            narrow ? Math.PI / 2 : 0, narrow ? AttachmentPoint.MiddleLeft : AttachmentPoint.BottomCenter);
                        if (name == null) continue;
                        // A name wider than its strip stands upright, as the office writes "מפרדה" in its 3.20.
                        var upright = name.Length * TextHeight * 0.7 > width;
                        Text(name, mid, chainY - 0.7, TextHeight, TypicalSectionContracts.NamesLayer,
                            upright ? -Math.PI / 2 : 0, upright ? AttachmentPoint.MiddleLeft : AttachmentPoint.TopCenter);
                    }
                }
                foreach (var group in section.Groups)
                {
                    var subY = chainY - 4.6;
                    var inner = section.Breaks.Select(b => b.OffsetM).Where(o => o >= group.FromM - 1e-6 && o <= group.ToM + 1e-6).OrderBy(o => o).ToList();
                    Seg(group.FromM, subY, group.ToM, subY, ChainLayer);
                    foreach (var x in inner) Seg(x - 0.1, subY - 0.1, x + 0.1, subY + 0.1, ChainLayer);
                    foreach (var x in inner.Where(groupInner.Contains)) Seg(x, subY + 0.2, x, level(x) + 0.25, ChainLayer);
                    for (var i = 0; i + 1 < inner.Count; i++)
                    {
                        var mid = (inner[i] + inner[i + 1]) / 2;
                        Text(Width(inner[i + 1] - inner[i]), mid, subY + 0.08, TextHeight * 0.65, ChainLayer);
                        var name = section.Strips.FirstOrDefault(s => Math.Abs(s.FromM - inner[i]) < 1e-6)?.Name;
                        if (name != null) Text(name, mid, subY - 0.3, TextHeight * 0.75, TypicalSectionContracts.NamesLayer, 0, AttachmentPoint.TopCenter);
                    }
                }

                // ---- right of way
                foreach (var row in section.Breaks.Where(b => b.Kind == DraftedTypicalSectionLogic.Kind.Row))
                {
                    var y0 = level(row.OffsetM);
                    Seg(row.OffsetM, y0 - 1.0, row.OffsetM, chainY - 1.5, RowLayer);
                    Text("זכות הדרך", row.OffsetM + 0.25, y0 + 2.0, TextHeight * 0.8, RowLayer, Math.PI / 2, AttachmentPoint.BottomLeft);
                }

                // ---- the centreline
                var axis = Seg(0, low - 0.8, 0, chainY + 1.4, AxisLayer);
                if (!center.IsNull) { axis.LinetypeId = center; axis.LinetypeScale = 0.05; }
                Text("CL", 0, chainY + 1.5, TextHeight * 1.2, AxisLayer);

                // ---- the pavement layers, called out under the road from the first carriageway right of the axis
                if (geometry != null && geometry.Layers.Count > 0)
                {
                    var refX = section.Strips.Where(s => s.FromM >= -0.05 && (IsLane(s.Name) || s.Name == "מיסעה"))
                        .Concat(section.Strips.Where(s => s.ToM > 0 && TypicalSheetGeometryLogic.IsPaved(geometry.Surface, s.FromM, s.ToM)))
                        .Select(s => (double?)((Math.Max(s.FromM, 0) + s.ToM) / 2)).FirstOrDefault()
                        ?? section.Strips.Select(s => (double?)((s.FromM + s.ToM) / 2)).FirstOrDefault() ?? 2.0;
                    // One callout per layer code, the topmost where two shapes of one code meet under the offset.
                    var stack = TypicalSheetGeometryLogic.LayersAt(geometry.Layers, refX)
                        .GroupBy(l => l.Code).Select(g => g.First()).ToList();
                    for (var i = 0; i < stack.Count; i++)
                    {
                        var (code, layerTop, thickness) = stack[i];
                        var x = refX - 1.5 + i * 0.3;
                        var y = layerTop - thickness / 2;
                        var listY = low - 1.6 - i * 0.7;
                        Add(new Circle(new Point3d(x, y + dy, 0), Vector3d.ZAxis, 0.05), ChainLayer);
                        Seg(x, y, x, listY, ChainLayer);
                        Seg(x, listY, x + 7.0, listY, ChainLayer);
                        var cm = Math.Round(thickness * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
                        Note(cm + " ס\"מ " + code, x + 0.15, listY + 0.08, TextHeight * 0.7, ChainLayer, AttachmentPoint.BottomLeft);
                    }
                }

                // ---- titles
                if (plans.Count > 1 || overlay)
                    Note(panel.Title, minX, chainY + 1.2, TextHeight * 1.2, TypicalSectionContracts.NamesLayer, AttachmentPoint.BottomLeft);
                if (index == 0)
                {
                    var cx = (minX + maxX) / 2;
                    Note("חתך אופייני", cx, chainY + 4.4, TextHeight * 1.8, TypicalSectionContracts.NamesLayer);
                    Seg(cx - 3.2, chainY + 4.25, cx + 3.2, chainY + 4.25, TypicalSectionContracts.NamesLayer);
                    Text(pick.AlignmentName, cx, chainY + 3.3, TextHeight * 1.1, TypicalSectionContracts.NamesLayer);
                    Note(FormattableString.Invariant($"תחנה {pick.Station:0.00}"), cx, chainY + 2.5, TextHeight * 1.1, TypicalSectionContracts.NamesLayer);
                    if (geometry == null)
                        Note("סכמטי, ללא גבהים — נקרא מקווי התנוחה", cx, chainY + 1.9, TextHeight * 0.75, TypicalSectionContracts.NamesLayer);
                }
            }

            // The sheet's frame around everything drawn (2026-11-04, TR-M34): MHD_TYPICAL_SHEET places a section by its
            // frame, and a plan-drawn section without one never reached the A0 sheet.
            if (!overlay)
            {
                Extents3d? all = null;
                foreach (ObjectId id in definition)
                {
                    try
                    {
                        var e = ((Entity)tr.GetObject(id, OpenMode.ForRead)).GeometricExtents;
                        if (all is { } a) { a.AddExtents(e); all = a; } else all = e;
                    }
                    catch { /* an entity with no extents */ }
                }
                if (all is { } box)
                {
                    const double margin = 1.0;
                    var frameLayer = TypicalSectionPicker.EnsureLayer(tr, db, OfficeTypicalRenderer.FrameLayer, 4);
                    var frame = new Polyline { Closed = true, LayerId = frameLayer, ColorIndex = 8 };
                    frame.AddVertexAt(0, new Point2d(box.MinPoint.X - margin, box.MinPoint.Y - margin), 0, 0, 0);
                    frame.AddVertexAt(1, new Point2d(box.MaxPoint.X + margin, box.MinPoint.Y - margin), 0, 0, 0);
                    frame.AddVertexAt(2, new Point2d(box.MaxPoint.X + margin, box.MaxPoint.Y + margin), 0, 0, 0);
                    frame.AddVertexAt(3, new Point2d(box.MinPoint.X - margin, box.MaxPoint.Y + margin), 0, 0, 0);
                    definition.AppendEntity(frame);
                    tr.AddNewlyCreatedDBObject(frame, true);
                }
            }

            var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var reference = new BlockReference(new Point3d(originX, overlay ? originY + OverlayGapM - lowest : originY, 0), definitionId)
            {
                LayerId = TypicalSectionPicker.EnsureLayer(tr, db, SectionLayer, 7),
            };
            var referenceId = model.AppendEntity(reference);
            tr.AddNewlyCreatedDBObject(reference, true);
            // Added beside a firm run in a large drawing (6403) the block stayed undrawn until its graphics were marked.
            reference.RecordGraphicsModified(true);
            return referenceId;

            // A car seen from behind, 1.8 m wide, standing on the road.
            void DrawCar(double x, double road)
            {
                var b = road + 0.3;
                Poly(VehicleLayer, true, new[] { (x - 0.9, b), (x + 0.9, b), (x + 0.9, b + 0.55), (x + 0.68, b + 0.62),
                    (x + 0.55, b + 1.15), (x - 0.55, b + 1.15), (x - 0.68, b + 0.62), (x - 0.9, b + 0.55) });
                Poly(VehicleLayer, true, new[] { (x - 0.6, b + 0.66), (x + 0.6, b + 0.66), (x + 0.5, b + 1.07), (x - 0.5, b + 1.07) });
                Poly(VehicleLayer, true, new[] { (x - 0.82, road), (x - 0.58, road), (x - 0.58, b), (x - 0.82, b) });
                Poly(VehicleLayer, true, new[] { (x + 0.58, road), (x + 0.82, road), (x + 0.82, b), (x + 0.58, b) });
                Poly(VehicleLayer, true, new[] { (x - 0.8, b + 0.22), (x - 0.5, b + 0.22), (x - 0.5, b + 0.38), (x - 0.8, b + 0.38) });
                Poly(VehicleLayer, true, new[] { (x + 0.5, b + 0.22), (x + 0.8, b + 0.22), (x + 0.8, b + 0.38), (x + 0.5, b + 0.38) });
            }
        }

        /// <summary>How each corridor layer is hatched (the office's legend: asphalt grey and dense, base courses dotted,
        /// granular fill coarse).</summary>
        private static (string Pattern, double Scale, short Color) HatchFor(string code)
        {
            var c = code.ToUpperInvariant();
            if (c.Contains("ASF") || c.Contains("ASPH") || c.Contains("PAVE") || c.Contains("BIT")) return ("ANSI31", 0.008, 8);
            if (c.Contains("MAZ") || c.Contains("BASE")) return ("DOTS", 0.012, 33);
            if (c.Contains("AGUM") || c.Contains("SUB")) return ("ANSI37", 0.02, 30);
            return ("ANSI31", 0.02, 9);
        }

        private static ObjectId EnsureLinetype(Transaction tr, Database db, string name)
        {
            var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                try { db.LoadLineTypeFile(name, "acadiso.lin"); }
                catch { try { db.LoadLineTypeFile(name, "acad.lin"); } catch { return ObjectId.Null; } }
                lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            }
            return lt.Has(name) ? lt[name] : ObjectId.Null;
        }

        /// <summary>
        /// The typical section of a firm section (2026-11-02, the office's sheet DD-5001-02): drawn above the firm's MSV
        /// block on its centreline, from the corridor of this very road at the station (<paramref name="corridorName"/>:
        /// surface, layers, existing ground on <paramref name="groundSurface"/>) and from the plan (lanes, shoulders, median,
        /// stages, fences, trees). A strip between two carriageway edges is a carriageway only where that corridor paves it.
        /// Returns the block's id, or null (with the reason) when the plan has no road line along the pick or the firm
        /// block's centreline cannot be read.
        /// </summary>
        public static ObjectId? DrawOverPlanSection(Transaction tr, Database db, TypicalSectionPick pick, Handle firmBlock,
            string? corridorName, string? groundSurface,
            out IReadOnlyList<DraftedTypicalSectionLogic.Panel> panels, out string? reason)
        {
            panels = Array.Empty<DraftedTypicalSectionLogic.Panel>();
            reason = null;
            TypicalSheetGeometryLogic.SectionGeometry? design = null;
            if (!string.IsNullOrEmpty(corridorName))
            {
                try { design = TypicalSheetCorridorReader.Read(tr, corridorName!, pick.AlignmentName, pick.Station, pick, groundSurface); }
                catch { design = null; }
            }
            var (crossings, objects) = Cut(tr, db, pick);
            var built = DraftedTypicalSectionLogic.BuildPanels(crossings, objects, pick.HalfWidthM);
            // The corridor's road goes under the stage it built (the temporary road of a traffic stage is not the design).
            var designOrder = 1;
            if (design != null && design.Surface.Count > 0)
            {
                var corridorBreaks = design.Surface.SelectMany(l => new[] { l.A.X, l.B.X }).ToList();
                designOrder = DraftedTypicalSectionLogic.PanelBuiltBy(built, corridorBreaks) ?? 1;
                var g = design;
                built = DraftedTypicalSectionLogic.BuildPanels(crossings, objects, pick.HalfWidthM,
                    (from, to) => TypicalSheetGeometryLogic.IsPaved(g.Surface, from, to), designOrder);
            }
            if (built.All(p => p.Section.Strips.Count == 0 && p.Section.Objects.Count == 0) && design == null)
            {
                reason = "בתנוחה אין לאורך קו החתך קווי כביש מזוהים (שפת מיסעה, נתיב, שול, מדרכה) — החתך האופייני לא שורטט.";
                return null;
            }
            var block = FirmSectionBlockReader.Read(tr, db, firmBlock);
            var frame = FirmSectionFrameLogic.Decode(block.Input);
            if (frame.CentrelineX is not { } centreline)
            {
                reason = "לבלוק החתך אין קו ציר קריא — החתך האופייני לא מוקם מעליו.";
                return null;
            }
            var firm = (BlockReference)tr.GetObject(block.BlockReferenceId, OpenMode.ForRead);
            var axisX = firm.Position.X + centreline * firm.ScaleFactors.X;
            panels = built;
            var firmTop = firm.GeometricExtents.MaxPoint.Y;
            // With the corridor at the station the typical section is drawn as the office's sheet draws it (2026-11-02,
            // DD-500X): the design only, its widths from the plan, its road from the corridor.
            var designPanel = built.FirstOrDefault(p => p.Order == designOrder);
            if (design != null && design.Surface.Count > 0 && designPanel != null)
            {
                panels = new[] { designPanel };
                var input = new OfficeTypicalRenderer.Input(pick, designPanel.Section, design, DesignSpeedAt(tr, db, pick), designPanel.Section.Findings);
                return OfficeTypicalRenderer.Draw(tr, db, input, axisX, firmTop, overlay: true);
            }
            return Draw(tr, db, built, pick, axisX, firmTop, design, overlay: true, designOrder);
        }

        /// <summary>The alignment's design speed at the station (its last speed change before it), or null.</summary>
        internal static double? DesignSpeedAt(Transaction tr, Database db, TypicalSectionPick pick)
        {
            try
            {
                var civil = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    var alignment = (Autodesk.Civil.DatabaseServices.Alignment)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.Equals(alignment.Name, pick.AlignmentName, StringComparison.OrdinalIgnoreCase)) continue;
                    double? speed = null;
                    foreach (Autodesk.Civil.DatabaseServices.DesignSpeed ds in alignment.DesignSpeeds)
                        if (ds.Station <= pick.Station + 1e-6) speed = ds.Value;
                    return speed;
                }
            }
            catch { /* no speed */ }
            return null;
        }

        private static string UniqueName(BlockTable table, TypicalSectionPick pick)
        {
            var axis = new string(pick.AlignmentName.Select(c => "<>/\\\":;?*|,=`".IndexOf(c) >= 0 || char.IsWhiteSpace(c) ? '_' : c).ToArray());
            var stem = FormattableString.Invariant($"{BlockPrefix}{axis}_{pick.Station:0.000}_{DateTime.Now:yyMMdd_HHmmss}");
            var name = stem;
            for (var i = 2; table.Has(name); i++) name = FormattableString.Invariant($"{stem}_{i}");
            return name;
        }
    }
}
