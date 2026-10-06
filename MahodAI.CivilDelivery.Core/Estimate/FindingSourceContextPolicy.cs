using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Read-only explanation of recorded failures; never infers geometry or changes a finding.</summary>
public static class FindingSourceContextPolicy
{
    public sealed record Context(string Title, string Detail, string NextStep, string? ExactFailure);

    public static Context Capture(EstimateReviewPolicy.Issue issue, ProvenanceRef source) =>
        new(issue.Title, issue.Detail, issue.NextStep,
            issue.Code == EstimateFindingCodes.MeasurementFailed ? ExactFailure(issue.Detail, source) : null);

    public static string? ExactFailure(string detail, ProvenanceRef source)
    {
        // This is a presentation-only adapter for the existing full provenance line.
        // A leaf handle, layer or similar error text is never enough to associate a reason.
        if (FindingSourceLocationPolicy.Validate(source) != null) return null;
        static string D(string? value) => string.IsNullOrWhiteSpace(value) ? "(not recorded)" : value;
        var identity = $"source={D(source.SourcePathOrUri)}; sha256={D(source.DrawingChecksum)}; " +
            $"handle={D(source.SourceHandle)}; " +
            $"xref={(source.SourceKind == "drawing" && source.XrefPath == null ? "(host)" : D(source.XrefPath))}; " +
            $"layer={D(source.Layer)}; entity={D(source.EntityType)}; method={D(source.MeasurementMethod)}";
        var lines = (detail ?? "").Replace("\r\n", "\n").Split('\n');
        var indices = Enumerable.Range(0, lines.Length).Where(i =>
            lines[i].StartsWith("measurement-kind=", StringComparison.Ordinal) &&
            lines[i].IndexOf("; ", StringComparison.Ordinal) is var separator && separator >= 0 &&
            string.Equals(lines[i][(separator + 2)..], identity, StringComparison.Ordinal)).ToArray();
        if (indices.Length != 1) return null;
        var end = indices[0];
        var start = end - 1;
        while (start >= 0 && !lines[start].StartsWith("measurement-kind=", StringComparison.Ordinal) &&
               !lines[start].StartsWith("Complete measurement failures (", StringComparison.Ordinal)) start--;
        var reasonLines = lines.Skip(start + 1).Take(end - start - 1).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        // A multiline/unscoped neighbour is ambiguous in this legacy text format.
        // Keep its complete aggregate instead of claiming a per-object association.
        return reasonLines.Length == 1 ? reasonLines[0].Trim() : null;
    }

    public static string Format(IReadOnlyList<Context> contexts)
    {
        var text = new StringBuilder();
        foreach (var context in contexts)
        {
            text.AppendLine(context.ExactFailure == null
                ? "הקשר ממצא מצטבר — לא הוכחה שכל הסיבות שייכות לעצם שנבחר:"
                : "כשל מתועד לעצם שנבחר — התאמת זהות מקור מלאה:");
            text.AppendLine(context.ExactFailure ?? context.Detail);
            text.AppendLine("הצעד הבא המתועד: " + context.NextStep);
        }
        text.AppendLine("איתור הוא לעיון בלבד. לאחר טיפול הנדסי במקור יש לשמור ולסרוק מחדש. " +
            "לא חושב שטח חלופי, לא אושרה כמות ולא הוחרגו עבודות עפר.");
        return text.ToString().TrimEnd();
    }

    public static bool ContainsExactDiagnosticSource(JsonElement diagnostic, ProvenanceRef source)
    {
        if (FindingSourceLocationPolicy.Validate(source) != null || string.IsNullOrWhiteSpace(source.RunId) ||
            !diagnostic.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in sources.EnumerateArray())
        {
            if (!entry.TryGetProperty("Source", out var value)) continue;
            var candidate = value.Deserialize<ProvenanceRef>();
            if (candidate != null && candidate.SourceKind == source.SourceKind &&
                FindingSourceLocationPolicy.SamePath(candidate.SourcePathOrUri, source.SourcePathOrUri) &&
                string.Equals(candidate.DrawingChecksum, source.DrawingChecksum, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.SourceHandle, source.SourceHandle, StringComparison.OrdinalIgnoreCase) &&
                candidate.XrefPath == source.XrefPath && candidate.Layer == source.Layer &&
                candidate.EntityType == source.EntityType && candidate.MeasurementMethod == source.MeasurementMethod &&
                candidate.RunId == source.RunId) return true;
        }
        return false;
    }
}
