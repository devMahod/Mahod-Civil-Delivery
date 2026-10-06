using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Chooses one next user action. The caller executes its existing confirmed
    /// handler once; returning from a dialog never approves or runs a later step.
    /// Quantity discovery does not depend on final scope approval, earthworks,
    /// a price book or known layer meaning. Final scope remains a later decision.
    /// </summary>
    public static class EstimateGuidedActionPolicy
    {
        public enum Action
        {
            Unavailable, WaitForSave, ApproveSources, DecideEarthworks, Scan,
            ReviewFindings, ReviewSources, LoadCatalog, ReviewQuantities, Build, Export,
            ReviewProvenMappings, ExportPricedDraft, ReviewDrawingNoise,
            /// <summary>30.09: the project has BoQ rules and a fresh scan — the whole bill in one click.</summary>
            ExportBoqRules,
        }

        public const string BoqRulesCaption = "כתב כמויות לפי כללים…";
        // 01/10 (Codex): not "the full bill" — a file can have known missing objects; they are listed separately.
        public const string BoqRulesDetail =
            "כתב כמויות לפי כללי הפרויקט, מהסריקות האחרונות של קובצי הפרויקט — סעיפים, כמויות, מחירים וסה\"כ למה שנמדד. " +
            "מה שלא נמדד מופיע בנפרד (גיליון 'חסרים'), ומה שדורש החלטה מסומן בתוך הקובץ (\"לאישור\" / \"לבדיקה\"); אין צורך לעבור קבוצה-קבוצה. " +
            "שיוך שכבות ידני (למטה) — רק למי שבונה אומדן בשיטה הישנה.";

        public sealed record Snapshot(
            bool Available,
            bool SavePending,
            bool SourcesApproved,
            bool EarthworksResolved,
            bool SaveMayBeRequired,
            bool HasFreshScan,
            int QuantityGroups,
            int PendingReviewGroups,
            bool CatalogReady,
            bool EstimateBuilt,
            bool ExportReady,
            int BlockingFindings,
            int ProvenMappingGroups = 0,
            bool HasApprovedMappings = false,
            bool PartialPricedDraftReady = false,
            bool HasExistingScan = false,
            string? FreshnessReason = null,
            int NoiseReviewGroups = 0);

        public sealed record State(Action Next, string Caption, string Detail, bool Enabled = true);

        /// <summary>
        /// Missing catalog meaning and an exact known area/perimeter choice are
        /// resolved in quantity review. Other measurement/source findings still
        /// take priority; an unrelated mixed-dimension error is never suppressed.
        /// </summary>
        public static int SourceReviewFindingCount(
            IEnumerable<DeliveryFinding> findings, IEnumerable<string> exactAlternativeEvidence)
        {
            var alternatives = exactAlternativeEvidence.ToHashSet(StringComparer.Ordinal);
            return findings.Count(finding => EstimatePreflightPolicy.IsBlocking(finding) &&
                finding.Code != EstimateFindingCodes.Unmapped &&
                finding.Code != EstimateFindingCodes.SourceScopePolicyUnapproved &&
                finding.Code != EstimateFindingCodes.XrefPolicyUnapproved &&
                finding.Code != EstimatePreflightPolicy.EarthworksNotAssessedCode &&
                !(finding.Code == EstimateFindingCodes.MixedDimensionLayer &&
                  alternatives.Contains(finding.Message)));
        }

        public static State Evaluate(Snapshot value)
        {
            if (!value.Available)
                return new(Action.Unavailable, "בחר פרויקט ושרטוט", "נדרשים פרופיל תקין ושרטוט פעיל.", false);
            if (value.SavePending)
                return new(Action.WaitForSave, "ממתין לשמירה…", "לאחר השמירה תיבדק גרסת השרטוט והסריקה תימשך.", false);
            if (value.SaveMayBeRequired || !value.HasFreshScan)
            {
                if (value.HasExistingScan)
                {
                    var reason = string.IsNullOrWhiteSpace(value.FreshnessReason)
                        ? (value.SaveMayBeRequired
                            ? "נדרשת שמירת השרטוט לפני חידוש המדידות."
                            : "לא ניתן לאמת שהמדידות תואמות למקורות ולפרופיל הנוכחיים.")
                        : value.FreshnessReason.Trim();
                    return new(Action.Scan,
                        value.SaveMayBeRequired ? "שמור וחדש את הסריקה…" : "חדש את הסריקה",
                        "הסריקה הקודמת דורשת חידוש — " + reason +
                        " הנתונים הקודמים נשארים לעיון בלבד; לאחר הסריקה החדשה ניתן להמשיך בשיוך ובתמחור.");
                }
                return new(Action.Scan, value.SaveMayBeRequired ? "שמור וסרוק כמויות…" : "סרוק כמויות",
                    "מדוד תחילה אורכים, שטחים וספירות מהשרטוט והפניותיו. החלטת עבודות עפר ומחירון אינם נדרשים למדידה זו.");
            }
            if (value.QuantityGroups == 0)
                return value.BlockingFindings > 0 ? Findings(value.BlockingFindings) :
                    new(Action.ReviewSources, "בדוק את מקורות הסריקה…", "לא נמדדו קבוצות כמות; בדוק אילו מקורות השתתפו בסריקה.");
            if (!value.CatalogReady)
                return new(Action.LoadCatalog, "טען מחירון לשיוך…", "המדידות מוצגות בטבלה. מחירון נדרש רק לשיוך ולתמחור.");
            if (value.SourcesApproved && value.EarthworksResolved && value.EstimateBuilt && value.ExportReady)
                return new(Action.Export, "ייצא אומדן ל-Excel…", "ייצוא האומדן המאומת וקובצי הביקורת.");
            if (value.SourcesApproved && value.EstimateBuilt && value.PartialPricedDraftReady)
                return new(Action.ExportPricedDraft, "ייצא טיוטה מתומחרת…",
                    "השורות התקינות חושבו. ניתן לייצא טיוטה חלקית עם כל החסרים, או להמשיך לערוך שיוכים בטבלה. אין כאן אישור אומדן מלא.");
            if (value.SourcesApproved && value.HasApprovedMappings && !value.EstimateBuilt)
                return new(Action.Build, "חשב תמחור לשורות שאושרו",
                    "חשב את השיוכים המאושרים ובדוק את סכומם. שורות חסרות ובעיות מקור יישארו גלויות; אין צורך להשלים את כולן כדי לקבל טיוטה חלקית.");
            if (value.NoiseReviewGroups > 0)
                return value.SourcesApproved
                    ? new(Action.ReviewDrawingNoise, $"בדוק סימוני עזר יחד ({value.NoiseReviewGroups})…",
                        "הזיהוי מצא קבוצות תחנות ועזר שאינן בהכרח עבודות לתמחור. בדוק את הרשימה ואשר יחד רק אם אינן בהיקף. תשתיות קיימות אינן מוחרגות אוטומטית.")
                    : new(Action.ApproveSources, "בדוק ואשר מקורות…",
                        "אישור היקף המקורות יאפשר בדיקה מרוכזת של סימוני תחנות ועזר. אין צורך לשייך לכל סימן סעיף מחירון; דבר לא יוחרג בלי אישור.");
            if (value.PendingReviewGroups > 0 && value.SourcesApproved && value.ProvenMappingGroups > 0)
                return new(Action.ReviewProvenMappings, $"בדוק הצעות מבוססות ({value.ProvenMappingGroups})…",
                    "פתח טבלת הצעות שעברו בדיקת מקור, יחידה ומחיר. השמירה תתבצע רק לאחר אישור מפורש שלך; יתר הכמויות יישארו לבדיקה.");
            if (value.PendingReviewGroups > 0)
                return new(Action.ReviewQuantities, "בדוק ושייך קבוצות יחד…",
                    "ראה הצעות, חפש לפי תחום או סעיף וסמן קבוצות מוצגות המתאימות לאותו סעיף. אישור אחד שומר את הבחירות שנבדקו לשימוש חוזר בסריקות הבאות.");
            if (value.BlockingFindings > 0)
                return Findings(value.BlockingFindings);
            if (!value.SourcesApproved)
                return new(Action.ApproveSources, "אשר מקורות לאומדן…", "המדידות כבר מוצגות. אשר את היקף המקורות לפני בניית אומדן; האישור אינו מאשר מחירים.");
            if (!value.EarthworksResolved)
                return new(Action.DecideEarthworks, "הגדר היקף עבודות עפר…", EarthworksNotAssessed);
            if (!value.EstimateBuilt)
                return new(Action.Build, "בנה אומדן", "חשב לפי השיוכים והמחירים המאושרים ובדוק את התוצאה.");
            if (value.ExportReady)
                return new(Action.Export, "ייצא אומדן ל-Excel…", "ייצוא האומדן המאומת וקובצי הביקורת.");
            return new(Action.ReviewFindings, "בדוק חסימות באומדן…", "האומדן נבנה לעיון, אך אינו מוכן לייצוא.");
        }

        private static State Findings(int count) => new(Action.ReviewFindings,
            $"בדוק ממצאי מדידה ({count})…",
            "הכמויות שנמדדו נשמרות לעיון; יש לפתור את ממצאי המקור והמדידה לפני תמחור מלא.");

        public const string MeasurementContext =
            "מדידות לעיון — לא כתב כמויות מאושר. כמות ללא שיוך נשארת גלויה ומחירה ריק. " +
            "שטח והיקף של אותו פוליליין הם חלופות, לא כמויות לחיבור.";

        public const string EarthworksNotAssessed =
            "עבודות עפר טרם נבדקו — לא אפס ולא הוחרגו. החלטת היקף נדרשת לאומדן סופי; מדידה וטיוטה חלקית אינן מכריעות אותה.";
    }
}
