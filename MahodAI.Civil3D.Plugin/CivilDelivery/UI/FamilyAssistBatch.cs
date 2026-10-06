using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public sealed record FamilyAssistSelection(FamilyDecisionRow Row, string? Context, VisionImage? ShownImage);

/// <summary>One immutable entry in the displayed manifest. Local scope hashes are never sent to an AI provider.</summary>
public sealed class FamilyAssistBatchEntry
{
    private FamilyAssistBatchEntry(FamilyAssistSelection selection, string scope, string? contextId,
        ReviewedFamilyImage? reviewed, string? refusal)
    { Selection = selection; ScopeHash = scope; ContextId = contextId; ReviewedImage = reviewed; Refusal = refusal; }
    public FamilyAssistSelection Selection { get; }
    public FamilyDecisionRow Row => Selection.Row;
    public string? Context => Selection.Context;
    public VisionImage? DisplayedImage => Selection.ShownImage;
    public string ScopeHash { get; }
    public string? ContextId { get; }
    public ReviewedFamilyImage? ReviewedImage { get; }
    public string? Refusal { get; }

    internal static FamilyAssistBatchEntry Capture(FamilyAssistSelection selection, EngineerBoqLibrary library)
    {
        selection = selection with { Context = string.IsNullOrWhiteSpace(selection.Context) ? null : selection.Context.Trim() };
        var scope = Scope(selection, library);
        if (!selection.Row.CanAskAi) return new(selection, scope, null, null, "קבוצה מעורבת, כבר מוכרעת או ללא משפחה תואמת — לא נשלחת.");
        ReviewedFamilyImage? reviewed = null;
        if (selection.ShownImage != null)
        {
            reviewed = ReviewedFamilyImage.Bind(selection.Row.Group, library, selection.Context, selection.ShownImage,
                FamilyImageOrigin.SchematicGroup, out var refusal);
            if (reviewed == null) return new(selection, scope, null, null, refusal ?? "התמונה אינה מתאימה לקבוצה.");
        }
        var prepared = FamilyRecognitionAssist.Prepare(selection.Row.Group, library, selection.Context,
            selection.ShownImage == null ? null : new[] { selection.ShownImage });
        return new(selection, scope, prepared.Request?.ContextId, reviewed,
            prepared.Request == null ? prepared.AbstentionMessage ?? "אין ראיה מספקת לבקשה." : null);
    }

    internal bool HasSameScope(EngineerBoqLibrary library) => Scope(Selection, library) == ScopeHash;
    public bool IsCurrent(EngineerBoqLibrary library)
    {
        if (ContextId == null || !Row.CanAskAi || !HasSameScope(library)) return false;
        var now = FamilyRecognitionAssist.Prepare(Row.Group, library, Context,
            DisplayedImage == null ? null : new[] { DisplayedImage }).Request;
        return now?.ContextId == ContextId && (DisplayedImage == null ||
            ReviewedImage?.IsCurrent(Row.Group, library, Context, DisplayedImage) == true);
    }

    private static string Scope(FamilyAssistSelection s, EngineerBoqLibrary library)
    {
        // Whole local records cover blocked entries too: even a no-evidence group
        // changing beneath the consent revokes the complete displayed manifest.
        var localOnly = JsonSerializer.Serialize(new { Group = s.Row.Group, s.Context,
            Image = s.ShownImage?.Sha256, Kind = s.ShownImage?.Kind, Library = LibraryIdentity.LibraryHash(library) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(localOnly)));
    }
}

/// <summary>One explicit review of the exact displayed manifest. Never reusable for future images or groups.</summary>
public sealed class FamilyAssistManifest
{
    private int _consumed, _revoked;
    private FamilyAssistManifest(IReadOnlyList<FamilyAssistBatchEntry> entries, string approvedBy, DateTimeOffset at)
    {
        Entries = entries; ApprovedBy = approvedBy; ReviewedAtUtc = at;
        Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", entries.Select(e => e.ScopeHash)))));
    }
    public const int MaxGroups = 512;
    public IReadOnlyList<FamilyAssistBatchEntry> Entries { get; }
    public string ApprovedBy { get; }
    public DateTimeOffset ReviewedAtUtc { get; }
    public string Id { get; }
    public int ImageCount => Entries.Count(e => e.DisplayedImage != null);

