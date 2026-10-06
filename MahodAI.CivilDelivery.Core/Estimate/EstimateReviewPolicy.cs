using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Read-only presentation of existing evidence. Never changes approval or export authority.</summary>
public static class EstimateReviewPolicy
{
    public enum Recovery { Inspect, Mapping, Price, Sources, Catalog }

    public sealed record Issue(
        string Stage, string Code, string Title, string Detail, string NextStep,
        bool Blocking, Recovery Action, IReadOnlyList<string> RecordIds,
        IReadOnlyList<string> RuleKeys)
    {
        public IReadOnlyList<ProvenanceRef> Sources { get; init; } = Array.Empty<ProvenanceRef>();
    }

    public static IReadOnlyList<Issue> Collect(
        IEnumerable<NeutralQuantityRecord> records,
        IEnumerable<DeliveryFinding> scanFindings,
        IEnumerable<DeliveryFinding> catalogFindings,
        EstimateResult? result)
    {
        var sourceRecords = records.ToList();
        // Duplicate record IDs are not enough evidence to choose an object. This
        // index is navigation-only and never supplies measurement/approval data.
        var sourceByRecord = sourceRecords.GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var ruleByRecord = sourceRecords.Select(record => (record.RecordId, RuleKey: record.Classification.RuleKey ?? ""))
            .Concat(result?.Lines.Select(line => (line.RecordId, RuleKey: line.RuleKey ?? "")) ??
                Enumerable.Empty<(string RecordId, string RuleKey)>())
            .GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(record => record.RuleKey)
                .Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var findings = new List<(string Stage, DeliveryFinding Finding, string? RecordId)>();
        findings.AddRange(scanFindings.Select(finding => ("סריקה", finding, (string?)null)));
        findings.AddRange(catalogFindings.Select(finding => ("מחירון", finding, (string?)null)));
        findings.AddRange(sourceRecords.SelectMany(record => record.Findings.Select(finding =>
            ("מדידה", finding, (string?)record.RecordId))));
        if (result != null)
        {
            findings.AddRange(result.Findings.Select(finding => ("אומדן", finding, (string?)null)));
            findings.AddRange(result.Lines.SelectMany(line => line.Findings.Select(finding =>
                ("שורת אומדן", finding, (string?)line.RecordId))));
        }

        // A shared finding can appear on both the scan and many lines. Keep one
        // issue with every affected identity and every stage, not N repeated texts.
        var issues = findings.GroupBy(entry => (entry.Finding.FindingId, entry.Finding.Code,
                entry.Finding.Title, entry.Finding.Message, entry.Finding.Severity, entry.Finding.RecommendedAction))
            .Select(group =>
            {
                var finding = group.First().Finding;
                var ids = group.SelectMany(entry => entry.Finding.AffectedRecordIds
                        .Concat(entry.RecordId == null ? Array.Empty<string>() : new[] { entry.RecordId }))
                    .Distinct(StringComparer.Ordinal).ToList();
                var action = RecoveryFor(finding.Code);
                var capturedSources = group.Select(entry => entry.Finding)
                    .Distinct<DeliveryFinding>(ReferenceEqualityComparer.Instance)
                    .SelectMany(item => item.SourceRefs)
                    .Distinct<ProvenanceRef>(ReferenceEqualityComparer.Instance)
                    .DistinctBy(source => System.Text.Json.JsonSerializer.Serialize(source)).ToArray();
                if (capturedSources.Length == 0)
                {
                    // Only explicit affected IDs or a finding owned by a measured
                    // record establish this relationship. A global blocker copied
                    // onto estimate lines must not acquire 79,000 false sources.
                    capturedSources = group.SelectMany(entry => entry.Finding.AffectedRecordIds.Concat(
                            entry.Stage == "מדידה" && entry.RecordId != null ? new[] { entry.RecordId } : Array.Empty<string>()))
                        .Distinct(StringComparer.Ordinal)
                        .Select(id => sourceByRecord.TryGetValue(id, out var record) ? VerifiedRecordSource(record) : null)
                        .Where(source => source != null).Cast<ProvenanceRef>()
                        .DistinctBy(source => System.Text.Json.JsonSerializer.Serialize(source)).ToArray();
                }
                return new Issue(string.Join(" / ", group.Select(entry => entry.Stage).Distinct()),
                    finding.Code, finding.Title, finding.Message,
                    string.IsNullOrWhiteSpace(finding.RecommendedAction)
                        ? NextStepFor(action) : finding.RecommendedAction!,
                    EstimatePreflightPolicy.IsBlocking(finding), action, ids,
                    ids.SelectMany(id => ruleByRecord.TryGetValue(id, out var keys) ? keys : new List<string>())
                        .Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToList())
                {
                    // Some failed measurements have no emitted quantity record. Their
                    // source is still essential to repair the actual entity, not a
                    // similarly named layer in an unrelated XREF.
                    // Global blockers are shared by every estimate line. Deduplicate
                    // instances before traversing/serializing their source evidence;
                    // otherwise 81 sources x 79,197 lines becomes millions of serializations.
                    Sources = capturedSources,
                };
            }).ToList();

        if (result != null)
        {
            var lines = result.Lines.ToLookup(line => line.LineId, StringComparer.Ordinal);
            var structuredByCode = issues.Select((issue, index) => (issue, index))
                .Where(entry => entry.issue.Blocking).ToLookup(entry => entry.issue.Code, entry => entry.index, StringComparer.Ordinal);
            foreach (var reason in EstimatePreflightPolicy.ExportBlockingReasons(result))
            {
                // A structured blocker already carries this exact global export
                // code. Mark its additional boundary rather than repeating it;
                // distinct finding messages with that code are all preserved.
                var existing = structuredByCode[reason].ToList();
                if (existing.Count > 0)
                {
                    foreach (var index in existing)
                        issues[index] = issues[index] with { Stage = issues[index].Stage + " / ייצוא" };
                    continue;
                }
                // Raw export reasons remain available even when no structured
                // finding exists (e.g. a missing source hash or excluded line).
                EstimateLine? line = null;
                var reasonCode = reason;
                if (reason.StartsWith("line:", StringComparison.Ordinal))
                {
                    var separator = reason.IndexOf(':', 5);
                    if (separator > 5)
                    {
                        var matching = lines[reason[5..separator]].ToList();
                        if (matching.Count == 1) line = matching[0];
                        reasonCode = reason[(separator + 1)..];
                    }
                }
                var action = RecoveryFor(reasonCode);
                issues.Add(new Issue("ייצוא", reason, "חסם ייצוא: " + reasonCode,
                    line == null ? reason : $"סעיף: {line.CatalogCode ?? "—"}; מקור: {line.SourceDrawingPath}; " +
                        $"עצם: {line.SourceHandle}; רשומה: {line.RecordId}; מחיר: {line.Price?.ToString("G29") ?? "חסר"}",
                    NextStepFor(action), true, action,
                    line == null ? Array.Empty<string>() : new[] { line.RecordId },
                    line == null || string.IsNullOrWhiteSpace(line.RuleKey) ? Array.Empty<string>() : new[] { line.RuleKey! }));
            }
        }
        return issues.OrderByDescending(issue => issue.Blocking).ToList();
    }

