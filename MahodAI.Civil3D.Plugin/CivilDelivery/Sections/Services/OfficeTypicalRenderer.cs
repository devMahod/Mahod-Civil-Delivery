using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Color = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Shared;
using DraftedSection = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.DraftedSection;
using Kind = MahodAI.CivilDelivery.Shared.DraftedTypicalSectionLogic.Kind;
using SectionGeometry = MahodAI.CivilDelivery.Shared.TypicalSheetGeometryLogic.SectionGeometry;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// A typical section drawn as the office draws one on its sheets (2026-11-02, owner: "exactly the same as the separate
    /// sheet" - MHD-HW-000-IC_00000-DD-500X and the NTS drawing it shows): the office's layers, colours, text styles and
    /// heights, its dimension style (m-100: 0.20 text, oblique ticks, two decimals), its symbols (cars, arrows, the
    /// high-occupancy diamond, cyclists, lamp posts, barriers, grass, the 807 mark), the title stack over the axis, the
    /// width chain as dimensions with a dotted line down to the road at every break, the names under the chain, the
    /// median's own small chain, the slopes, and the pavement callouts, all in a frame. Every number is read from the
    /// drawing: widths from the plan lines, levels, layers and slopes from the corridor at the station.
    /// </summary>
    internal static class OfficeTypicalRenderer
    {
        public const string BlockPrefix = "MTS_";
        public const string FrameLayer = "HW-TYP-FRAME";

        private const double ChainAboveM = 8.9;      // the office: the chain 8.87 m over the road at the axis
        private const double NamesBelowChainM = 1.9;
        private const double ArrowAboveRoadM = 4.9;
        private const double TitleGapM = 1.2;
        private const double OverlayGapM = 3.0;
        private const double FrameMarginM = 2.0;

        internal static readonly (string Name, short Color, string? Linetype)[] Layers =
        {
            ("HW-TYP-TEXT", 2, null), ("HW-TYP-DIM", 15, null), ("HW-TYP-CL", 1, null), ("HW-CS-ALGN", 3, "CENTER"),
            ("HW-CS", 1, null), ("HW-CS-EX", 114, "DASHED"), ("HW-CS-SLOP", 2, null), ("HW-STRC-MISC", 3, null),
            ("HW-TYP-ASPH1", 254, null), ("HW-CS-HTCH-3", 145, null), ("HW-CS-HTCH-2", 8, null), ("EBUD-h-r", 253, null),
            ("HW-CS-TX-BARRIER", 1, null), ("BL-Annotation Text", 2, null), ("HW-CS-NOTE", 5, null), ("PGVUL3", 5, null),
            ("4MAN", 8, null), ("44_block", 1, null), ("LA-SYMB-BL", 10, null), ("HW-TYP-BARI-FLEX", 2, null),
            ("HW-BARI-RIGD-BL", 7, null), ("TR-MARK-YLW-807", 2, null), ("LA-CS-VEGT-BL", 3, null), (FrameLayer, 4, null),
            ("0", 7, null),
        };

        /// <summary>
        /// The colour each layer's entities are drawn in (2026-11-03, owner: "hard to see on a white background, make it
        /// more natural and contrast"). The office's layer colours are screen colours for a dark background and its plot
        /// style (yellow callouts, cyan frames, light green); on white paper they wash out. Set on the entities, so the
        /// office's layers keep their own colours: 7 (black on white, white on dark) for text and the road, greys for
        /// dimensions, layer lines and frames, brown ground, dark green grass, red for the right of way and the axis.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, short> Palette = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase)
        {
            ["HW-TYP-TEXT"] = 7, ["HW-TYP-DIM"] = 8, ["HW-TYP-CL"] = 1, ["HW-CS-ALGN"] = 1, ["HW-CS"] = 7,
            ["HW-CS-EX"] = 32, ["HW-CS-SLOP"] = 7, ["HW-STRC-MISC"] = 8, ["HW-TYP-ASPH1"] = 252, ["HW-CS-HTCH-3"] = 33,
            ["HW-CS-HTCH-2"] = 8, ["EBUD-h-r"] = 252, ["HW-CS-TX-BARRIER"] = 1, ["BL-Annotation Text"] = 7,
            ["HW-CS-NOTE"] = 7, ["PGVUL3"] = 1, ["4MAN"] = 8, ["44_block"] = 8, ["LA-SYMB-BL"] = 8,
            ["HW-TYP-BARI-FLEX"] = 8, ["HW-BARI-RIGD-BL"] = 8, ["TR-MARK-YLW-807"] = 40, ["LA-CS-VEGT-BL"] = 94,
            [FrameLayer] = 8, ["0"] = 7,
        };

        /// <summary>What one panel is drawn from.</summary>
        internal sealed record Input(
            TypicalSectionPick Pick, DraftedSection Section, SectionGeometry? Design, double? DesignSpeedKmh, IReadOnlyList<string> Notes);

        /// <summary>
        /// Draws the panel as one block, 1:1 in metres, the axis at <paramref name="originX"/>; the road at the axis on
        /// <paramref name="originY"/>, or with <paramref name="overlay"/> the whole panel's frame <see cref="OverlayGapM"/>
        /// above it. Returns the reference.
        /// </summary>
        public static ObjectId Draw(Transaction tr, Database db, Input input, double originX, double originY, bool overlay)
        {
            var section = input.Section;
            var design = input.Design;
            var layer = Layers.ToDictionary(l => l.Name, l => EnsureLayer(tr, db, l.Name, l.Color, l.Linetype));
            var fonts = Fonts.Ensure(tr, db);
            var dimStyle = EnsureDimStyle(tr, db, fonts.Number);
            var dot = EnsureLinetype(tr, db, "DOT2");

            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            var definition = new BlockTableRecord { Name = UniqueName(table, input.Pick), Origin = Point3d.Origin };
            var definitionId = table.Add(definition);
            tr.AddNewlyCreatedDBObject(definition, true);

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            void Grow(double x, double y) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            T Add<T>(T e, string lay) where T : Entity
            {
                e.LayerId = layer[lay];
                e.ColorIndex = Palette.TryGetValue(lay, out var colour) ? colour : (short)256;
                definition.AppendEntity(e);
                tr.AddNewlyCreatedDBObject(e, true);
                return e;
            }
            Line Seg(double x1, double y1, double x2, double y2, string lay)
            {
                Grow(x1, y1); Grow(x2, y2);
                return Add(new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)), lay);
            }
            Polyline Poly(string lay, bool closed, IEnumerable<(double X, double Y)> points, double width = 0)
            {
                var pl = new Polyline { Closed = closed };
                var i = 0;
                foreach (var (x, y) in points) { pl.AddVertexAt(i++, new Point2d(x, y), 0, width, width); Grow(x, y); }
                return Add(pl, lay);
            }
            // Hebrew in the office's SHX (right to left from its anchor's right end), numbers and Latin LTR.
            DBText Text(string value, double x, double y, double height, string lay, AttachmentPoint justify, double rotation = 0, bool cl = false)
            {
                var hebrew = OfficeTypicalSheet.HasHebrew(value);
                // The office's SHX has no capitals and no "%": a line with Latin or a percent is laid out run by run,
                // its Hebrew in the SHX and the rest in MONOTXT.
                if (hebrew && fonts.HebrewIsShx && value.Any(ch => ch == '%' || (ch < 128 && char.IsLetter(ch))))
                    return Note(value, x, y, height, lay, justify);
                var styleId = cl ? fonts.Axis : hebrew ? fonts.Hebrew : fonts.Number;
                var shown = hebrew && fonts.HebrewIsShx ? OfficeTypicalSheet.ShxHebrew(value) : value;
                var anchor = new Point3d(x, y, 0);
                var t = Add(new DBText { TextString = shown, Height = height, TextStyleId = styleId, Rotation = rotation, Position = anchor }, lay);
                if (hebrew && !fonts.HebrewIsShx)
                {
                    // No office Hebrew font here: the TrueType fallback lays its runs out right to left itself.
                    t.Erase();
                    return Note(value, x, y, height, lay, justify);
                }
                if (rotation == 0)
                {
                    // Placed by its measured extents: AutoCAD's justification of a right-to-left SHX lands the text on
                    // the wrong side of its anchor (live 2026-11-02).
                    try
                    {
                        var e = t.GeometricExtents;
                        var dx = justify switch
                        {
                            AttachmentPoint.BaseLeft or AttachmentPoint.BottomLeft or AttachmentPoint.MiddleLeft or AttachmentPoint.TopLeft => x - e.MinPoint.X,
                            AttachmentPoint.BaseRight or AttachmentPoint.BottomRight or AttachmentPoint.MiddleRight or AttachmentPoint.TopRight => x - e.MaxPoint.X,
                            _ => x - (e.MinPoint.X + e.MaxPoint.X) / 2,
                        };
                        var dy = justify switch
                        {
                            AttachmentPoint.TopLeft or AttachmentPoint.TopCenter or AttachmentPoint.TopRight => -height,
                            AttachmentPoint.MiddleLeft or AttachmentPoint.MiddleCenter or AttachmentPoint.MiddleRight => -height / 2,
                            _ => 0.0,
                        };
                        t.Position = new Point3d(anchor.X + dx, anchor.Y + dy, 0);
                    }
                    catch { /* left at its anchor */ }
                }
                else if (justify != AttachmentPoint.BaseLeft)
                {
                    t.Justify = justify;
                    t.AlignmentPoint = anchor;
                    t.AdjustAlignment(db);
                }
                try { var e = t.GeometricExtents; Grow(e.MinPoint.X, e.MinPoint.Y); Grow(e.MaxPoint.X, e.MaxPoint.Y); } catch { Grow(x, y); }
                return t;
            }
            DBText Note(string value, double x, double y, double height, string lay, AttachmentPoint attach)
            {
                var runs = TypicalSheetText.Runs(value);
                var gap = height * 0.5;
                var texts = new List<(DBText T, double W)>();
                var rtl = new HashSet<DBText>();
                foreach (var run in runs)
                {
                    var hebrewRun = OfficeTypicalSheet.HasHebrew(run);
                    var shx = fonts.HebrewIsShx && hebrewRun;
                    var style = shx ? fonts.Hebrew : fonts.HebrewIsShx ? fonts.Number : fonts.Fallback;
                    var t = Add(new DBText { TextString = shx ? OfficeTypicalSheet.ShxHebrew(run) : run, Height = height, TextStyleId = style, Position = Point3d.Origin }, lay);
                    if (shx) rtl.Add(t);
                    var e = t.GeometricExtents;
                    texts.Add((t, e.MaxPoint.X - e.MinPoint.X));
                }
                var total = texts.Sum(t => t.W) + gap * Math.Max(0, texts.Count - 1);
                var right = attach switch
                {
                    AttachmentPoint.BottomLeft or AttachmentPoint.BaseLeft or AttachmentPoint.TopLeft or AttachmentPoint.MiddleLeft => x + total,
                    AttachmentPoint.BottomRight or AttachmentPoint.BaseRight or AttachmentPoint.TopRight or AttachmentPoint.MiddleRight => x,
                    _ => x + total / 2,
                };
                var baseY = attach is AttachmentPoint.TopLeft or AttachmentPoint.TopCenter or AttachmentPoint.TopRight ? y - height
                    : attach is AttachmentPoint.MiddleLeft or AttachmentPoint.MiddleCenter or AttachmentPoint.MiddleRight ? y - height / 2 : y;
                foreach (var (t, w) in texts)
                {
                    t.Position = new Point3d(rtl.Contains(t) ? right : right - w, baseY, 0);
                    right -= w;
                    Grow(right, baseY); Grow(right + w, baseY + height);
                    right -= gap;
                }
                return texts.Count > 0 ? texts[0].T : Add(new DBText { TextString = "", Height = height, Position = new Point3d(x, y, 0) }, lay);
            }
            void Symbol(string name, double x, double y, double sx, double sy, double rotation, string lay, short color = 256)
            {
                var id = EnsureSymbol(tr, db, table, name);
                var r = Add(new BlockReference(new Point3d(x, y, 0), id) { ScaleFactors = new Scale3d(sx, sy, 1), Rotation = rotation }, lay);
                if (color != 256) r.ColorIndex = color;
                try { var e = r.GeometricExtents; Grow(e.MinPoint.X, e.MinPoint.Y); Grow(e.MaxPoint.X, e.MaxPoint.Y); } catch { Grow(x, y); }
            }
            void Hatch(string lay, IReadOnlyList<(double X, double Y)> outline, string pattern, double scale, double angleDeg, short? color = null)
            {
                Polyline? boundary = null;
                try
                {
                    boundary = new Polyline { Closed = true };
                    for (var i = 0; i < outline.Count; i++) boundary.AddVertexAt(i, new Point2d(outline[i].X, outline[i].Y), 0, 0, 0);
                    var h = Add(new Hatch(), lay);
                    if (color is { } c) h.ColorIndex = c;
                    h.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                    h.PatternScale = scale;
                    h.PatternAngle = angleDeg * Math.PI / 180;
                    h.Associative = false;
                    var loop = new Point2dCollection(); var bulges = new DoubleCollection();
                    foreach (var (x, y) in outline) { loop.Add(new Point2d(x, y)); bulges.Add(0); }
                    loop.Add(new Point2d(outline[0].X, outline[0].Y)); bulges.Add(0);
                    h.AppendLoop(HatchLoopTypes.Polyline, loop, bulges);
                    h.EvaluateHatch(true);
                }
                catch { /* the outline still shows the layer */ }
                finally { boundary?.Dispose(); }
            }
            static string W(double w) => w.ToString("0.00", CultureInfo.InvariantCulture);

            // ---- the road's level across the section: the corridor's finished surface, flat where there is none
            Func<double, double> level;
            if (design != null && design.Surface.Count > 0)
            {
                var g = design;
                level = x => TypicalSheetGeometryLogic.LevelAt(g.Surface, x)
                    ?? g.Surface.SelectMany(l => new[] { l.A, l.B }).OrderBy(p => Math.Abs(p.X - x)).First().Y;
            }
            else level = _ => 0.0;

            // ---- the chain's breaks: the plan's, then the corridor's own beyond them up to where the side slope starts
            // (6403 RD-40 79650: the 1.30 behind the shoulder, the 1.00 behind the bike path)
            var breaks = section.Breaks.Where(b => b.Kind != "axis").Select(b => b.OffsetM).OrderBy(o => o).ToList();
            var groupInner = new HashSet<double>(section.Groups.SelectMany(gr =>
                breaks.Where(o => o > gr.FromM + 1e-6 && o < gr.ToM - 1e-6)));
            var extra = new List<double>();
            // With no plan line at all (2026-11-04, a coworker's 6403 with every plan reference missing) the chain is the
            // corridor's own: its paved pieces from the axis out, so every width is still dimensioned.
            var fromAxis = breaks.Count == 0;
            if (design != null && fromAxis &&
                design.Surface.Any(l => Math.Abs(l.A.X) < 0.05 || Math.Abs(l.B.X) < 0.05))
                extra.Add(0);
            if (design != null && (breaks.Count > 0 || fromAxis))
            {
                foreach (var side in new[] { -1, 1 })
                {
                    var edge = fromAxis ? 0 : side < 0 ? breaks[0] : breaks[^1];
                    var links = design.Surface
                        .Select(l => l.A.X <= l.B.X ? l : l with { A = l.B, B = l.A })
                        .Where(l => side < 0 ? l.A.X < edge - 0.05 : l.B.X > edge + 0.05)
                        .OrderBy(l => side < 0 ? -l.B.X : l.A.X).ToList();
                    foreach (var l in links)
                    {
                        var run = l.B.X - l.A.X;
                        if (run < 0.05) continue;
                        if (!l.Paved || Math.Abs((l.B.Y - l.A.Y) / run) > 0.25) break;   // the side slope or the ditch begins
                        var x = side < 0 ? l.A.X : l.B.X;
                        if (Math.Abs(x - edge) > 0.05 && !extra.Any(e => Math.Abs(e - x) < 0.05)) extra.Add(x);
                    }
                }
            }
            var chain = breaks.Where(o => !groupInner.Contains(o)).Concat(extra).OrderBy(o => o).ToList();
            var roadTop = chain.Count > 0 ? chain.Max(x => level(x)) : level(0);
            var chainY = Math.Max(level(0), roadTop) + ChainAboveM;

            // ---- the road: the corridor's surface, its layers hatched as the office's legend, the existing ground dashed
            var pavedRanges = new List<(double From, double To)>();
            if (design != null)
            {
                foreach (var link in design.Surface)
                    Seg(link.A.X, link.A.Y, link.B.X, link.B.Y, "HW-CS");
                foreach (var shape in design.Layers)
                {
                    if (shape.Outline.Count < 3) continue;
                    Poly("HW-STRC-MISC", true, shape.Outline.Select(p => (p.X, p.Y)));
                    var (lay, pattern, scale, angle) = HatchOf(shape.Code);
                    Hatch(lay, shape.Outline.Select(p => (p.X, p.Y)).ToList(), pattern, scale, angle);
                }
                if (design.Ground.Count >= 2)
                {
                    var ground = Poly("HW-CS-EX", false, design.Ground.Select(p => (p.X, p.Y)));
                    ground.LinetypeScale = 0.05;
                }
                pavedRanges.AddRange(section.Strips
                    .Where(s => s.Name is "נתיב" or "נר\"ת" or "נתיב זמני" or "מיסעה" || (s.Name == "שול" && TypicalSheetGeometryLogic.IsPaved(design.Surface, s.FromM, s.ToM)))
                    .Select(s => (s.FromM, s.ToM)));
            }
            else
            {
                if (chain.Count >= 2) Seg(chain[0], 0, chain[^1], 0, "HW-CS");
            }

            // ---- the width chain: dimensions in the office's style, a dotted line down to the road at every break
            for (var i = 0; i + 1 < chain.Count; i++)
                Dim(chain[i], chain[i + 1], chainY);
            foreach (var x in chain.Concat(section.Groups.SelectMany(gr => new[] { gr.FromM, gr.ToM })).Distinct())
            {
                var guide = Seg(x, level(x) + 0.05, x, chainY + 0.6, "HW-TYP-DIM");
                if (!dot.IsNull) { guide.LinetypeId = dot; guide.LinetypeScale = 0.02; }
            }
            foreach (var x in section.Breaks.Select(b => b.OffsetM).Where(groupInner.Contains))
            {
                var guide = Seg(x, level(x) + 0.05, x, chainY - 0.7, "HW-TYP-DIM");
                if (!dot.IsNull) { guide.LinetypeId = dot; guide.LinetypeScale = 0.02; }
            }
            void Dim(double from, double to, double y, double extTop = double.NaN)
            {
                var dim = new RotatedDimension(Math.PI, new Point3d(to, level(to) + 1.7, 0), new Point3d(from, level(from) + 1.7, 0),
                    new Point3d(from, y, 0), string.Empty, dimStyle);
                Add(dim, "HW-TYP-DIM");
                Grow(from, y + 0.3); Grow(to, y + 0.3);
            }

            // ---- names under the chain; the median as one name standing up over its own small chain
            foreach (var strip in section.Strips.Where(s => s.Name != null))
            {
                if (section.Groups.Any(gr => strip.FromM >= gr.FromM - 1e-6 && strip.ToM <= gr.ToM + 1e-6)) continue;
                Text(strip.Name!, (strip.FromM + strip.ToM) / 2, chainY - NamesBelowChainM, 0.2, "HW-CS-TX-BARRIER", AttachmentPoint.BaseCenter);
            }
            foreach (var gr in section.Groups)
            {
                var subY = chainY - 1.0;
                var inner = section.Breaks.Select(b => b.OffsetM).Where(o => o >= gr.FromM - 1e-6 && o <= gr.ToM + 1e-6).OrderBy(o => o).ToList();
                for (var i = 0; i + 1 < inner.Count; i++)
                {
                    var narrow = inner[i + 1] - inner[i] < 1.0;
                    Dim(inner[i], inner[i + 1], narrow ? subY - 0.35 : subY);
                    var name = section.Strips.FirstOrDefault(s => Math.Abs(s.FromM - inner[i]) < 1e-6)?.Name;
                    if (name != null)
                        Text(name, (inner[i] + inner[i + 1]) / 2, chainY - NamesBelowChainM, 0.2, "HW-CS-TX-BARRIER", AttachmentPoint.BaseCenter);
                }
                Text(gr.Name, (gr.FromM + gr.ToM) / 2 - 0.05, chainY - 0.2, 0.2, "HW-CS-TX-BARRIER", AttachmentPoint.MiddleRight, Math.PI / 2);
            }

            var arrowRow = chainY - ChainAboveM + ArrowAboveRoadM;
            // ---- in every lane its direction (right of the axis up-station), the high-occupancy diamond, a vehicle
            foreach (var strip in section.Strips)
            {
                var mid = (strip.FromM + strip.ToM) / 2;
                var road = level(mid);
                var slope = Math.Atan2(level(Math.Min(strip.ToM, mid + 0.9)) - level(Math.Max(strip.FromM, mid - 0.9)), Math.Min(strip.ToM, mid + 0.9) - Math.Max(strip.FromM, mid - 0.9));
                switch (strip.Name)
                {
                    case "נתיב" or "נתיב זמני":
                        // The office's arrows stand in one row whatever the road's slope; d-13's centre is 0.23 off its base.
                        Symbol("Arrow", mid, arrowRow + (mid > 0 ? 0.23 : -0.24), -0.2, 0.2, mid > 0 ? 0 : Math.PI, "HW-TYP-DIM");
                        Symbol(mid > 0 ? "CarBack" : "CarFront", mid, road, 0.025, 0.025, slope, "0", 7);
                        break;
                    case "נר\"ת":
                        Hatch("HW-TYP-DIM", new[] { (mid, arrowRow - 0.36), (mid + 0.215, arrowRow), (mid, arrowRow + 0.36), (mid - 0.215, arrowRow) }, "SOLID", 1, 0, 40);
                        Symbol(mid > 0 ? "CarBack" : "CarFront", mid, road, 0.025, 0.025, slope, "0", 7);
                        break;
                    case "שביל אופניים":
                        Symbol("Cyclist", mid - 0.5, road, 0.79, 0.79, 0, "4MAN");
                        Symbol("Cyclist", mid + 0.6, road, -0.79, 0.79, 0, "4MAN");
                        break;
                    case "מדרכה":
                        Symbol("Pedestrian", mid, road, -0.1, 0.1, 0, "44_block");
                        break;
                }
            }

            // ---- the yellow edge lines, barriers, lamp posts and trees where the plan has them
            foreach (var b in section.Breaks.Where(b => b.Kind == Kind.EdgeLine && b.Layer.Contains("807")))
                Symbol("Mark807", b.OffsetM, level(b.OffsetM), b.OffsetM < 0 ? -1 : 1, 1, 0, "TR-MARK-YLW-807");
            foreach (var b in section.Barriers)
            {
                var inMedian = section.Groups.Any(gr => b.OffsetM > gr.FromM && b.OffsetM < gr.ToM);
                if (inMedian) Symbol("BarrierRigid", b.OffsetM, level(b.OffsetM) + 0.75, 1, 1, 0, "HW-BARI-RIGD-BL");
                else Symbol("BarrierFlex", b.OffsetM, level(b.OffsetM) + 0.75, b.OffsetM < 0 ? 1 : -1, 1, 0, "HW-TYP-BARI-FLEX");
            }
            foreach (var o in section.Objects.Where(o => o.Kind == DraftedTypicalSectionLogic.ObjectKind.Light))
                Symbol("LampPost", o.OffsetM, level(o.OffsetM), o.OffsetM < 0 ? 2 : -2, 2, 0, "LA-SYMB-BL");

            // ---- slopes: each carriageway's cross slope by its median edge, each side slope's ratio, grass on it
            if (design != null && design.Surface.Count > 0)
            {
                foreach (var cs in TypicalSheetGeometryLogic.CrossSlopes(design.Surface, pavedRanges))
                {
                    var inner = Math.Abs(cs.FromX) < Math.Abs(cs.ToX) ? cs.FromX : cs.ToX;
                    var x0 = inner + (inner < 0 ? -1.1 : 1.1);
                    var y0 = level(x0) + 0.3;
                    var tip = cs.DownToRight ? x0 + 0.5 : x0 - 0.5;
                    var tail = cs.DownToRight ? x0 - 0.5 : x0 + 0.5;
                    Poly("HW-CS-SLOP", false, new[] { (tail, y0), (tip, y0) });
                    Poly("HW-CS-SLOP", false, new[] { (tip + (cs.DownToRight ? -0.17 : 0.17), y0 + 0.1), (tip, y0), (tip + (cs.DownToRight ? -0.17 : 0.17), y0 - 0.1) });
                    Text(cs.Text, x0, y0 + 0.05, 0.2, "HW-CS-SLOP", AttachmentPoint.BottomCenter);
                }
                var pavedMin = pavedRanges.Count > 0 ? pavedRanges.Min(r => r.From) : 0;
                var pavedMax = pavedRanges.Count > 0 ? pavedRanges.Max(r => r.To) : 0;
                foreach (var s in TypicalSheetGeometryLogic.SideSlopes(design.Surface, pavedMin, pavedMax))
                {
                    var up = Math.Cos(s.AngleRad) >= 0 ? s.AngleRad : s.AngleRad + Math.PI;
                    Text(s.Text, s.At.X - Math.Sin(up) * 0.25, s.At.Y + Math.Cos(up) * 0.25, 0.2, "HW-CS-SLOP", AttachmentPoint.BottomCenter, up);
                }
                foreach (var link in design.Surface.Where(l => !l.Paved))
                {
                    var run = Math.Abs(l(link).dx);
                    if (run < 1.0 || Math.Abs(l(link).dy / run) < 0.1) continue;
                    var n = (int)(Math.Sqrt(run * run + l(link).dy * l(link).dy) / 1.3);
                    for (var i = 1; i <= n; i++)
                    {
                        var t = i / (double)(n + 1);
                        Symbol("Grass", link.A.X + (link.B.X - link.A.X) * t, link.A.Y + (link.B.Y - link.A.Y) * t, i % 2 == 0 ? 1 : -1, 1, 0, "LA-CS-VEGT-BL");
                    }
                }
                static (double dx, double dy) l(TypicalSheetGeometryLogic.SurfaceLink k) => (k.B.X - k.A.X, k.B.Y - k.A.Y);
            }

            // ---- right of way
            foreach (var row in section.Breaks.Where(b => b.Kind == Kind.Row))
            {
                var y0 = level(row.OffsetM);
                Poly("PGVUL3", false, new[] { (row.OffsetM, y0 - 1.0), (row.OffsetM, chainY - 3.0) }, 0.1);
                Text("זכות הדרך", row.OffsetM + (row.OffsetM < 0 ? 0.55 : -0.15), y0 + 1.5, 0.4, "PGVUL3", AttachmentPoint.BaseLeft, Math.PI / 2);
            }

            // ---- the axis and its CL mark
            Poly("HW-CS-ALGN", false, new[] { (0.0, level(0)), (0.0, chainY + 0.9) }).LinetypeScale = 0.05;
            Text("C", -0.26, chainY + 0.62, 0.3, "HW-TYP-CL", AttachmentPoint.BaseLeft, 0, cl: true);
            Text("L", -0.1, chainY + 0.5, 0.3, "HW-TYP-CL", AttachmentPoint.BaseLeft, 0, cl: true);

            // ---- the pavement callouts: one stack per structure the corridor builds, under its widest strip
            var bottom = design != null && design.Layers.Count > 0 ? design.Layers.SelectMany(s => s.Outline).Min(p => p.Y) : level(0) - 1.0;
            if (design != null && design.Ground.Count > 0) bottom = Math.Min(bottom, design.Ground.Min(p => p.Y));
            var calloutsLow = bottom;
            if (design != null && design.Layers.Count > 0)
            {
                var stacks = section.Strips
                    .Where(s => s.WidthM >= 1.0 && TypicalSheetGeometryLogic.IsPaved(design.Surface, s.FromM, s.ToM))
                    .Select(s => (Strip: s, Layers: TypicalSheetGeometryLogic.LayersAt(design.Layers, (s.FromM + s.ToM) / 2)))
                    .Where(t => t.Layers.Count > 0)
                    .GroupBy(t => string.Join("|", t.Layers.Select(l => l.Code + "@" + Math.Round(l.ThicknessM, 3))))
                    .Select(g => g.OrderByDescending(t => t.Strip.WidthM).First())
                    .OrderByDescending(t => t.Strip.WidthM).ToList();
                // As the office stacks them: the top layer's leader outermost, each lower layer's one step inward, every
                // row's text running outward from its leader so no leader crosses a row above it; a stack that would
                // overlap one already placed drops below it.
                var placed = new List<(double X0, double X1, double Y0, double Y1)>();
                var skipped = new List<DraftedTypicalSectionLogic.Strip>();
                static double TextWidth(string? t) => t == null ? 0 : t.Length * 0.15 + 0.2;
                foreach (var (strip, stack) in stacks)
                {
                    var rows = OfficeTypicalSheet.Describe(stack.Select(l => (l.Code, l.ThicknessM)));
                    var outward = (strip.FromM + strip.ToM) / 2 < 0 ? -1 : 1;
                    var x0 = outward < 0 ? strip.FromM + 0.5 : strip.ToM - 0.5;
                    var leaders = Enumerable.Range(0, rows.Count).Select(i => x0 - outward * 0.3 * i).ToList();
                    var pitch = rows.Select(r => r.Down != null ? 0.75 : 0.45).ToList();
                    var height = pitch.Sum() + 0.4;
                    var reach = rows.Select((r, i) => leaders[i] + outward * (Math.Max(TextWidth(r.Up), TextWidth(r.Down)) + 0.25)).ToList();
                    var xa = Math.Min(leaders.Min(), reach.Min()) - 1.2; var xb = Math.Max(leaders.Max(), reach.Max()) + 1.2;
                    var top = bottom - 1.2;
                    // One row of stacks, as the office's: a structure whose stack would overlap a wider strip's is not
                    // called out again (its rows would only repeat most of the neighbour's).
                    if (placed.Any(o => xa < o.X1 && xb > o.X0)) { skipped.Add(strip); continue; }
                    placed.Add((xa, xb, top - height, top + 0.3));
                    var rowY = top;
                    var used = 0;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var target = stack[used];
                        used += row.Count;
                        var lx = leaders[i];
                        var ly = target.Top - target.ThicknessM / 2;
                        var donut = Poly("HW-CS-NOTE", true, new[] { (lx - 0.0175, ly), (lx + 0.0175, ly) }, 0.035);
                        donut.SetBulgeAt(0, 1); donut.SetBulgeAt(1, 1);
                        Seg(lx, ly, lx, rowY, "BL-Annotation Text");
                        var anchorX = lx + outward * 0.15;
                        Text(row.Up, anchorX, rowY + 0.05, 0.2, "BL-Annotation Text", outward < 0 ? AttachmentPoint.BottomRight : AttachmentPoint.BottomLeft);
                        if (row.Down != null)
                            Text(row.Down, anchorX, rowY - 0.05, 0.2, "BL-Annotation Text", outward < 0 ? AttachmentPoint.TopRight : AttachmentPoint.TopLeft);
                        Seg(lx, rowY, reach[i], rowY, "BL-Annotation Text");
                        rowY -= pitch[i];
                    }
                    // The structure's total beside the stack, on its inner side, with a bracket over its rows.
                    var (t1, t2, t3) = OfficeTypicalSheet.Total(stack.Sum(l => l.ThicknessM));
                    var bx = leaders.Last() - outward * 0.5;
                    Seg(bx, top + 0.2, bx, rowY + 0.3, "BL-Annotation Text");
                    Seg(bx, top + 0.2, bx + outward * 0.15, top + 0.2, "BL-Annotation Text");
                    Seg(bx, rowY + 0.3, bx + outward * 0.15, rowY + 0.3, "BL-Annotation Text");
                    var tx = bx - outward * 0.15;
                    var ty = (top + rowY) / 2 + 0.3;
                    var al = outward < 0 ? AttachmentPoint.BaseLeft : AttachmentPoint.BaseRight;
                    Text(t1, tx, ty, 0.2, "BL-Annotation Text", al);
                    Text(t2, tx, ty - 0.3, 0.2, "BL-Annotation Text", al);
                    Text(t3, tx, ty - 0.6, 0.2, "BL-Annotation Text", al);
                    calloutsLow = Math.Min(calloutsLow, rowY);
                }
            }

            // ---- the title over the axis, as the office stacks it, and the design speed by the chain
            var withAgum = design?.Layers.Any(s => s.Code.Equals("AGUM", StringComparison.OrdinalIgnoreCase)) ?? false;
            var titleY = Math.Max(chainY + 2.0, maxY) + TitleGapM;
            Text(OfficeTypicalSheet.SectionLabel(input.Pick.Station), 0, titleY, 0.3, "HW-TYP-TEXT", AttachmentPoint.BaseCenter);
            Text(OfficeTypicalSheet.RoadTitle(input.Pick.AlignmentName, withAgum), 0, titleY + 0.75, 0.4, "HW-TYP-TEXT", AttachmentPoint.BaseCenter);
            Seg(-6.0, titleY + 0.62, 6.0, titleY + 0.62, "HW-TYP-TEXT");
            Seg(-6.0, titleY + 0.67, 6.0, titleY + 0.67, "HW-TYP-TEXT");
            Text("חתך אופייני לאזור", 0, titleY + 1.45, 0.4, "HW-TYP-TEXT", AttachmentPoint.BaseCenter);
            if (input.DesignSpeedKmh is { } speed)
                Text(FormattableString.Invariant($"מהירות תכן: {speed:0} קמ\"ש"), -0.8, chainY + 0.55, 0.2, "HW-TYP-TEXT", AttachmentPoint.BaseRight);

            // ---- the frame
            var fx0 = minX - FrameMarginM; var fx1 = maxX + FrameMarginM;
            var fy0 = Math.Min(minY, calloutsLow) - FrameMarginM; var fy1 = maxY + FrameMarginM;
            Poly(FrameLayer, true, new[] { (fx0, fy0), (fx1, fy0), (fx1, fy1), (fx0, fy1) });

            var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var y = overlay ? originY + OverlayGapM - fy0 : originY;
            var reference = new BlockReference(new Point3d(originX, y, 0), definitionId)
            {
                LayerId = EnsureLayer(tr, db, DraftedTypicalSectionService.SectionLayer, 7, null),
            };
            var referenceId = model.AppendEntity(reference);
            tr.AddNewlyCreatedDBObject(reference, true);
            reference.RecordGraphicsModified(true);
            return referenceId;
        }

        /// <summary>
        /// A panel's extents from its own frame (the block's polyline on <see cref="FrameLayer"/>), so they are known even
        /// where the reference reports none yet (inside an enclosing transaction, as the MCP code runner runs the tool).
        /// Null when the block has no frame.
        /// </summary>
        internal static Extents3d? FrameExtents(Transaction tr, ObjectId referenceId)
        {
            if (tr.GetObject(referenceId, OpenMode.ForRead) is not BlockReference reference) return null;
            foreach (ObjectId id in (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead))
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Polyline frame || frame.Layer != FrameLayer || !frame.Closed) continue;
                var ext = new Extents3d();
                for (var i = 0; i < frame.NumberOfVertices; i++) ext.AddPoint(frame.GetPoint3dAt(i).TransformBy(reference.BlockTransform));
                return ext;
            }
            return null;
        }

        /// <summary>The office's legend: asphalt solid grey, base courses AR-CONC, the sub-base sand, type-B base hatched.</summary>
        private static (string Layer, string Pattern, double Scale, double AngleDeg) HatchOf(string code)
        {
            var c = code.ToUpperInvariant();
            if (c.Contains("ASF") || c.Contains("ASPH") || c.Contains("BIT")) return ("HW-TYP-ASPH1", "SOLID", 1, 0);
            if (c.Contains("AGUM")) return ("HW-CS-HTCH-3", "AR-SAND", 0.0015, 0);
            if (c.Contains("MAZA-B") || c.Contains("MAZ-B")) return ("HW-CS-HTCH-2", "ANSI31", 0.02, 0);
            if (c.Contains("MAZ")) return ("HW-CS-HTCH-3", "AR-CONC", 0.001, 45);
            return ("EBUD-h-r", "BRASS", 0.02, 2);
        }

        // ------------------------------------------------------------------------------------------- styles

        private sealed record Fonts(ObjectId Hebrew, bool HebrewIsShx, ObjectId Number, ObjectId Axis, ObjectId Fallback)
        {
            /// <summary>
            /// The office's Hebrew SHX (miryl.shx; the firm's 0-miryl.shx where only that is installed), its MONOTXT for
            /// numbers and Times bold for the CL; Arial when a font is missing, the Hebrew then laid out run by run.
            /// </summary>
            public static Fonts Ensure(Transaction tr, Database db)
            {
                var fallback = FirmSectionBlockReader.EnsureTrueTypeStyle(tr, db);
                var hebrewFont = new[] { "miryl.shx", "0-miryl.shx" }.FirstOrDefault(f => Found(db, f));
                var hebrew = hebrewFont != null ? Style(tr, db, "MHD-TYP-MIRYL", hebrewFont, null) : fallback;
                var number = Found(db, "monotxt.shx") ? Style(tr, db, "MHD-TYP-MONOTXT", "monotxt.shx", null) : fallback;
                var axis = Style(tr, db, "MHD-TYP-TIMESBD", "timesbd.ttf", "Times New Roman");
                return new Fonts(hebrew, hebrewFont != null, number, axis, fallback);
            }

            private static bool Found(Database db, string file)
            {
                try { return !string.IsNullOrEmpty(HostApplicationServices.Current.FindFile(file, db, FindFileHint.FontFile)); }
                catch { return false; }
            }

            private static ObjectId Style(Transaction tr, Database db, string name, string file, string? typeface)
            {
                var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (table.Has(name)) return table[name];
                table.UpgradeOpen();
                var style = new TextStyleTableRecord { Name = name, FileName = file };
                if (typeface != null) style.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(typeface, true, false, 0, 0);
                var id = table.Add(style);
                tr.AddNewlyCreatedDBObject(style, true);
                return id;
            }
        }

        /// <summary>The office's m-100: 0.20 text above the line, oblique ticks 0.10, extension 0.05 off the object, two decimals.</summary>
        private static ObjectId EnsureDimStyle(Transaction tr, Database db, ObjectId textStyle)
        {
            const string name = "MHD-TYP-m-100";
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            var byBlock = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            var numbers = Color.FromColorIndex(ColorMethod.ByAci, 7);   // black on white paper, white on a dark screen
            if (table.Has(name))
            {
                // A drawing styled by 2.1.22 has the parts by layer (red on the office's HW-TYP-DIM): set as below.
                var existing = (DimStyleTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
                if (!existing.Dimclrd.IsByBlock || !existing.Dimclre.IsByBlock || existing.Dimclrt.ColorIndex != 7)
                {
                    existing.UpgradeOpen();
                    existing.Dimclrd = byBlock; existing.Dimclre = byBlock; existing.Dimclrt = numbers;
                }
                return table[name];
            }
            table.UpgradeOpen();
            var oblique = ArrowBlock(tr, db, "_OBLIQUE");
            var style = new DimStyleTableRecord
            {
                Name = name, Dimscale = 1, Dimtxt = 0.2, Dimasz = 0.1, Dimsah = true, Dimblk1 = oblique, Dimblk2 = oblique,
                Dimexo = 0.05, Dimexe = 0, Dimgap = 0.1, Dimtad = 1, Dimjust = 0, Dimtih = false, Dimtoh = false,
                Dimatfit = 3, Dimtmove = 1, Dimdec = 2, Dimzin = 0, Dimdsep = '.', Dimtxsty = textStyle,
                // lines by block (the palette's grey for HW-TYP-DIM), the widths in 7 so they read on any background
                Dimclrd = byBlock, Dimclre = byBlock, Dimclrt = numbers,
            };
            var id = table.Add(style);
            tr.AddNewlyCreatedDBObject(style, true);
            return id;
        }

        private static ObjectId ArrowBlock(Transaction tr, Database db, string name)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(name))
            {
                var old = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DIMBLK");
                Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("DIMBLK", name);
                Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("DIMBLK", old ?? "");
            }
            bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return bt.Has(name) ? bt[name] : ObjectId.Null;
        }

        /// <summary>The office symbol as a block of this drawing ("MHD-TYP-SYM-Car..."), defined once from its primitives.</summary>
        private static ObjectId EnsureSymbol(Transaction tr, Database db, BlockTable table, string name)
        {
            // v2: every primitive by block, so the panel's palette colours the symbol (v1 kept the office's red cars)
            var blockName = "MHD-TYP-SYM2-" + name;
            if (table.Has(blockName)) return table[blockName];
            var def = new BlockTableRecord { Name = blockName, Origin = Point3d.Origin };
            var id = table.Add(def);
            tr.AddNewlyCreatedDBObject(def, true);
            var zero = db.LayerZero;
            void Put(Entity e, int color)
            {
                e.LayerId = zero;
                e.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                def.AppendEntity(e);
                tr.AddNewlyCreatedDBObject(e, true);
            }
            foreach (var p in OfficeTypicalSheet.Symbol(name))
            {
                try
                {
                    switch (p.Type)
                    {
                        case "L": Put(new Line(new Point3d(p.P[0], p.P[1], 0), new Point3d(p.P[2], p.P[3], 0)), p.Color); break;
                        case "A": Put(new Arc(new Point3d(p.P[0], p.P[1], 0), p.P[2], p.P[3], p.P[4]), p.Color); break;
                        case "C": Put(new Circle(new Point3d(p.P[0], p.P[1], 0), Vector3d.ZAxis, p.P[2]), p.Color); break;
                        case "S": Put(new Solid(new Point3d(p.P[0], p.P[1], 0), new Point3d(p.P[2], p.P[3], 0), new Point3d(p.P[4], p.P[5], 0), new Point3d(p.P[6], p.P[7], 0)), p.Color); break;
                        case "P":
                            var pl = new Polyline { Closed = p.Closed };
                            for (var i = 0; i + 2 < p.P.Length; i += 3) pl.AddVertexAt(i / 3, new Point2d(p.P[i], p.P[i + 1]), p.P[i + 2], p.Width, p.Width);
                            if (pl.NumberOfVertices >= 2) Put(pl, p.Color); else pl.Dispose();
                            break;
                        case "H":
                            var h = new Hatch();
                            Put(h, p.Color);
                            h.SetHatchPattern(HatchPatternType.PreDefined, p.Pattern == "SOLID" || string.IsNullOrEmpty(p.Pattern) ? "SOLID" : "SOLID");
                            foreach (var loop in p.Loops ?? Array.Empty<double[]>())
                            {
                                if (loop.Length < 9) continue;
                                var pts = new Point2dCollection(); var bulges = new DoubleCollection();
                                for (var i = 0; i + 2 < loop.Length; i += 3) { pts.Add(new Point2d(loop[i], loop[i + 1])); bulges.Add(loop[i + 2]); }
                                pts.Add(pts[0]); bulges.Add(0);
                                h.AppendLoop(HatchLoopTypes.Polyline, pts, bulges);
                            }
                            h.EvaluateHatch(true);
                            break;
                    }
                }
                catch { /* one primitive less */ }
            }
            return id;
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db, string name, short color, string? linetype)
        {
            var id = TypicalSectionPicker.EnsureLayer(tr, db, name, color);
            if (linetype != null)
            {
                var lt = EnsureLinetype(tr, db, linetype);
                var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!lt.IsNull && rec.LinetypeObjectId == db.ContinuousLinetype) { rec.UpgradeOpen(); rec.LinetypeObjectId = lt; }
            }
            return id;
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
