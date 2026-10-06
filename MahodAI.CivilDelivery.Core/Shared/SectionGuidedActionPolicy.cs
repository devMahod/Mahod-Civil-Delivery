using System;

namespace MahodAI.CivilDelivery.Shared
{
    public enum SectionGuidedActionKind
    {
        OpenDrawing,
        ConfigureProfile,
        RecoverPreview,
        Plan,
        ChooseRecord,
        VerifySelected,
        ShowVerifiedView,
        ChooseCrossing,
        ApproveRow,
        NameSpans,
        ResolveDirection,
        ApplySelected,
        ShowExistingView,
        InspectIssues,
        LocateExistingView,
        ChooseAnnotationRecoveryRecord,
        ConfigureSectionSources,
    }

    public enum SectionGuidedStage
    {
        Setup,
        Planning,
        Review,
        Creation,
        Verification,
        Result,
        Recovery,
    }

    /// <summary>
    /// Current authoritative workflow state, projected by the host without Civil
    /// objects. CanChooseCrossing/CanApproveRow/CanNameSpans/CanResolveDirection
    /// mean an outstanding prerequisite can be resolved, not just that its editor
    /// is generally available. Cancelled dialogs and pending saves belong to the
    /// host: they must not advance this snapshot before a result is committed.
    /// </summary>
    public sealed record SectionGuidedActionSnapshot
    {
        public bool HasDrawing { get; init; }
        public bool ProfileUsable { get; init; }
        public bool NeedsSectionSetup { get; init; }
        // Only current, unresolved CL findings, with no unrelated global blocker.
        public bool CanRecoverClSource { get; init; }
        public bool SelectedHasNoAlignmentCrossing { get; init; }
        public bool HasPlan { get; init; }
        public bool StalePlan { get; init; }
        public bool PreviewCleanupBlocked { get; init; }
        public bool EvidenceBlocked { get; init; }
        public string? GlobalPlanningBlockReason { get; init; }
        public bool CanNavigateToAnnotationRecovery { get; init; }
        public bool RequiresBatchAnnotationRecovery { get; init; }
        public bool RepairingDeadAnnotations { get; init; }
        public bool SelectedRecord { get; init; }
        public bool CanChooseCrossing { get; init; }
        public bool CanApproveRow { get; init; }
        public bool CanNameSpans { get; init; }
        public bool CanResolveDirection { get; init; }
        public bool CanApplySelected { get; init; }
        // The selected record is ready, but a committed APPLY already used the
        // current plan as its baseline; the next creation needs a fresh PLAN.
        public bool ApplyAwaitsFreshPlan { get; init; }
        public bool CanRebuildSelected { get; init; }
        public bool CanVerifySelected { get; init; }
        public bool CanRevalidateCreatedView { get; init; }
        public bool HasCreatedView { get; init; }
        public bool ManagedViewLookupAvailable { get; init; }
        public bool SelectedVerified { get; init; }
        public int PlanRecordCount { get; init; }
    }

    public sealed record SectionGuidedActionDecision(
        SectionGuidedActionKind Action,
        string Title,
        string Detail,
        string ButtonText,
        SectionGuidedStage Stage);

    /// <summary>
    /// Chooses one useful next action without executing a command, creating an
    /// engineering decision, or promoting a discovery record into a created view.
    /// Existing service gates remain authoritative when the action is invoked.
    /// </summary>
    public static class SectionGuidedActionPolicy
    {
        public const string PlanningExplanation =
            "התכנון מאתר קווי CL ובודק את הנתונים הדרושים; הוא אינו יוצר חתכים.";

        public const string PrecreationLocationExplanation =
            "לפני יצירת חתך, הצגת מיקום CL מפנה לקו בתוכנית.";

        public static SectionGuidedActionDecision Evaluate(SectionGuidedActionSnapshot value)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!value.HasDrawing)
                return At(SectionGuidedActionKind.OpenDrawing, SectionGuidedStage.Setup,
                    "פתח שרטוט לעבודה", "יש לפתוח את שרטוט הפרויקט כדי לבחור מקורות ולתכנן חתכים.",
                    "פתח שרטוט");

            if (!value.ProfileUsable)
                return At(SectionGuidedActionKind.ConfigureProfile, SectionGuidedStage.Setup,
                    "בחר פרופיל פרויקט תקין",
                    "יש לבחור קובץ פרופיל פרויקט קיים ותקין. הבחירה נבדקת לפני השימוש ואינה מעתיקה אישורים בין פרויקטים; לאחר מכן ניתן לבחור את מקורות החתכים.",
                    "בחר פרופיל פרויקט…");

