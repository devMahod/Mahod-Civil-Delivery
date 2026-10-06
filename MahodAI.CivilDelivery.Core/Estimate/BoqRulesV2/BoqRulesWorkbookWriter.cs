using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2
{
    /// <summary>
    /// The NTI-format bill of quantities workbook (based on the reference כתב_כמויות_6422_v7.xlsx, v4 of
    /// 30.09.2026): כתב כמויות, הסבר, פרמטרים, תמרורים, מעברי חציה, פירוט חישוב, עצם אחד פעם אחת, לא נכלל, לא סווג,
    /// חסרים, מקורות. Every quantity is an Excel formula: measured base × a cell of the parameters sheet (signs and
    /// crossings through their own sheets; a line_sum line as the sum of its lines' rounded cells; a count_of line as the
    /// count cell of its line), so the engineer sees and can change each assumption. Project texts come from the ruleset
    /// (workbook, sign_rules, crosswalk_geometry, control_reasons). Exact text/layout parity still requires independent
    /// acceptance; diagnostics are run-derived and reference example-only rows depend on an explicit rules contract.
    /// Written with MiniXlsx (no OpenXml in the
    /// host), which stores each formula with its computed (cached) value, so previews that do not calculate (Teams,
    /// Protected View, phones) still show the quantities; Excel still recalculates on open (fullCalcOnLoad). A cell the
    /// reference leaves empty is never written as an empty text.
    /// </summary>
    public static class BoqRulesWorkbookWriter
    {
        public const string SheetBoq = "כתב כמויות";
        public const string SheetExplain = "הסבר";
        public const string SheetParams = "פרמטרים";
        public const string SheetSigns = "תמרורים";
        public const string SheetCross = "מעברי חציה";
        public const string SheetDetail = "פירוט חישוב";
        public const string SheetObjects = "עצם אחד פעם אחת";
        public const string SheetExcluded = "לא נכלל";
        public const string SheetUnclassified = "לא סווג";
        public const string SheetMissing = "חסרים";
        public const string SheetSources = "מקורות";

        /// <summary>Hebrew labels for the ruleset source keys (the engineer reads the sheet, not the JSON keys).</summary>
        internal static readonly IReadOnlyDictionary<string, string> SourceLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["signs_size"] = "מידות תמרורים", ["marking_size"] = "מידות סימון", ["poles"] = "אורך עמודים",
            ["method"] = "שיטת חישוב הסימון", ["items"] = "סעיפים ומחירים", ["marking_numbers"] = "מספרי סימון",
        };

        /// <summary>"עצם אחד" for one, "12 עצמים" otherwise — "1 עצמים" is wrong Hebrew (reference count_he).</summary>
        internal static string CountHe(int n, string one, string many) =>
            n == 1 ? one : n.ToString(CultureInfo.InvariantCulture) + " " + many;

        public static IReadOnlyList<string> SheetOrder { get; } = new[]
        {
            SheetBoq, SheetExplain, SheetParams, SheetSigns, SheetCross, SheetDetail, SheetObjects,
            SheetExcluded, SheetUnclassified, SheetMissing, SheetSources,
        };

        // ---- reference texts (build_boq_v7.py v4); project facts in them come from the ruleset / the result ----
        internal const string ObjectsDuplicateBlocksTitle = "בלוקים כפולים (אותו בלוק באותה נקודה, גם בין קבצים) — נספרו פעם אחת";

        internal static readonly string[] ObjectsHeaders =
        {
            "סעיף", "חלק", "קובץ", "שכבות", "קווים", "סכום כל הקווים כפי שצוירו", "אורך בתוכנית", "עצם אחד פעם אחת",
            "מרחק מרבי בין קווי אותו עצם (מ')", "איך העצם מצויר",
        };

        internal static readonly string[] DuplicateBlocksHeaders =
        {
            "סעיף", "חלק", "קובץ", "שכבה", "בלוק", "מזהה שהוסר (Handle)", "נשאר (קובץ:מזהה)",
        };

        /// <summary>v4: the one-object-once sheet says how each object is drawn, from the measurement itself.</summary>
        internal const string ObjectsDrawnAs = " | אורך העצם לפי מספר הקווים שצוירו באותו מקום: ";

        internal const string ObjectsParagraphDrawing =
            "מתכננים משרטטים עצם אחד בכמה קווים מקבילים באותה שכבה: צינור ניקוז = ציר + 2 דפנות + 2 קווי עטיפה; קו ניתוב כפול = 2 קווים; " +
            "אבן שפה / אבן אי / אבן גן = 2 פאות; פס צבע לעתים = 2 קווי שוליים; וגם עותקים חופפים של אותו קו (גם בין קבצים). " +
            "כל קטע קו נספר חלקי מספר הקווים המקבילים של אותו סעיף שנמצאים בתוך רוחב העצם באותו מקום — כך העצם נספר פעם אחת לכל אורכו, " +
            "גם במקום שבו מספר הקווים משתנה (למשל ציר שנמשך לתוך השוחה).";

        internal const string ObjectsParagraphPlan =
            "האורך הוא אורך בתוכנית: לחלק מהקווים גובה Z שגוי (קצה אחד ב-0 והשני בגובה אחר), ולכן אורך תלת-ממדי מנפח את הכמות";

        internal const string ExplainOneObjectOnce =
            "• עצם אחד נספר פעם אחת: צינור ניקוז (5 קווים), קו כפול (2 קווים), אבן (2 פאות) ופס צבע (2 שוליים) נמדדים פעם אחת לאורכם, " +
            "באורך בתוכנית — ראו גיליון \"" + SheetObjects + "\".";

        internal static readonly string[] CrossingHeaders =
        {
            "#", "אופן השרטוט", "אורך (מטר)", "רוחב (מטר)", "שטח המעבר (מ\"ר)", "הצללה מדודה (מ\"ר)", "שטח צבוע (מ\"ר)", "עצמים", "מזהי עצמים (Handle)",
        };

        internal const string NotMeasured = "לא נמדד";
        internal const string WithoutItem = "ללא סעיף";
        internal const string NotIncludedStatus = "לא נכלל";

        public sealed record Context(DateTime Generated);

        public sealed record WriteResult(string XlsxPath, string XlsxSha256, int BoqLineCount, int MappedLineCount,
            IReadOnlyList<string> Sheets);

        /// <summary>A verified corridor measurement run (read back from its corridor_boq_measures.json) whose chapters
        /// 51.01–51.04 are priced inside this bill, so the plan files and the corridors come out as one workbook.</summary>
        public sealed record CorridorInput(CorridorBoqExport.Export Export, CorridorBoqExport.Context Context, string RunId,
            string MeasuresSha256, string RulesetSha256);

        /// <summary>NTI chapter the corridors cover only in part: stripping is measured, demolition stays an engineer decision.</summary>
        internal const string CorridorPartialChapter = "51.01";

        internal const string CorridorNotePrefix = "מהקורידורים: ";

        private static bool IsCorridorItemRow(MiniXlsx.OutRow row) =>
            row.Cells.Any(c => c.Reference == "H" + row.Index && c.Value.StartsWith(CorridorNotePrefix, StringComparison.Ordinal));

        /// <summary>Corridors outside the corridor total for a chapter: partial in its domain (earthworks for 51.01/51.02,
        /// materials for 51.03/51.04) or not measured at all.</summary>
        internal static IReadOnlyList<string> CorridorsOutside(CorridorBoqExport.Export export, string chapter)
        {
            var earth = chapter is "51.01" or "51.02";
            static string Name(string id) => id.Split(" [", 2)[0];
            static string Why(string reason) => reason.Contains("IsProcessed=false", StringComparison.Ordinal) ? "מכובה בשרטוט" : "לא נמדד";
            return export.Measures.Where(m => earth ? !m.EarthworksComplete : !m.MaterialsComplete).Select(m => $"{Name(m.CorridorId)} (מדידה חלקית)")
                .Concat(export.Skipped.Select(k => $"{Name(k.Corridor)} ({Why(k.Reason)})")).Distinct(StringComparer.Ordinal).ToList();
        }

        public static WriteResult Write(BoqEngineResult result, string path, Context context, CorridorInput? corridor = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var workbook = Build(result, context, out var mapped, corridor);
            MiniXlsx.Write(workbook, path);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var sheets = corridor == null ? SheetOrder
                : SheetOrder.Concat(new[] { CorridorBoqWorkbook.SheetCalc, CorridorBoqWorkbook.SheetMeasure, CorridorBoqWorkbook.SheetCompare,
                    CorridorBoqWorkbook.SheetCoverage }.Select(n => CorridorBoqWorkbook.CombinedPrefix + n)).ToList();
            // Every line row of the bill (chapter titles excluded): the plan-file lines and the corridor item lines.
            var lineCount = result.Lines.Count + (corridor == null ? 0 : workbook.Rows.Count(IsCorridorItemRow));
            return new WriteResult(path, hash, lineCount, mapped, sheets);
        }

        public static MiniXlsx.Workbook Build(BoqEngineResult result, Context context, out int mappedLines, CorridorInput? corridor = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(context);
            ValidateHaOverlap(result.HaOverlap);
            var rules = result.Rules;
            var wb = new MiniXlsx.Workbook
            {
                SheetName = SheetBoq, RightToLeft = true, PrintLandscapeFitToWidth = true, FreezeTopRows = 4,
            };
            var s = new Styles(wb);
            // The corridor chapters (51.01–51.04) with their own styles and supporting sheets — only when a corridor run is
            // given, so a bill of the plan files alone stays byte-identical.
            var corridorSection = corridor == null ? null
                : CorridorBoqWorkbook.BuildSheets(corridor.Export, corridor.Context, CorridorBoqWorkbook.AddStyles(wb), CorridorBoqWorkbook.CombinedPrefix);
            static string ChapterOf(string code) => code.Length >= 5 ? code[..5] : code;
            var corridorChapters = corridorSection == null ? new HashSet<string>(StringComparer.Ordinal)
                : corridorSection.Lines.Where(l => l.ChapterTitle != null).Select(l => ChapterOf(l.Code)).ToHashSet(StringComparer.Ordinal);
            // A chapter is covered by the corridors only when at least one of its lines carries a measured quantity.
            var coveredChapters = corridorSection == null ? new HashSet<string>(StringComparer.Ordinal)
                : corridorSection.Lines.Where(l => l.ChapterTitle == null && l.Quantity != null).Select(l => ChapterOf(l.Code)).ToHashSet(StringComparer.Ordinal);
            // Structural guard only: the same chapter must not be written by both the plan-file rules and the corridors. It is
            // no evidence that the HA hatch areas and the corridor layers do not overlap (see the explanation sheet).
            if (rules.Chapters.FirstOrDefault(c => corridorChapters.Contains(c.Id) && result.Lines.Any(l => l.Line.Chapter == c.Id)) is { } both)
                throw new InvalidOperationException($"Chapter {both.Id} is priced by both the plan-file rules and the corridor measurement; one bill cannot count it twice.");

            // ---------------------------------------------------------------- parameters
            var wp = NewSheet(SheetParams, new[] { 5d, 58, 10, 70, 18 }, new[] { "#", "פרמטר", "ערך", "הערה / מצב", "מקור" }, s);
            var pref = new Dictionary<string, string>(StringComparer.Ordinal);
            var r = 2;
            foreach (var p in rules.Parameters)
            {
                var row = Row(wp, r).Number("A", (double)(r - 1), s.Count);
                TextOrBlank(row, "B", p.Label, s.Text);
                row.Number("C", result.Parameters.TryGetValue(p.Id, out var v) ? v : p.Value, s.Input);
                TextOrBlank(row, "D", p.Status, s.Text);
                TextOrBlank(row, "E", p.Source, s.Text);
                pref[p.Id] = $"'{SheetParams}'!$C${r}";
                r++;
            }
            Row(wp, rules.Parameters.Count + 3).Text("B", rules.Workbook.ParamsFooter, s.BoldText);
            var road = pref.TryGetValue(rules.RoadClassParam, out var roadRef) ? roadRef : "1";

            // ---------------------------------------------------------------- signs (table 3)
            var wsg = NewSheet(SheetSigns, new[] { 9d, 10, 8, 11, 11, 11, 14, 13, 13 }, new[]
            {
                "מספר תמרור", "צורה", "כמות", "מידה עירוני (ס\"מ)", "לא עירוני חד (ס\"מ)", "לא עירוני דו (ס\"מ)",
                "מידה לפי סוג הדרך (ס\"מ)", "שטח ליחידה (מ\"ר)", "שטח כולל (מ\"ר)",
            }, s);
            r = 2;
            var sizes = rules.SignSizes;
            foreach (var num in result.SignCounts.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var count = (double)result.SignCounts[num];
                var label = result.SignLabels.TryGetValue(num, out var shown) ? shown : num;
                var row = Row(wsg, r).Text("A", label, s.Latin);
                if (sizes.Circle.TryGetValue(num, out var d))
                {
                    row.Text("B", "עגול", s.Text).Number("C", count, s.Count)
                        .Number("D", (double)d[0], s.Count).Number("E", (double)d[1], s.Count).Number("F", (double)d[2], s.Count)
                        .Formula("G", $"CHOOSE({road},{d[0]},{d[1]},{d[2]})", s.Count)
                        .Formula("H", $"ROUND(PI()*(G{r}/200)^2,4)", s.Num4);
                }
                else if (sizes.Triangle.TryGetValue(num, out var a))
                {
                    row.Text("B", "משולש", s.Text).Number("C", count, s.Count)
                        .Number("D", (double)a[0], s.Count).Number("E", (double)a[1], s.Count).Number("F", (double)a[2], s.Count)
                        .Formula("G", $"CHOOSE({road},{a[0]},{a[1]},{a[2]})", s.Count)
                        .Formula("H", $"ROUND(SQRT(3)/4*(G{r}/100)^2,4)", s.Num4);
                }
                else
                {
                    var wh = sizes.Rect[num];
                    var labels = wh.Select(x => $"{x.W}×{x.H}").ToArray();
                    var areas = wh.Select(x => (x.W * x.H / 10000.0).ToString("R", CultureInfo.InvariantCulture)).ToArray();
                    row.Text("B", "מלבן", s.Text).Number("C", count, s.Count)
                        .Text("D", labels[0], s.Latin).Text("E", labels[1], s.Latin).Text("F", labels[2], s.Latin)
                        .Formula("G", $"CHOOSE({road},\"{labels[0]}\",\"{labels[1]}\",\"{labels[2]}\")", s.Latin)
                        .Formula("H", $"CHOOSE({road},{areas[0]},{areas[1]},{areas[2]})", s.Num4);
                }
                row.Formula("I", $"ROUND(C{r}*H{r},2)", s.Num2);
                r++;
            }
            var signTotal = $"'{SheetSigns}'!$I${r}";
            Row(wsg, r).Text("A", "סה\"כ", s.BoldText).Formula("C", r > 2 ? $"SUM(C2:C{r - 1})" : "0", s.Count).Formula("I", r > 2 ? $"SUM(I2:I{r - 1})" : "0", s.BoldNum2);
            var signSource = rules.Sources.FirstOrDefault(pair => pair.Key == "signs_size").Value;
            Notice(wsg, r + 2, "I", rules.Workbook.SignsNote ?? ("מידות התמרורים לפי " +
                (string.IsNullOrWhiteSpace(signSource) ? "טבלת המידות שבכללים" : signSource) + ". סוג הדרך נקבע בגיליון \"פרמטרים\"."), s, 45);

            // ---------------------------------------------------------------- crossings (one row per physical crossing)
            var wc = NewSheet(SheetCross, new[] { 6d, 40, 11, 11, 13, 13, 13, 9, 60 }, CrossingHeaders, s);
            string? crossTotal = null;
            var bikeOnly = 0;
            var cw = rules.Crosswalk;
            var crossingPart = result.Parts.FirstOrDefault(p => p.Part.Kind == "crosswalk_geo");
            if (crossingPart != null && cw != null)
            {
                var cr = 2;
                var k = 1;
                var fill = pref[cw.FillParam];
                var crossings = crossingPart.Crossings ?? Array.Empty<BoqCrossing>();
                var ordered = crossings.OrderBy(x => x.WidthSource, StringComparer.Ordinal).ThenByDescending(x => Math.Round(x.Length ?? 0.0, 6)).ToList();
                foreach (var c in ordered)
                {
                    var kindText = c.Kind;
                    if (c.OutlineWidth is { } outline)
                        kindText += $" · מתאר צהוב {F2(outline)} מטר, רצועת אופניים {F2(c.BikeBandWidth ?? 0.0)} מטר נספרת ב-812";
                    if (c.Fallback > 0) kindText += " · שטח ההצללה חושב מהפוליליין הסגור שבגבולה (שטח ההצללה לא נקרא מהשרטוט)";
                    if (c.MissingHatches > 0) kindText += " · שטח ההצללה לא נמדד — ראו \"חסרים\"";
                    var row = Row(wc, cr).Number("A", (double)k, s.Count).Text("B", kindText, s.Text);
                    var hatched = c.WidthSource is "hatch" or "hatch-assembly";
                    if (hatched)
                    {
                        var style = c.MissingHatches > 0 ? s.WarnNum2 : s.Num2;
                        if (c.MissingHatches > 0 && (c.HatchM2 ?? 0.0) == 0.0) row.Text("F", NotMeasured, c.MissingHatches > 0 ? s.WarnText : s.Text);
                        else row.Number("F", BoqRulesEngine.PythonRound(c.HatchM2 ?? 0.0, 4), style);
                    }
                    if (c.WidthSource == "hatch")
                        row.Formula("G", $"ROUND(N(F{cr}),3)", s.Num2);
                    else
                    {
                        row.Number("C", BoqRulesEngine.PythonRound(c.Length ?? 0.0, 3), s.Num2);
                        if (c.WidthSource == "measured") row.Number("D", BoqRulesEngine.PythonRound(c.Width ?? 0.0, 3), s.Num2);
                        else row.Formula("D", pref[cw.StandardWidthParam], s.InputNum2);
                        row.Formula("E", $"ROUND(C{cr}*D{cr},3)", s.Num2)
                            .Formula("G", c.WidthSource == "hatch-assembly" ? $"ROUND(E{cr}*{fill}+N(F{cr}),3)" : $"ROUND(E{cr}*{fill},3)", s.Num2);
                    }
                    row.Number("H", (double)c.Handles.Count, s.Count)
                        .Text("I", string.Join(", ", c.Handles.Take(12)) + (c.Handles.Count > 12 ? " …" : ""), s.Latin);
                    cr++;
                    k++;
                }
                Row(wc, cr).Text("B", "סה\"כ", s.BoldText)
                    .Formula("E", cr > 2 ? $"SUM(E2:E{cr - 1})" : "0", s.BoldNum2)
                    .Formula("F", cr > 2 ? $"SUM(F2:F{cr - 1})" : "0", s.BoldNum2)
                    .Formula("G", cr > 2 ? $"SUM(G2:G{cr - 1})" : "0", s.BoldNum2)
                    .Formula("H", cr > 2 ? $"SUM(H2:H{cr - 1})" : "0", s.Count);
                crossTotal = $"'{SheetCross}'!$G${cr}";
                bikeOnly = crossings.Count(c => c.Kind.StartsWith(cw.LabelBikeOnly, StringComparison.Ordinal));
                var crossingNote = cw.Note;
                if (string.IsNullOrWhiteSpace(crossingNote))
                {
                    crossingNote = "מעבר אחד = קבוצת עצמים בשכבות מעבר החציה במרחק עד " + F(cw.LinkDistance) + " מטר. קווי שוליים נותנים אורך × רוחב נמדד; " +
                        "קו לבן או הצללה על מעבר כזה הם אותו מעבר ולא נספרים שוב (\"לא נכלל\"). מעבר עם הצללה וקו מקווקו — הקו המקווקו × רוחב תקני × חלק צבוע, " +
                        "ושטח ההצללה נוסף לה; הצללה בלבד — שטח ההצללה. קו לבן בלבד — אורך × רוחב תקני. שטח צבוע = שטח המעבר × חלק צבוע (פרמטרים) + הצללה מדודה.";
                    if (!string.IsNullOrWhiteSpace(result.Input.GeometryReader))
                        crossingNote += " גאומטריה לסיווג בלבד: " + result.Input.GeometryReader + ".";
                }
                var fallbackRows = ordered.Select((c, index) => (Crossing: c, Number: index + 1))
                    .Where(x => x.Crossing.Fallback > 0).Select(x => x.Number).ToList();
                // Upgrade the old project template only; custom engineer prose is never replaced.
                var legacySource = " הגאומטריה לסיווג בלבד: קריאה ישירה של אותו קובץ SM, בלי לשנות אותו.";
                var upgradedCrossingNote = crossingNote.EndsWith(legacySource, StringComparison.Ordinal);
                if (upgradedCrossingNote)
                    crossingNote = crossingNote[..^legacySource.Length]
                        .Replace(" (10 מעברים)", "", StringComparison.Ordinal)
                        .Replace("קו לבן בלבד —", "קו לבן בלבד / קו צהוב בודד —", StringComparison.Ordinal)
                        .Replace(" \"לא נמדד\" = הצללה ששטחה לא הוחזר מהשרטוט; השטח לא ידוע (לא אפס) ואינו בסה\"כ — ראו \"חסרים\".", "", StringComparison.Ordinal);
                if (fallbackRows.Count > 0)
                    crossingNote += " במעברים #" + string.Join(", #", fallbackRows) + " שטח ההצללה חושב מהפוליליין הסגור שבגבולה.";
                if (crossings.Any(c => c.MissingHatches > 0))
                    crossingNote += " \"לא נמדד\" = הצללה שלא ניתן היה לקרוא את שטחה; השטח לא ידוע (לא אפס) ואינו בסה\"כ — ראו \"חסרים\".";
                if (upgradedCrossingNote)
                    crossingNote += " " + (result.Input.CrossingSourceNote ??
                        (result.Input.GeometryReader.StartsWith("ACadSharp", StringComparison.Ordinal)
                            ? "הקיבוץ למעברים, אורך המעברים והרוחב הנמדד מחושבים מקריאה ישירה של אותו קובץ SM (בלי לשנות אותו; טביעת הקריאה בגיליון \"מקורות\") — לא ממדידת Civil; שטחי ההצללות (עמודה F) — מרשומות המדידה של Civil"
                            : "מקור הקיבוץ, האורך והרוחב: " + (string.IsNullOrWhiteSpace(result.Input.GeometryReader) ? "לא נמסר" : result.Input.GeometryReader) + "; שטחי ההצללות — מרשומות המדידה")) +
                        (fallbackRows.Count > 0 ? $" ({fallbackRows.Count} מהן מהפוליליין הסגור שבגבולן)." : ".");
                else if (!string.IsNullOrWhiteSpace(result.Input.GeometryReader) && !crossingNote.Contains(result.Input.GeometryReader, StringComparison.Ordinal))
                    crossingNote += " מקור גאומטריית הקיבוץ, האורך והרוחב: " + result.Input.GeometryReader +
                        "; שטחי ההצללות מרשומות המדידה, למעט התאוששות הגבול המצוינת לכל מעבר.";
                Notice(wc, cr + 2, "I", crossingNote, s, 90);
            }

            // ---------------------------------------------------------------- calculation detail
            var wd = NewSheet(SheetDetail, new[] { 16d, 44, 8, 40, 9, 13, 8, 26, 13, 44 }, new[]
            {
                "סעיף", "חלק", "קובץ", "שכבות / בלוקים", "עצמים", "כמות מדודה", "יח'", "מקדם", "כמות לסעיף", "הערות",
            }, s);
            var plabel = rules.Parameters.ToDictionary(p => p.Id, p => p.Label, StringComparer.Ordinal);
            var lineRows = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var linesById = result.Lines.ToDictionary(l => l.Line.Id, StringComparer.Ordinal);
            var incompleteDependencySources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var signTexts = rules.SignTexts;
            r = 2;
            foreach (var P in result.Parts)
            {
                var kind = P.Part.Kind;
                var layers = P.Layers.Count > 0 ? string.Join(", ", P.Layers.Keys) : "—";
                if (kind is "count_x" or "count" && P.Signs.Count > 0)
                    layers += " | בלוקים: " + string.Join(", ", P.Signs.MostCommon().Select(pair => $"{pair.Key}×{pair.Value}"));
                var notes = new List<string>(P.Notes);
                // Dependencies read earlier lines. Propagate disclosure, never missing ownership or quantities.
                var partialDependency = false;
                if (kind is "count_of" or "line_sum")
                {
                    var sources = new HashSet<string>(StringComparer.Ordinal);
                    var referencedIds = kind == "line_sum" ? P.Part.Lines : new[] { P.Part.Of! };
                    foreach (var sourceId in referencedIds)
                    {
                        if (linesById.TryGetValue(sourceId, out var source) && source.Missing > 0) sources.Add(sourceId);
                        // line_sum may include a count_of line, but never another line_sum (ruleset validation).
                        if (kind == "line_sum" && incompleteDependencySources.TryGetValue(sourceId, out var inherited))
                            sources.UnionWith(inherited);
                    }
                    if (sources.Count > 0)
                    {
                        if (!incompleteDependencySources.TryGetValue(P.LineId, out var lineSources))
                            incompleteDependencySources[P.LineId] = lineSources = new HashSet<string>(StringComparer.Ordinal);
                        lineSources.UnionWith(sources);
                        notes.Add(DependencySourceDisclosure(sources, result));
                        partialDependency = true;
                    }
                }
                if (PartConfirmation(P, result) is { } partConfirmation) notes.Add(Confirmation(partConfirmation));
                if (P.Part.Alt is { } alt && P.Part.Param != null)
                {
                    var here = result.Parameters.TryGetValue(P.Part.Param, out var value) ? value : 1.0;
                    notes.Add($"{alt.Label}: {F2c(P.Base * alt.Value)} מ\"ר (כאן {F2c(P.Base * here)} מ\"ר)");
                }
                var twins = result.Dedup.Where(d => d.LineId == P.LineId && d.PartLabel == P.Part.Label).ToList();
                if (P.Objects == 0 && twins.Count > 0)
                    notes.Add($"{twins.Count} בלוקים באותן נקודות כמו בקובץ " +
                        string.Join("/", twins.Select(d => d.Kept.Split(':')[0]).Distinct().OrderBy(x => x, StringComparer.Ordinal)) +
                        " — נספרו שם פעם אחת (גיליון \"עצם אחד פעם אחת\")");
                if (kind == "sign_area")
                    foreach (var layerNote in signTexts.LayerNotes)
                        if (P.Layers[layerNote.Layer] > 0)
                            notes.Add(layerNote.Note.Replace("{n}", P.Layers[layerNote.Layer].ToString(CultureInfo.InvariantCulture))
                                .Replace("{layer}", layerNote.Layer));
                if (P.Missing > 0) notes.Add(CountHe(P.Missing, "עצם אחד שלא נמדד", "עצמים שלא נמדדו") + " — גיליון \"חסרים\"");
                var row = Row(wd, r).Text("A", P.Line.HasItem ? P.Line.Ref() : WithoutItem, s.Latin).Text("B", DisplaySignRangeLabel(P), s.Text)
                    .Text("C", P.Srcs.Count > 0 ? string.Join(", ", P.Srcs.Keys) : "—", s.Latin);
                if (kind is "line_sum" or "count_of") row.Text("E", "—", s.Text);
                else row.Number("E", (double)P.Objects, s.Count);
                switch (kind)
                {
                    case "crosswalk_geo":
                        row.Text("D", layers, s.Latin)
                            .Number("F", (double)(P.Crossings?.Count ?? 0), s.Num2).Text("G", "מעברים", s.Text)
                            .Text("H", "שטח צבוע לפי גיליון \"מעברי חציה\"", s.Text)
                            .Formula("I", crossTotal != null ? $"ROUND({crossTotal},2)" : "0", s.Num2);
                        if (bikeOnly > 0)
                            notes.Add($"כולל {CountHe(bikeOnly, "מעבר אופניים בלבד אחד", "מעברי אופניים בלבד")} (0 מ\"ר — נספר ב-812)");
                        notes.Add("עצמים = העצמים שנכנסו לשטח; ברשימת המעברים מופיעים גם עצמים של אותו מעבר שלא נספרו שוב (גיליון \"לא נכלל\")");
                        break;
                    case "sign_area":
                        row.Text("D", layers, s.Latin)
                            .Number("F", (double)P.Objects, s.Num2).Text("G", Unit(kind), s.Text)
                            .Text("H", "שטח לפי טבלה 3 (גיליון \"תמרורים\")", s.Text).Formula("I", signTotal, s.Num2);
                        break;
                    case "sign_block_area":
                    {
                        var (rangeFrom, rangeTo) = P.Part.SignRange ?? (0, 0);
                        notes.Insert(0, SignRangeText(result.GuideHits.Count == 0 ? signTexts.DetailAbsent : signTexts.DetailFound, rangeFrom, rangeTo, result));
                        row.Text("D", layers, s.Latin).Number("F", 0.0, s.Num2).Text("G", Unit(kind), s.Text)
                            .Text("H", SignRangeText(P.Objects == 0 ? signTexts.DetailFactorAbsent : signTexts.DetailFactorFound, rangeFrom, rangeTo, result), s.Text)
                            .Number("I", 0.0, s.Num2);
                        break;
                    }
                    case "count_of":
                    {
                        var of = P.Part.Of ?? "";
                        var countedRows = lineRows.TryGetValue(of, out var ofRows) ? ofRows : new List<int>();
                        row.Text("D", rules.Workbook.CountOfDetail.Replace("{ref}", result.Ref(of)), s.Text)
                            // count_of is the sum of every referenced part's base, not its factored quantity.
                            .Formula("F", countedRows.Count > 0 ? string.Join("+", countedRows.Select(x => $"F{x}")) : "0", s.Num2).Text("G", Unit(kind), s.Text)
                            .Text("H", "× 1", s.Text).Formula("I", $"ROUND(F{r},2)", s.Num2);
                        break;
                    }
                    case "line_sum":
                    {
                        // The sum of the referenced lines' detail cells (each already rounded to 0.01), written above.
                        var refs = P.Part.Lines.SelectMany(id => lineRows.TryGetValue(id, out var found) ? found : new List<int>()).ToList();
                        var sumUnit = P.Part.Lines.Select(id => result.Parts.FirstOrDefault(other => other.LineId == id)?.Part.Kind)
                            .FirstOrDefault(sumKind => sumKind != null) ?? "length";
                        row.Text("D", "סכום סעיפים " + string.Join(" + ", P.Part.Lines.Select(result.Ref)), s.Text)
                            .Formula("F", refs.Count > 0 ? string.Join("+", refs.Select(x => $"I{x}")) : "0", s.Num2)
                            .Text("G", Unit(sumUnit), s.Text).Text("H", "× 1", s.Text).Formula("I", $"ROUND(F{r},2)", s.Num2);
                        if (result.Controls.TryGetValue(P.LineId, out var control))
                            notes.Add($"{control.Label}: {control.Objects} קווים, {BoqRulesEngine.F1(control.Length)} מ' (עצם אחד פעם אחת) — להשוואה בלבד, לא נכנס לכמות");
                        break;
                    }
                    default:
                        row.Text("D", layers, s.Latin).Number("F", BoqRulesEngine.PythonRound(P.Base, 4), s.Num2).Text("G", Unit(kind), s.Text);
                        if (kind == "count_x" && signTexts.PolesParam != null && P.Part.Param == signTexts.PolesParam)
                            notes.Add(PolesText(signTexts.PolesDetailNote, result));
                        if (P.Part.Param != null)
                            row.Text("H", "× " + plabel[P.Part.Param].Split(':')[0], s.Text).Formula("I", $"ROUND(F{r}*{pref[P.Part.Param]},2)", s.Num2);
                        else
                            row.Text("H", "× 1", s.Text).Formula("I", $"ROUND(F{r},2)", s.Num2);
                        break;
                }
                var notesStyle = P.Missing > 0 || partialDependency ? s.WarnText : s.Text;
                if (notes.Count > 0) row.Text("J", DisplayNumericRanges(string.Join(" | ", notes)), notesStyle);
                else row.Blank("J", notesStyle);
                if (!lineRows.TryGetValue(P.LineId, out var rows)) lineRows[P.LineId] = rows = new List<int>();
                rows.Add(r);
                r++;
            }
            wd.AutoFilterRange = $"A1:J{Math.Max(1, r - 1)}";

            // ---------------------------------------------------------------- one physical object once (rules 2.1)
            var wk = ObjectsSheet(result, s, result.HaOverlap);

            // ---------------------------------------------------------------- bill of quantities (NTI format)
            var title = rules.Workbook.Title.Replace("{project}", rules.Project)
                .Replace("{date}", context.Generated.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
            Row(wb, 1).Text("A", title, s.Title);
            var subtitle = rules.Workbook.Subtitle;
            if (!string.IsNullOrWhiteSpace(rules.Pricebook.Name))
                subtitle = subtitle.Replace("{pricebook}", $"{rules.Pricebook.Name} {rules.Pricebook.Edition}".Trim());
            Row(wb, 2).Text("A", subtitle, s.Sub);
            var head = Row(wb, 4);
            var headers = new[] { "מספר", "תאור", "יח' מידה", "כמות", "מחיר", "סה\"כ", "מצב", "הערות" };
            for (var i = 0; i < headers.Length; i++) head.Text(Col(i + 1), headers[i], s.Head);
            foreach (var (col, width) in new[] { (1, 12d), (2, 62d), (3, 8d), (4, 13d), (5, 10d), (6, 14d), (7, 13d), (8, 58d) })
                wb.ColumnWidths.Add((col, width));
            var rowIndex = 5;
            int? firstLineRow = null, lastLineRow = null;
            mappedLines = 0;
            if (corridorSection != null)
            {
                foreach (var cl in corridorSection.Lines)
                {
                    var crow = Row(wb, rowIndex);
                    if (cl.ChapterTitle != null)
                    {
                        crow.Text("A", cl.Code, s.ChapterLatin).Text("B", cl.ChapterTitle, s.Chapter);
                        foreach (var col in new[] { "C", "D", "E", "F", "G", "H" }) crow.Blank(col, s.Chapter);
                        rowIndex++;
                        continue;
                    }
                    crow.Text("A", cl.Code, s.Latin).Text("B", cl.Description, s.Text).Text("C", cl.Unit, s.Text);
                    if (cl.Quantity == null)
                        crow.Text("D", cl.NotMeasured ? NotMeasured : "לא אומת", s.Warn).Number("E", cl.Price ?? 0m, s.Num2).Text("F", "—", s.Text);
                    else
                        crow.Formula("D", cl.Quantity, s.Num2).Number("E", cl.Price ?? 0m, s.Num2)
                            .Formula("F", $"IF(E{rowIndex}=\"\",\"\",ROUND(D{rowIndex}*E{rowIndex},2))", s.Money);
                    var cStatus = cl.Quantity == null ? (cl.NotMeasured ? NotMeasured : "לא אומת") : cl.Status;
                    var cStyle = cl.Quantity == null ? s.Warn
                        : cStatus.StartsWith("לאישור", StringComparison.Ordinal) || cStatus == CorridorBoqWorkbook.Assumption ||
                          cStatus == CorridorBoqWorkbook.ZeroInRun ? s.Assume : s.Ok;
                    crow.Text("G", cStatus, cStyle)
                        .Text("H", CorridorNotePrefix + cl.Note, s.Text);
                    mappedLines++;
                    firstLineRow ??= rowIndex;
                    lastLineRow = rowIndex;
                    rowIndex++;
                }
            }
            foreach (var chapter in rules.Chapters)
            {
                var chapterLines = result.Lines.Where(l => l.Line.Chapter == chapter.Id).ToList();
                if (chapterLines.Count == 0) continue;
                var chRow = Row(wb, rowIndex);
                if (chapter.Id.Length > 0 && char.IsDigit(chapter.Id[0])) chRow.Text("A", chapter.Id + ".0000", s.ChapterLatin);
                else chRow.Blank("A", s.ChapterLatin);
                chRow.Text("B", chapter.Title, s.Chapter);
                foreach (var col in new[] { "C", "D", "E", "F", "G", "H" }) chRow.Blank(col, s.Chapter);
                rowIndex++;
                foreach (var lr in chapterLines)
                {
                    var L = lr.Line;
                    var drows = lineRows.TryGetValue(L.Id, out var dr) ? dr : new List<int>();
                    var missing = lr.Missing;
                    var notes = new List<string>();
                    var partialDependency = incompleteDependencySources.TryGetValue(L.Id, out var incompleteSources);
                    if (partialDependency) notes.Add(DependencySourceDisclosure(incompleteSources!, result));
                    if (L.Confirm != null) notes.Add(Confirmation(L.Confirm));
                    if (L.Assumption != null) notes.Add(AssumptionNote(L.Assumption));
                    foreach (var P in lr.Parts.Where(p => PartConfirmation(p, result) != null)) notes.Add(PartBrief(P) ?? $"{P.Part.Label}: {PartConfirmation(P, result)}");
                    foreach (var P in lr.Parts.Where(p => p.Part.Assumption != null && PartConfirmation(p, result) == null))
                        notes.Add($"{P.Part.Label}: {AssumptionNote(P.Part.Assumption!)}");
                    if (missing > 0) notes.Add(CountHe(missing, "עצם אחד לא נמדד", "עצמים לא נמדדו") + " (גיליון \"חסרים\") — הכמות חלקית");
                    if (lr.Parts.FirstOrDefault(p => p.Part.Kind == "sign_block_area" && p.Objects == 0) is { } guide)
                    {
                        var (rangeFrom, rangeTo) = guide.Part.SignRange ?? (0, 0);
                        notes.Insert(0, SignRangeText(result.GuideHits.Count == 0 ? signTexts.BoqAbsent : signTexts.BoqFound, rangeFrom, rangeTo, result));
                    }
                    if (!string.IsNullOrEmpty(L.Review)) notes.Add("לבדיקה: " + L.Review);
                    if (!string.IsNullOrEmpty(L.DrawingNote)) notes.Add(L.DrawingNote!);
                    if (signTexts.PolesParam != null && lr.Parts.Any(p => p.Part.Kind == "count_x" && p.Part.Param == signTexts.PolesParam))
                        notes.Add(PolesText(signTexts.PolesBoqNote, result));
                    // A record without plan geometry is counted as drawn: the one-object-once rule did not run for it.
                    var noGeometry = lr.Parts.Sum(p => p.NoGeometry);
                    if (noGeometry > 0) notes.Add($"לבדיקה: {noGeometry} קווים ללא גאומטריה בתוכנית נספרו כפי שצוירו — לא נבדק עצם אחד פעם אחת, ייתכן כפל");
                    var held = result.HeldLongObjects(L);
                    var additionalHeld = result.Input.Records.Select((record, index) => (Record: record, Index: index))
                        .Where(x => result.Buckets[x.Index] == BoqBucket.Excluded &&
                            (result.Reasons[x.Index]?.StartsWith("לבדיקה", StringComparison.Ordinal) ?? false) &&
                            result.LongObjects.ContainsKey((x.Record.Src, x.Record.Handle)) &&
                            L.HeldNoteLayers.Contains(x.Record.Layer)).ToList();
                    if (additionalHeld.Count > 0)
                        notes.Add("לבדיקה: " + CountHe(additionalHeld.Count, "עצם אחד ארוך", "עצמים ארוכים") +
                            $" ({additionalHeld.Sum(x => x.Record.Qty).ToString("#,##0", CultureInfo.InvariantCulture)} מ') בשכבת " +
                            string.Join(", ", additionalHeld.Select(x => x.Record.Layer).Distinct().OrderBy(x => x, StringComparer.Ordinal)) +
                            " לא נכללו — אם הם קווי נתיב אופניים, מקומם בסעיף זה (ראו \"לא נכלל\")");
                    if (held is { } h)
                        notes.Add("לבדיקה: " + CountHe(h.Count, "עצם אחד ארוך", "עצמים ארוכים") + $" מ-30 מטר ({h.Length.ToString("#,0", CultureInfo.InvariantCulture)} מטר) " +
                                  $"בשכבות {string.Join(", ", h.Layers)} לא נכללו — גיליון \"{SheetExcluded}\"");
                    var curbRows = result.CurbUnique.Where(c => L.Parts.Any(p => p.Layers.Contains(c.Key))).Select(c => c.Value).ToList();
                    if (curbRows.Count > 0)
                    {
                        var curbCount = curbRows.Sum(c => c.Count);
                        var curbSource = result.CurbSources.Count == 1 ? " בקובץ " + result.CurbSources[0]
                            : " בקבצים " + string.Join(" / ", result.CurbSources.OrderBy(x => x, StringComparer.Ordinal));
                        notes.Add("לבדיקה: " + CountHe(curbCount, "קטע אבן אחד", "קטעי אבן") + curbSource +
                            $" ({F2c(curbRows.Sum(c => c.Length))} מ') " +
                            (curbCount == 1 ? "שאינו זהה לעצם ב-GM לא נכלל" : "שאינם זהים לעצם ב-GM לא נכללו") + " (גיליון \"לא נכלל\")");
                    }
                    var haNotes = HaLineNotes(L, result, result.HaOverlap);
                    notes.AddRange(haNotes);
                    // Rules 2.8 (BOQ-N1): approved-footprint placements — the unproven ones are never counted silently.
                    var footprintReview = result.FootprintReview.Where(d => d.LineId == L.Id).ToList();
                    if (footprintReview.Count > 0)
                        notes.Add("לבדיקה: " + CountHe(footprintReview.Count, "בלוק אחד", "בלוקים") + " באותה נקודה כמו בלוק שנספר, בלי גוף נפרד מוכח (" +
                            string.Join("; ", footprintReview.Select(d => d.Reason).Distinct().OrderBy(x => x, StringComparer.Ordinal)) +
                            ") — נספרו פעם אחת (גיליון \"לא נכלל\")");
                    var footprintDistinct = result.Distinct.Where(d => d.LineId == L.Id).ToList();
                    if (footprintDistinct.Count > 0)
                        notes.Add(CountHe(footprintDistinct.Count, "מתקן אחד נוסף", "מתקנים נוספים") + " באותה נקודה כמו מתקן שנספר, בסיבוב אחר, " +
                            $"עם גוף מתקן נפרד ({footprintDistinct.Min(d => d.BodyGapM).ToString("F2", CultureInfo.InvariantCulture)}–" +
                            $"{footprintDistinct.Max(d => d.BodyGapM).ToString("F2", CultureInfo.InvariantCulture)} מ' בין הגופים) — " +
                            "נספרו כמתקנים נפרדים (זוגות גב-אל-גב; גוף המתקן לפי ההגדרה המאושרת)");
                    notes = notes.Where(n => n.StartsWith("לבדיקה", StringComparison.Ordinal))
                        .Concat(notes.Where(n => !n.StartsWith("לבדיקה", StringComparison.Ordinal))).ToList();
                    notes.Add(lr.Parts.Count > 2 ? "שיטה ופירוט: גיליון \"פירוט חישוב\"" : "פירוט: " + string.Join("; ", lr.Parts.Select(p => p.Part.Label)));

                    var row = Row(wb, rowIndex);
                    bool mapped;
                    string unit;
                    if (L.ItemUrban != null)
                    {
                        rules.Pricebook.Items.TryGetValue(L.ItemUrban, out var u);
                        rules.Pricebook.Items.TryGetValue(L.ItemRural ?? "", out var rr);
                        var du = u != null ? rules.Pricebook.Describe(u) : MissingItem;
                        var drr = rr != null ? rules.Pricebook.Describe(rr) : MissingItem;
                        row.Formula("A", $"IF({road}=1,\"{L.ItemUrban}\",\"{L.ItemRural}\")", s.Latin)
                            .Formula("B", $"IF({road}=1,{FormulaText(du)},{FormulaText(drr)})", s.Text);
                        unit = u?.Unit ?? L.Unit;
                        row.Text("C", unit, s.Text).Formula("D", Quantity(drows), s.Num2);
                        if (u?.BasePrice is { } pu && rr?.BasePrice is { } pr)
                            row.Formula("E", $"IF({road}=1,{pu.ToString(CultureInfo.InvariantCulture)},{pr.ToString(CultureInfo.InvariantCulture)})", s.Num2);
                        mapped = true;
                    }
                    else if (!string.IsNullOrEmpty(L.Item))
                    {
                        rules.Pricebook.Items.TryGetValue(L.Item!, out var it);
                        unit = it?.Unit ?? L.Unit;
                        row.Text("A", L.Item!, s.Latin).Text("B", it != null ? rules.Pricebook.Describe(it) : MissingItem, s.Text).Text("C", unit, s.Text)
                            .Formula("D", Quantity(drows), s.Num2);
                        if (it?.BasePrice is { } price) row.Number("E", price, s.Num2);
                        mapped = true;
                    }
                    else
                    {
                        unit = L.Unit;
                        var name = !string.IsNullOrEmpty(L.Label) ? L.Label! : string.Join(" / ", lr.Parts.Select(p => p.Part.Label));
                        row.Blank("A", s.Latin)
                            .Text("B", name + " — סעיף מחירון לבחירה", s.Text)
                            .Text("C", unit, s.Text).Formula("D", Quantity(drows), s.Num2);
                        mapped = false;
                    }
                    // Every row carries the total formula: a price typed into a "סעיף לבחירה" row flows into the grand total.
                    row.Formula("F", $"IF(E{rowIndex}=\"\",\"\",ROUND(D{rowIndex}*E{rowIndex},2))", s.Money);
                    var confirm = L.Confirm != null || lr.Parts.Any(p => PartConfirmation(p, result) != null);
                    var assumed = L.Assumption != null || lr.Parts.Any(p => p.Part.Assumption != null);
                    var status = !mapped ? "סעיף לבחירה" : confirm ? "לאישור" : assumed ? EstimateAssumption : "מוכן";
                    if (partialDependency && status == "מוכן") status = "חלקי";
                    else if (missing > 0 || partialDependency) status += " · חלקי";
                    if (held != null || noGeometry > 0 || !string.IsNullOrEmpty(L.Review) || curbRows.Count > 0 || additionalHeld.Count > 0 || haNotes.Count > 0 ||
                        footprintReview.Count > 0) status += " · לבדיקה";
                    var statusStyle = missing > 0 || partialDependency ? s.Warn
                        : status == "מוכן" ? s.Ok
                        : status.StartsWith("לאישור", StringComparison.Ordinal) || status.StartsWith(EstimateAssumption, StringComparison.Ordinal) ? s.Assume
                        : status.StartsWith("סעיף", StringComparison.Ordinal) ? s.Choose : s.Warn;
                    row.Text("G", status, statusStyle).Text("H", DisplayNumericRanges(string.Join("\n", notes)), s.Text);
                    if (mapped) mappedLines++;
                    firstLineRow ??= rowIndex;
                    lastLineRow = rowIndex;
                    rowIndex++;
                }
            }
            // Chapters of the example that this draft does not measure — named, with the reason, no quantity and no price.
            rowIndex++;
            // A chapter the corridors price leaves this list only when every corridor was measured completely for it; otherwise
            // its row names the corridors outside the total. 51.01 stays for demolition (only stripping is measured).
            var notIncluded = rules.NotIncluded.Where(ni => !coveredChapters.Contains(ni.Id) || ni.Id == CorridorPartialChapter ||
                CorridorsOutside(corridor!.Export, ni.Id).Count > 0).ToList();
            if (notIncluded.Count > 0)
            {
                var niHead = Row(wb, rowIndex).Blank("A", s.ChapterLatin).Text("B", rules.Workbook.NotIncludedTitle, s.Chapter);
                foreach (var col in new[] { "C", "D", "E", "F", "G", "H" }) niHead.Blank(col, s.Chapter);
                rowIndex++;
                foreach (var ni in notIncluded)
                {
                    var niRow = Row(wb, rowIndex).Text("A", ni.Id + ".0000", s.Latin);
                    var (niTitle, niReason) = coveredChapters.Contains(ni.Id) ? CorridorNotIncluded(ni, CorridorsOutside(corridor!.Export, ni.Id))
                        : (ni.Title, ni.Reason);
                    TextOrBlank(niRow, "B", niTitle, s.Text);
                    niRow.Text("G", NotIncludedStatus, s.Choose);
                    TextOrBlank(niRow, "H", niReason, s.Text);
                    rowIndex++;
                }
                rowIndex++;
            }
            if (rules.ExampleItems is { } exampleItems)
            {
                var exampleHead = Row(wb, rowIndex++).Text("B", exampleItems.Title, s.Chapter);
                foreach (var col in new[] { "A", "C", "D", "E", "F", "G", "H" }) exampleHead.Blank(col, s.Chapter);
                foreach (var item in exampleItems.Rows)
                {
                    Row(wb, rowIndex++).Text("A", item.Chapter, s.Latin).Text("B", item.Items, s.Text)
                        .Text("G", item.Status, s.Choose).Text("H", item.Action, s.Text);
                }
                rowIndex++;
            }
            Row(wb, rowIndex).Text("B", rules.Workbook.TotalLabel, s.BoldText)
                .Formula("F", firstLineRow is { } first && lastLineRow is { } last ? $"SUM(F{first}:F{last})" : "0", s.BoldMoney);
            rowIndex += 2;
            foreach (var text in rules.Workbook.Legend)
            {
                Notice(wb, rowIndex, "H", text, s, 30);
                rowIndex++;
            }

            // ---------------------------------------------------------------- excluded / unclassified / missing
            var we = NewSheet(SheetExcluded, new[] { 70d, 7, 28, 8, 9, 14 }, new[] { "סיבה", "קובץ", "שכבה", "יח'", "עצמים", "כמות" }, s);
            var excluded = result.ExcludedGroups();
            r = 2;
            foreach (var g in excluded)
            {
                Row(we, r).Text("A", DisplayNumericRanges(g.Reason), s.Text).Text("B", g.Src, s.Latin).Text("C", g.Layer, s.Latin)
                    .Text("D", MeasureUnit(g.Kind), s.Text).Number("E", (double)g.Count, s.Count)
                    .Number("F", BoqRulesEngine.PythonRound(g.Quantity, 2) + 0.0, s.Num2);
                r++;
            }
            we.AutoFilterRange = $"A1:F{excluded.Count + 1}";

            var wu = NewSheet(SheetUnclassified, new[] { 7d, 30, 7, 9, 14, 14, 50, 60 },
                new[] { "קובץ", "שכבה", "יח'", "עצמים", "כמות (באורך: בתוכנית)", "אורך כפי שצויר (מ')", "סוגי עצמים / בלוקים", "הערה" }, s);
            var unclassified = result.UnclassifiedGroups();
            r = 2;
            foreach (var g in unclassified)
            {
                var row = Row(wu, r).Text("A", g.Src, s.Latin).Text("B", g.Layer, s.Latin).Text("C", MeasureUnit(g.Kind), s.Text)
                    .Number("D", (double)g.Count, s.Count).Number("E", BoqRulesEngine.PythonRound(g.Kind == "length" ? g.Plan ?? g.Quantity : g.Quantity, 2) + 0.0, s.Num2)
                    .Text("G", string.Join(", ", g.Types.Take(5).Select(t => $"{t.Key}×{t.Value}")), s.Latin);
                if (g.Kind == "length") row.Number("F", BoqRulesEngine.PythonRound(g.Quantity, 2), s.Num2);
                var unclassifiedNote = UnclassifiedNote(result, g);
                if (g.Kind == "length" && g.ZCount > 0)
                    unclassifiedNote += (unclassifiedNote.Length > 0 ? " | " : "") +
                        $"מתוך {F2c(g.Quantity)} מ' כפי שצויר, {F2c(g.ZExcess)} מ' נובעים מ-" + CountHe(g.ZCount, "עצם אחד", "עצמים") +
                        $" עם גובה Z שגוי (הפרש של יותר ממטר לעצם; למשל {g.ZExampleHandle}); אורך בתוכנית {F2c(g.Plan ?? g.Quantity)} מ' — שאר ההפרש מעצמים עם הפרש קטן";
                TextOrBlank(row, "H", unclassifiedNote, s.Text);
                r++;
            }
            wu.AutoFilterRange = $"A1:H{unclassified.Count + 1}";

            var wm = NewSheet(SheetMissing, new[] { 7d, 7, 26, 12, 50, 50 }, new[] { "סעיף", "קובץ", "שכבה", "מזהה עצם (Handle)", "מה חסר", "מה לעשות" }, s);
            r = 2;
            foreach (var m in ExpandedMissingRows(result))
            {
                var what = m.Src == "HA" && m.What == "הצללה ששטחה לא הוחזר — השטח לא ידוע (לא אפס)"
                    ? "הצללה שלא ניתן היה לקרוא את שטחה מהשרטוט — השטח לא ידוע (לא אפס)" : m.What;
                var action = m.Src == "HA" && m.Action == "לפתוח את המקור ב-Civil: LIST / Properties ובדיקת לולאות הגבול"
                    ? "לבדוק את ההצללה בשרטוט (מאפייני ההצללה / גבול סגור)" : m.Action;
                Row(wm, r).Text("A", result.Ref(m.LineId), s.Latin).Text("B", m.Src, s.Latin).Text("C", m.Layer, s.Latin)
                    .Text("D", m.Handle, s.Latin).Text("E", what, s.Text).Text("F", action, s.Text);
                r++;
            }

            // ---------------------------------------------------------------- explanation and sources
            var wx = new MiniXlsx.Worksheet { SheetName = SheetExplain, RightToLeft = true, PrintLandscapeFitToWidth = true };
            wx.ColumnWidths.Add((1, 125));
            r = 1;
            foreach (var (text, bold) in Explanation(result))
            {
                // An empty line stays an empty row (the reference's '' cell is not kept by Excel).
                if (text.Length > 0) Row(wx, r).Text("A", text, bold ? s.H12 : s.T11);
                r++;
            }
            if (corridor != null && corridorSection != null)
            {
                r++;
                Row(wx, r++).Text("A", "עפר, מצעים ואספלט — מהקורידורים", s.H12);
                foreach (var text in CorridorExplanation(corridor, corridorSection)) Row(wx, r++).Text("A", text, s.T11);
            }
            if (result.Warnings.Count > 0)
            {
                r++;
                Row(wx, r++).Text("A", "אזהרות הריצה", s.H12);
                foreach (var warning in result.Warnings) Row(wx, r++).Text("A", "• " + warning, s.WarnText);
            }

            var wsrc = new MiniXlsx.Worksheet { SheetName = SheetSources, RightToLeft = true, PrintLandscapeFitToWidth = true };
            wsrc.ColumnWidths.Add((1, 38));
            wsrc.ColumnWidths.Add((2, 80));
            var sourceRows = SourcesRows(result);
            r = 1;
            foreach (var (label, value) in sourceRows)
            {
                if (label.Length > 0 || value.Length > 0)
                {
                    var row = Row(wsrc, r);
                    TextOrBlank(row, "A", label, s.Latin);
                    TextOrBlank(row, "B", value, s.Text);
                }
                r++;
            }
            if (corridor != null)
            {
                r++;
                foreach (var (label, value) in CorridorSourceRows(corridor))
                {
                    var row = Row(wsrc, r);
                    TextOrBlank(row, "A", label, s.Latin);
                    TextOrBlank(row, "B", value, s.Text);
                    r++;
                }
            }

            wb.AdditionalSheets.Add(wx);
            wb.AdditionalSheets.Add(wp);
            wb.AdditionalSheets.Add(wsg);
            wb.AdditionalSheets.Add(wc);
            wb.AdditionalSheets.Add(wd);
            wb.AdditionalSheets.Add(wk);
            wb.AdditionalSheets.Add(we);
            wb.AdditionalSheets.Add(wu);
            wb.AdditionalSheets.Add(wm);
            wb.AdditionalSheets.Add(wsrc);
            if (corridorSection != null)
                foreach (var sheet in corridorSection.Sheets) wb.AdditionalSheets.Add(sheet);
            return wb;
        }

        /// <summary>The corridor run behind chapters 51.01–51.04: run, drawing, measurement file, rules and pricebook identities.</summary>
        internal static IReadOnlyList<(string Label, string Value)> CorridorSourceRows(CorridorInput corridor)
        {
            var rules = corridor.Export.Rules;
            return new List<(string, string)>
            {
                ("מדידת קורידורים (ריצה)", corridor.RunId),
                ("שרטוט הקורידורים", $"{corridor.Context.DrawingPath} · SHA-256 {corridor.Context.DrawingSha256}"),
                ("קובץ המדידה", $"{CorridorBoqMeasuresFile.FileName} · SHA-256 {corridor.MeasuresSha256}"),
                ("נתוני גלם לכל חתך", $"{Path.GetFileName(corridor.Context.RawReceiptPath)} · SHA-256 {corridor.Context.RawReceiptSha256}"),
                ("כללי הקורידורים", $"פרויקט {rules.Project} קורידורים v{rules.Version} · SHA-256 {corridor.RulesetSha256}"),
                ("מחירון הקורידורים", $"{rules.Pricebook.Name} {rules.Pricebook.Edition} ({rules.Pricebook.Id}) · קובץ המקור SHA-256 {rules.Pricebook.SourceSha256}"),
            };
        }

        /// <summary>The 'לא נכלל' row of a chapter the corridors price: what stays outside it (demolition for 51.01; the
        /// corridors outside the corridor total for every chapter).</summary>
        internal static (string Title, string Reason) CorridorNotIncluded(BoqNotIncluded ni, IReadOnlyList<string> outside)
        {
            var outsideText = outside.Count == 0 ? "" : $"לא בסה\"כ הקורידורים: {string.Join(", ", outside)}.";
            if (ni.Id == CorridorPartialChapter)
                return ($"{ni.Title} — פירוקים", $"החישוף נמדד מהקורידורים (פרק {ni.Id}, למעלה). הפירוקים לא נמדדו: מה מפורק דורש החלטת מהנדס — לאישור." +
                    (outsideText.Length > 0 ? " " + outsideText : ""));
            return ($"{ni.Title} — מחוץ למדידת הקורידורים", $"נמדד מהקורידורים (פרק {ni.Id}, למעלה) רק בקורידורים שנמדדו במלואם. {outsideText}");
        }

        /// <summary>Explanation lines for the corridor chapters: what was measured, what is totalled, what is open.</summary>
        internal static IReadOnlyList<string> CorridorExplanation(CorridorInput corridor, CorridorBoqWorkbook.Section section)
        {
            var lines = new List<string>
            {
                $"• פרקים {DisplayLtr("51.01–51.04")} (חישוף, חפירה ומילוי, מצעים, שכבות אספלט וריסוסים) נמדדו בכלי מהקורידורים בקובץ {DisplayLtr(Path.GetFileName(corridor.Context.DrawingPath))} " +
                $"({corridor.Context.Now:dd.MM.yyyy HH:mm}) — כל כמות היא נוסחה מעל הגיליונות \"{CorridorBoqWorkbook.CombinedPrefix}{CorridorBoqWorkbook.SheetCalc}\" ו-\"{CorridorBoqWorkbook.CombinedPrefix}{CorridorBoqWorkbook.SheetMeasure}\".",
                "• קורידור נכנס לסה\"כ רק בתחום שנמדד בו במלואו (עפר לחוד, מצעים ואספלט לחוד); קורידור חלקי מוצג ואינו בסה\"כ.",
                "• שורות \"שטחים מדודים\" (מיסעה, נת\"צ, שביל אופניים — מההצללות בתכנית) הן ללא מחיר ואינן בסה\"כ; שכבות האספלט בפרק 51.04 הן מהקורידורים. " +
                "שני המקורות מתארים שטחים חופפים ולא הותאמו זה לזה — אין להוסיף אותם זה לזה.",
            };
            lines.AddRange(section.Open.Skip(1).Select(o => o.StartsWith("•", StringComparison.Ordinal) ? o : "• " + o));
            return lines;
        }

        private const string MissingItem = "(הסעיף אינו במחירון שבכללים — לבדיקה)";

        private static string DependencySourceDisclosure(IEnumerable<string> sources, BoqEngineResult result) =>
            "מקור חלקי: " + string.Join(", ", sources.OrderBy(id => id, StringComparer.Ordinal).Select(result.Ref)) +
            " (ראו \"חסרים\"). הכמות הידועה בלבד; הסך הכולל אינו ידוע.";

        // A stable, human-readable receipt. Missing provenance is disclosed, never replaced by the reference's old run.
        private static IReadOnlyList<(string Label, string Value)> SourcesRows(BoqEngineResult result)
        {
            const string unknown = "לא נמסר בריצה זו";
            var rules = result.Rules;
            var input = result.Input;
            var rows = new List<(string, string)>();
            var used = new HashSet<BoqSourceRow>();
            var roles = rules.SourceRoles;
            BoqSourceRow? Drawing(string role) => input.SourceRows.FirstOrDefault(x => x.Role == role) ??
                input.SourceRows.FirstOrDefault(x => x.Role == null && x.Label == role && !x.Value.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
            foreach (var role in roles)
            {
                var source = Drawing(role.Id);
                if (source != null) used.Add(source);
                var value = source?.FileName is { Length: > 0 } file
                    ? file + (source.MeasuredAtUtc is { } date ? " — נמדד ב-Civil 3D ב-" + date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + ": " : " — תאריך המדידה לא נמסר: ")
                    : source?.Value is { Length: > 0 } raw ? raw : unknown;
                if (!string.IsNullOrWhiteSpace(role.Contribution)) value += role.Contribution;
                rows.Add(("קובץ " + role.Id, value));
            }
            rows.Add(("", ""));
            var version = rules.Version.Split(' ')[0];
            rows.Add((version.Length > 0 ? $"כללי החישוב (גרסה {version})" : $"כללי החישוב — פרויקט {rules.Project}", "כללי החישוב של הקובץ — הכללים, הפרמטרים והמיפוי לסעיפים"));
            var pricebook = rules.Pricebook.Name == "נתיבי ישראל - המחירון האחוד" ? "המחירון האחוד של נתיבי ישראל" : rules.Pricebook.Name;
            rows.Add(("מחירון", string.IsNullOrWhiteSpace(pricebook) ? unknown : $"{pricebook} — {rules.Pricebook.Edition} (מחירי בסיס)"));
            rows.Add(("", ""));
            rows.AddRange(rules.Sources.Select(pair => (SourceLabels.TryGetValue(pair.Key, out var label) ? label : pair.Key, pair.Value)));
            rows.Add(("", ""));
            rows.Add(("טביעות קבצים ומזהי הרצות (לביקורת בלבד)", ""));
            string Named(string label)
            {
                var source = input.SourceRows.FirstOrDefault(x => x.Role == null && x.Label == label);
                if (source == null) return unknown;
                used.Add(source); return string.IsNullOrWhiteSpace(source.Value) ? unknown : source.Value;
            }
            foreach (var role in roles)
                rows.Add(("הרצת מדידה " + role.Id, Drawing(role.Id)?.RunId ?? Named("הרצת מדידה " + role.Id)));
            rows.Add(("כללי החישוב", "SHA-256: " + rules.Sha256));
            rows.Add(("מחירון", string.IsNullOrWhiteSpace(rules.Pricebook.SourceSha256) ? unknown : "SHA-256: " + rules.Pricebook.SourceSha256));
            foreach (var label in new[] { "ראיות מדידה HA — בדיקת שטחי ההצללות", "ראיות מדידה DR ו-GM — השוואת עצמים משותפים", "ראיות מדידה SM" })
                rows.Add((label, Named(label)));
            foreach (var role in roles.Where(x => x.Id != rules.Crosswalk?.Src.FirstOrDefault()))
            {
                var label = "רשומות מדידה " + role.Id;
                rows.Add((label, Named(label)));
            }
            var crossRole = rules.Crosswalk?.Src.FirstOrDefault();
            if (crossRole != null)
            {
                var label = $"גאומטריית מעברי החציה ({crossRole}) — קיבוץ, אורך ורוחב (עמודות C–D בגיליון \"מעברי חציה\")";
                var value = Named(label);
                rows.Add((label, value == unknown && !string.IsNullOrWhiteSpace(input.GeometryReader) ? input.GeometryReader : value));
            }
            // Geometry order is explicitly separate from the drawing order; preserve receipt order when available.
            var geometryRoles = roles.OrderBy(x => x.Id == "DR" ? 0 : x.Id == "GM" ? 1 : x.Id == "HA" ? 2 : 3);
            foreach (var role in geometryRoles)
            {
                var label = "גאומטריה לעצם אחד פעם אחת (" + role.Id + ")";
                rows.Add((label, Named(label)));
            }
            rows.Add(("גבולות הצללות HA לאומדן החפיפות (Civil 3D)", Named("גבולות הצללות HA לאומדן החפיפות (Civil 3D)")));
            // Preserve additional producer evidence instead of discarding it to achieve a reference row count.
            foreach (var source in input.SourceRows.Where(x => !used.Contains(x))) rows.Add((source.Label, source.Value));
            foreach (var role in roles)
                if (Drawing(role.Id)?.DrawingSha256 is { Length: > 0 } sha) rows.Add(("טביעת קובץ " + role.Id, "SHA-256: " + sha));
            return rows;
        }

        /// <summary>A sign-range text: "{from}", "{to}" and "{hits}" (the reference prints the files as a Python dict).</summary>
        private static string SignRangeText(string template, int from, int to, BoqEngineResult result) => DisplayNumericRanges(
            (template == "אין שלטים {from}–{to} בשרטוט — אין שטח למדידה"
                ? "לא נמצאו בשמות הבלוקים והשכבות (טקסטים ו-XREF לא נבדקו) — השטח לא נמדד; אם יש שלטים {from}–{to} בתכנית, יש למדוד את שטחם" : template)
            .Replace("{from}", from.ToString(CultureInfo.InvariantCulture))
                .Replace("{to}", to.ToString(CultureInfo.InvariantCulture))
                .Replace("{hits}", "{" + string.Join(", ", result.GuideHits.Items.Select(pair =>
                    $"'{pair.Key}': {pair.Value.ToString(CultureInfo.InvariantCulture)}")) + "}"));

        private static string PolesText(string template, BoqEngineResult result) =>
            (template == "בשרטוט {signs} תמרורים על {poles} עמודים — לאישור"
                ? "בשרטוט {signs} תמרורים מול {poles} עמודים — לא נבדק אילו עמודים נושאים שני תמרורים — לאישור"
                : template == "בשרטוט {signs} תמרורים על {poles} עמודים" ? "בשרטוט {signs} תמרורים מול {poles} עמודים — לא נבדק אילו עמודים נושאים שני תמרורים" : template)
            .Replace("{signs}", result.SignsTotal.ToString(CultureInfo.InvariantCulture))
                .Replace("{poles}", result.PolesCount.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// The note of an unclassified group (reference UNCL_NOTES + the manhole / inlet notes): the first ruleset note whose
        /// file, layer, kind and contained type text all match; "" when none. "{poles}" = the pole count, "{ref:LINE}" = that
        /// line's item.
        /// </summary>
        internal static string UnclassifiedNote(BoqEngineResult result, BoqGroup group)
        {
            var types = string.Join(" ", group.Types.Select(t => t.Key));
            foreach (var note in result.Rules.Workbook.UnclassifiedNotes)
            {
                if (note.Src != null && !string.Equals(note.Src, group.Src, StringComparison.Ordinal)) continue;
                if (note.Layer != null && !string.Equals(note.Layer, group.Layer, StringComparison.Ordinal)) continue;
                if (note.Kind != null && !string.Equals(note.Kind, group.Kind, StringComparison.Ordinal)) continue;
                if (note.TypesContain != null && !types.Contains(note.TypesContain, StringComparison.Ordinal)) continue;
                var text = note.Note.Replace("{poles}", result.PolesCount.ToString(CultureInfo.InvariantCulture));
                foreach (var line in result.Rules.Lines)
                    text = text.Replace("{ref:" + line.Id + "}", line.Ref())
                        .Replace("{review:" + line.Id + "}", line.Review ?? "לא נמסרה הערת בדיקה לסעיף");
                // These are reviewed project observations, not measurements this writer performs. Without a
                // producer's explicit context verification they must remain attributable prior-review notes.
                if (text.Contains("זוגות קונצנטריים", StringComparison.Ordinal) ||
                    text.Contains("בני הזוג בכיוון ההפוך שאוחדו", StringComparison.Ordinal) ||
                    text.Contains("ששטחה שווה לשטח הפוליליין", StringComparison.Ordinal) ||
                    text.Contains("אותו עצם גם ב-DR", StringComparison.Ordinal))
                {
                    var contextKey = "אימות הערת פרויקט " + group.Src + ":" + group.Layer + ":" + group.Kind;
                    var fingerprint = ContextEvidenceFingerprint(result.Input, group, text);
                    var verified = result.Input.SourceRows.Any(x => x.Label == contextKey && x.Value == "אומת מול קלט הריצה" &&
                        string.Equals(x.DrawingSha256, fingerprint, StringComparison.OrdinalIgnoreCase));
                    if (!verified) text = "הערת בדיקת הפרויקט (לא אומתה מחדש מול קלט הריצה): " + text;
                }
                return text;
            }
            return "";
        }

        /// <summary>
        /// Binds a producer's prior-review context receipt to this exact input and group. Computing this digest is not
        /// context verification; the producer may attach it only after checking the recorded engineering observation.
        /// Mutable record coordinates, types, count/rotation geometry and hatch inputs are included, so merely retaining
        /// a source label or a group count cannot make an old review current.
        /// </summary>
        public static string ContextEvidenceFingerprint(BoqInputSet input, BoqGroup? group = null, string? reviewedNote = null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                input.Records, input.Geometry, input.HatchLayers, input.HaHatches, input.HaHatchPolygons, input.Arrays,
                Points = input.CountPoints.OrderBy(x => x.Key.Src, StringComparer.Ordinal).ThenBy(x => x.Key.Handle, StringComparer.Ordinal)
                    .Select(x => new { x.Key.Src, x.Key.Handle, x.Value.X, x.Value.Y }),
                Rotations = input.CountRotations.OrderBy(x => x.Key.Src, StringComparer.Ordinal).ThenBy(x => x.Key.Handle, StringComparer.Ordinal)
                    .Select(x => new { x.Key.Src, x.Key.Handle, Angle = x.Value }),
                Lengths = input.LengthGeometry.OrderBy(x => x.Key.Src, StringComparer.Ordinal).ThenBy(x => x.Key.Handle, StringComparer.Ordinal)
                    .Select(x => new { x.Key.Src, x.Key.Handle, Geometry = x.Value }),
                Group = group,
                ReviewedNote = reviewedNote,
            });
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        }

        /// <summary>
        /// The 'עצם אחד פעם אחת' sheet (build_boq_v7.py v4): one row per length part measured one object once (the item,
        /// lines, the sum as drawn, the plan length, the one-object-once quantity, the search distance and how the object is
        /// drawn — the length at each number of parallel lines), then the removed duplicate blocks, then how the rule works.
        /// </summary>
        private static MiniXlsx.Worksheet ObjectsSheet(BoqEngineResult result, Styles s, BoqHaOverlap? ha)
        {
            var wk = NewSheet(SheetObjects, new[] { 16d, 40, 8, 34, 9, 14, 14, 14, 10, 58 }, ObjectsHeaders, s);
            wk.PrintTitleRows = 0;
            var r = 2;
            foreach (var P in result.Parts.Where(p => p.DrawnSum != null))
            {
                var drawn = P.DrawnSum!.Value;
                var merged = drawn - P.Base > 0.01 * Math.Max(P.Base, 1);
                Row(wk, r).Text("A", P.Line.HasItem ? P.Line.Ref() : WithoutItem, s.Latin).Text("B", P.Part.Label, s.Text)
                    .Text("C", string.Join(", ", P.Srcs.Keys), s.Latin).Text("D", string.Join(", ", P.Layers.Keys), s.Latin)
                    .Number("E", (double)P.Objects, s.Count).Number("F", BoqRulesEngine.PythonRound(drawn, 2), s.Num2)
                    .Number("G", BoqRulesEngine.PythonRound(P.PlanSum ?? 0.0, 2), s.Num2)
                    .Number("H", BoqRulesEngine.PythonRound(P.Base, 2), merged ? s.WarnNum2 : s.Num2)
                    .Number("I", P.Width ?? 0.0, s.Count).Text("J", (P.WidthNote ?? "") + DrawnAs(P.ByMultiplicity), s.Text);
                r++;
            }
            r++;
            Row(wk, r).Text("A", ObjectsDuplicateBlocksTitle, s.BoldText);
            wk.SingleRowMerges.Add($"A{r}:G{r}");
            r++;
            var head = Row(wk, r);
            for (var i = 0; i < DuplicateBlocksHeaders.Length; i++) head.Text(Col(i + 1), DuplicateBlocksHeaders[i], s.Head);
            r++;
            foreach (var d in result.Dedup)
            {
                var line = result.Rules.Lines.FirstOrDefault(x => x.Id == d.LineId);
                Row(wk, r).Text("A", line?.HasItem == true ? result.Ref(d.LineId) : WithoutItem, s.Latin).Text("B", d.PartLabel, s.Text).Text("C", d.Src, s.Latin)
                    .Text("D", d.Layer, s.Latin).Text("E", d.Block, s.Latin).Text("F", d.Handle, s.Latin).Text("G", d.Kept, s.Latin);
                r++;
            }
            r++;
            foreach (var text in ObjectsParagraphs(result))
            {
                Notice(wk, r, "J", text, s, 48);
                r++;
            }
            AppendNoteFigures(wk, ref r, result, s);
            AppendHaTable(wk, ref r, ha, result.Parts.Any(p => p.Part.Kind == "hatch"), result.Rules.HaOverlap, result.Input, s);
            return wk;
        }

        private static void AppendNoteFigures(MiniXlsx.Worksheet sheet, ref int row, BoqEngineResult result, Styles s)
        {
            var nf = result.NoteFigures;
            if (nf == null) return;
            var cfg = result.Rules.NoteFigures ?? throw new InvalidDataException("Note figures exist without their calculation settings.");
            row++;
            Row(sheet, row++).Text("B", "מספרים שמופיעים בהערות — מאיפה הם", s.BoldText);
            Row(sheet, row++).Text("B", "מה", s.Head).Text("E", "קווים", s.Head).Text("F", "כפי שצויר (מ')", s.Head)
                .Text("G", "בתוכנית (מ')", s.Head).Text("H", "עצם אחד פעם אחת (מ')", s.Head).Text("J", "איך חושב", s.Head);
            var noteRow = row;
            void Add(string label, double? count, double? drawn, double? plan, double? one, string how)
            {
                var r = Row(sheet, noteRow++).Text("B", label, s.Text).Text("J", how, s.Text);
                if (count != null) r.Number("E", count.Value, s.Count);
                if (drawn != null) r.Number("F", drawn.Value, s.Num2);
                if (plan != null) r.Number("G", plan.Value, s.Num2);
                if (one != null) r.Number("H", one.Value, s.Num2);
            }
            var how = $"לאורך האבן המונמכת ({cfg.Src}): קטע שלצידו, עד {F(cfg.ReachM)} מ' ובמקביל, קו של הסעיף שההיטל עליו בתוך הקו (לא בהמשכו)";
            Add(result.Ref("C4") + ": אבן מונמכת — כל האורך", result.Parts.Where(p => p.Line.Id == "C4").Sum(p => p.Objects), null, null, nf.LoweredTotalM, "עצם אחד פעם אחת");
            Add("אבן מונמכת שלצידה קו " + result.Ref("C1"), null, null, null, nf.LoweredBesideC1M, how);
            Add("אבן מונמכת שלצידה קו " + result.Ref("C2"), null, null, null, nf.LoweredBesideC2M, how);
            Add("אבן מונמכת שלצידה קווים של שני הסעיפים", null, null, null, nf.LoweredBesideBothM, how);
            var pairRange = Math.Abs(cfg.PairLoM - 0.165) < 1e-9 && Math.Abs(cfg.PairHiM - 0.265) < 1e-9
                ? "0.17–0.26 מ' (±5 מ\"מ)" : $"{cfg.PairLoM.ToString("0.###", CultureInfo.InvariantCulture)}–{cfg.PairHiM.ToString("0.###", CultureInfo.InvariantCulture)} מ' לפי הגדרות הריצה";
            Add(result.Ref("C1") + " שלצידו קו אבן אי תנועה (" + result.Ref("C2") + ") במרחק " + pairRange, null, null, null, nf.CurbC1WithC2FaceM, "ייתכן שאותה אבן בשתי שכבות (פאה בכל שכבה) — לבדיקה; לא הופחת");
            var frameLabel = cfg.FrameLayer == "TR-MARK-WHT-815" ? "815" : cfg.FrameLayer;
            var frameItem = result.Rules.Lines.FirstOrDefault(l => l.Parts.Any(p => p.Layers.Contains(cfg.FrameLayer)))?.ItemFor(result.RoadClass);
            Add(frameLabel + $": קווים ארוכים מ-{F(cfg.FrameMinM)} מ' (כנראה מסגרת האי)", nf.Frame815Lines, null, nf.Frame815PlanM, nf.Frame815OneObjectM,
                $"קווי {cfg.FrameLayer} ארוכים מ-{F(cfg.FrameMinM)} מ'; עצם אחד פעם אחת ברוחב {F2(cfg.FrameWidthM)}" + (frameItem == null ? " לפי הגדרות הריצה" : $" (כמו בסעיף {frameItem})"));
            var singleItem = result.Rules.Lines.FirstOrDefault(l => l.Parts.Any(p => p.Param != null && result.Parameters.TryGetValue(p.Param, out var value) && Math.Abs(value - cfg.FrameAltWidthM) < 1e-9) ||
                l.Parts.Any(p => p.Alt != null && Math.Abs(p.Alt.Value - cfg.FrameAltWidthM) < 1e-9))?.ItemFor(result.RoadClass);
            // A length item's billing unit may be metres rather than an area multiplier; match the explicit road-width parameter too.
            singleItem ??= Math.Abs(cfg.FrameAltWidthM - 0.10) < 1e-9 && result.RoadClass == 1
                ? result.Rules.Lines.FirstOrDefault(l => l.ItemUrban == "51.32.1852")?.ItemFor(result.RoadClass) : null;
            Add(frameLabel + $": אותם קווים ברוחב קו יחיד ({F2(cfg.FrameAltWidthM)})", null, null, null, nf.Frame815OneObjectAt1852WidthM, singleItem == null ? "חלופה לבדיקה — לא שינוי בכמות החיוב" : "אם יעברו ל-" + singleItem);
            Add((cfg.LongRectLayer == "TR-MARK-WHT-3-1.5-808" ? "808: " : "") + "מלבנים ארוכים (" + cfg.LongRectLayer + ")", nf.LongRect808Lines, null, nf.LongRect808PlanM, null, "כל הקווים בשכבה");
            if (result.Controls.TryGetValue("M4", out var ctl))
                Add("בקרת צביעת אבני שפה (TR-CURB-RED/BLK/YLV)", ctl.Objects, BoqRulesEngine.PythonRound(ctl.Drawn, 2), null, BoqRulesEngine.PythonRound(ctl.Length, 1), "כפי שצויר (גיליון \"לא נכלל\") מול עצם אחד פעם אחת (הערת 51.32.2640)");
            row = noteRow;
        }

        private static IReadOnlyList<string> HaLineNotes(BoqLine line, BoqEngineResult result, BoqHaOverlap? ha)
        {
            var layers = line.Parts.Where(p => p.Kind == "hatch").SelectMany(p => p.Layers).Distinct().ToList();
            if (layers.Count == 0) return Array.Empty<string>();
            if (ha == null) return new[] { "לבדיקה: בדיקת חפיפות ואומדני גבולות ההצללות טרם נמסרה בריצה זו; הכמות היא המדידה הקיימת בלבד, ללא השלמה או הפחתת חפיפות" };
            var names = result.Rules.Lines.SelectMany(l => l.Parts.Where(p => p.Kind == "hatch").SelectMany(p => p.Layers.Select(layer => (Layer: layer, Label: p.Label))))
                .GroupBy(x => x.Layer).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
            var notes = new List<string>();
            foreach (var layer in layers)
            {
                var missing = ha.Missing.FirstOrDefault(x => x.Layer == layer);
                var bits = new List<string>();
                if (missing != null && missing.Built > 0)
                    bits.Add(CountHe(missing.Count, "הצללה אחת", "הצללות") +
                        " ששטחן לא נקרא ב-Civil — אומדן מקורב מגבולן: כ-" + Whole(missing.AreaM2) +
                        " מ\"ר (לא כלול בכמות)" + (missing.Built < missing.Count ? $"; {missing.Count - missing.Built} גבולות נוספים לא נבנו" : ""));
                var overlaps = new List<string>();
                var metrics = ha.Layers.FirstOrDefault(x => x.Layer == layer);
                if (metrics != null && metrics.OverlapInsideLayerM2 >= 10)
                    overlaps.Add("בתוך השכבה כ-" + Whole(metrics.OverlapInsideLayerM2) + " מ\"ר");
                foreach (var pair in ha.Between.Where(x => x.AreaM2 >= 10).OrderByDescending(x => x.AreaM2))
                {
                    var members = new[] {pair.LayerA, pair.LayerB};
                    if (members.Length != 2 || !members.Contains(layer)) continue;
                    var other = members.First(x => x != layer);
                    overlaps.Add("עם " + names.GetValueOrDefault(other, other) + " כ-" + Whole(pair.AreaM2) + " מ\"ר");
                }
                if (overlaps.Count > 0)
                    bits.Add("אומדן מקורב של חפיפה בין הצללות (לא הופחת ולא אושר): " + string.Join(", ", overlaps));
                if (bits.Count > 0) notes.Add("לבדיקה: " + string.Join("; ", bits) + " — ראו \"עצם אחד פעם אחת\"");
            }
            return notes;
        }

        private static string Whole(double area) => BoqRulesEngine.PythonRound(area, 0).ToString("#,##0", CultureInfo.InvariantCulture);

        private static void ValidateHaOverlap(BoqHaOverlap? ha)
        {
            if (ha == null) return;
            static bool Area(double value) => double.IsFinite(value) && value >= 0;
            if (ha.Hatches < 0 || ha.Polygons < 0 || ha.Polygons > ha.Hatches || ha.CheckedAgainstCivil < 0 ||
                ha.CheckedAgainstCivil > ha.Polygons || ha.AgreeWithCivil < 0 || ha.AgreeWithCivil > ha.CheckedAgainstCivil ||
                ha.Layers == null || ha.Between == null || ha.Missing == null ||
                ha.Layers.Any(x => x == null || x.Count < 0 || !Area(x.SumM2) || !Area(x.UnionM2) || !Area(x.OverlapInsideLayerM2)) ||
                ha.Between.Any(x => x == null || !Area(x.AreaM2)) ||
                ha.Missing.Any(x => x == null || x.Count < 0 || x.Built < 0 || x.Built > x.Count || !Area(x.AreaM2) || x.NotBuilt == null))
                throw new InvalidDataException("Invalid HA overlap diagnostics; no workbook was published.");
        }

        private static IEnumerable<BoqMissingRow> ExpandedMissingRows(BoqEngineResult result)
        {
            foreach (var missing in result.Missing)
            {
                if (missing.Src == "HA" && missing.Handle.EndsWith(" הצללות (ללא פירוט Handle במקור)", StringComparison.Ordinal))
                {
                    var unresolved = result.Input.HaHatches.Where(h => h.Layer == missing.Layer && h.State == "unresolved").ToList();
                    var countText = missing.Handle.Split(' ')[0];
                    if (int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) &&
                        unresolved.Count == expected && unresolved.All(h => !string.IsNullOrWhiteSpace(h.Handle)) &&
                        unresolved.Select(h => h.Handle).Distinct(StringComparer.Ordinal).Count() == expected)
                    {
                        foreach (var hatch in unresolved) yield return missing with { Handle = hatch.Handle };
                        continue;
                    }
                }
                yield return missing;
            }
        }

        private static void AppendHaTable(MiniXlsx.Worksheet sheet, ref int row, BoqHaOverlap? ha, bool applicable, BoqHaOverlapConfig? cfg, BoqInputSet input, Styles s)
        {
            if (!applicable) return;
            row++;
            Row(sheet, row++).Text("B", "שטחי הצללות בהערות — אומדן מקורב (לא כלול בכמות, לא הופחת ולא אושר כמדידה)", s.BoldText);
            if (ha == null)
            {
                Notice(sheet, row++, "J", "נתוני בדיקת הגבולות/חפיפות לא נמסרו לריצה זו; אין אומדן שטח או טענת כיסוי. החסרים נשארים בגיליון \"חסרים\".", s, 42);
                return;
            }
            var polygonSource = input.SourceRows.FirstOrDefault(x => x.Label == "שיטת קריאת גבולות הצללות HA" &&
                string.Equals(x.DrawingSha256, ContextEvidenceFingerprint(input), StringComparison.OrdinalIgnoreCase))?.Value;
            var method = string.IsNullOrWhiteSpace(polygonSource) ? "גבולות ההצללות שנמסרו לריצה (שיטת חלוקת הקשתות ומקור הגבולות לא נמסרו)" : polygonSource;
            var coverage = $"האומדן מחושב מ{method}, לא ממדידת שטח: נבנו {ha.Polygons} מתוך {ha.Hatches} הצללות" +
                (ha.Hatches > ha.Polygons ? $" ({ha.Hatches - ha.Polygons} לא נבנו)" : "") +
                $"; מתוך {ha.CheckedAgainstCivil} הצללות ש-Civil מדד, ב-{ha.AgreeWithCivil} השטח מהגבול קרוב לשטח של Civil " +
                (cfg == null ? "(סבולת לפי נתוני המנוע)" : $"(עד {F(cfg.AgreeRel * 100)}%, לפחות {F(cfg.AgreeAbsM2)} מ\"ר)") +
                $" וב-{ha.CheckedAgainstCivil - ha.AgreeWithCivil} ההפרש גדול יותר. לכן אלה סדרי גודל לבדיקה, לא כמויות.";
            var coverageRow = Row(sheet, row); coverageRow.HeightPoints = 48; coverageRow.Text("B", coverage, s.Note);
            sheet.SingleRowMerges.Add($"B{row}:J{row}"); row++;
            Row(sheet, row++).Text("B", "מה", s.Head).Text("E", "הצללות", s.Head).Text("G", "שטח מקורב (מ\"ר)", s.Head).Text("J", "איך חושב", s.Head);
            foreach (var pair in ha.Missing.Where(x => x.Built > 0))
                Row(sheet, row++).Text("B", pair.Layer + ": הצללות ששטחן לא נקרא ב-Civil", s.Text)
                    .Number("E", (double)pair.Count, s.Count).Number("G", BoqRulesEngine.PythonRound(pair.AreaM2, 0), s.Count)
                    .Text("J", "שטח המצולע מגבול ההצללה — אומדן, לא כלול בכמות" + (pair.Built < pair.Count ? $"; נבנו {pair.Built} מתוך {pair.Count} גבולות" : ""), s.Text);
            foreach (var pair in ha.Layers.Where(x => x.OverlapInsideLayerM2 >= 10))
                Row(sheet, row++).Text("B", pair.Layer + ": חפיפה בין הצללות באותה שכבה", s.Text)
                    .Number("E", (double)pair.Count, s.Count).Number("G", BoqRulesEngine.PythonRound(pair.OverlapInsideLayerM2, 0), s.Count)
                    .Text("J", "סכום שטחי ההצללות שנבנו בשכבה (עמודה E) פחות שטח האיחוד — אומדן, לא הופחת", s.Text);
            foreach (var pair in ha.Between.Where(x => x.AreaM2 >= 10))
                Row(sheet, row++).Text("B", "חפיפה בין השכבות " + pair.LayerA + " × " + pair.LayerB, s.Text).Number("G", BoqRulesEngine.PythonRound(pair.AreaM2, 0), s.Count)
                    .Text("J", "שטח החיתוך של שתי השכבות — אומדן, לא הופחת", s.Text);
        }

        /// <summary>
        /// v4: " | אורך העצם לפי מספר הקווים שצוירו באותו מקום: קו אחד: 40 מ'; 2 קווים: 2 מ'; 3 ומעלה: 800 מ'" — the length
        /// counted where the object is drawn as one line, two lines and three or more (only amounts of at least 0.5 m).
        /// </summary>
        internal static string DrawnAs(IReadOnlyDictionary<int, double>? byMultiplicity)
        {
            var bm = byMultiplicity ?? new Dictionary<int, double>();
            var groups = new (string Label, double Value)[]
            {
                ("קו אחד", bm.TryGetValue(1, out var one) ? one : 0.0),
                ("2 קווים", bm.TryGetValue(2, out var two) ? two : 0.0),
                ("3 ומעלה", bm.Where(pair => pair.Key >= 3).Sum(pair => pair.Value)),
            };
            return ObjectsDrawnAs + string.Join("; ", groups.Where(g => g.Value >= 0.5)
                .Select(g => $"{g.Label}: {g.Value.ToString("#,##0", CultureInfo.InvariantCulture)} מ'"));
        }

        /// <summary>
        /// The paragraphs of the one-object-once sheet: the ruleset's (workbook.objects_notes, the reference's texts) when
        /// given; otherwise generic texts with the example numbers, the tolerance and the geometry source of this result.
        /// </summary>
        internal static IReadOnlyList<string> ObjectsParagraphs(BoqEngineResult result)
        {
            if (result.Rules.Workbook.ObjectsNotes.Count > 0) return result.Rules.Workbook.ObjectsNotes;
            var drawing = ObjectsParagraphDrawing +
                          $" רוחב העצם של כל סעיף — בעמודה \"{ObjectsHeaders[8]}\", ועוד {F(result.Rules.Measurement.ToleranceM)} מ' סבולת שרטוט.";
            var plan = ObjectsParagraphPlan;
            var most = result.Parts.Where(p => p.DrawnSum != null && p.PlanSum != null)
                .OrderByDescending(p => p.DrawnSum!.Value - p.PlanSum!.Value).FirstOrDefault();
            var inflation = most == null ? 0.0 : most.DrawnSum!.Value - most.PlanSum!.Value;
            if (most != null && inflation > 1.0)
            {
                var amount = inflation.ToString("#,##0", CultureInfo.InvariantCulture);
                plan += $" (למשל כ-{amount} מ' ב{most.Part.Label})";
            }
            plan += " — ההפרש בין \"סכום כל הקווים כפי שצוירו\" ל\"אורך בתוכנית\".";
            var reader = result.Input.ObjectGeometryReader;
            var source = "הגאומטריה: " + (string.IsNullOrWhiteSpace(reader)
                ? "לא נמסרה גאומטריה בתוכנית — כל קו נמדד לפי האורך המקורי."
                : reader);
            return new[] { drawing, plan, source };
        }

        /// <summary>
        /// The explanation sheet: the ruleset's text as is when it says it is complete (workbook.explanation_complete, the
        /// reference's EXPLAIN list); otherwise the ruleset's text plus the rules 2.1 bullets of build_boq_v7.py (the
        /// line_sum rule of every line, one object once, duplicate blocks) inserted at the end of the rules block — each only
        /// when the ruleset's own text does not already say it. The line_sum bullet is built from the ruleset (label, item,
        /// who decided, the control), never from project names in code.
        /// </summary>
        internal static IReadOnlyList<(string Text, bool Bold)> Explanation(BoqEngineResult result)
        {
            var rules = result.Rules;
            // Overlap diagnostics are approximate/partial boundary analysis, not a native accepted measurement.
            // Preserve quantity semantics and only correct the misleading presentation claim.
            var lines = rules.Workbook.Explanation.Select(l =>
                (Text: l.Text.Contains("חפיפות", StringComparison.Ordinal)
                    ? l.Text.Replace("מדודות ולא הופחתו", "אומדן חלקי ומקורב שלא הופחת", StringComparison.Ordinal)
                    : l.Text, l.Bold)).ToList();
            if (rules.Workbook.ExplanationComplete) return lines;
            bool Mentions(string fragment) => lines.Any(l => l.Text.Contains(fragment, StringComparison.Ordinal));
            var added = new List<(string Text, bool Bold)>();
            foreach (var line in rules.Lines)
                foreach (var part in line.Parts.Where(p => p.Kind == "line_sum"))
                {
                    var item = line.ItemFor(result.RoadClass);
                    if (item != null && Mentions(item)) continue;
                    var bullet = "• " + part.Label + (item != null ? $" (סעיף {item})" : "");
                    if (!string.IsNullOrWhiteSpace(part.DecidedBy)) bullet += " — " + part.DecidedBy;
                    bullet += ".";
                    if (line.Control != null) bullet += $" {line.Control.Label} — להשוואה בלבד, לא נכנסת לכמות.";
                    added.Add((bullet, false));
                }
            if (result.Parts.Any(p => p.DrawnSum != null) && !Mentions(SheetObjects))
                added.Add((ExplainOneObjectOnce, false));
            if (rules.AllParts.Any(p => p.SamePoint != null) && !Mentions("באותה נקודה"))
                added.Add(($"• בלוקים כפולים באותה נקודה (גם בין קבצים) נספרים פעם אחת ({result.Dedup.Count} כפילויות הוסרו) — ראו גיליון \"{SheetObjects}\".", false));
            if (added.Count == 0) return lines;
            // At the end of the rules block: before the blank line that precedes the last heading (else at the end).
            var at = lines.FindLastIndex(l => l.Bold);
            if (at <= 0) at = lines.Count;
            else if (lines[at - 1].Text.Length == 0) at--;
            lines.InsertRange(at, added);
            return lines;
        }

        private static string Quantity(IReadOnlyList<int> detailRows) =>
            detailRows.Count == 0 ? "0" : string.Join("+", detailRows.Select(x => $"'{SheetDetail}'!I{x}"));

        // Display-only opt-ins. Never apply these helpers to catalog descriptions,
        // formulas, source receipts, identifiers, or the underlying engine strings.
        internal static string DisplayLtr(string token)
        {
            if (token.Length == 0) return token;
            var marked = Bidi.Ltr(token);
            var before = token[0] == '\u200E';
            var after = token[^1] == '\u200E';
            return marked.Substring(before ? 1 : 0, marked.Length - (before ? 1 : 0) - (after ? 1 : 0));
        }

        internal static string DisplayToken(string text, string token)
        {
            if (string.IsNullOrEmpty(token) || !HasHebrew(text)) return text;
            // Whole tokens only: 613–640 must not alter 1613–6400.
            var pattern = @"(?<![\p{L}\p{N}_.+\-/–])" + Regex.Escape(token) + @"(?![\p{L}\p{N}_.+\-/–])";
            return RestoreRtlAfter(
                Regex.Replace(text, pattern, match => HasCompoundContinuation(text, match) ? match.Value : MarkDisplayMatch(text, match)),
                pattern);
        }

        private static string DisplaySignRangeLabel(BoqPartResult part)
        {
            if (part.Part.Kind != "sign_block_area" || part.Part.SignRange is not { } range)
                return part.Part.Label;
            var from = range.From.ToString(CultureInfo.InvariantCulture);
            var to = range.To.ToString(CultureInfo.InvariantCulture);
            // Preserve the exact separator supplied in the display label.
            return DisplayToken(DisplayToken(part.Part.Label, from + "–" + to), from + "-" + to);
        }

        internal static string DisplayNumericRanges(string text)
        {
            if (!HasHebrew(text)) return text;
            var ranges = Regex.Replace(text, NumericRangePattern,
                match => !HasCompoundContinuation(text, match) && (HebrewPrecedes(text, match.Index) || HasDisplayMark(text, match)) ? MarkDisplayMatch(text, match) : match.Value);
            return DisplayInchRanges(RestoreRtlAfter(ranges, NumericRangePattern));
        }

        /// <summary>A range in inches, 4"–6" (each number followed by its inch mark), is one left-to-right token; in a Hebrew
        /// line it would otherwise read "4–"6 (Codex 00:45). Only this exact shape after Hebrew text is isolated.</summary>
        internal static string DisplayInchRanges(string text)
        {
            if (!HasHebrew(text)) return text;
            return RestoreRtlAfter(Regex.Replace(text, InchRangePattern,
                match => !HasCompoundContinuation(text, match) && (HebrewPrecedes(text, match.Index) || HasDisplayMark(text, match)) ? MarkDisplayMatch(text, match) : match.Value),
                InchRangePattern);
        }

        private const string NumericRangePattern =
            @"(?<![\p{L}\p{N}_.+\-/–])[-+]?\d+(?:\.\d+)?[–-][-+]?\d+(?:\.\d+)?(?![\p{L}\p{N}_.+\-/–])";
        private const string InchRangePattern =
            @"(?<![\p{L}\p{N}_.+\-/–""])\d+(?:\.\d+)?""[–-]\d+(?:\.\d+)?""(?![\p{L}\p{N}_.+\-/–""])";
        private const string AdditiveRunPattern =
            @"(?<![\p{L}\p{N}_.+\-/–])\d+(?:\.\d+)?(?:[ \t]*\+[ \t]*\d+(?:\.\d+)?)+(?![\p{L}\p{N}_.+\-/–])";

        /// <summary>
        /// b18 live (02/10, Codex 03:33): a wrapped token's closing LRM is a strong L, so an ASCII number after it (before any
        /// letter) resolved L and joined the LTR run — in Excel "(51.31.2205 / 2208)" after ‎4"–6"‎ drew in another order. An
        /// RLM right after the closing LRM restores the right-to-left context. Applied to raw, pre-wrapped and half-wrapped
        /// tokens alike, exactly once (LRM + token + LRM + RLM); a letter, a line/paragraph break or any direction control
        /// before the number adds nothing.
        /// </summary>
        private static string RestoreRtlAfter(string text, string tokenPattern) =>
            Regex.Replace(text, "(?:" + tokenPattern + ")\u200E(?!\u200F)",
                // The same compound guard as the wrap, on the token alone (the match without its closing LRM): a token that
                // an operator continues (‎1–2‎ /3) was rightly not wrapped and gets no RLM either (Codex 03:59).
                match => !HasCompoundContinuation(text, match.Index, match.Length - 1) && NumberFollows(text, match.Index + match.Length)
                    ? match.Value + '\u200F' : match.Value);

        private static bool NumberFollows(string text, int index)
        {
            for (var i = index; i < text.Length; i++)
            {
                var c = text[i];
                if (c is >= '0' and <= '9') return true;
                if (char.IsDigit(c)) return false; // another script's digit is not the proven case (Codex 04:04)
                if (char.IsLetter(c) || c is '\r' or '\n' or '\u2028' or '\u2029' or '\u200E' or '\u200F' or '\u061C'
                        or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
                    return false;
            }
            return false;
        }

        internal static string DisplayAdditiveRuns(string text)
        {
            if (!HasHebrew(text)) return text;
            // Isolate complete digit-led sums only. A leading operator belongs to the surrounding
            // Hebrew expression: wrapping +512 in מסגרת+512 moves the plus in native Excel.
            return RestoreRtlAfter(Regex.Replace(text, AdditiveRunPattern,
                match => !HasCompoundContinuation(text, match) && (HebrewPrecedes(text, match.Index) || HasDisplayMark(text, match)) ? MarkDisplayMatch(text, match) : match.Value),
                AdditiveRunPattern);
        }

        // A space does not end a token if a path/range/additive operator continues it.
        // Refuse partial wrapping of e.g. 512+801 +foo or 6422-2026 /10.
        private static bool HasCompoundContinuation(string text, Match match) => HasCompoundContinuation(text, match.Index, match.Length);

        private static bool HasCompoundContinuation(string text, int index, int length)
        {
            static bool Operator(char c) => c is '+' or '-' or '–' or '−' or '/' or '\\' or '_' or '.';
            var before = index - 1;
            while (before >= 0 && text[before] is ' ' or '\t' or '\u200E') before--;
            var after = index + length;
            while (after < text.Length && text[after] is ' ' or '\t' or '\u200E') after++;
            return (before >= 0 && Operator(text[before])) || (after < text.Length && Operator(text[after]))
                || (before > 0 && text[before] == ',' && char.IsDigit(text[before - 1]))
                || (after + 1 < text.Length && text[after] == ',' && char.IsDigit(text[after + 1]));
        }

        private static bool HasHebrew(string text) => text.Any(c => c is >= '\u0590' and <= '\u05FF');

        private static bool HasDisplayMark(string text, Match match) =>
            (match.Index > 0 && text[match.Index - 1] == '\u200E') ||
            (match.Index + match.Length < text.Length && text[match.Index + match.Length] == '\u200E');

        private static bool HebrewPrecedes(string text, int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (text[i] is '\r' or '\n' or '\u200E') return false;
                if (char.IsLetter(text[i])) return text[i] is >= '\u0590' and <= '\u05FF';
            }
            return false;
        }

        private static string MarkDisplayMatch(string text, Match match)
        {
            var before = match.Index > 0 && text[match.Index - 1] == '\u200E';
            var after = match.Index + match.Length < text.Length && text[match.Index + match.Length] == '\u200E';
            if (before && after) return match.Value;
            var marked = Bidi.Ltr(match.Value);
            return marked.Substring(before ? 1 : 0, marked.Length - (before ? 1 : 0) - (after ? 1 : 0));
        }

        private static string Confirmation(string text) => DisplayAdditiveRuns(
            text.TrimEnd().EndsWith("לאישור", StringComparison.Ordinal) ? text : "לאישור: " + text);

        /// <summary>Status of a line priced by a jointly decided estimating assumption (rules 'assumption').</summary>
        internal const string EstimateAssumption = "הנחת אומדן";

        private static string AssumptionNote(string text) =>
            text.StartsWith(EstimateAssumption, StringComparison.Ordinal) ? text : EstimateAssumption + ": " + text;

        private static string? PartConfirmation(BoqPartResult part, BoqEngineResult? result = null)
        {
            var assemblies = part.Crossings?.Where(c => c.WidthSource == "hatch-assembly").ToList();
            if (assemblies == null || assemblies.Count == 0) return part.Part.Confirm;
            var fallbackCount = assemblies.Count(c => c.Fallback > 0);
            string Param(string? id) => result == null || id == null ? "" :
                result.Rules.Parameters.Select((p, index) => (p.Id, Number: index + 1)).FirstOrDefault(p => p.Id == id) is var p && p.Number > 0 ? $" (פרמטר {p.Number})" : "";
            var dynamic = $"{assemblies.Count} מעברים עם הצללה ליד קו העצירה (גיליון \"מעברי חציה\", {CrossingReferences(part)}): " +
                "הזברה לפי הקו המקווקו × רוחב תקני" + Param(result?.Rules.Crosswalk?.StandardWidthParam) + " × חלק צבוע" + Param(result?.Rules.Crosswalk?.FillParam) +
                ", כמו \"/2×3\" בגיליון של נטלי, ובנוסף שטח ההצללה (משולשים / מלבן על קו העצירה) " +
                HatchAreaDisclosure(assemblies) +
                (fallbackCount > 0 ? $"; ב-{fallbackCount} מהם שטח ההצללה חושב מהפוליליין הסגור שבגבולה" : "") +
                "; קו השוליים הרציף של אותו מעבר לא נספר שוב — לאישור";
            return string.IsNullOrWhiteSpace(part.Part.Confirm) ? dynamic : part.Part.Confirm + "\n" + dynamic;
        }

        private static string? PartBrief(BoqPartResult part)
        {
            var assemblies = part.Crossings?.Where(c => c.WidthSource == "hatch-assembly").ToList();
            if (assemblies == null || assemblies.Count == 0) return part.Part.Brief;
            var label = part.Part.Label == "811 מעברי חציה — לפי גאומטריה (גיליון \"מעברי חציה\")" ? "811" : part.Part.Label;
            var dynamic = $"{label}: {assemblies.Count} מעברים עם הצללה ליד קו העצירה — זברה לפי הקו המקווקו × רוחב תקני × חלק צבוע, ובנוסף שטח ההצללה " +
                HatchAreaDisclosure(assemblies) + $" (גיליון \"מעברי חציה\" {CrossingReferences(part)}) — לאישור";
            return string.IsNullOrWhiteSpace(part.Part.Brief) ? dynamic : part.Part.Brief + "\n" + dynamic;
        }

        private static string HatchAreaDisclosure(IReadOnlyList<BoqCrossing> assemblies)
        {
            var known = assemblies.Where(c => c.HatchM2 != null).ToList();
            var missing = assemblies.Sum(c => c.MissingHatches) + assemblies.Count(c => c.HatchM2 == null && c.MissingHatches == 0);
            if (known.Count == 0) return "לא נמדד" + (missing > 0 ? $"; {missing} הצללות חסרות קריאת שטח" : "");
            return F2c(known.Sum(c => c.HatchM2!.Value)) + " מ\"ר" +
                (missing > 0 ? $" שנקראו בלבד; {missing} הצללות חסרות קריאת שטח (אין השלמת שטח)" : "");
        }

        private static string CrossingReferences(BoqPartResult part)
        {
            var numbers = (part.Crossings ?? Array.Empty<BoqCrossing>()).OrderBy(c => c.WidthSource, StringComparer.Ordinal)
                .ThenByDescending(c => Math.Round(c.Length ?? 0, 6)).Select((c, index) => (Crossing: c, Number: index + 1))
                .Where(x => x.Crossing.WidthSource == "hatch-assembly").Select(x => x.Number).ToList();
            return numbers.Count > 1 && numbers.Last() - numbers[0] == numbers.Count - 1
                ? $"#{numbers[0]}–#{numbers.Last()}" : string.Join(", ", numbers.Select(n => "#" + n));
        }

        private static string Quote(string text) => text.Replace("\"", "\"\"");

        /// <summary>
        /// A text constant for a formula: Excel refuses a single constant longer than 255 characters, so longer text is
        /// joined from quoted pieces of at most 200 characters (as LandQ's formula_text).
        /// </summary>
        internal static string FormulaText(string text)
        {
            const int chunk = 200;
            if (text.Length <= chunk) return "\"" + Quote(text) + "\"";
            var pieces = new List<string>();
            for (var i = 0; i < text.Length; i += chunk)
                pieces.Add("\"" + Quote(text.Substring(i, Math.Min(chunk, text.Length - i))) + "\"");
            return string.Join("&", pieces);
        }

        private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string F2(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        /// <summary>Python's "{:,.2f}".</summary>
        private static string F2c(double value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

        private static string Unit(string kind) => kind switch
        {
            "crosswalk_geo" or "area" or "hatch" => "מ\"ר",
            "length" => "מ'",
            _ => "יח'",
        };

        private static string MeasureUnit(string kind) => kind switch
        {
            "length" => "מ'",
            "area" => "מ\"ר",
            "count" => "יח'",
            _ => kind,
        };

        private static string Col(int index)
        {
            var name = "";
            while (index > 0)
            {
                name = (char)('A' + (index - 1) % 26) + name;
                index = (index - 1) / 26;
            }
            return name;
        }

        private static MiniXlsx.OutRow Row(MiniXlsx.Worksheet sheet, int index)
        {
            var row = new MiniXlsx.OutRow(index);
            sheet.Rows.Add(row);
            return row;
        }

        /// <summary>A text, or a styled blank cell when the text is empty (Excel keeps no empty text; neither does the reference).</summary>
        private static MiniXlsx.OutRow TextOrBlank(MiniXlsx.OutRow row, string col, string? text, int style) =>
            string.IsNullOrEmpty(text) ? row.Blank(col, style) : row.Text(col, text, style);

        private static void Notice(MiniXlsx.Worksheet sheet, int rowIndex, string lastColumn, string text, Styles s, double height)
        {
            var row = Row(sheet, rowIndex);
            row.HeightPoints = height;
            row.Text("A", text, s.Note);
            sheet.SingleRowMerges.Add($"A{rowIndex}:{lastColumn}{rowIndex}");
        }

        private static MiniXlsx.Worksheet NewSheet(string name, double[] widths, string[] head, Styles s)
        {
            var sheet = new MiniXlsx.Worksheet { SheetName = name, RightToLeft = true, PrintLandscapeFitToWidth = true, FreezeTopRows = 1, PrintTitleRows = 1 };
            for (var i = 0; i < widths.Length; i++) sheet.ColumnWidths.Add((i + 1, widths[i]));
            var row = Row(sheet, 1);
            for (var i = 0; i < head.Length; i++) row.Text(Col(i + 1), head[i], s.Head);
            return sheet;
        }

        private sealed class Styles
        {
            public readonly int Title, Sub, Head, Text, Latin, Num2, Num4, Count, Money, Input, InputNum2, Chapter, ChapterLatin,
                Ok, Assume, Choose, Warn, BoldText, BoldMoney, BoldNum2, Note, H12, T11, WarnText, WarnNum2;

            public Styles(MiniXlsx.Workbook wb)
            {
                int Add(MiniXlsx.Style style)
                {
                    wb.Styles.Add(style);
                    return wb.Styles.Count - 1;
                }
                const string head = "FFD9E1F2", input = "FFFFF2CC", ok = "FFE2EFDA", grey = "FFEDEDED", warn = "FFFCE4D6";
                Title = Add(new MiniXlsx.Style(Bold: true, FontSize: 13) { RightToLeft = true });
                // One line spilling over the empty row, like the title and the reference workbook. Wrapped
                // inside the 12-wide column A it became a tall column of single words (live 30.09.2026).
                Sub = Add(new MiniXlsx.Style(FontSize: 10) { RightToLeft = true });
                Head = Add(new MiniXlsx.Style(Bold: true, FillRgb: head, ThinTopBottomBorder: true, WrapText: true) { RightToLeft = true });
                Text = Add(new MiniXlsx.Style(ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                Latin = Add(new MiniXlsx.Style(ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                Num2 = Add(new MiniXlsx.Style(ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));
                Num4 = Add(new MiniXlsx.Style(ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands4, AlignTop: true));
                Count = Add(new MiniXlsx.Style(ThinTopBottomBorder: true, AlignTop: true));
                Money = Add(new MiniXlsx.Style(ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));
                Input = Add(new MiniXlsx.Style(FillRgb: input, ThinTopBottomBorder: true, AlignTop: true));
                InputNum2 = Add(new MiniXlsx.Style(FillRgb: input, ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));
                Chapter = Add(new MiniXlsx.Style(Bold: true, FillRgb: head, ThinTopBottomBorder: true, WrapText: true) { RightToLeft = true });
                ChapterLatin = Add(new MiniXlsx.Style(Bold: true, FillRgb: head, ThinTopBottomBorder: true) { AlignRight = true });
                Ok = Add(new MiniXlsx.Style(FillRgb: ok, ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                Assume = Add(new MiniXlsx.Style(FillRgb: input, ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                Choose = Add(new MiniXlsx.Style(FillRgb: grey, ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                Warn = Add(new MiniXlsx.Style(FillRgb: warn, ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                BoldText = Add(new MiniXlsx.Style(Bold: true, WrapText: true) { RightToLeft = true });
                BoldMoney = Add(new MiniXlsx.Style(Bold: true, ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2));
                BoldNum2 = Add(new MiniXlsx.Style(Bold: true, ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2));
                Note = Add(new MiniXlsx.Style(WrapText: true, AlignTop: true) { RightToLeft = true });
                H12 = Add(new MiniXlsx.Style(Bold: true, FontSize: 12, WrapText: true) { RightToLeft = true });
                T11 = Add(new MiniXlsx.Style(WrapText: true) { RightToLeft = true });
                WarnText = Add(new MiniXlsx.Style(FillRgb: warn, ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true });
                WarnNum2 = Add(new MiniXlsx.Style(FillRgb: warn, ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));
            }
        }
    }
}
