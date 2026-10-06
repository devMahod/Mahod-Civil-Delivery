using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Standalone measured cross-section-area report. Never a BOQ, plan-area or volume
/// export. The caller must prove the current drawing/profile and published scan
/// before calling this host-free serializer; hashes supplied here are provenance,
/// not a substitute for that live workflow gate.
/// </summary>
public static class MaterialSectionAreaExcelWriter
{
    public const int HeaderRow = 10;
    public const string SheetName = "שטחי חומר בחתכים";

    public sealed record SourceIdentity(string Path, string Sha256);

    public sealed record WriteContext(
        string ProjectProfileId,
        string ScanRunId,
        string ProjectProfileHash,
        string SourceDrawingPath,
        string SourceDrawingHash,
        string PublishedScanSha256,
        IReadOnlyList<SourceIdentity>? AllowedMeasuredSources = null);

    public sealed record WriteResult(string XlsxPath, string XlsxHash, int ObservationCount);

    /// <summary>
    /// Preserves all measured rows, including real zeros, close stations and partial
    /// coverage. Partial/unproven data is labelled, not hidden or claimed complete.
    /// Empty input is unavailable evidence, never an empty successful area report.
    /// Writes one new file; an existing workbook is never replaced.
    /// </summary>
    public static WriteResult Write(
        IReadOnlyList<MaterialSectionAreaObservation> observations,
        string outputDir, string baseName, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var snapshot = observations.ToArray();
        var workbook = Build(snapshot, context);
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new ArgumentException("An explicit material report output directory is required.", nameof(outputDir));
        if (string.IsNullOrWhiteSpace(baseName) || baseName != Path.GetFileName(baseName) ||
            baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || baseName is "." or "..")
            throw new ArgumentException("The material report base name must be a file name, not a path.", nameof(baseName));

        var directory = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".material-areas-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            MiniXlsx.Write(workbook, temporary);
            var hash = ArtifactHash.Sha256OfFile(temporary);
            for (var suffix = 0; suffix < 1000; suffix++)
            {
                var name = baseName + (suffix == 0 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture)) + ".xlsx";
                var destination = Path.Combine(directory, name);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                try
                {
                    // No overwrite overload: a concurrent writer cannot replace a
                    // pre-existing workbook between the existence check and move.
                    File.Move(temporary, destination);
                    return new WriteResult(destination, hash, snapshot.Length);
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
            throw new IOException("No unused material report file name was available.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static MiniXlsx.Workbook Build(
        IReadOnlyList<MaterialSectionAreaObservation> observations, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(context);
        if (observations.Count == 0)
            throw new InvalidOperationException("לא נמדדו שטחי חומר בחתכים. היעדר תצפיות אינו שטח אפס.");
        if (observations.Count > 1048576 - HeaderRow)
            throw new InvalidOperationException("The material area report exceeds the worksheet row limit; no rows were discarded.");
        if (string.IsNullOrWhiteSpace(context.ProjectProfileId) || string.IsNullOrWhiteSpace(context.ScanRunId) ||
            string.IsNullOrWhiteSpace(context.SourceDrawingPath) ||
            !CatalogIdentity.IsValidSha256(context.ProjectProfileHash) ||
            !CatalogIdentity.IsValidSha256(context.SourceDrawingHash) ||
            !CatalogIdentity.IsValidSha256(context.PublishedScanSha256))
            throw new ArgumentException("The material report requires identified profile, drawing and published scan evidence.");

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new[] { new SourceIdentity(context.SourceDrawingPath, context.SourceDrawingHash) }
            .Concat(context.AllowedMeasuredSources ?? Array.Empty<SourceIdentity>()).ToList();
        if (sources.Any(s => s == null || string.IsNullOrWhiteSpace(s.Path) ||
                !Path.IsPathFullyQualified(s.Path) || !CatalogIdentity.IsValidSha256(s.Sha256)))
            throw new ArgumentException("Every permitted material source must have its own path and SHA-256 identity.");
        if (sources.GroupBy(s => Path.GetFullPath(s.Path), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(s => s.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
            throw new ArgumentException("The published scan declares conflicting identities for one material source path.");
        foreach (var row in observations)
        {
            if (row == null || !double.IsFinite(row.Station) || !double.IsFinite(row.Area) || row.Area < 0 ||
                row.ShapeCount <= 0 || string.IsNullOrWhiteSpace(row.RunId) ||
                string.IsNullOrWhiteSpace(row.CorridorHandle) || string.IsNullOrWhiteSpace(row.BaselineSeries) ||
                string.IsNullOrWhiteSpace(row.ShapeCode) || string.IsNullOrWhiteSpace(row.SourceUnits))
                throw new ArgumentException("The material report contains an invalid station/code/area observation.");
            if (!sources.Any(source => SameLocalPath(row.DrawingPath, source.Path) &&
                    string.Equals(row.DrawingHash, source.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Material observation source is not bound to the published scan; no unproven-source report was written.");
            if (row.StationUnit != (row.MetricUnitsProven ? "מטר" : "יחידת שרטוט") ||
                row.AreaUnit != (row.MetricUnitsProven ? "מ\"ר" : "יחידת שרטוט²"))
                throw new ArgumentException("Material area units disagree with their measurement evidence.");

            var key = string.Join("\u001f", Path.GetFullPath(row.DrawingPath), row.DrawingHash,
                row.CorridorHandle, row.BaselineSeries, row.ShapeCode,
                row.Station.ToString("R", CultureInfo.InvariantCulture));
            if (!identities.Add(key))
                throw new ArgumentException("Duplicate material station/code evidence cannot be exported as separate measurements.");
        }

        var wb = new MiniXlsx.Workbook
        {
            SheetName = SheetName, RightToLeft = true, ShowGridLines = false,
            FreezeTopRows = HeaderRow, FreezeLeftColumns = 4,
            AutoFilterRange = $"A{HeaderRow}:R{HeaderRow + observations.Count}",
        };
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 15)); // 1: title
        wb.Styles.Add(new MiniXlsx.Style(Italic: true)); // 2: scope
        wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE7EDF3", WrapText: true)); // 3: headings
        wb.Styles.Add(new MiniXlsx.Style(WrapText: true, AlignTop: true)); // 4: literal source text
        // General retains very close stations and tiny positive areas visibly;
        // fixed four-decimal display would make them appear equal to another row/zero.
        wb.Styles.Add(new MiniXlsx.Style(AlignTop: true)); // 5: unrounded measurement
        wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", WrapText: true, AlignTop: true)); // 6: incomplete evidence
        wb.Styles.Add(new MiniXlsx.Style(AlignTop: true)); // 7: counts

        var widths = new[] { 8d, 23, 26, 22, 20, 14, 20, 15, 12, 24, 18, 22, 16, 68, 70, 49, 59, 64 };
        for (var i = 0; i < widths.Length; i++) wb.ColumnWidths.Add((i + 1, widths[i]));
        wb.Rows.Add(new MiniXlsx.OutRow(2) { HeightPoints = 24 }.Text("A", "שטחי חתך מדודים — נתוני מקור, ללא אישור כתב כמויות", 1));
        wb.Rows.Add(new MiniXlsx.OutRow(3) { HeightPoints = 20 }.Text("A", MaterialSectionAreaEvidence.ScopeNotice, 2));
        wb.Rows.Add(new MiniXlsx.OutRow(4) { HeightPoints = 20 }.Text("A",
            $"פרופיל: {context.ProjectProfileId}; ריצת סריקה: {context.ScanRunId}; תצפיות: {observations.Count}. ללא סכום שטחים בין תחנות.", 0));
        wb.Rows.Add(new MiniXlsx.OutRow(5) { HeightPoints = 20 }.Text("A",
            $"שרטוט מארח של הסריקה: {context.SourceDrawingPath}", 0));
        wb.Rows.Add(new MiniXlsx.OutRow(6) { HeightPoints = 20 }.Text("A",
            $"SHA-256 שרטוט מארח: {context.SourceDrawingHash}", 0));
        wb.Rows.Add(new MiniXlsx.OutRow(7) { HeightPoints = 20 }.Text("A",
            $"SHA-256 פרופיל: {context.ProjectProfileHash}; SHA-256 ראיית הסריקה: {context.PublishedScanSha256}", 0));
        wb.Rows.Add(new MiniXlsx.OutRow(8) { HeightPoints = 20 }.Text("A",
            $"תצפיות בכיסוי לא מוכח: {observations.Count(x => !x.CorridorCoverageProven)}; " +
            $"תצפיות ביחידות לא מוכחות: {observations.Count(x => !x.MetricUnitsProven)}. " +
            "הדוח משמר מדידות וחריגים; אינו אישור שלמות או שיוך חומר לסעיף מחירון.", 2));

        // The identity lines are long; in an RTL sheet an 8-wide first column clips them
        // (seen live 2026-09-24: hashes in rows 6/7 not legible). Merge each notice row
        // across the data columns so the complete text is visible without editing cells.
        for (var notice = 2; notice <= 8; notice++) wb.SingleRowMerges.Add($"A{notice}:R{notice}");

        var headers = new[] { "מספר", "קורידור", "ציר / סדרת תחנות", "קוד צורה במקור", "תחנה", "יחידת תחנה",
            "שטח חתך", "יחידת שטח", "מספר צורות", "כיסוי סדרת תחנות", "יחידות המקור", "הוכחת יחידות",
            "מזהה קורידור", "נתיב מקור המדידה", "SHA-256 מקור המדידה", "ריצת המדידה המקורית", "שיטת מדידה", "משמעות המדידה" };
        var heading = new MiniXlsx.OutRow(HeaderRow) { HeightPoints = 34 };
        for (var i = 0; i < headers.Length; i++) heading.Text(Column(i), headers[i], 3);
        wb.Rows.Add(heading);

        var rowIndex = HeaderRow;
        foreach (var value in observations.OrderBy(x => x.CorridorHandle, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.BaselineSeries, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Station).ThenBy(x => x.ShapeCode, StringComparer.OrdinalIgnoreCase))
        {
            var row = new MiniXlsx.OutRow(++rowIndex) { HeightPoints = 48 };
            row.Number("A", (double)(rowIndex - HeaderRow), 7)
                .Text("B", value.CorridorName, 4).Text("C", value.BaselineSeries, 4)
                .Text("D", value.ShapeCode, 4).Number("E", value.Station, 5)
                .Text("F", value.StationUnit, value.MetricUnitsProven ? 4 : 6)
                .Number("G", value.Area, 5).Text("H", value.AreaUnit, value.MetricUnitsProven ? 4 : 6)
                .Number("I", (double)value.ShapeCount, 7)
                .Text("J", value.CorridorCoverageProven ? "מוכח בסריקה" : "חלקי / לא מוכח", value.CorridorCoverageProven ? 4 : 6)
                .Text("K", value.SourceUnits, 4)
                .Text("L", value.MetricUnitsProven ? "מטריות מוכחות" : "אין המרה מטרית מוכחת", value.MetricUnitsProven ? 4 : 6)
                .Text("M", value.CorridorHandle, 4).Text("N", value.DrawingPath, 4)
                .Text("O", value.DrawingHash, 4).Text("P", value.RunId, 4)
                .Text("Q", value.MeasurementMethod, 4).Text("R", value.QuantityMeaning, 4);
            // Accommodate long native names/paths without clipping or truncation.
            var lines = row.Cells.Where(c => c.Kind == MiniXlsx.CellKind.Text)
                .Max(c => WrappedLineCount(c.Value, widths[c.Reference[0] - 'A']));
            row.HeightPoints = Math.Min(409, Math.Max(48, lines * 16 + 8));
            if (lines * 16 + 8 > 409)
                throw new ArgumentException("A material evidence field is too long for a readable worksheet row; it was not truncated.");
            wb.Rows.Add(row);
        }
        return wb;
    }

    private static string Column(int zeroBased) => ((char)('A' + zeroBased)).ToString();

    private static int WrappedLineCount(string text, double width) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / Math.Max(1, width - 2))));

    private static bool SameLocalPath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        // Path canonicalisation only. Never opens a DWG, resolves an XREF, or probes
        // a network drive while serializing the already-captured material evidence.
        return string.Equals(Path.GetFullPath(first.Replace('/', Path.DirectorySeparatorChar)),
            Path.GetFullPath(second.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase);
    }
}
