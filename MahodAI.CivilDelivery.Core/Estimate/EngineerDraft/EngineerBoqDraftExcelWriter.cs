using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.EngineerDraft;

/// <summary>
/// Writes the engineer draft as an editable Excel workbook. Quantities are live
/// formulas over the base measurements and the parameters sheet, so the engineer
/// changes a thickness, an include flag or a factor and every line recalculates.
/// Nothing in the workbook is an approval; the first sheet says so and lists what
/// the subtotal leaves out.
/// </summary>
public static class EngineerBoqDraftExcelWriter
{
    public const string SummarySheet = "סיכום";
    public const string ChecksSheet = "בדיקות והערות";
    public const string BoqSheet = "כתב כמויות";
    public const string ParametersSheet = "פרמטרים";
    public const string BaseSheet = "מדידות בסיס";
    public const string UnmappedSheet = "לא שויכו";
    public const string AlternativesSheet = "מדידות חלופיות";
    public const string ExistingSheet = "מצב קיים ותשתיות";
    public const string AidsSheet = "סימוני עזר";
    public const string RecognitionSheet = "הצעות זיהוי";

    public const string DraftNotice =
        "טיוטת כתב כמויות להנדסה — הצעה אוטומטית מתוך המדידות בשרטוט. זו אינה אומדן מאושר: כל פריט, מבנה והנחה דורשים בדיקה ואישור הנדסי.";

    public const string SubtotalLabel = "סכום ביניים של שורות מוצעות עם מחיר — לא אומדן";
    public const string CompletedTotalLabel = "סכום ביניים כולל היקפים והגדרות שהושלמו — לא אומדן";

    private const int StyleTitle = 1, StyleHeader = 2, StyleText = 3, StyleNumber = 4, StyleInput = 5,
        StyleChapter = 6, StyleChapterTotal = 7, StyleWarning = 8, StyleAssumption = 9, StyleDecision = 10,
        StyleNotice = 11, StyleSubtotal = 12, StyleFlagInput = 13, StyleSubtle = 14, StyleCount = 15,
        StyleSubtotalBoq = 16, StyleCountHeader = 17, StyleLtr = 18, StyleLtrChapter = 19, StyleLtrSubtle = 20;

    /// <param name="LineCount">Element × catalog-item contributions in the draft.</param>
    /// <param name="PricedLineCount">BoQ rows that carry a catalog price.</param>
    /// <param name="BoqRowCount">Proposed BoQ rows (without scope-input and definition rows).</param>
    public sealed record WriteResult(string XlsxPath, int LineCount, int ElementCount, int PricedLineCount,
        decimal PricedTotalAtDefaults, int AccountedRecords, int BoqRowCount = 0);

    /// <summary>One proposed BoQ row: the contributions that share a catalog item (or a separate-row rule).</summary>
    internal sealed record BoqRow(string Chapter, string SubChapter, string RowKey, IReadOnlyList<DraftLine> Lines)
    {
        public DraftLine Head => Lines[0];
        public decimal? Price => Head.Price;
        public DraftConfidence Confidence => Lines.Max(l => l.Element.Rule.Confidence);
    }

    public static WriteResult Write(EngineerBoqDraft draft, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("נדרש נתיב לקובץ הטיוטה.", nameof(outputPath));
        if (draft.AccountedRecords != draft.RecordCount)
            throw new InvalidOperationException(
                $"שגיאה פנימית בטיוטה: {draft.AccountedRecords} רשומות שובצו מתוך {draft.RecordCount} שנסרקו. הקובץ לא נוצר.");
        var workbook = Build(draft, out var pricedTotal, out var pricedRows, out var boqRows);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        MiniXlsx.Write(workbook, outputPath);
        return new WriteResult(outputPath, draft.Lines.Count, draft.Elements.Count, pricedRows, pricedTotal, draft.AccountedRecords, boqRows);
    }

