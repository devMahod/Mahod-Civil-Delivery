using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Host adapter around the Autodesk-free SectionSourceIntegrityLogic. This is the
    /// single source/unit/revision gate used by direct workflow and AI tool routes.
    /// </summary>
    internal static class SectionInputIntegrityService
    {
        internal static void CapturePlanDatabaseState(Database db, SectionPlan plan, ProjectProfile? profile)
        {
            ArgumentNullException.ThrowIfNull(db);
            ArgumentNullException.ThrowIfNull(plan);
            plan.SourceUnitCode = (int)db.Insunits;
            plan.SourceUnitName = db.Insunits.ToString();
            var units = HostDrawingUnitService.Resolve(db, profile);
            plan.PhysicalUnitCode = units.EffectiveUnitCode;
            plan.PhysicalUnitDeclarationDigest = units.DeclarationDigest;
            plan.SourceDrawingFingerprint = db.FingerprintGuid;
            plan.PhysicalUnitEvidence = units.Evidence;
            plan.SourceDatabaseRevision = DrawingRevisionTracker.Capture(db);
        }

        internal static DeliveryFinding? ValidatePlanUnits(Database db, string projectProfileId, ProjectProfile? profile)
        {
            var code = (int)db.Insunits;
            var units = HostDrawingUnitService.Resolve(db, profile);
            if (units.AllowsMetricSections) return null;
            return new DeliveryFinding
            {
                Code = SectionFindingCodes.UnitNotMetres,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = "יחידות השרטוט אינן מטרים — תכנון החתכים חסום",
                Message = $"INSUNITS={db.Insunits} ({code}); גאומטריית החתכים, רוחבי הרצועות והבלוקים מוגדרים במטרים. {units.FailureReason}",
                RecommendedAction = "בלשונית פרויקט פתח יחידות פיזיות. אם השרטוט מצויר במטרים (Unitless, או יחידה רשומה אחרת שנבדקה מול ראיה), שמור הכרעת מהנדס מנומקת והריץ תכנון מחדש. חתכים נבנים במטרים בלבד; שרטוט שיחידתו הפיזית אחרת אינו נתמך לחתכים. הכלי אינו משנה את DWG או INSUNITS.",
                ProjectProfileId = projectProfileId,
            };
        }

        internal static IReadOnlyList<DeliveryFinding> ValidateCurrent(
            Database db,
            SectionPlan plan,
            string? expectedDatabaseRevision,
            string stage, ProjectProfile? profile)
        {
            var failures = new List<SectionSourceIntegrityLogic.Failure>();
            var units = HostDrawingUnitService.Resolve(db, profile);
            if (!MatchesCurrentUnits(plan, units, db.FingerprintGuid, db.Filename))
                failures.Add(new SectionSourceIntegrityLogic.Failure(
                    SectionSourceIntegrityLogic.FailureKind.UnitNotMetres,
                    $"Physical-unit evidence differs from PLAN or is not approved metres. Raw INSUNITS={units.RawUnitCode}; {units.FailureCode}: {units.FailureReason}"));

            var revision = SectionSourceIntegrityLogic.ValidateRevision(
                expectedDatabaseRevision, DrawingRevisionTracker.Capture(db));
            if (revision != null) failures.Add(revision);

            return failures.Select(failure => ToFinding(failure, plan, stage))
                .Concat(ValidateExternalSourcesCurrent(plan, stage)).ToList();
        }

        internal static IReadOnlyList<DeliveryFinding> ValidateExternalSourcesCurrent(
            SectionPlan plan, string stage)
        {
            var evidenceCoverage = ValidateEvidenceCoverage(plan, stage);
            var failures = SectionSourceIntegrityLogic.ValidateExternalSources(
                plan.ExternalSources.Select(source =>
                    new SectionSourceIntegrityLogic.ExternalSourceSnapshot(
                        source.SourcePath, source.Sha256,
                        source.LiveDatabaseRevision, source.RequiresLiveDatabase)),
                ProbeExternalSource);
            return failures.Select(failure => ToFinding(failure, plan, stage))
                .Concat(evidenceCoverage).ToList();
        }

        internal static string? StaleReason(
            Database db,
            SectionPlan plan,
            string? expectedDatabaseRevision,
            string stage, ProjectProfile? profile)
        {
            var findings = ValidateCurrent(db, plan, expectedDatabaseRevision, stage, profile);
            return findings.Count == 0
                ? null
                : string.Join(" | ", findings.Select(f => $"{f.Code}: {f.Title} ({f.Message})"));
        }

        // Explicit metres without a decision, or a reviewed host decision of metres bound to its digest, GUID and
        // path — from unitless (the original route) or, since b24, from any reviewed explicit INSUNITS. A reviewed
        // non-metre unit (for example confirmed feet) never reaches here: AllowsMetricSections is false for it.
        internal static bool HasMetricUnitEvidence(SectionPlan plan) =>
            string.IsNullOrEmpty(plan.PhysicalUnitDeclarationDigest)
                ? plan.SourceUnitCode == PhysicalDrawingUnitPolicy.Metres &&
                  (plan.PhysicalUnitCode == null || plan.PhysicalUnitCode == PhysicalDrawingUnitPolicy.Metres)
                : plan.PhysicalUnitCode == PhysicalDrawingUnitPolicy.Metres &&
                  CatalogIdentity.IsValidSha256(plan.PhysicalUnitDeclarationDigest) &&
                  Guid.TryParse(plan.SourceDrawingFingerprint, out var guid) && guid != Guid.Empty &&
                  PhysicalDrawingUnitPolicy.CanonicalDrawingPath(plan.SourceDrawing) != null;

        internal static bool MatchesCurrentUnits(SectionPlan plan,
            PhysicalDrawingUnitPolicy.Resolution units, string fingerprint, string? drawingPath) =>
            HasMetricUnitEvidence(plan) && units.AllowsMetricSections &&
            plan.SourceUnitCode == units.RawUnitCode &&
            (plan.PhysicalUnitCode ?? plan.SourceUnitCode) == units.EffectiveUnitCode &&
            string.Equals(plan.PhysicalUnitDeclarationDigest, units.DeclarationDigest, StringComparison.Ordinal) &&
            (!units.UsedDeclaration || (Guid.TryParse(plan.SourceDrawingFingerprint, out var planned) &&
                Guid.TryParse(fingerprint, out var current) && planned == current &&
                string.Equals(PhysicalDrawingUnitPolicy.CanonicalDrawingPath(plan.SourceDrawing),
                    PhysicalDrawingUnitPolicy.CanonicalDrawingPath(drawingPath), StringComparison.Ordinal)));

        private static bool HasStructuralPlanningIntegrityBlocker(SectionPlan plan) =>
            !HasMetricUnitEvidence(plan) ||
            string.IsNullOrWhiteSpace(plan.SourceDatabaseRevision) ||
            plan.Records.Count == 0 ||
            ValidateEvidenceCoverage(plan, "PLAN").Count > 0;

        private static bool IsUnresolvedBlocker(DeliveryFinding finding) =>
            finding.Severity >= FindingSeverity.ReviewRequired &&
            (finding.ResolvedAtUtc == null ||
             string.IsNullOrWhiteSpace(finding.ResolvedBy) ||
             string.IsNullOrWhiteSpace(finding.Resolution));

        /// <summary>
        /// Strict batch predicate: every unresolved PLAN-level finding blocks. A
        /// finding without affected ids is global by definition.
        /// </summary>
        internal static bool HasPlanningIntegrityBlocker(SectionPlan plan) =>
            HasStructuralPlanningIntegrityBlocker(plan) ||
            plan.Findings.Any(IsUnresolvedBlocker);

        /// <summary>
        /// Selected-record predicate. Global findings (no affected ids), source/input
        /// integrity and findings that explicitly affect this record remain blocking;
        /// a finding scoped only to another record cannot prevent an independently
        /// READY section from being applied or verified.
        /// </summary>
        internal static bool HasSelectedPlanningIntegrityBlocker(
            SectionPlan plan, SectionPlanRecord selected) =>
            HasStructuralPlanningIntegrityBlocker(plan) ||
            plan.Findings.Any(finding => IsUnresolvedBlocker(finding) &&
                (finding.AffectedRecordIds.Count == 0 ||
                 finding.AffectedRecordIds.Contains(
                     selected.RecordId, StringComparer.Ordinal))) ||
            selected.Findings.Any(IsUnresolvedBlocker);

        internal static IReadOnlyList<DeliveryFinding> SelectedPlanningBlockers(
            SectionPlan plan, SectionPlanRecord selected) =>
            plan.Findings.Where(finding => IsUnresolvedBlocker(finding) &&
                    (finding.AffectedRecordIds.Count == 0 ||
                     finding.AffectedRecordIds.Contains(
                         selected.RecordId, StringComparer.Ordinal)))
                .Concat(selected.Findings.Where(IsUnresolvedBlocker))
                .Distinct()
                .ToList();

        internal static AnnotationInventoryLogic.RepairScopeVerdict AnnotationRecoveryScope(
            SectionPlan plan, IEnumerable<string> targetRecordIds) =>
            AnnotationInventoryLogic.EvaluateRepairScope(plan.Findings
                .Concat(plan.Records.SelectMany(r => r.Findings))
                .Where(f => f.Code == SectionFindingCodes.AnnotationRegistryRepairable)
                .SelectMany(f => f.AffectedRecordIds), targetRecordIds);

        /// <summary>APPLY-only preflight. VERIFY retains the original strict gate:
        /// an already committed batch must not be blocked by old PLAN repair warnings.</summary>
        internal static DeliveryFinding? SelectedAnnotationRecoveryScopeFinding(
            SectionPlan plan, SectionPlanRecord selected)
        {
            var scope = AnnotationRecoveryScope(plan, new[] { selected.RecordId });
            if (scope.CanProceed) return null;
            return new DeliveryFinding
            {
                Code = SectionFindingCodes.AnnotationRepairScopeRequired,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.ReviewRequired,
                Title = scope.RequiresBatch
                    ? "רישום הערות של כמה חתכים דורש תיקון — נדרשת אצווה הכוללת את כולם"
                    : "יש לעדכן קודם את החתך שרישום ההערות שלו דורש תיקון",
                Message = $"selected_record={selected.RecordId}; required_repair_records={string.Join(",", scope.RequiredRecordIds)}; " +
                    $"outside_selected_scope={string.Join(",", scope.OutsideTargetRecordIds)}; requires_batch={scope.RequiresBatch}",
                RecommendedAction = scope.RequiresBatch
                    ? "יש להשלים את אישורי האצווה ולהחיל אצווה הכוללת את כל חתכי התיקון המפורטים. " +
                      "החלת חתך יחיד לא תתקן רישום של חתכים אחרים. לא בוצע שינוי."
                    : $"יש לבחור את {scope.RequiredRecordIds[0]} ולהחיל את עדכונו; לאחר הצלחה להריץ PLAN מחדש. לא בוצע שינוי.",
                AffectedRecordIds = scope.RequiredRecordIds.ToList(),
            };
        }

        internal static bool HasSelectedApplyPlanningIntegrityBlocker(SectionPlan plan, SectionPlanRecord selected) =>
            HasSelectedPlanningIntegrityBlocker(plan, selected) ||
            SelectedAnnotationRecoveryScopeFinding(plan, selected) != null;

        internal static IReadOnlyList<DeliveryFinding> SelectedApplyPlanningBlockers(SectionPlan plan, SectionPlanRecord selected)
        {
            var findings = SelectedPlanningBlockers(plan, selected).ToList();
            if (SelectedAnnotationRecoveryScopeFinding(plan, selected) is { } recovery) findings.Add(recovery);
            return findings;
        }

        private static IReadOnlyList<DeliveryFinding> ValidateEvidenceCoverage(
            SectionPlan plan, string stage)
        {
            var expected = plan.Records
                .Select(record => record.Cl.SourceDrawingPath)
                .Concat(plan.Records.SelectMany(record => record.ProjectedEntities)
                    .Select(projected => projected.SourceDrawingPath))
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                               !SamePathOrScope(path!, plan.SourceDrawing))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missing = expected.Where(path => plan.ExternalSources.All(source =>
                    !SamePath(source.SourcePath, path!)))
                .ToList();
            if (missing.Count == 0) return Array.Empty<DeliveryFinding>();
            return new[]
            {
                new DeliveryFinding
                {
                    Code = SectionFindingCodes.ExternalSourceChanged,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = $"{stage} סורב — חסרה ראיית SHA-256 למקור חיצוני ששימש את החתכים",
                    Message = string.Join(" | ", missing),
                    RecommendedAction = "יש להריץ PLAN מחדש בגרסה הנוכחית; אין להשתמש בתכנית ישנה ללא ראיות מקור.",
                    ProjectProfileId = plan.ProjectProfileId,
                    AffectedRecordIds = plan.Records.Select(r => r.RecordId).ToList(),
                }
            };
        }

        private static SectionSourceIntegrityLogic.ExternalSourceProbe ProbeExternalSource(
            string path)
        {
            if (!File.Exists(path))
                return new SectionSourceIntegrityLogic.ExternalSourceProbe(
                    Exists: false, Sha256: null, Error: "הקובץ החיצוני אינו קיים.");

            var hash = ClInstructionReader.HashFileShared(path);
            string? liveRevision = null;
            try
            {
                foreach (Document document in AcadApp.DocumentManager)
                {
                    string? currentPath = null;
                    try { currentPath = document.Database?.Filename; } catch { }
                    if (string.IsNullOrWhiteSpace(currentPath) || !SamePath(path, currentPath))
                        continue;
                    var liveDb = document.Database;
                    if (liveDb != null)
                        liveRevision = DrawingRevisionTracker.Capture(liveDb);
                    break;
                }
            }
            catch { /* headless tests / no DocumentManager => no live proof */ }

            return new SectionSourceIntegrityLogic.ExternalSourceProbe(
                Exists: true,
                Sha256: string.IsNullOrWhiteSpace(hash) ? null : hash,
                LiveDatabaseRevision: liveRevision,
                Error: string.IsNullOrWhiteSpace(hash)
                    ? "לא ניתן לחשב SHA-256 לקובץ החיצוני." : null);
        }

        private static DeliveryFinding ToFinding(
            SectionSourceIntegrityLogic.Failure failure,
            SectionPlan plan,
            string stage)
        {
            var unit = failure.Kind == SectionSourceIntegrityLogic.FailureKind.UnitNotMetres;
            var revision = failure.Kind is
                SectionSourceIntegrityLogic.FailureKind.RevisionUnavailable or
                SectionSourceIntegrityLogic.FailureKind.RevisionChanged;
            return new DeliveryFinding
            {
                Code = unit
                    ? SectionFindingCodes.UnitNotMetres
                    : revision
                        ? SectionFindingCodes.DrawingChanged
                        : SectionFindingCodes.ExternalSourceChanged,
                Domain = SectionPlanLogic.Domain,
                Severity = FindingSeverity.Error,
                Title = unit
                    ? $"{stage} סורב — יחידות החתכים אינן מטרים"
                    : revision
                        ? $"{stage} סורב — השרטוט השתנה מאז שלב הראיה הקודם"
                        : $"{stage} סורב — מקור חיצוני השתנה או אינו ניתן לאימות",
                Message = string.IsNullOrWhiteSpace(failure.Path)
                    ? failure.Message
                    : $"{failure.Message} source={failure.Path}",
                RecommendedAction = unit
                    ? "יש לבדוק יחידות פיזיות בלשונית פרויקט ולהריץ תכנון מחדש. אישור היחידות קשור לשרטוט המסוים ואינו עוקף שינוי במקור."
                    : "יש לרענן/לתקן את המקורות ולהריץ PLAN מחדש לפני כל כתיבה.",
                ProjectProfileId = plan.ProjectProfileId,
                AffectedRecordIds = plan.Records.Select(r => r.RecordId).ToList(),
            };
        }

        private static bool SamePath(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool SamePathOrScope(string first, string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope) ||
                scope.StartsWith("UNSAVED", StringComparison.OrdinalIgnoreCase))
                return false;
            return SamePath(first, scope);
        }
    }
}
