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
        private const double ChainY = -1.6;
        private const double NamesY = -2.6;

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
        public static (List<Crossing> Crossings, List<PlacedObject> Objects) Cut(Transaction tr, Database db, TypicalSectionPick pick)
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
                        layerKinds[layer] = kind = DraftedTypicalSectionLogic.ClassifyLayer(layer);
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

        /// <summary>Vertical pitch between stacked stage panels (metres in the block): objects reach ~9.5 m above a
        /// panel's ground, the chain, names and panel title ~4 m below it.</summary>
        private const double PanelPitchM = 17.0;

        /// <summary>
        /// Draws the section as one block inserted at (<paramref name="originX"/>, <paramref name="originY"/>) on
        /// <see cref="SectionLayer"/>: one panel per stage stacked downward (existing, design, traffic stages, as the
        /// office's sheets stack שלב 0/1/2), the axis at the insertion point's X, the first panel's ground at its Y, the
        /// title under the last panel. Returns the reference.
        /// </summary>
        public static ObjectId Draw(Transaction tr, Database db, IReadOnlyList<DraftedTypicalSectionLogic.Panel> panels,
            TypicalSectionPick pick, double originX, double originY, bool overlay = false)
        {
            var styleId = FirmSectionBlockReader.EnsureTrueTypeStyle(tr, db);
            var layers = new Dictionary<string, ObjectId>
            {
                [RoadLayer] = TypicalSectionPicker.EnsureLayer(tr, db, RoadLayer, 7),
                [AsphaltLayer] = TypicalSectionPicker.EnsureLayer(tr, db, AsphaltLayer, 8),
                [GreenLayer] = TypicalSectionPicker.EnsureLayer(tr, db, GreenLayer, 3),
                [AxisLayer] = TypicalSectionPicker.EnsureLayer(tr, db, AxisLayer, 1),
                [ChainLayer] = TypicalSectionPicker.EnsureLayer(tr, db, ChainLayer, 2),
                [ObjectLayer] = TypicalSectionPicker.EnsureLayer(tr, db, ObjectLayer, 4),
                [WorkLayer] = TypicalSectionPicker.EnsureLayer(tr, db, WorkLayer, 30),
                [TypicalSectionContracts.NamesLayer] = TypicalSectionPicker.EnsureLayer(tr, db, TypicalSectionContracts.NamesLayer, 7),
            };

            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            var definition = new BlockTableRecord { Name = UniqueName(table, pick), Origin = Point3d.Origin };
            var definitionId = table.Add(definition);
            tr.AddNewlyCreatedDBObject(definition, true);

            var dy = 0.0; // the current panel's ground level inside the block
            void Add(Entity e, string layer)
            {
                e.LayerId = layers[layer];
                e.ColorIndex = 256;
                definition.AppendEntity(e);
                tr.AddNewlyCreatedDBObject(e, true);
            }
            void Line(double x1, double y1, double x2, double y2, string layer) =>
                Add(new Line(new Point3d(x1, y1 + dy, 0), new Point3d(x2, y2 + dy, 0)), layer);
            Polyline Poly(string layer, bool closed, params (double X, double Y)[] points)
            {
                var pl = new Polyline(points.Length) { Closed = closed };
                for (var i = 0; i < points.Length; i++) pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y + dy), 0, 0, 0);
                Add(pl, layer);
                return pl;
            }
            void Text(string value, double x, double y, double height, string layer, double rotation = 0,
                AttachmentPoint justify = AttachmentPoint.BottomCenter)
            {
                var anchor = new Point3d(x, y + dy, 0);
                var text = new DBText
                {
                    TextString = value, Height = height, TextStyleId = styleId, Rotation = rotation,
                    Position = anchor, Justify = justify, AlignmentPoint = anchor,
                };
                Add(text, layer);
                text.AdjustAlignment(db);
            }
            static string Width(double w) => w.ToString("0.00", CultureInfo.InvariantCulture);

            var min = Math.Min(0, panels.Min(p => p.Section.MinM));
            var max = Math.Max(0, panels.Max(p => p.Section.MaxM));
            var anyLight = panels.Any(p => p.Section.Objects.Any(o => o.Kind == DraftedTypicalSectionLogic.ObjectKind.Light));

            var lowest = NamesY; // the lowest name of the current panel, so the title clears upright names
            for (var index = 0; index < panels.Count; index++)
            {
                dy = -index * PanelPitchM;
                lowest = NamesY;
                var panel = panels[index];
                var section = panel.Section;

                // Surface: road at grade with its asphalt layer, raised strips above the curb, the rest as ground.
                double LevelOf(DraftedTypicalSectionLogic.Strip strip) => strip.SurfaceKind == "raised" ? RaisedM : 0.0;
                for (var i = 0; i < section.Strips.Count; i++)
                {
                    var strip = section.Strips[i];
                    var y = LevelOf(strip);
                    Line(strip.FromM, y, strip.ToM, y, strip.Name is "גינון" ? GreenLayer : RoadLayer);
                    if (strip.SurfaceKind == "road")
                    {
                        var band = Poly(AsphaltLayer, true, (strip.FromM, 0), (strip.ToM, 0), (strip.ToM, -0.25), (strip.FromM, -0.25));
                        try
                        {
                            var hatch = new Hatch();
                            Add(hatch, AsphaltLayer);
                            hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
                            hatch.PatternScale = 0.05;
                            hatch.Associative = false;
                            hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { band.ObjectId });
                            hatch.EvaluateHatch(true);
                        }
                        catch { /* the outline alone still shows the asphalt */ }
                    }
                    // A level change between neighbours is a curb face.
                    if (i + 1 < section.Strips.Count)
                    {
                        var next = LevelOf(section.Strips[i + 1]);
                        if (Math.Abs(next - y) > 1e-6) Line(strip.ToM, y, strip.ToM, next, RoadLayer);
                    }
                }

                // Ground on to every tree, pole, sign and fence beyond the last road line, so none of them floats.
                var reach = section.Objects.Select(o => o.OffsetM).Concat(section.Fences.Select(f => f.OffsetM))
                    .Concat(section.Barriers.Select(b => b.OffsetM)).ToList();
                if (reach.Count > 0)
                {
                    var groundMin = Math.Min(section.MinM, reach.Min() - 1.0);
                    var groundMax = Math.Max(section.MaxM, reach.Max() + 1.0);
                    if (groundMin < section.MinM - 1e-6) Line(groundMin, 0, section.MinM, 0, GreenLayer);
                    if (groundMax > section.MaxM + 1e-6) Line(section.MaxM, 0, groundMax, 0, GreenLayer);
                }

                // Curbs as the stone they are.
                foreach (var curb in section.Breaks.Where(b => b.Kind == DraftedTypicalSectionLogic.Kind.Curb))
                    Poly(RoadLayer, true, (curb.OffsetM - 0.075, -0.25), (curb.OffsetM + 0.075, -0.25),
                        (curb.OffsetM + 0.075, RaisedM), (curb.OffsetM - 0.075, RaisedM));

                // Work zones: a frame on the road with its name.
                foreach (var zone in section.Zones)
                {
                    Poly(WorkLayer, true, (zone.FromM, 0.05), (zone.ToM, 0.05), (zone.ToM, 1.2), (zone.FromM, 1.2));
                    Text(zone.Name, (zone.FromM + zone.ToM) / 2, 0.45, TextHeight, WorkLayer);
                }

                // Fences, barriers and the objects on the line.
                foreach (var fence in section.Fences)
                {
                    Line(fence.OffsetM, 0, fence.OffsetM, 1.8, ObjectLayer);
                    for (var y = 0.4; y < 1.8; y += 0.45) Line(fence.OffsetM - 0.15, y, fence.OffsetM + 0.15, y + 0.3, ObjectLayer);
                    Text("גדר", fence.OffsetM, 1.95, TextHeight * 0.75, ObjectLayer);
                }
                foreach (var barrier in section.Barriers)
                    Poly(ObjectLayer, true, (barrier.OffsetM - 0.3, 0), (barrier.OffsetM + 0.3, 0), (barrier.OffsetM + 0.12, 0.3),
                        (barrier.OffsetM + 0.08, 0.8), (barrier.OffsetM - 0.08, 0.8), (barrier.OffsetM - 0.12, 0.3));
                foreach (var placed in section.Objects)
                {
                    var x = placed.OffsetM;
                    switch (placed.Kind)
                    {
                        case DraftedTypicalSectionLogic.ObjectKind.Tree:
                            Line(x, 0, x, 2.5, GreenLayer);
                            Add(new Circle(new Point3d(x, 4.0 + dy, 0), Vector3d.ZAxis, 1.5), GreenLayer);
                            var fate = placed.Layer.IndexOf("KRITA", StringComparison.OrdinalIgnoreCase) >= 0 ? "עץ לכריתה"
                                : placed.Layer.IndexOf("DEAD", StringComparison.OrdinalIgnoreCase) >= 0 ? "עץ יבש"
                                : placed.Layer.IndexOf("SHIMUR", StringComparison.OrdinalIgnoreCase) >= 0 ? "עץ לשימור" : "עץ";
                            Text(fate, x, 5.7, TextHeight * 0.75, GreenLayer);
                            break;
                        case DraftedTypicalSectionLogic.ObjectKind.Light:
                            Line(x, 0, x, 8.0, ObjectLayer);
                            Line(x, 8.0, x + (x >= 0 ? -1.5 : 1.5), 8.3, ObjectLayer);
                            Text("עמוד תאורה", x, 8.5, TextHeight * 0.75, ObjectLayer);
                            break;
                        case DraftedTypicalSectionLogic.ObjectKind.Sign:
                            Line(x, 0, x, 2.2, ObjectLayer);
                            Poly(ObjectLayer, true, (x - 0.4, 2.2), (x + 0.4, 2.2), (x + 0.4, 2.8), (x - 0.4, 2.8));
                            Text("תמרור", x, 2.95, TextHeight * 0.75, ObjectLayer);
                            break;
                    }
                }

                // Axis and the panel's name.
                var top = anyLight ? 9.5 : 7.0;
                Line(0, NamesY - 0.4, 0, top, AxisLayer);
                Text(pick.AlignmentName, 0, top + 0.2, TextHeight, AxisLayer);
                Text(panel.Title, min, top - 0.5, TextHeight * 1.5, TypicalSectionContracts.NamesLayer, 0, AttachmentPoint.BottomLeft);

                // Width chain and names below the ground line.
                Line(section.MinM, ChainY, section.MaxM, ChainY, ChainLayer);
                foreach (var mark in section.Breaks)
                    Line(mark.OffsetM, -0.4, mark.OffsetM, ChainY - 0.15, ChainLayer);
                foreach (var strip in section.Strips)
                {
                    var mid = (strip.FromM + strip.ToM) / 2;
                    var narrow = strip.WidthM < 1.2;
                    Text(Width(strip.WidthM), mid, narrow ? ChainY - 0.2 : ChainY + 0.1, TextHeight * (narrow ? 0.7 : 1.0), ChainLayer,
                        narrow ? -Math.PI / 2 : 0, narrow ? AttachmentPoint.MiddleLeft : AttachmentPoint.BottomCenter);
                    if (strip.Name == null) continue;
                    // A name wider than its strip stands upright under it, as the firm turns a number too wide for its width.
                    var upright = strip.Name.Length * TextHeight * 0.75 > strip.WidthM;
                    if (upright) lowest = Math.Min(lowest, NamesY - strip.Name.Length * TextHeight * 0.8);
                    Text(strip.Name, mid, NamesY, TextHeight, TypicalSectionContracts.NamesLayer,
                        upright ? -Math.PI / 2 : 0, upright ? AttachmentPoint.MiddleLeft : AttachmentPoint.TopCenter);
                }
            }

            // Title under the last panel. Each line is one script: a DBText mixing Hebrew with Latin and numbers came out
            // in scrambled order (live 2026-11-02), so the Hebrew, the axis/station and the note are separate lines.
            var center = (min + max) / 2;
            if (overlay)
            {
                // Above the firm's section, which carries its own title, levels and offsets.
                Text("רצועות, סימון ואביזרים מהתנוחה — לחתך שמתחת", center, lowest - 1.0, TextHeight,
                    TypicalSectionContracts.NamesLayer, 0, AttachmentPoint.TopCenter);
            }
            else
            {
                Text("חתך טיפוסי", center, lowest - 1.0, TextHeight * 2, TypicalSectionContracts.NamesLayer, 0, AttachmentPoint.TopCenter);
                Text(FormattableString.Invariant($"{pick.AlignmentName}   STA {pick.Station:0.00}   1:200"), center, lowest - 2.2,
                    TextHeight * 1.25, TypicalSectionContracts.NamesLayer, 0, AttachmentPoint.TopCenter);
                Text("סכמטי, ללא גבהים — נקרא מקווי התנוחה", center, lowest - 3.1, TextHeight,
                    TypicalSectionContracts.NamesLayer, 0, AttachmentPoint.TopCenter);
            }

            var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var reference = new BlockReference(new Point3d(originX, originY, 0), definitionId)
            {
                LayerId = TypicalSectionPicker.EnsureLayer(tr, db, SectionLayer, 7),
            };
            var referenceId = model.AppendEntity(reference);
            tr.AddNewlyCreatedDBObject(reference, true);
            // Added beside a firm run in a large drawing (6403) the block stayed undrawn until its graphics were marked.
            reference.RecordGraphicsModified(true);
            return referenceId;
        }

        /// <summary>
        /// The typical-section layer of a firm section (2026-11-02, compared with the office's MAAKAF sheet): the firm's
        /// MSV block gives the road as the corridor built it — levels, offsets, the width of each carriageway — and the
        /// office's typical section adds, right above it on the same axis, the lanes, shoulders and median the plan marks,
        /// the stage panels, fences, barriers and trees. This draws that layer from the plan above the block whose
        /// handle is given, its axis on the block's centreline. Returns the overlay's id, or null (with the reason) when the
        /// plan has no road line along the pick or the block's centreline cannot be read.
        /// </summary>
        public static ObjectId? DrawOverPlanSection(Transaction tr, Database db, TypicalSectionPick pick, Handle firmBlock,
            out IReadOnlyList<DraftedTypicalSectionLogic.Panel> panels, out string? reason)
        {
            panels = Array.Empty<DraftedTypicalSectionLogic.Panel>();
            reason = null;
            var (crossings, objects) = Cut(tr, db, pick);
            var built = DraftedTypicalSectionLogic.BuildPanels(crossings, objects, pick.HalfWidthM);
            if (built.All(p => p.Section.Strips.Count == 0 && p.Section.Objects.Count == 0))
            {
                reason = "בתנוחה אין לאורך קו החתך קווי כביש מזוהים (שפת מיסעה, נתיב, שול, מדרכה) — לא נוספה שכבת הרצועות.";
                return null;
            }
            var block = FirmSectionBlockReader.Read(tr, db, firmBlock);
            var frame = FirmSectionFrameLogic.Decode(block.Input);
            if (frame.CentrelineX is not { } centreline)
            {
                reason = "לבלוק החתך אין קו ציר קריא — שכבת הרצועות מהתנוחה לא מוקמה מעליו.";
                return null;
            }
            var reference = (BlockReference)tr.GetObject(block.BlockReferenceId, OpenMode.ForRead);
            var axisX = reference.Position.X + centreline * reference.ScaleFactors.X;
            var headerTop = double.IsFinite(block.HeaderTopY) ? block.HeaderTopY : 12.0;
            // The overlay hangs ~7 m below each panel's ground (chain, names, note) and stacks its panels downward.
            var originY = reference.Position.Y + headerTop * reference.ScaleFactors.Y + 8.5 + (built.Count - 1) * PanelPitchM;
            panels = built;
            return Draw(tr, db, built, pick, axisX, originY, overlay: true);
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