            // A cleanup recovery is offered only for an actual retained-preview
            // failure, never merely because APPLY or another operation is blocked.
            if (value.PreviewCleanupBlocked)
                return At(SectionGuidedActionKind.RecoverPreview, SectionGuidedStage.Recovery,
                    "יש להשלים את ניקוי התצוגה המקדימה",
                    "ניקוי התצוגה המקדימה לא הושלם. יש לחזור לשרטוט שבו נוצרה ולנסות לנקות אותה לפני המשך העבודה.",
                    "נקה תצוגה מקדימה");

            if (value.NeedsSectionSetup && !value.HasPlan)
                return At(SectionGuidedActionKind.ConfigureSectionSources, SectionGuidedStage.Setup,
                    "בחר את מקורות החתכים בשרטוט הזה",
                    "יש לפתוח את הגדרת המקורות ולבחור שכבת קווי CL, תוואי ומקור דגימה. אפשר לבחור קובץ CL נפרד, או להשתמש בקווים במודל; אם אין קווי CL, יש ליצור LINE במיקומים וברוחבים שאישר המהנדס/ת, לשמור ולסרוק שוב. מדידות ואומדן זמינים גם ללא CL.",
                    "הגדר מקורות חתכים…");

            // This is an explicit new validation, never permission to reuse old
            // green evidence. The host refreshes PLAN and re-proves source/output
            // identity on click; stale geometry cannot authorize an engineering edit.
            if (value.HasPlan && value.SelectedRecord && value.CanRevalidateCreatedView &&
                value.StalePlan && !value.EvidenceBlocked)
                return At(SectionGuidedActionKind.VerifySelected, SectionGuidedStage.Verification,
                    "בדוק מחדש את החתך הקיים",
                    "הנתונים השתנו מאז הבדיקה הקודמת. הפעולה תרענן את התכנון ותבדוק מחדש מקורות ותוצר; היא אינה משנה את השרטוט ואינה משתמשת באישור הישן.",
                    "הצג ואמת חתך");

            if (!value.HasPlan || value.StalePlan)
            {
                var detail = value.StalePlan
                    ? "הנתונים השתנו מאז התכנון האחרון. יש להריץ תכנון מחדש לקבלת רשומות עדכניות. "
                    : "יש להריץ תכנון כדי לאתר את רשומות החתכים ולזהות את ההחלטות הדרושות. ";
                detail += PlanningExplanation;
                if (value.EvidenceBlocked)
                    detail += " ראיות התהליך אינן שלמות; התכנון מחדש נועד לשחזר אותן. יצירה ואימות יישארו חסומים עד לפרסום ראיות תקינות.";
                return At(SectionGuidedActionKind.Plan, SectionGuidedStage.Planning,
                    value.StalePlan ? "נדרש תכנון עדכני" : "אתר את החתכים לתכנון",
                    detail, value.StalePlan ? "תכנן מחדש" : "תכנן חתכים");
            }

            if (value.CanRecoverClSource && !value.EvidenceBlocked)
                return At(SectionGuidedActionKind.ConfigureSectionSources, SectionGuidedStage.Recovery,
                    "בחר או תקן את מקור קווי ה-CL",
                    "במסך הגדרת המקורות אפשר לבחור שכבת CL במודל או לבחור קובץ CL נפרד וזמין. אם אין קווי CL, יש ליצור LINE במיקומים וברוחבים שאישר המהנדס/ת, לשמור ולסרוק שוב. לאחר אישור המקורות יש להריץ תכנון מחדש; בחירת מקור אינה יוצרת חתכים. מדידות ואומדן אינם דורשים CL.",
                    "בחר מקורות CL…");

            if (!string.IsNullOrWhiteSpace(value.GlobalPlanningBlockReason))
                return At(SectionGuidedActionKind.InspectIssues, SectionGuidedStage.Recovery,
                    "התכנון חסום לכל החתכים",
                    value.GlobalPlanningBlockReason +
                    // 1.4.1: a multi-line reason (XREF name list) keeps the next sentence on its own line.
                    (value.GlobalPlanningBlockReason.Contains('\n') ? "\n" : " ") +
                    "אין כרגע אפשרות ליצור או לאמת חתכים. פרטי החסימה מוצגים בתוך הכלי; בחירת שורה אחרת או תכנון חוזר ללא טיפול בסיבה לא יפתרו אותה.",
                    "הצג את סיבת החסימה");

