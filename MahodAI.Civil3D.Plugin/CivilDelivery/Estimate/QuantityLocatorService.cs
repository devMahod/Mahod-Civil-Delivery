using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Shows the engineer exactly which drawing objects a quantity row was measured from:
    /// selects them and zooms the current view to their combined extents. A priced metre
    /// that cannot be pointed at in the drawing is not trustworthy; this makes every row
    /// of the estimate clickable back to geometry.
    /// </summary>
    public static class QuantityLocatorService
    {
        public sealed record Outcome(int Selected, int InXref, int Missing, bool Zoomed, string Message);

        /// <summary>Maximum objects selected at once; beyond this the view still zooms to all.</summary>
        public const int SelectionBound = 2000;

        public static Outcome Show(Document doc, IEnumerable<NeutralQuantityRecord> records, ProjectProfile? profile = null)
        {
            var db = doc.Database;
            var ed = doc.Editor;
            var ids = new List<ObjectId>();
            int inXref = 0, xrefBounded = 0, missing = 0;
            Extents3d? ext = null;
            var drawingUnits = HostDrawingUnitService.Scale(HostDrawingUnitService.Resolve(db, profile));

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var r in records)
                {
                    if (r.Source.Xref != null)
                    {
                        // A nested entity cannot be put in AutoCAD's implied host
                        // selection by its child-database handle.  Its scan evidence is
                        // already transformed into host WCS, however, so it can and must
                        // still drive an honest zoom instead of a silent 0-object no-op.
                        inXref++;
                        if (drawingUnits.IsSupported && TryGeometryEvidenceExtents(
                                r.Measurement.GeometryEvidence,
                                drawingUnits.LinearToMetres,
                                out var xrefExtents))
                        {
                            ext = ext == null ? xrefExtents : Union(ext.Value, xrefExtents);
                            xrefBounded++;
                        }
                        else
                        {
                            missing++;
                        }
                        continue;
                    }
                    var id = Resolve(db, r.Source.Handle);
                    if (id.IsNull || id.IsErased) { missing++; continue; }
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) { missing++; continue; }
                        var e = ent.GeometricExtents;
                        ext = ext == null ? e : Union(ext.Value, e);
                        if (ids.Count < SelectionBound) ids.Add(id);
                    }
                    catch { missing++; }
                }
                tr.Commit();

                if (ids.Count > 0)
                {
                    try { ed.SetImpliedSelection(ids.ToArray()); } catch { }
                }
            }

            bool zoomed = false;
            if (ext != null)
            {
                zoomed = ZoomTo(doc, ext.Value);
            }

            var parts = new List<string>();
            if (ids.Count > 0)
                parts.Add(ids.Count == 1 ? "נבחר עצם אחד" : $"נבחרו {ids.Count:N0} עצמים");
            if (inXref > 0)
                parts.Add(xrefBounded == inXref
                    ? $"{inXref:N0} בתוך XREF — התמקדות לפי תחומי המקור (לא ניתנים לבחירה ישירה)"
                    : $"{inXref:N0} בתוך XREF · התמקדות ב-{xrefBounded:N0} בעלי תחום מאומת");
            if (missing > 0) parts.Add($"{missing:N0} לא נמצאו בשרטוט הנוכחי");
            if (!zoomed && ext != null) parts.Add("לא ניתן להתמקד אוטומטית. " + NativeViewZoomService.RecoveryGuidance);
            if (parts.Count == 0) parts.Add("לא נמצאו מקורות להצגה");
            return new Outcome(ids.Count, inXref, missing, zoomed, string.Join(" · ", parts));
        }

        /// <summary>
        /// Neutral geometry evidence is stored in SI metres. Convert it back to the
        /// active drawing's WCS units before feeding AutoCAD's view API.
        /// </summary>
        internal static bool TryGeometryEvidenceExtents(
            double[]? evidence, double linearToMetres, out Extents3d extents)
        {
            extents = default;
            if (evidence == null || evidence.Length != 4 ||
                !double.IsFinite(linearToMetres) || linearToMetres <= 0 ||
                evidence.Any(value => !double.IsFinite(value)))
                return false;

            var minX = evidence[0] / linearToMetres;
            var minY = evidence[1] / linearToMetres;
            var maxX = evidence[2] / linearToMetres;
            var maxY = evidence[3] / linearToMetres;
            if (minX > maxX || minY > maxY) return false;

            extents = new Extents3d(
                new Point3d(minX, minY, 0),
                new Point3d(maxX, maxY, 0));
            return true;
        }

        internal static ObjectId Resolve(Database db, string handleText)
        {
            if (string.IsNullOrWhiteSpace(handleText)) return ObjectId.Null;
            // Discovery records carry the bare host handle; rule records may prefix a
            // nesting path ("xrefname/..."); only bare hex handles live in the host DB.
            var h = handleText.Trim();
            var slash = h.LastIndexOf('/');
            if (slash >= 0) return ObjectId.Null;
            if (!long.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return ObjectId.Null;
            try
            {
                return db.TryGetObjectId(new Handle(value), out var id) ? id : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        internal static Extents3d Union(Extents3d a, Extents3d b)
        {
            var min = new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y), Math.Min(a.MinPoint.Z, b.MinPoint.Z));
            var max = new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), Math.Max(a.MaxPoint.Z, b.MaxPoint.Z));
            return new Extents3d(min, max);
        }

        /// <summary>
        /// Zooms the active viewport using its complete WCS-to-DCS transform.
        /// True means native view readback matched, not that a command was queued.
        /// </summary>
        internal static bool ZoomTo(Document doc, Extents3d ext)
            => NativeViewZoomService.TryZoom(doc, ext, margin: 1.25);
    }
}