    internal static MiniXlsx.Workbook Build(EngineerBoqDraft draft, out decimal pricedTotalAtDefaults, out int pricedRows, out int boqRowCount)
    {
        var wb = new MiniXlsx.Workbook { SheetName = SummarySheet, RightToLeft = true, ShowGridLines = false, PrintLandscapeFitToWidth = true };
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 14) { RightToLeft = true });                                                     // 1 title
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFD9E1F2", ThinTopBottomBorder: true, WrapText: true) { RightToLeft = true });  // 2 header
        wb.Styles.Add(new MiniXlsx.Style(WrapText: true, AlignTop: true) { RightToLeft = true });                                              // 3 text
        wb.Styles.Add(new MiniXlsx.Style(NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));                                               // 4 number
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true)); // 5 input
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true }); // 6 chapter
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true)); // 7 chapter total
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFCE4D6", WrapText: true, AlignTop: true) { RightToLeft = true });                         // 8 warning
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", WrapText: true, AlignTop: true) { RightToLeft = true });                         // 9 assumption
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFF8CBAD", WrapText: true, AlignTop: true) { RightToLeft = true });                         // 10 decision
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, WrapText: true, AlignTop: true) { RightToLeft = true });                                  // 11 notice
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 12, FillRgb: "FFEDEDED", ThinTopBottomBorder: true, NumFmtId: 3, AlignTop: true)); // 12 subtotal
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", ThinTopBottomBorder: true, NumFmtId: 1, AlignTop: true));                       // 13 flag input
        wb.Styles.Add(new MiniXlsx.Style(Italic: true, FontSize: 9, WrapText: true, AlignTop: true) { RightToLeft = true });                   // 14 subtle
        wb.Styles.Add(new MiniXlsx.Style(NumFmtId: 3, AlignTop: true));                                                                         // 15 count / #,##0
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFEDEDED", ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true)); // 16 BoQ subtotal
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true, NumFmtId: 3, AlignTop: true));            // 17 chapter count
        wb.Styles.Add(new MiniXlsx.Style(WrapText: true, AlignTop: true) { AlignRight = true });                                             // 18 Latin text
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { AlignRight = true }); // 19 Latin chapter
        wb.Styles.Add(new MiniXlsx.Style(Italic: true, FontSize: 9, WrapText: true, AlignTop: true) { AlignRight = true });                  // 20 Latin subtle

        var rows = BoqRows(draft);
        var parameters = BuildParameters(draft, rows, out var parameterRows);
        var baseSheet = BuildBase(draft, out var elementTotalRows);
        var boq = BuildBoq(draft, rows, parameterRows, elementTotalRows, out var boqLayout, out pricedTotalAtDefaults, out pricedRows);
        boqRowCount = rows.Count;
        var unmapped = BuildGroupsSheet(UnmappedSheet,
            $"שכבות תכנון שנמדדו ואין להן כלל ב{draft.Library.Texts.LibraryName}, וכן תשתיות בתכנון (ניקוז, מים, ביוב וכו'). לא נכללו בסכום. אם זו עבודה בפרויקט — להוסיף שורה ידנית בכתב הכמויות.",
            draft.UnmappedDesign);
        var alternatives = BuildGroupsSheet(AlternativesSheet,
            "מדידות נוספות של שכבות שכבר נמדדו ברכיב בכתב הכמויות (למשל גבול הצללה, היקף או ספירה). לא להוסיף לכתב הכמויות — הכמות תיספר פעמיים. מוצגות לבדיקה בלבד.",
            draft.NotUsedAlternatives);
        var existing = BuildGroupsSheet(ExistingSheet,
            "מצב קיים (תכנית מדידה ושכבות קיים) ותשתיות קיימות. לא נכללו בכתב הכמויות לעבודות חדשות. שימושי להיקפי פירוק, העתקה והגנה — לפי החלטה הנדסית.",
            draft.Existing.Concat(draft.Utilities).OrderBy(g => g.Role).ThenByDescending(g => g.Quantity).ToList());
        var aids = BuildGroupsSheet(AidsSheet,
            draft.Library.Texts.AidsIntro,
            draft.CorridorVolumes.Concat(draft.ExcludedByDecision).Concat(draft.DraftingAids).ToList());
        var checks = BuildChecks(draft);
        BuildSummary(wb, draft, rows, boqLayout, pricedTotalAtDefaults, pricedRows);

        wb.AdditionalSheets.Add(checks);
        wb.AdditionalSheets.Add(boq);
        wb.AdditionalSheets.Add(parameters);
        wb.AdditionalSheets.Add(baseSheet);
        wb.AdditionalSheets.Add(unmapped);
        if (draft.RecognitionProposals.Count > 0) wb.AdditionalSheets.Add(BuildRecognition(draft));
        wb.AdditionalSheets.Add(alternatives);
        wb.AdditionalSheets.Add(existing);
        wb.AdditionalSheets.Add(aids);
        IsolateLatinInHebrewCells(wb);
        return wb;
    }

    /// <summary>Row positions on the BoQ sheet that the summary refers to.</summary>
    internal sealed record BoqLayout(int FirstItemRow, int LastItemRow, int SubtotalRow, int ScopeTotalRow, int ScopeCount, int DefinitionCount,
        int FirstDefinitionRow, int LastDefinitionRow);

    // ------------------------------------------------------------------ summary

    private static void BuildSummary(MiniXlsx.Workbook sheet, EngineerBoqDraft draft, IReadOnlyList<BoqRow> rows,
        BoqLayout layout, decimal pricedTotal, int pricedRows)
    {
        sheet.ColumnWidths.AddRange(new[] { (1, 44.0), (2, 100.0) });
        var r = 1;
        Row(sheet, r++).Text("A", $"טיוטת כתב כמויות להנדסה — {draft.Context.ProjectName}", StyleTitle);
        Row(sheet, r).Text("A", DraftNotice, StyleNotice);
        sheet.SingleRowMerges.Add($"A{r}:B{r}");
        sheet.Rows[^1].HeightPoints = 36;
        r += 2;

        Row(sheet, r++).Text("A", "איך עובדים עם הקובץ — ארבעה צעדים", StyleTitle);
        var texts = draft.Library.Texts;
        var scopeStep = draft.Library.ScopeItems.Count > 0
            ? $"להזין כמות בשורות ההיקף ({texts.ScopeExamples}), ופריט ומחיר בשורות 'הגדרה'"
            : "להזין פריט ומחיר בשורות 'הגדרה'";
        var steps = new[]
        {
            ("צעד 1 — בדיקות", $"לעבור על גיליון '{ChecksSheet}'. קודם השורות שמסומנות 'כן' בעמודה 'משפיע על הסכום': עצמים שלא נמדדו, חשד לכפילות, פריטים ללא מחיר."),
            ("צעד 2 — פרמטרים", $"לאשר או לשנות את הערכים הצהובים בגיליון '{ParametersSheet}' ({texts.ParameterExamples}). כל השורות התלויות מתעדכנות. ערך ריק או מחוץ לטווח אינו אפס: הוא מסומן #N/A עד לתיקון."),
            ("צעד 3 — כתב הכמויות", $"לעבור על גיליון '{BoqSheet}' שורה אחר שורה: פריט, כמות ומחיר. בסוף הגיליון: {scopeStep}. שורה שאין לה מחיר מקבלת מחיר שמזינים בעמודה 'מחיר יח''. הכול נכנס לשורה 'סכום ביניים כולל היקפים והגדרות שהושלמו'."),
            ("צעד 4 — מקורות", $"בגיליון '{BaseSheet}' לסמן 1/0 בעמודה 'לכלול' לפי מה שנבדק בשרטוט, ולעבור על '{UnmappedSheet}'. הגיליונות '{AlternativesSheet}', '{ExistingSheet}' ו-'{AidsSheet}' — לבדיקה בלבד."),
        };
        foreach (var (label, text) in steps) Row(sheet, r++).Text("A", label, StyleHeader).Text("B", text, StyleText);
        r++;

        void Pair(string label, string value, int style = StyleText) => Row(sheet, r++).Text("A", label, StyleHeader).Text("B", value, style);
        Row(sheet, r++).Text("A", "פרטי הטיוטה", StyleTitle);
        Pair("פרויקט", $"{draft.Context.ProjectName} (פרופיל {Bidi.Ltr(draft.Context.ProfileId)})");
        Pair("שרטוט", Bidi.Ltr(draft.Context.SourceDrawing));
        Pair("סריקה", Bidi.Ltr(draft.Context.ScanRunId));
        Pair("מחירון", draft.Context.CatalogLabel + (string.IsNullOrWhiteSpace(draft.Context.CatalogIdentity) ? string.Empty : $" · {Bidi.Ltr(draft.Context.CatalogIdentity)}"));
        Pair("ספריית שיוך", $"{draft.Library.Title} ({Bidi.Ltr(draft.Library.Id)})");
        if (!string.IsNullOrWhiteSpace(draft.Context.PluginVersion)) Pair("גרסת הכלי", Bidi.Ltr(draft.Context.PluginVersion));
        Pair("רשומות שנסרקו", $"{draft.RecordCount:N0}. כל רשומה מופיעה בדיוק בגיליון אחד (בדיקה פנימית: {draft.AccountedRecords:N0}).");
        r++;

        Row(sheet, r++).Text("A", "סכום ביניים", StyleTitle);
        Row(sheet, r++).Text("A", SubtotalLabel, StyleHeader).Formula("B", $"'{BoqSheet}'!G{layout.SubtotalRow}", StyleSubtotal);
        var lastParameterRow = 3 + draft.Library.Parameters.Count;
        Row(sheet, r++).Text("A", "פרמטרים לא תקינים", StyleWarning)
            .Formula("B", $"IF(SUMPRODUCT(--NOT(ISNUMBER('{ParametersSheet}'!G4:G{lastParameterRow})))=0,\"אין — כל הפרמטרים בטווח\"," +
                          $"SUMPRODUCT(--NOT(ISNUMBER('{ParametersSheet}'!G4:G{lastParameterRow})))&\" פרמטרים ריקים, טקסט או מחוץ לטווח. הסכום אינו תקף עד לתיקון בגיליון '{ParametersSheet}'.\")", StyleWarning);
        string Range(string column) => $"'{BoqSheet}'!{column}{layout.FirstItemRow}:{column}{Math.Max(layout.FirstItemRow, layout.LastItemRow)}";
        foreach (var confidence in new[] { DraftConfidence.Direct, DraftConfidence.Assumption, DraftConfidence.Decision })
        {
            var label = EngineerBoqDraftBuilder.ConfidenceLabel(confidence);
            var count = rows.Count(row => row.Confidence == confidence);
            Row(sheet, r++).Text("A", $"מתוכו שורות '{label}' ({count} שורות)", StyleText)
                .Formula("B", $"SUMIF({Range("I")},\"{label}\",{Range("G")})", StyleCount);
        }
        Pair("שורות בכתב הכמויות", $"{rows.Count} שורות מוצעות מתוך {draft.Elements.Count} רכיבים שנמדדו בשרטוט; ל-{pricedRows} יש מחיר במחירון ול-{rows.Count - pricedRows} אין.");
        Pair("עם השלמות ידניות", $"שורות ההיקף ושורות ההגדרה שבסוף כתב הכמויות נכנסות לשורה '{CompletedTotalLabel}' רק אחרי שמזינים להן כמות, פריט ומחיר. " +
                                   "שורת הגדרה נספרת רק כשיש בה גם מק\"ט וגם מחיר.");
        if (layout.DefinitionCount > 0)
            Row(sheet, r++).Text("A", "רכיבים שהוגדרו", StyleHeader)
                .Formula("B", $"COUNTIF('{BoqSheet}'!I{layout.FirstDefinitionRow}:I{layout.LastDefinitionRow},\"{DefinitionComplete}\")&\" מתוך {layout.DefinitionCount} הושלמו (מק\"\"ט ומחיר)\"", StyleText);
        r++;

        Row(sheet, r++).Text("A", "מה לא נכלל בסכום הביניים", StyleTitle);
        foreach (var (topic, text) in Exclusions(draft, rows, layout))
            Row(sheet, r++).Text("A", topic, StyleWarning).Text("B", text, StyleWarning);
        r++;

        Row(sheet, r++).Text("A", "רגישות לפרמטרים עיקריים", StyleTitle);
        Row(sheet, r++).Text("A", "הסכומים כאן חושבו פעם אחת בעת יצירת הקובץ ואינם מתעדכנים. הם מראים כמה כל הנחה מזיזה את סכום הביניים.", StyleSubtle);
        sheet.SingleRowMerges.Add($"A{r - 1}:B{r - 1}");
        Row(sheet, r++).Text("A", "ערכי ברירת המחדל בקובץ", StyleText).Text("B", $"{Money(pricedTotal)} ₪", StyleText);
        foreach (var (label, setting, total) in Sensitivities(draft, rows))
            Row(sheet, r++).Text("A", label, StyleText)
                .Text("B", $"{Money(total)} ₪ (שינוי של {SignedMoney(total - pricedTotal)} ₪) · הגדרה: {Bidi.Ltr(setting)}", StyleText);
        r++;

        Row(sheet, r++).Text("A", "מקורות בסריקה", StyleTitle);
        Row(sheet, r++).Text("A", "מקור", StyleHeader).Text("B", "תפקיד ומה נעשה איתו", StyleHeader);
        foreach (var source in draft.Sources)
            Row(sheet, r++).Text("A", EngineerBoqDraftBuilder.DisplaySource(source.Source), source.Source == EngineerBoqDraftBuilder.HostSource ? StyleText : StyleLtr)
                .Text("B", $"{EngineerBoqDraftBuilder.SourceRoleLabel(source.Role)} · {source.Records:N0} רשומות · {source.Meaning}", StyleText);
        r++;

        Row(sheet, r++).Text("A", "מקרא — רמת ודאות", StyleTitle);
        Row(sheet, r++).Text("A", EngineerBoqDraftBuilder.ConfidenceLabel(DraftConfidence.Direct), StyleText)
            .Text("B", "פריט יחיד מתאים במחירון לרכיב עם שם ברור. עדיין טעון אישור הנדסי.", StyleText);
        Row(sheet, r++).Text("A", EngineerBoqDraftBuilder.ConfidenceLabel(DraftConfidence.Assumption), StyleAssumption)
            .Text("B", "מבנה, פירוש שם שכבה, בחירת פריט או פרמטר שהוצעו. לאשר או לשנות.", StyleAssumption);
        Row(sheet, r++).Text("A", EngineerBoqDraftBuilder.ConfidenceLabel(DraftConfidence.Decision), StyleDecision)
            .Text("B", "הכמות נמדדה, אבל הפריט, המבנה או ההכללה תלויים בהחלטה שאין בשרטוט.", StyleDecision);
        r++;

        Row(sheet, r++).Text("A", "על מה מבוססות ההצעות", StyleTitle);
        Row(sheet, r++).Text("A", "ספריית השיוך", StyleHeader).Text("B", draft.Library.Basis, StyleText);
        Row(sheet, r).Text("A", "אומדן הייחוס", StyleHeader)
            .Text("B", draft.Library.Texts.ReferenceEstimate, StyleText);
    }

    private static IEnumerable<(string Topic, string Text)> Exclusions(EngineerBoqDraft draft, IReadOnlyList<BoqRow> rows, BoqLayout layout)
    {
        var definitions = draft.Elements.Where(e => e.Rule.Emits.Count == 0).ToList();
        if (definitions.Count > 0)
            yield return ("רכיבים שממתינים להגדרת פריט",
                $"{definitions.Count} רכיבים נמדדו ואין להם פריט מוצע: " +
                string.Join("; ", definitions.Select(e => $"{e.Rule.DisplayName} {Quantity(e.IncludedQuantity)} {e.Unit}")) +
                $". הם מופיעים בסוף גיליון '{BoqSheet}' בלי מחיר.");

        var heldBack = draft.Elements.Where(e => !e.Rule.IncludedByDefault).ToList();
        foreach (var element in heldBack)
        {
            var primary = element.Sources.Where(s => s.InPrimarySource).Sum(s => s.Group.Quantity);
            var copies = element.Sources.Where(s => !s.InPrimarySource).Sum(s => s.Group.Quantity);
            yield return ("לא נכלל עד בדיקה",
                $"{element.Rule.Element}: נמדדו {Quantity(primary)} {element.Unit} במודל הראשי" +
                (copies > 0 ? $" (ועוד {Quantity(copies)} {element.Unit} בהעתקים במודלים אחרים, שלא ייכללו גם אם יאושר)" : string.Empty) +
                $". השורה מופיעה בכתב הכמויות עם כמות 0 עד שמשנים 'לכלול' ל-1 בשורות המודל הראשי בגיליון '{BaseSheet}'.");
        }

        var otherModels = draft.Elements.Where(e => e.Rule.IncludedByDefault)
            .SelectMany(e => e.Sources.Where(s => !s.Included).Select(s => (e, s))).ToList();
        if (otherModels.Count > 0)
        {
            var byUnit = otherModels.GroupBy(x => x.s.Group.Unit).Select(g => $"{Quantity(g.Sum(x => x.s.Group.Quantity))} {g.Key}");
            yield return ("מקורות במודלים נוספים",
                $"{otherModels.Count} מקורות של רכיבים שכבר נמדדו במודל הראשי לא נכללו, כדי למנוע ספירה כפולה (סה\"כ {string.Join(" ו-", byUnit)}). " +
                $"הם מסומנים 0 בעמודה 'לכלול' בגיליון '{BaseSheet}'; לשנות ל-1 רק אם זו עבודה נפרדת.");
        }

        var zeroRows = rows.Where(row => row.Lines.Any(l => l.Element.IncludedQuantity > 0) &&
                                         RowQuantity(row, DefaultParameter(draft), null) == 0).ToList();
        if (zeroRows.Count > 0)
            yield return ("שורות שכמותן 0 בברירת המחדל",
                string.Join("; ", zeroRows.Select(row => $"{Bidi.Ltr(row.Head.Emit.Code)} {row.Head.Emit.Note ?? row.Head.Element.Rule.Element}")) +
                $". הכמות תופיע אם משנים את הפרמטר הרלוונטי בגיליון '{ParametersSheet}'.");

        var mismatched = rows.Where(row => row.Lines.Any(l => l.UnitMismatch)).ToList();
        var unpriced = rows.Where(row => row.Price == null && !row.Lines.Any(l => l.UnitMismatch)).ToList();
        if (unpriced.Count > 0)
            yield return ("פריטים ללא מחיר",
                $"{unpriced.Count} שורות: " + string.Join("; ", unpriced.Select(row =>
                    $"{Bidi.Ltr(row.Head.Emit.Code)} {row.Head.Element.Rule.Element} ({(row.Head.Item == null ? "לא נמצא במחירון" : "אין מחיר במהדורה")})")) +
                ". נספרות 0 עד שמזינים מחיר בשורה.");
        if (mismatched.Count > 0)
            yield return ("יחידה לא תואמת",
                $"{mismatched.Count} שורות שיחידת הפריט שלהן שונה מיחידת המדידה: " +
                string.Join("; ", mismatched.Select(row => $"{Bidi.Ltr(row.Head.Emit.Code)} {row.Head.Element.Rule.Element}")) +
                ". הן לא ייכנסו לסכום גם אם יוזן מחיר; צריך לבחור פריט ביחידה המתאימה או להגדיר המרה.");

        yield return ("היקפים שאינם משורטטים", layout.ScopeCount > 0
            ? $"{layout.ScopeCount} שורות היקף ({draft.Library.Texts.ScopeExamples}) עם כמות 0 בסוף כתב הכמויות — להזנה ידנית. " + draft.Library.Texts.ScopeNote
            : draft.Library.Texts.ScopeNote);

        if (draft.ExcludedByDecision.Count > 0)
            yield return ("הוחרג בהחלטת מהנדס",
                $"{draft.ExcludedByDecision.Count} קבוצות ({draft.ExcludedByDecision.Sum(g => g.Count):N0} עצמים) שסומנו בפרופיל 'לא כמות בנייה'. מפורטות בגיליון '{AidsSheet}'.");

        if (draft.UnmappedDesign.Count > 0)
        {
            var utilities = draft.UnmappedDesign.Count(g => g.Role == DraftLayerRole.DesignUtility);
            var proposed = draft.RecognitionProposals.Where(p => p.Status == RecognitionStatus.Proposed).ToList();
            yield return ("שכבות תכנון שלא שויכו",
                $"{draft.UnmappedDesign.Count} קבוצות מדידה ({draft.UnmappedDesign.Sum(g => g.Count):N0} עצמים), מהן {utilities} של תשתיות בתכנון. " +
                $"מפורטות בגיליון '{UnmappedSheet}'." +
                (draft.RecognitionProposals.Count == 0 ? string.Empty
                    : $" לפי ראיות השרטוט הוצעה משפחה ל-{proposed.Sum(p => p.RecordIds.Count):N0} עצמים ({proposed.Select(p => p.FamilyId).Distinct().Count()} משפחות); " +
                      $"לא נכללו בסכום עד אישור משפחה. פירוט בגיליון '{RecognitionSheet}'."));
        }

        if (draft.UnmeasuredDesignObjects > 0)
            yield return ("עצמים שלא נמדדו",
                $"{draft.UnmeasuredDesignObjects} עצמים במודלי התכנון לא נמדדו (למשל הצללות עם גבול פתוח). הכמויות של הרכיבים שלהם נמוכות מהמשורטט. פירוט בגיליון '{ChecksSheet}'.");

        var overlapPairs = draft.Elements.Sum(e => e.OverlapPairs);
        if (overlapPairs > 0)
            yield return ("חשד לחפיפה",
                $"{overlapPairs} זוגות עצמים ברכיבי כתב הכמויות שהמלבנים החוסמים שלהם חופפים. אם אותו שטח שורטט פעמיים — הסכום גבוה מדי. פירוט בגיליון '{ChecksSheet}'.");

        yield return ("מערכות אחרות", draft.Library.Texts.OtherSystems);
        yield return ("מע\"מ ותוספות", "הסכום לפי מחירי המחירון, לפני מע\"מ, בלי הנחה או תוספת קבלן, בלי בלתי צפויים ובלי תקורות.");
        if (draft.Context.RecordedEstimateDecisions > 0)
            yield return ("החלטות שמורות בפרופיל",
                $"בפרופיל הפרויקט שמורות {draft.Context.RecordedEstimateDecisions} החלטות אומדן. שיוכים מאושרים שתקפים למחירון הפעיל נכללו " +
                "(שורות 'שיוך מאושר בפרופיל'), והחלטות 'לא כמות בנייה' עם שם מאשר הוחלו. מפתחות ישנים בלי מאשר אינם מוחלים.");
    }

    private static IEnumerable<(string Label, string Setting, decimal Total)> Sensitivities(EngineerBoqDraft draft, IReadOnlyList<BoqRow> rows)
    {
        var keys = draft.Library.Parameters.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        (string, string, decimal)? Scenario(string label, string key, double value) =>
            keys.Contains(key)
                ? (label, $"{key} = {value.ToString("0.##", CultureInfo.InvariantCulture)}", TotalAt(draft, rows, Override(draft, key, value), null))
                : null;
        var scenarios = new[]
        {
            Scenario("כל המיסעה נבנית לכל העומק", "FULL_DEPTH_SHARE", 1.0),
            Scenario("שיקום בלבד: קרצוף ושכבה עליונה", "FULL_DEPTH_SHARE", 0.0),
            Scenario("מדרכות ואיים באספלט", "SIDEWALK_PAVER_SHARE", 0.0),
            // The reference estimate's factor, as a sensitivity only; a library without a reference factor shows none.
            draft.Library.Texts.ReferenceGlobalFactor is { } reference
                ? Scenario($"מקדם {reference.ToString("0.##", CultureInfo.InvariantCulture)} על כמויות מדודות (אורך, שטח, נפח)", EngineerBoqLibrary.GlobalFactorKey, reference)
                : null,
        };
        foreach (var scenario in scenarios)
            if (scenario is { } s) yield return s;
        foreach (var element in draft.Elements.Where(e => !e.Rule.IncludedByDefault && e.Rule.Emits.Count > 0 &&
                     !e.Rule.Id.StartsWith("profile-approved:", StringComparison.Ordinal)))
            yield return ($"הכללת '{element.Rule.Element}'", "לכלול = 1 במודל הראשי",
                TotalAt(draft, rows, DefaultParameter(draft), new HashSet<string>(StringComparer.Ordinal) { element.Rule.Id }));
    }

    // ------------------------------------------------------------------- checks

    private static MiniXlsx.Worksheet BuildChecks(EngineerBoqDraft draft)
    {
        var sheet = new MiniXlsx.Worksheet { SheetName = ChecksSheet, RightToLeft = true, FreezeTopRows = 3, PrintLandscapeFitToWidth = true };
        sheet.ColumnWidths.AddRange(new[] { (1, 5.0), (2, 22.0), (3, 70.0), (4, 28.0), (5, 48.0), (6, 11.0), (7, 8.0) });
        Row(sheet, 1).Text("A", "בדיקות והערות — לעבור לפני שימוש בסכום", StyleTitle);
        Row(sheet, 2).Text("A", "קודם השורות שמשפיעות על הסכום. עמודת 'טופל' נועדה לסימון ידני.", StyleSubtle);
        sheet.SingleRowMerges.Add("A2:E2");
        Row(sheet, 3).Text("A", "#", StyleHeader).Text("B", "נושא", StyleHeader).Text("C", "הערה", StyleHeader)
            .Text("D", "משפיע על", StyleHeader).Text("E", "מה לעשות", StyleHeader).Text("F", "משפיע על הסכום", StyleHeader)
            .Text("G", "טופל", StyleHeader);
        var general = new List<DraftWarning>
        {
            new("שיטת בדיקת החפיפות",
                $"הסריקה בודקת חפיפה רק בין עצמים באותה שכבה, ולפי המלבן החוסם שלהם. חפיפה בין שכבות שונות (למשל {draft.Library.Texts.OverlapExample}) לא נבדקה.",
                draft.Library.Texts.OverlapAffects, draft.Library.Texts.OverlapAction, true),
            new("עבודות עפר ופירוק",
                "עבודות עפר, פירוק וניסור אינם נמדדים מהשכבות.",
                "סכום הביניים", draft.Library.ScopeItems.Count > 0
                    ? $"להזין כמויות בשורות ההיקף שבסוף גיליון '{BoqSheet}'."
                    : $"להוסיף שורות ידניות בגיליון '{BoqSheet}' לפי תכניות הפרויקט.", true),
            new("היקף הטיוטה",
                "הטיוטה כוללת רק עבודות חדשות ממודלי התכנון ומהקובץ הראשי. תכנית המדידה, תשתיות קיימות ושכבות עזר לא נכללו ומופיעות בגיליונות נפרדים.",
                "לידיעה", "—", false),
            new("רוחב קו משורטט",
                "רוחב קו משורטט משמש לתמחור רק כשהקו בקובץ הראשי, או כשהסריקה הוכיחה את התמרת ה-XREF של הרשומה עצמה (סיבוב והגדלה אחידה, אותן יחידות), ורק בשרטוט במטרים. רוחב קיים שלא הוכח אינו מוחלף בפרמטר: הקווים מוצגים כרכיב 'להחלטה' ולא מתומחרים. פרמטר הרוחב חל רק על קו שאין לו רוחב משורטט.",
                "שורות סימון לפי רוחב", "לסרוק בגרסה שאוספת את התמרת ה-XREF, או להחליט ידנית על הקווים שסומנו 'רוחב משורטט לא מוכח'.", false),
            new("מחירים",
                "המחירים לקוחים מהמחירון הפעיל, לפני מע\"מ ובלי הנחה או תוספת קבלן. פריט ללא מחיר נשאר 0 עד להצעת מחיר.",
                "סכום הביניים", "—", false),
        };
        // Drawn marking widths exist only in a library that classifies marking layers by width (roads).
        if (draft.Library.WidthClassifiedLayerPatterns.Count == 0) general.RemoveAll(w => w.Topic == "רוחב קו משורטט");
        var r = 4;
        var n = 1;
        foreach (var warning in draft.Warnings.Where(w => w.AffectsTotal).Concat(general.Where(w => w.AffectsTotal))
                     .Select((w, i) => (w, i)).OrderBy(x => TopicPriority(x.w.Topic)).ThenBy(x => x.i).Select(x => x.w)
                     .Concat(draft.Warnings.Where(w => !w.AffectsTotal)).Concat(general.Where(w => !w.AffectsTotal)))
        {
            var style = warning.AffectsTotal ? StyleWarning : StyleText;
            Row(sheet, r++).Number("A", (double)n++, StyleCount).Text("B", warning.Topic, style).Text("C", warning.Message, style)
                .Text("D", warning.Affects, style).Text("E", warning.Action, style)
                .Text("F", warning.AffectsTotal ? "כן" : "לא", style).Text("G", string.Empty, StyleFlagInput);
        }
        r++;
        Row(sheet, r++).Text("B", "ממצאי הסריקה לפי קוד", StyleTitle);
        Row(sheet, r++).Text("A", "#", StyleHeader).Text("B", "חומרה", StyleHeader).Text("C", "דוגמה לכותרת", StyleHeader)
            .Text("D", "קוד", StyleHeader).Text("E", "מופעים · רשומות מושפעות", StyleHeader);
        var f = 1;
        foreach (var finding in draft.Findings)
            Row(sheet, r++).Number("A", (double)f++, StyleCount).Text("B", finding.Severity, StyleText)
                .Text("C", FindingTitle(finding), LatinLed(FindingTitle(finding)) ? StyleLtr : StyleText)
                .Text("D", finding.Code, StyleLtr)
                .Text("E", $"{finding.Count:N0} מופעים · {finding.AffectedRecords:N0} רשומות", StyleText);
        if (draft.Findings.Count == 0) Row(sheet, r).Text("B", "אין ממצאים.", StyleText);
        return sheet;
    }

    // --------------------------------------------------------------- parameters

    private static MiniXlsx.Worksheet BuildParameters(EngineerBoqDraft draft, IReadOnlyList<BoqRow> rows, out Dictionary<string, int> parameterRows)
    {
        var sheet = new MiniXlsx.Worksheet { SheetName = ParametersSheet, RightToLeft = true, FreezeTopRows = 3, PrintLandscapeFitToWidth = true };
        sheet.ColumnWidths.AddRange(new[] { (1, 24.0), (2, 46.0), (3, 12.0), (4, 10.0), (5, 70.0), (6, 34.0), (7, 12.0), (8, 14.0) });
        Row(sheet, 1).Text("A", "פרמטרים והנחות — הערכים הצהובים ניתנים לשינוי", StyleTitle);
        Row(sheet, 2).Text("A", ParameterRangeNotice, StyleSubtle);
        sheet.SingleRowMerges.Add("A2:F2");
        Row(sheet, 3).Text("A", "מפתח", StyleHeader).Text("B", "תיאור", StyleHeader).Text("C", "יחידה", StyleHeader)
            .Text("D", "ערך", StyleHeader).Text("E", "בסיס ההנחה", StyleHeader).Text("F", "משמש בשורות", StyleHeader)
            .Text("G", "ערך בשימוש", StyleHeader).Text("H", "טווח מותר", StyleHeader);
        parameterRows = new Dictionary<string, int>(StringComparer.Ordinal);
        var r = 4;
        foreach (var parameter in draft.Library.Parameters)
        {
            var users = parameter.Key == EngineerBoqLibrary.GlobalFactorKey
                ? string.Join(", ", rows.Where(row => row.Lines.Any(l => !IsCounted(l)))
                    .Select(row => row.Head.Emit.Code + (row.Lines.Any(IsCounted) ? " (חלקית)" : string.Empty)).Distinct(StringComparer.Ordinal))
                : string.Join(", ", rows.Where(row => row.Lines.Any(l => l.Emit.ParameterKeys.Contains(parameter.Key)))
                    .Select(row => row.Head.Emit.Code).Distinct(StringComparer.Ordinal));
            Row(sheet, r).Text("A", parameter.Key, StyleLtrSubtle).Text("B", parameter.Label, StyleText).Text("C", parameter.Unit, StyleText)
                .Number("D", parameter.DefaultValue, StyleInput).Text("E", parameter.Basis, StyleAssumption)
                .Text("F", users.Length == 0 ? "לא בשימוש בסריקה זו" : users, users.Length == 0 ? StyleText : StyleLtr)
                .Formula("G", ParameterInUse(r, parameter), StyleNumber)
                .Text("H", $"{Invariant(parameter.Min)} עד {Invariant(parameter.Max)}", StyleText);
            sheet.DataValidations.Add(new MiniXlsx.DecimalValidation($"D{r}", parameter.Min, parameter.Max, "ערך מחוץ לטווח",
                $"להזין מספר בין {Invariant(parameter.Min)} ל-{Invariant(parameter.Max)}. תא ריק אינו אפס: כדי לבטל רכיב יש להזין 0 במפורש."));
            parameterRows[parameter.Key] = r++;
        }
        return sheet;
    }

    // ---------------------------------------------------------- base measurements

    private static MiniXlsx.Worksheet BuildBase(EngineerBoqDraft draft, out Dictionary<DraftElement, int> totals)
    {
        var sheet = new MiniXlsx.Worksheet { SheetName = BaseSheet, RightToLeft = true, FreezeTopRows = 3, PrintLandscapeFitToWidth = true };
        sheet.ColumnWidths.AddRange(new[] { (1, 28.0), (2, 26.0), (3, 30.0), (4, 16.0), (5, 9.0), (6, 14.0), (7, 7.0), (8, 7.0), (9, 60.0) });
        Row(sheet, 1).Text("A", "מדידות בסיס לפי רכיב — ממה נגזרת כל כמות. עמודת 'לכלול' (1/0) קובעת אם המקור נכנס לסכום.", StyleTitle);
        Row(sheet, 3).Text("A", "רכיב", StyleHeader).Text("B", "מקור", StyleHeader).Text("C", "שכבה · בלוק", StyleHeader)
            .Text("D", "אופן מדידה", StyleHeader).Text("E", "עצמים", StyleHeader).Text("F", "כמות", StyleHeader)
            .Text("G", "יחידה", StyleHeader).Text("H", "לכלול", StyleHeader).Text("I", "הערה", StyleHeader);
        totals = new Dictionary<DraftElement, int>();
        var r = 4;
        foreach (var element in draft.Elements)
        {
            var headerRow = r++;
            var first = r;
            foreach (var source in element.Sources)
            {
                var g = source.Group;
                Row(sheet, r).Text("B", EngineerBoqDraftBuilder.DisplaySource(g.Source), g.Source == EngineerBoqDraftBuilder.HostSource ? StyleText : StyleLtr)
                    .Text("C", g.Block == null ? g.Layer : $"{g.Layer} · {g.Block}", StyleLtr)
                    .Text("D", EngineerBoqDraftBuilder.MethodLabel(g.MethodClass), StyleText)
                    .Number("E", (double)g.Count, StyleCount).Number("F", g.Quantity, StyleNumber).Text("G", g.Unit, StyleText)
                    .Number("H", source.Included ? 1d : 0d, StyleFlagInput)
                    .Text("I", source.Note, string.IsNullOrEmpty(source.Note) ? StyleText : StyleWarning);
                r++;
            }
            var last = r - 1;
            var patterns = string.Join(", ", element.Rule.LayerPatterns) +
                           (element.Rule.BlockPatterns == null ? string.Empty : " · blocks " + string.Join(", ", element.Rule.BlockPatterns));
            Row(sheet, headerRow).Text("A", element.Rule.DisplayName, StyleChapter).Text("B", "סה\"כ נכלל", StyleChapter)
                .Text("C", patterns, StyleLtrChapter)
                .Text("D", EngineerBoqDraftBuilder.ConfidenceLabel(element.Rule.Confidence), StyleChapter)
                .Formula("E", $"SUMPRODUCT(E{first}:E{last},H{first}:H{last})", StyleCountHeader)
                .Formula("F", $"SUMPRODUCT(F{first}:F{last},H{first}:H{last})", StyleChapterTotal)
                .Text("G", element.Unit, StyleChapter).Text("H", string.Empty, StyleChapter)
                .Text("I", element.Rule.Note, StyleChapter);
            totals[element] = headerRow;
            r++;
        }
        if (draft.Elements.Count == 0) Row(sheet, r).Text("A", "לא נמצאו רכיבים מהספרייה בסריקה זו.", StyleText);
        return sheet;
    }

    // ------------------------------------------------------------------------ BoQ

    /// <summary>
    /// The approval note of a BoQ row. Only sources of the counted model speak for a row: an approval on a copy the rule
    /// does not count (F-5, review 03.10) is reported as not applied in the checks and says nothing about this line.
    /// </summary>
    internal static string? ApprovalNote(BoqRow row)
    {
        if (row.Lines.Any(line => line.Element.Rule.Id.StartsWith("profile-approved:", StringComparison.Ordinal)))
            return "שיוך מאושר בפרופיל";
        if (row.Lines.Any(line => line.Element.Sources.Any(s => s.InPrimarySource &&
                string.Equals(s.Group.ApprovedCode, line.Emit.Code, StringComparison.OrdinalIgnoreCase))))
            return "הסעיף הזה אושר בפרופיל לשכבה; שאר שורות המתכון הן הצעה ואינן מאושרות";
        if (row.Lines.Any(line => line.Element.Sources.Any(s => s.InPrimarySource && s.Group.ApprovedCode != null)))
            return "שורת מתכון מוצעת — בפרופיל אושר לשכבה רק סעיף אחר של המתכון; שורה זו אינה מאושרת";
        return null;
    }

    internal static IReadOnlyList<BoqRow> BoqRows(EngineerBoqDraft draft) =>
        draft.Lines.GroupBy(line => line.RowKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var lines = group.OrderBy(l => l.Element.Rule.Element, StringComparer.Ordinal).ToList();
                return new BoqRow(lines[0].Chapter, lines[0].SubChapter, group.Key, lines);
            })
            .OrderBy(row => row.SubChapter, StringComparer.Ordinal)
            .ThenBy(row => row.Head.Emit.Code, StringComparer.Ordinal)
            .ThenBy(row => row.RowKey, StringComparer.Ordinal)
            .ToList();

    private static MiniXlsx.Worksheet BuildBoq(EngineerBoqDraft draft, IReadOnlyList<BoqRow> rows,
        IReadOnlyDictionary<string, int> parameterRows, IReadOnlyDictionary<DraftElement, int> elementTotalRows,
        out BoqLayout layout, out decimal pricedTotal, out int pricedRows)
    {
        var sheet = new MiniXlsx.Worksheet { SheetName = BoqSheet, RightToLeft = true, FreezeTopRows = 4, PrintLandscapeFitToWidth = true };
        sheet.ColumnWidths.AddRange(new[] { (1, 14.0), (2, 13.0), (3, 55.0), (4, 7.0), (5, 13.0), (6, 12.0), (7, 15.0), (8, 24.0), (9, 13.0), (10, 45.0) });
        Row(sheet, 1).Text("A", $"כתב כמויות — טיוטה להנדסה ({draft.Library.Texts.Structure})", StyleTitle);
        Row(sheet, 2).Text("A", DraftNotice, StyleNotice);
        sheet.SingleRowMerges.Add("A2:J2");
        sheet.Rows[^1].HeightPoints = 30;
        Row(sheet, 4).Text("A", "מס'", StyleHeader).Text("B", "מק\"ט", StyleHeader).Text("C", "תיאור", StyleHeader)
            .Text("D", "יח'", StyleHeader).Text("E", "כמות", StyleHeader).Text("F", "מחיר יח'", StyleHeader)
            .Text("G", "סה\"כ", StyleHeader).Text("H", "רכיב בשרטוט", StyleHeader).Text("I", "ודאות", StyleHeader)
            .Text("J", "הערה / מקור הכמות", StyleHeader);
        var globalRow = parameterRows[EngineerBoqLibrary.GlobalFactorKey];
        var parameter = DefaultParameter(draft);
        pricedTotal = 0m;
        pricedRows = 0;
        var r = 5;
        var firstItemRow = r;
        var subChapterTotals = new List<int>();
        foreach (var chapter in rows.GroupBy(row => row.Chapter).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (draft.ChapterTitles.TryGetValue(chapter.Key, out var chapterTitle))
            {
                Row(sheet, r).Text("A", $"פרק {chapter.Key}", StyleChapter).Text("B", string.Empty, StyleChapter)
                    .Text("C", $"פרק {chapterTitle}", StyleChapter);
                r++;
            }
            foreach (var sub in chapter.GroupBy(row => row.SubChapter).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var title = draft.ChapterTitles.TryGetValue(sub.Key, out var t) ? $"תת פרק {t}" : $"תת פרק {sub.Key}";
                Row(sheet, r).Text("A", sub.Key, StyleChapter).Text("B", string.Empty, StyleChapter).Text("C", title, StyleChapter);
                r++;
                var first = r;
                var itemNumber = 0;
                foreach (var row in sub)
                {
                    itemNumber++;
                    var formula = QuantityFormula(row, elementTotalRows, parameterRows, globalRow);
                    var confidence = row.Confidence;
                    var noteStyle = confidence switch
                    {
                        DraftConfidence.Assumption => StyleAssumption,
                        DraftConfidence.Decision => StyleDecision,
                        _ => StyleText,
                    };
                    var notes = new List<string>();
                    var baseRows = string.Join(", ", row.Lines.Select(line => elementTotalRows[line.Element]).Distinct().OrderBy(n => n));
                    var emitNotes = row.Lines.Select(line => line.Emit.Note).Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
                    var distinctElements = row.Lines.Select(line => line.Element).Distinct().Count();
                    if (emitNotes.Count <= 4) notes.AddRange(emitNotes);
                    if (row.Lines.Count <= 3)
                        notes.Add("מקור הכמות: " + string.Join("; ", row.Lines.Select(line =>
                            $"{line.Element.Rule.DisplayName} (גיליון '{BaseSheet}' שורה {elementTotalRows[line.Element]})").Distinct(StringComparer.Ordinal)));
                    else
                        notes.Add($"הכמות מחברת {row.Lines.Count} תרומות מ-{distinctElements} רכיבים, כל אחת לפי אופן המדידה שלה (רוחב משורטט, פרמטר או מספר יחידות). " +
                                  $"פירוט החישוב בגיליון '{BaseSheet}' שורות {baseRows}.");
                    if (ApprovalNote(row) is { } approvalNote) notes.Add(approvalNote);
                    var elements = row.Lines.Select(line => line.Element).Distinct().ToList();
                    var unmeasured = elements.Sum(e => e.UnmeasuredObjects);
                    if (unmeasured > 0) notes.Add($"לא נמדדו {unmeasured} עצמים — הכמות חסרה");
                    if (elements.Any(e => e.OverlapPairs > 0)) notes.Add("חשד לחפיפה — לבדוק");
                    if (elements.FirstOrDefault(e => !e.Rule.IncludedByDefault) is { } heldBack)
                        notes.Add(heldBack.Rule.HeldBackNote ?? "לא נכלל עד בדיקה בשרטוט (כמות 0)");
                    if (row.Head.Item == null) notes.Add("הפריט לא נמצא במחירון הפעיל");
                    else if (row.Lines.Any(l => l.UnitMismatch))
                        notes.Add($"יחידת הפריט אינה תואמת את המדידה ({row.Head.Element.Unit}) — לא תומחר, ומחיר שיוזן כאן לא ייכנס לסכום; לבחור פריט ביחידה המתאימה או להגדיר המרה");
                    else if (row.Price == null) notes.Add("אין מחיר במהדורת המחירון — להזין מחיר מהצעה בעמודה 'מחיר יח'' והסכום יתעדכן");
                    var outRow = Row(sheet, r).Text("A", $"01.{sub.Key}.{itemNumber * 10:0000}", StyleText)
                        .Text("B", row.Head.Emit.Code, StyleLtr)
                        .Text("C", row.Head.Item?.Description ?? "(הפריט לא נמצא במחירון הפעיל)", StyleText)
                        .Text("D", row.Head.Item?.UnitRaw.Trim() ?? string.Empty, StyleText)
                        .Formula("E", formula, StyleNumber);
                    if (row.Price is { } price)
                    {
                        outRow.Number("F", price, StyleNumber).Formula("G", $"ROUND(E{r}*F{r},2)", StyleNumber);
                        pricedRows++;
                        pricedTotal += RowTotal(RowQuantity(row, parameter, null), price);
                    }
                    else if (row.Lines.Any(l => l.UnitMismatch))
                    {
                        // The quantity is in the measured unit, not the item's: a typed price must never reach a total.
                        outRow.Text("F", "—", StyleDecision).Number("G", 0d, StyleNumber);
                    }
                    else
                    {
                        // A price typed over the dash flows into every total; until then the row counts 0.
                        outRow.Text("F", "—", StyleDecision).Formula("G", LiveTotal(r), StyleNumber);
                    }
                    outRow.Text("H", string.Join(" + ", row.Lines.Select(line => line.Element.Rule.Element).Distinct(StringComparer.Ordinal)), StyleText)
                        .Text("I", EngineerBoqDraftBuilder.ConfidenceLabel(confidence), noteStyle)
                        .Text("J", string.Join(" · ", notes), noteStyle);
                    r++;
                }
                var last = r - 1;
                Row(sheet, r).Text("C", $"סה\"כ {title}", StyleChapter).Formula("G", $"SUM(G{first}:G{last})", StyleChapterTotal);
                subChapterTotals.Add(r);
                r += 2;
            }
        }
        var lastItemRow = Math.Max(firstItemRow, r - 1);

        var subtotalRow = r;
        Row(sheet, subtotalRow).Text("C", SubtotalLabel, StyleChapter)
            .Formula("G", subChapterTotals.Count == 0 ? "0" : string.Join("+", subChapterTotals.Select(c => $"G{c}")), StyleSubtotalBoq);
        r += 3;

        // Items the drawing cannot measure: explicit input rows, zero until the engineer fills them.
        Row(sheet, r++).Text("C", "היקפי עבודה שאינם משורטטים — להזנת כמות (לא נכללים בסכום הביניים עד להזנה)", StyleTitle);
        var scopeFirst = r;
        foreach (var scope in draft.Library.ScopeItems)
        {
            var item = FindItem(draft, scope.Code);
            var price = FindPrice(draft, scope.Code);
            var outRow = Row(sheet, r).Text("A", "היקף", StyleDecision).Text("B", scope.Code, StyleLtr)
                .Text("C", item?.Description ?? scope.Element, StyleText).Text("D", item?.UnitRaw.Trim() ?? string.Empty, StyleText)
                .Number("E", 0d, StyleInput);
            if (price is { } p) outRow.Number("F", p, StyleNumber).Formula("G", $"ROUND(E{r}*F{r},2)", StyleNumber);
            else outRow.Text("F", "—", StyleDecision).Formula("G", LiveTotal(r), StyleNumber);
            outRow.Text("H", scope.Element, StyleText).Text("I", EngineerBoqDraftBuilder.ConfidenceLabel(DraftConfidence.Decision), StyleDecision)
                .Text("J", scope.Note, StyleDecision);
            r++;
        }
        var scopeLast = r - 1;
        Row(sheet, r).Text("C", "סה\"כ היקפים שהוזנו", StyleChapter)
            .Formula("G", scopeLast >= scopeFirst ? $"SUM(G{scopeFirst}:G{scopeLast})" : "0", StyleChapterTotal);
        var scopeTotalRow = r;
        r += 3;

        // Elements with a measured quantity but no proposed item: the engineer can complete them in place.
        var definitions = draft.Elements.Where(e => e.Rule.Emits.Count == 0).ToList();
        var definitionTotalRow = 0;
        var definitionRange = (0, 0);
        if (definitions.Count > 0)
        {
            Row(sheet, r++).Text("C", "רכיבים שנמדדו וממתינים להגדרת פריט — להזין פריט ומחיר (לא נכללים בסכום הביניים)", StyleTitle);
            var definitionFirst = r;
            foreach (var element in definitions)
            {
                Row(sheet, r).Text("A", "הגדרה", StyleDecision).Text("B", DefinitionPlaceholder, StyleDecision)
                    .Text("C", element.Rule.DisplayName, StyleText).Text("D", element.Unit, StyleText)
                    .Formula("E", element.Sources.Count > 0 && element.Sources.All(s => s.Group.Kind == "count")
                        ? $"'{BaseSheet}'!F{elementTotalRows[element]}"
                        : $"'{BaseSheet}'!F{elementTotalRows[element]}*'{ParametersSheet}'!G{globalRow}", StyleNumber)
                    .Text("F", "—", StyleDecision).Formula("G", DefinitionTotal(r), StyleNumber)
                    .Text("H", element.Rule.DisplayName, StyleText)
                    .Formula("I", DefinitionStatus(r), StyleDecision)
                    .Text("J", element.Rule.Note + " · להזין מק\"ט בעמודה B ומחיר בעמודה F. השורה נכנסת לסכום רק כששניהם מולאו; מחיר בלי מק\"ט אינו נספר.", StyleDecision);
                r++;
            }
            Row(sheet, r).Text("C", "סה\"כ רכיבים שהוגדרו", StyleChapter)
                .Formula("G", $"SUM(G{definitionFirst}:G{r - 1})", StyleChapterTotal)
                .Formula("I", $"COUNTIF(I{definitionFirst}:I{r - 1},\"{DefinitionComplete}\")&\" מתוך {definitions.Count} הושלמו\"", StyleChapter);
            definitionTotalRow = r;
            definitionRange = (definitionFirst, r - 1);
            r += 2;
        }
        Row(sheet, r).Text("C", CompletedTotalLabel, StyleChapter)
            .Formula("G", $"G{subtotalRow}+G{scopeTotalRow}" + (definitionTotalRow > 0 ? $"+G{definitionTotalRow}" : string.Empty), StyleSubtotalBoq);
        r += 2;
        layout = new BoqLayout(firstItemRow, lastItemRow, subtotalRow, scopeTotalRow, draft.Library.ScopeItems.Count, definitions.Count,
            definitionRange.Item1, definitionRange.Item2);
        return sheet;
    }

    /// <summary>A price cell that may hold text ('—') until the engineer types a price.</summary>
    private static string LiveTotal(int row) => $"IF(ISNUMBER(F{row}),ROUND(E{row}*F{row},2),0)";

    /// <summary>
    /// A definition row counts only when it has both an item code (not blank, not the placeholder dash) and a numeric
    /// price. A price alone never completes it, and deleting the code returns the row to "missing".
    /// </summary>
    internal static string DefinitionTotal(int row) =>
        $"IF(AND(ISNUMBER(F{row}),LEN(TRIM(B{row}))>0,B{row}<>\"{DefinitionPlaceholder}\"),ROUND(E{row}*F{row},2),0)";

    internal static string DefinitionStatus(int row) =>
        $"IF(OR(LEN(TRIM(B{row}))=0,B{row}=\"{DefinitionPlaceholder}\"),\"{DefinitionMissingCode}\",IF(ISNUMBER(F{row}),\"{DefinitionComplete}\",\"{DefinitionMissingPrice}\"))";

    internal const string DefinitionPlaceholder = "—";
    internal const string DefinitionMissingCode = "חסר מק\"\"ט";
    internal const string DefinitionMissingPrice = "חסר מחיר";
    internal const string DefinitionComplete = "הושלם";

    /// <summary>The value formulas use: the typed value only when it is a number inside the allowed range, otherwise #N/A.</summary>
    internal static string ParameterInUse(int row, DraftParameter parameter) =>
        $"IF(AND(ISNUMBER(D{row}),D{row}>={Invariant(parameter.Min)},D{row}<={Invariant(parameter.Max)}),D{row},NA())";

    private const string ParameterRangeNotice =
        "הנוסחאות משתמשות בעמודה 'ערך בשימוש'. ערך ריק, טקסט או ערך מחוץ לטווח מוצג שם כ-#N/A, והשורות התלויות והסכום לא יחושבו עד לתיקון. אפס מפורש הוא ערך תקין.";

    /// <summary>Counted objects (shelters, poles, arrows) are whole items: the global quantity factor never scales them.</summary>
    internal static bool IsCounted(DraftLine line) =>
        line.Element.Sources.Count > 0 && line.Element.Sources.All(s => s.Group.Kind == "count");

    private static string QuantityFormula(BoqRow row, IReadOnlyDictionary<DraftElement, int> elementTotalRows,
        IReadOnlyDictionary<string, int> parameterRows, int globalRow)
    {
        var measured = row.Lines.Where(l => !IsCounted(l)).Select(l => Contribution(l, elementTotalRows, parameterRows)).ToList();
        var counted = row.Lines.Where(IsCounted).Select(l => Contribution(l, elementTotalRows, parameterRows)).ToList();
        var parts = new List<string>();
        if (measured.Count == 1) parts.Add($"{measured[0]}*'{ParametersSheet}'!G{globalRow}");
        else if (measured.Count > 1) parts.Add($"({string.Join("+", measured)})*'{ParametersSheet}'!G{globalRow}");
        parts.AddRange(counted);
        return string.Join("+", parts);
    }

    private static string Contribution(DraftLine line, IReadOnlyDictionary<DraftElement, int> elementTotalRows,
        IReadOnlyDictionary<string, int> parameterRows)
    {
        var text = $"'{BaseSheet}'!F{elementTotalRows[line.Element]}";
        if (line.Emit.Factor != 1.0) text += $"*{Invariant(line.Emit.Factor)}";
        if (line.Emit.ParameterKey != null) text += $"*'{ParametersSheet}'!G{parameterRows[line.Emit.ParameterKey]}";
        if (line.Emit.SecondParameterKey != null) text += $"*'{ParametersSheet}'!G{parameterRows[line.Emit.SecondParameterKey]}";
        if (line.Emit.ComplementKey != null) text += $"*(1-'{ParametersSheet}'!G{parameterRows[line.Emit.ComplementKey]})";
        return text;
    }

    // ---------------------------------------------------------------- recognition

    /// <summary>
    /// Recognition proposals for unmapped design groups: what the drawing evidence suggests each group is, never priced.
    /// Approving a family (once, for all matching groups) is an engineer decision made in the tool, not in this sheet.
    /// </summary>
    private static MiniXlsx.Worksheet BuildRecognition(EngineerBoqDraft draft)
    {
        var sheet = new MiniXlsx.Worksheet { SheetName = RecognitionSheet, RightToLeft = true, FreezeTopRows = 3, PrintLandscapeFitToWidth = true };
        sheet.ColumnWidths.AddRange(new[] { (1, 22.0), (2, 24.0), (3, 12.0), (4, 9.0), (5, 13.0), (6, 7.0), (7, 11.0), (8, 26.0), (9, 20.0), (10, 50.0), (11, 40.0), (12, 34.0) });
        Row(sheet, 1).Text("A", "הצעות זיהוי לשכבות שלא שויכו — לפי ראיות מהשרטוט (בלוק, תכונות, מקרא, PropertySet, טקסט סמוך, רוחב). " +
                                "אלה הצעות בלבד: לא נכללו בסכום ולא אושרו. אישור משפחה נעשה בכלי, פעם אחת לכל הקבוצות המתאימות.", StyleNotice);
        sheet.SingleRowMerges.Add("A1:L1");
        sheet.Rows[^1].HeightPoints = 32;
        Row(sheet, 3).Text("A", "מקור", StyleHeader).Text("B", "שכבה", StyleHeader).Text("C", "סוג", StyleHeader)
            .Text("D", "עצמים", StyleHeader).Text("E", "כמות", StyleHeader).Text("F", "יחידה", StyleHeader)
            .Text("G", "מצב", StyleHeader).Text("H", "משפחה מוצעת", StyleHeader).Text("I", "סעיפים אפשריים", StyleHeader)
            .Text("J", "ראיות מהשרטוט", StyleHeader).Text("K", "פרשנות וחלופות", StyleHeader).Text("L", "מה חסר להכרעה", StyleHeader);
        var groups = draft.RecognitionGroups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
        var r = 4;
        foreach (var proposal in draft.RecognitionProposals
                     .OrderBy(p => p.Status == RecognitionStatus.Proposed ? 0 : 1)
                     .ThenBy(p => p.FamilyId ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(p => p.GroupId, StringComparer.Ordinal))
        {
            groups.TryGetValue(proposal.GroupId, out var group);
            var ids = proposal.RecordIds.ToHashSet(StringComparer.Ordinal);
            var covered = group?.Records.Where(x => ids.Contains(x.RecordId)).ToList() ?? new List<NeutralQuantityRecord>();
            var family = draft.Library.Rules.FirstOrDefault(x => x.Id == proposal.FamilyId);
            var proposed = proposal.Status == RecognitionStatus.Proposed;
            var style = proposed ? StyleAssumption : StyleDecision;
            var interpretation = string.Join(" · ", proposal.Inferred.Concat(proposal.Alternatives.Select(a =>
                $"חלופה: {draft.Library.Rules.FirstOrDefault(x => x.Id == a.FamilyId)?.Element ?? a.FamilyId}" + (string.IsNullOrWhiteSpace(a.Why) ? string.Empty : $" ({a.Why})"))));
            Row(sheet, r++).Text("A", group == null ? string.Empty : EngineerBoqDraftBuilder.DisplaySource(group.Source),
                    group?.Source == EngineerBoqDraftBuilder.HostSource ? StyleText : StyleLtr)
                .Text("B", group?.LayerLeaf ?? proposal.GroupId, StyleLtr)
                .Text("C", group == null ? string.Empty : EngineerBoqDraftBuilder.KindLabel(group.Kind), StyleText)
                .Number("D", (double)covered.Count, StyleCount)
                .Number("E", covered.Sum(x => x.Measurement.RawValue), StyleNumber)
                .Text("F", group?.Unit ?? string.Empty, StyleText)
                .Text("G", proposed ? "הצעה — לא מאושרת" : "לא הוכרע", style)
                .Text("H", family?.Element ?? string.Empty, style)
                .Text("I", string.Join(", ", proposal.CandidateCodes), StyleLtr)
                .Text("J", string.Join(" · ", proposal.Observed), StyleText)
                .Text("K", interpretation, StyleText)
                .Text("L", string.Join(" · ", proposal.MissingDetails), proposal.MissingDetails.Count == 0 ? StyleText : StyleWarning);
        }
        return sheet;
    }

    // --------------------------------------------------------------- group sheets

    private static MiniXlsx.Worksheet BuildGroupsSheet(string name, string description, IReadOnlyList<DraftBaseGroup> groups)
    {
        var sheet = new MiniXlsx.Worksheet { SheetName = name, RightToLeft = true, FreezeTopRows = 3, PrintLandscapeFitToWidth = true };
        sheet.ColumnWidths.AddRange(new[] { (1, 26.0), (2, 28.0), (3, 18.0), (4, 16.0), (5, 9.0), (6, 14.0), (7, 7.0), (8, 30.0), (9, 58.0) });
        Row(sheet, 1).Text("A", description, StyleNotice);
        sheet.SingleRowMerges.Add("A1:I1");
        sheet.Rows[^1].HeightPoints = 32;
        Row(sheet, 3).Text("A", "מקור", StyleHeader).Text("B", "שכבה", StyleHeader).Text("C", "סוג", StyleHeader)
            .Text("D", "אופן מדידה", StyleHeader).Text("E", "עצמים", StyleHeader).Text("F", "כמות", StyleHeader)
            .Text("G", "יחידה", StyleHeader).Text("H", "בלוקים עיקריים", StyleHeader).Text("I", "סיבה", StyleHeader);
        var r = 4;
        foreach (var g in groups)
        {
            Row(sheet, r++).Text("A", EngineerBoqDraftBuilder.DisplaySource(g.Source), g.Source == EngineerBoqDraftBuilder.HostSource ? StyleText : StyleLtr)
                .Text("B", g.Layer, StyleLtr)
                .Text("C", $"{EngineerBoqDraftBuilder.LayerRoleLabel(g.Role)} · {EngineerBoqDraftBuilder.KindLabel(g.Kind)}", StyleText)
                .Text("D", EngineerBoqDraftBuilder.MethodLabel(g.MethodClass), StyleText)
                .Number("E", (double)g.Count, StyleCount).Number("F", g.Quantity, StyleNumber).Text("G", g.Unit, StyleText)
                .Text("H", g.TopBlocks ?? string.Empty, LatinLed(g.TopBlocks) ? StyleLtr : StyleText).Text("I", g.Reason, StyleText);
        }
        if (groups.Count == 0) Row(sheet, r).Text("A", "אין רשומות בקטגוריה זו.", StyleText);
        return sheet;
    }

    // ------------------------------------------------------------------- helpers

    private static MiniXlsx.OutRow Row(MiniXlsx.Worksheet sheet, int index)
    {
        var existing = sheet.Rows.FirstOrDefault(row => row.Index == index);
        if (existing != null) return existing;
        var row = new MiniXlsx.OutRow(index);
        sheet.Rows.Add(row);
        return row;
    }

    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Hebrew wording for scan findings whose original title is English or speaks about the approved estimate.</summary>
    private static string FindingTitle(DraftFindingSummary finding) => finding.Code switch
    {
        EstimateFindingCodes.MeasurementFailed =>
            "עצמים נתמכים שהסריקה לא הצליחה למדוד. הממצא חוסם את האומדן המאושר, לא את הטיוטה; הפירוט לפי שכבה נמצא בשורות 'כמות חסרה' ו'עצמים שלא נמדדו' למעלה.",
        EstimateFindingCodes.OverlapRisk =>
            "חשד לחפיפה בין עצמים באותה שכבה (לפי מלבן חוסם). הפירוט לפי רכיב נמצא בשורות 'חפיפה אפשרית' למעלה.",
        _ => finding.ExampleTitle,
    };

    private static readonly Regex LatinToken = new(@"(?:(?<=^|[\s(])-)?[A-Za-z0-9](?:[A-Za-z0-9_$.+\-]*[A-Za-z0-9_$])?", RegexOptions.CultureInvariant);
    private static readonly HashSet<int> RtlStyles = new() { StyleTitle, StyleHeader, StyleText, StyleChapter, StyleWarning,
        StyleAssumption, StyleDecision, StyleNotice, StyleSubtle };

    /// <summary>
    /// Keeps Latin names (layers, codes, parameter keys) intact inside Hebrew cells: each Latin token is isolated
    /// with left-to-right marks so a name like 24-TAMRUR-BUS_ST is not reordered by the right-to-left paragraph.
    /// </summary>
    internal static string IsolateLatin(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(c => c is >= '\u0590' and <= '\u05FF')) return text;
        return LatinToken.Replace(text, m => m.Value.Any(char.IsAsciiLetter) ? $"\u200E{m.Value}\u200E" : m.Value);
    }

    private static void IsolateLatinInHebrewCells(MiniXlsx.Workbook workbook)
    {
        foreach (var sheet in new MiniXlsx.Worksheet[] { workbook }.Concat(workbook.AdditionalSheets))
            foreach (var row in sheet.Rows)
                for (var i = 0; i < row.Cells.Count; i++)
                {
                    var cell = row.Cells[i];
                    // Catalog descriptions (BoQ column C) are the price list's own text and stay byte-identical.
                    if (cell.Kind == MiniXlsx.CellKind.Text && RtlStyles.Contains(cell.Style) &&
                        !(sheet.SheetName == BoqSheet && cell.Reference.StartsWith("C", StringComparison.Ordinal)))
                        row.Cells[i] = cell with { Value = IsolateLatin(cell.Value) };
                }
    }

    /// <summary>True when the first letter is Latin: such text reads left to right even inside a Hebrew sheet.</summary>
    internal static bool LatinLed(string? text)
    {
        foreach (var c in text ?? string.Empty)
        {
            if (c is >= '\u0590' and <= '\u05FF') return false;
            if (char.IsLetter(c)) return true;
        }
        return false;
    }

    /// <summary>Checks that change the subtotal most come first: plausibility, missing quantities, duplicates, then prices.</summary>
    private static int TopicPriority(string topic) => topic switch
    {
        _ when topic.StartsWith("בדיקת סבירות", StringComparison.Ordinal) => 0,
        "שיוך מאושר לא הוחל" or "שיוך מאושר חלקי" or "שיוך מאושר תלוי בפרמטר" or "החרגה נדחתה" => 1,
        "עצמים שלא נמדדו" or "כמות חסרה" => 2,
        "חפיפה אפשרית" or "שיטת בדיקת החפיפות" or "שטח ללא הצללה" or "מקורות שחוברו" => 3,
        "לא נכלל עד בדיקה" or "מועמד שלא אושר" or "רכיב ללא פריט" or "עבודות עפר ופירוק" => 4,
        "פריט ללא מחיר" or "פריט חסר במחירון" or "יחידה לא תואמת" or "קווים רוחביים דקים" => 5,
        _ => 6,
    };

    private static string Quantity(double value) => value.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,##0", CultureInfo.InvariantCulture);

    private static string SignedMoney(decimal value) => (value > 0 ? "+" : value < 0 ? "−" : string.Empty) + Money(Math.Abs(value));

    private static CatalogItem? FindItem(EngineerBoqDraft draft, string code) =>
        draft.Catalog.Items.TryGetValue(code, out var item) ? item : null;

    private static decimal? FindPrice(EngineerBoqDraft draft, string code) =>
        draft.Catalog.Prices.TryGetValue(code, out var price) && !price.IsMissing ? price.Price : null;

    internal static Func<string, double> DefaultParameter(EngineerBoqDraft draft)
    {
        var values = draft.Library.Parameters.ToDictionary(p => p.Key, p => p.DefaultValue, StringComparer.Ordinal);
        return key => values[key];
    }

    private static Func<string, double> Override(EngineerBoqDraft draft, string key, double value)
    {
        var defaults = DefaultParameter(draft);
        return k => k == key ? value : defaults(k);
    }

    /// <summary>
    /// The quantity the workbook computes for one contribution, in the same operation
    /// order as the Excel formula: base × factor × P1 × P2 × (1 − C).
    /// </summary>
    internal static double ContributionQuantity(DraftLine line, Func<string, double> parameter, ISet<string>? includeRules)
    {
        var element = line.Element;
        var forceInclude = includeRules != null && includeRules.Contains(element.Rule.Id);
        var quantity = element.Sources.Where(s => s.Included || (forceInclude && s.InPrimarySource)).Sum(s => s.Group.Quantity);
        if (line.Emit.Factor != 1.0) quantity *= line.Emit.Factor;
        if (line.Emit.ParameterKey != null) quantity *= parameter(line.Emit.ParameterKey);
        if (line.Emit.SecondParameterKey != null) quantity *= parameter(line.Emit.SecondParameterKey);
        if (line.Emit.ComplementKey != null) quantity *= 1 - parameter(line.Emit.ComplementKey);
        return quantity;
    }

    /// <summary>The row quantity in the Excel formula's operation order: (measured…) × global + counted….</summary>
    internal static double RowQuantity(BoqRow row, Func<string, double> parameter, ISet<string>? includeRules)
    {
        var measured = row.Lines.Where(l => !IsCounted(l)).ToList();
        var quantity = 0d;
        if (measured.Count > 0)
        {
            var sum = 0d;
            foreach (var line in measured) sum += ContributionQuantity(line, parameter, includeRules);
            quantity = sum * parameter(EngineerBoqLibrary.GlobalFactorKey);
        }
        foreach (var line in row.Lines.Where(IsCounted)) quantity += ContributionQuantity(line, parameter, includeRules);
        return quantity;
    }

    /// <summary>Excel ROUND(E×F, 2): half away from zero.</summary>
    internal static decimal RowTotal(double quantity, decimal price) =>
        Math.Round((decimal)quantity * price, 2, MidpointRounding.AwayFromZero);

    internal static decimal TotalAt(EngineerBoqDraft draft, IReadOnlyList<BoqRow> rows, Func<string, double> parameter, ISet<string>? includeRules) =>
        rows.Where(row => row.Price != null).Sum(row => RowTotal(RowQuantity(row, parameter, includeRules), row.Price!.Value));
}
