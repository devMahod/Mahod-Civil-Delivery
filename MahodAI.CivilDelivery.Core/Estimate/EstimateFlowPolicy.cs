namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Pure presentation policy for the five estimate stages.  Keeping the decision
    /// outside WPF makes every button/state transition testable without Civil 3D.
    /// It never approves an engineering choice: source scope, earthworks and catalog
    /// mappings remain explicit human decisions.
    /// </summary>
    public static class EstimateFlowPolicy
    {
        public sealed record Snapshot(
            bool SourcesApproved,
            bool EarthworksResolved,
            bool DrawingReady,
            bool SaveCanRepairDrawing,
            bool HasFreshScan,
            int QuantityGroups,
            int PendingReviewGroups,
            bool CatalogReady,
            bool EstimateBuilt,
            bool ExportReady,
            int BlockingFindings);

        public sealed record State(int Step, string Progress, string NextAction);

        public static State Evaluate(Snapshot value)
        {
            if (!value.DrawingReady)
                return At(1, value.SaveCanRepairDrawing
                    ? "שמור את ה-DWG והמשך אוטומטית לסריקה."
                    : "תקן את גישת המקור ל-DWG לפני הסריקה.");
            if (!value.HasFreshScan)
                return At(1, "סרוק כמויות כלליות מהשרטוט וה-XREF. עבודות עפר שטרם הוחלט עליהן לא נבדקות.");
            if (value.QuantityGroups == 0)
                return At(2, "לא נמצאו קבוצות כמות; בדוק את מקורות השרטוט וכללי ההיקף.");
            if (!value.CatalogReady)
                return At(2, "המדידות מוצגות. טען מחירון לשיוך ולתמחור בלבד.");
            if (value.PendingReviewGroups > 0)
                return At(2, $"בדוק {value.PendingReviewGroups} קבוצות: אשר מיפוי או החרג עם סיבה.");
            if (value.BlockingFindings > 0)
                return At(2, $"המדידות מוצגות; בדוק {value.BlockingFindings} ממצאי מקור/מדידה לפני אומדן מלא.");
            if (!value.SourcesApproved)
                return At(3, "אשר את היקף מקורות האומדן לאחר עיון במדידות.");
            if (!value.EarthworksResolved)
                return At(3, EstimateGuidedActionPolicy.EarthworksNotAssessed);
            if (!value.EstimateBuilt)
                return At(4, "בנה את האומדן ובדוק את השורות המתומחרות.");
            if (!value.ExportReady)
                return At(4, "האומדן נבנה לבקרה אך עדיין קיימת חסימת ייצוא.");
            return At(5, "האומדן נקי ומוכן לייצוא Excel עם קובץ ביקורת.");
        }

        private static State At(int step, string next) => new(
            step,
            $"1 מדידה {(step > 1 ? "✓" : "←")}  ·  " +
            $"2 בדיקת כמויות {(step > 2 ? "✓" : step == 2 ? "←" : "")}  ·  " +
            $"3 היקף אומדן {(step > 3 ? "✓" : step == 3 ? "←" : "")}  ·  " +
            $"4 תמחור {(step > 4 ? "✓" : step == 4 ? "←" : "")}  ·  " +
            $"5 Excel {(step == 5 ? "←" : "")}",
            next);
    }
}
