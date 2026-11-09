using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The engineer marks the area of a bill of quantities: corner by corner on the drawing (a live outline follows), or by
/// picking a closed polyline already drawn. The outline is only on screen: nothing is added to the drawing.
/// Prompts are English, as every Mahod tool's command line (engineers' request 2026-10-14).
/// </summary>
internal static class BoqAreaPicker
{
    internal sealed record Picked(SpatialQuantityClip Area, ObjectId PickedPolyline);

    internal static Picked? Pick(Document doc, double metresPerUnit)
    {
        var ed = doc.Editor;
        while (true)
        {
            var first = new PromptPointOptions("");
            first.SetMessageAndKeywords("\nBOQ area - first corner, or [Select polyline]: ", "Select");
            first.AllowNone = false;
            var start = ed.GetPoint(first);
            if (start.Status == PromptStatus.Keyword) return SelectPolyline(doc, metresPerUnit);
            if (start.Status != PromptStatus.OK) return null;
            var ucs = new List<Point3d> { start.Value };
            using var preview = new Outline();
            while (true)
            {
                var wcs = ucs.Select(p => p.TransformBy(ed.CurrentUserCoordinateSystem)).ToList();
                preview.Show(wcs, closed: wcs.Count >= 3);
                var next = new PromptPointOptions("");
                next.SetMessageAndKeywords(ucs.Count < 3
                    ? "\nNext corner, or [Undo]: "
                    : "\nNext corner, or [Undo/Close] <Close>: ", ucs.Count < 3 ? "Undo" : "Undo Close");
                next.UseBasePoint = true; next.BasePoint = ucs[^1]; next.UseDashedLine = true;
                next.AllowNone = ucs.Count >= 3;
                var r = ed.GetPoint(next);
                if (r.Status == PromptStatus.OK) { ucs.Add(r.Value); continue; }
                if (r.Status == PromptStatus.Keyword && r.StringResult == "Undo")
                {
                    if (ucs.Count > 1) ucs.RemoveAt(ucs.Count - 1);
                    continue;
                }
                if (r.Status == PromptStatus.None || (r.Status == PromptStatus.Keyword && r.StringResult == "Close")) break;
                return null;   // Esc
            }
            var corners = ucs.Select(p => p.TransformBy(ed.CurrentUserCoordinateSystem)).Select(p => (p.X, p.Y)).ToArray();
            try { return new Picked(new SpatialQuantityClip(corners), ObjectId.Null); }
            catch (ArgumentException) { ed.WriteMessage("\nThe area crosses itself or has no size - draw it again."); }
        }
    }

    private static Picked? SelectPolyline(Document doc, double metresPerUnit)
    {
        var ed = doc.Editor;
        while (true)
        {
            var options = new PromptEntityOptions("\nSelect a closed polyline for the BOQ area: ");
            options.SetRejectMessage("\nOnly a polyline.");
            options.AddAllowedClass(typeof(Polyline), true);
            var chosen = ed.GetEntity(options);
            if (chosen.Status != PromptStatus.OK) return null;
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var pl = (Polyline)tr.GetObject(chosen.ObjectId, OpenMode.ForRead);
            var closed = pl.Closed || (pl.NumberOfVertices > 2 && pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(pl.NumberOfVertices - 1)) < 1e-9);
            if (!closed || pl.NumberOfVertices < 3) { ed.WriteMessage("\nThe polyline is not closed."); continue; }
            if (Math.Abs(Math.Abs(pl.Normal.Z) - 1) > 1e-9) { ed.WriteMessage("\nThe polyline is not in the plan (XY) plane."); continue; }
            // Arcs become short chords (1 mm), so a rounded area is followed closely.
            var tolerance = 0.001 / metresPerUnit;
            var points = new List<(double X, double Y)>();
            for (var i = 0; i < pl.NumberOfVertices; i++)
            {
                var p = pl.GetPoint3dAt(i); points.Add((p.X, p.Y));
                if (pl.GetSegmentType(i) != SegmentType.Arc) continue;
                var arc = pl.GetArcSegmentAt(i);
                var sweep = 4 * Math.Atan(Math.Abs(pl.GetBulgeAt(i)));
                var step = arc.Radius > tolerance ? 2 * Math.Acos(1 - tolerance / arc.Radius) : sweep;
                var n = Math.Clamp((int)Math.Ceiling(sweep / step), 2, 4096);
                for (var k = 1; k < n; k++) { var q = pl.GetPointAtParameter(i + (double)k / n); points.Add((q.X, q.Y)); }
            }
            tr.Commit();
            try { return new Picked(new SpatialQuantityClip(points), chosen.ObjectId); }
            catch (ArgumentException) { ed.WriteMessage("\nThe polyline crosses itself - select another one."); }
        }
    }

    /// <summary>
    /// The area on screen only (transient graphics): an orange outline on top and, once it closes, a see-through orange
    /// fill, so the engineer sees what will be measured. A thin outline alone was hard to see over a busy plan (live 2026-11-09).
    /// </summary>
    internal sealed class Outline : IDisposable
    {
        private readonly List<Entity> _shown = new();
        internal void Show(IReadOnlyList<Point3d> wcs, bool closed)
        {
            Clear();
            if (wcs.Count < 2) return;
            var pl = new Polyline(wcs.Count);
            pl.SetDatabaseDefaults();
            for (var i = 0; i < wcs.Count; i++) pl.AddVertexAt(i, new Point2d(wcs[i].X, wcs[i].Y), 0, 0, 0);
            pl.Closed = closed; pl.ColorIndex = 30; pl.LineWeight = LineWeight.LineWeight050;
            Add(pl, TransientDrawingMode.DirectTopmost);
            if (!closed || wcs.Count < 3) return;
            try
            {
                var fill = new Hatch();
                fill.SetDatabaseDefaults();
                fill.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                var ring = new Point2dCollection(); var bulges = new DoubleCollection();
                foreach (var p in wcs) { ring.Add(new Point2d(p.X, p.Y)); bulges.Add(0); }
                ring.Add(new Point2d(wcs[0].X, wcs[0].Y)); bulges.Add(0);
                fill.AppendLoop(HatchLoopTypes.Polyline, ring, bulges);
                fill.EvaluateHatch(true);
                fill.ColorIndex = 30;
                fill.Transparency = new Autodesk.AutoCAD.Colors.Transparency((byte)70);   // ~73 % see-through
                Add(fill, TransientDrawingMode.Main);
            }
            catch (System.Exception) { }   // the outline alone still shows the area
        }
        internal void Show(SpatialQuantityClip area)
            => Show(area.Vertices.Select(v => new Point3d(v.X, v.Y, 0)).ToList(), closed: true);
        private void Add(Entity entity, TransientDrawingMode mode)
        {
            try { TransientManager.CurrentTransientManager.AddTransient(entity, mode, 128, new IntegerCollection()); _shown.Add(entity); }
            catch (System.Exception) { entity.Dispose(); }
        }
        private void Clear()
        {
            foreach (var e in _shown)
            {
                try { TransientManager.CurrentTransientManager.EraseTransient(e, new IntegerCollection()); }
                catch (System.Exception) { }
                e.Dispose();
            }
            _shown.Clear();
        }
        public void Dispose() => Clear();
    }
}
