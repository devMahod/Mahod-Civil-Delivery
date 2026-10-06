using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>A reported DWG definition, not verified or measured source evidence.</summary>
    public sealed record EstimateXrefInventoryEntry(
        string Name, string Path, string Status, bool IsUnloaded, bool IsResolved,
        string DefinitionHandle = "", bool IsNested = false);

    public sealed record EstimateSourceInventorySnapshot(
        string HostDrawingPath,
        IReadOnlyList<EstimateXrefInventoryEntry> References,
        string? ReadFailure = null, string HostFingerprint = "")
    {
        public bool IsComplete => string.IsNullOrWhiteSpace(ReadFailure);
        public string Text => EstimateSourceInventoryService.Format(this);
        public EstimateSourceInventory ToSelectionInventory()
        {
            if (!IsComplete) throw new InvalidOperationException(ReadFailure);
            return new EstimateSourceInventory(HostFingerprint, HostDrawingPath,
                new[] { new EstimateSourceDefinition(EstimateSourceSelectionPolicy.HostKey,
                    System.IO.Path.GetFileName(HostDrawingPath), HostDrawingPath, "מארח", true) }
                .Concat(References.Select(r => new EstimateSourceDefinition(
                    EstimateSourceSelectionPolicy.XrefKey(r.Name, r.Path, r.DefinitionHandle),
                    r.Name, r.Path, r.Status, IsNested: r.IsNested))).ToArray());
        }
    }

    /// <summary>
    /// Captures the active host's known external-reference definitions for informed
    /// scope approval. This is deliberately not geometry traversal, disk identity
    /// verification, or the list of sources that a later SCAN actually measures.
    /// </summary>
    public static class EstimateSourceInventoryService
    {
        public static EstimateSourceInventorySnapshot Capture(Document document)
        {
            var hostPath = string.Empty;
            var fingerprint = string.Empty;
            var references = new List<EstimateXrefInventoryEntry>();
            var failures = new List<string>();
            try
            {
                ArgumentNullException.ThrowIfNull(document);
                // Opt-in trace boundaries only (Codex 01:56): the 02/10 crash stack ended in this LockDocument.
                EstimateScanTrace.Mark("inventory.lock.begin");
                using (document.LockDocument())
                {
                    EstimateScanTrace.Mark("inventory.lock.acquired");
                    var database = document.Database;
                    hostPath = database.Filename ?? string.Empty;
                    fingerprint = database.FingerprintGuid;
                    if (string.IsNullOrWhiteSpace(hostPath))
                        failures.Add("לא נמצא נתיב מלא לשרטוט המארח.");

                    using var transaction = database.TransactionManager.StartTransaction();
                    try
                    {
                        var table = (BlockTable)transaction.GetObject(
                            database.BlockTableId, OpenMode.ForRead);
                        foreach (ObjectId id in table)
                        {
                            try
                            {
                                var definition = (BlockTableRecord)transaction.GetObject(
                                    id, OpenMode.ForRead);
                                if (!definition.IsFromExternalReference &&
                                    !definition.IsFromOverlayReference) continue;

                                references.Add(new EstimateXrefInventoryEntry(
                                    definition.Name ?? string.Empty,
                                    definition.PathName ?? string.Empty,
                                    definition.XrefStatus.ToString(),
                                    definition.IsUnloaded,
                                    definition.IsResolved, definition.Handle.ToString(), definition.IsDependent));
                            }
                            catch (Exception ex)
                            {
                                failures.Add("לא ניתן לקרוא הגדרת בלוק: " + ex.Message);
                            }
                        }
                    }
                    finally
                    {
                        transaction.Abort();
                        EstimateScanTrace.Mark("inventory.transaction.aborted");
                    }
                }
                EstimateScanTrace.Mark("inventory.lock.released");
            }
            catch (Exception ex)
            {
                EstimateScanTrace.Mark("inventory.fail", null, null, ex.GetType().Name);
                failures.Add("קריאת מקורות השרטוט נכשלה: " + ex.Message);
            }

            return new EstimateSourceInventorySnapshot(
                hostPath,
                references.OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                failures.Count == 0 ? null : string.Join(Environment.NewLine, failures), fingerprint);
        }

        /// <summary>Same metadata contract inside the scan's existing locked transaction; no new lock or traversal.</summary>
        internal static EstimateSourceInventory CaptureForScan(Database database, Transaction transaction)
        {
            var rows = new List<EstimateSourceDefinition>
            {
                new(EstimateSourceSelectionPolicy.HostKey, System.IO.Path.GetFileName(database.Filename), database.Filename, "מארח", true),
            };
            var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                var definition = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                if (!definition.IsFromExternalReference && !definition.IsFromOverlayReference) continue;
                rows.Add(new EstimateSourceDefinition(
                    EstimateSourceSelectionPolicy.XrefKey(definition.Name, definition.PathName ?? "", definition.Handle.ToString()),
                    definition.Name, definition.PathName ?? "", definition.XrefStatus.ToString(), IsNested: definition.IsDependent));
            }
            return new EstimateSourceInventory(database.FingerprintGuid, database.Filename, rows);
        }

        /// <summary>Formats every captured definition; never silently caps the inventory.</summary>
        public static string Format(EstimateSourceInventorySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var text = new StringBuilder();
            text.AppendLine("המקורות הידועים בשרטוט הפתוח כעת");
            text.AppendLine("שרטוט מארח: " + Bidi.Ltr(
                string.IsNullOrWhiteSpace(snapshot.HostDrawingPath)
                    ? "(נתיב לא זמין)" : snapshot.HostDrawingPath));
            text.AppendLine("זו רשימת הגדרות XREF מטבלת הבלוקים של ה-DWG המארח בלבד. " +
                "זו אינה סריקה רקורסיבית של גאומטריה, ואינה הוכחה שכל המקורות נמדדו או אומתו.");
            text.AppendLine("הנתיבים להלן הם הנתיבים המוגדרים בשרטוט; לא בוצעו טעינה, צירוף או פתרון הפניות.");
            text.AppendLine();

            if (!snapshot.IsComplete)
            {
                text.AppendLine("רשימת המקורות אינה מלאה עקב כשל קריאה — אין להסיק שאין הפניות נוספות.");
                text.AppendLine(snapshot.ReadFailure);
                text.AppendLine();
            }

            if (snapshot.References.Count == 0)
            {
                text.AppendLine(snapshot.IsComplete
                    ? "לא נמצאו הגדרות XREF בטבלת הבלוקים: המארח בלבד ברשימה זו."
                    : "לא נקראו הגדרות XREF בהצלחה; זה אינו אישור שהשרטוט הוא מארח בלבד.");
            }
            else
            {
                var loaded = snapshot.References.Count(reference =>
                    reference.IsResolved && !reference.IsUnloaded);
                var unloaded = snapshot.References.Count(reference => reference.IsUnloaded);
                var unresolved = snapshot.References.Count(reference =>
                    !reference.IsResolved && !reference.IsUnloaded);
                text.AppendLine($"הגדרות XREF שנקראו: {snapshot.References.Count} " +
                    $"(טעונות/פתורות: {loaded}; לא טעונות: {unloaded}; לא פתורות: {unresolved}).");
                for (var index = 0; index < snapshot.References.Count; index++)
                {
                    var reference = snapshot.References[index];
                    text.AppendLine($"{index + 1}. " + Bidi.Ltr(reference.Name));
                    text.AppendLine("   נתיב מוגדר: " + Bidi.Ltr(
                        string.IsNullOrWhiteSpace(reference.Path) ? "(לא מוגדר)" : reference.Path));
                    text.AppendLine("   מצב DWG: " + Bidi.Ltr(reference.Status) +
                        $"; {(reference.IsNested ? "מקונן — יורש בחירת ענף" : "מקור ראשי")}" +
                        $"; לא טעון: {(reference.IsUnloaded ? "כן" : "לא")}" +
                        $"; פתור: {(reference.IsResolved ? "כן" : "לא")}");
                }
            }

            text.AppendLine();
            text.AppendLine("הגדרות מקוננות עשויות להופיע ברשימה; תוכן של הפניה חסרה או לא טעונה לא נפתח ולא נבדק. " +
                "הסריקה הבאה תבדוק אילו מקורות משתתפים ואת תקינותם.");
            return text.ToString().TrimEnd();
        }
    }
}
