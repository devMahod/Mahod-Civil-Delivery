using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilDb = Autodesk.Civil.DatabaseServices;
using State = MahodAI.CivilDelivery.Shared.ManagedSectionViewLookupPolicy.State;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Recovers a managed view after a fresh UNCHANGED PLAN has discarded APPLY
    /// handles. Reads only native objects in the exact active drawing; never opens
    /// an external drawing, trusts a name, or searches by a legacy key alias.
    /// </summary>
    internal static class ManagedSectionViewLookupService
    {
        internal const int MaxNativeObjects = 200000;

        /// <summary>Call only for an explicit Show action, never a recurring UI refresh.</summary>
        internal static ManagedSectionViewLookupPolicy.Result Resolve(
            Document doc, SectionPlan plan, SectionPlanRecord record)
        {
            try { return ResolveCore(doc, plan, record); }
            catch (Exception ex)
            {
                return new(State.Unreadable, null, "לא ניתן להשלים איתור חתך בבעלות הכלי: " + ex.Message);
            }
        }

        private static ManagedSectionViewLookupPolicy.Result ResolveCore(
            Document doc, SectionPlan plan, SectionPlanRecord record)
        {
            if (record.Action != PlanAction.Unchanged || record.ManualSectionReuse != null)
                return new(State.NotApplicable, null, "האיתור מיועד לחתך מנוהל שתוכנן ללא שינוי.");
            if (!ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, doc) ||
                !string.Equals(plan.SourceDrawing, DrawingScopeIdentity.For(doc), StringComparison.OrdinalIgnoreCase))
                return new(State.WrongDrawing, null, "השרטוט הפעיל שונה משרטוט התכנון; לא בוצע איתור.");
            if (plan.Records.Count(candidate => candidate.RecordId == record.RecordId) != 1 ||
                !plan.Records.Any(candidate => ReferenceEquals(candidate, record)) ||
                plan.Records.Count(candidate => candidate.LogicalKey == record.LogicalKey) != 1)
                return new(State.IncompleteIdentity, null, "הרשומה או מפתח החתך אינם יחידים בתכנון הנוכחי.");

            var candidates = new List<ManagedSectionViewLookupPolicy.Candidate>();
            var readable = true;
            var inspected = 0;
            try
            {
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    if (string.IsNullOrWhiteSpace(plan.SourceDatabaseRevision) ||
                        !string.Equals(plan.SourceDatabaseRevision, DrawingRevisionTracker.Capture(doc.Database), StringComparison.Ordinal))
                        return new(State.IncompleteIdentity, null, "השרטוט השתנה מאז התכנון; יש לתכנן מחדש לפני איתור החתך.");
                    var viewClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(CivilDb.SectionView));
                    var lineClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(CivilDb.SampleLine));
                    var blocks = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId blockId in blocks)
                    {
                        if (++inspected > MaxNativeObjects)
                            return new(State.Unreadable, null,
                                $"מלאי השרטוט חורג מגבול האיתור ({MaxNativeObjects:N0} עצמים); לא נבחרה תוצאה חלקית.");
                        var space = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                        if (!space.IsLayout || space.IsFromExternalReference || space.IsFromOverlayReference) continue;
                        foreach (ObjectId id in space)
                        {
                            if (++inspected > MaxNativeObjects)
                                return new(State.Unreadable, null,
                                    $"מלאי השרטוט חורג מגבול האיתור ({MaxNativeObjects:N0} עצמים); לא נבחרה תוצאה חלקית.");
                            if (id.IsNull || id.IsErased) continue;
                            // Inspect cheap native type metadata before opening any entity.
                            // Count every visited ID so a sparse drawing remains bounded.
                            if (!id.ObjectClass.IsDerivedFrom(viewClass) &&
                                !id.ObjectClass.IsDerivedFrom(lineClass)) continue;
                            var obj = tr.GetObject(id, OpenMode.ForRead);
                            if (obj is not CivilDb.SectionView && obj is not CivilDb.SampleLine) continue;
                            var ownership = SectionOwnershipService.Read(tr, obj);
                            if (ownership == null || !string.Equals(ownership.LogicalKey, record.LogicalKey, StringComparison.Ordinal)) continue;
                            string? parent = null;
                            if (obj is CivilDb.SectionView view)
                            {
                                if (view.SampleLineId.IsNull || view.SampleLineId.IsErased ||
                                    tr.GetObject(view.SampleLineId, OpenMode.ForRead) is not CivilDb.SampleLine sampleLine)
                                    readable = false;
                                else parent = sampleLine.Handle.ToString();
                            }
                            candidates.Add(new(obj.Handle.ToString(), obj is CivilDb.SectionView, ownership, parent));
                        }
                    }
                    if (!string.Equals(plan.SourceDatabaseRevision, DrawingRevisionTracker.Capture(doc.Database), StringComparison.Ordinal))
                        readable = false;
                    tr.Abort();
                }
            }
            catch (Exception ex)
            {
                return new(State.Unreadable, null, "לא ניתן להשלים איתור חתך בבעלות הכלי: " + ex.Message);
            }
            return ManagedSectionViewLookupPolicy.Resolve(
                plan.SourceDrawing, DrawingScopeIdentity.For(doc), plan.ProjectProfileId,
                record.LogicalKey, record.InputFingerprint, record.Cl.SourceHandle, record.Cl.SourceDrawingHash,
                readable, candidates);
        }
    }
}
