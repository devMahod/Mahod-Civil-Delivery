using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Region = Autodesk.AutoCAD.DatabaseServices.Region;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Read-only, occurrence-aware traversal. Only clones are transformed or written.</summary>
internal sealed class InfrastructureDrawingService : IDisposable
{
    internal sealed record Measured(InfrastructureLine Line, Entity Geometry)
    {
        internal Database? SourceDatabase { get; init; }
        internal ObjectId SourceId { get; init; }
        internal Matrix3d SourceTransform { get; init; } = Matrix3d.Identity;
        /// <summary>Counted, but its geometry cannot be written to the audit DWG; listed in not-copied.txt instead.</summary>
        internal string? ExportSkipReason { get; init; }
        /// <summary>
        /// A hatch area recovered from its boundary: the shape and its plane. Its region is built inside the audit DWG at
        /// export, because a region built in memory belongs to the host drawing (eWrongDatabase on append, live 2026-11-06).
        /// </summary>
        internal NetTopologySuite.Geometries.Geometry? RecoveredShape { get; init; }
        internal Vector3d RecoveredNormal { get; init; } = Vector3d.ZAxis;
        internal double RecoveredElevation { get; init; }
    }
    internal List<Measured> Measurements { get; } = new();
    internal List<string> Findings { get; } = new();
    internal string Drawing { get; }
    internal string DrawingHash { get; }
    private readonly double _metres;
    internal double MetresPerHostUnit => _metres;
    private readonly Dictionary<string, int> _unsupported = new();
    private readonly Dictionary<(string Layer, string Source), (string Topic, string Evidence, string Code)> _classifications = new();
    private int _symbolBlocks;
    internal InfrastructureDrawingService(Database db, string drawing, double metresPerHostUnit)
    {
        if (!double.IsFinite(metresPerHostUnit) || metresPerHostUnit <= 0) throw new ArgumentOutOfRangeException(nameof(metresPerHostUnit));
        Drawing = drawing;
        DrawingHash = MahodAI.CivilDelivery.Shared.ArtifactHash.Sha256OfFile(drawing);
        _metres = metresPerHostUnit;
        using var tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Walk(tr, bt[BlockTableRecord.ModelSpace], Matrix3d.Identity, drawing, "", "0", new HashSet<ObjectId>(), 0);
        foreach (var omitted in _unsupported.OrderBy(p => p.Key)) Findings.Add($"סוג גאומטרי שלא נמדד: {omitted.Key} ({omitted.Value:N0} מופעים)");
        if (_symbolBlocks > 0) Findings.Add($"{_symbolBlocks:N0} בלוקים נספרו לפי שם בלוק ושכבה. בשרטוט הייצוא מופיעים סימוני מיקום; הגדרות הבלוק המלאות נשארות במקור.");
        tr.Commit();
    }
    private void Walk(Transaction tr, ObjectId blockId, Matrix3d transform, string source, string chain,
        string inheritedLayer, HashSet<ObjectId> ancestors, int depth)
    {
        if (depth > 32 || !ancestors.Add(blockId)) { Findings.Add("מקור מחזורי או עמוק מדי: " + source); return; }
        try
        {
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference && (!block.IsResolved || block.IsUnloaded))
            { Findings.Add("מקור לא טעון: " + block.PathName); return; }
            foreach (ObjectId id in block)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                var layer = entity.Layer == "0" ? inheritedLayer : entity.Layer;
                var handle = chain + entity.Handle;
                if (entity is BlockReference reference)
                {
                    // XREFs carry references whose definition did not survive (null / erased): one finding, the scan goes on.
                    if (reference.BlockTableRecord.IsNull || reference.BlockTableRecord.IsErased ||
                        tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) is not BlockTableRecord child)
                    { _unsupported["BlockReference ללא הגדרת בלוק"] = _unsupported.GetValueOrDefault("BlockReference ללא הגדרת בלוק") + 1; continue; }
                    // One placed symbol is one count; its internal outline is never pipe length or paving area.
                    if (!child.IsFromExternalReference)
                    {
                        if (reference is MInsertBlock) { _unsupported["MInsertBlock — מערך בלוקים"] = _unsupported.GetValueOrDefault("MInsertBlock — מערך בלוקים") + 1; continue; }
                        var definition = reference.IsDynamicBlock && !reference.DynamicBlockTableRecord.IsNull
                            ? (BlockTableRecord)tr.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead) : child;
                        var normalized = InfrastructureLayerNames.Normalize(layer + " " + definition.Name);
                        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"(?:^| )(TITLE|FRAME|LEGEND|TEXT|LABEL|DIM|כותרת|מקרא)(?: |$)")) continue;
                        _symbolBlocks++;
                        Add(tr, entity, new DBPoint(reference.Position.TransformBy(transform)), layer, source, handle, 0, 0, 0, 1, "בלוק " + definition.Name);
                        continue;
                    }
                    var childSource = child.IsFromExternalReference ? child.PathName : source;
                    if (child.IsFromExternalReference && (child.Name.Contains("LEGEND", StringComparison.OrdinalIgnoreCase) ||
                        childSource.Contains("LEGEND", StringComparison.OrdinalIgnoreCase)))
                    { Findings.Add("מקרא הוחרג: " + childSource); continue; }
                    Walk(tr, reference.BlockTableRecord, transform * reference.BlockTransform, childSource,
                        handle + "/", layer, ancestors, depth + 1);
                    continue;
                }
                if (entity is Hatch hatch)
                {
                    var copy = (Hatch)hatch.Clone();
                    double area;
                    try { copy.Associative = false; copy.TransformBy(transform); area = copy.Area * _metres * _metres; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        // AutoCAD cannot compute the area of a self-touching / self-intersecting hatch. Measure its boundary
                        // instead: silently dropping it lost ~59,000 m² of carriageway on a real interchange plan (2026-11-06).
                        RecoverHatchArea(tr, entity, copy, layer, source, handle, ex.ErrorStatus.ToString());
                        copy.Dispose();
                        continue;
                    }
                    catch (System.Exception ex) { copy.Dispose(); Unmeasured(layer, source, handle, ex.Message); continue; }
                    if (!double.IsFinite(area) || area <= 0) { copy.Dispose(); Unmeasured(layer, source, handle, "שטח Hatch אינו חיובי"); continue; }
                    Add(tr, entity, copy, layer, source, handle, 0, area, 0, 0, "שטחים", transform);
                    continue;
                }
                if (entity is Solid3d solid)
                {
                    var copy = (Solid3d)solid.Clone();
                    try { copy.TransformBy(transform); var volume = copy.MassProperties.Volume * Math.Pow(_metres, 3); if (!double.IsFinite(volume) || volume <= 0) throw new InvalidOperationException("נפח גוף אינו חיובי"); Add(tr, entity, copy, layer, source, handle, 0, 0, volume, 0, "גופים", transform); }
                    catch (System.Exception ex) { copy.Dispose(); Findings.Add($"לא נמדד גוף {source} / {handle}: {ex.Message}"); }
                    continue;
                }
                if (entity is not (Polyline or Polyline2d or Polyline3d or Line or Arc or Circle or Ellipse))
                {
                    if (entity is Curve || entity.GetType().Name.Contains("Proxy", StringComparison.OrdinalIgnoreCase))
                        _unsupported[entity.GetType().Name] = _unsupported.GetValueOrDefault(entity.GetType().Name) + 1;
                    continue;
                }
                try
                {
                    var curve = CopyGeometry((Curve)entity, tr);
                    try
                    {
                        curve.TransformBy(transform);
                        var length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                        if (!double.IsFinite(length) || length <= 1e-9) { curve.Dispose(); continue; }
                        Add(tr, entity, curve, layer, source, handle, length * _metres, ClosedArea(curve) * _metres * _metres, 0, 0, "");
                    }
                    catch { curve.Dispose(); throw; }
                }
                catch (System.Exception ex) { Findings.Add($"לא נמדד {source} / {handle}: {ex.Message}"); }
            }
        }
        finally { ancestors.Remove(blockId); }
    }
    private void Add(Transaction tr, Entity original, Entity copy, string layer, string source, string handle,
        double length, double area, double volume, double count, string part, Matrix3d? sourceTransform = null, string? exportSkip = null,
        Hatch? recoveredFrom = null, NetTopologySuite.Geometries.Geometry? recoveredShape = null)
    {
        if (!_classifications.TryGetValue((layer, source), out var classification))
        {
            var description = ((LayerTableRecord)tr.GetObject(original.LayerId, OpenMode.ForRead)).Description;
            _classifications[(layer, source)] = classification = InfrastructureBoq.Classify(layer, source, description);
        }
        var safe = string.Concat(layer.Split('|').Last().Select(c => "<>/\\\":;?*|,=".Contains(c) ? '_' : c));
        var sourceKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source + "\n" + part)))[..8];
        var outputLayer = "BOQ_" + classification.Code + "__" + sourceKey + "__" + safe;
        if (outputLayer.Length > 240) outputLayer = outputLayer[..220] + "_" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(layer)))[..16];
        Measurements.Add(new(InfrastructureBoq.Describe(new InfrastructureLine(source, handle, layer, original.GetType().Name, length,
            original is Curve c && c.Closed, classification.Topic, classification.Evidence, outputLayer)
            { AreaSquareMetres = area, VolumeCubicMetres = volume, CountUnits = count, GroupPart = part }), copy)
            { SourceDatabase = sourceTransform.HasValue ? original.Database : null, SourceId = original.ObjectId, SourceTransform = sourceTransform ?? Matrix3d.Identity,
              ExportSkipReason = exportSkip, RecoveredShape = recoveredShape,
              RecoveredNormal = recoveredFrom?.Normal ?? Vector3d.ZAxis, RecoveredElevation = recoveredFrom?.Elevation ?? 0 });
    }
    /// <summary>A hatch area the scan had to work around: recovered from the boundary (AutoCAD gave no area), or not measured at all.</summary>
    internal sealed record AreaIssue(string Layer, string Source, string Handle, double AreaSquareMetres, string Reason)
    {
        internal string LayerName => Layer.Split('|').Last();
    }
    /// <summary>Hatches whose area AutoCAD could not compute and the scan measured from their boundary loops (counted; review).</summary>
    internal List<AreaIssue> RecoveredAreas { get; } = new();
    /// <summary>Hatches with no area at all: they are NOT in the bill of quantities, and the window says so.</summary>
    internal List<AreaIssue> UnmeasuredAreas { get; } = new();

    private void Unmeasured(string layer, string source, string handle, string reason)
    {
        UnmeasuredAreas.Add(new(layer, source, handle, 0, reason));
        Findings.Add($"לא נמדד שטח Hatch — לא נכלל בכתב הכמויות: {layer.Split('|').Last()} / {handle} ({Path.GetFileName(source)}): {reason}");
    }

    private void RecoverHatchArea(Transaction tr, Entity original, Hatch hatch, string layer, string source, string handle, string error)
    {
        string refusal;
        try
        {
            var tolerance = 0.001 / _metres;   // 1 mm chord error on curved edges
            var loops = new List<IReadOnlyList<(double X, double Y)>>();
            for (var i = 0; i < hatch.NumberOfLoops; i++) loops.Add(LoopPoints(hatch.GetLoopAt(i), tolerance));
            if (HatchBoundaryArea.TryMeasure(loops, hatch.HatchStyle.ToString(), out var result, out refusal))
            {
                var expected = result!.Area * _metres * _metres;
                if (expected < 0.01)
                {
                    // A collapsed sliver: no quantity, nothing to complete by hand.
                    Findings.Add($"Hatch ללא שטח ממשי (AutoCAD: {error}) — {layer.Split('|').Last()} / {handle}: {expected:N4} מ״ר, לא נכלל");
                    return;
                }
                // The audit DWG carries the same shape as a region when AutoCAD can build one; its area then is the
                // quantity, so the DWG check still holds. When it cannot (dense or self-touching rings on the largest
                // carriageway hatches, live 2026-11-06), the boundary area is still counted and the shape is listed
                // in not-copied.txt, never dropped from the bill of quantities.
                var region = BuildRegion(result.Shape, hatch.Normal, hatch.Elevation);
                var area = region == null ? double.NaN : region.Area * _metres * _metres;
                region?.Dispose();
                string? exportSkip = null;
                if (!double.IsFinite(area) || Math.Abs(area - expected) > Math.Max(1e-6, expected * 1e-6))
                {
                    area = expected;
                    exportSkip = "שטח שחושב מגבולות Hatch שאינו ניתן לשחזור כאזור בשרטוט";
                }
                Add(tr, original, (Entity)hatch.Clone(), layer, source, handle, 0, area, 0, 0, "שטחים", exportSkip: exportSkip,
                    recoveredFrom: hatch, recoveredShape: result.Shape);
                RecoveredAreas.Add(new(layer, source, handle, area, error));
                Findings.Add($"שטח Hatch חושב מגבולותיו (AutoCAD אינו מחשב לו שטח: {error}) — {layer.Split('|').Last()} / {handle}: {area:N2} מ״ר. נכלל בכמות — לבדיקה.");
                return;
            }
        }
        catch (System.Exception ex) { refusal = ex.Message; }
        Unmeasured(layer, source, handle, $"{error}; {refusal}");
    }

    /// <summary>A hatch loop as a ring of points in the hatch plane; arcs and curves are densified to <paramref name="tolerance"/>.</summary>
    private static IReadOnlyList<(double X, double Y)> LoopPoints(HatchLoop loop, double tolerance)
    {
        var points = new List<(double X, double Y)>();
        if (loop.IsPolyline)
        {
            var vertices = new List<BulgeVertex>();
            foreach (BulgeVertex v in loop.Polyline) vertices.Add(v);
            for (var i = 0; i < vertices.Count; i++)
            {
                var a = vertices[i].Vertex; var b = vertices[(i + 1) % vertices.Count].Vertex; var bulge = vertices[i].Bulge;
                points.Add((a.X, a.Y));
                if (Math.Abs(bulge) < 1e-12 || a.GetDistanceTo(b) < 1e-12) continue;
                // Bulge arc: sweep = 4·atan(bulge), centre on the left of the chord for a positive bulge.
                var chord = b - a; var c = chord.Length;
                var radius = c * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
                var offset = c * (1 - bulge * bulge) / (4 * bulge);
                var mid = new Point2d((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                var centre = mid + new Vector2d(-chord.Y, chord.X) / c * offset;
                var start = Math.Atan2(a.Y - centre.Y, a.X - centre.X);
                var sweep = 4 * Math.Atan(bulge);
                var n = ArcSegments(radius, Math.Abs(sweep), tolerance);
                for (var k = 1; k < n; k++)
                {
                    var t = start + sweep * k / n;
                    points.Add((centre.X + radius * Math.Cos(t), centre.Y + radius * Math.Sin(t)));
                }
            }
            return points;
        }
        foreach (Curve2d curve in loop.Curves)
        {
            var interval = curve.GetInterval();
            double lo = interval.LowerBound, hi = interval.UpperBound;
            var first = curve.EvaluatePoint(lo);
            points.Add((first.X, first.Y));
            if (curve is LineSegment2d) continue;
            var n = curve is CircularArc2d arc
                ? ArcSegments(arc.Radius, Math.Abs(arc.EndAngle - arc.StartAngle), tolerance)
                : Math.Clamp((int)Math.Ceiling(curve.GetLength(lo, hi) / (50 * tolerance)), 8, 4096);
            for (var k = 1; k < n; k++) { var p = curve.EvaluatePoint(lo + (hi - lo) * k / n); points.Add((p.X, p.Y)); }
        }
        return points;
    }

    private static int ArcSegments(double radius, double sweep, double tolerance)
    {
        if (!(radius > tolerance)) return 2;
        var step = 2 * Math.Acos(1 - tolerance / radius);
        return Math.Clamp((int)Math.Ceiling(sweep / step), 2, 4096);
    }

    /// <summary>The recovered shape as one region in the hatch plane (outer rings minus their holes).</summary>
    private static Region? BuildRegion(NetTopologySuite.Geometries.Geometry shape, Vector3d normal, double elevation)
    {
        Region? total = null;
        try
        {
            foreach (var (outer, holes) in HatchBoundaryArea.Polygons(shape))
            {
                var region = RingRegion(outer, normal, elevation);
                if (region == null) { total?.Dispose(); return null; }
                foreach (var hole in holes)
                {
                    var cut = RingRegion(hole, normal, elevation);
                    if (cut == null) { region.Dispose(); total?.Dispose(); return null; }
                    region.BooleanOperation(BooleanOperationType.BoolSubtract, cut);
                    cut.Dispose();
                }
                if (total == null) total = region;
                else { total.BooleanOperation(BooleanOperationType.BoolUnite, region); region.Dispose(); }
            }
            return total;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { total?.Dispose(); return null; }
    }

    private static Region? RingRegion(IReadOnlyList<(double X, double Y)> ring, Vector3d normal, double elevation)
    {
        using var outline = new Polyline(ring.Count) { Normal = normal, Elevation = elevation };
        for (var i = 0; i < ring.Count; i++) outline.AddVertexAt(i, new Point2d(ring[i].X, ring[i].Y), 0, 0, 0);
        outline.Closed = true;
        var regions = Region.CreateFromCurves(new DBObjectCollection { outline });
        if (regions.Count == 1) return (Region)regions[0];
        foreach (DBObject r in regions) r.Dispose();
        return null;
    }

    /// <summary>A new solid hatch in <paramref name="model"/> with the source's boundary loops; null when a loop cannot be copied.</summary>
    private static Hatch? RebuildHatch(Hatch source, BlockTableRecord model, Transaction tr)
    {
        var hatch = new Hatch();
        try
        {
            hatch.Normal = source.Normal; hatch.Elevation = source.Elevation;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.HatchStyle = source.HatchStyle;
            model.AppendEntity(hatch); tr.AddNewlyCreatedDBObject(hatch, true);
            for (var i = 0; i < source.NumberOfLoops; i++)
            {
                var loop = source.GetLoopAt(i);
                if (loop.IsPolyline)
                {
                    var points = new Point2dCollection(); var bulges = new DoubleCollection();
                    foreach (BulgeVertex v in loop.Polyline) { points.Add(v.Vertex); bulges.Add(v.Bulge); }
                    hatch.AppendLoop(loop.LoopType, points, bulges);
                    continue;
                }
                var edges = new IntegerCollection();
                foreach (Curve2d c in loop.Curves)
                    edges.Add(c switch { LineSegment2d => 1, CircularArc2d => 2, EllipticalArc2d => 3, _ => 4 });
                hatch.AppendLoop(loop.LoopType, loop.Curves, edges);
            }
            hatch.EvaluateHatch(true);
            // The audit drawing never carries an area other than the measured one.
            if (Math.Abs(hatch.Area - source.Area) > Math.Max(1e-7, Math.Abs(source.Area) * 1e-9)) { hatch.Erase(); return null; }
            return hatch;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            if (hatch.ObjectId.IsNull) hatch.Dispose(); else hatch.Erase();
            return null;
        }
    }

    private static double ClosedArea(Curve curve)
    {
        if (!curve.Closed) return 0;
        DBObjectCollection? regions = null;
        try
        {
            using var input = (Curve)curve.Clone();
            regions = Region.CreateFromCurves(new DBObjectCollection { input });
            return regions.Count == 1 && regions[0] is Region r && double.IsFinite(r.Area) ? Math.Abs(r.Area) : 0;
        }
        catch { return 0; }
        finally { if (regions != null) foreach (DBObject r in regions) r.Dispose(); }
    }
    private static Curve CopyGeometry(Curve original, Transaction tr)
    {
        // Fresh geometry carries no source database dictionaries, XDATA, reactors or foreign style IDs.
        // A shallow Clone of an XREF entity can produce an invalid destination DWG even when AppendEntity succeeds.
        switch (original)
        {
            case Line line: return new Line(line.StartPoint, line.EndPoint);
            case Arc arc: return new Arc(arc.Center, arc.Normal, arc.Radius, arc.StartAngle, arc.EndAngle);
            case Circle circle: return new Circle(circle.Center, circle.Normal, circle.Radius);
            case Ellipse ellipse: return new Ellipse(ellipse.Center, ellipse.Normal, ellipse.MajorAxis, ellipse.RadiusRatio, ellipse.StartAngle, ellipse.EndAngle);
            case Polyline pl:
                var copy = new Polyline(pl.NumberOfVertices) { Normal = pl.Normal, Elevation = pl.Elevation, Closed = pl.Closed };
                for (var i = 0; i < pl.NumberOfVertices; i++) copy.AddVertexAt(i, pl.GetPoint2dAt(i), pl.GetBulgeAt(i), pl.GetStartWidthAt(i), pl.GetEndWidthAt(i));
                return copy;
            case Polyline2d pl when pl.PolyType == Poly2dType.SimplePoly:
                var flat = new Polyline { Normal = pl.Normal, Elevation = pl.Elevation, Closed = pl.Closed };
                foreach (ObjectId id in pl)
                {
                    var vertex = (Vertex2d)tr.GetObject(id, OpenMode.ForRead);
                    flat.AddVertexAt(flat.NumberOfVertices, new Point2d(vertex.Position.X, vertex.Position.Y), vertex.Bulge, vertex.StartWidth, vertex.EndWidth);
                }
                return flat;
            case Polyline3d pl when pl.PolyType == Poly3dType.SimplePoly:
                var points = new Point3dCollection();
                foreach (ObjectId id in pl) points.Add(((PolylineVertex3d)tr.GetObject(id, OpenMode.ForRead)).Position);
                return new Polyline3d(Poly3dType.SimplePoly, points, pl.Closed);
            default: throw new InvalidOperationException("Fit/spline legacy polylines require a dedicated exact adapter; not approximated");
        }
    }
    /// <summary>Writes the measured geometry to a metric DWG and verifies it. Returns the entities that could not be copied
    /// (listed beside the DWG); the Excel quantities never depend on this drawing.</summary>
    internal int SaveDrawing(string path, IEnumerable<InfrastructureGroup> included)
    {
        var skipped = new List<(Measured Item, string Reason)>();
        var groups = included.ToDictionary(g => (g.Source, g.Layer, g.GroupPart));
        var selected = Measurements.Where(m => groups.ContainsKey((m.Line.Source, m.Line.Layer, m.Line.GroupPart)))
            .Select(m => m with { Line = InfrastructureBoq.ReviewedLine(m.Line, groups[(m.Line.Source, m.Line.Layer, m.Line.GroupPart)]) }).ToArray();
        using var output = new Database(true, true);
        // A bare database otherwise inherits imperial defaults. Civil 3D can
        // initialize drawing settings from MEASUREMENT when opening this DWG.
        output.Measurement = MeasurementValue.Metric;
        output.Insunits = UnitsValue.Meters;
        output.TileMode = true;
        using (var tr = output.TransactionManager.StartTransaction())
        {
            var layers = (LayerTable)tr.GetObject(output.LayerTableId, OpenMode.ForWrite);
            var bt = (BlockTable)tr.GetObject(output.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            Extents3d? bounds = null;
            foreach (var measured in selected)
            {
                if (measured.ExportSkipReason != null) { skipped.Add((measured, measured.ExportSkipReason)); continue; }
                var name = measured.Line.OutputLayer;
                if (!layers.Has(name))
                {
                    short colour = name.Split(new[] { "__" }, StringSplitOptions.None)[0] switch
                    {
                        "BOQ_WATER" => 5, "BOQ_SEWER" => 3, "BOQ_DRAINAGE" => 4,
                        "BOQ_GAS" => 2, "BOQ_ELECTRIC" => 1, "BOQ_TELECOM" => 6, _ => 7
                    };
                    var layer = new LayerTableRecord { Name = name,
                        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colour) };
                    layers.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
                }
                Entity clone;
                if (measured.RecoveredShape != null)
                {
                    // Built while the audit DWG is the working database, so the region belongs to it.
                    var working = HostApplicationServices.WorkingDatabase;
                    Region? rebuilt;
                    try { HostApplicationServices.WorkingDatabase = output; rebuilt = BuildRegion(measured.RecoveredShape, measured.RecoveredNormal, measured.RecoveredElevation); }
                    finally { HostApplicationServices.WorkingDatabase = working; }
                    if (rebuilt == null) { skipped.Add((measured, "לא ניתן לבנות אזור מהגבול בשרטוט הייצוא")); continue; }
                    clone = rebuilt;
                }
                else if (measured.SourceDatabase != null)
                {
                    // Native cross-database cloning translates the Hatch/ACIS symbol references; a shallow Clone cannot.
                    // Some XREF hatches refuse it (eInvalidKey on a 40-XREF sheet, 2026-11-05): skip and list, never abort.
                    try
                    {
                        var mapping = new IdMapping();
                        measured.SourceDatabase.WblockCloneObjects(new ObjectIdCollection { measured.SourceId }, model.ObjectId, mapping, DuplicateRecordCloning.Ignore, false);
                        clone = (Entity)tr.GetObject(mapping[measured.SourceId].Value, OpenMode.ForWrite);
                        if (clone is Hatch copiedHatch) copiedHatch.Associative = false;
                        clone.TransformBy(measured.SourceTransform);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        // XREF hatches mostly refuse the native clone; rebuild the same boundary loops as a solid hatch
                        // (already in host coordinates), so the audit drawing still carries every measured area.
                        var rebuilt = measured.Geometry is Hatch source ? RebuildHatch(source, model, tr) : null;
                        if (rebuilt == null) { skipped.Add((measured, ex.ErrorStatus.ToString())); continue; }
                        clone = rebuilt;
                    }
                }
                else clone = (Entity)measured.Geometry.Clone();
                clone.SetDatabaseDefaults(output);
                if (_metres != 1) clone.TransformBy(Matrix3d.Scaling(_metres, Point3d.Origin));
                try { if (measured.SourceDatabase == null) { model.AppendEntity(clone); tr.AddNewlyCreatedDBObject(clone, true); } }
                catch (System.Exception ex) { clone.Dispose(); throw new InvalidOperationException("לא ניתן לייצא " + measured.Line.EntityType + " / " + measured.Line.Handle, ex); }
                clone.LayerId = layers[name];
                var extents = clone.GeometricExtents;
                if (bounds is { } accumulated) { accumulated.AddExtents(extents); bounds = accumulated; }
                else bounds = extents;
            }
            if (bounds is { } drawingBounds)
            {
                var viewports = (ViewportTable)tr.GetObject(output.ViewportTableId, OpenMode.ForRead);
                var view = (ViewportTableRecord)tr.GetObject(viewports["*Active"], OpenMode.ForWrite);
                view.ViewDirection = Vector3d.ZAxis; view.Target = Point3d.Origin; view.ViewTwist = 0;
                view.CenterPoint = new Point2d((drawingBounds.MinPoint.X + drawingBounds.MaxPoint.X) / 2,
                    (drawingBounds.MinPoint.Y + drawingBounds.MaxPoint.Y) / 2);
                view.Height = Math.Max(1, Math.Max(drawingBounds.MaxPoint.Y - drawingBounds.MinPoint.Y,
                    (drawingBounds.MaxPoint.X - drawingBounds.MinPoint.X) / 1.5)) * 1.15;
            }
            tr.Commit();
        }
        output.UpdateExt(true);
        output.SaveAs(path, DwgVersion.Current);
        using (var verification = new Database(false, true))
        {
            verification.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
            verification.CloseInput(true);
            if (verification.Insunits != UnitsValue.Meters || verification.Measurement != MeasurementValue.Metric)
                throw new InvalidOperationException("Output DWG did not preserve metric unit metadata; do not use this drawing");
            using var tr = verification.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(verification.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var actual = new Dictionary<string, (int Count, double Length, double Area, double Volume, double Units)>();
            foreach (ObjectId id in model)
            {
                var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
                var value = actual.GetValueOrDefault(entity.Layer);
                var length = entity is Curve curve ? curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam) : 0;
                var area = entity is Curve closed ? ClosedArea(closed) : entity is Hatch hatch ? hatch.Area : entity is Region region ? region.Area : 0;
                actual[entity.Layer] = (value.Count + 1, value.Length + length, value.Area + area,
                    value.Volume + (entity is Solid3d solid ? solid.MassProperties.Volume : 0), value.Units + (entity is DBPoint ? 1 : 0));
            }
            var omitted = skipped.Select(s => s.Item).ToHashSet();
            // Counted outlines (a closed manhole curve = 1 unit) stay curves in the DWG; only block markers are points.
            var expected = selected.Where(m => !omitted.Contains(m)).GroupBy(m => m.Line.OutputLayer)
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Length: g.Sum(m => m.Line.LengthMetres), Area: g.Sum(m => m.Line.AreaSquareMetres), Volume: g.Sum(m => m.Line.VolumeCubicMetres), Units: g.Sum(m => m.Geometry is DBPoint ? m.Line.CountUnits : 0)));
            static bool Same(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-7, Math.Abs(b) * 1e-8);
            if (actual.Count != expected.Count || expected.Any(e => !actual.TryGetValue(e.Key, out var a) || a.Count != e.Value.Count ||
                !Same(a.Length, e.Value.Length) || !Same(a.Area, e.Value.Area) || !Same(a.Volume, e.Value.Volume) || !Same(a.Units, e.Value.Units)))
                throw new InvalidOperationException("Output DWG did not preserve the measured lengths, areas, volumes and counts");
        }
        File.WriteAllText(path + ".sources.json", JsonSerializer.Serialize(selected
            .Select(m => m.Line), new JsonSerializerOptions { WriteIndented = true }));
        if (skipped.Count > 0)
            File.WriteAllLines(path + ".not-copied.txt", skipped.Select(s => string.Join(" | ", s.Item.Line.Source, s.Item.Line.Handle, s.Item.Line.Layer, s.Item.Line.EntityType, s.Reason)));
        return skipped.Count;
    }
    public void Dispose() { foreach (var m in Measurements) m.Geometry.Dispose(); }
}