            if (value.PlanRecordCount <= 0)
                return At(value.EvidenceBlocked ? SectionGuidedActionKind.InspectIssues : SectionGuidedActionKind.ConfigureSectionSources, SectionGuidedStage.Planning,
                    "לא נמצאו רשומות חתכים לבחירה",
                    "יש לבדוק את מקור קווי CL, את הגדרות המקורות ואת החיתוכים עם הצירים, ואז להריץ תכנון מחדש. " +
                    PlanningExplanation + (value.EvidenceBlocked
                        ? " יש להשלים גם את פרסום ראיות התכנון לפני יצירה או אימות."
                        : ""),
                    value.EvidenceBlocked ? "בדוק את פרטי התקלה" : "הגדר מקורות וחיתוכים…");

            if (!value.SelectedRecord)
                return At(SectionGuidedActionKind.ChooseRecord, SectionGuidedStage.Review,
                    "בחר רשומה להמשך העבודה",
                    $"בתכנון נמצאו {value.PlanRecordCount} רשומות. יש לבחור שורה כדי לבדוק את ההחלטות והפעולה הדרושות עבורה. " +
                    PrecreationLocationExplanation,
                    "בחר רשומה");

            // A failed evidence publication cannot be bypassed by stale positive
            // capability flags or by a view that still exists in the drawing.
            if (value.EvidenceBlocked)
                return At(SectionGuidedActionKind.InspectIssues, SectionGuidedStage.Recovery,
                    "יש לשחזר את ראיות התהליך",
                    "ראיות התכנון, ההחלה או האימות אינן שלמות. יש לבדוק את פרטי התקלה ולתקן את שמירת הראיות. " +
                    "לאחר התיקון, תכנון מחדש יכול לשחזר את הנתונים; הוא אינו יוצר חתכים ואינו מהווה אימות. יצירה ואימות יישארו חסומים עד לפרסום ראיות תקינות.",
                    "בדוק את פרטי התקלה");

            if (value.CanNavigateToAnnotationRecovery)
                return At(SectionGuidedActionKind.ChooseAnnotationRecoveryRecord, SectionGuidedStage.Recovery,
                    "תחילה יש לשחזר חתך קודם",
                    "רישום הערות של חתך אחר דורש עדכון. יש לעבור אליו ולעדכן אותו תחילה; יצירת החתך הנוכחי לא תתקן אוטומטית חתכים אחרים.",
                    "עבור לחתך לשחזור");

            // The host keeps its explicit reverify command available after success.
            // A current verified view must advance the guide to the result even
            // while that command remains enabled.
            if (value.HasCreatedView && value.SelectedVerified)
                return At(SectionGuidedActionKind.ShowVerifiedView, SectionGuidedStage.Result,
                    "החתך הנבחר אומת",
                    "האימות הנוכחי של החתך הנבחר הושלם. אפשר להציג את תצוגת החתך ולבדוק את התוצאה על המסך.",
                    "הצג חתך מאומת");

            if (value.CanRebuildSelected)
                return At(SectionGuidedActionKind.ApplySelected, SectionGuidedStage.Recovery,
                    "האימות נכשל — ניתן לבנות מחדש את החתך הנבחר",
                    "לאחר אישור מפורש יוחלף רק התוצר שבבעלות הכלי עבור חתך זה לפי התכנון והמקורות הנוכחיים. החלטות התכנון ושאר החתכים לא ישתנו. לאחר הבנייה נדרש אימות חדש.",
                    "בנה מחדש חתך נבחר…");

            if (value.CanVerifySelected)
                return At(SectionGuidedActionKind.VerifySelected, SectionGuidedStage.Verification,
                    "בדוק את החתך הנבחר",
                    "יש להריץ אימות לחתך הנבחר ולבדוק את תוצאת הבדיקה לפני הסתמכות על התוצר.",
                    "הצג ואמת חתך");

            if (value.CanChooseCrossing)
                return At(SectionGuidedActionKind.ChooseCrossing, SectionGuidedStage.Review,
                    "בחר את חיתוך הציר המתאים",
                    "לרשומה יש כמה חיתוכים אפשריים. יש לבחור במפורש את החיתוך שאליו שייך החתך.",
                    "בחר חיתוך");

            if (value.SelectedHasNoAlignmentCrossing)
                return At(SectionGuidedActionKind.ConfigureSectionSources, SectionGuidedStage.Review,
                    "קו ה-CL אינו חוצה תוואי זמין",
                    "יש לבדוק בהגדרת המקורות את שכבת ה-CL ואת התוואים שנבחרו. אם חסר תוואי Civil, יש לפתוח את מודל התכנון וליצור הפניית נתונים תקינה, או Alignment מגאומטריה שאישר המהנדס/ת. קו ב-XREF לבדו אינו תוואי Civil. לאחר התיקון יש לשמור ולהריץ תכנון מחדש.",
                    "בדוק מקורות ותוואים…");

