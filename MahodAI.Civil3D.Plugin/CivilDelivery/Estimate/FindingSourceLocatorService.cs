using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Locates captured finding provenance in the already-loaded, unchanged drawing graph.</summary>
internal static class FindingSourceLocatorService
{
    internal sealed record Outcome(bool Identified, bool Selected, bool Zoomed, string Message);

    internal static Outcome Show(Document doc, EstimateWorkflowService.ScanResult scan, ProvenanceRef source)
    {
        var invalid = FindingSourceLocationPolicy.Validate(source);
        if (invalid != null) return new(false, false, false, invalid);
        // FreshnessReason hashes the host and every recorded XREF. Guard ALL those paths
        // before calling it; rejecting only the selected source would still touch a P: share.
        foreach (var guardedPath in scan.ExternalSources.Select(item => item.DrawingPath)
                     .Append(scan.SourceDrawing).Append(EstimateWorkflowService.DrawingIdentity(doc))
                     .Append(source.SourcePathOrUri))
        {
            var localFailure = LocalDriveFailure(guardedPath);
            if (localFailure != null) return new(false, false, false, localFailure);
        }
        using var documentLock = doc.LockDocument();
        var stale = EstimateWorkflowService.FreshnessReason(doc, scan);
        if (stale != null) return new(false, false, false, "האיתור נעצר — " + stale);
        if (!FindingSourceLocationPolicy.SamePath(scan.SourceDrawing, EstimateWorkflowService.DrawingIdentity(doc)))
            return new(false, false, false, "השרטוט הפעיל אינו השרטוט שנסרק");

        var db = doc.Database;
        var path = EstimateWorkflowService.DrawingIdentity(doc);
        var chain = new List<string>();
        var handles = source.SourceHandle!.Split('/');
        var transform = Matrix3d.Identity;
        Entity? entity = null;
        ObjectId selectedId = ObjectId.Null;
        Extents3d? bounds = null;
        string? boundsFailure = null;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var owner = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            for (var index = 0; index < handles.Length; index++)
            {
                // Resolve ONLY within the captured owner/insertion chain. A child handle
                // must never fall back to an unrelated host object with the same handle.
                entity = null;
                foreach (ObjectId id in owner)
                {
                    if (id.IsErased || !string.Equals(id.Handle.ToString(), handles[index], StringComparison.OrdinalIgnoreCase)) continue;
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity candidate) continue;
                    if (entity != null) return new(false, false, false, "handle אינו יחיד בהגדרה הטעונה");
                    entity = candidate;
                }
                if (entity == null) return new(false, false, false, $"לא אותר handle {handles[index]} בשרשרת המתועדת; הכשל נשאר פתוח");
                if (index == handles.Length - 1) break;
                if (entity is not BlockReference reference)
                    return new(false, false, false, "שרשרת המקור אינה עוברת דרך BlockReference המתועד");
                transform = transform * reference.BlockTransform;
                owner = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                if (owner.IsFromExternalReference || owner.IsFromOverlayReference)
                {
                    if (owner.IsUnloaded || !owner.IsResolved)
                        return new(false, false, false, "ה-XREF אינו טעון/פתור; לא נפתח קובץ חלופי");
                    var resolved = ClInstructionReader.ResolvePath(owner.PathName, path);
                    // ResolvePath is lexical only (no Exists/Open). Check drive type before any hash.
                    var localFailure = LocalDriveFailure(resolved);
                    if (localFailure != null) return new(false, false, false, localFailure);
                    path = resolved!;
                    chain.Add(owner.Name);
                    var loaded = owner.GetXrefDatabase(false);
                    if (loaded == null || !FindingSourceLocationPolicy.SamePath(loaded.Filename, path))
                        return new(false, false, false, "זהות מסד XREF הטעון אינה תואמת לנתיב המתועד");
                }
            }
            var identityFailure = FindingSourceLocationPolicy.IdentityFailure(source, path,
                ClInstructionReader.HashFileShared(path), chain.Count == 0 ? null : string.Join(" > ", chain));
            if (identityFailure != null) return new(false, false, false, identityFailure);
            if (source.SourceKind == "civil-model" && entity is not Autodesk.Civil.DatabaseServices.Corridor)
                return new(false, false, false, "סוג העצם החי אינו Corridor המתועד; לא בוצעו בחירה או התמקדות");
            // Reading geometry may fail although identity is proven. Report those states separately.
            try
            {
                var extents = entity!.GeometricExtents;
                extents.TransformBy(transform);
                if (!Finite(extents.MinPoint) || !Finite(extents.MaxPoint)) throw new InvalidOperationException("גבולות לא סופיים");
                bounds = extents;
            }
            catch (Exception ex) { boundsFailure = ex.Message; }
            // Never select the outer XREF/INSERT as a substitute for its failed child.
            if (handles.Length == 1 && chain.Count == 0) selectedId = entity!.ObjectId;
            tr.Commit();
        }
        stale = EstimateWorkflowService.FreshnessReason(doc, scan);
        if (stale != null) return new(false, false, false, "המקור השתנה במהלך האיתור — " + stale);
        var selected = false;
        // Clear the previous implied selection so an unrelated old object cannot appear to be the result.
        try
        {
            doc.Editor.SetImpliedSelection(selectedId.IsNull ? Array.Empty<ObjectId>() : new[] { selectedId });
            selected = !selectedId.IsNull;
        }
        catch { /* Identity/zoom remain independently reported. */ }
        var zoomed = bounds.HasValue && QuantityLocatorService.ZoomTo(doc, bounds.Value);
        var message = $"זוהה עצם {source.SourceHandle} במקור המאומת. " +
            (selected ? "העצם נבחר. " : "העצם לא נבחר ישירות; לא נבחר XREF שלם במקומו. ") +
            (zoomed ? "בוצעה התמקדות. " : "לא בוצעה התמקדות" + (boundsFailure == null ? ". " : ": " + boundsFailure + ". ")) +
            "האיתור אינו תיקון או אישור — הממצא נשאר פתוח; לאחר תיקון מקור יש לשמור ולסרוק מחדש.";
        return new(true, selected, zoomed, message);
    }

    private static bool Finite(Point3d point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static string? LocalDriveFailure(string? path) =>
        FindingSourceLocationPolicy.LocalDriveFailure(path, root => new DriveInfo(root).DriveType);
}
