using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Writes the early-design estimate workbook in the Judgment-2 structure
    /// (directive §27): RTL sheet, familiar columns, chapter hierarchy derived from
    /// catalog codes, REAL formulas for line totals and chapter sums, visual flags
    /// for review/missing-price/unmapped lines, and a machine-readable audit.json.
    /// Never overwrites an existing file.
    /// </summary>
    public static class EstimateExcelWriter
    {
        public sealed record WriteResult(
            string XlsxPath,
            string AuditPath,
            string ManifestPath,
            string XlsxHash,
            string AuditHash,
            string ManifestHash);

        /// <summary>
        /// Human context printed in the workbook header. Everything is optional; the
        /// identifiers from the estimate itself are always printed as well.
        /// </summary>
        public sealed record WriteOptions(
            string? ProjectTitle = null,
            string? PriceBookLabel = null,
            string? DrawingName = null,
            string? PreparedBy = null);

        public static WriteResult Write(EstimateResult estimate, string outputDir, string baseName)
            => Write(estimate, outputDir, baseName, null);

        public static WriteResult Write(EstimateResult estimate, string outputDir, string baseName, WriteOptions? options)
            => WriteCore(estimate, outputDir, baseName, options, partialDraft: false);

        public static WriteResult WritePartialPricedDraft(EstimateResult estimate, string outputDir,
            string baseName, WriteOptions? options = null)
            => WriteCore(estimate, outputDir, baseName, options, partialDraft: true);

        private static WriteResult WriteCore(EstimateResult estimate, string outputDir, string baseName,
            WriteOptions? options, bool partialDraft)
        {
            var draft = partialDraft ? EstimatePartialPricedDraftPolicy.Evaluate(estimate) : null;
            var blockers = draft?.BlockingReasons ?? EstimatePreflightPolicy.ExportBlockingReasons(estimate);
            if (blockers.Count > 0)
                throw new InvalidOperationException(
                    (partialDraft ? "Partial priced draft export is blocked: " :
                    "Estimate export is blocked until every line is Ready, mapped, unit-safe, priced and included: ") +
                    string.Join(", ", blockers));

            var boqGroups = EstimateBoqSemantics.GroupLines(estimate.Lines);
            var canonicalTotal = boqGroups
                .Where(g => g.IncludedInTotals)
                .Sum(g => g.Total ?? 0m);
            if (canonicalTotal != estimate.CleanTotal)
                throw new InvalidOperationException(
                    $"Estimate total is inconsistent with workbook grouping: audit={estimate.CleanTotal}, workbook={canonicalTotal}.");

            EstimateResultArtifact? partialAudit = null;
            if (partialDraft)
            {
                var uniqueFindings = EstimatePreflightPolicy.DistinctEquivalentFindings(
                    estimate.Findings.Concat(estimate.Lines.SelectMany(line => line.Findings))).ToList();
                if (uniqueFindings.Any(finding => string.IsNullOrWhiteSpace(finding.FindingId)) ||
                    uniqueFindings.GroupBy(finding => finding.FindingId, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new InvalidOperationException("Partial audit cannot merge conflicting finding identities; rebuild with unambiguous evidence IDs.");
                // A global coverage finding may be attached to thousands of blocked
                // lines. Reuse the canonical runtime registry, never duplicate its
                // full source-ref list in every audit line. Full export stays v2.
                partialAudit = EstimateResultArtifact.From(estimate);
            }

            Directory.CreateDirectory(outputDir);
            var paths = UniquePackagePaths(outputDir, baseName);
            var nonce = Guid.NewGuid().ToString("N");
            var xlsxTemp = paths.Xlsx + ".tmp-" + nonce;
            var auditTemp = paths.Audit + ".tmp-" + nonce;
            var manifestTemp = paths.Manifest + ".tmp-" + nonce;
            var committed = new List<string>();

            try
            {
                // Dependency-free xlsx (see MiniXlsx): immune to the host's OpenXml build.
                var wb = new MiniXlsx.Workbook { SheetName = "כתב כמויות", RightToLeft = true, ShowGridLines = false };
                AddStyles(wb);
                BuildSheet(estimate, boqGroups, wb, options ?? new WriteOptions(),
                    Path.GetFileName(paths.Manifest), partialDraft);
                MiniXlsx.Write(wb, xlsxTemp);
                var xlsxHash = ArtifactHash.Sha256OfFile(xlsxTemp);
                var generatedAtUtc = DateTime.UtcNow;

                File.WriteAllText(auditTemp, JsonSerializer.Serialize(new
                {
                    schema_version = partialDraft ? 3 : 2,
                    package_kind = partialDraft ? EstimatePartialPricedDraftPolicy.PackageKind : "mahod-estimate-export",
                    draft_notice = partialDraft ? EstimatePartialPricedDraftPolicy.DraftNotice : null,
                    partial_priced_draft = draft,
                    full_estimate_export_allowed = EstimatePreflightPolicy.CanExport(estimate),
                    run_id = estimate.RunId,
                    generated_at_utc = generatedAtUtc,
                    package_manifest = Path.GetFileName(paths.Manifest),
                    workbook_file = Path.GetFileName(paths.Xlsx),
                    workbook_sha256 = xlsxHash,
                    project_profile_id = estimate.ProjectProfileId,
                    project_profile_hash = estimate.ProjectProfileHash,
                    project_profile_hash_kind = estimate.ProjectProfileHashKind,
                    project_profile_effective_hash = estimate.ProjectProfileEffectiveHash,
                    price_book_id = estimate.PriceBookId,
                    price_book_hash = estimate.PriceBookHash,
                    source_snapshot_kind = estimate.SourceSnapshotKind,
                    source_drawing_path = estimate.SourceDrawingPath,
                    source_drawing_hash = estimate.SourceDrawingHash,
                    source_database_revision = estimate.SourceDatabaseRevision,
                    source_dbmod = estimate.SourceDbMod,
                    source_scope_policy = estimate.SourceScopePolicy,
                    source_selection = estimate.SourceSelection,
                    xref_policy = estimate.XrefPolicy,
                    scope_notice = estimate.ScopeNotice,
                    external_sources = estimate.ExternalSources,
                    clean_total = estimate.CleanTotal,
                    boq_groups = boqGroups,
                    exclusions = estimate.Exclusions,
                    excluded_line_count = estimate.ExcludedLineCount,
                    status = estimate.Status.ToString(),
                    lines = partialAudit == null ? (object)estimate.Lines : partialAudit.Lines,
                    findings = partialAudit == null ? (object)estimate.Findings : partialAudit.Findings,
                }, AuditJson));
                var auditHash = ArtifactHash.Sha256OfFile(auditTemp);

                File.WriteAllText(manifestTemp, JsonSerializer.Serialize(new
                {
                    schema_version = 1,
                    package_kind = partialDraft ? EstimatePartialPricedDraftPolicy.PackageKind : "mahod-estimate-export",
                    draft_notice = partialDraft ? EstimatePartialPricedDraftPolicy.DraftNotice : null,
                    run_id = estimate.RunId,
                    generated_at_utc = generatedAtUtc,
                    workbook = new { file = Path.GetFileName(paths.Xlsx), sha256 = xlsxHash },
                    audit = new { file = Path.GetFileName(paths.Audit), sha256 = auditHash },
                    profile = new
                    {
                        id = estimate.ProjectProfileId,
                        source_sha256 = estimate.ProjectProfileHash,
                        source_hash_kind = estimate.ProjectProfileHashKind,
                        effective_sha256 = estimate.ProjectProfileEffectiveHash,
                    },
                    catalog = new { id = estimate.PriceBookId, sha256 = estimate.PriceBookHash },
                    drawing = new
                    {
                        path = estimate.SourceDrawingPath,
                        sha256 = estimate.SourceDrawingHash,
                        live_revision = estimate.SourceDatabaseRevision,
                        dbmod = estimate.SourceDbMod,
                        snapshot_kind = estimate.SourceSnapshotKind,
                    },
                    external_sources = estimate.ExternalSources,
                }, AuditJson));
                var manifestHash = ArtifactHash.Sha256OfFile(manifestTemp);

                // The manifest is moved last and is therefore the package completion
                // marker. If any move fails, every final created by this attempt is
                // removed; callers never receive a partial workbook/audit pair.
                File.Move(xlsxTemp, paths.Xlsx); committed.Add(paths.Xlsx);
                File.Move(auditTemp, paths.Audit); committed.Add(paths.Audit);
                File.Move(manifestTemp, paths.Manifest); committed.Add(paths.Manifest);
                return new WriteResult(paths.Xlsx, paths.Audit, paths.Manifest,
                    xlsxHash, auditHash, manifestHash);
            }
            catch (Exception exportError)
            {
                var cleanupFailures = new List<Exception>();
                foreach (var path in committed.AsEnumerable().Reverse())
                {
                    try { WithdrawCommittedFile(path, nonce); }
                    catch (Exception cleanupError) { cleanupFailures.Add(cleanupError); }
                }
                if (cleanupFailures.Count > 0)
                    throw new InvalidOperationException(
                        "Estimate export failed and at least one incomplete final-looking file could not be deleted or quarantined. The package remains invalid because no complete manifest was returned.",
                        new AggregateException(
                            new[] { exportError }.Concat(cleanupFailures)));
                throw;
            }
            finally
            {
                SafeDelete(xlsxTemp);
                SafeDelete(auditTemp);
                SafeDelete(manifestTemp);
            }
        }

        private static readonly JsonSerializerOptions AuditJson = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // ------------------------------------------------------------- worksheet

        private static void BuildSheet(
            EstimateResult estimate,
            IReadOnlyList<EstimateBoqSemantics.Group> canonicalGroups,
            MiniXlsx.Workbook wb,
            WriteOptions options,
            string manifestFileName,
            bool partialDraft = false)
        {
            int rowIndex = 1;
            int? openingPartialTotalRow = null;
            var unpriced = AddSheet(wb, "לא מתומחר");
            var findings = AddSheet(wb, "ממצאים");
            var trace = AddSheet(wb, "עקבה");
            var identity = AddSheet(wb, "זהות ראיות");
            var isNeutral = string.Equals(estimate.SourceSnapshotKind, EstimateBuildContext.NeutralRecordSet,
                StringComparison.OrdinalIgnoreCase);

            // Header block: what the engineer needs to know before the first number.
            var title = partialDraft ? $"טיוטה מתומחרת חלקית — פרויקט {estimate.ProjectProfileId}"
                : $"אומדן מוקדם — פרויקט {estimate.ProjectProfileId}";
            if (!string.IsNullOrWhiteSpace(options.ProjectTitle)) title += $" — {options.ProjectTitle}";
            wb.Rows.Add(TextRow(rowIndex++, StyleTitle, title, "", "", "", "", "", ""));
            if (partialDraft)
            {
                AddScopeWarning(wb, rowIndex++, EstimatePartialPricedDraftPolicy.DraftNotice);
                var evaluation = EstimatePartialPricedDraftPolicy.Evaluate(estimate);
                wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                    $"שורות עצמאיות מתומחרות: {evaluation.EligibleLineCount}; שורות שלא נכללו בסכום הביניים: {evaluation.UnresolvedLineCount}; ממצאי פרויקט פתוחים: {evaluation.ProjectBlockingFindingCount}. היעדר מדידה אינו אפס."));
                // Reserve a visible opening subtotal; populate its forward reference
                // after the one authoritative total formula is known below.
                openingPartialTotalRow = rowIndex++;
            }

            var priceBook = string.IsNullOrWhiteSpace(options.PriceBookLabel)
                ? estimate.PriceBookId
                : $"{options.PriceBookLabel} ({estimate.PriceBookId})";
            wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                $"מחירון: {priceBook}  ·  מהדורה: {Short(estimate.PriceBookHash)}  ·  מחיר יחידה בדיוק המקור.", "", "", "", "", "", ""));
            wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                "סכום שורה = כמות מצטברת × מחיר יחידה, בעיגול לאגורה פעם אחת לשורת כתב־כמויות."));

            var produced = $"הופק: {DateTime.Now:dd/MM/yyyy HH:mm}  ·  Mahod Civil Delivery";
            if (!string.IsNullOrWhiteSpace(options.DrawingName)) produced = $"{(isNeutral ? "מקור רשומות" : "שרטוט")}: {MahodAI.CivilDelivery.Shared.Bidi.Ltr(options.DrawingName)}  ·  " + produced;
            if (!string.IsNullOrWhiteSpace(options.PreparedBy)) produced += $"  ·  הוכן ע\"י: {options.PreparedBy}";
            wb.Rows.Add(TextRow(rowIndex++, StyleNote, produced, "", "", "", "", "", ""));
            wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                $"{(isNeutral ? "כמויות מרשומות מקור; לא בוצע אימות CAD חי" : "כמויות מהגיאומטריה בשרטוט")}; סעיף ללא מחיר או ללא שיוך אינו אפס — הוא מסומן ואינו נכלל בסה\"כ.  ·  מזהה הרצה: {MahodAI.CivilDelivery.Shared.Bidi.Ltr(estimate.RunId)}",
                "", "", "", "", "", ""));
            wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                "זהות המקורות המלאה, נתיבים וחתימות SHA-256 מופיעים בגיליון זהות ראיות.",
                "", "", "", "", "", ""));
            wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                "שורות שלא נכללו בסכום בגיליון לא מתומחר; סיבות מרוכזות בגיליון ממצאים.",
                "", "", "", "", "", ""));
            wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                $"{(isNeutral ? "מקורות חיצוניים מדווחים ברשומות (ללא אימות XREF חי)" : "מקורות XREF מאומתים")}: {estimate.ExternalSources.Count}  ·  פרטים מלאים בגיליון זהות ראיות וב-manifest",
                "", "", "", "", "", ""));
            AddScopeWarning(wb, rowIndex++,
                estimate.SourceSelection != null ? estimate.ScopeNotice!
                    : isNeutral ? EstimatePreflightPolicy.NeutralRecordScopeNotice
                    : estimate.ScopeNotice ?? EstimatePreflightPolicy.HostOnlyScopeNotice);
            rowIndex++; // blank

            // Header
            wb.Rows.Add(TextRow(rowIndex, StyleHeader,
                "מספר", "מס' קטלוגי", "תאור", "יח' מידה", "כמות", "מחיר יחידה (דיוק מקור)", "סה\"כ", "סטטוס"));
            wb.FreezeTopRows = rowIndex;
            rowIndex++;

            // One BOQ row per catalog item (code + unit + price), summing the quantity of
            // every drawing object mapped to it. The per-object lines remain in the
            // EstimateResult / audit.json for traceability; the SHEET reads like an
            // estimate, not like a dump of 437 kerb polylines (first real export, 2026-08-19).
            var boqRows = canonicalGroups
                .Select(g =>
                {
                    var verdict = g.CatalogCode == null
                        ? QuantitySignificance.Classify(new QuantitySignificance.Group(
                            "", g.SourceLayer, g.Unit, (double)g.Quantity, g.ObjectCount))
                        : null;
                    return new BoqRow(
                        CatalogCode: g.CatalogCode,
                        Description: g.Description
                                     ?? (verdict == null ? null
                                        : verdict.IsLikelyQuantity
                                            ? $"שכבה {g.SourceLayer ?? "?"} — ללא מיפוי קטלוגי"
                                            : $"שכבה {g.SourceLayer ?? "?"} — {verdict.Reason}"),
                        Unit: g.Unit,
                        Quantity: g.Quantity,
                        Price: g.Price,
                        IncludedInTotals: g.IncludedInTotals,
                        Status: g.Status,
                        ObjectCount: g.ObjectCount,
                        SortRank: verdict == null ? 0 : verdict.IsLikelyQuantity ? 0
                                  : verdict.Kind == QuantitySignificance.Kind.ExistingUtility ? 1 : 2);
                })
                .ToList();

            // Priced chapters first, in catalog order; the unmapped appendix LAST - an
            // estimate opens on what is priced, not on what is not.
            var grouped = boqRows.Where(row => row.IncludedInTotals)
                .GroupBy(r => Chapter(r.CatalogCode))
                .OrderBy(g => g.Key == "" ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.Ordinal);

            int chapterSeq = 0;
            var allLineRows = new List<int>();

            foreach (var group in grouped)
            {
                chapterSeq++;
                var chapterLabel = group.Key == "" ? "נספח — שכבות שנמדדו ללא שיוך לסעיף (לא כלולות בסה\"כ)" : $"פרק {group.Key}";
                wb.Rows.Add(TextRow(rowIndex++, StyleChapter,
                    $"{chapterSeq:00}.{(group.Key == "" ? "00" : group.Key)}.00.0000", "",
                    chapterLabel, "", "", "", "", ""));

                var lineRows = new List<int>();
                int itemSeq = 0;
                foreach (var line in group.OrderBy(l => l.SortRank).ThenBy(l => l.CatalogCode ?? "~", StringComparer.Ordinal).ThenBy(l => l.Unit))
                {
                    itemSeq++;
                    var style = line.IncludedInTotals ? StyleItem : StyleFlagged;
                    var num = line.IncludedInTotals ? StyleItemNum : StyleFlaggedNum;
                    var priceNum = line.IncludedInTotals ? StyleItemPrice : StyleFlaggedPrice;
                    var quantityNum = line.IncludedInTotals ? StyleItemQuantity : StyleFlaggedQuantity;

                    var row = new MiniXlsx.OutRow(rowIndex)
                        .Text("A", $"{chapterSeq:00}.{itemSeq:000}", style)
                        .Text("B", line.CatalogCode ?? "—", style)
                        .Text("C", (line.Description ?? "(ללא תיאור קטלוגי)") +
                                   (line.ObjectCount > 1 ? $"  [{line.ObjectCount} עצמים]" : ""), style)
                        .Text("D", line.Unit, style);
                    if (!partialDraft || line.IncludedInTotals || line.Quantity > 0)
                        row.Number("E", line.Quantity, quantityNum);
                    else row.Text("E", "לא נמדד / לא תקין", StyleFlagged);
                    if (partialDraft && !line.IncludedInTotals) row.Text("F", "לא נכלל בתמחור הטיוטה", StyleFlagged);
                    else if (line.Price != null) row.Number("F", line.Price.Value, priceNum);
                    else row.Text("F", "חסר מחיר", StyleFlagged);
                    if (line.IncludedInTotals) row.Formula("G", $"ROUND(E{rowIndex}*F{rowIndex},2)", num);
                    else row.Text("G", "", StyleFlagged);
                    row.Text("H", line.Status, style);
                    wb.Rows.Add(row);

                    if (line.IncludedInTotals)
                    {
                        lineRows.Add(rowIndex);
                        allLineRows.Add(rowIndex);
                    }
                    rowIndex++;
                }

                if (lineRows.Count > 0)
                {
                    wb.Rows.Add(new MiniXlsx.OutRow(rowIndex)
                        .Text("A", "", StyleChapterSum)
                        .Text("B", "", StyleChapterSum)
                        .Text("C", $"סה\"כ {chapterLabel}", StyleChapterSum)
                        .Text("D", "", StyleChapterSum)
                        .Text("E", "", StyleChapterSum)
                        .Text("F", "", StyleChapterSum)
                        .Formula("G", SumFormula(lineRows), StyleChapterSumNum)
                        .Text("H", "", StyleChapterSum));
                    rowIndex++;
                }
                rowIndex++; // spacer
            }

            // Grand total over line rows (not chapter rows, avoids double count).
            var grand = new MiniXlsx.OutRow(rowIndex)
                .Text("A", "", StyleGrand)
                .Text("B", "", StyleGrand)
                .Text("C", partialDraft ? EstimatePartialPricedDraftPolicy.SubtotalNotice : "סה\"כ כללי (שורות תקינות בלבד)", StyleGrand)
                .Text("D", "", StyleGrand)
                .Text("E", "", StyleGrand)
                .Text("F", "", StyleGrand);
            if (allLineRows.Count > 0) grand.Formula("G", SumFormula(allLineRows), StyleGrandNum);
            else grand.Text("G", "0", StyleGrand);
            grand.Text("H", "", StyleGrand);
            wb.Rows.Add(grand);
            if (openingPartialTotalRow is int openingRow)
                wb.Rows.Add(new MiniXlsx.OutRow(openingRow)
                    .Text("C", EstimatePartialPricedDraftPolicy.SubtotalNotice, StyleGrand)
                    .Formula("G", $"G{rowIndex}", StyleGrandNum));
            rowIndex += 2;

            if (estimate.ExcludedLineCount > 0)
            {
                wb.Rows.Add(TextRow(rowIndex++, StyleNote,
                    $"שים לב: {estimate.ExcludedLineCount} שורות אינן כלולות בסה\"כ (חסר מחיר / ללא מיפוי / דרושה בדיקה) — ראה גיליון לא מתומחר ו-audit.json.",
                    "", "", "", "", "", ""));
            }

            if (estimate.Exclusions.Count > 0)
            {
                rowIndex += 2;
                wb.Rows.Add(TextRow(rowIndex++, StyleChapter,
                    "נספח ביקורת — כמויות שהוחרגו בהחלטה הנדסית", "", "", "", "", "", "", ""));
                wb.Rows.Add(TextRow(rowIndex++, StyleHeader,
                    "מספר", "מפתח חוק", "סיבה הנדסית", "יח' מידה", "כמות", "מקורות (שרטוט#ידית@hash)", "מאשר", "זמן אישור UTC"));

                var exclusionNumber = 0;
                foreach (var exclusion in estimate.Exclusions.OrderBy(x => x.RuleKey, StringComparer.Ordinal))
                {
                    var byUnit = exclusion.Sources
                        .GroupBy(s => s.Unit, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal);
                    foreach (var unitGroup in byUnit)
                    {
                        exclusionNumber++;
                        var sources = string.Join("; ", unitGroup.Select(s =>
                            $"{Path.GetFileName(s.DrawingPath ?? s.Drawing)}#{s.Handle}@{Short(s.DrawingHash)}" +
                            (string.IsNullOrWhiteSpace(s.Xref) ? "" : $"[{s.Xref}]")));
                        wb.Rows.Add(new MiniXlsx.OutRow(rowIndex++)
                            .Text("A", exclusionNumber.ToString(), StyleNote)
                            .Text("B", exclusion.RuleKey, StyleNote)
                            .Text("C", exclusion.Reason, StyleNote)
                            .Text("D", unitGroup.Key, StyleNote)
                            .Number("E", (decimal)unitGroup.Sum(s => s.RawValue), StyleItemQuantity)
                            .Text("F", sources, StyleNote)
                            .Text("G", exclusion.ApprovedBy, StyleNote)
                            .Text("H", exclusion.ApprovedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'"), StyleNote));
                    }
                }
            }

            BuildUnpricedSheet(estimate, unpriced);
            BuildFindingsSheet(estimate, findings);
            var identityRow = 1;
            identity.Rows.Add(TextRow(identityRow++, StyleTitle, "זהות ראיות — המקורות המלאים של הייצוא"));
            identity.Rows.Add(TextRow(identityRow++, StyleHeader, "סוג", "ערך"));
            foreach (var item in new (string Label, string? Value)[]
            {
                ("ריצה", estimate.RunId), ("פרופיל SHA-256", estimate.ProjectProfileHash),
                ("זהות פרופיל אפקטיבית", estimate.ProjectProfileEffectiveHash),
                ("שרטוט", estimate.SourceDrawingPath), ("שרטוט SHA-256", estimate.SourceDrawingHash),
                ("גרסת מסד נתונים", estimate.SourceDatabaseRevision),
                ("מחירון", estimate.PriceBookId), ("מחירון SHA-256", estimate.PriceBookHash),
                ("מניפסט החבילה", manifestFileName),
                ("כל הרשומות והממצאים ללא קיצור", manifestFileName.Replace(".manifest.json", ".audit.json")),
            }) identity.Rows.Add(TextRow(identityRow++, StyleNote, item.Label, item.Value ?? "?"));
            if (estimate.SourceSelection is { } selectedSources)
            {
                identity.Rows.Add(TextRow(identityRow++, StyleChapter, "בחירת מקורות — לפני מדידה"));
                identity.Rows.Add(TextRow(identityRow++, StyleNote, "מאשר / זמן UTC", $"{selectedSources.ApprovedBy} / {Utc(selectedSources.ApprovedAtUtc)}"));
                identity.Rows.Add(TextRow(identityRow++, StyleNote, "חתימת מצאי", selectedSources.InventoryHash));
                identity.Rows.Add(TextRow(identityRow++, StyleHeader, "מקור", "נתיב מוגדר", "קטגוריה", "היקף", "ישויות מוחרגות"));
                foreach (var source in selectedSources.Sources)
                    identity.Rows.Add(TextRow(identityRow++, StyleNote, source.Name, source.Path,
                        EstimateSourceSelectionPolicy.Categories.GetValueOrDefault(source.Category, source.Category),
                        source.Included ? "כלול עם הענף המקונן" : "מוחרג עם הענף המקונן — לא נמדד",
                        source.Included ? "לא חל" : "לא ידוע — המקור לא נמדד"));
            }
            rowIndex = 1;
            trace.Rows.Add(TextRow(rowIndex++, StyleTitle, "עקבת השורות הכלולות בסכום"));
            trace.Rows.Add(TextRow(rowIndex++, StyleNote, "עקבה מלאה לכל שורה מתומחרת; כל הרשומות, כולל הלא מתומחרות, נשמרות בקובץ audit.json שבחבילה."));
            trace.Rows.Add(TextRow(rowIndex++, StyleHeader,
                "שורה / רשומה", "שרטוט", "גרסה / SHA-256", "עצם מקור", "שיטת מדידה",
                "חוק ואישור מיפוי", "זהות קטלוג", "מקור מחיר / התאמות"));
            trace.FreezeTopRows = 3;
            foreach (var line in estimate.Lines.Where(l => l.IncludedInTotals).OrderBy(l => l.LineId, StringComparer.Ordinal))
            {
                var drawing = line.SourceDrawingPath ?? line.SourceDrawing ?? "?";
                var sourceObject = $"handle={line.SourceHandle}; type={line.SourceEntityType}; layer={line.SourceLayer}";
                if (!string.IsNullOrWhiteSpace(line.SourceXref))
                    sourceObject += $"; xref={line.SourceXref}";
                if (!string.IsNullOrWhiteSpace(line.SourceCivilIdentity))
                    sourceObject += $"; civil={line.SourceCivilIdentity}";
                if (line.SourceStationFrom != null || line.SourceStationTo != null)
                    sourceObject += $"; station={line.SourceStationFrom:R}..{line.SourceStationTo:R}";
                var mapping = $"{line.RuleKey}; {line.MappingApprovedBy}@{Utc(line.MappingApprovedAtUtc)}";
                var catalog = $"{line.ApprovedCatalogId}/{line.CatalogCode}; hash={line.ApprovedCatalogHash}; item={line.ApprovedCatalogItemFingerprint}";
                var adjustments = line.Adjustments.Count == 0
                    ? "none"
                    : string.Join("; ", line.Adjustments.Select(a =>
                        $"{a.RuleId} x{a.Factor:R} ({a.Source}; {a.ApprovedBy}@{Utc(a.ApprovedAtUtc)})"));
                var price = line.PriceStatus switch
                {
                    PriceStatus.Priced => $"מחירון {line.PriceBookId ?? estimate.PriceBookId}; סעיף {line.CatalogCode}; " +
                        $"SHA-256={estimate.PriceBookHash}; התאמות: {adjustments}",
                    PriceStatus.ProjectOverride => $"מחיר פרויקט; מקור: {line.PriceDecisionSource}; נימוק: {line.PriceDecisionReason}; " +
                        $"אישור: {line.PriceApprovedBy}@{Utc(line.PriceApprovedAtUtc)}; התאמות: {adjustments}",
                    _ => $"לא תומחר ({line.PriceStatus}); התאמות: {adjustments}",
                };
                trace.Rows.Add(TextRow(rowIndex++, StyleNote,
                    $"{line.LineId} / {line.RecordId}", drawing,
                    $"{line.SourceDatabaseRevision} / {line.SourceDrawingHash}",
                    sourceObject, line.MeasurementMethod ?? "?", mapping, catalog, price));
            }

            if (estimate.ExternalSources.Count > 0)
            {
                identityRow += 2;
                identity.Rows.Add(TextRow(identityRow++, StyleChapter,
                    "נספח מקורות XREF — קבצים, שרשראות וזהות snapshot", "", "", "", "", "", "", ""));
                identity.Rows.Add(TextRow(identityRow++, StyleHeader,
                    "מספר", "קובץ XREF", "SHA-256", "שרשרת XREF", "נתיב ידיות הפניה", "", "", ""));
                var sourceNumber = 0;
                foreach (var source in estimate.ExternalSources
                             .OrderBy(item => item.DrawingPath, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.ReferenceHandlePath, StringComparer.Ordinal))
                {
                    sourceNumber++;
                    identity.Rows.Add(TextRow(identityRow++, StyleNote,
                        sourceNumber.ToString(), source.DrawingPath, source.DrawingHash,
                        source.XrefChain, source.ReferenceHandlePath, "", "", ""));
                }
            }

            wb.ColumnWidths.AddRange(new[] { (1, 12.0), (2, 21.0), (3, 58.0), (4, 12.0), (5, 20.0), (6, 26.0), (7, 22.0), (8, 20.0) });
            unpriced.ColumnWidths.AddRange(new[] { (1, 10.0), (2, 21.0), (3, 58.0), (4, 12.0), (5, 20.0), (6, 28.0), (7, 14.0), (8, 36.0) });
            findings.ColumnWidths.AddRange(new[] { (1, 38.0), (2, 24.0), (3, 42.0), (4, 34.0), (5, 18.0), (6, 42.0), (7, 48.0), (8, 36.0) });
            trace.ColumnWidths.AddRange(new[] { (1, 30.0), (2, 42.0), (3, 42.0), (4, 42.0), (5, 28.0), (6, 38.0), (7, 42.0), (8, 48.0) });
            identity.ColumnWidths.AddRange(new[] { (1, 38.0), (2, 70.0), (3, 70.0), (4, 36.0), (5, 30.0), (6, 20.0), (7, 20.0), (8, 20.0) });
            SizeWrappedRows(wb, wb.Styles);
            foreach (var sheet in wb.AdditionalSheets) SizeWrappedRows(sheet, wb.Styles);
        }

        private static MiniXlsx.Worksheet AddSheet(MiniXlsx.Workbook wb, string name)
        {
            var sheet = new MiniXlsx.Worksheet { SheetName = name, RightToLeft = true, ShowGridLines = false };
            wb.AdditionalSheets.Add(sheet);
            return sheet;
        }

        private static void BuildUnpricedSheet(EstimateResult estimate, MiniXlsx.Worksheet sheet)
        {
            sheet.Rows.Add(TextRow(1, StyleTitle, "קבוצות שלא נכללו בסכום — אין כאן מחיר או סכום מאושר"));
            sheet.Rows.Add(TextRow(2, StyleNote, "הכמויות נשמרו; יש להשלים מדידה, שיוך או אישור בכלי. הפרטים המלאים לכל עצם בקובץ audit.json."));
            sheet.Rows.Add(TextRow(3, StyleHeader, "מספר", "סעיף / הצעה", "תיאור / שכבה", "יחידה", "כמות", "מצב", "עצמים", "פעולה הבאה"));
            sheet.FreezeTopRows = 3;
            var number = 0;
            // Zero on a failed line can be a storage sentinel, not a measurement.
            // Keep unavailable quantities separate so a valid peer cannot hide them.
            var groups = estimate.Lines.Where(line => !line.IncludedInTotals)
                .GroupBy(line => line.BoqQuantity > 0 ? 1 :
                    line.BoqQuantity == 0 && double.IsFinite(line.RawQuantity) && line.RawQuantity > 0 &&
                    !line.Findings.Any(f => f.Code == EstimateFindingCodes.MeasurementFailed && EstimatePreflightPolicy.IsBlocking(f)) ? 2 : 0)
                .SelectMany(bucket => EstimateBoqSemantics.GroupLines(bucket)
                    .Select(group => (Group: group, QuantityState: bucket.Key)))
                .OrderBy(item => item.Group.CatalogCode ?? item.Group.SourceLayer, StringComparer.Ordinal);
            foreach (var item in groups)
            {
                var group = item.Group;
                number++;
                var row = new MiniXlsx.OutRow(number + 3)
                    .Text("A", number.ToString(), StyleFlagged)
                    .Text("B", group.CatalogCode ?? "—", StyleFlagged)
                    .Text("C", group.Description ?? group.SourceLayer ?? "?", StyleFlagged)
                    .Text("D", group.Unit, StyleFlagged);
                if (item.QuantityState == 1) row.Number("E", group.Quantity, StyleFlaggedQuantity);
                else row.Text("E", item.QuantityState == 2 ? "כמות חיובית שעוגלה ל־0; ראה מדידה מקורית" : "לא נמדד / לא תקין", StyleFlagged);
                row.Text("F", group.Status, StyleFlagged)
                    .Number("G", (decimal)group.ObjectCount, StyleFlagged)
                    .Text("H", "השלמת הממצא ואישור בכלי; לא נכלל בסכום", StyleFlagged);
                sheet.Rows.Add(row);
            }
            if (number == 0) sheet.Rows.Add(TextRow(4, StyleNote, "אין קבוצות לא מתומחרות."));
            if (number > 0) sheet.AutoFilterRange = $"A3:H{number + 3}";
        }

        /// <summary>One printed estimate row: a catalog item with its summed quantity.</summary>
        private sealed record BoqRow(
            string? CatalogCode, string? Description, string Unit, decimal Quantity,
            decimal? Price, bool IncludedInTotals, string Status, int ObjectCount,
            int SortRank = 0);

        private static string Chapter(string? code) =>
            code == null ? "" : CatalogItem.ChapterOf(code);

        /// <summary>SUM over the given rows; contiguous runs collapse to ranges (G6:G40).</summary>
        private static string SumFormula(List<int> rows)
        {
            var parts = new List<string>();
            var sorted = rows.OrderBy(r => r).ToList();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                parts.Add(j > i ? $"G{sorted[i]}:G{sorted[j]}" : $"G{sorted[i]}");
                i = j + 1;
            }
            return "SUM(" + string.Join(",", parts) + ")";
        }

        // ------------------------------------------------------------ primitives

        private const int StyleDefault = 0;
        private const int StyleTitle = 1;
        private const int StyleHeader = 2;
        private const int StyleChapter = 3;
        private const int StyleItem = 4;
        private const int StyleFlagged = 5;
        private const int StyleChapterSum = 6;
        private const int StyleGrand = 7;
        private const int StyleNote = 8;
        private const int StyleItemNum = 9;
        private const int StyleFlaggedNum = 10;
        private const int StyleChapterSumNum = 11;
        private const int StyleGrandNum = 12;
        private const int StyleItemQuantity = 13;
        private const int StyleFlaggedQuantity = 14;
        private const int StyleScopeWarning = 15;
        private const int StyleWrappedNote = 16;
        private const int StyleItemPrice = 17;
        private const int StyleFlaggedPrice = 18;

        /// <summary>Style table; index meaning is fixed by the constants above.</summary>
        private static void AddStyles(MiniXlsx.Workbook wb)
        {
            // index 0 (default) is pre-seeded by the workbook
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 14));                                            // 1 title
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFD9E1F2", ThinTopBottomBorder: true, WrapText: true)); // 2 header
            wb.Styles.Add(new MiniXlsx.Style(Bold: true));                                                           // 3 chapter
            wb.Styles.Add(new MiniXlsx.Style(WrapText: true));                                                        // 4 item
            wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", WrapText: true));                                   // 5 flagged
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true));          // 6 chapter sum
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true));          // 7 grand
            wb.Styles.Add(new MiniXlsx.Style(Italic: true, FontSize: 9));                                            // 8 note
            // Numeric twins of 4/5/6/7 with "#,##0.00": quantities, prices and totals read
            // like an estimate (33,476.09), not like a debugger dump (33476.0935).
            wb.Styles.Add(new MiniXlsx.Style(NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));                                                        // 9 item number
            wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", NumFmtId: MiniXlsx.NumFmtThousands2, AlignTop: true));                                   // 10 flagged number
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2));          // 11 chapter sum number
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFE2EFDA", ThinTopBottomBorder: true, NumFmtId: MiniXlsx.NumFmtThousands2));          // 12 grand number
            wb.Styles.Add(new MiniXlsx.Style(NumFmtId: MiniXlsx.NumFmtThousands4, AlignTop: true));                                                        // 13 item quantity
            wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", NumFmtId: MiniXlsx.NumFmtThousands4, AlignTop: true));                                   // 14 flagged quantity
            wb.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "FFFFC7CE", WrapText: true, AlignTop: true) { RightToLeft = true });                      // 15 scope warning
            wb.Styles.Add(new MiniXlsx.Style(Italic: true, FontSize: 9, WrapText: true));                                                                    // 16 trace note
            // General is a supported native numeric format: preserve the source
            // price precision instead of showing 94.72 while multiplying 94.72001286.
            // The existing 38-character price column accommodates native precision.
            wb.Styles.Add(new MiniXlsx.Style(AlignTop: true));                                                                                             // 17 numeric source price
            wb.Styles.Add(new MiniXlsx.Style(FillRgb: "FFFFF2CC", AlignTop: true));                                                                          // 18 flagged source price
        }

        private static void AddScopeWarning(MiniXlsx.Workbook wb, int rowIndex, string text)
        {
            wb.Rows.Add(TextRow(rowIndex, StyleScopeWarning, text));
            wb.SingleRowMerges.Add($"A{rowIndex}:H{rowIndex}");
        }

        private static MiniXlsx.OutRow TextRow(int rowIndex, int style, params string[] values)
        {
            var row = new MiniXlsx.OutRow(rowIndex);
            // Header notes span empty neighbours; multi-field trace rows cannot.
            if (style == StyleNote && values.Count(value => !string.IsNullOrEmpty(value)) > 1)
                style = StyleWrappedNote;
            for (int i = 0; i < values.Length; i++)
            {
                // An empty-string cell is still a cell: it blocks the neighbour's text
                // overflow, which truncated the title/notes in column A. Skip empties.
                if (string.IsNullOrEmpty(values[i])) continue;
                var col = ((char)('A' + i)).ToString();
                row.Text(col, values[i], style);
            }
            return row;
        }

        private static void BuildFindingsSheet(EstimateResult estimate, MiniXlsx.Worksheet sheet)
        {
            sheet.Rows.Add(TextRow(1, StyleTitle, "סיכום ממצאים — החסמים נשמרים, לא נעקפו"));
            sheet.Rows.Add(TextRow(2, StyleNote, "עד 20 דוגמאות לכל קבוצה. כל הממצאים, המקורות וההכרעות ללא קיצור נמצאים בקובץ audit.json שבחבילה."));
            sheet.Rows.Add(TextRow(3, StyleHeader, "קוד", "חומרה / מצב", "כותרת", "מספר ממצאים / פרטי דוגמה", "רשומות מושפעות", "פעולה הבאה", "מקור לדוגמה", "הערה / זהות"));
            sheet.FreezeTopRows = 3;
            var rowIndex = 4;
            var all = EstimatePreflightPolicy.DistinctEquivalentFindings(
                estimate.Findings.Concat(estimate.Lines.SelectMany(line => line.Findings)));
            foreach (var group in all.GroupBy(f => new { f.Code, f.Severity,
                         Blocking = EstimatePreflightPolicy.IsBlocking(f), Resolved = f.ResolvedAtUtc != null })
                         .OrderByDescending(g => g.Key.Blocking).ThenBy(g => g.Key.Code, StringComparer.Ordinal))
            {
                var first = group.First();
                sheet.Rows.Add(new MiniXlsx.OutRow(rowIndex++)
                    .Text("A", group.Key.Code, StyleHeader)
                    .Text("B", $"{group.Key.Severity}; {(group.Key.Resolved ? "הוכרע" : group.Key.Blocking ? "חוסם" : "לבדיקה")}", StyleHeader)
                    .Text("C", SummaryText(first.Title), StyleHeader)
                    .Number("D", (decimal)group.Count(), StyleHeader)
                    .Number("E", (decimal)group.SelectMany(f => f.AffectedRecordIds).Distinct(StringComparer.Ordinal).Count(), StyleHeader)
                    .Text("F", SummaryText(first.RecommendedAction), StyleHeader)
                    .Text("H", "סיכום קבוצה; הפירוט המלא ב-audit.json", StyleHeader));
                foreach (var finding in group.OrderBy(f => f.FindingId, StringComparer.Ordinal).Take(20))
                {
                    sheet.Rows.Add(TextRow(rowIndex++, StyleWrappedNote,
                        "דוגמה", "", SummaryText(finding.Title), SummaryText(finding.Message),
                        SummaryText(string.Join(", ", finding.AffectedRecordIds)), SummaryText(finding.RecommendedAction),
                        SummaryText(JsonSerializer.Serialize(finding.SourceRefs, AuditJson)),
                        SummaryText($"{finding.FindingId}; {finding.ResolvedBy}; {finding.ResolvedAtUtc:O}; {finding.Resolution}")));
                }
            }
            if (rowIndex == 4) sheet.Rows.Add(TextRow(4, StyleNote, "אין ממצאים."));
        }

        private static string SummaryText(string? text)
        {
            text ??= "";
            if (text.Length <= 240) return text;
            var length = char.IsHighSurrogate(text[239]) ? 239 : 240;
            return text.Substring(0, length) + "… [המשך ב-audit.json]";
        }

        private static void SizeWrappedRows(MiniXlsx.Worksheet wb, IReadOnlyList<MiniXlsx.Style> styles)
        {
            var widths = wb.ColumnWidths.ToDictionary(pair => pair.Column, pair => pair.Width);
            foreach (var row in wb.Rows)
            {
                double height = 0;
                foreach (var cell in row.Cells.Where(cell => cell.Kind == MiniXlsx.CellKind.Text))
                {
                    var style = styles[cell.Style];
                    if (!style.WrapText) continue;
                    var column = cell.Reference[0] - 'A' + 1; // this sheet has only A:H
                    // Conservative font-relative estimate, including word-wrap slack.
                    // Explicit height is required by headless/Excel preview renderers;
                    // wrap alone leaves the default one-line height and still clips.
                    var mergedNotice = wb.SingleRowMerges.Contains($"A{row.Index}:H{row.Index}");
                    var width = mergedNotice ? widths.Values.Sum() : widths[column];
                    var capacity = Math.Max(8, (int)(width * 11 / style.FontSize * 0.9));
                    var lines = 0;
                    foreach (var paragraph in cell.Value.Replace("\r", "").Split('\n'))
                    {
                        var used = 0;
                        lines++;
                        foreach (var word in paragraph.Split(' '))
                        {
                            var required = word.Length + (used == 0 ? 0 : 1);
                            if (used > 0 && used + required > capacity) { lines++; used = 0; }
                            if (word.Length > capacity)
                            {
                                lines += (word.Length - 1) / capacity;
                                used = (word.Length - 1) % capacity + 1;
                            }
                            else used += word.Length + (used == 0 ? 0 : 1);
                        }
                    }
                    height = Math.Max(height, Math.Max(mergedNotice ? 30 : 0, lines * style.FontSize * 1.35 + 8));
                }
                if (height > 0) row.HeightPoints = Math.Min(409, Math.Ceiling(height));
            }
        }

        private sealed record PackagePaths(string Xlsx, string Audit, string Manifest);

        private static PackagePaths UniquePackagePaths(string dir, string baseName)
        {
            for (var i = 0; ; i++)
            {
                var suffix = i == 0 ? "" : $" ({i})";
                var stem = Path.Combine(dir, baseName + suffix);
                var paths = new PackagePaths(
                    stem + ".xlsx", stem + ".audit.json", stem + ".manifest.json");
                if (!File.Exists(paths.Xlsx) && !File.Exists(paths.Audit) &&
                    !File.Exists(paths.Manifest)) return paths;
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// Removes a final-path member of an export attempt. If an external scanner
        /// or filesystem prevents deletion, rename it away from every supported final
        /// extension so users and UI enumeration cannot mistake it for a workbook or
        /// audit package. Failure to do either is surfaced, never swallowed.
        /// </summary>
        internal static void WithdrawCommittedFile(string path, string nonce)
        {
            if (!File.Exists(path)) return;
            Exception? deleteError = null;
            try
            {
                File.Delete(path);
                if (!File.Exists(path)) return;
            }
            catch (Exception ex)
            {
                deleteError = ex;
            }

            var quarantine = path + ".FAILED-" + nonce;
            for (var suffix = 1; File.Exists(quarantine); suffix++)
                quarantine = path + ".FAILED-" + nonce + "-" + suffix;
            try
            {
                File.Move(path, quarantine);
                if (!File.Exists(path)) return;
                throw new IOException(
                    "The incomplete export still exists at its final path after quarantine.");
            }
            catch (Exception moveError)
            {
                var causes = deleteError == null
                    ? new[] { moveError }
                    : new[] { deleteError, moveError };
                throw new IOException(
                    "Could not withdraw or quarantine incomplete estimate export member: " + path,
                    new AggregateException(causes));
            }
        }

        private static string Utc(DateTime? value) => value == null
            ? "?"
            : value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'");

        private static string Short(string? hash) =>
            string.IsNullOrEmpty(hash) ? "?" : hash[..Math.Min(12, hash.Length)];
    }
}