    private static ProvenanceRef? VerifiedRecordSource(NeutralQuantityRecord record)
    {
        var proof = record.Provenance;
        var source = record.Source;
        if (proof == null || FindingSourceLocationPolicy.Validate(proof) != null ||
            string.IsNullOrWhiteSpace(record.RunId) || proof.RunId != record.RunId ||
            !FindingSourceLocationPolicy.SamePath(source.DrawingPath, proof.SourcePathOrUri) ||
            !string.Equals(source.DrawingHash, proof.DrawingChecksum, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Handle, proof.SourceHandle, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Xref ?? "", proof.XrefPath ?? "", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(source.EntityType) ||
            !string.Equals(source.EntityType, proof.EntityType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Layer ?? "", proof.Layer ?? "", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(record.Measurement.Method) || proof.MeasurementMethod != record.Measurement.Method ||
            (proof.SourceKind == "xref") != !string.IsNullOrWhiteSpace(source.Xref)) return null;
        // Return the captured proof unchanged, including full insertion identity
        // and any transform. Live locator still rechecks host/XREF hashes/revision.
        return proof;
    }

    public static Recovery RecoveryFor(string code)
    {
        if (code is EstimateFindingCodes.MissingPrice or EstimateFindingCodes.OverrideIncomplete or
            EstimateFindingCodes.PriceSourceUnverified || code.Contains("price", StringComparison.OrdinalIgnoreCase))
            return Recovery.Price;
        if (code is EstimateFindingCodes.Unmapped or EstimateFindingCodes.UnitMismatch ||
            code.Contains("unmapped", StringComparison.OrdinalIgnoreCase) || code.Contains("unit-", StringComparison.OrdinalIgnoreCase))
            return Recovery.Mapping;
        if (code.StartsWith("EST-XREF-", StringComparison.Ordinal) ||
            code.Contains("source", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("trace:", StringComparison.Ordinal))
            return Recovery.Sources;
        if (code.Contains("catalog", StringComparison.OrdinalIgnoreCase)) return Recovery.Catalog;
        return Recovery.Inspect;
    }

    public static string NextStepFor(Recovery action) => action switch
    {
        Recovery.Mapping => "בחר את הקבוצה ובדוק שיוך לסעיף אמיתי בעל אותה יחידת מדידה. אין המרת יחידה אוטומטית.",
        Recovery.Price => "בדוק את הסעיף והמחירון המדויקים. מחיר פרויקט מחייב מחיר חיובי, מקור, נימוק, מאשר וחותמת זמן; אין מחיר חלופי או אפס אוטומטי. לאחר השלמה תקפה יש לבנות מחדש.",
        Recovery.Sources => "בדוק את המקור המדויק ואת פרטי הממצא; לאחר תיקון או שינוי מקור, שמור וסרוק מחדש. אין לראות במדידות החלקיות אומדן מלא.",
        Recovery.Catalog => "טען את מהדורת המחירון הנכונה ובדוק את זהותה; שינוי מחירון מחייב בדיקה מחודשת של השיוכים.",
        _ => "בדוק את פרטי הממצא והקבוצה המושפעת. אין לאשר או להחריג אותה רק כדי להסיר חסימה.",
    };
}