    // The UI must show every selected group and exact owned PNG in the manifest
    // before calling this with explicitImageConsent. No capture, policy change or send occurs.
    public static FamilyAssistManifest Review(IReadOnlyList<FamilyAssistSelection> shown, EngineerBoqLibrary library,
        bool explicitImageConsent, string approvedBy, CancellationToken cancellationToken = default)
    {
        if (shown.Count == 0 || shown.Count > MaxGroups || shown.Any(s => s == null) ||
            shown.Select(s => s.Row.Group.GroupId).Distinct(StringComparer.Ordinal).Count() != shown.Count)
            throw new ArgumentException("נדרשות 1–512 קבוצות ייחודיות; אין קיצוץ סמוי.", nameof(shown));
        if (shown.Any(s => s.ShownImage != null) && (!explicitImageConsent || string.IsNullOrWhiteSpace(approvedBy) ||
            approvedBy.Length > 200 || approvedBy.Any(char.IsControl)))
            throw new InvalidOperationException("תמונות מחייבות בדיקת המניפסט המוצג, הסכמה מפורשת ושם מאשר.");
        var entries = new List<FamilyAssistBatchEntry>();
        foreach (var selection in shown) { cancellationToken.ThrowIfCancellationRequested(); entries.Add(FamilyAssistBatchEntry.Capture(selection, library)); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(entries.AsReadOnly(), approvedBy, DateTimeOffset.UtcNow);
    }
    public bool IsCurrent(EngineerBoqLibrary library) => Volatile.Read(ref _revoked) == 0 && Entries.All(e => e.HasSameScope(library));
    public void Invalidate() => Interlocked.Exchange(ref _revoked, 1);
    internal bool TryConsume() => Volatile.Read(ref _revoked) == 0 && Interlocked.Exchange(ref _consumed, 1) == 0;
}

public sealed record FamilyAssistBatchOutcome(FamilyAssistManifest Manifest, FamilyAssistBatchEntry Entry,
    string State, IReadOnlyList<RecognitionProposal> Proposals, string Message)
{
    // The local assistant dispatch was scheduled, not a claim that HTTP reached a provider.
    public bool AssistantDispatchStarted { get; init; }
}
public sealed record FamilyAssistBatchProgress(int Completed, int Total, string GroupId, string State);

/// <summary>Bounded serial dispatch through the existing assistant and its gates. No model/profile mutation here.</summary>
public static class FamilyAssistBatch
{
    // Only requests never dispatched may resume automatically. Timed-out, failed or
    // blocked requests require a separate explicit user decision, not a hidden retry.
    public static IReadOnlyList<FamilyDecisionRow> Unattempted(IEnumerable<FamilyAssistBatchOutcome> outcomes) =>
        outcomes.Where(o => o.State == "not-sent").Select(o => o.Entry.Row).Distinct().ToArray();
    // Deliberately shorter than the existing Core assistant's 30-second timeout:
    // a late provider cannot make Core return before our stop signal and start another request.
    public const int RequestTimeoutSeconds = 25;
    public const int BatchTimeoutSeconds = 120;
    public delegate Task<IReadOnlyList<RecognitionProposal>> Assistant(RecognitionGroupInput group,
        IReadOnlyList<RecognitionProposal> local, string? context, VisionPayload? vision, CancellationToken token);

