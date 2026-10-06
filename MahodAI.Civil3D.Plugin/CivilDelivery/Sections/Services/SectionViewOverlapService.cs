using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Overlap evidence for ONE SectionView against every other live SectionView in
    /// model space — managed, manual, or foreign. Fail-closed: the selected view's
    /// own extents must be valid (finite, positive area); another view whose extents
    /// cannot be read or are degenerate is reported as unmeasurable, never skipped.
    /// </summary>
    internal static class SectionViewOverlapService
    {
        internal static LayoutOverlapLogic.Box ManagedEnvelope(
            Transaction tr, Database db, CivilDb.SectionView view, string logicalKey)
        {
            LayoutOverlapLogic.Box viewBox;
            try
            {
                var ext = view.GeometricExtents;
                viewBox = new LayoutOverlapLogic.Box(
                    ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y);
            }
            catch
            {
                return new LayoutOverlapLogic.Box(
                    double.NaN, double.NaN, double.NaN, double.NaN);
            }

            var annotations = SectionAnnotationRegistry.ReadEnvelope(tr, db, logicalKey);
            if (!viewBox.IsValid || !annotations.IsValid ||
                annotations.Bounds is not { Length: 4 } bounds)
                return new LayoutOverlapLogic.Box(
                    double.NaN, double.NaN, double.NaN, double.NaN);

            return new LayoutOverlapLogic.Box(
                System.Math.Min(viewBox.MinX, bounds[0]),
                System.Math.Min(viewBox.MinY, bounds[1]),
                System.Math.Max(viewBox.MaxX, bounds[2]),
                System.Math.Max(viewBox.MaxY, bounds[3]));
        }

        internal static LayoutOverlapLogic.Report Inspect(
            Transaction tr, Database db, CivilDb.SectionView self, string selfLogicalKey)
        {
            var selfBox = ManagedEnvelope(tr, db, self, selfLogicalKey);

            var others = new List<(string Id, LayoutOverlapLogic.Box Box)>();
            var sectionViewClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(CivilDb.SectionView));
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(
                table[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (id.IsNull || id.IsErased || id == self.ObjectId) continue;
                if (!id.ObjectClass.IsDerivedFrom(sectionViewClass)) continue;

                var handle = id.Handle.ToString();
                CivilDb.SectionView? view = null;
                try { view = tr.GetObject(id, OpenMode.ForRead, openErased: false) as CivilDb.SectionView; }
                catch { }
                if (view == null)
                {
                    others.Add((handle, new LayoutOverlapLogic.Box(double.NaN, double.NaN, double.NaN, double.NaN)));
                    continue;
                }

                try
                {
                    var ext = view.GeometricExtents;
                    others.Add((handle, new LayoutOverlapLogic.Box(
                        ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y)));
                }
                catch
                {
                    others.Add((handle, new LayoutOverlapLogic.Box(double.NaN, double.NaN, double.NaN, double.NaN)));
                }
            }

            // A SectionView's native extents do not contain the title, axis, utility
            // symbols, vehicles or dimensions drawn around it. Compare against every
            // other registered visual envelope as well, including decorations around
            // reused manual views that deliberately carry no ownership on the Civil
            // object itself.
            foreach (var envelope in SectionAnnotationRegistry.ReadOtherEnvelopes(
                         tr, db, selfLogicalKey))
            {
                var box = envelope.IsValid && envelope.Bounds is { Length: 4 } b
                    ? new LayoutOverlapLogic.Box(b[0], b[1], b[2], b[3])
                    : new LayoutOverlapLogic.Box(
                        double.NaN, double.NaN, double.NaN, double.NaN);
                others.Add(("annotations:" + envelope.Id, box));
            }

            return LayoutOverlapLogic.Inspect(selfBox, others);
        }
    }
}
