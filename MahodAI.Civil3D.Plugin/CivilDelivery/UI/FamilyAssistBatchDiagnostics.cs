using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Public local receipt only. Never serializes an outcome, request, source, image, prompt or provider body.</summary>
public static class FamilyAssistBatchDiagnostics
{
    public const int MaxRows = 512, MaxBytes = 256 * 1024, MaxReceipts = 32;
    public sealed record Counts(int Eligible, int Processed, int Blocked, int AssistantDispatches,
        int ReturnedWithProposal, int Abstained, int Failed, int Cancelled, int Stale, int Unsent, int NotRequested)
    {
        public string PublicSummary => $"מתוך {Eligible} קבוצות: {Blocked} נחסמו; {AssistantDispatches} ניסיונות עוזר (לא הוכחת משלוח לרשת). " +
            $"חזרו {ReturnedWithProposal} עם הצעה ו־{Abstained} ללא הצעה; {Failed} כשלו, {Cancelled} בוטלו, {Stale} התיישנו; " +
            $"{Unsent} טרם נשלחו וניתן להמשיך אליהן; {NotRequested} לא נכללו. דבר לא אושר.";
    }
    /// <summary><see cref="RejectionCode"/>: the fixed class of a received but rejected answer (additive; ReasonCode is unchanged).</summary>
    public sealed record Entry(string GroupSha256, string ManifestSha256, string? ContextSha256,
        string State, bool AssistantDispatchStarted, string ReasonCode, string? RejectionCode = null);
    public sealed record Receipt(int SchemaVersion, string Scope, DateTime RecordedAtUtc, Counts Counts, IReadOnlyList<Entry> Entries);

    public static Receipt Capture(int eligible, IEnumerable<FamilyAssistBatchOutcome> source)
    {
        var rows = source.Take(MaxRows + 1).ToArray();
        if (eligible is < 0 or > MaxRows || rows.Length > MaxRows || rows.Length > eligible ||
            rows.Select(o => o.Entry.Row.Group.GroupId).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new ArgumentException("Batch receipt exceeds the bounded, unique displayed scope.");
        var entries = rows.Select(o => new Entry(Hash(o.Entry.Row.Group.GroupId), OpaqueId(o.Manifest.Id),
            o.Entry.ContextId == null ? null : OpaqueId(o.Entry.ContextId), FinalState(o), o.AssistantDispatchStarted, Reason(o),
            Rejection(o))).ToArray();
        var counts = new Counts(eligible, rows.Length, entries.Count(o => o.State == "blocked"),
            rows.Count(o => o.AssistantDispatchStarted), rows.Count(HasProposal),
            entries.Count(o => o.State == "abstained"), entries.Count(o => o.State == "failed"),
            entries.Count(o => o.State == "cancelled"), entries.Count(o => o.State == "stale"),
            entries.Count(o => o.State == "not-sent"), eligible - rows.Length);
        return new Receipt(1, "local batch review only; no approval and no proof of provider HTTP", DateTime.UtcNow, counts, entries);
    }
    public static bool HasProposal(FamilyAssistBatchOutcome outcome) => outcome.State == "answered" &&
        outcome.Proposals.Any(p => p.Status == RecognitionStatus.Proposed && p.FamilyId != null &&
            outcome.Entry.Row.FamilyOptions.Any(f => f.FamilyId == p.FamilyId));
    private static string Reason(FamilyAssistBatchOutcome outcome) => outcome.State switch {
        "answered" when HasProposal(outcome) => "proposal_requires_review",
        "answered" when outcome.Proposals.Count == 0 => "no_matching_group_response",
        "answered" when outcome.Proposals.Count == 1 => FamilyRecognitionAssist.PublicOutcomeCode(outcome.Proposals[0]),
        "answered" => "assistant_abstained_or_response_rejected",
        "blocked" when outcome.Entry.Refusal != null => "preflight_evidence_or_scope_refused",
        "blocked" => "image_permit_refused",
        "not-sent" => "batch_stopped_before_dispatch",
        "cancelled" => "cancelled_or_time_limit",
        "stale" => "source_or_manifest_changed",
        "failed" => "assistant_unavailable",
        _ => "unknown_public_state" };
    private static string? Rejection(FamilyAssistBatchOutcome outcome) =>
        outcome.State == "answered" && outcome.Proposals.Count == 1 && !HasProposal(outcome)
            ? FamilyRecognitionAssist.PublicRejectionCode(outcome.Proposals[0])
            : null;
    private static string FinalState(FamilyAssistBatchOutcome outcome)
    {
        if (outcome.State != "answered") return KnownState(outcome.State);
        if (HasProposal(outcome)) return "proposed";
        return Reason(outcome) switch {
            "assistant_unavailable" => "failed",
            "assistant_timeout" or "assistant_cancelled" => "cancelled",
            "source_or_catalog_changed" => "stale",
            "image_permit_refused" or "image_policy_disabled" or "subject_evidence_missing" or "evidence_withheld_by_privacy" or
                "engineer_context_refused" or "group_not_eligible" or "no_compatible_family" => "blocked",
            _ => "abstained" };
    }
    private static string KnownState(string value) => value is "answered" or "blocked" or "not-sent" or "cancelled" or "stale" or "failed" ? value : "unknown";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string OpaqueId(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : Hash(value);

    /// <summary>One receipt per dialog session, overwritten after resume. Bounded retention; never deletes previous receipts.</summary>
    public static bool TryWrite(Receipt receipt, Guid sessionId, string directory, out string? path)
    {
        path = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true });
            if (receipt.Entries.Count > MaxRows || bytes.Length > MaxBytes) return false;
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "family-batch-" + sessionId.ToString("N") + ".json");
            if (!File.Exists(target) && Directory.EnumerateFiles(directory, "family-batch-*.json").Take(MaxReceipts).Count() >= MaxReceipts) return false;
            var temp = target + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temp, target, overwrite: true); path = target; return true;
        }
        catch (Exception) { return false; } // Storage failure cannot change results or imply approval.
    }
}
