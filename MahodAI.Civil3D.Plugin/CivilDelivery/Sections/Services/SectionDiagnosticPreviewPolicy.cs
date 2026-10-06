using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Closed, host-free eligibility policy for the geometry-only transient preview.
    /// PREVIEW is a diagnostic aid; this policy never changes a PLAN record to READY
    /// and never relaxes the independent complete-batch gate used by APPLY.
    /// </summary>
    internal static class SectionDiagnosticPreviewPolicy
    {
        private static readonly HashSet<string> AllowedReviewFindingCodes = new(
            StringComparer.Ordinal)
        {
            SectionFindingCodes.PresentationCoverageMissing,
            SectionFindingCodes.TrafficDirectionUnresolved,
        };

        /// <summary>
        /// READY records retain their existing layout behaviour. A ReviewRequired
        /// record receives a deterministic PLAN position only when it is safe for the
        /// diagnostic preview without that position. This writes only the PLAN model;
        /// it does not authorise APPLY or create any DWG object.
        /// </summary>
        internal static bool CanAssignLayout(SectionPlanRecord record)
        {
            if (record == null || record.ManualSectionReuse != null)
                return false;
            if (record.Status == DeliveryStatus.Ready)
                return true;
            return RecordBlockReason(record, requireLayout: false) == null;
        }

        internal static bool CanPreview(SectionPlan plan, SectionPlanRecord record) =>
            PreviewBlockReason(plan, record) == null;

        internal static string? PreviewBlockReason(
            SectionPlan? plan,
            SectionPlanRecord? record)
        {
            if (plan == null)
                return "אין תוכנית חתכים עדכנית לתצוגת גאומטריה.";
            if (plan.Status is DeliveryStatus.Blocked or DeliveryStatus.Failed ||
                plan.Findings.Any(finding => finding.Severity == FindingSeverity.Error))
            {
                return "התוכנית חסומה בגלל ממצא שגיאה. בדיקת גאומטריה חלקית לא הוצגה.";
            }
            if (!SectionInputIntegrityService.HasMetricUnitEvidence(plan) ||
                string.IsNullOrWhiteSpace(plan.SourceDatabaseRevision))
            {
                return "לא ניתן להוכיח את יחידות השרטוט ואת גרסת מסד הנתונים של התוכנית.";
            }
            if (record == null || !plan.Records.Contains(record))
                return "החתך שנבחר אינו שייך עוד לתוכנית הפעילה. יש להריץ תכנון מחדש.";

            return RecordBlockReason(record, requireLayout: true);
        }

        private static string? RecordBlockReason(
            SectionPlanRecord record,
            bool requireLayout)
        {
            if (record.Status == DeliveryStatus.ReviewRequired)
            {
                if (record.Action != PlanAction.ReviewRequired)
                    return "מצב החתך אינו עקבי ולכן תצוגת האבחון נחסמה.";

                var reviewFindings = record.Findings
                    .Where(finding => finding.Severity == FindingSeverity.ReviewRequired)
                    .ToList();
                if (record.Findings.Any(finding => finding.Severity == FindingSeverity.Error) ||
                    reviewFindings.Count == 0 ||
                    reviewFindings.Any(finding =>
                        !AllowedReviewFindingCodes.Contains(finding.Code)))
                {
                    return "החתך דורש הכרעה הנדסית שאינה מוגבלת לתצוגה, לכיוון נסיעה או לשמות רצועות; " +
                           "בדיקת הגאומטריה נחסמה.";
                }
            }
            else if (record.Status == DeliveryStatus.Ready)
            {
                if (record.Action is PlanAction.ReviewRequired or PlanAction.Excluded ||
                    record.Findings.Any(finding =>
                        finding.Severity >= FindingSeverity.ReviewRequired))
                    return "מצב החתך אינו עקבי ולכן תצוגת האבחון נחסמה.";
            }
            else
            {
                return "החתך אינו מוכן לבדיקת גאומטריה אבחונית.";
            }

            if (record.ManualSectionReuse != null)
            {
                return "החתך שנבחר כבר קיים בשרטוט ונשמר לשימוש חוזר. " +
                       "אין ליצור עבורו תצוגה זמנית חדשה; יש להשתמש ב'הצג חתך'.";
            }

            var endpoints = record.Cl.WcsEndpoints;
            var crossing = record.SelectedCrossing;
            if (endpoints is not { Length: 4 } || endpoints.Any(value => !double.IsFinite(value)) ||
                crossing?.Point is not { Length: >= 2 } ||
                !double.IsFinite(crossing.Point[0]) || !double.IsFinite(crossing.Point[1]) ||
                !double.IsFinite(crossing.Station) || !double.IsFinite(crossing.TangentDeg) ||
                record.Station is not { } station || !double.IsFinite(station) ||
                string.IsNullOrWhiteSpace(record.SelectedAlignment) ||
                !string.Equals(record.SelectedAlignment, crossing.AlignmentName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "לחתך שנבחר אין גאומטריית CL, תוואי וחצייה מלאים ומוכחים.";
            }

            var dx = endpoints[2] - endpoints[0];
            var dy = endpoints[3] - endpoints[1];
            if (dx * dx + dy * dy < 1e-12 ||
                record.LeftExtent is not { } left || !double.IsFinite(left) || left <= 0 ||
                record.RightExtent is not { } right || !double.IsFinite(right) || right <= 0)
            {
                return "גאומטריית ה-CL אינה מגדירה שני צדדים אמינים סביב הציר.";
            }

            var surfaces = record.PlannedSources
                .Where(source => string.Equals(
                    source.SourceType, "surface", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (surfaces.Count != 2 || surfaces.Any(source =>
                    !source.Required || !source.NativeSampleCapability ||
                    source.Status != DeliveryStatus.Ready ||
                    string.IsNullOrWhiteSpace(source.SourceName) ||
                    string.IsNullOrWhiteSpace(source.SourceHandle)) ||
                surfaces.Select(source => source.SourceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
                surfaces.Select(source => source.SourceHandle)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
            {
                return "לא הוכחו בדיוק שני משטחי Civil נפרדים ומוכנים: קרקע קיימת ותכנון.";
            }

            if (requireLayout &&
                (record.PlannedLayoutPosition is not { Length: 2 } ||
                 record.PlannedLayoutPosition.Any(value => !double.IsFinite(value))))
            {
                return "לחתך שנבחר אין מיקום פריסה זמני, בטוח ודטרמיניסטי.";
            }

            return null;
        }
    }
}
