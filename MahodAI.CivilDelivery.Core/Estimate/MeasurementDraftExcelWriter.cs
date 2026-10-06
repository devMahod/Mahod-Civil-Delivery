using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Host-free serialization of a complete neutral measurement snapshot, not an
/// approved estimate. The workflow must independently prove live freshness and
/// immutable publication before and after writing. No mapping or price authority
/// is inferred here, and no measured records are filtered, combined or reordered.
/// </summary>
public static class MeasurementDraftExcelWriter
{
    public const int HeaderRow = 6;
    public const string SheetName = "טיוטת מדידה";
    public const string FindingsSheetName = "ממצאי מדידה";
    public const string ProposalsSheetName = "הצעות שיוך";
    public const string SourcesSheetName = "מקורות וראיות";
    public const string TextPartsSheetName = "פרטי טקסט";
    /// <summary>Height of one measurement row on the main sheet: a compact data table, one line per record.</summary>
    public const double CompactRecordRowHeight = 21;
    public const string DraftNotice = "טיוטת מדידה מלאה של רשומות הסריקה — DRAFT. לא אומדן מאושר ולא אישור שלמות המדידה.";
    public const string LegacyFailureNotice = "בסריקה היסטורית זו פירוט חלק מכשלי המדידה קוצר בעת הסריקה. הייצוא משמר את הראיה הקיימת ואינו משחזר פרטים חסרים. יש לסרוק מחדש לקבלת הפירוט המלא.";

    public sealed record WriteContext(
        string ProjectProfileId, string ScanRunId, string ProjectProfileHash,
        string SourceDrawingPath, string SourceDrawingHash,
        string PublishedScanPath, string PublishedScanSha256,
        IReadOnlyList<MappingProposal>? MappingProposals = null,
        string? MappingProposalsEvidencePath = null,
        string? MappingProposalsEvidenceSha256 = null,
        string? ProposalReferenceSource = null,
        string? ProposalReferenceHash = null);

