using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Loads an Israeli price book from xlsx - Netivei Israel, Dekel, or a contractor's
    /// own sheet - by recognising the header row and mapping columns BY NAME.
    ///
    /// All of them share one shape: chapter.sub.item codes (with or without a publisher
    /// prefix such as NTI's "U"), Hebrew units, chapter/sub-chapter header rows that
    /// carry no unit and no price, and '-' for an item that has no price this edition.
    /// What differs is the header wording and the column order - exactly what a
    /// position-based reader gets wrong (the original reader loaded a Dekel sheet as
    /// zero items, 2026-08-19).
    ///
    /// Snapshot identity is the file hash - never "current catalog + factor". A missing
    /// price is MISSING_PRICE, never zero.
    /// </summary>
    public static class PriceBookXlsxLoader
    {
        /// <summary>What a workbook looks like before it is trusted as a price book.</summary>
        public sealed record Inspection(
            string Path,
            string FileHash,
            int HeaderRow,
            string? CodeColumn,
            string? DescriptionColumn,
            string? UnitColumn,
            string? PriceColumn,
            int ItemCount,
            int MissingPriceCount,
            IReadOnlyList<string> Chapters,
            string? Publisher,
            string? EditionNote,
            IReadOnlyList<string> Problems)
        {
            public string? SheetName { get; init; }
            public IReadOnlyList<string> AvailableSheets { get; init; } = Array.Empty<string>();
            public IReadOnlyList<HeaderCandidate> HeaderCandidates { get; init; } = Array.Empty<HeaderCandidate>();
            public RowCoverage Coverage { get; init; } = new(false, 0, 0, 0, 0, 0,
                new Dictionary<string, int>());
            public IReadOnlyList<RowDiagnostic> RowDiagnostics { get; init; } = Array.Empty<RowDiagnostic>();
            public ColumnMapping? AppliedMapping { get; init; }
            public bool IsUsable => HeaderRow > 0 && CodeColumn != null &&
                                    UnitColumn != null && PriceColumn != null &&
                                    ItemCount > 0 && Problems.Count == 0;
        }

        /// <summary>An explicit choice tied to exact workbook bytes, never a reusable guess by filename.</summary>
        public sealed record ColumnMapping(string ExpectedFileHash, string SheetName, int HeaderRow,
            string CodeColumn, string? DescriptionColumn, string UnitColumn, string PriceColumn);

        public sealed record HeaderCandidate(int Row, string Role, string Column, string Text);
        public enum RowOutcome { Imported, Duplicate, Rejected, NonItem }
        public sealed record RowDiagnostic(int Row, RowOutcome Outcome, string ReasonCode,
            string Code, string Unit, string PriceRaw, string? Detail);
        public sealed record RowCoverage(bool Evaluated, int DataRows, int ImportedRows,
            int DuplicateRows, int RejectedRows, int NonItemRows, IReadOnlyDictionary<string, int> ReasonCounts);

        // Header words, per role. Matched after stripping quotes/geresh and whitespace.
        private static readonly string[] CodeHeaders = { "מקט", "סעיף", "קוד", "מספר", "מס", "code", "item" };
        private static readonly string[] DescHeaders = { "תאור", "תיאור", "פירוט", "description" };
        private static readonly string[] UnitHeaders = { "יחידה", "יח", "יחידתמידה", "unit" };
        private static readonly string[] PriceHeaders = { "מחיר", "מחיריחידה", "מחירליחידה", "price" };

        // chapter.sub.item, optionally prefixed by a letter (NTI "U"), e.g. U51.01.0250 / 68.052.0010
        private static readonly Regex CodePattern =
            new(@"^[A-Za-z]?\d{1,3}\.\d{1,3}(\.\d{1,5})?$", RegexOptions.Compiled);

        public static CatalogSnapshot Load(string xlsxPath, string snapshotId) => LoadCore(xlsxPath, snapshotId, null);

        public static CatalogSnapshot Load(string xlsxPath, string snapshotId, ColumnMapping mapping)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            return LoadCore(xlsxPath, snapshotId, mapping);
        }

        private static CatalogSnapshot LoadCore(string xlsxPath, string snapshotId, ColumnMapping? mapping)
        {
            var (inspection, items, prices) = Read(xlsxPath, snapshotId, mapping);
            if (!inspection.IsUsable)
                throw new InvalidOperationException(
                    "לא ניתן לטעון את הקובץ כמחירון תקין: " +
                    (inspection.Problems.Count == 0
                        ? "לא נמצאו שורות סעיפים שקודיהן תואמים למבנה פרק.תת־פרק.סעיף."
                        : string.Join(" ", inspection.Problems)));

            return new CatalogSnapshot
            {
                SnapshotId = snapshotId,
                FileHash = inspection.FileHash,
                PublicationNote = inspection.EditionNote,
                Items = items,
                Prices = prices,
            };
        }

        /// <summary>Read-only look at a workbook: what is in it, and would it load?</summary>
        public static Inspection Inspect(string xlsxPath) => Read(xlsxPath, "inspect", null).Inspection;

        public static Inspection Inspect(string xlsxPath, ColumnMapping mapping)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            return Read(xlsxPath, "inspect", mapping).Inspection;
        }

        // ---------------------------------------------------------------- core

        private static (Inspection Inspection,
                        Dictionary<string, CatalogItem> Items,
                        Dictionary<string, PriceRecord> Prices) Read(string xlsxPath, string snapshotId, ColumnMapping? mapping)
        {
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException("קובץ המחירון לא נמצא.", xlsxPath);

            // Identity and all selected-sheet reads use one immutable byte snapshot.
            var workbookBytes = File.ReadAllBytes(xlsxPath);
            var fileHash = Convert.ToHexString(SHA256.HashData(workbookBytes)).ToLowerInvariant();
            var items = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
            var prices = new Dictionary<string, PriceRecord>(StringComparer.OrdinalIgnoreCase);
            var firstRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();
            var chapters = new SortedSet<string>(StringComparer.Ordinal);
            var candidates = new List<HeaderCandidate>();
            var diagnostics = new List<RowDiagnostic>();
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var sampleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            IReadOnlyList<string> sheetNames = Array.Empty<string>();
            string? sheetName = null, codeCol = null, descCol = null, unitCol = null, priceCol = null;
            string? publisher = null, edition = null;
            int headerRow = 0, missingPrice = 0, dataRows = 0, imported = 0, duplicates = 0, rejected = 0, nonItems = 0;
            bool evaluated = false;
            ColumnMapping? applied = null;
            var selectedHeaderTexts = new Dictionary<string, string>(StringComparer.Ordinal);

            (Inspection, Dictionary<string, CatalogItem>, Dictionary<string, PriceRecord>) Finish()
            {
                var info = new Inspection(xlsxPath, fileHash, headerRow, codeCol, descCol, unitCol, priceCol,
                    items.Count, missingPrice, chapters.ToArray(), publisher, edition, problems.AsReadOnly())
                {
                    SheetName = sheetName,
                    AvailableSheets = Array.AsReadOnly(sheetNames.ToArray()),
                    HeaderCandidates = candidates.AsReadOnly(),
                    AppliedMapping = applied,
                    Coverage = new RowCoverage(evaluated, dataRows, imported, duplicates, rejected, nonItems,
                        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(reasons)),
                    RowDiagnostics = diagnostics.AsReadOnly(),
                };
                return (info, items, prices);
            }

            void Diagnose(int row, RowOutcome outcome, string reason, string code, string unit, string price, string? detail = null)
            {
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                var count = sampleCounts.GetValueOrDefault(reason);
                if (count < 5 && diagnostics.Count < 50)
                {
                    // Bounded previews; full raw catalog fields remain unchanged in Items/Prices.
                    string Sample(string value) => value.Length <= 240 ? value : value[..240];
                    diagnostics.Add(new RowDiagnostic(row, outcome, reason, Sample(code), Sample(unit), Sample(price), detail));
                    sampleCounts[reason] = count + 1;
                }
            }

            if (mapping != null)
            {
                // Validate hash BEFORE any ZIP/XML parsing; no stale choice may fall back to auto mapping.
                if (mapping.ExpectedFileHash == null || !Regex.IsMatch(mapping.ExpectedFileHash, @"\A[0-9a-fA-F]{64}\z") ||
                    !string.Equals(mapping.ExpectedFileHash, fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("חתימת הקובץ במיפוי שנבחר (SHA-256) חסרה, אינה תקינה או אינה תואמת לתוכן הקובץ. יש לטעון תצוגה מקדימה ולבחור את המיפוי מחדש.");
                    return Finish();
                }
                if (string.IsNullOrEmpty(mapping.SheetName) || mapping.HeaderRow is < 1 or > 1048576)
                {
                    problems.Add("המיפוי שנבחר דורש שם גיליון מדויק ומספר שורת כותרת תקין.");
                    return Finish();
                }
                codeCol = ValidColumn(mapping.CodeColumn);
                descCol = mapping.DescriptionColumn == null ? null : ValidColumn(mapping.DescriptionColumn);
                unitCol = ValidColumn(mapping.UnitColumn);
                priceCol = ValidColumn(mapping.PriceColumn);
                var chosen = new[] { codeCol, descCol, unitCol, priceCol }.Where(c => c != null).ToArray();
                if (codeCol == null || unitCol == null || priceCol == null ||
                    (mapping.DescriptionColumn != null && descCol == null) ||
                    chosen.Distinct(StringComparer.Ordinal).Count() != chosen.Length)
                {
                    problems.Add("יש לבחור עמודות תקינות ושונות, בטווח A עד XFD, לכל תפקיד במיפוי.");
                    return Finish();
                }
            }

            sheetNames = PriceBookWorkbook.ReadSheetNames(workbookBytes);
            if (sheetNames.Count == 0)
            {
                problems.Add("לא נמצאו גיליונות בקובץ המחירון.");
                return Finish();
            }
            sheetName = mapping?.SheetName ?? sheetNames[0];
            if (sheetNames.Count(s => string.Equals(s, sheetName, StringComparison.Ordinal)) != 1)
            {
                problems.Add($"נדרש גיליון יחיד בשם '{sheetName}'; לא נבחר גיליון חלופי.");
                return Finish();
            }
            var sheetRows = PriceBookWorkbook.ReadSheet(workbookBytes, sheetName);
            var rows = new List<(int Number, List<(string Col, string Text, bool IsNumeric)> Cells)>();
            int rowIndex = 0;
            foreach (var row in sheetRows)
            {
                rowIndex = row.Count > 0 && row[0].Row > 0 ? row[0].Row : rowIndex + 1;
                rows.Add((rowIndex, row.Select(c => (c.Column, c.Text.Trim(), c.IsNumeric))
                    .Where(c => c.Column.Length > 0).ToList()));
            }

            foreach (var row in rows)
            {
                if (mapping != null && row.Number >= mapping.HeaderRow) break;
                var joined = string.Join(" ", row.Cells.Select(c => c.Text));
                if (publisher == null)
                {
                    if (joined.Contains("דקל")) publisher = "דקל";
                    else if (joined.Contains("נתיבי ישראל") || joined.Contains("נת\"י") || joined.Contains("נת\"צ") ||
                             joined.Contains("iroads") || joined.Contains("מחירון עירוני")) publisher = "נתיבי ישראל";
                }
                if (edition == null && (joined.Contains("מהדור") || joined.Contains("פרסום") || Regex.IsMatch(joined, @"20\d\d")))
                    edition = joined.Length > 160 ? joined[..160] : joined;
                if (mapping != null) continue;

                var found = HeaderMatches(row.Number, row.Cells.Select(c => (c.Col, c.Text)));
                if (!found.Any(c => c.Role == "code") || !found.Any(c => c.Role == "price")) continue;
                headerRow = row.Number;
                candidates.AddRange(found);
                foreach (var role in new[] { "code", "description", "unit", "price" })
                {
                    var matches = found.Where(c => c.Role == role).ToArray();
                    if (matches.Length > 1)
                        problems.Add($"נמצאו כמה עמודות לתפקיד '{HeaderRoleDisplay(role)}' בשורת הכותרת {headerRow}: {string.Join(", ", matches.Select(c => c.Column))}. יש לבחור את העמודה במפורש.");
                }
                if (found.GroupBy(c => c.Column, StringComparer.Ordinal).Any(g => g.Select(c => c.Role).Distinct().Count() > 1))
                    problems.Add("תא כותרת אחד מתאים לכמה תפקידים. יש לבחור במפורש עמודה נפרדת לכל תפקיד.");
                string? Unique(string role) => found.Count(c => c.Role == role) == 1 ? found.First(c => c.Role == role).Column : null;
                codeCol = Unique("code"); descCol = Unique("description"); unitCol = Unique("unit"); priceCol = Unique("price");
                break;
            }

            if (mapping != null)
            {
                headerRow = mapping.HeaderRow;
                var headers = rows.Where(r => r.Number == headerRow).ToArray();
                if (headers.Length != 1)
                    problems.Add("שורת הכותרת שנבחרה חסרה או מופיעה יותר מפעם אחת בגיליון הנבחר.");
                else
                {
                    candidates.AddRange(HeaderMatches(headerRow, headers[0].Cells.Select(c => (c.Col, c.Text))));
                    foreach (var col in new[] { codeCol, descCol, unitCol, priceCol }.Where(c => c != null))
                    {
                        var cells = headers[0].Cells.Where(c => c.Col == col).ToArray();
                        if (cells.Length != 1 || string.IsNullOrWhiteSpace(cells[0].Text))
                            problems.Add($"תא הכותרת בעמודה '{col}' בשורה {headerRow} חסר, מופיע יותר מפעם אחת או ריק.");
                        else selectedHeaderTexts.Add(col!, cells[0].Text);
                    }
                }
            }
            if (headerRow == 0)
                problems.Add("לא נמצאה שורת כותרת הכוללת עמודת קוד (מק\"ט/סעיף/קוד) ועמודת מחיר (מחיר).");
            if (headerRow > 0 && unitCol == null)
                problems.Add("לא נמצאה עמודת יחידה (יחידה); אי אפשר לבדוק התאמה בין יחידות המחירון ליחידות המדידה.");
            if (problems.Count > 0) return Finish();

            applied = new ColumnMapping(fileHash, sheetName, headerRow, codeCol!, descCol, unitCol!, priceCol!);
            evaluated = true;
            if (mapping != null)
            {
                // An explicit header choice cannot silently cut item rows out of the coverage preview.
                // Titles/dates/chapter headings alone are not items: require code plus a unit or positive price.
                foreach (var row in rows.Where(r => r.Number <= headerRow))
                {
                    string Text(string? col) => row.Cells.Where(c => c.Col == col).Select(c => c.Text).FirstOrDefault("");
                    var code = Text(codeCol);
                    var unitRaw = Text(unitCol);
                    var priceCell = row.Cells.Where(c => c.Col == priceCol).FirstOrDefault();
                    if (!CodePattern.IsMatch(code) || Units.Parse(unitRaw).Dimension == UnitDimension.Note ||
                        (unitRaw.Length == 0 && ParsePrice(priceCell.Text ?? "", priceCell.IsNumeric).Price == null)) continue;
                    dataRows++;
                    rejected++;
                    var selectedRow = row.Number == headerRow;
                    Diagnose(row.Number, RowOutcome.Rejected, selectedRow ? "item_as_selected_header" : "item_before_selected_header",
                        code, unitRaw, priceCell.Text ?? "", selectedRow
                            ? "השורה שנבחרה ככותרת נראית כשורת סעיף ולא נקלטה. יש לבחור שורת כותרת ולא שורת נתונים."
                            : "שורה דמוית סעיף נמצאת מעל שורת הכותרת שנבחרה ולא נקלטה. יש לבחור את הכותרת שמעל כל הסעיפים.");
                }
                var above = reasons.GetValueOrDefault("item_before_selected_header");
                if (above > 0)
                    problems.Add($"נמצאו {above} שורות דמויות סעיף מעל שורת הכותרת שנבחרה (שורה {headerRow}); הקליטה אינה מלאה. יש לבחור את הכותרת שמעל כל הסעיפים ולבדוק שוב.");
                if (reasons.ContainsKey("item_as_selected_header"))
                    problems.Add($"השורה שנבחרה ככותרת (שורה {headerRow}) נראית כשורת סעיף; היא לא נקלטה. יש לבחור שורת כותרת ולא שורת נתונים ולבדוק שוב.");
            }
            foreach (var row in rows.Where(r => r.Number > headerRow))
            {
                dataRows++;
                var cells = row.Cells;
                (string Text, bool IsNumeric) Cell(string? col) =>
                    col == null ? ("", false) : cells.Where(c => c.Col == col).Select(c => (c.Text, c.IsNumeric)).FirstOrDefault(("", false));
                var code = Cell(codeCol).Text;
                var desc = Cell(descCol).Text;
                var unitRaw = Cell(unitCol).Text;
                var priceCell = Cell(priceCol);
                var unit = Units.Parse(unitRaw);

                void NonItem(string reason)
                {
                    nonItems++;
                    Diagnose(row.Number, RowOutcome.NonItem, reason, code, unitRaw, priceCell.Text);
                }
                if (cells.All(c => c.Text.Length == 0)) { NonItem("blank_row"); continue; }
                var repeated = HeaderMatches(row.Number, cells.Select(c => (c.Col, c.Text)));
                // Explicit mappings may use any header wording. Require all selected cells to match exactly;
                // a code-shaped selected row must not turn an identical data row into a repeated header.
                var repeatedSelected = mapping != null && !CodePattern.IsMatch(selectedHeaderTexts[codeCol!]) &&
                    selectedHeaderTexts.All(h => cells.Count(c => c.Col == h.Key) == 1 &&
                        string.Equals(Cell(h.Key).Text, h.Value, StringComparison.Ordinal));
                if (repeatedSelected || (mapping == null &&
                    repeated.Any(c => c.Role == "code" && c.Column == codeCol) &&
                    repeated.Any(c => c.Role == "price" && c.Column == priceCol)))
                { NonItem("repeated_header"); continue; }
                if (unit.Dimension == UnitDimension.Note) { NonItem("note_unit"); continue; }
                // A bare chapter/subchapter with no unit or price is a heading, not a missing-price item.
                if (unitRaw.Length == 0 && priceCell.Text.Length == 0 &&
                    Regex.IsMatch(code, @"^[A-Za-z]?\d{1,3}(\.\d{1,3})?$"))
                { NonItem("chapter_heading"); continue; }
                if (!CodePattern.IsMatch(code))
                {
                    var itemLike = unitRaw.Length > 0 || priceCell.Text.Length > 0 ||
                                   Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9._-]*\d[A-Za-z0-9._-]*$");
                    if (!itemLike) { NonItem("text_or_heading"); continue; }
                    rejected++;
                    Diagnose(row.Number, RowOutcome.Rejected, code.Length == 0 ? "missing_item_code" : "unsupported_item_code",
                        code, unitRaw, priceCell.Text, "קוד הסעיף נשמר כפי שהוא, ללא המרה או החלפה.");
                    continue;
                }

                var parsedPrice = ParsePrice(priceCell.Text, priceCell.IsNumeric);
                var price = parsedPrice.Price;
                if (items.TryGetValue(code, out var existingItem))
                {
                    var identical = string.Equals(existingItem.Description.Trim(), desc.Trim(), StringComparison.Ordinal) &&
                                    string.Equals(existingItem.UnitRaw.Trim(), unitRaw.Trim(), StringComparison.Ordinal) &&
                                    prices[code].Price == price;
                    if (identical)
                    {
                        duplicates++;
                        Diagnose(row.Number, RowOutcome.Duplicate, "identical_duplicate", code, unitRaw, priceCell.Text);
                    }
                    else
                    {
                        rejected++;
                        Diagnose(row.Number, RowOutcome.Rejected, "conflicting_duplicate", code, unitRaw, priceCell.Text);
                        problems.Add($"קוד הסעיף '{code}' מופיע בשורות {firstRows[code]} ו-{row.Number} עם תיאור, יחידה או מחיר שונים.");
                    }
                    continue;
                }

                items.Add(code, new CatalogItem { Code = code, Description = desc, UnitRaw = unitRaw });
                prices.Add(code, new PriceRecord { Code = code, Price = price, PriceBookId = snapshotId, SourceHash = fileHash });
                firstRows.Add(code, row.Number);
                imported++;
                if (price == null) missingPrice++;
                chapters.Add(CatalogItem.ChapterOf(code));
                Diagnose(row.Number, RowOutcome.Imported, price == null ? (parsedPrice.Error == null ? "missing_price" : "invalid_price") : "imported",
                    code, unitRaw, priceCell.Text, parsedPrice.Error);
                if (unit.Dimension == UnitDimension.Unknown)
                    Diagnose(row.Number, RowOutcome.Imported, "unknown_unit", code, unitRaw, priceCell.Text,
                        "היחידה המקורית נשמרה, ללא הסקת יחידה אחרת או המרה.");
            }
            var unsupported = reasons.GetValueOrDefault("unsupported_item_code") + reasons.GetValueOrDefault("missing_item_code");
            if (unsupported > 0)
                problems.Add($"ב-{unsupported} שורות דמויות סעיף קוד הסעיף חסר או אינו נתמך; הקליטה אינה מלאה. יש לבדוק את אבחון השורות.");
            if (items.Count == 0)
                problems.Add($"שורת הכותרת נמצאה (שורה {headerRow}), אך מתחתיה לא נמצאו שורות סעיפים שקודיהן תואמים למבנה פרק.תת־פרק.סעיף.");
            return Finish();
        }

        // Display text only. HeaderCandidate.Role and all diagnostic reason codes remain stable.
        private static string HeaderRoleDisplay(string role) => role switch
        {
            "code" => "קוד סעיף",
            "description" => "תיאור",
            "unit" => "יחידה",
            "price" => "מחיר",
            _ => role,
        };

        private static string? ValidColumn(string? column)
        {
            if (column == null) return null;
            var col = column.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(col, @"^[A-Z]{1,3}$")) return null;
            var index = 0;
            foreach (var c in col) index = index * 26 + c - 'A' + 1;
            return index <= 16384 ? col : null;
        }

        private static List<HeaderCandidate> HeaderMatches(int row, IEnumerable<(string Col, string Text)> cells)
        {
            var result = new List<HeaderCandidate>();
            var roles = new[] { ("code", CodeHeaders), ("description", DescHeaders), ("unit", UnitHeaders), ("price", PriceHeaders) };
            foreach (var cell in cells)
            {
                var key = NormalizeHeader(cell.Text);
                if (key.Length == 0) continue;
                foreach (var (role, words) in roles)
                    if (words.Any(h => key.StartsWith(h, StringComparison.Ordinal)))
                        result.Add(new HeaderCandidate(row, role, cell.Col, cell.Text));
            }
            return result;
        }

        // -------------------------------------------------------------- helpers

        private static string NormalizeHeader(string s)
        {
            // "מק\"ט", "מק״ט", "מק'ט" -> "מקט"; "מחיר יחידה" -> "מחיריחידה"; case-fold latin.
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        internal sealed record PriceParseResult(decimal? Price, string? Error);

        internal static PriceParseResult ParsePrice(string text, bool numericCell = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return new PriceParseResult(null, null);
            var t = text.Trim();
            if (t == "-" || t == "—" || t == "–") return new PriceParseResult(null, null);
            t = t.Replace("₪", "").Replace("ש\"ח", "").Replace("ש״ח", "").Trim();

            if (numericCell)
                return Positive(decimal.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var numeric)
                    ? numeric : (decimal?)null, t, "אינו ערך מספרי תקין בתא המספרי של הקובץ");

            // Text cells need an explicit locale decision. A single separator with
            // three trailing digits ("1,200" / "1.200") is genuinely ambiguous and
            // is rejected. With both separators, the last one is decimal and the
            // other must be a valid 3-digit grouping separator.
            t = t.Replace("\u00A0", "").Replace("\u202F", "").Replace(" ", "");
            var sign = "";
            if (t.StartsWith('+') || t.StartsWith('-'))
            {
                sign = t[..1];
                t = t[1..];
            }
            if (t.Length == 0 || t.Any(ch => !char.IsDigit(ch) && ch != ',' && ch != '.'))
                return new PriceParseResult(null, $"'{text}' אינו ערך כספי מזוהה");

            var commas = t.Count(ch => ch == ',');
            var dots = t.Count(ch => ch == '.');
            string canonical;
            if (commas > 0 && dots > 0)
            {
                var decimalSep = t.LastIndexOf(',') > t.LastIndexOf('.') ? ',' : '.';
                var groupingSep = decimalSep == ',' ? '.' : ',';
                if (t.Count(ch => ch == decimalSep) != 1)
                    return new PriceParseResult(null, $"'{text}' מכיל יותר ממפריד עשרוני אחד");
                var split = t.Split(decimalSep);
                if (split.Length != 2 || split[1].Length is < 1 or > 2 || !split[1].All(char.IsDigit) ||
                    !TryUngroup(split[0], groupingSep, out var integerDigits))
                    return new PriceParseResult(null, $"'{text}' מכיל מפרידים לא חד־משמעיים או לא תקינים");
                canonical = sign + integerDigits + "." + split[1];
            }
            else if (commas + dots == 0)
            {
                canonical = sign + t;
            }
            else
            {
                var separator = commas > 0 ? ',' : '.';
                var count = commas + dots;
                if (count > 1)
                {
                    if (!TryUngroup(t, separator, out var integerDigits))
                        return new PriceParseResult(null, $"'{text}' מכיל חלוקה לא תקינה לקבוצות ספרות");
                    canonical = sign + integerDigits;
                }
                else
                {
                    var split = t.Split(separator);
                    if (split.Length != 2 || split[0].Length == 0 || split[1].Length == 0 ||
                        !split[0].All(char.IsDigit) || !split[1].All(char.IsDigit))
                        return new PriceParseResult(null, $"'{text}' מכיל מפרידים לא תקינים");
                    if (split[1].Length == 3 && split[0] != "0")
                        return new PriceParseResult(null,
                            $"'{text}' אינו חד־משמעי: שבר עשרוני או הפרדת אלפים");
                    if (split[1].Length > 3)
                        return new PriceParseResult(null, $"'{text}' מכיל יותר מדי ספרות אחרי המפריד העשרוני");
                    canonical = sign + split[0] + "." + split[1];
                }
            }

            return Positive(decimal.TryParse(canonical, System.Globalization.NumberStyles.AllowLeadingSign |
                    System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed : (decimal?)null,
                text, "לא ניתן לפענוח חד־משמעי");
        }

        private static PriceParseResult Positive(decimal? value, string original, string invalidReason)
        {
            if (value == null) return new PriceParseResult(null, $"'{original}' {invalidReason}");
            if (value.Value <= 0) return new PriceParseResult(null, $"'{original}' אינו גדול מאפס");
            return new PriceParseResult(value.Value, null);
        }

        private static bool TryUngroup(string text, char separator, out string digits)
        {
            digits = string.Empty;
            var groups = text.Split(separator);
            if (groups.Length < 2 || groups[0].Length is < 1 or > 3 || !groups[0].All(char.IsDigit) ||
                groups.Skip(1).Any(g => g.Length != 3 || !g.All(char.IsDigit))) return false;
            digits = string.Concat(groups);
            return true;
        }

    }
}
