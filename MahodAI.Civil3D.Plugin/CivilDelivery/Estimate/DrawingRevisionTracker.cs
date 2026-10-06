using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// A live, per-Database mutation revision.  Filename/profile checks cannot catch
    /// a kerb that was moved after Scan; FingerprintGuid alone commonly changes only
    /// on save.  AutoCAD database events catch append/modify/erase in memory and the
    /// fingerprint catches a save/reopen boundary.  A mismatch is deliberately a
    /// false-positive-safe rescan request, never reuse of stale money.
    /// </summary>
    internal static class DrawingRevisionTracker
    {
        internal sealed record LiveSnapshot(
            string DrawingPath,
            string DrawingHash,
            string DatabaseRevision,
            int? DbMod,
            string? Failure);

        private sealed class State
        {
            private long _revision;

            internal State(Database db)
            {
                db.ObjectAppended += OnObjectChanged;
                db.ObjectModified += OnObjectChanged;
                db.ObjectErased += OnObjectErased;
            }

            internal long Revision => Interlocked.Read(ref _revision);

            private void OnObjectChanged(object sender, ObjectEventArgs e) =>
                Interlocked.Increment(ref _revision);

            private void OnObjectErased(object sender, ObjectErasedEventArgs e) =>
                Interlocked.Increment(ref _revision);
        }

        private static readonly ConditionalWeakTable<Database, State> States = new();

        internal static string Capture(Database db)
        {
            ArgumentNullException.ThrowIfNull(db);
            var state = States.GetValue(db, value => new State(value));
            string fingerprint;
            try { fingerprint = db.FingerprintGuid.ToString(); }
            catch { fingerprint = "fingerprint-unavailable"; }
            return $"{fingerprint}:{state.Revision}";
        }

        internal static bool Matches(Database db, string? captured) =>
            !string.IsNullOrWhiteSpace(captured) &&
            string.Equals(Capture(db), captured, StringComparison.Ordinal);

        internal static SavedDrawingPathPolicy.Identity CaptureSavedDrawingIdentity(Document doc)
        {
            ArgumentNullException.ThrowIfNull(doc);
            if (!IsActive(doc)) return SavedDrawingPathPolicy.Refused("The requested document is not active.");
            // Autodesk DWGTITLED is read-only: 0 = unnamed, 1 = named. Do not infer
            // saved state from Database.Filename; a new document can retain its DWT.
            var identity = SavedDrawingPathPolicy.Capture(
                () => Convert.ToInt32(AcadApp.GetSystemVariable("DWGTITLED")),
                () => doc.Name, () => doc.Database.Filename);
            return IsActive(doc) ? identity : SavedDrawingPathPolicy.Refused(
                "The active document changed while its saved identity was captured.");
        }

        /// <summary>
        /// Captures the only drawing identity that may back money.  Capture(db) is
        /// deliberately the first operation: mutation events are subscribed before
        /// DBMOD/file checks and before callers begin measurement.
        /// </summary>
        internal static LiveSnapshot CaptureLive(Document doc, bool includeFileHash = true)
        {
            ArgumentNullException.ThrowIfNull(doc);
            var db = doc.Database;
            var before = Capture(db);

            if (!IsActive(doc))
                return new LiveSnapshot("", "", before, null,
                    "the requested drawing is not the active Civil document; DBMOD would describe another drawing");

            var identity = CaptureSavedDrawingIdentity(doc);
            if (identity.Failure != null)
                return new LiveSnapshot("", "", before, null, identity.Failure);

            int? dbMod = null;
            string? failure = null;
            try { dbMod = Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD")); }
            catch (Exception ex) { failure = "DBMOD could not be read: " + ex.Message; }

            var path = identity.DrawingPath;

            string hash = "";
            // A post-scan view change still needs real disk evidence for freshness.
            // InitialFailure/ForScan remain strict; no DBMOD or revision is altered.
            if (identity.IsSaved && failure == null &&
                EstimateSourceSnapshotPolicy.CanReadSavedDrawingHash(dbMod) && includeFileHash)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        failure = "the active drawing is not saved to an existing DWG file";
                    else
                        hash = identity.ReadSavedHash(ArtifactHash.Sha256OfFile);
                }
                catch (Exception ex)
                {
                    failure = "the saved DWG bytes could not be hashed: " + ex.Message;
                }
            }

            var after = Capture(db);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                failure = "the live drawing database changed while its source identity was captured";

            return new LiveSnapshot(path, hash, after, dbMod, failure);
        }

        private static bool IsActive(Document doc)
        {
            try
            {
                var active = AcadApp.DocumentManager.MdiActiveDocument;
                return active != null &&
                       active.Database.UnmanagedObject == doc.Database.UnmanagedObject;
            }
            catch { return false; }
        }
    }
}
