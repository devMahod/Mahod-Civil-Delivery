using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// THE deterministic Sections API. The direct command, the smoke command and the
    /// MahodAI tools all call this one service layer — no duplicated business logic
    /// in command handlers, tools, chat pipelines or prompts (plan §1.7).
    /// </summary>
    public sealed class SectionsWorkflowService
    {
        public static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly SectionPlanService _planService = new();
        private readonly SectionApplyService _applyService = new();
        private readonly SectionVerifyService _verifyService = new();
        private readonly SectionPreviewService _previewService = new();

        public ProjectProfileLoader.LoadResult LoadProfile(string profileIdOrPath)
        {
            var path = ProfileLocator.Resolve(profileIdOrPath);
            return path == null
                ? new ProjectProfileLoader.LoadResult
                {
                    Findings =
                    {
                        new DeliveryFinding
                        {
                            Code = "SHR-PROFILE-MISSING",
                            Domain = "shared",
                            Severity = FindingSeverity.Error,
                            Title = $"פרופיל הפרויקט '{profileIdOrPath}' לא נמצא באף מיקום מוכר",
                            Message = ProfileLocator.DescribeSearchPaths(profileIdOrPath),
                        }
                    }
                }
                : ProjectProfileLoader.LoadFromFile(path);
        }

        /// <summary>
        /// Stage trail shared by every operation in this workflow instance. Written
        /// and flushed per stage so a Civil hang names the exact blocking call.
        /// </summary>
        public StageLog? Log { get; set; }

        public SectionPlan Plan(Document doc, ProjectProfile profile, string? profileHash)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.PlanAction);
            var db = doc.Database;
            Log?.Begin("workflow.plan.start_transaction");
            SectionPlan plan;
            // Publication is deliberately outside this scope. Abort plus successful
            // Dispose is the boundary between provisional Civil read-back and an
            // authoritative PLAN artifact/session value.
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Log?.End("workflow.plan.start_transaction");

                Log?.Begin("workflow.plan.get_civil_document");
                var civilDoc = CivilDocument.GetCivilDocument(db);
                Log?.End("workflow.plan.get_civil_document");

                plan = _planService.Plan(db, tr, civilDoc, profile, profileHash,
                    sourceDrawingIdentity: DrawingScopeIdentity.For(doc), runId: null, log: Log);
                tr.Abort(); // PLAN is a read-only contract; never persist lazy host changes
            }
            SectionInputIntegrityService.CapturePlanDatabaseState(db, plan, profile);

            WriteArtifact(plan.RunId, "section_plan.json", plan);
            WritePlanManifest(doc, profile, profileHash, plan);
            CivilDeliveryUsage.Step(CivilDeliveryUsage.PlanAction, ok: true);
            return plan;
        }

        public SectionPreviewDisplay Preview(
            Document doc, ProjectProfile profile, string? profileHash, SectionPlan plan,
            string? selectedRecordId = null)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.PreviewAction);
            var staleReason = SectionPlanLogic.ScopeStaleReason(
                plan, DrawingScopeIdentity.For(doc), profile, profileHash);
            if (staleReason != null)
                throw new InvalidOperationException(
                    "PREVIEW refused before display: " + staleReason);

            // The palette is modeless.  Surface elevation queries are reads, but Civil
            // still requires the active document to be locked while its database is
            // traversed.  Compute while the transaction is open, but do not touch the
            // transient manager until Abort AND Dispose have both succeeded.
            using (doc.LockDocument())
            {
                PreparedSectionPreview? prepared = null;
                Log?.Begin("workflow.preview.sample_surfaces", selectedRecordId);
                try
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            prepared = _previewService.PreparePreview(
                                doc.Database, tr, profile, plan, selectedRecordId);
                        }
                        finally
                        {
                            tr.Abort();
                        }
                    }

                    // This is intentionally outside the transaction using-scope.  If
                    // Abort or Dispose failed, control never reaches screen publication.
                    var preview = _previewService.PublishPreview(prepared);
                    prepared = null; // ownership was transferred to the preview service
                    Log?.End("workflow.preview.sample_surfaces",
                        $"record={preview.RecordId} drawables={preview.DrawableCount}");
                    CivilDeliveryUsage.Step(CivilDeliveryUsage.PreviewAction, ok: true);
                    return preview;
                }
                catch (Exception ex)
                {
                    prepared?.Dispose();
                    Log?.Fail("workflow.preview.sample_surfaces", ex);
                    throw;
                }
            }
        }

        public void ClearPreview() => _previewService.ClearPreview();

        public bool HasActivePreview => _previewService.HasActivePreview;

        public bool OwnsPreview(Database database) => _previewService.OwnsPreview(database);

        public void ClearPreviewForDocument(Document document) =>
            _previewService.ClearPreviewForDocument(document);

        public SectionApplyResult Apply(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, IReadOnlyCollection<string>? approvedRecordIds = null)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.ApplyAction);
            RequirePlanEvidence(plan);
            Log?.Begin("workflow.apply.get_civil_document");
            var civilDoc = CivilDocument.GetCivilDocument(doc.Database);
            Log?.End("workflow.apply.get_civil_document");

            var result = _applyService.Apply(doc, civilDoc, profile, plan, approvedRecordIds,
                runId: null, log: Log, profileHash: profileHash);

            PersistApplyEvidence(doc, profile, profileHash, plan, result);
            return result;
        }

        public SectionApplyResult ApplySelected(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, string selectedRecordId)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.ApplyAction);
            RequirePlanEvidence(plan);
            Log?.Begin("workflow.apply-selected.get_civil_document", selectedRecordId);
            var civilDoc = CivilDocument.GetCivilDocument(doc.Database);
            Log?.End("workflow.apply-selected.get_civil_document");

            var result = _applyService.ApplySelected(
                doc, civilDoc, profile, plan, selectedRecordId,
                runId: null, log: Log, profileHash: profileHash);

            PersistApplyEvidence(doc, profile, profileHash, plan, result);
            return result;
        }

        internal SectionApplyResult RebuildSelected(
            Document doc, ProjectProfile profile, string? profileHash, SectionPlan plan,
            string selectedRecordId, SectionVerifyResult failedVerification)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.ApplyAction);
            RequirePlanEvidence(plan);
            var stale = SectionPlanLogic.ScopeStaleReason(plan, DrawingScopeIdentity.For(doc), profile, profileHash)
                ?? SectionInputIntegrityService.StaleReason(doc.Database, plan, plan.SourceDatabaseRevision, "REBUILD-SELECTED", profile);
            var reason = SectionSelectedRebuildPolicy.Rejection(plan, selectedRecordId, failedVerification, stale == null);
            if (reason != null) throw new InvalidOperationException(reason);
            var producer = SectionVerificationRecoveryService.RecoverSelected(doc, plan, selectedRecordId);
            var authority = new SectionSelectedRebuildPolicy.Authority(plan, failedVerification, producer);
            SectionSelectedRebuildPolicy.RequirePublished(authority);
            var result = _applyService.RebuildSelected(doc, CivilDocument.GetCivilDocument(doc.Database),
                profile, plan, selectedRecordId, authority, Log, profileHash);
            result.Findings.Add(new DeliveryFinding
            {
                Code = "SEC-SELECTED-REBUILD-REQUESTED", Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Info, Title = "בנייה מחדש מפורשת לאחר אימות שנכשל",
                Message = $"selected={selectedRecordId}; failed_verify={failedVerification.RunId}; " +
                    $"producer_apply={producer.Applied.RunId}; immutable_plan={plan.RunId}; plan_action=Unchanged; " +
                    $"explicit owned selected rebuild requested; committed={result.Committed}; fresh VERIFY still required",
                AffectedRecordIds = new() { selectedRecordId },
                EvidenceRefs = new() { failedVerification.RunId, producer.Applied.RunId, plan.RunId },
            });
            PersistApplyEvidence(doc, profile, profileHash, plan, result);
            return result;
        }

        public SectionVerifyResult Verify(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult applied)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.VerifyAction);
            RequirePlanEvidence(plan);
            RequireApplyEvidence(applied);
            var staleReason = SectionPlanLogic.ScopeStaleReason(
                plan, DrawingScopeIdentity.For(doc), profile, profileHash);
            if (staleReason != null)
                throw new InvalidOperationException(
                    "VERIFY refused before read-back: " + staleReason);

            var db = doc.Database;
            SectionVerifyResult result;
            // The palette is modeless, so even Civil APIs that are logically reads may
            // require a write-open object internally. Hold the document lock for the
            // complete read-back and abort the transaction: VERIFY must never persist
            // a lazy Civil database update.
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Log?.Begin("workflow.verify.start_transaction");
                var civilDoc = CivilDocument.GetCivilDocument(db);
                Log?.End("workflow.verify.start_transaction");

                Log?.Begin("workflow.verify.read_back");
                result = _verifyService.Verify(db, tr, civilDoc, plan, applied, profile: profile);
                Log?.End("workflow.verify.read_back", $"records={result.Records.Count}");
                tr.Abort();
            }

            PersistVerifyEvidence(doc, profile, profileHash, plan, applied, result);
            return result;
        }

        /// <summary>
        /// Reads back exactly one independently applied section.  This is deliberately
        /// a separate contract from batch VERIFY: the returned artifact remains scoped
        /// to <paramref name="selectedRecordId"/> and cannot turn omitted PLAN records
        /// green.  Scope/profile freshness is checked before the database is opened;
        /// SectionVerifyService then checks the post-APPLY database revision and every
        /// external input before inspecting the complete per-record contract.
        /// </summary>
        public SectionVerifyResult VerifySelected(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult applied, string selectedRecordId)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.VerifyAction);
            RequirePlanEvidence(plan);
            RequireApplyEvidence(applied);
            var staleReason = SectionPlanLogic.ScopeStaleReason(
                plan, DrawingScopeIdentity.For(doc), profile, profileHash);
            if (staleReason != null)
                throw new InvalidOperationException(
                    "VERIFY SELECTED refused before read-back: " + staleReason);

            if (!string.Equals(applied.Scope, "selected-record", StringComparison.Ordinal) ||
                !string.Equals(applied.SelectedRecordId, selectedRecordId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "VERIFY SELECTED refused: APPLY result does not belong to the selected record.");

            var db = doc.Database;
            SectionVerifyResult result;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Log?.Begin("workflow.verify-selected.start_transaction", selectedRecordId);
                var civilDoc = CivilDocument.GetCivilDocument(db);
                Log?.End("workflow.verify-selected.start_transaction");

                Log?.Begin("workflow.verify-selected.read_back", selectedRecordId);
                result = _verifyService.VerifySelected(
                    db, tr, civilDoc, plan, applied, selectedRecordId, profile: profile);
                Log?.End("workflow.verify-selected.read_back",
                    $"record={selectedRecordId} checks={result.Records.Sum(r => r.Checks.Count)}");
                tr.Abort();
            }

            PersistVerifyEvidence(doc, profile, profileHash, plan, applied, result);
            return result;
        }

        internal sealed record CurrentSelectedVerification(
            SectionPlan Plan, SectionApplyResult Applied, string ProducerPlanRunId, SectionVerifyResult Result);

        internal sealed class VerificationRefreshRequiredException : InvalidOperationException
        {
            internal SectionPlan CurrentPlan { get; }

            internal VerificationRefreshRequiredException(SectionPlan currentPlan, Exception cause)
                : base(cause.Message, cause) => CurrentPlan = currentPlan;
        }

        /// <summary>
        /// New validation of an existing section, including re-PLAN/reopen. A fresh
        /// PLAN replaces stale input evidence; exact ownership selects its original
        /// producer artifacts. The verifier still proves live source/output geometry.
        /// </summary>
        internal CurrentSelectedVerification VerifySelectedCurrent(
            Document doc, ProjectProfile profile, string? profileHash, SectionPlan plan, string recordId)
        {
            using var usage = CivilDeliveryUsage.Begin(CivilDeliveryUsage.VerifyAction);
            RequirePlanEvidence(plan);
            var scopeReason = SectionPlanLogic.ScopeStaleReason(
                plan, DrawingScopeIdentity.For(doc), profile, profileHash);
            if (scopeReason != null) throw new InvalidOperationException(scopeReason);
            // A committed APPLY leaves the in-memory PLAN saying Update/Create for the
            // record it just replaced, and the revision counter may not have moved
            // (live 07/09 17:27: the first committed 1.2.48 section, then "האיתור מיועד
            // לחתך מנוהל שתוכנן ללא שינוי" 162 ms later). The recovery lookup accepts
            // only an Unchanged record, so verification always starts from a PLAN that
            // has seen the created view; a PLAN that already says Unchanged is reused.
            var planned = plan.Records.SingleOrDefault(r => r.RecordId == recordId);
            var current = SectionInputIntegrityService.StaleReason(
                doc.Database, plan, plan.SourceDatabaseRevision, "VERIFY-REFRESH", profile) == null &&
                planned is { Action: PlanAction.Unchanged }
                ? plan : Plan(doc, profile, profileHash);
            SectionVerificationRecoveryService.Authority authority;
            try { authority = SectionVerificationRecoveryService.RecoverSelected(doc, current, recordId); }
            catch (Exception ex)
            {
                // The refreshed PLAN is already published. Do not leave the palette
                // offering VERIFY for the stale record when current inputs require UPDATE.
                throw new VerificationRefreshRequiredException(current, ex);
            }
            SectionVerifyResult result;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                result = _verifyService.VerifyRecoveredSelected(doc.Database, tr,
                    CivilDocument.GetCivilDocument(doc.Database), current, authority, recordId, profile);
                tr.Abort();
            }
            result.VerifiedDatabaseRevision = DrawingRevisionTracker.Capture(doc.Database);
            PersistVerifyEvidence(doc, profile, profileHash, current, authority.Applied, result);
            return new(current, authority.Applied, authority.ProducerPlan.RunId, result);
        }

        // ------------------------------------------------------------- evidence

        /// <summary>
        /// The drawing transaction is already committed when evidence is written. A
        /// disk failure must not strand the engineer without the in-memory result
        /// (VERIFY needs it): keep the result, say the files are missing.
        /// </summary>
        internal static bool PersistApplyEvidence(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult result)
        {
            CivilDeliveryUsage.Step(CivilDeliveryUsage.ApplyAction, result.Committed);
            try
            {
                PersistEvidenceBundle(
                    result.RunId,
                    "apply_result.json",
                    result,
                    (pendingRoot, publishedRoot) => WriteApplyManifest(
                        doc, profile, profileHash, plan, result,
                        pendingRoot, publishedRoot));
                return true;
            }
            catch (Exception ex)
            {
                result.Findings.Add(EvidenceWriteFinding(result.RunId, ex));
                result.Status = DeliveryStatus.Failed;
                return false;
            }
        }

        internal static bool PersistVerifyEvidence(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult applied, SectionVerifyResult result)
        {
            CivilDeliveryUsage.Verified(doc, result);   // every verification is persisted here: one unit per verified section
            try
            {
                // Selected-record VERIFY also stages its diagnostic layout-input receipt
                // into this same private bundle before the manifest is written, so the
                // manifest lists and hashes it beside the result. A staging failure
                // fails the whole bundle like any other artifact. Batch VERIFY keeps
                // exactly the historical artifact set.
                var selectedScope = string.Equals(
                    result.Scope, "selected-record", StringComparison.Ordinal);
                Action<string>? stageLayoutInputs = selectedScope
                    ? pendingRoot => SectionLayoutInputCapture.Stage(
                        pendingRoot, doc, profile, profileHash, plan, applied, result)
                    : null;
                PersistEvidenceBundle(
                    result.RunId,
                    "verify_result.json",
                    result,
                    (pendingRoot, publishedRoot) => WriteVerifyManifest(
                        doc, profile, profileHash, plan, applied, result,
                        pendingRoot, publishedRoot),
                    stageAdditionalArtifacts: stageLayoutInputs);
                return true;
            }
            catch (Exception ex)
            {
                result.Findings.Add(EvidenceWriteFinding(result.RunId, ex));
                result.Status = DeliveryStatus.Failed;
                return false;
            }
        }

        internal static void PersistEvidenceBundle<T>(
            string runId,
            string artifactName,
            T result,
            Action<string, string> writeManifest,
            bool replaceExisting = false,
            IReadOnlyCollection<string>? removeArtifacts = null,
            Action<string>? stageAdditionalArtifacts = null,
            string? runsRoot = null,
            IReadOnlyCollection<string>? replaceArtifacts = null,
            Action<string, string>? moveDirectory = null)
        {
            // Production uses the normal run store; an explicit root permits real
            // filesystem transaction tests without touching a user's run evidence.
            var publicationRoot = runsRoot ?? RunsRoot;
            Directory.CreateDirectory(publicationRoot);
            var pendingRoot = Path.Combine(
                publicationRoot, ".pending-" + runId + "-" + Guid.NewGuid().ToString("N"));
            var pendingRun = Path.Combine(pendingRoot, runId);
            var finalRun = Path.Combine(publicationRoot, runId);
            var backupRun = Path.Combine(
                publicationRoot, ".replaced-" + runId + "-" + Guid.NewGuid().ToString("N"));
            var replaced = new HashSet<string>(replaceArtifacts ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var removed = new HashSet<string>(removeArtifacts ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var name in replaced.Concat(removed).Append(artifactName))
            {
                if (string.IsNullOrWhiteSpace(name) ||
                    !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
                    name is "." or "..")
                    throw new InvalidDataException("Evidence staging accepts file names only.");
            }
            if (replaced.Overlaps(removed))
                throw new InvalidDataException("An evidence file cannot be both replaced and removed.");
            var notCopied = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
            notCopied.UnionWith(replaced);
            notCopied.Add(artifactName);
            notCopied.Add("run_manifest.json");
            var published = false;
            try
            {
                if (replaceExisting)
                {
                    if (!Directory.Exists(finalRun))
                        throw new DirectoryNotFoundException(
                            $"The prerequisite evidence run does not exist: {finalRun}");
                    // Do not copy hundreds of MB of canonical scan files that this
                    // transaction immediately replaces. Historical and unrelated
                    // artifacts are still copied unchanged; no hard links are used.
                    CopyDirectoryStrict(finalRun, pendingRun, notCopied);
                }
                foreach (var obsoleteName in removeArtifacts ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(obsoleteName) ||
                        !string.Equals(Path.GetFileName(obsoleteName), obsoleteName,
                            StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "Evidence removal accepts file names only.");
                    var obsoletePath = Path.Combine(pendingRun, obsoleteName);
                    if (File.Exists(obsoletePath)) File.Delete(obsoletePath);
                }
                WriteArtifact(runId, artifactName, result, pendingRoot);
                stageAdditionalArtifacts?.Invoke(pendingRoot);
                foreach (var replacementName in replaced)
                    if (!File.Exists(Path.Combine(pendingRun, replacementName)))
                        throw new IOException($"Replacement evidence was not staged: {replacementName}");
                writeManifest(pendingRoot, publicationRoot);
                var pendingArtifact = Path.Combine(pendingRun, artifactName);
                var pendingManifest = Path.Combine(pendingRun, "run_manifest.json");
                if (!File.Exists(pendingArtifact) || !File.Exists(pendingManifest))
                    throw new IOException(
                        "The evidence bundle did not produce both its result and manifest.");
                if (!replaceExisting && Directory.Exists(finalRun))
                    throw new IOException(
                        $"The unique evidence run directory already exists: {finalRun}");

                if (!replaceExisting)
                {
                    // Same-volume directory rename is the publication boundary: before
                    // it neither file is authoritative; after it both are visible.
                    MoveEvidenceDirectory(pendingRun, finalRun, moveDirectory);
                    published = true;
                }
                else
                {
                    // Rebuild/export replace an existing scan evidence generation. Seed
                    // a complete sibling copy, then swap whole directories; never write
                    // result and manifest independently into the authoritative run.
                    MoveEvidenceDirectory(finalRun, backupRun, moveDirectory);
                    try
                    {
                        MoveEvidenceDirectory(pendingRun, finalRun, moveDirectory);
                        published = true;
                    }
                    catch (Exception publicationError)
                    {
                        try
                        {
                            if (Directory.Exists(finalRun) || File.Exists(finalRun) || !Directory.Exists(backupRun))
                                throw new IOException("The original evidence cannot be restored without overwriting a changed destination.");
                            MoveEvidenceDirectory(backupRun, finalRun, moveDirectory);
                        }
                        catch (Exception restorationError)
                        {
                            // Never discard the only old generation merely because
                            // a destination now exists. Preserve the exact backup and
                            // both failures so support can recover without guessing.
                            throw new AggregateException(
                                $"Evidence publication and restoration failed. Original evidence is retained where present: {backupRun}; destination: {finalRun}.",
                                publicationError, restorationError);
                        }
                        throw;
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(pendingRoot))
                        Directory.Delete(pendingRoot, recursive: true);
                }
                catch { }
                try
                {
                    if (published && Directory.Exists(backupRun) && Directory.Exists(finalRun))
                        Directory.Delete(backupRun, recursive: true);
                }
                catch { }
            }
        }

        internal static void MoveEvidenceDirectory(
            string source, string destination,
            Action<string, string>? move = null,
            Action<int>? wait = null,
            Func<string, bool>? sourceDirectoryExists = null,
            Func<string, bool>? destinationExists = null)
        {
            move ??= Directory.Move;
            wait ??= System.Threading.Thread.Sleep;
            sourceDirectoryExists ??= Directory.Exists;
            destinationExists ??= path => Directory.Exists(path) || File.Exists(path);
            // Same-volume rename only. Four attempts, at most 250 ms of waiting;
            // persistent access errors still fail and retain their original error.
            int[] delays = { 25, 75, 150 };
            for (var attempt = 0; ; attempt++)
            {
                try { move(source, destination); return; }
                catch (Exception error) when (
                    attempt < delays.Length &&
                    (error is IOException || error is UnauthorizedAccessException) &&
                    (unchecked((uint)error.HResult) & 0xffff0000u) == 0x80070000u &&
                    (error.HResult & 0xffff) is 5 or 32 or 33 &&
                    sourceDirectoryExists(source) && !destinationExists(destination))
                {
                    wait(delays[attempt]);
                    // A contender may have changed either name while we waited.
                    // Do not even attempt another move in that changed state.
                    if (!sourceDirectoryExists(source) || destinationExists(destination)) throw;
                }
            }
        }

        private static void CopyDirectoryStrict(string source, string destination,
            ISet<string>? excludedRootFiles = null)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                if (excludedRootFiles?.Contains(Path.GetFileName(file)) != true)
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            foreach (var child in Directory.GetDirectories(source))
                CopyDirectoryStrict(child,
                    Path.Combine(destination, Path.GetFileName(child)));
        }

        private static DeliveryFinding EvidenceWriteFinding(string runId, Exception ex) => new()
        {
            Code = SectionFindingCodes.EvidenceWriteFailed,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Error,
            Title = "התוצאה נשמרה בזיכרון בלבד — כתיבת קובצי הראיות של הריצה נכשלה",
            Message = $"run={runId}; {ex.GetType().Name}: {ex.Message}",
            RecommendedAction = "יש לבדוק את תיקיית הריצות (%LOCALAPPDATA%\\MahodAI_Civil3D\\civil-delivery\\runs), " +
                                "לתקן הרשאות/מקום פנוי ולהריץ APPLY מחדש; אין להמשיך לאימות או למסירה.",
        };

        public static string RunsRoot => RunManifestWriter.DefaultRunsRoot;

        public static string WriteArtifact<T>(
            string runId, string name, T payload, string? runsRoot = null)
        {
            var dir = Path.Combine(runsRoot ?? RunsRoot, runId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            AtomicTextFile.WriteAllText(path, JsonSerializer.Serialize(payload, Json));
            return path;
        }

        internal static string WriteCompressedArtifact<T>(
            string runId, string name, T payload, string runsRoot)
        {
            // Only private, not-yet-published bundle staging calls this method.
            // The entire original JSON is retained losslessly, not just a digest or
            // a selected subset of records. Canonical runtime JSON stays unchanged.
            var directory = Path.Combine(runsRoot, runId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                JsonSerializer.Serialize(gzip, payload, Json);
            file.Flush(flushToDisk: true);
            return path;
        }

        internal static void WritePlanManifest(
            Document doc, ProjectProfile profile, string? profileHash, SectionPlan plan)
        {
            RuntimeRunManifestService.Write(
                plan.RunId, "sections", "plan", profile.ProfileId, profileHash,
                plan.Status, new Dictionary<string, int>
                {
                    ["cl_records"] = plan.Records.Count,
                    ["ready"] = plan.Records.Count(r => r.Status == DeliveryStatus.Ready),
                    ["review_required"] = plan.Records.Count(r => r.Status == DeliveryStatus.ReviewRequired),
                    ["traffic_direction_resolved"] = plan.Records
                        .SelectMany(r => r.TrafficDirections).Count(d => d.IsResolved),
                    ["traffic_direction_unresolved"] = plan.Records
                        .SelectMany(r => r.TrafficDirections).Count(d => !d.IsResolved),
                }, plan.Records.SelectMany(r => r.Findings).Concat(plan.Findings),
                SectionManifestInputs(doc, profile, plan));
        }

        internal static void WriteApplyManifest(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult result, string? runsRoot = null,
            string? publishedRunsRoot = null)
        {
            var planProof = RequirePlanEvidence(plan);
            var selectedScope = string.Equals(
                result.Scope, "selected-record", StringComparison.Ordinal);
            var manifestedRecordIds = result.Records
                .Select(record => record.RecordId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            RuntimeRunManifestService.Write(
                result.RunId, "sections", selectedScope ? "apply-selected" : "apply",
                profile.ProfileId, profileHash,
                result.Status, new Dictionary<string, int>
                {
                    ["applied"] = result.Records.Count(r => r.Status == DeliveryStatus.Applied),
                    ["unchanged"] = result.Records.Count(r => r.ActionTaken == PlanAction.Unchanged),
                    ["failed"] = result.Records.Count(r => r.Status == DeliveryStatus.Failed),
                    ["traffic_direction_arrows"] = result.Records
                        .Sum(r => r.TrafficDirectionArrows.Count),
                    ["scope_selected_record"] = selectedScope ? 1 : 0,
                    ["scope_batch"] = selectedScope ? 0 : 1,
                    ["plan_total"] = plan.Records.Count,
                    ["manifest_records"] = manifestedRecordIds,
                    ["omitted_plan_records"] = Math.Max(0,
                        plan.Records.Count - manifestedRecordIds),
                }, result.Records.SelectMany(r => r.Findings).Concat(result.Findings),
                SectionManifestInputs(doc, profile, plan),
                new[] { new RunManifestArtifactInput(planProof.Path, planProof.Hash) },
                scope: result.Scope,
                selectedRecordId: result.SelectedRecordId,
                runsRoot: runsRoot,
                publishedRunsRoot: publishedRunsRoot);
        }

        internal static void WriteVerifyManifest(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult applied, SectionVerifyResult result,
            string? runsRoot = null, string? publishedRunsRoot = null)
        {
            var planProof = RequirePlanEvidence(plan);
            var applyProof = RequireApplyEvidence(applied);
            var selectedScope = string.Equals(
                result.Scope, "selected-record", StringComparison.Ordinal);
            var verifiedRecordIds = result.Records
                .Select(record => record.RecordId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            RuntimeRunManifestService.Write(
                result.RunId, "sections", selectedScope ? "verify-selected" : "verify",
                profile.ProfileId, profileHash,
                result.Status, new Dictionary<string, int>
                {
                    ["verified"] = result.Records.Count(r => r.Status == DeliveryStatus.Verified),
                    ["failed"] = result.Records.Count(r => r.Status == DeliveryStatus.Failed),
                    ["checks"] = result.Records.Sum(r => r.Checks.Count),
                    ["scope_selected_record"] = selectedScope ? 1 : 0,
                    ["scope_batch"] = selectedScope ? 0 : 1,
                    ["plan_total"] = plan.Records.Count,
                    ["manifest_records"] = verifiedRecordIds,
                    ["omitted_plan_records"] = Math.Max(0,
                        plan.Records.Count - verifiedRecordIds),
                }, result.Findings,
                SectionManifestInputs(doc, profile, plan),
                new[]
                {
                    new RunManifestArtifactInput(planProof.Path, planProof.Hash),
                    new RunManifestArtifactInput(applyProof.Path, applyProof.Hash),
                },
                scope: result.Scope,
                selectedRecordId: result.SelectedRecordId,
                runsRoot: runsRoot,
                publishedRunsRoot: publishedRunsRoot);
        }

        internal static IEnumerable<RunManifestInput> SectionManifestInputs(
            Document doc, ProjectProfile profile, SectionPlan plan)
        {
            string? hostPath = null;
            try { hostPath = doc.Database.Filename; } catch { }
            yield return new RunManifestInput(hostPath);
            yield return new RunManifestInput(plan.SourceDrawing);

            foreach (var configured in profile.Sections.Cl.SourceFiles)
                yield return new RunManifestInput(ResolveDrawingPath(configured, hostPath));

            foreach (var external in plan.ExternalSources)
                yield return new RunManifestInput(external.SourcePath, external.Sha256);

            foreach (var record in plan.Records)
            {
                var cl = record.Cl;
                // Legacy plans used SourceXref as the path; current plans keep it as
                // a readable insert chain and store the resolved path explicitly.
                var legacyClXrefPath = string.IsNullOrWhiteSpace(cl.SourceDrawingPath)
                    ? ResolveDrawingPath(cl.SourceXref, hostPath) : null;
                yield return new RunManifestInput(
                    cl.SourceDrawingPath ?? legacyClXrefPath ??
                    ResolveDrawingPath(cl.SourceDrawing, hostPath),
                    cl.SourceDrawingHash);

                foreach (var projected in record.ProjectedEntities)
                {
                    var legacyProjectionPath = string.IsNullOrWhiteSpace(projected.SourceDrawingPath)
                        ? ResolveDrawingPath(projected.SourceXref, hostPath) : null;
                    yield return new RunManifestInput(
                        projected.SourceDrawingPath ?? legacyProjectionPath,
                        projected.SourceDrawingHash);
                }
            }
        }

        private static string? ResolveDrawingPath(string? path, string? hostPath)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.StartsWith("UNSAVED", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
                var hostDir = string.IsNullOrWhiteSpace(hostPath)
                    ? null : Path.GetDirectoryName(hostPath);
                return string.IsNullOrWhiteSpace(hostDir)
                    ? path
                    : Path.GetFullPath(Path.Combine(hostDir, path));
            }
            catch
            {
                return path;
            }
        }

        internal static PublishedArtifactProof RequirePlanEvidence(SectionPlan plan) =>
            RuntimeRunManifestService.RequirePublishedArtifact(
                plan.RunId, "section_plan.json", plan, Json,
                "sections", "plan");

        internal static PublishedArtifactProof RequireApplyEvidence(
            SectionApplyResult applied) =>
            RuntimeRunManifestService.RequirePublishedArtifact(
                applied.RunId, "apply_result.json", applied, Json,
                "sections", "apply", "apply-selected");
    }

    internal static class DrawingScopeIdentity
    {
        internal static string For(Document doc)
        {
            string? fingerprint = null;
            try
            {
                fingerprint = doc.Database.GetType().GetProperty("FingerprintGuid")
                    ?.GetValue(doc.Database)?.ToString();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(fingerprint))
                fingerprint = System.Runtime.CompilerServices.RuntimeHelpers
                    .GetHashCode(doc.Database).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return FromParts(doc.Database.Filename, doc.Name, fingerprint);
        }

        internal static string FromParts(
            string? savedPath, string? documentName, string? databaseFingerprint)
        {
            if (!string.IsNullOrWhiteSpace(savedPath)) return savedPath!;
            if (!string.IsNullOrWhiteSpace(documentName)) return "UNSAVED:" + documentName;
            return "UNSAVED-DB:" + (databaseFingerprint ?? "UNKNOWN");
        }
    }

    /// <summary>Profile resolution for installed and development environments.</summary>
    public static class ProfileLocator
    {
        public static string? Resolve(string profileIdOrPath)
        {
            if (string.IsNullOrWhiteSpace(profileIdOrPath)) return null;
            if (File.Exists(profileIdOrPath)) return profileIdOrPath;

            foreach (var candidate in SearchPaths(profileIdOrPath))
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public static string DescribeSearchPaths(string profileId) =>
            string.Join(Environment.NewLine, SearchPaths(profileId));

        private static IEnumerable<string> SearchPaths(string profileId)
        {
            var env = Environment.GetEnvironmentVariable("MHD_PROFILE_DIR");
            if (!string.IsNullOrEmpty(env))
                yield return Path.Combine(env, profileId, "project-profile.yaml");

            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery", "profiles", profileId, "project-profile.yaml");

            var dllDir = Path.GetDirectoryName(typeof(ProfileLocator).Assembly.Location);
            if (!string.IsNullOrEmpty(dllDir))
            {
                yield return Path.Combine(dllDir, "profiles", profileId, "project-profile.yaml");
                // Development checkout: <repo>/profiles/civil-delivery/<id>/
                yield return Path.GetFullPath(Path.Combine(
                    dllDir, "..", "..", "..", "..", "profiles", "civil-delivery", profileId, "project-profile.yaml"));
            }
        }
    }
}