    public static async Task<IReadOnlyList<FamilyAssistBatchOutcome>> RunAsync(FamilyAssistManifest manifest,
        EngineerBoqLibrary library, Assistant assistant, bool sendConfirmed,
        IProgress<FamilyAssistBatchProgress>? progress, CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null, TimeSpan? batchTimeout = null)
    {
        if (FamilyAssistProviderAdmission.IsBusy) throw new InvalidOperationException(FamilyAssistProviderAdmission.RetryBlocked);
        if (!sendConfirmed || !manifest.IsCurrent(library) || !manifest.TryConsume())
            throw new InvalidOperationException("האצווה דורשת אישור משלוח חד־פעמי למניפסט עדכני.");
        var each = requestTimeout ?? TimeSpan.FromSeconds(RequestTimeoutSeconds);
        var whole = batchTimeout ?? TimeSpan.FromSeconds(BatchTimeoutSeconds);
        if (each <= TimeSpan.Zero || each > TimeSpan.FromSeconds(RequestTimeoutSeconds) ||
            whole <= TimeSpan.Zero || whole > TimeSpan.FromSeconds(BatchTimeoutSeconds)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batch.CancelAfter(whole);
        var outcomes = new List<FamilyAssistBatchOutcome>();
        var dispatched = new HashSet<FamilyAssistBatchEntry>();
        var stopped = false;
        foreach (var entry in manifest.Entries)
        {
            if (stopped || batch.IsCancellationRequested || !manifest.IsCurrent(library))
            { stopped = true; Add(entry, "not-sent", Array.Empty<RecognitionProposal>(), "לא נשלחה: ביטול, סוף זמן או שינוי בהיקף המניפסט."); continue; }
            if (entry.Refusal != null)
            { Add(entry, "blocked", Array.Empty<RecognitionProposal>(), entry.Refusal); continue; }
            if (!entry.IsCurrent(library))
            { stopped = true; Add(entry, "stale", Array.Empty<RecognitionProposal>(), "הראיות או ההקשר השתנו לפני השליחה."); continue; }
            VisionPayload? payload = null;
            if (entry.DisplayedImage != null && (entry.ReviewedImage == null ||
                !entry.ReviewedImage.TryTakePayload(entry.Row.Group, library, entry.Context, entry.DisplayedImage,
                    explicitConsent: true, manifest.ApprovedBy, manifest.ReviewedAtUtc, out payload) || payload == null))
            { stopped = true; Add(entry, "blocked", Array.Empty<RecognitionProposal>(), "הסכמת התמונה אינה תקפה עוד; האצווה נעצרה."); continue; }
            using var request = CancellationTokenSource.CreateLinkedTokenSource(batch.Token);
            request.CancelAfter(each);
            var dispatchLease = FamilyAssistProviderAdmission.TryEnterDispatch();
            if (dispatchLease == null)
            { stopped = true; Add(entry, "not-sent", Array.Empty<RecognitionProposal>(), FamilyAssistProviderAdmission.RetryBlocked); continue; }
            progress?.Report(new(outcomes.Count, manifest.Entries.Count, entry.Row.Group.GroupId, "sending"));
            Task<IReadOnlyList<RecognitionProposal>>? pending = null;
            try
            {
                var token = request.Token;
                // Also bound a provider that blocks synchronously before returning its Task.
                dispatched.Add(entry);
                pending = Task.Run(() => {
                    token.ThrowIfCancellationRequested();
                    return assistant(entry.Row.Group, new[] { entry.Row.Proposal }, entry.Context, payload, token);
                });
                _ = pending.ContinueWith(_ => dispatchLease.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (await Task.WhenAny(pending, Task.Delay(Timeout.InfiniteTimeSpan, request.Token)).ConfigureAwait(false) != pending)
                {
                    stopped = true;
                    Add(entry, "cancelled", Array.Empty<RecognitionProposal>(), "הבקשה בוטלה או חרגה מהזמן. תשובה מאוחרת לא תוחל ולא נשלחת בקשה נוספת.");
                    _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    continue;
                }
                var proposals = await pending.ConfigureAwait(false);
                if (FamilyAssistProviderAdmission.ProviderBusy)
                { stopped = true; Add(entry, "cancelled", Array.Empty<RecognitionProposal>(), FamilyAssistProviderAdmission.RetryBlocked); continue; }
                if (request.IsCancellationRequested)
                { stopped = true; Add(entry, "cancelled", Array.Empty<RecognitionProposal>(), "הבקשה בוטלה או חרגה מהזמן; התשובה לא תוחל."); continue; }
                if (!manifest.IsCurrent(library) || !entry.IsCurrent(library))
                { stopped = true; Add(entry, "stale", Array.Empty<RecognitionProposal>(), "המניפסט או ראיות המקור השתנו; התשובה לא תוחל."); continue; }
                var ids = entry.Row.Group.Records.Select(r => r.RecordId).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                var own = (proposals ?? Array.Empty<RecognitionProposal>()).Where(p => p.GroupId == entry.Row.Group.GroupId &&
                    p.RecordIds.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(ids, StringComparer.Ordinal)).ToArray();
                Add(entry, "answered", own, own.Length == 0 ? "אין תשובה שמתאימה לקבוצה ולכל עצמים שלה." : "התקבלו הצעות לבדיקה בלבד; דבר לא אושר.");
            }
            catch (OperationCanceledException)
            { stopped = true; Add(entry, "cancelled", Array.Empty<RecognitionProposal>(), "המשלוח בוטל; לא נשלחות קבוצות נוספות."); }
            catch (Exception)
            { Add(entry, "failed", Array.Empty<RecognitionProposal>(), "העוזר אינו זמין לקבוצה; אין שיוך או אישור חלופי."); }
            finally { request.Cancel(); if (pending == null || pending.IsCompleted) dispatchLease.Dispose(); }
        }
        return outcomes;
        void Add(FamilyAssistBatchEntry entry, string state, IReadOnlyList<RecognitionProposal> proposals, string message)
        {
            outcomes.Add(new(manifest, entry, state, proposals, message) { AssistantDispatchStarted = dispatched.Contains(entry) });
            progress?.Report(new(outcomes.Count, manifest.Entries.Count, entry.Row.Group.GroupId, state));
        }
    }

    /// <summary>Called on the UI thread. A suggestion is not selected or approved.</summary>
    public static bool Present(FamilyAssistBatchOutcome outcome, EngineerBoqLibrary library)
    {
        if (outcome.State != "answered" || !outcome.Manifest.IsCurrent(library) || !outcome.Entry.IsCurrent(library)) return false;
        outcome.Entry.Row.ApplyAssistant(outcome.Proposals, id => library.Rules.FirstOrDefault(r => r.Id == id)?.Element ?? id);
        return true;
    }
    public static bool SelectReviewedSuggestion(FamilyAssistBatchOutcome outcome, EngineerBoqLibrary library, bool explicitlyReviewed)
    {
        var row = outcome.Entry.Row;
        if (!explicitlyReviewed || outcome.State != "answered" || !outcome.Manifest.IsCurrent(library) || !outcome.Entry.IsCurrent(library) ||
            row.AiProposal is not { Status: RecognitionStatus.Proposed } proposal || !outcome.Proposals.Contains(proposal)) return false;
        var option = row.FamilyOptions.FirstOrDefault(f => f.FamilyId == proposal.FamilyId);
        if (option == null) return false;
        row.ChosenFamily = option; row.IsSelected = true;
        return row.IsSelected && row.ChosenFromAi;
    }
}
