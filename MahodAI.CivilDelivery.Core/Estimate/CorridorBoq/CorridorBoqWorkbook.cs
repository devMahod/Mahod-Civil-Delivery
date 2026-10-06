using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq
{
    /// <summary>
    /// The corridor bill workbook (right-to-left, NTI format of Natali's example): 'כתב כמויות' (51.01–51.04, every quantity a
    /// formula), 'חישוב' (per corridor, formulas over the measurement), 'מדידה' (the tool's measured totals and the stripping
    /// depth parameter), 'השוואה לטבלאות' (the 'Road Volumes' tables found in the drawing, if any) and 'כיסוי' (skipped
    /// corridors, issues, evidence and the raw receipt). A corridor whose measurement is not complete is shown but not totalled.
    /// </summary>
    public static class CorridorBoqWorkbook
    {
        private static readonly string[] AsphaltOrder = { "ASF-5-19-70", "ASF-6-25-70", "ASF-7-25-68" };
        public const string SheetMeasure = "מדידה";
        public const string SheetCalc = "חישוב";
        public const string SheetCompare = "השוואה לטבלאות";
        public const string SheetCoverage = "כיסוי";
        /// <summary>Sheet-name prefix of the corridor sheets inside the combined bill (4 plan files + corridors).</summary>
        public const string CombinedPrefix = "קורידורים — ";

        /// <summary>Status of a line priced by a chosen estimating assumption (a joint Claude/Codex decision, named in the
        /// line's note) — not a measurement and not an approval.</summary>
        public const string Assumption = "הנחת אומדן";

        /// <summary>Status of a line whose item follows from the measured object itself (e.g. the asphalt layer's name).</summary>
        public const string Ready = "מוכן";

        /// <summary>A material line whose quantity in the corridors totalled by this run is 0 (Codex 22:57, 87A67B11): not "ready"
        /// and not a claim that the layer is absent from the whole project.</summary>
        public const string ZeroInRun = "כמות 0 בריצה זו";

        /// <summary>The scope note of a <see cref="ZeroInRun"/> line, naming the coverage sheet of this workbook: 'כיסוי' in the
        /// standalone corridor workbook, 'קורידורים — כיסוי' in the combined bill (Codex 23:56).</summary>
        public static string ZeroInRunNote(string sheetPrefix) =>
            "בסכום ריצה זו הכמות המחושבת לסעיף היא 0. אין בכך קביעה שהשכבה אינה קיימת בכל הפרויקט; " +
            $"הכיסוי והמקורות שלא נמדדו מפורטים בגיליון {sheetPrefix}{SheetCoverage}.";

        /// <summary>The nine cell styles of the corridor sheets, registered in one fixed order (standalone bytes depend on it).</summary>
        public sealed record SheetStyles(int Title, int Sub, int Head, int Chap, int Text, int Code, int Num, int NumB, int Warn);

        public static SheetStyles AddStyles(MiniXlsx.Workbook wb)
        {
            ArgumentNullException.ThrowIfNull(wb);
            int Add(MiniXlsx.Style s) { wb.Styles.Add(s); return wb.Styles.Count - 1; }
            return new SheetStyles(
                Add(new MiniXlsx.Style(Bold: true, FontSize: 13) { RightToLeft = true }),
                Add(new MiniXlsx.Style(FontSize: 9) { RightToLeft = true }),
                Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFD9E1F2", ThinTopBottomBorder: true, WrapText: true) { RightToLeft = true }),
                Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFEDEDED", ThinTopBottomBorder: true) { RightToLeft = true }),
                Add(new MiniXlsx.Style(ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true }),
                Add(new MiniXlsx.Style(ThinTopBottomBorder: true, AlignTop: true) { AlignRight = true }),
                Add(new MiniXlsx.Style(ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true)),
                Add(new MiniXlsx.Style(Bold: true, ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2)),
                Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", ThinTopBottomBorder: true, WrapText: true, AlignTop: true) { RightToLeft = true }));
        }

        /// <summary>One bill line of the corridor chapters 51.01–51.04: a chapter heading (ChapterTitle set) or an item row
        /// whose quantity is a formula over the supporting sheets; NotMeasured = the domain was measured in no corridor
        /// ('לא נמדד', never a formula 0); a null Quantity on a measured item = not verified ('לא אומת', unpriced).</summary>
        public sealed record BillLine(string Code, string? ChapterTitle, string? Quantity, string Status, string Note, bool NotMeasured,
            string Description, string Unit, decimal? Price);

        /// <summary>The corridor chapters for a bill: the lines, whether any corridor is totalled per domain, the open items,
        /// and the four supporting sheets (not yet attached to any workbook).</summary>
        public sealed record Section(IReadOnlyList<BillLine> Lines, bool AnyEarth, bool AnyMaterial, IReadOnlyList<string> Open,
            IReadOnlyList<MiniXlsx.Worksheet> Sheets);

        private static string Col(int i) { var s = ""; for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s; return s; }
        private static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string Name(string corridorId) => corridorId.Contains(" [") ? corridorId[..corridorId.IndexOf(" [", StringComparison.Ordinal)] : corridorId;

        /// <summary>The corridor name as displayed in a right-to-left sheet: isolated left-to-right so "2000-DES" never reads
        /// "DES-2000" (Codex 22:57). Display only — <see cref="Name"/> stays the matching key.</summary>
        private static string DisplayName(string corridorId) => Bidi.Ltr(Name(corridorId));

        /// <summary>The quantity the calc sheet's total row gives a material code: the code's plan area (asphalt) or volume
        /// (bases) summed over the corridors whose materials are fully measured. Zero only when at least one corridor is
        /// totalled and none of them has the code.</summary>
        private static bool ZeroInTotalledRun(CorridorBoqExport.Export export, string code, bool area)
        {
            var totalled = export.Measures.Where(m => m.MaterialsComplete).ToList();
            return totalled.Count > 0 && totalled.Sum(m => (area ? m.CodePlanArea : m.CodeVolume).GetValueOrDefault(code)) == 0;
        }

        public static MiniXlsx.Workbook Create(CorridorBoqExport.Export export, CorridorBoqExport.Context ctx)
        {
            var r = export.Rules;
            var wb = new MiniXlsx.Workbook { SheetName = "כתב כמויות", RightToLeft = true, PrintLandscapeFitToWidth = true };
            var st = AddStyles(wb);
            var (title, sub, head, chap, text, code, num, numB, warn) = (st.Title, st.Sub, st.Head, st.Chap, st.Text, st.Code, st.Num, st.NumB, st.Warn);
            var section = BuildSheets(export, ctx, st, "");

            // ---------------- 'כתב כמויות' ----------------
            wb.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "כתב כמויות — עפר, מצעים ואספלט מקורידורים — טיוטה", title));
            wb.Rows.Add(new MiniXlsx.OutRow(2).Text("A",
                $"נמדד בכלי מהשרטוט {System.IO.Path.GetFileName(ctx.DrawingPath)} ({ctx.Now:dd.MM.yyyy HH:mm}). מחירים: {r.Pricebook.Name} {r.Pricebook.Edition} (מחיר בסיס). " +
                "טיוטה לבדיקה הנדסית — הנחות מסומנות \"לאישור\"; לא אומדן מאושר.", sub));
            var bh = new MiniXlsx.OutRow(4);
            var bHead = new[] { "מספר", "תאור", "יח'", "כמות", "מחיר", "סה\"כ", "מצב", "הערות" };
            for (var j = 0; j < bHead.Length; j++) bh.Text(Col(j), bHead[j], head);
            wb.Rows.Add(bh);
            var row = 5;
            var first = row;
            foreach (var l in section.Lines)
            {
                if (l.ChapterTitle != null)
                {
                    wb.Rows.Add(new MiniXlsx.OutRow(row).Text("A", l.Code, chap).Text("B", l.ChapterTitle, chap).Blank("C", chap).Blank("D", chap)
                        .Blank("E", chap).Blank("F", chap).Blank("G", chap).Blank("H", chap));
                    row++;
                    continue;
                }
                var line = new MiniXlsx.OutRow(row).Text("A", l.Code, code).Text("B", l.Description, text).Text("C", l.Unit, text);
                if (l.Quantity == null)
                    line.Text("D", l.NotMeasured ? "לא נמדד" : "לא אומת", warn).Number("E", l.Price ?? 0m, num).Text("F", "—", text);
                else
                    line.Formula("D", l.Quantity, num).Number("E", l.Price ?? 0m, num).Formula("F", $"ROUND(D{row}*E{row},2)", num);
                wb.Rows.Add(line.Text("G", l.Status, text).Text("H", l.Note, l.Status is "לאישור" or Assumption or ZeroInRun ? warn : text));
                row++;
            }
            var last = row - 1;
            row++;
            var totalRow = new MiniXlsx.OutRow(row).Text("B", "סה\"כ טיוטה (ללא מע\"מ) — רק תחומים שנמדדו במלואם (ראו 'מה עוד פתוח')", head);
            if (section.AnyEarth || section.AnyMaterial) totalRow.Formula("F", $"SUM(F{first}:F{last})", numB);
            else totalRow.Text("F", "—", warn);
            wb.Rows.Add(totalRow);
            row += 2;
            foreach (var t in section.Open) wb.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", t, sub));
            wb.ColumnWidths.AddRange(new[] { (1, 12.0), (2, 58.0), (3, 7.0), (4, 13.0), (5, 10.0), (6, 14.0), (7, 9.0), (8, 70.0) });
            wb.PrintTitleRows = 4;
            foreach (var sheet in section.Sheets) wb.AdditionalSheets.Add(sheet);
            return wb;
        }

        /// <summary>The corridor measurement / calculation / comparison / coverage sheets (names prefixed by
        /// <paramref name="sheetPrefix"/>) and the bill lines of chapters 51.01–51.04 as formulas over them. The styles must
        /// belong to the workbook the sheets will be attached to.</summary>
        public static Section BuildSheets(CorridorBoqExport.Export export, CorridorBoqExport.Context ctx, SheetStyles st, string sheetPrefix)
        {
            ArgumentNullException.ThrowIfNull(export);
            ArgumentNullException.ThrowIfNull(ctx);
            ArgumentNullException.ThrowIfNull(st);
            sheetPrefix ??= "";
            var r = export.Rules;
            var (title, sub, head, text, code, num, numB, warn) = (st.Title, st.Sub, st.Head, st.Text, st.Code, st.Num, st.NumB, st.Warn);
            var M = $"'{sheetPrefix}{SheetMeasure}'!";
            var C = $"'{sheetPrefix}{SheetCalc}'!";

            // ---------------- 'מדידה' (numbers measured by the tool) ----------------
            var ms = new MiniXlsx.Worksheet { SheetName = sheetPrefix + SheetMeasure, RightToLeft = true };
            ms.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "מדידה מהשרטוט — כל קורידור: חפירה ומילוי מול הקרקע הקיימת לפני חישוף ואחריו, רוחבי חפירה/מילוי בתוכנית ובתלת-ממד, ונפחים ושטחים לכל שכבה", title));
            ms.Rows.Add(new MiniXlsx.OutRow(2).Text("A", r.MethodText, sub));
            ms.Rows.Add(new MiniXlsx.OutRow(3).Text("A", "עומק חישוף (מ')", head).Number("C", r.HisufDepthM, num).Text("D", r.HisufNote, warn));
            // The depth the tool computed the earthworks after stripping with: they are not linear in the depth, so a different
            // value in C3 turns the earthworks formulas into #N/A (measure again) instead of showing a stale quantity.
            var computedDepth = export.Measures.Select(m => m.StripDepthM).DefaultIfEmpty(r.HisufDepthM).First();
            ms.Rows.Add(new MiniXlsx.OutRow(4).Text("A", "עומק החישוב בכלי (מ')", head).Number("C", computedDepth, num)
                .Text("D", $"החפירה והמילוי אחרי החישוף חושבו בכלי בעומק זה (שיטה: {export.Measures.Select(m => m.StripMethodId).FirstOrDefault(id => id != null) ?? "—"}). " +
                           "עומק אחר בתא C3 מבטל את כמויות העפר (#N/A) — יש למדוד מחדש בכלי.", warn));
            var codes = AsphaltOrder.Concat(r.Codes.Values.Where(c => c.Kind == "base").Select(c => c.Code)).Where(r.Codes.ContainsKey).ToList();
            var mHead = new List<string> { "קורידור", "עפר נכלל בסה\"כ (1 = מדידה מלאה)", "אורך כיסוי עפר (מ׳)", "נפח חפירה (מ\"ק)", "נפח מילוי (מ\"ק)",
                "שטח חפירה בתוכנית (מ\"ר)", "שטח מילוי בתוכנית (מ\"ר)", "שטח חפירה תלת-ממדי (מ\"ר)", "שטח מילוי תלת-ממדי (מ\"ר)" };
            mHead.AddRange(codes.Select(c => $"{c} — נפח (מ\"ק)"));
            mHead.AddRange(codes.Select(c => $"{c} — שטח בתוכנית (מ\"ר)"));
            mHead.Add("שטח האספלט — איחוד כל השכבות (מ\"ר)");
            mHead.Add("סדר ומגע אנכי בין שכבות האספלט");
            mHead.Add("מצעים ואספלט נכללים בסה\"כ (1 = מדידה מלאה)");
            mHead.Add("נפח חישוף (מ\"ק)");
            mHead.Add("חפירה אחרי חישוף (מ\"ק)");
            mHead.Add("מילוי אחרי חישוף (מ\"ק)");
            var unionCol = 9 + 2 * codes.Count;
            var matIncCol = 11 + 2 * codes.Count;
            var stripCol = 12 + 2 * codes.Count;
            var postCutCol = 13 + 2 * codes.Count;
            var postFillCol = 14 + 2 * codes.Count;
            var hr = new MiniXlsx.OutRow(5); for (var j = 0; j < mHead.Count; j++) hr.Text(Col(j), mHead[j], head); ms.Rows.Add(hr);
            // The depth notes span the table width instead of wrapping inside one narrow column.
            ms.SingleRowMerges.Add($"D3:{Col(mHead.Count - 1)}3");
            ms.SingleRowMerges.Add($"D4:{Col(mHead.Count - 1)}4");
            var mRow = new Dictionary<string, int>(StringComparer.Ordinal);
            var row = 6;
            foreach (var m in export.Measures)
            {
                mRow[m.CorridorId] = row;
                var earthMeasured = m.LengthM > 0;
                var materialMeasured = m.CodeVolume.Count > 0 || m.CodePlanArea.Count > 0;
                var o = new MiniXlsx.OutRow(row).Text("A", DisplayName(m.CorridorId), text).Number("B", m.EarthworksComplete ? 1.0 : 0.0, num);
                void Ew(string col, double v) { if (earthMeasured) o.Number(col, v, num); else o.Text(col, "לא נמדד", warn); }
                void Mat(string col, double v) { if (materialMeasured) o.Number(col, v, num); else o.Text(col, "לא נמדד", warn); }
                Ew("C", m.LengthM); Ew("D", m.VolCut); Ew("E", m.VolFill); Ew("F", m.PlanCut); Ew("G", m.PlanFill);
                Ew("H", m.Plan3DCut); Ew("I", m.Plan3DFill);
                for (var k = 0; k < codes.Count; k++)
                {
                    Mat(Col(9 + k), m.CodeVolume.GetValueOrDefault(codes[k]));
                    Mat(Col(9 + codes.Count + k), m.CodePlanArea.GetValueOrDefault(codes[k]));
                }
                Mat(Col(9 + 2 * codes.Count), m.AsphaltPlanArea);
                o.Text(Col(10 + 2 * codes.Count), m.InterfaceStatus switch
                {
                    "consistent" => "תקין — השכבות מונחות זו על זו",
                    "failed" => "נמצא פער או היפוך בין שכבות",
                    "not_applicable" => "אין שכבות חופפות",
                    _ => "לא הוכרע",
                }, m.InterfaceStatus == "consistent" || m.InterfaceStatus == "not_applicable" ? text : warn);
                o.Number(Col(11 + 2 * codes.Count), m.MaterialsComplete ? 1.0 : 0.0, num);
                void Post(int col, double? v) { if (v is { } value) o.Number(Col(col), value, num); else o.Text(Col(col), "לא נמדד", warn); }
                Post(stripCol, m.StripDebit); Post(postCutCol, m.PostCut); Post(postFillCol, m.PostFill);
                ms.Rows.Add(o);
                row++;
            }
            ms.ColumnWidths.Add((1, 18)); for (var j = 2; j <= mHead.Count; j++) ms.ColumnWidths.Add((j, 14));

            // ---------------- 'חישוב' (formulas over the measurement) ----------------
            var cs = new MiniXlsx.Worksheet { SheetName = sheetPrefix + SheetCalc, RightToLeft = true };
            var cHead = new[] { "קורידור", "שטח חישוף (מ\"ר)", "נפח חישוף (מ\"ק)", "בדיקת מאזן: (חפירה − מילוי) לפני − חישוף − (חפירה − מילוי) אחרי (מ\"ק)",
                "חפירה אחרי חישוף (מ\"ק)", "מילוי אחרי חישוף (מ\"ק)", "אספלט 5 ס\"מ (מ\"ר)", "אספלט 6 ס\"מ (מ\"ר)", "אספלט 7 ס\"מ (מ\"ר)", "שטח האספלט — ציפוי יסוד (מ\"ר)",
                "ציפוי מאחה (מ\"ר)", "MAZA (מ\"ק)", "S5 (מ\"ק)" };
            var ch = new MiniXlsx.OutRow(1); for (var j = 0; j < cHead.Length; j++) ch.Text(Col(j), cHead[j], head); cs.Rows.Add(ch);
            // N(): a 'לא נמדד' cell reads as 0 inside a formula that is already excluded from the totals (× 0).
            string MC(string corridor, int col) => $"N({M}{Col(col)}{mRow[corridor]})";
            int CodeCol(string c, bool area) { var k = codes.IndexOf(c); return k < 0 ? -1 : 9 + (area ? codes.Count : 0) + k; }
            row = 2;
            foreach (var m in export.Measures)
            {
                var id = m.CorridorId;
                var inc = MC(id, 1);
                var incM = MC(id, matIncCol);
                string AreaOf(string c) => CodeCol(c, true) < 0 ? "0" : MC(id, CodeCol(c, true));
                string VolOf(string c) => CodeCol(c, false) < 0 ? "0" : MC(id, CodeCol(c, false));
                // Earthworks after stripping come from the tool at the computed depth (C4); another depth in C3 gives #N/A.
                string AtDepth(string value) => $"IF({M}$C$3={M}$C$4,{value},NA())";
                var partial = !m.EarthworksComplete && !m.MaterialsComplete ? " — לא בסה\"כ (מדידה חלקית)"
                    : !m.EarthworksComplete ? " — עפר לא בסה\"כ (מדידה חלקית)" : !m.MaterialsComplete ? " — מצעים ואספלט לא בסה\"כ (מדידה חלקית)" : "";
                cs.Rows.Add(new MiniXlsx.OutRow(row).Text("A", DisplayName(id) + partial, partial.Length == 0 ? text : warn)
                    .Formula("B", $"({MC(id, 5)}+{MC(id, 6)})*{inc}", num)
                    .Formula("C", AtDepth($"{MC(id, stripCol)}*{inc}"), num)
                    .Formula("D", AtDepth($"({MC(id, 3)}-{MC(id, 4)}-{MC(id, stripCol)}-({MC(id, postCutCol)}-{MC(id, postFillCol)}))*{inc}"), num)
                    .Formula("E", AtDepth($"{MC(id, postCutCol)}*{inc}"), num)
                    .Formula("F", AtDepth($"{MC(id, postFillCol)}*{inc}"), num)
                    .Formula("G", $"{AreaOf(AsphaltOrder[0])}*{incM}", num)
                    .Formula("H", $"{AreaOf(AsphaltOrder[1])}*{incM}", num)
                    .Formula("I", $"{AreaOf(AsphaltOrder[2])}*{incM}", num)
                    .Formula("J", $"{MC(id, unionCol)}*{incM}", num)
                    .Formula("K", $"G{row}+H{row}+I{row}-J{row}", num)
                    .Formula("L", $"{VolOf("MAZA")}*{incM}", num)
                    .Formula("M", $"{VolOf("S5")}*{incM}", num));
                row++;
            }
            var tot = row;
            var tr = new MiniXlsx.OutRow(tot).Text("A", "סה\"כ (מדידה מלאה בלבד)", head);
            for (var j = 1; j < cHead.Length; j++) tr.Formula(Col(j), export.Measures.Count == 0 ? "0" : $"SUM({Col(j)}2:{Col(j)}{tot - 1})", numB);
            cs.Rows.Add(tr);
            cs.Rows.Add(new MiniXlsx.OutRow(tot + 1).Text("A", "לכל קורידור: עפר נכנס לסה\"כ רק ממדידת עפר מלאה; מצעים ואספלט רק ממדידת חומרים מלאה.", sub));
            cs.ColumnWidths.Add((1, 34)); for (var j = 2; j <= cHead.Length; j++) cs.ColumnWidths.Add((j, 15));
            string T(string col) => $"{C}{col}{tot}";

            // ---------------- bill lines (chapters 51.01–51.04) ----------------
            var pb = r.Pricebook.Items;
            var lines = new List<(string Code, string? Chapter, string? Qty, string Status, string Note)>
            {
                ("51.01.0000", "עבודות הכנה", null, "", ""),
                (r.Items["strip"], null, T("B"), Assumption, $"הוכרע (הנחת אומדן): חישוף וסילוק, בלי זיכוי עירום/שימוש חוזר. שטח חישוף = שטח החפירה + המילוי בתוכנית; עומק החישוף בגיליון '{sheetPrefix}{SheetMeasure}' (C3). חלופה: {r.Items["strip_alternative"]} (חישוף ועירום זמני)"),
                ("51.02.0000", "עבודות עפר", null, "", ""),
                (r.Items["cut_reuse"], null, "0", Assumption, r.Assumptions["cut_reuse"]),
                (r.Items["cut_remove"], null, T("E"), Assumption, $"כל החפירה אחרי החישוף — לסילוק, לפי ההכרעה בשורת {r.Items["cut_reuse"]}. " + r.Assumptions["post_strip"]),
                (r.Items["fill_imported"], null, T("F"), Assumption, $"כל המילוי אחרי החישוף — מובא, לפי ההכרעה בשורת {r.Items["cut_reuse"]}. " + r.Assumptions["post_strip"]),
                ("51.03.0000", "שכבות מצע ותשתיות אגו\"מ", null, "", ""),
            };
            if (r.Codes.TryGetValue("MAZA", out var maza)) lines.Add((maza.Item, null, T("L"), Assumption, maza.Note ?? ""));
            if (r.Codes.TryGetValue("S5", out var s5)) lines.Add((s5.Item, null, T("M"), Assumption, s5.Note ?? ""));
            lines.Add(("51.04.0000", "שכבות אספלטיות במיסעות", null, "", ""));
            string[] asfCol = { "G", "H", "I" };
            for (var k = 0; k < AsphaltOrder.Length; k++)
                if (r.Codes.TryGetValue(AsphaltOrder[k], out var a))
                {
                    var asphaltNote = $"{a.Code} — שטח בתוכנית מרוחב השכבה בכל חתך של הקורידור. הוכרע: הסעיף לפי שם השכבה (עובי {a.ThicknessM * 100:0} ס\"מ, אגרגט ו-PG כמו בשם) — מיפוי אומדן, לא בדיקת מעבדה";
                    var zero = ZeroInTotalledRun(export, AsphaltOrder[k], area: true);
                    lines.Add((a.Item, null, T(asfCol[k]), zero ? ZeroInRun : Ready, zero ? asphaltNote + " " + ZeroInRunNote(sheetPrefix) : asphaltNote));
                }
            var interfaces = export.Measures.Where(m => m.MaterialsComplete).Select(m => m.InterfaceStatus).Distinct().ToList();
            // Priced only with positive evidence: at least one corridor with checked contact and none failed / unknown.
            var tackVerified = interfaces.Contains("consistent") && interfaces.All(s => s is "consistent" or "not_applicable");
            var tackNone = interfaces.Count > 0 && interfaces.All(s => s == "not_applicable");
            var tackEvidence = tackVerified
                ? "נבדק גאומטרית בכל החתכים שנמדדו: השכבות מונחות זו על זו ונוגעות זו בזו (סדר ומגע אנכי). תחולת ציפוי מאחה — לאישור הנדסי; הבדיקה הגאומטרית אינה אישור."
                : tackNone ? "אין שכבות אספלט חופפות בחתכים שנמדדו — אין ציפוי מאחה."
                : $"לא מתומחר: סדר / מגע אנכי בין השכבות לא אומת (גיליון '{sheetPrefix}{SheetMeasure}'); הכמות בגיליון '{sheetPrefix}{SheetCalc}' לבדיקה ולאישור.";
            lines.Add((r.Items["prime_coat"], null, T("J"), Assumption, "שטח האספלט = איחוד תחומי כל שכבות האספלט בכל חתך. " + r.Assumptions["coats"]));
            lines.Add((r.Items["tack_coat"], null, tackVerified || tackNone ? T("K") : null, tackVerified || tackNone ? Assumption : "לאישור",
                "סכום שטחי השכבות פחות שטח האספלט. " + tackEvidence));
            var anyEarth = export.Measures.Any(m => m.EarthworksComplete);
            var anyMaterial = export.Measures.Any(m => m.MaterialsComplete);
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Chapter == null && lines[i].Status == Assumption &&
                    ((r.Codes.TryGetValue("MAZA", out var mz) && lines[i].Code == mz.Item && ZeroInTotalledRun(export, "MAZA", area: false)) ||
                     (r.Codes.TryGetValue("S5", out var sv) && lines[i].Code == sv.Item && ZeroInTotalledRun(export, "S5", area: false))))
                    lines[i] = lines[i] with { Status = ZeroInRun, Note = lines[i].Note + " " + ZeroInRunNote(sheetPrefix) };
            var earthItems = new HashSet<string> { r.Items["strip"], r.Items["cut_reuse"], r.Items["cut_remove"], r.Items["fill_imported"] };
            var billLines = new List<BillLine>();
            foreach (var (itemCode, chapter, qtyIn, status, note) in lines)
            {
                if (chapter != null) { billLines.Add(new BillLine(itemCode, chapter, null, "", "", false, "", "", null)); continue; }
                var item = pb[itemCode];
                var notMeasured = earthItems.Contains(itemCode) ? !anyEarth : !anyMaterial;
                billLines.Add(new BillLine(itemCode, null, notMeasured ? null : qtyIn, status, note, notMeasured, item.Description, item.Unit, item.BasePrice));
            }
            var open = new List<string> { "מה עוד פתוח (לפני אומדן סופי):" };
            foreach (var m in export.Measures.Where(m => !m.EarthworksComplete || !m.MaterialsComplete))
            {
                var missing = !m.EarthworksComplete && !m.MaterialsComplete ? "עפר, מצעים ואספלט" : !m.EarthworksComplete ? "עבודות העפר" : "המצעים והאספלט";
                open.Add($"• {DisplayName(m.CorridorId)} — מדידת {missing} לא מלאה ולכן לא נכללה בסה\"כ ({m.Issues.Count(i => !i.StartsWith("note ", StringComparison.Ordinal))} ממצאים, ראו '{sheetPrefix}{SheetCoverage}').");
            }
            foreach (var (c, reason) in export.Skipped) open.Add($"• {DisplayName(c)} — לא נמדד: {reason}");
            open.Add($"• הנחות אומדן שהוכרעו (מסומנות 'הנחת אומדן' בשורות, לא מדידה ולא אישור הנדסי): עומק חישוף {computedDepth:0.00} מ' ושיטת החישוף המקומית, " +
                     "חישוף וסילוק, בלי זיכוי שימוש בחפירה למילוי, MAZA כמצע א', S5 כמצע ג'. שינוי עומק — מדידה מחדש בכלי.");
            if (export.DrawingTables.Count > 0) open.Add($"• השוואה לטבלאות Road Volumes שבשרטוט — גיליון '{sheetPrefix}{SheetCompare}'.");

            // ---------------- 'השוואה לטבלאות' ----------------
            var vs = new MiniXlsx.Worksheet { SheetName = sheetPrefix + SheetCompare, RightToLeft = true };
            vs.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "המדידה של הכלי מול טבלאות 'Road Volumes' שבשרטוט (אותה שיטה). הפרש גדול = תחום אחר או טבלה שחושבה על מצב קודם של הקורידור", title));
            var vh = new MiniXlsx.OutRow(3); string[] vHead = { "טבלה (מזהה)", "כביש", "עמודה", "בטבלה", "מדידת הכלי", "הפרש", "הפרש %" };
            for (var j = 0; j < vHead.Length; j++) vh.Text(Col(j), vHead[j], head); vs.Rows.Add(vh);
            row = 4;
            foreach (var t in export.DrawingTables)
            {
                var m = export.Measures.FirstOrDefault(x => string.Equals(Name(x.CorridorId), t.Road, StringComparison.OrdinalIgnoreCase));
                if (m == null) continue;
                var map = new List<(string Column, string Formula, bool Available)>
                {
                    ("Length, m", MC(m.CorridorId, 2), m.EarthworksComplete),
                    ("Plan Area Cut, sq.m", MC(m.CorridorId, 5), m.EarthworksComplete),
                    ("Plan Area Fill, sq.m", MC(m.CorridorId, 6), m.EarthworksComplete),
                    ("Volume Cut, cu.m", MC(m.CorridorId, 3), m.EarthworksComplete),
                    ("Volume Fill, cu.m", MC(m.CorridorId, 4), m.EarthworksComplete),
                    ("Hisuf Cut, cu.m", $"{MC(m.CorridorId, 7)}*{M}$C$3", m.EarthworksComplete),
                    ("Hisuf Fill, cu.m", $"{MC(m.CorridorId, 8)}*{M}$C$3", m.EarthworksComplete),
                };
                foreach (var c in codes) if (CodeCol(c, false) >= 0) map.Add(($"{c}, cu.m", MC(m.CorridorId, CodeCol(c, false)), m.MaterialsComplete));
                foreach (var (column, formula, available) in map)
                {
                    if (!t.Values.TryGetValue(column, out var v)) continue;
                    var comparisonRow = new MiniXlsx.OutRow(row).Text("A", t.Handle, code).Text("B", Bidi.Ltr(t.Road), text).Text("C", column, code).Number("D", v, num);
                    if (available)
                        comparisonRow.Formula("E", formula, num).Formula("F", $"E{row}-D{row}", num)
                            .Formula("G", $"IF(D{row}=0,0,F{row}/D{row}*100)", num);
                    else
                        comparisonRow.Text("E", "לא נמדד במלואו", warn).Text("F", "—", text).Text("G", "—", text);
                    vs.Rows.Add(comparisonRow);
                    row++;
                }
            }
            if (row == 4) vs.Rows.Add(new MiniXlsx.OutRow(4).Text("A", "לא נמצאו בשרטוט טבלאות Road Volumes של קורידור שנמדד.", sub));
            vs.ColumnWidths.AddRange(new[] { (1, 12.0), (2, 12.0), (3, 24.0), (4, 14.0), (5, 14.0), (6, 12.0), (7, 10.0) });

            // ---------------- 'כיסוי' ----------------
            var cv = new MiniXlsx.Worksheet { SheetName = sheetPrefix + SheetCoverage, RightToLeft = true };
            row = 1;
            cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", "כיסוי המדידה וראיות", title));
            cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", $"שרטוט: {ctx.DrawingPath} · SHA-256 {ctx.DrawingSha256}", sub));
            cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", $"נתוני גלם לכל חתך: {System.IO.Path.GetFileName(ctx.RawReceiptPath)} · SHA-256 {ctx.RawReceiptSha256}", sub));
            cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", $"כללים: פרויקט {r.Project} קורידורים v{r.Version}; קוד תחתית '{r.LinkCode}', משטח קרקע קיימת '{r.ExistingGroundSurface}'; מחירון {r.Pricebook.Id} (SHA-256 {r.Pricebook.SourceSha256})", sub));
            foreach (var e in export.Evidence) cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", e, sub));
            row++;
            foreach (var m in export.Measures)
            {
                cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", $"{DisplayName(m.CorridorId)}: עפר — {(m.EarthworksComplete ? "מלא, בסה\"כ" : "חלקי, לא בסה\"כ")} · מצעים ואספלט — {(m.MaterialsComplete ? "מלא, בסה\"כ" : "חלקי, לא בסה\"כ")} · {m.Stations} תחנות · אורך כיסוי עפר {m.LengthM:N2} מ׳", m.Complete ? text : warn));
                foreach (var i in m.Issues.Take(200)) cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", "   " + i, sub));
                if (m.Issues.Count > 200) cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", $"   … ועוד {m.Issues.Count - 200} ממצאים (בקובץ הגלם)", sub));
            }
            foreach (var (c, reason) in export.Skipped) cv.Rows.Add(new MiniXlsx.OutRow(row++).Text("A", $"{DisplayName(c)}: לא נמדד — {reason}", warn));
            cv.ColumnWidths.Add((1, 160));

            return new Section(billLines, anyEarth, anyMaterial, open, new[] { cs, ms, vs, cv });
        }
    }
}