            if (value.CanApproveRow)
                return At(SectionGuidedActionKind.ApproveRow, SectionGuidedStage.Review,
                    "אשר מקור לגבולות הדרך",
                    "יש לאשר את מקור גבולות הדרך (ROW) בפרופיל הפרויקט. אותו אישור יכול לפתור את הדרישה בכמה חתכים לאחר תכנון מחדש; אין צורך באישור נפרד לכל שורה המשתמשת באותו מקור.",
                    "אשר מקור ROW");

            if (value.CanNameSpans)
                return At(SectionGuidedActionKind.NameSpans, SectionGuidedStage.Review,
                    "השלם את שמות הרצועות",
                    "יש לבדוק את הרצועות מול התוכנית ולשמור שמות מפורשים לרצועות שטרם הוגדרו.",
                    "הגדר שמות רצועות");

            if (value.CanResolveDirection)
                return At(SectionGuidedActionKind.ResolveDirection, SectionGuidedStage.Review,
                    "קבע את כיווני הנסיעה בנתיבים",
                    "יש לאשר כיוון נסיעה לכל נתיב לפי התוכנית. כיוון המבט של החתך וכיוון הציר לבדם אינם קובעים את כיוון הנסיעה בכל הנתיבים.",
                    "הכרע כיווני נסיעה");

            if (value.RequiresBatchAnnotationRecovery)
                return At(SectionGuidedActionKind.InspectIssues, SectionGuidedStage.Recovery,
                    "נדרש שחזור של כמה חתכים באצווה",
                    "כמה חתכים זקוקים לתיקון רישום. יש להשלים את נתוני האצווה ואז להחיל אותה יחד; החלת חתך יחיד אינה מורשית לתקן רישום של חתכים אחרים.",
                    "הצג את החתכים לשחזור");

            if (value.CanApplySelected)
                return At(SectionGuidedActionKind.ApplySelected, SectionGuidedStage.Creation,
                    value.RepairingDeadAnnotations ? "עדכן ושחזר את החתך הנבחר" : "החל את החתך הנבחר",
                    value.RepairingDeadAnnotations
                        ? "בעדכון יוסרו רק הפניות להערות שהוכח שאינן קיימות, והחתך ייבנה מחדש באותה פעולה אטומית. אם הבנייה תיכשל גם תיקון הרישום יבוטל. לאחר העדכון יש לאמת את התוצר."
                        : "הפעולה יוצרת או מעדכנת את החתך הנבחר לפי התכנון המאושר. לאחר סיום הפעולה יש לאמת את התוצאה.",
                    "החל חתך נבחר");

            if (value.ApplyAwaitsFreshPlan)
                return At(SectionGuidedActionKind.Plan, SectionGuidedStage.Planning,
                    "נדרש תכנון עדכני לפני יצירת חתך נוסף",
                    "חתך אחר נוצר בשרטוט מאז התכנון האחרון. כדי ליצור את החתך הנבחר יש להריץ תכנון מחדש מול השרטוט המעודכן. " +
                    "התכנון אינו משנה את השרטוט ואינו מוחק את החתך שכבר נוצר; את החתך שנוצר יש לאמת שוב לאחר התכנון.",
                    "תכנן מחדש");

            if (value.HasCreatedView)
                return At(SectionGuidedActionKind.ShowExistingView, SectionGuidedStage.Review,
                    "אפשר להציג את החתך הקיים",
                    "קיימת תצוגת חתך לרשומה הנבחרת. ניתן להציג אותה, אך האימות הנוכחי שלה טרם הושלם.",
                    "הצג חתך קיים");

            if (value.ManagedViewLookupAvailable)
                return At(SectionGuidedActionKind.LocateExistingView, SectionGuidedStage.Review,
                    "אתר את תצוגת החתך הקיימת",
                    "התכנון מסמן חתך מנוהל ללא שינוי. האיתור יבדוק את זהות החתך בשרטוט לפני ההצגה; הוא אינו אימות הנדסי.",
                    "אתר חתך קיים");

            return At(SectionGuidedActionKind.InspectIssues, SectionGuidedStage.Review,
                "בדוק את החסימות ברשומה הנבחרת",
                "יש לפתוח את פרטי השורה ולבדוק אילו נתונים או החלטות חסרים לפני יצירת החתך. " +
                PrecreationLocationExplanation,
                "בדוק פרטי רשומה");
        }

        private static SectionGuidedActionDecision At(
            SectionGuidedActionKind action, SectionGuidedStage stage,
            string title, string detail, string buttonText) =>
            new(action, title, detail, buttonText, stage);
    }
}
