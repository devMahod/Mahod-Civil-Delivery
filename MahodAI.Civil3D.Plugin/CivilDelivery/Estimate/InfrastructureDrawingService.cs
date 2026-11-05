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
                    var child = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                    // One placed symbol is one count; its internal outline is never pipe length or paving area.
                    if (!child.IsFromExternalReference)
                    {
                        if (reference is MInsertBlock) { _unsupported["MInsertBlock — מערך בלוקים"] = _unsupported.GetValueOrDefault("MInsertBlock — מערך בלוקים") + 1; continue; }
                        var definition = (BlockTableRecord)tr.GetObject(reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead);
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
                    try { copy.Associative = false; copy.TransformBy(transform); var area = copy.Area * _metres * _metres;
                        if (!double.IsFinite(area) || area <= 0) throw new InvalidOperationException("שטח Hatch אינו חיובי");
                        Add(tr, entity, copy, layer, source, handle, 0, area, 0, 0, "שטחים", transform); }
                    catch (System.Exception ex) { copy.Dispose(); Findings.Add($"לא נמדד שטח Hatch {source} / {handle}: {ex.Message}"); }
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
        double length, double area, double volume, double count, string part, Matrix3d? sourceTransform = null)
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
        Measurements.Add(new(new InfrastructureLine(source, handle, layer, original.GetType().Name, length,
            original is Curve c && c.Closed, classification.Topic, classification.Evidence, outputLayer)
            { AreaSquareMetres = area, VolumeCubicMetres = volume, CountUnits = count, GroupPart = part }, copy)
            { SourceDatabase = sourceTransform.HasValue ? original.Database : null, SourceId = original.ObjectId, SourceTransform = sourceTransform ?? Matrix3d.Identity });
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
    internal void SaveDrawing(string path, IEnumerable<InfrastructureGroup> included)
    {
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
                if (measured.SourceDatabase != null)
                {
                    // Native cross-database cloning translates the Hatch/ACIS symbol references; a shallow Clone cannot.
                    var mapping = new IdMapping();
                    measured.SourceDatabase.WblockCloneObjects(new ObjectIdCollection { measured.SourceId }, model.ObjectId, mapping, DuplicateRecordCloning.Ignore, false);
                    clone = (Entity)tr.GetObject(mapping[measured.SourceId].Value, OpenMode.ForWrite);
                    if (clone is Hatch copiedHatch) copiedHatch.Associative = false;
                    clone.TransformBy(measured.SourceTransform);
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
            var expected = selected.GroupBy(m => m.Line.OutputLayer)
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Length: g.Sum(m => m.Line.LengthMetres), Area: g.Sum(m => m.Line.AreaSquareMetres), Volume: g.Sum(m => m.Line.VolumeCubicMetres), Units: g.Sum(m => m.Line.CountUnits)));
            static bool Same(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-7, Math.Abs(b) * 1e-8);
            if (actual.Count != expected.Count || expected.Any(e => !actual.TryGetValue(e.Key, out var a) || a.Count != e.Value.Count ||
                !Same(a.Length, e.Value.Length) || !Same(a.Area, e.Value.Area) || !Same(a.Volume, e.Value.Volume) || !Same(a.Units, e.Value.Units)))
                throw new InvalidOperationException("Output DWG did not preserve the measured lengths, areas, volumes and counts");
        }
        File.WriteAllText(path + ".sources.json", JsonSerializer.Serialize(selected
            .Select(m => m.Line), new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Dispose() { foreach (var m in Measurements) m.Geometry.Dispose(); }
}