    public sealed record WriteResult(string XlsxPath, string XlsxHash,
        int RecordCount, int RecordFindingCount, int ScanFindingCount);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static WriteResult Write(IReadOnlyList<NeutralQuantityRecord> records,
        IReadOnlyList<DeliveryFinding> scanFindings, string outputDir, string baseName, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(scanFindings);
        var snapshot = records.ToArray();
        var findings = scanFindings.ToArray();
        var workbook = Build(snapshot, findings, context);
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new ArgumentException("An explicit measurement draft output directory is required.", nameof(outputDir));
        if (string.IsNullOrWhiteSpace(baseName) || baseName != Path.GetFileName(baseName) ||
            baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || baseName is "." or "..")
            throw new ArgumentException("The draft base name must be a file name, not a path.", nameof(baseName));
        var directory = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".measurement-draft-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            MiniXlsx.Write(workbook, temporary);
            var hash = ArtifactHash.Sha256OfFile(temporary);
            for (var suffix = 0; suffix < 1000; suffix++)
            {
                var destination = Path.Combine(directory, baseName +
                    (suffix == 0 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture)) + ".xlsx");
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                try
                {
                    File.Move(temporary, destination);
                    return new WriteResult(destination, hash, snapshot.Length,
                        snapshot.Sum(record => record.Findings.Count), findings.Length);
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
            throw new IOException("No unused measurement draft file name was available.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static MiniXlsx.Workbook Build(IReadOnlyList<NeutralQuantityRecord> records,
        IReadOnlyList<DeliveryFinding> scanFindings, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(scanFindings);
        ArgumentNullException.ThrowIfNull(context);
        if (records.Count == 0)
            throw new InvalidOperationException("אין רשומות מדידה לייצוא. היעדר רשומות אינו מדידה של אפס.");
        if (string.IsNullOrWhiteSpace(context.ProjectProfileId) || string.IsNullOrWhiteSpace(context.ScanRunId) ||
            string.IsNullOrWhiteSpace(context.SourceDrawingPath) || string.IsNullOrWhiteSpace(context.PublishedScanPath) ||
            !CatalogIdentity.IsValidSha256(context.ProjectProfileHash) ||
            !CatalogIdentity.IsValidSha256(context.SourceDrawingHash) ||
            !CatalogIdentity.IsValidSha256(context.PublishedScanSha256))
            throw new ArgumentException("The draft requires identified profile, drawing and published scan evidence.");
        if (records.Any(record => record == null || record.Source == null || record.Measurement == null ||
                record.Classification == null || record.Findings == null) || scanFindings.Any(finding => finding == null))
            throw new ArgumentException("The draft snapshot contains a missing record or finding.");
        if (context.MappingProposals?.Count > 0 &&
            (string.IsNullOrWhiteSpace(context.MappingProposalsEvidencePath) ||
             !CatalogIdentity.IsValidSha256(context.MappingProposalsEvidenceSha256)))
            throw new ArgumentException("Mapping proposals require their published evidence path and SHA-256.");

        var wb = new MiniXlsx.Workbook
        {
            SheetName = SheetName, RightToLeft = true, ShowGridLines = false,
            FreezeTopRows = HeaderRow, FreezeLeftColumns = 2,
            AutoFilterRange = $"A{HeaderRow}:AF{HeaderRow + records.Count}",
        };
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 15)); // 1 title
        wb.Styles.Add(new MiniXlsx.Style(Italic: true)); // 2 context
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE7EDF3", WrapText: true)); // 3 heading
        wb.Styles.Add(new MiniXlsx.Style(WrapText: true, AlignTop: true)); // 4 literal evidence
        wb.Styles.Add(new MiniXlsx.Style(AlignTop: true)); // 5 unrounded numeric
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", WrapText: true, AlignTop: true)); // 6 warning
        wb.Styles.Add(new MiniXlsx.Style(AlignTop: true)); // 7 compact record text (no wrap)
        var widths = new[] { 10d, 29, 32, 26, 23, 20, 20, 24, 30, 26, 24, 90, 68, 24, 40, 25,
            25, 52, 40, 68, 49, 24, 29, 25, 90, 90, 65, 95, 20, 90, 16, 75 };
        for (var i = 0; i < widths.Length; i++) wb.ColumnWidths.Add((i + 1, widths[i]));
        MiniXlsx.Worksheet currentSheet = wb;
        var currentWidths = widths;
        var parts = new List<(string Scope, string Owner, string Field, int Part, string Text)>();
        var metadata = new List<(string Label, string Value)>();
        int WrappedLines(string value, int column) =>
            value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                // Excel column width is not a guaranteed character count for Arial,
                // mixed Hebrew/Latin or long CAD identifiers. Reserve wrapping space
                // rather than clipping text at a height inferred from Latin digits.
                .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / Math.Max(1, (currentWidths[column] - 2) * .75))));
        void Text(MiniXlsx.OutRow row, int column, string? value, string scope, string owner, int style = 4)
        {
            value ??= "";
            var field = "'" + currentSheet.SheetName.Replace("'", "''") + "'!" + Column(column) + row.Index.ToString(CultureInfo.InvariantCulture);
            // Excel permits 32767 characters but not a readable 32767-character
            // row. Preserve oversized text in ordered, explicitly linked parts.
            // Evidence columns must not make the whole measured-record row tall.
            // Move full text to linked details instead of merely clipping its row.
            var recordText = scope == "record";
            var maxLines = recordText ? 4 : 22;
            var readableLimit = (int)((currentWidths[column] - 2) * maxLines);
            if (value.Length > Math.Min(32767, readableLimit) || WrappedLines(value, column) > maxLines ||
                recordText && column >= 24 && value.Length > 150)
            {
                var partIndex = 0;
                for (var offset = 0; offset < value.Length;)
                {
                    var length = Math.Min(1200, value.Length - offset);
                    var newlines = 0;
                    for (var index = 0; index < length; index++)
                        if (value[offset + index] == '\n' && ++newlines == 20) { length = index + 1; break; }
                    if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1])) length--;
                    parts.Add((scope, owner, field, ++partIndex, value.Substring(offset, length)));
                    offset += length;
                }
                value = $"TEXT_PARTS:{field}";
            }
            // Measurement rows are a data table: one line per record. Long rule keys,
            // paths and JSON stay complete in the cell (and in the linked text parts)
            // but do not wrap; 74,891 rows at 51pt showed ~15 records per screen (2026-09-24).
            if (recordText && style == 4) style = 7;
            row.Text(Column(column), value, style);
        }
        void Add(MiniXlsx.OutRow row)
        {
            if (row.Index > 1048576)
                throw new InvalidOperationException("The draft exceeds the worksheet row limit; no records were discarded.");
            if (ReferenceEquals(currentSheet, wb) && row.Index > HeaderRow)
            {
                row.HeightPoints = CompactRecordRowHeight;
                currentSheet.Rows.Add(row);
                return;
            }
            var lines = row.Cells.Where(c => c.Kind == MiniXlsx.CellKind.Text).Select(cell =>
            {
                var column = ColumnIndex(cell.Reference);
                return WrappedLines(cell.Value, column);
            }).DefaultIfEmpty(1).Max();
            row.HeightPoints = Math.Min(409, Math.Max(32, lines * 15 + 6));
            currentSheet.Rows.Add(row);
        }
        MiniXlsx.Worksheet Additional(string name, double[] sheetWidths, params string[] headings)
        {
            var sheet = new MiniXlsx.Worksheet { SheetName = name, RightToLeft = true,
                ShowGridLines = false, FreezeTopRows = HeaderRow, FreezeLeftColumns = 1 };
            for (var i = 0; i < sheetWidths.Length; i++) sheet.ColumnWidths.Add((i + 1, sheetWidths[i]));
            sheet.Rows.Add(new MiniXlsx.OutRow(2) { HeightPoints = 26 }.Text("A", name + " — טיוטת מדידה, ללא אישור או תמחור", 1));
            sheet.Rows.Add(new MiniXlsx.OutRow(3) { HeightPoints = 24 }.Text("A",
                $"ריצת מקור: {context.ScanRunId}. טקסט ארוך נשמר במלואו בגיליון {TextPartsSheetName} לפי גיליון ותא מקור.", 2));
            var heading = new MiniXlsx.OutRow(HeaderRow) { HeightPoints = 42 };
            for (var i = 0; i < headings.Length; i++) heading.Text(Column(i), headings[i], 3);
            sheet.Rows.Add(heading); wb.AdditionalSheets.Add(sheet); return sheet;
        }
        void SelectSheet(MiniXlsx.Worksheet sheet)
        {
            currentSheet = sheet;
            currentWidths = sheet.ColumnWidths.OrderBy(item => item.Column).Select(item => item.Width).ToArray();
        }
        void CompleteSheet(MiniXlsx.Worksheet sheet, int lastRow)
        {
            sheet.AutoFilterRange = $"A{HeaderRow}:{Column(sheet.ColumnWidths.Count - 1)}{Math.Max(HeaderRow, lastRow)}";
        }
        void Metadata(string label, string? value) => metadata.Add((label, value ?? ""));
        wb.Rows.Add(new MiniXlsx.OutRow(2) { HeightPoints = 26 }.Text("A", DraftNotice, 1));
        wb.Rows.Add(new MiniXlsx.OutRow(3) { HeightPoints = 24 }.Text("A",
            "הכמויות נשמרות ללא איחוד או המרת יחידות. מחירים וסכומים ריקים. הצעות שיוך אינן אישור.", 2));
        wb.Rows.Add(new MiniXlsx.OutRow(4) { HeightPoints = 18 }.Text("A",
            $"רשומות: {records.Count}; ממצאי רשומה: {records.Sum(r => r.Findings.Count)}; ממצאי סריקה: {scanFindings.Count}. הממצאים, ההצעות והמקורות בגיליונות נפרדים.", 0));
        var legacyFailures = scanFindings.Concat(records.SelectMany(record => record.Findings)).Any(HasLegacyTruncatedFailure);
        wb.Rows.Add(new MiniXlsx.OutRow(5) { HeightPoints = 18 }.Text("A", legacyFailures
            ? $"פירוט כשלי המדידה בסריקה זו חלקי. ראו LEGACY_NOTICE בגיליון {FindingsSheetName}."
            : $"כל הממצאים שנשמרו בסריקה בגיליון {FindingsSheetName}. ממצא לא פתור אינו אישור או אפס.", 0));
        Metadata("פרופיל / ריצת סריקה", context.ProjectProfileId + " / " + context.ScanRunId);
        Metadata("שרטוט מארח", context.SourceDrawingPath);
        Metadata("SHA-256 מארח / פרופיל", context.SourceDrawingHash + " / " + context.ProjectProfileHash);
        Metadata("ראיית סריקה", context.PublishedScanPath);
        Metadata("SHA-256 סריקה", context.PublishedScanSha256);
        Metadata("ראיית הצעות / SHA-256", context.MappingProposalsEvidencePath + " / " + context.MappingProposalsEvidenceSha256);
        Metadata("מקור ייחוס להצעות / SHA-256", context.ProposalReferenceSource + " / " + context.ProposalReferenceHash);
        Metadata("ממצאים ופרטי ראיות", $"TEXT_PARTS מפנה לחלקים ממוספרים בגיליון {TextPartsSheetName}, השומרים את הטקסט המקורי במלואו. החלקים מציינים גיליון ותא מקור, ויש לחברם לפי מספר החלק.");
        var headings = new[] { "מספר", "מזהה רשומה", "קבוצת שיוך", "שכבה", "סוג ישות", "סוג מדידה", "כמות מקור",
            "יחידת מקור", "מצב רשומה", "מחיר — לא תומחר", "סכום — לא תומחר", "שם שרטוט מקור", "נתיב מקור", "Handle מקור",
            "שיטת מדידה", "תחנה מתחילה", "תחנה מסתיימת", "XREF", "זהות Civil", "SHA-256 מקור", "ריצת מקור", "פרופיל מקור",
            "סיווג מקור", "סעיף מועמד — לא אישור", "סיווג וראיות שיוך (JSON)", "פרמטרי מדידה (JSON)", "ראיה גאומטרית (JSON)",
            "מקור וראיות (JSON)", "מספר ממצאי רשומה", "הצעות לא מאושרות (JSON)", "גרסת סכימה", "מזהי ממצאי רשומה (JSON)" };
        var header = new MiniXlsx.OutRow(HeaderRow) { HeightPoints = 42 };
        for (var column = 0; column < headings.Length; column++) header.Text(Column(column), headings[column], 3);
        wb.Rows.Add(header);
        var proposals = (context.MappingProposals ?? Array.Empty<MappingProposal>())
            .GroupBy(p => p.RuleKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Json(group.ToArray()), StringComparer.Ordinal);
        var rowIndex = HeaderRow;
        foreach (var record in records)
        {
            var row = new MiniXlsx.OutRow(++rowIndex).Number("A", (double)(rowIndex - HeaderRow), 5);
            void R(int column, string? value, int style = 4) => Text(row, column, value, "record", record.RecordId, style);
            R(1, record.RecordId); R(2, record.Classification.RuleKey); R(3, record.Source.Layer);
            R(4, record.Source.EntityType); R(5, record.Measurement.Kind);
            if (double.IsFinite(record.Measurement.RawValue)) row.Number("G", record.Measurement.RawValue, 5);
            else R(6, record.Measurement.RawValue.ToString("R", CultureInfo.InvariantCulture), 6);
            R(7, record.Measurement.Unit); R(8, record.Status.ToString());
            R(9, ""); R(10, ""); R(11, record.Source.Drawing); R(12, record.Source.DrawingPath);
            R(13, record.Source.Handle); R(14, record.Measurement.Method);
            NullableNumber(row, "P", record.Source.StationFrom); NullableNumber(row, "Q", record.Source.StationTo);
            R(17, record.Source.Xref); R(18, record.Source.CivilIdentity); R(19, record.Source.DrawingHash);
            R(20, record.RunId); R(21, record.ProjectProfileId); R(22, record.Classification.SourceClass);
            R(23, record.Classification.CandidateCatalogCode); R(24, Json(record.Classification));
            R(25, Json(record.Measurement.Parameters)); R(26, record.Measurement.GeometryEvidence == null ? "" : Json(record.Measurement.GeometryEvidence));
            R(27, record.Provenance == null ? "" : Json(record.Provenance)); row.Number("AC", (double)record.Findings.Count, 5);
            R(29, record.Classification.RuleKey != null && proposals.TryGetValue(record.Classification.RuleKey, out var proposalJson) ? proposalJson : "");
            row.Number("AE", (double)record.SchemaVersion, 5); R(31, Json(record.Findings.Select(f => f.FindingId)));
            Add(row);
        }
        var findingSheet = Additional(FindingsSheetName,
            new[] { 22d, 35, 32, 18, 24, 40, 48, 65, 65, 42, 100, 100 },
            "תחום ראיה", "רשומה / קבוצת שיוך", "מזהה ממצא", "חומרה", "קוד", "כותרת", "פעולה מומלצת",
            "מקורות (JSON)", "הפניות ראיה (JSON)", "רשומות מושפעות (JSON)", "הודעה", "ראיה מלאה (JSON)");
        var proposalSheet = Additional(ProposalsSheetName, new[] { 40d, 25, 32, 110 },
            "קבוצת שיוך", "סעיף מוצע — לא אישור", "מצב", "הצעה מלאה (JSON)");
        var sourceSheet = Additional(SourcesSheetName, new[] { 40d, 110 }, "נתון", "ערך מקור");
        var partSheet = Additional(TextPartsSheetName, new[] { 24d, 36, 48, 12, 110 },
            "תחום ראיה", "בעלים", "גיליון ותא מקור", "מספר חלק", "טקסט המקור — לחבר לפי מספר החלק");
        SelectSheet(findingSheet); rowIndex = HeaderRow;
        void Finding(DeliveryFinding finding, string scope, string owner)
        {
            var row = new MiniXlsx.OutRow(++rowIndex);
            var values = new[] { scope, owner, finding.FindingId, finding.Severity.ToString(), finding.Code, finding.Title,
                finding.RecommendedAction, Json(finding.SourceRefs), Json(finding.EvidenceRefs),
                Json(finding.AffectedRecordIds), finding.Message, Json(finding) };
            for (var i = 0; i < values.Length; i++) Text(row, i, values[i], scope, owner);
            Add(row);
            if (HasLegacyTruncatedFailure(finding))
                Add(new MiniXlsx.OutRow(++rowIndex).Text("A", "LEGACY_NOTICE", 6).Text("B", owner, 4)
                    .Text("C", finding.FindingId, 4).Text("K", LegacyFailureNotice, 6));
        }
        foreach (var record in records)
            foreach (var finding in record.Findings) Finding(finding, "record_finding", record.RecordId);
        foreach (var finding in scanFindings) Finding(finding, "scan_finding", "");
        CompleteSheet(findingSheet, rowIndex);
        SelectSheet(proposalSheet); rowIndex = HeaderRow;
        foreach (var proposal in context.MappingProposals ?? Array.Empty<MappingProposal>())
        {
            var row = new MiniXlsx.OutRow(++rowIndex);
            Text(row, 0, proposal.RuleKey, "proposal", proposal.RuleKey);
            Text(row, 1, proposal.ProposedCode, "proposal", proposal.RuleKey);
            row.Text("C", "PROPOSED_UNAPPROVED", 6);
            Text(row, 3, Json(proposal), "proposal", proposal.RuleKey);
            Add(row);
        }
        CompleteSheet(proposalSheet, rowIndex);
        SelectSheet(sourceSheet); rowIndex = HeaderRow;
        foreach (var item in metadata)
        {
            var row = new MiniXlsx.OutRow(++rowIndex);
            Text(row, 0, item.Label, "metadata", item.Label);
            Text(row, 1, item.Value, "metadata", item.Label);
            Add(row);
        }
        CompleteSheet(sourceSheet, rowIndex);
        SelectSheet(partSheet); rowIndex = HeaderRow;
        // Each part is a literal cell, never a formula or hyperlink. Concatenating
        // E in part order for the qualified original cell reconstructs exact text.
        foreach (var part in parts)
            Add(new MiniXlsx.OutRow(++rowIndex).Text("A", part.Scope, 4)
                .Text("B", part.Owner.Length <= 200 ? part.Owner : "owner in original row", 4).Text("C", part.Field, 4)
                .Number("D", (double)part.Part, 5).Text("E", part.Text, 4));
        CompleteSheet(partSheet, rowIndex);
        return wb;
    }

    internal static bool HasLegacyTruncatedFailure(DeliveryFinding finding) =>
        finding.Code == EstimateFindingCodes.MeasurementFailed &&
        !finding.Message.StartsWith("Complete measurement failures (", StringComparison.Ordinal) &&
        Regex.Match(finding.Title, @"^\s*(\d+)\s+supported construction objects could not be measured").Groups[1].Value is var countText &&
        int.TryParse(countText, out var count) && count > 10;

    private static void NullableNumber(MiniXlsx.OutRow row, string column, double? value)
    {
        if (value.HasValue && double.IsFinite(value.Value)) row.Number(column, value.Value, 5);
        else row.Text(column, value?.ToString("R", CultureInfo.InvariantCulture) ?? "", value.HasValue ? 6 : 4);
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static string Column(int index)
    {
        var name = "";
        for (var value = index + 1; value > 0; value = (value - 1) / 26)
            name = (char)('A' + (value - 1) % 26) + name;
        return name;
    }
    private static int ColumnIndex(string reference)
    {
        var value = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter)) value = value * 26 + character - 'A' + 1;
        return value - 1;
    }
}
