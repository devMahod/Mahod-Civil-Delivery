using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// A deliberately small xlsx codec with NO external dependency.
    ///
    /// Why it exists: an xlsx is a zip of XML, and the estimate needs only cells,
    /// strings, numbers, formulas, a few styles and column widths. The OpenXml SDK
    /// delivered that - until it was loaded inside Civil 3D, where Autodesk ships its
    /// own (older) DocumentFormat.OpenXml.dll and other plugins ship newer ones. The
    /// host's copy wins, the types no longer match what we compiled against, and the
    /// first generic call throws ("type argument 'Sheet' violates the constraint") -
    /// found live on 6422, 2026-08-19. A reader/writer built on System.IO.Compression
    /// and System.Xml cannot be hijacked by anything the host loads.
    ///
    /// Scope is intentionally narrow: worksheets, shared + inline strings,
    /// numbers, formulas, cell styles by index, column widths, right-to-left view.
    ///
    /// Every formula is written WITH its cached value (&lt;v&gt;, t="str" for text, t="b" for
    /// TRUE/FALSE), computed at write time by <see cref="XlsxFormulaEvaluator"/>. Without it Teams
    /// preview, Excel Protected View and phone viewers show empty quantities (Natali, 29.09.2026).
    /// A formula the evaluator cannot compute exactly as Excel would (error, unsupported syntax or
    /// function, cycle, or reading such a cell) is written without a value, as before; Excel still
    /// recalculates everything on open (fullCalcOnLoad).
    /// </summary>
    public static class MiniXlsx
    {
        private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string NsCt = "http://schemas.openxmlformats.org/package/2006/content-types";

        // ================================================================== READ

        /// <summary>A cell as read: column letters, row number, text as displayed.</summary>
        public readonly record struct CellText(string Column, int Row, string Text, string DataType = "")
        {
            /// <summary>True for an OOXML numeric/formula cached value, not text that merely looks numeric.</summary>
            public bool IsNumeric => string.IsNullOrEmpty(DataType);
        }

        /// <summary>
        /// Reads the first worksheet: rows in file order, each a list of (column, text).
        /// Shared strings, inline strings, numbers and booleans all come back as text;
        /// formulas come back as their cached value (what Excel shows).
        /// </summary>
        public static List<List<CellText>> ReadFirstSheet(string xlsxPath)
        {
            using var zip = ZipFile.OpenRead(xlsxPath);
            return ReadFirstSheet(zip);
        }

        /// <summary>
        /// Reads the first worksheet from one immutable workbook byte snapshot.
        /// Callers that attach a SHA-256 identity to parsed content must use this
        /// overload so hashing and parsing cannot observe two different path versions.
        /// </summary>
        public static List<List<CellText>> ReadFirstSheet(byte[] workbookBytes)
        {
            ArgumentNullException.ThrowIfNull(workbookBytes);
            using var stream = new MemoryStream(workbookBytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return ReadFirstSheet(zip);
        }

        /// <summary>Worksheet names from one immutable byte snapshot, in workbook order.</summary>
        public static IReadOnlyList<string> ReadSheetNames(byte[] workbookBytes)
        {
            ArgumentNullException.ThrowIfNull(workbookBytes);
            using var stream = new MemoryStream(workbookBytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return SheetEntries(zip).Select(s => s.Name).ToArray();
        }

        /// <summary>One exact named worksheet from the same bytes used for source identity; no first-sheet fallback.</summary>
        public static List<List<CellText>> ReadSheet(byte[] workbookBytes, string sheetName)
        {
            ArgumentNullException.ThrowIfNull(workbookBytes);
            ArgumentNullException.ThrowIfNull(sheetName);
            using var stream = new MemoryStream(workbookBytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var matches = SheetEntries(zip).Where(s => string.Equals(s.Name, sheetName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected exactly one worksheet named '{sheetName}', found {matches.Length}.");
            return ReadSheet(zip, matches[0].Path, ReadSharedStrings(zip));
        }

        private static List<List<CellText>> ReadFirstSheet(ZipArchive zip)
        {
            var shared = ReadSharedStrings(zip);
            var first = SheetPaths(zip).FirstOrDefault()
                        ?? throw new InvalidOperationException("workbook has no sheets");
            return ReadSheet(zip, first, shared);
        }

        /// <summary>Every worksheet in workbook order, each as rows of cells.</summary>
        public static List<List<List<CellText>>> ReadAllSheets(string xlsxPath)
        {
            using var zip = ZipFile.OpenRead(xlsxPath);
            var shared = ReadSharedStrings(zip);
            return SheetPaths(zip).Select(p => ReadSheet(zip, p, shared)).ToList();
        }

        private static List<List<CellText>> ReadSheet(ZipArchive zip, string sheetPath, List<string> shared)
        {
            var entry = zip.GetEntry(sheetPath)
                        ?? throw new InvalidOperationException($"worksheet part not found: {sheetPath}");

            var rows = new List<List<CellText>>();
            using var s = entry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });

            int lastRow = 0;
            // Advance to each <row>; ReadOuterXml consumes it whole and positions the
            // reader on the next sibling, so no further Read() is needed for that row.
            while (!xr.EOF)
            {
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "row")
                {
                    if (!xr.Read()) break;
                    continue;
                }

                var rowDoc = new XmlDocument();
                rowDoc.LoadXml(xr.ReadOuterXml());   // reader now sits AFTER this row
                var rowEl = rowDoc.DocumentElement!;
                var rowNum = int.TryParse(rowEl.GetAttribute("r"), out var rn) ? rn : lastRow + 1;
                lastRow = rowNum;

                var cells = new List<CellText>();
                foreach (XmlNode node in rowEl.ChildNodes)
                {
                    if (node is not XmlElement c || c.LocalName != "c") continue;
                    var reference = c.GetAttribute("r");
                    var type = c.GetAttribute("t");
                    var col = new string(reference.TakeWhile(char.IsLetter).ToArray());

                    string? v = null, isText = null;
                    foreach (XmlNode child in c.ChildNodes)
                    {
                        if (child is not XmlElement ce) continue;
                        if (ce.LocalName == "v") v = ce.InnerText;
                        else if (ce.LocalName == "is")
                        {
                            var sb = new StringBuilder();
                            foreach (XmlNode t in ce.GetElementsByTagName("t", NsMain)) sb.Append(t.InnerText);
                            if (sb.Length == 0) foreach (XmlNode t in ce.GetElementsByTagName("t")) sb.Append(t.InnerText);
                            isText = sb.ToString();
                        }
                    }

                    var text = type switch
                    {
                        "s" => v != null && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "",
                        "inlineStr" => isText ?? "",
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        _ => v ?? isText ?? "",
                    };
                    cells.Add(new CellText(col, rowNum, text, type));
                }
                rows.Add(cells);
            }
            return rows;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return list;
            using var s = entry.Open();
            using var xr = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });

            // One <si> at a time via ReadOuterXml: rich-text runs (<r><t>..</t></r>) and
            // plain <t> both concatenate to the displayed string, and the reader position
            // is never left mid-element (which silently dropped entries and shifted every
            // shared-string index after it).
            while (!xr.EOF)
            {
                if (xr.NodeType != XmlNodeType.Element || xr.LocalName != "si")
                {
                    if (!xr.Read()) break;
                    continue;
                }
                var doc = new XmlDocument();
                doc.LoadXml(xr.ReadOuterXml());
                var sb = new StringBuilder();
                foreach (XmlNode t in doc.DocumentElement!.GetElementsByTagName("t", NsMain)) sb.Append(t.InnerText);
                if (sb.Length == 0) foreach (XmlNode t in doc.DocumentElement!.GetElementsByTagName("t")) sb.Append(t.InnerText);
                list.Add(sb.ToString());
            }
            return list;
        }

        /// <summary>Worksheet part paths in workbook order (resolved through the rels).</summary>
        private static List<string> SheetPaths(ZipArchive zip)
            => SheetEntries(zip).Select(s => s.Path).ToList();

        private static List<(string Name, string Path)> SheetEntries(ZipArchive zip)
        {
            var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("xl/workbook.xml missing - not an xlsx");
            var sheets = new List<(string Name, string Rid)>();
            using (var s = wb.Open())
            using (var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "sheet")
                    {
                        var rid = xr.GetAttribute("id", NsRel) ?? xr.GetAttribute("r:id");
                        if (rid != null) sheets.Add((xr.GetAttribute("name") ?? "", rid));
                    }
                }
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var rels = zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidOperationException("workbook rels missing");
            using (var s = rels.Open())
            using (var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "Relationship")
                    {
                        var id = xr.GetAttribute("Id"); var target = (xr.GetAttribute("Target") ?? "").TrimStart('/');
                        if (id == null) continue;
                        map[id] = target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target;
                    }
                }
            }
            return sheets.Where(s => map.ContainsKey(s.Rid)).Select(s => (s.Name, map[s.Rid])).ToList();
        }

        // ================================================================= WRITE

        /// <summary>Blank: a styled cell without a value (a fill or border band), read back as empty — never an empty text.</summary>
        public enum CellKind { Text, Number, Formula, Blank }

        public readonly record struct OutCell(string Reference, CellKind Kind, string Value, int Style);

        public sealed class OutRow
        {
            public int Index { get; }
            public double? HeightPoints { get; set; }
            public List<OutCell> Cells { get; } = new();
            public OutRow(int index) => Index = index;
            public OutRow Text(string col, string text, int style) { Cells.Add(new OutCell($"{col}{Index}", CellKind.Text, text, style)); return this; }
            public OutRow Number(string col, double value, int style) { Cells.Add(new OutCell($"{col}{Index}", CellKind.Number, value.ToString("R", CultureInfo.InvariantCulture), style)); return this; }
            public OutRow Number(string col, decimal value, int style) { Cells.Add(new OutCell($"{col}{Index}", CellKind.Number, value.ToString(CultureInfo.InvariantCulture), style)); return this; }
            public OutRow Formula(string col, string formula, int style) { Cells.Add(new OutCell($"{col}{Index}", CellKind.Formula, formula, style)); return this; }
            public OutRow Blank(string col, int style) { Cells.Add(new OutCell($"{col}{Index}", CellKind.Blank, "", style)); return this; }
        }

        /// <summary>A cell style: font + fill + border, indexed by position in the list.</summary>
        public sealed record Style(
            bool Bold = false, bool Italic = false, double FontSize = 11, string FontName = "Arial",
            string? FillRgb = null, bool ThinTopBottomBorder = false, int NumFmtId = 0,
            bool WrapText = false, bool AlignTop = false)
        {
            // Opt-in for mixed Hebrew/English presentation notices only.
            public bool RightToLeft { get; init; }
            // Opt-in: right-aligned left-to-right text (codes, layer names) inside a right-to-left sheet.
            public bool AlignRight { get; init; }
        }

        /// <summary>Built-in Excel number format "#,##0.00" (no custom numFmts part needed).</summary>
        public const int NumFmtThousands2 = 4;
        /// <summary>Custom Excel number format "#,##0.0000" for measured quantities.</summary>
        public const int NumFmtThousands4 = 164;

        /// <summary>A decimal "between" validation (Excel rejects typed values outside [Min, Max], blanks and text).</summary>
        public sealed record DecimalValidation(string Sqref, double Min, double Max, string ErrorTitle, string Error);

        /// <summary>One worksheet. Cell style indices refer to its owning workbook's Styles.</summary>
        public class Worksheet
        {
            public string SheetName { get; set; } = "Sheet1";
            public bool RightToLeft { get; set; }
            // Opt-in presentation features. Defaults preserve existing BOQ output.
            public bool ShowGridLines { get; set; } = true;
            public int FreezeTopRows { get; set; }
            public int FreezeLeftColumns { get; set; }
            /// <summary>Repeat the first N rows on printed pages; zero disables print titles.</summary>
            public int PrintTitleRows { get; set; }
            public string? AutoFilterRange { get; set; }
            public List<(int Column, double Width)> ColumnWidths { get; } = new();
            public List<OutRow> Rows { get; } = new();
            public List<string> SingleRowMerges { get; } = new();
            /// <summary>Opt-in print layout: landscape A4, one page wide, any number of pages tall. Off by default.</summary>
            public bool PrintLandscapeFitToWidth { get; set; }
            /// <summary>Opt-in: decimal range validations on single cells or ranges. Off (empty) by default.</summary>
            public List<DecimalValidation> DataValidations { get; } = new();
        }

        /// <summary>
        /// The inherited worksheet remains sheet 1 for existing callers. Extra sheets
        /// follow in collection order and share the same workbook-wide style indices.
        /// </summary>
        public sealed class Workbook : Worksheet
        {
            public List<Style> Styles { get; } = new() { new Style() };   // index 0 = default
            public List<Worksheet> AdditionalSheets { get; } = new();
        }

        public static void Write(Workbook wb, string path)
        {
            ArgumentNullException.ThrowIfNull(wb);
            var sheets = new List<Worksheet> { wb };
            sheets.AddRange(wb.AdditionalSheets);
            // Validate all sheets before touching an existing destination. In particular,
            // an invalid additional sheet must not truncate an otherwise valid export.
            ValidateWorkbook(wb, sheets);
            // Cached values for every formula, computed once for all sheets before the file is opened.
            var cached = EvaluateValidated(sheets);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            AddEntry(zip, "[Content_Types].xml", ContentTypesXml(sheets.Count));
            AddEntry(zip, "_rels/.rels", RootRelsXml());
            AddEntry(zip, "xl/workbook.xml", WorkbookXml(sheets));
            AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml(sheets.Count));
            AddEntry(zip, "xl/styles.xml", StylesXml(wb.Styles));
            for (var index = 0; index < sheets.Count; index++)
                AddEntry(zip, $"xl/worksheets/sheet{index + 1}.xml", SheetXml(sheets[index], cached));
        }

        /// <summary>
        /// The cached values <see cref="Write"/> stores for this workbook: the value of every evaluable
        /// formula and the list of formulas that stay without a value (with the reason). The workbook is
        /// validated exactly as <see cref="Write"/> validates it (invalid state throws ArgumentException).
        /// </summary>
        public static XlsxFormulaEvaluation EvaluateFormulas(Workbook wb)
        {
            ArgumentNullException.ThrowIfNull(wb);
            var sheets = new List<Worksheet> { wb };
            sheets.AddRange(wb.AdditionalSheets);
            ValidateWorkbook(wb, sheets);
            return EvaluateValidated(sheets);
        }

        // A formula's content never makes Write fail: the evaluator reports what it cannot compute,
        // and anything unexpected leaves every formula without a cached value (the previous output).
        private static XlsxFormulaEvaluation EvaluateValidated(IReadOnlyList<Worksheet> sheets)
        {
            try
            {
                return XlsxFormulaEvaluator.Evaluate(new ModelCellSource(sheets));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var reason = "formula evaluation stopped (" + ex.GetType().Name + ")";
                var formulas = sheets.SelectMany(sheet => sheet.Rows.SelectMany(row => row.Cells
                        .Where(cell => cell.Kind == CellKind.Formula)
                        .Select(cell => new XlsxUnevaluableCell(sheet.SheetName, cell.Reference, reason))))
                    .ToList();
                return XlsxFormulaEvaluation.Aborted(formulas, reason);
            }
        }

        /// <summary>The in-memory model as the evaluator's cell source (sheet index built lazily, only for sheets formulas read).</summary>
        private sealed class ModelCellSource : IXlsxCellSource
        {
            private readonly IReadOnlyList<Worksheet> _sheets;
            private readonly Dictionary<string, Worksheet> _byName = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<long, OutCell>> _cells = new(StringComparer.Ordinal);

            public ModelCellSource(IReadOnlyList<Worksheet> sheets)
            {
                _sheets = sheets;
                foreach (var sheet in sheets) _byName.TryAdd(sheet.SheetName, sheet);
                SheetNames = sheets.Select(sheet => sheet.SheetName).ToList();
            }

            public IReadOnlyList<string> SheetNames { get; }

            public IEnumerable<XlsxFormulaCell> FormulaCells()
            {
                foreach (var sheet in _sheets)
                    foreach (var row in sheet.Rows)
                        foreach (var cell in row.Cells)
                            if (cell.Kind == CellKind.Formula && TryCellAddress(cell.Reference, out var column, out var rowIndex))
                                yield return new XlsxFormulaCell(sheet.SheetName, rowIndex, column, cell.Value);
            }

            public XlsxSourceCell GetCell(string sheet, int row, int column)
            {
                if (!_byName.TryGetValue(sheet, out var worksheet)) return XlsxSourceCell.Empty;
                if (!_cells.TryGetValue(sheet, out var index))
                {
                    index = new Dictionary<long, OutCell>();
                    foreach (var outRow in worksheet.Rows)
                        foreach (var outCell in outRow.Cells)
                            if (TryCellAddress(outCell.Reference, out var cellColumn, out var cellRow))
                                index[((long)cellRow << 16) | (uint)cellColumn] = outCell;
                    _cells[sheet] = index;
                }
                if (!index.TryGetValue(((long)row << 16) | (uint)column, out var cell)) return XlsxSourceCell.Empty;
                return cell.Kind switch
                {
                    CellKind.Number => XlsxSourceCell.FromNumber(double.Parse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture)),
                    CellKind.Formula => XlsxSourceCell.FromFormula(cell.Value),
                    CellKind.Blank => XlsxSourceCell.Empty,
                    _ => XlsxSourceCell.FromText(cell.Value),
                };
            }
        }

        private static void AddEntry(ZipArchive zip, string name, string xml)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            // ZIP's default current timestamp otherwise changes identical workbook bytes.
            e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var s = e.Open();
            var bytes = new UTF8Encoding(false).GetBytes(xml);
            s.Write(bytes, 0, bytes.Length);
        }

        private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

        private static string ContentTypesXml(int sheetCount) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Types xmlns=\"{NsCt}\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            string.Concat(Enumerable.Range(1, sheetCount).Select(index =>
                $"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        private static string RootRelsXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Relationships xmlns=\"{NsPkgRel}\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string WorkbookXml(IReadOnlyList<Worksheet> sheets) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<workbook xmlns=\"{NsMain}\" xmlns:r=\"{NsRel}\">" +
            "<sheets>" + string.Concat(sheets.Select((sheet, index) =>
                $"<sheet name=\"{X(sheet.SheetName)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>")) + "</sheets>" +
            (sheets.Any(sheet => sheet.PrintTitleRows > 0) ? "<definedNames>" + string.Concat(sheets.Select((sheet, index) =>
                sheet.PrintTitleRows > 0 ? $"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{index}\">" +
                X("'" + sheet.SheetName.Replace("'", "''") + "'!$1:$" + sheet.PrintTitleRows.ToString(CultureInfo.InvariantCulture)) +
                "</definedName>" : "")) + "</definedNames>" : "") +
            "<calcPr calcMode=\"auto\" fullCalcOnLoad=\"1\" forceFullCalc=\"1\"/>" +
            "</workbook>";

        private static string WorkbookRelsXml(int sheetCount) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Relationships xmlns=\"{NsPkgRel}\">" +
            string.Concat(Enumerable.Range(1, sheetCount).Select(index =>
                $"<Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>")) +
            $"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        private static string StylesXml(List<Style> styles)
        {
            // fonts/fills/borders are de-duplicated; cellXfs has one entry per style index.
            var fonts = new List<string>(); var fills = new List<string> { "none", "gray125" }; var borders = new List<string> { "" , "thin" };
            string FontKey(Style s) => $"{s.Bold}|{s.Italic}|{s.FontSize.ToString(CultureInfo.InvariantCulture)}|{s.FontName}";
            var fontIndex = new Dictionary<string, int>();
            var fillIndex = new Dictionary<string, int>();
            var xfs = new StringBuilder();

            foreach (var s in styles)
            {
                var fk = FontKey(s);
                if (!fontIndex.TryGetValue(fk, out var fi)) { fi = fonts.Count; fonts.Add(fk); fontIndex[fk] = fi; }
                int fillI = 0;
                if (s.FillRgb != null)
                {
                    if (!fillIndex.TryGetValue(s.FillRgb, out fillI)) { fillI = fills.Count; fills.Add(s.FillRgb); fillIndex[s.FillRgb] = fillI; }
                }
                int borderI = s.ThinTopBottomBorder ? 1 : 0;
                xfs.Append($"<xf numFmtId=\"{s.NumFmtId}\" fontId=\"{fi}\" fillId=\"{fillI}\" borderId=\"{borderI}\" xfId=\"0\"" +
                           (s.NumFmtId > 0 ? " applyNumberFormat=\"1\"" : "") +
                           (fi > 0 ? " applyFont=\"1\"" : "") + (fillI > 1 ? " applyFill=\"1\"" : "") + (borderI > 0 ? " applyBorder=\"1\"" : "") +
                           (s.WrapText || s.AlignTop || s.RightToLeft || s.AlignRight ? " applyAlignment=\"1\"><alignment" +
                               (s.WrapText ? " wrapText=\"1\"" : "") +
                               (s.RightToLeft ? " readingOrder=\"2\" horizontal=\"right\"" : s.AlignRight ? " readingOrder=\"1\" horizontal=\"right\"" : "") +
                               " vertical=\"top\"/></xf>" : "/>"));
            }

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<styleSheet xmlns=\"{NsMain}\">");
            sb.Append($"<numFmts count=\"1\"><numFmt numFmtId=\"{NumFmtThousands4}\" formatCode=\"#,##0.0000\"/></numFmts>");
            sb.Append($"<fonts count=\"{fonts.Count}\">");
            foreach (var f in fonts)
            {
                var p = f.Split('|');
                sb.Append("<font>");
                if (p[0] == "True") sb.Append("<b/>");
                if (p[1] == "True") sb.Append("<i/>");
                sb.Append($"<sz val=\"{p[2]}\"/><name val=\"{X(p[3])}\"/>");
                sb.Append("</font>");
            }
            sb.Append("</fonts>");
            sb.Append($"<fills count=\"{fills.Count}\">");
            foreach (var f in fills)
            {
                if (f == "none") sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
                else if (f == "gray125") sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
                else sb.Append($"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"{f}\"/><bgColor indexed=\"64\"/></patternFill></fill>");
            }
            sb.Append("</fills>");
            sb.Append("<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
                      "<border><left/><right/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border></borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append($"<cellXfs count=\"{styles.Count}\">{xfs}</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        private static string SheetXml(Worksheet wb, XlsxFormulaEvaluation cached)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append($"<worksheet xmlns=\"{NsMain}\" xmlns:r=\"{NsRel}\">");
            if (wb.PrintLandscapeFitToWidth) sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            if (wb.FreezeTopRows < 0 || wb.FreezeTopRows >= 1048576 ||
                wb.FreezeLeftColumns < 0 || wb.FreezeLeftColumns >= 16384)
                throw new ArgumentException("Frozen worksheet panes exceed valid row/column bounds.");
            sb.Append($"<sheetViews><sheetView workbookViewId=\"0\"{(wb.RightToLeft ? " rightToLeft=\"1\"" : "")}" +
                (wb.ShowGridLines ? "" : " showGridLines=\"0\""));
            if (wb.FreezeTopRows == 0 && wb.FreezeLeftColumns == 0)
                sb.Append("/></sheetViews>");
            else
            {
                var activePane = wb.FreezeTopRows > 0
                    ? (wb.FreezeLeftColumns > 0 ? "bottomRight" : "bottomLeft") : "topRight";
                var columnNumber = wb.FreezeLeftColumns + 1;
                var columnName = "";
                while (columnNumber > 0)
                {
                    columnName = (char)('A' + (columnNumber - 1) % 26) + columnName;
                    columnNumber = (columnNumber - 1) / 26;
                }
                var firstCell = columnName + (wb.FreezeTopRows + 1).ToString(CultureInfo.InvariantCulture);
                sb.Append("><pane" +
                    (wb.FreezeLeftColumns > 0 ? $" xSplit=\"{wb.FreezeLeftColumns}\"" : "") +
                    (wb.FreezeTopRows > 0 ? $" ySplit=\"{wb.FreezeTopRows}\"" : "") +
                    $" topLeftCell=\"{firstCell}\" activePane=\"{activePane}\" state=\"frozen\"/>" +
                    $"<selection pane=\"{activePane}\" activeCell=\"{firstCell}\" sqref=\"{firstCell}\"/>" +
                    "</sheetView></sheetViews>");
            }
            if (wb.ColumnWidths.Count > 0)
            {
                sb.Append("<cols>");
                foreach (var (c, w) in wb.ColumnWidths)
                    sb.Append($"<col min=\"{c}\" max=\"{c}\" width=\"{w.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            foreach (var row in wb.Rows.OrderBy(r => r.Index))
            {
                sb.Append($"<row r=\"{row.Index}\"" + (row.HeightPoints is > 0 and <= 409
                    ? $" ht=\"{row.HeightPoints.Value.ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\"" : "") + ">");
                // SpreadsheetML requires the cells of a row in column order; a writer may add them in any order.
                foreach (var c in row.Cells.OrderBy(cell => TryCellAddress(cell.Reference, out var column, out _) ? column : int.MaxValue))
                {
                    var st = c.Style > 0 ? $" s=\"{c.Style}\"" : "";
                    switch (c.Kind)
                    {
                        case CellKind.Text:
                            sb.Append($"<c r=\"{c.Reference}\"{st} t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(c.Value)}</t></is></c>");
                            break;
                        case CellKind.Number:
                            sb.Append($"<c r=\"{c.Reference}\"{st}><v>{c.Value}</v></c>");
                            break;
                        case CellKind.Formula:
                            sb.Append(FormulaCellXml(c, st, wb.SheetName, cached));
                            break;
                        case CellKind.Blank:
                            sb.Append($"<c r=\"{c.Reference}\"{st}/>");
                            break;
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            if (!string.IsNullOrWhiteSpace(wb.AutoFilterRange))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(wb.AutoFilterRange,
                        @"^[A-Z]{1,3}[1-9][0-9]{0,6}:[A-Z]{1,3}[1-9][0-9]{0,6}$"))
                    throw new ArgumentException("Auto-filter must reference an explicit worksheet range.");
                sb.Append($"<autoFilter ref=\"{X(wb.AutoFilterRange)}\"/>");
            }
            if (wb.SingleRowMerges.Count > 0)
            {
                sb.Append($"<mergeCells count=\"{wb.SingleRowMerges.Count}\">");
                foreach (var range in wb.SingleRowMerges) sb.Append($"<mergeCell ref=\"{X(range)}\"/>");
                sb.Append("</mergeCells>");
            }
            if (wb.DataValidations.Count > 0)
            {
                sb.Append($"<dataValidations count=\"{wb.DataValidations.Count}\">");
                foreach (var v in wb.DataValidations)
                    sb.Append("<dataValidation type=\"decimal\" operator=\"between\" allowBlank=\"0\" showErrorMessage=\"1\" " +
                              $"errorTitle=\"{X(v.ErrorTitle)}\" error=\"{X(v.Error)}\" sqref=\"{X(v.Sqref)}\">" +
                              $"<formula1>{v.Min.ToString("R", CultureInfo.InvariantCulture)}</formula1>" +
                              $"<formula2>{v.Max.ToString("R", CultureInfo.InvariantCulture)}</formula2></dataValidation>");
                sb.Append("</dataValidations>");
            }
            if (wb.PrintLandscapeFitToWidth)
                sb.Append("<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/>" +
                    "<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        /// <summary>A formula cell with its cached value when the evaluator computed one; otherwise the formula alone.</summary>
        private static string FormulaCellXml(OutCell c, string style, string sheet, XlsxFormulaEvaluation cached)
        {
            var formula = $"<f>{X(c.Value)}</f>";
            if (!cached.TryGetValue(sheet, c.Reference, out var value))
                return $"<c r=\"{c.Reference}\"{style}>{formula}</c>";
            return value.Kind switch
            {
                XlsxValueKind.Text => $"<c r=\"{c.Reference}\"{style} t=\"str\">{formula}<v>{X(value.Text)}</v></c>",
                XlsxValueKind.Boolean => $"<c r=\"{c.Reference}\"{style} t=\"b\">{formula}<v>{(value.IsTrue ? "1" : "0")}</v></c>",
                _ => $"<c r=\"{c.Reference}\"{style}>{formula}<v>{value.Number.ToString("R", CultureInfo.InvariantCulture)}</v></c>",
            };
        }

        private static void ValidateWorkbook(Workbook wb, IReadOnlyList<Worksheet> sheets)
        {
            if (wb.Styles.Count == 0 || wb.Styles.Any(style => style == null))
                throw new ArgumentException("Workbook must contain a default style and no null styles.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < sheets.Count; index++)
            {
                var sheet = sheets[index];
                if (sheet == null || (index > 0 && sheet is Workbook))
                    throw new ArgumentException("Additional sheets must be worksheets, not null or nested workbooks.");
                if (sheet.PrintTitleRows < 0 || sheet.PrintTitleRows > 1048576)
                    throw new ArgumentException("Print titles exceed valid worksheet row bounds.");
                var name = sheet.SheetName;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 31 ||
                    name.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) >= 0 ||
                    name[0] == '\'' || name[^1] == '\'' || name.Any(char.IsControl) ||
                    string.Equals(name, "History", StringComparison.OrdinalIgnoreCase) || !names.Add(name))
                    throw new ArgumentException("Worksheet names must be unique, nonempty, at most 31 characters and Excel-compatible.");
                ValidateXmlText(name);
                if (sheet.FreezeTopRows < 0 || sheet.FreezeTopRows >= 1048576 ||
                    sheet.FreezeLeftColumns < 0 || sheet.FreezeLeftColumns >= 16384)
                    throw new ArgumentException("Frozen worksheet panes exceed valid row/column bounds.");
                var columns = new HashSet<int>();
                foreach (var (column, width) in sheet.ColumnWidths)
                    if (column < 1 || column > 16384 || !columns.Add(column) ||
                        !double.IsFinite(width) || width < 0 || width > 255)
                        throw new ArgumentException("Column widths must be finite, bounded and unique per worksheet column.");
                var rowIndices = new HashSet<int>();
                foreach (var row in sheet.Rows)
                {
                    if (row == null || row.Index < 1 || row.Index > 1048576 || !rowIndices.Add(row.Index))
                        throw new ArgumentException("Worksheet rows must have unique valid indices.");
                    if (row.HeightPoints.HasValue && (!double.IsFinite(row.HeightPoints.Value) ||
                        row.HeightPoints.Value <= 0 || row.HeightPoints.Value > 409))
                        throw new ArgumentException("Row height must be finite and within Excel limits.");
                    var cells = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cell in row.Cells)
                    {
                        if (!TryCellAddress(cell.Reference, out _, out var cellRow) || cellRow != row.Index ||
                            !cells.Add(cell.Reference) || cell.Style < 0 || cell.Style >= wb.Styles.Count ||
                            !Enum.IsDefined(cell.Kind))
                            throw new ArgumentException("Cells must have unique valid addresses, matching rows and workbook style indices.");
                        if (cell.Value == null) throw new ArgumentException("Cell value must not be null.");
                        // Reject before opening the destination. Excel cannot retain
                        // oversized cell text/formulas; silently shortening either
                        // would lose evidence or change the calculation contract.
                        if (cell.Kind == CellKind.Text && cell.Value.Length > 32767)
                            throw new ArgumentException("Worksheet cell text exceeds Excel's 32767-character limit; split the evidence explicitly.");
                        if (cell.Kind == CellKind.Formula && cell.Value.Length > 8192)
                            throw new ArgumentException("Worksheet formula exceeds Excel's 8192-character limit.");
                        ValidateXmlText(cell.Value);
                        if (cell.Kind == CellKind.Number && (!double.TryParse(cell.Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var numeric) || !double.IsFinite(numeric)))
                            throw new ArgumentException("Numeric cells must contain finite invariant numbers.");
                        if (cell.Kind == CellKind.Formula && string.IsNullOrWhiteSpace(cell.Value))
                            throw new ArgumentException("Formula cells must contain a formula.");
                    }
                }
                if (!string.IsNullOrWhiteSpace(sheet.AutoFilterRange))
                {
                    var range = sheet.AutoFilterRange.Split(':');
                    if (range.Length != 2 || !TryCellAddress(range[0], out var firstCol, out var firstRow) ||
                        !TryCellAddress(range[1], out var lastCol, out var lastRow) || firstCol > lastCol || firstRow > lastRow)
                        throw new ArgumentException("Auto-filter must reference an ordered, bounded worksheet range.");
                }
                ValidateSingleRowMerges(sheet);
                foreach (var v in sheet.DataValidations)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(v.Sqref ?? "", @"^[A-Z]{1,3}[1-9][0-9]{0,6}(:[A-Z]{1,3}[1-9][0-9]{0,6})?$") ||
                        !double.IsFinite(v.Min) || !double.IsFinite(v.Max) || v.Min > v.Max ||
                        (v.ErrorTitle ?? "").Length > 32 || (v.Error ?? "").Length > 255)
                        throw new ArgumentException("Data validations need an explicit cell range and a finite, ordered range.");
                    ValidateXmlText(v.ErrorTitle ?? ""); ValidateXmlText(v.Error ?? "");
                }
            }
        }

        private static void ValidateXmlText(string value)
        {
            try { XmlConvert.VerifyXmlChars(value); }
            catch (XmlException ex) { throw new ArgumentException("Worksheet text contains invalid XML characters.", ex); }
        }

        private static bool TryCellAddress(string? reference, out int column, out int row)
        {
            column = 0; row = 0;
            var match = System.Text.RegularExpressions.Regex.Match(reference ?? "", @"^([A-Z]{1,3})([1-9][0-9]{0,6})$");
            if (!match.Success) return false;
            foreach (var letter in match.Groups[1].Value) column = column * 26 + letter - 'A' + 1;
            row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return column <= 16384 && row <= 1048576;
        }

        private static void ValidateSingleRowMerges(Worksheet wb)
        {
            if (wb.SingleRowMerges.Count == 0) return;
            var spans = new List<(int Row, int First, int Last)>();
            var rows = wb.Rows.ToLookup(row => row.Index);
            static int Column(string letters) => letters.Aggregate(0, (value, c) => value * 26 + c - 'A' + 1);
            foreach (var range in wb.SingleRowMerges)
            {
                var match = System.Text.RegularExpressions.Regex.Match(range ?? "",
                    @"^([A-Z]{1,3})([1-9][0-9]{0,6}):([A-Z]{1,3})([1-9][0-9]{0,6})$");
                if (!match.Success) throw new ArgumentException("Merge must be an explicit single-row worksheet range.");
                var first = Column(match.Groups[1].Value); var last = Column(match.Groups[3].Value);
                var rowIndex = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (rowIndex > 1048576 || match.Groups[2].Value != match.Groups[4].Value ||
                    first >= last || last > 16384 || spans.Any(span => span.Row == rowIndex && first <= span.Last && last >= span.First))
                    throw new ArgumentException("Single-row merges must be bounded, ordered and non-overlapping.");
                var row = rows[rowIndex].SingleOrDefault()
                    ?? throw new ArgumentException("A merged notice must reference an existing row.");
                var anchor = match.Groups[1].Value + match.Groups[2].Value;
                if (row.Cells.Count(cell => cell.Reference == anchor) != 1 ||
                    row.Cells.Single(cell => cell.Reference == anchor).Kind != CellKind.Text ||
                    row.Cells.Any(cell => cell.Reference != anchor && Column(new string(cell.Reference.TakeWhile(char.IsLetter).ToArray())) >= first &&
                        Column(new string(cell.Reference.TakeWhile(char.IsLetter).ToArray())) <= last))
                    throw new ArgumentException("A merged notice must contain only its text anchor; no data may be hidden.");
                spans.Add((rowIndex, first, last));
            }
        }
    }
}
