using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Read-only, occurrence-aware traversal. Only clones are transformed or written.</summary>
internal sealed class InfrastructureDrawingService : IDisposable
{
    internal sealed record Measured(InfrastructureLine Line, Curve Geometry);
    internal List<Measured> Measurements { get; } = new();
    internal List<string> Findings { get; } = new();
    internal string Drawing { get; }
    internal string DrawingHash { get; }
    private readonly double _metres;
    internal double MetresPerHostUnit => _metres;
    private readonly Dictionary<string, int> _unsupported = new();
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
        foreach (var omitted in _unsupported.OrderBy(p => p.Key)) Findings.Add($"סוג גאומטרי שלא נמדד במסלול אורכי קווים: {omitted.Key} ({omitted.Value:N0} מופעים)");
        if (_symbolBlocks > 0) Findings.Add($"{_symbolBlocks:N0} בלוקים שאינם XREF לא נמדדו כאורך צינור. סמלים, עמודים ושוחות מחייבים מדידת ספירה נפרדת.");
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
                    // Symbol outlines are not pipe centre lines. Only external drawing references are traversed.
                    if (!child.IsFromExternalReference) { _symbolBlocks++; continue; }
                    var childSource = child.IsFromExternalReference ? child.PathName : source;
                    if (child.IsFromExternalReference && (child.Name.Contains("LEGEND", StringComparison.OrdinalIgnoreCase) ||
                        childSource.Contains("LEGEND", StringComparison.OrdinalIgnoreCase)))
                    { Findings.Add("מקרא הוחרג: " + childSource); continue; }
                    Walk(tr, reference.BlockTableRecord, transform * reference.BlockTransform, childSource,
                        handle + "/", layer, ancestors, depth + 1);
                    continue;
                }
                if (entity is not (Polyline or Polyline2d or Polyline3d or Line or Arc))
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
                        var classification = InfrastructureBoq.Classify(layer, source);
                        // Keep the original layer and source separation. A topic is a proposal, never a payable item.
                        var leaf = layer.Split('|').Last();
                        var safe = string.Concat(leaf.Select(c => "<>/\\\":;?*|,=".Contains(c) ? '_' : c));
                        var sourceKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..8];
                        var outputLayer = "BOQ_" + classification.Code + "__" + sourceKey + "__" + safe;
                        if (outputLayer.Length > 240) outputLayer = outputLayer[..220] + "_" +
                            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(layer)))[..16];
                        Measurements.Add(new(new InfrastructureLine(source, handle, layer, entity.GetType().Name,
                            length * _metres, curve.Closed, classification.Topic, classification.Evidence, outputLayer), curve));
                    }
                    catch { curve.Dispose(); throw; }
                }
                catch (System.Exception ex) { Findings.Add($"לא נמדד {source} / {handle}: {ex.Message}"); }
            }
        }
        finally { ancestors.Remove(blockId); }
    }
    private static Curve CopyGeometry(Curve original, Transaction tr)
    {
        // Fresh geometry carries no source database dictionaries, XDATA, reactors or foreign style IDs.
        // A shallow Clone of an XREF entity can produce an invalid destination DWG even when AppendEntity succeeds.
        switch (original)
        {
            case Line line: return new Line(line.StartPoint, line.EndPoint);
            case Arc arc: return new Arc(arc.Center, arc.Normal, arc.Radius, arc.StartAngle, arc.EndAngle);
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
        var keys = included.Select(g => (g.Source, g.Layer)).ToHashSet();
        using var output = new Database(true, true);
        output.Insunits = UnitsValue.Meters;
        using (var tr = output.TransactionManager.StartTransaction())
        {
            var layers = (LayerTable)tr.GetObject(output.LayerTableId, OpenMode.ForWrite);
            var bt = (BlockTable)tr.GetObject(output.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            foreach (var measured in Measurements.Where(m => keys.Contains((m.Line.Source, m.Line.Layer))))
            {
                var name = measured.Line.OutputLayer;
                if (!layers.Has(name)) { var layer = new LayerTableRecord { Name = name }; layers.Add(layer); tr.AddNewlyCreatedDBObject(layer, true); }
                var clone = (Curve)measured.Geometry.Clone();
                clone.SetDatabaseDefaults(output);
                if (_metres != 1) clone.TransformBy(Matrix3d.Scaling(_metres, Point3d.Origin));
                clone.Layer = name;
                model.AppendEntity(clone); tr.AddNewlyCreatedDBObject(clone, true);
            }
            tr.Commit();
        }
        output.SaveAs(path, DwgVersion.Current);
        using (var verification = new Database(false, true))
        {
            verification.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
            verification.CloseInput(true);
            using var tr = verification.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(verification.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var actual = new Dictionary<string, (int Count, double Length)>();
            foreach (ObjectId id in model)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Curve curve) throw new InvalidOperationException("Unexpected entity in output DWG");
                var value = actual.GetValueOrDefault(curve.Layer);
                actual[curve.Layer] = (value.Count + 1, value.Length + curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam));
            }
            var expected = Measurements.Where(m => keys.Contains((m.Line.Source, m.Line.Layer))).GroupBy(m => m.Line.OutputLayer)
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Length: g.Sum(m => m.Line.LengthMetres)));
            if (actual.Count != expected.Count || expected.Any(e => !actual.TryGetValue(e.Key, out var a) || a.Count != e.Value.Count ||
                Math.Abs(a.Length - e.Value.Length) > Math.Max(1e-7, e.Value.Length * 1e-10)))
                throw new InvalidOperationException("Output DWG did not preserve measured counts and lengths; do not use this drawing");
        }
        File.WriteAllText(path + ".sources.json", JsonSerializer.Serialize(Measurements.Where(m => keys.Contains((m.Line.Source, m.Line.Layer)))
            .Select(m => m.Line), new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Dispose() { foreach (var m in Measurements) m.Geometry.Dispose(); }
}
