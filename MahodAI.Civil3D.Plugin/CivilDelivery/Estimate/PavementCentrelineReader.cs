using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// The centre lines a pavement report's segments are measured along, and the cross-section numbers on them, from the
/// model and every loaded XREF (host coordinates):
/// <list type="bullet">
/// <item>polylines and lines on centre-line layers (Mahod: <c>HW-ALGN</c> in the CL drawing; also AX-, CL, CENTERLINE…),
/// with the numbers written beside them (Mahod: MText on <c>HW-ALGN-SEC-NAME</c> every 20 m);</item>
/// <item>Civil 3D alignments of the host drawing, with the names of their sample lines as section numbers.</item>
/// </list>
/// Read only; nothing in the drawing changes.
/// </summary>
internal static class PavementCentrelineReader
{
    internal sealed record Result(IReadOnlyList<PavementSegmentBands.Centreline> Lines, IReadOnlyList<PavementSegmentBands.SectionLabel> Labels,
        IReadOnlyList<string> Findings);

    // A centre-line layer names the axis itself, as a whole word of the layer name: HW-ALGN, AX-43, AXIS, CENTERLINE, "CL".
    // Live 2026-11-09 (6422 west model, 22 XREFs): a loose match took 6,846 "centre lines" — contours (CL_Cont_Minor),
    // drainage (0-DR-CL), cross-section lines (HW-CS-ALGN), IP tangents — and the section numbers snapped to them.
    private static readonly Regex LineLayer = new(@"(?:^|[-_ .])(?:ALGN|ALIGN|ALIGNMENT|AXIS|AX|CENTER ?LINE|CENTRE ?LINE|ציר)(?:[-_ .]|$)|^C-?L$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotALine = new(@"SEC|NAME|TEXT|TXT|LBL|LABEL|DIM|STA|TICK|NUM|ANNO|CONT|SLOPE|ELEV|HINGE|(?:^|[-_ .])(?:CS|TX|IP|TANG|DR|PRAT|FRAME)(?:[-_ .]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Section numbers are written on their own layer: …SEC-NAME, …SECT, STA, CHAINAGE, חתכים.
    private static readonly Regex LabelLayer = new(@"SEC|SECT|STA|CHAIN|חתך|חתכים", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>A layer whose lines are road centre lines (after the XREF namespace is removed).</summary>
    internal static bool IsCentreLineLayer(string layer) { var l = Canonical(layer); return LineLayer.IsMatch(l) && !NotALine.IsMatch(l); }
    /// <summary>A layer whose numbers are cross-section numbers.</summary>
    internal static bool IsSectionLabelLayer(string layer) { var l = Canonical(layer); return LabelLayer.IsMatch(l) && !NotALabel.IsMatch(l); }
    private static readonly Regex NotALabel = new(@"CONT|ELEV|SLOPE|HINGE|ARROW|DIST", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static Result Read(Database db)
    {
        var lines = new List<PavementSegmentBands.Centreline>();
        var labels = new List<PavementSegmentBands.SectionLabel>();
        var findings = new List<string>();
        using (var tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            Walk(tr, bt[BlockTableRecord.ModelSpace], Matrix3d.Identity, "", new HashSet<ObjectId>(), 0, lines, labels, new DrawingVisibility());
        }
        try { ReadAlignments(db, lines, labels, findings); }
        catch (System.Exception ex) when (ex is System.IO.FileNotFoundException or TypeLoadException or MissingMethodException)
        { /* AutoCAD without Civil 3D: polylines only */ }
        return new Result(lines, labels, findings);
    }

    /// <summary>The layer without its XREF namespace ("X|" or a bound "X$0$").</summary>
    internal static string Canonical(string layer) { var l = layer.Split('|').Last(); var i = l.LastIndexOf("$0$", StringComparison.Ordinal); return i >= 0 ? l[(i + 3)..] : l; }

    private static void Walk(Transaction tr, ObjectId blockId, Matrix3d transform, string prefix, HashSet<ObjectId> ancestors, int depth,
        List<PavementSegmentBands.Centreline> lines, List<PavementSegmentBands.SectionLabel> labels, DrawingVisibility visibility)
    {
        if (depth > 8 || !ancestors.Add(blockId)) return;
        try
        {
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference && (!block.IsResolved || block.IsUnloaded)) return;
            foreach (ObjectId id in block)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                var layer = Canonical(entity.Layer);
                switch (entity)
                {
                    case BlockReference reference:
                        if (reference.BlockTableRecord.IsNull || reference.BlockTableRecord.IsErased) continue;
                        if (tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) is not BlockTableRecord child) continue;
                        // XREFs (the CL drawing) and plain blocks alike; a legend is never a road.
                        if (child.Name.Contains("LEGEND", StringComparison.OrdinalIgnoreCase)) continue;
                        // A key-map inset or a second copy of the same XREF is not a road. Hidden layers ARE read: centre lines are
                        // references for locating segments, not quantities, and engineers often freeze the axis layer.
                        if (child.IsFromExternalReference && (DrawingVisibility.IsInset(reference.ScaleFactors) ||
                            !visibility.FirstPlacement(reference.BlockTableRecord, transform * reference.BlockTransform))) continue;
                        Walk(tr, reference.BlockTableRecord, transform * reference.BlockTransform, prefix + entity.Handle + "/", ancestors, depth + 1, lines, labels, visibility);
                        break;
                    case Curve curve when curve is Polyline or Polyline2d or Line or Arc && IsCentreLineLayer(layer):
                        try
                        {
                            var points = Points(curve, transform);
                            if (points.Count >= 2) lines.Add(new(prefix + entity.Handle, layer, points));
                        }
                        catch (System.Exception) { /* a curve without geometry is not a centre line */ }
                        break;
                    case DBText text when IsSectionLabelLayer(layer):
                        AddLabel(text.TextString, text.Position.TransformBy(transform), layer, labels);
                        break;
                    case MText mtext when IsSectionLabelLayer(layer):
                        AddLabel(mtext.Text, mtext.Location.TransformBy(transform), layer, labels);
                        break;
                }
            }
        }
        finally { ancestors.Remove(blockId); }
    }

    private static void AddLabel(string text, Point3d at, string layer, List<PavementSegmentBands.SectionLabel> labels)
    {
        if (PavementSegmentBands.SectionNumber(text) != null) labels.Add(new(text.Trim(), at.X, at.Y, layer));
    }

    /// <summary>The curve as plan points every ≤ 2 drawing units on arcs, exact on straight parts.</summary>
    private static IReadOnlyList<(double X, double Y)> Points(Curve source, Matrix3d transform)
    {
        var points = new List<(double X, double Y)>();
        void Add(Point3d p) { p = p.TransformBy(transform); if (points.Count == 0 || Math.Abs(points[^1].X - p.X) + Math.Abs(points[^1].Y - p.Y) > 1e-9) points.Add((p.X, p.Y)); }
        if (source is Line line) { Add(line.StartPoint); Add(line.EndPoint); return points; }
        if (source is Polyline pl)
        {
            var segments = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
            for (var i = 0; i < segments; i++)
            {
                Add(pl.GetPoint3dAt(i));
                if (pl.GetSegmentType(i) != SegmentType.Arc) continue;
                var arc = pl.GetArcSegmentAt(i);
                var n = Math.Max(2, (int)Math.Ceiling(arc.GetLength(arc.GetParameterOf(arc.StartPoint), arc.GetParameterOf(arc.EndPoint), 1e-6) / 2));
                for (var k = 1; k < n; k++) Add(pl.GetPointAtParameter(i + (double)k / n));
            }
            Add(pl.Closed ? pl.GetPoint3dAt(0) : pl.GetPoint3dAt(pl.NumberOfVertices - 1));
            return points;
        }
        var length = source.GetDistanceAtParameter(source.EndParam);
        var steps = Math.Max(2, (int)Math.Ceiling(length / 2));
        for (var k = 0; k <= steps; k++) Add(source.GetPointAtDist(length * k / steps));
        return points;
    }

    /// <summary>Civil 3D alignments (host drawing): sampled every 2 m; their sample lines' numeric names are the sections.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadAlignments(Database db, List<PavementSegmentBands.Centreline> lines, List<PavementSegmentBands.SectionLabel> labels, List<string> findings)
    {
        var civil = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
        if (civil == null) return;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (ObjectId id in civil.GetAlignmentIds())
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.Alignment alignment) continue;
                var points = new List<(double X, double Y)>();
                var start = alignment.StartingStation; var end = alignment.EndingStation;
                var steps = Math.Max(2, (int)Math.Ceiling((end - start) / 2));
                for (var k = 0; k <= steps; k++)
                {
                    double x = 0, y = 0;
                    alignment.PointLocation(start + (end - start) * k / steps, 0, ref x, ref y);
                    points.Add((x, y));
                }
                lines.Add(new("ALIGNMENT:" + alignment.Handle, "Alignment " + alignment.Name, points));
                foreach (ObjectId groupId in alignment.GetSampleLineGroupIds())
                {
                    if (tr.GetObject(groupId, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.SampleLineGroup group) continue;
                    foreach (ObjectId lineId in group.GetSampleLineIds())
                    {
                        if (tr.GetObject(lineId, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.SampleLine sample) continue;
                        if (PavementSegmentBands.SectionNumber(sample.Name) == null) continue;
                        double x = 0, y = 0;
                        alignment.PointLocation(sample.Station, 0, ref x, ref y);
                        labels.Add(new(sample.Name, x, y, "SampleLine"));
                    }
                }
            }
            catch (System.Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            { findings.Add("Alignment לא נקרא: " + ex.Message); }
        }
    }
}
