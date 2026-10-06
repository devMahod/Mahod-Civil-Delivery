using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Pure v1 binding, not a source resolver, geometry reader, approval, pricing or allocation.
/// The producer must supply the exact stable SourceScans used by Result and verify their
/// artifact receipts/freshness outside this class. This class never accesses files or CAD.
/// </summary>
public static class ProjectRuleRecordContext
{
    public enum State { GenericUnchanged, RuleReview, ExcludedReview, UnclassifiedReview, PendingLineage }
    public sealed record Stamp(string ProjectId, string ActiveRunId, string ActiveDrawingPath,
        string ActiveDrawingSha256, string ScanSha256, string ProfileSha256, string EffectiveProfileSha256,
        string RulesSha256, string CatalogSnapshotId, string CatalogSha256, string SourceSetSha256,
        string ResolutionReceiptSha256, string OutcomeSha256);

    /// <summary>Artifact hashes are producer receipts, not verified disk facts in this pure binder.</summary>
    public sealed record SourceIdentity(string Role, string RunId, string DrawingPath, string DrawingSha256,
        string RecordsArtifactSha256, string RecordObjectSha256, string ManifestSha256,
        string AuxiliaryEvidenceSha256);
    public sealed record Source(SourceIdentity Identity, BoqNeutralRecordAdapter.SourceScan Scan);
    public sealed record Request(Stamp Captured, Stamp Current, IReadOnlyList<Source> Sources,
        IReadOnlyList<NeutralQuantityRecord> ActiveRecords, BoqEngineResult Result);

    /// <summary>No payable/allocated quantity or price. OriginalRaw is identification only.</summary>
    public sealed record Guidance(string RecordId, string? RuleKey, double OriginalRaw, string OriginalUnit,
        string? ExistingCatalogCode, State State, string ReasonCode, string Message,
        string? Role = null, int? InputIndex = null, string? LineId = null, int? PartIndex = null,
        string? RuleCatalogCode = null, string? RuleUnit = null, string? PartKind = null,
        double? ObjectWidth = null, string? Confirmation = null, string? Review = null,
        string? CrossingRole = null, IReadOnlyList<int>? CrossingIndices = null)
    {
        public bool IsApproval => false;
        public bool IsPrice => false;
    }
    public sealed record Snapshot(Stamp Stamp, IReadOnlyList<Guidance> Records, string Digest)
    {
        public bool HasPending => Records.Any(r => r.State == State.PendingLineage);
    }

    private sealed record Frozen(NeutralQuantityRecord Record, string Digest);
    private sealed record FrozenSource(SourceIdentity Identity, Frozen[] Records,
        IReadOnlyDictionary<string, HashSet<string>> KindsByHandle);
    private sealed record Outcome(BoqRecord Input, BoqBucket Bucket, string? Reason,
        (string Line, int Part)? Item);
    private sealed record Part(string LineId, int Index, string? Code, string Unit, string Kind,
        double? Width, string? Confirmation, string? Review, string[] Roles);
    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IncludeFields = true,
    };

    /// <summary>Canonical object digest, distinct from the artifact's byte SHA-256.</summary>
    public static string RecordDigest(IReadOnlyList<NeutralQuantityRecord> records) => Digest(records);

    /// <summary>Object receipt of all non-record source evidence consumed by the existing adapter.</summary>
    public static string AuxiliaryDigest(BoqNeutralRecordAdapter.SourceScan source) =>
        Digest(new { source.Findings, source.HatchDiagnostics, source.ScannedAtUtc });

    /// <summary>
    /// Capture immediately after Run, while the producer still owns a stable result. This
    /// detects later changes to binding facts; it does not prove that Run was executed.
    /// </summary>
    public static string OutcomeDigest(BoqEngineResult result) => Digest(new
    {
        result.Rules.Project, result.Rules.Sha256, result.Rules.SourceRoles, result.Rules.Crosswalk,
        result.Parameters, result.RoadClass, Input = result.Input.Records,
        result.Buckets, result.Reasons,
        Items = result.Items.Select(x => x is { } item ? new { item.Line, item.Part } : null).ToArray(),
        Parts = result.Parts.Select(p => new { p.LineId, p.Index, p.Line, p.Part }).ToArray(),
        Lines = result.Lines.Select(l => new { l.Line, l.Item }).ToArray(),
        result.Crossings, result.Roles,
    });

    public static string SourceSetDigest(IEnumerable<SourceIdentity> identities) =>
        Digest(identities.OrderBy(x => x.Role, StringComparer.Ordinal)
            .ThenBy(x => x.RunId, StringComparer.Ordinal).ToArray());

    public static Snapshot Bind(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ActiveRecords);
        ArgumentNullException.ThrowIfNull(request.Captured);
        // Detach all current members first. Null/malformed payloads cannot produce a binding.
        var active = request.ActiveRecords.Select(Freeze).ToArray();
        Snapshot All(State state, string code, string message) =>
            Finish(request.Captured, active.Select(a => Basic(a.Record, state, code, message)));
        if (request.Result?.Rules == null || request.Current == null ||
            request.Sources == null || !Valid(request.Captured) || !Valid(request.Current))
            return All(State.PendingLineage, "invalid_context", "זהות הקשר הכללים חסרה או לא תקינה.");
        if (request.Captured != request.Current)
            return All(State.PendingLineage, "stale_context", "המקור, הפרופיל, הכללים או המחירון השתנו; יש להכין הקשר חדש.");
        if (active.Any(n => n.Record.ProjectProfileId != request.Captured.ProjectId))
            return All(State.PendingLineage, "active_project_mismatch", "הרשומות הפעילות אינן שייכות לפרויקט המוצמד.");
        if (!string.Equals(request.Captured.RulesSha256, request.Result.Rules.Sha256, StringComparison.OrdinalIgnoreCase))
            return All(State.PendingLineage, "rules_mismatch", "תוצאת החישוב אינה שייכת לגרסת הכללים המוצגת.");
        if (!string.Equals(request.Captured.ProjectId, request.Result.Rules.Project, StringComparison.Ordinal))
            return All(State.GenericUnchanged, "different_project", "כללי החישוב שייכים לפרויקט אחר; המסלול הכללי לא השתנה.");

        // Freeze producer records, input facts and outcomes once, before matching. Caller owns
        // exclusive stable capture during this operation; this is not a concurrent snapshot API.
        FrozenSource[] sources;
        try
        {
            sources = request.Sources.Select(s =>
            {
                if (s?.Identity == null || s.Scan == null || s.Scan.Records == null ||
                    !Valid(s.Identity) || s.Identity.Role != s.Scan.Role || s.Identity.RunId != s.Scan.RunId ||
                    s.Identity.DrawingPath != s.Scan.DrawingPath ||
                    !string.Equals(s.Identity.DrawingSha256, s.Scan.DrawingHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(s.Identity.AuxiliaryEvidenceSha256, AuxiliaryDigest(s.Scan), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(s.Identity.RecordObjectSha256, RecordDigest(s.Scan.Records), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("source identity");
                var members = s.Scan.Records.Select(Freeze).ToArray();
                if (members.Any(n => n.Record.ProjectProfileId != request.Captured.ProjectId || !Text(n.Record.RecordId)) ||
                    members.Select(n => n.Record.RecordId).Distinct(StringComparer.Ordinal).Count() != members.Length)
                    throw new ArgumentException("source project identity");
                return new FrozenSource(s.Identity, members, members.Where(n => string.IsNullOrEmpty(n.Record.Source.Xref))
                    .GroupBy(n => n.Record.Source.Handle, StringComparer.Ordinal).ToDictionary(g => g.Key,
                        g => g.Select(n => n.Record.Measurement.Kind).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal));
            }).ToArray();
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or NullReferenceException)
        { return All(State.PendingLineage, "source_receipt_mismatch", "הרשומות אינן תואמות לקבלת מקור החישוב."); }
        if (sources.GroupBy(s => s.Identity.Role, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
            sources.Any(s => request.Result.Rules.SourceRoles.Count(r => r.Id == s.Identity.Role) != 1) ||
            !string.Equals(SourceSetDigest(sources.Select(s => s.Identity)), request.Captured.SourceSetSha256, StringComparison.OrdinalIgnoreCase))
            return All(State.PendingLineage, "source_set_mismatch", "תפקידי המקורות אינם חד־משמעיים או אינם תואמים לקבלה.");

        var own = sources.Where(s => s.Identity.RunId == request.Captured.ActiveRunId &&
            s.Identity.DrawingPath == request.Captured.ActiveDrawingPath &&
            string.Equals(s.Identity.DrawingSha256, request.Captured.ActiveDrawingSha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (own.Length != 1 || !SameMembers(active, own[0].Records))
            return All(State.PendingLineage, "active_members_mismatch", "היקף הרשומות הפעיל אינו זהה להיקף המקור המוצמד.");
        var role = own[0].Identity.Role;
        var r = request.Result;
        if (r.Input?.Records == null || r.Buckets == null || r.Items == null || r.Reasons == null ||
            r.Input.Records.Count != r.Buckets.Length || r.Input.Records.Count != r.Items.Length ||
            r.Input.Records.Count != r.Reasons.Length || r.Parts == null || r.Lines == null)
            return All(State.PendingLineage, "result_shape_mismatch", "החישוב אינו מכיל תוצאה תואמת לכל רשומת קלט.");
        try
        {
            if (!string.Equals(OutcomeDigest(r), request.Captured.OutcomeSha256, StringComparison.OrdinalIgnoreCase))
                return All(State.PendingLineage, "outcome_changed", "תוצאת החישוב השתנתה לאחר הצמדתה; יש להכין הקשר חדש.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or JsonException or NotSupportedException)
        { return All(State.PendingLineage, "invalid_outcome", "לא ניתן לאמת את זהות תוצאת החישוב."); }
        Outcome[] outcomes;
        Part[] parts;
        try
        {
            outcomes = r.Input.Records.Select((b, i) =>
                new Outcome(b with { Bbox = b.Bbox?.ToArray() }, r.Buckets[i], r.Reasons[i], r.Items[i])).ToArray();
            parts = r.Parts.Select(p =>
            {
                var rule = r.Rules.Lines.Single(l => l.Id == p.LineId);
                var line = r.Lines.Single(l => l.Line.Id == p.LineId);
                if (p.Index < 0 || p.Index >= rule.Parts.Count || Digest(p.Part) != Digest(rule.Parts[p.Index]) ||
                    p.Line.Unit != rule.Unit || line.Item != rule.ItemFor(r.RoadClass))
                    throw new ArgumentException("part definition mismatch");
                return new Part(p.LineId, p.Index, line.Item, rule.Unit, p.Part.Kind,
                    p.Part.ObjectWidth, p.Part.Confirm ?? rule.Confirm, rule.Review, p.Part.Src.ToArray());
            }).ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException)
        { return All(State.PendingLineage, "result_part_mismatch", "חלקי תוצאת החישוב אינם תואמים לכללים."); }
        var hatchRoles = r.Rules.AllParts.Where(p => p.Kind == "hatch").SelectMany(p => p.Src).ToHashSet(StringComparer.Ordinal);
        var crossingSources = r.Rules.Crosswalk?.Src.ToArray() ?? Array.Empty<string>();
        var crossingRoles = r.Roles?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var crossingHandles = r.Crossings?.Select(c => c.Handles.Concat(c.Boundary.Values).ToHashSet(StringComparer.Ordinal)).ToArray();
        var byInput = outcomes.Select((o, i) => (o, i)).GroupBy(x => Key(x.o.Input.Src, x.o.Input.Handle, x.o.Input.Kind))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var bySource = sources.SelectMany(s => s.Records.Select(n => (s, n)))
            .GroupBy(x => Key(x.s.Identity.Role, x.n.Record.Source.Handle, x.n.Record.Measurement.Kind))
            .ToDictionary(g => g.Key, g => g.ToArray());

        // No phantom, altered or duplicated engine input can affect a claimed part invisibly.
        // A known unsupported branch still needs this exact source/field match, but only its
        // own guidance is Pending; legitimate array/recovery inputs do not poison ordinary peers.
        var badRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in byInput)
        {
            var item = pair.Value[0].o.Input;
            if (pair.Value.Length != 1 || !bySource.TryGetValue(pair.Key, out var original) || original.Length != 1 ||
                !Matches(original[0].n.Record, item, original[0].s))
                badRoles.Add(item.Src);
        }
        // An invalid sibling can change ownership or a derived part of the active role.
        if (badRoles.Count > 0)
            return All(State.PendingLineage, "engine_members_mismatch", "רשומת חישוב כפולה, זרה או שונה מהמקור; אין הצמדה בטוחה.");

        var results = new List<Guidance>();
        foreach (var a in active)
        {
            var n = a.Record;
            Guidance Pending(string code, string text) => Basic(n, State.PendingLineage, code, text) with { Role = role };
            var unsupported = Unsupported(n, role, hatchRoles);
            if (unsupported != null)
            { results.Add(Pending(unsupported, "הרשומה אינה במסלול הרגיל החד־חד־ערכי; דרושה ראיית lineage מפורשת.")); continue; }
            var key = Key(role, n.Source.Handle, n.Measurement.Kind);
            if (!bySource.TryGetValue(key, out var source) || source.Length != 1 ||
                !byInput.TryGetValue(key, out var found) || found.Length != 1)
            { results.Add(Pending("not_one_to_one", "לא נמצאה התאמה חד־חד־ערכית לרשומת החישוב.")); continue; }
            var o = found[0].o;
            if (!Matches(n, o.Input, own[0]))
            { results.Add(Pending("fields_mismatch", "שדות הרשומה או ערך המדידה אינם זהים לקלט החישוב.")); continue; }
            Guidance Bound(State state, string code, string text) =>
                Basic(n, state, code, text) with { Role = role, InputIndex = found[0].i };
            if (o.Bucket == BoqBucket.Excluded && o.Item == null && !string.IsNullOrWhiteSpace(o.Reason))
            { results.Add(Bound(State.ExcludedReview, "rules_excluded_review", o.Reason)); continue; }
            if (o.Bucket == BoqBucket.Unclassified && o.Item == null)
            { results.Add(Bound(State.UnclassifiedReview, "rules_unclassified", "הרשומה לא סווגה בחישוב הכללים; אין מכאן אישור לשיוך.")); continue; }
            if (o.Bucket != BoqBucket.Item || o.Item is not { } selected)
            { results.Add(Pending("inconsistent_outcome", "מצב הרשומה והשיוך בחישוב אינם עקביים.")); continue; }
            var matching = parts.Where(p => p.LineId == selected.Line && p.Index == selected.Part).ToArray();
            if (matching.Length != 1 || !matching[0].Roles.Contains(role, StringComparer.Ordinal))
            { results.Add(Pending("part_mismatch", "חלק החישוב אינו חד־משמעי או אינו שייך לתפקיד המקור.")); continue; }
            var part = matching[0];
            if (part.Kind == "crosswalk_geo" && (crossingSources.Length != 1 || crossingSources[0] != role))
            { results.Add(Pending("crossing_source_ambiguous", "תפקיד מקור הגאומטריה המורכבת אינו חד־משמעי.")); continue; }
            string? crossingRole = null;
            IReadOnlyList<int>? crossingIndices = null;
            if (part.Kind == "crosswalk_geo")
            {
                if (crossingRoles == null || crossingHandles == null)
                { results.Add(Pending("crossing_lineage_missing", "חסרה ראיית ההשתייכות לחישוב הגאומטריה המורכבת.")); continue; }
                crossingRoles.TryGetValue(n.Source.Handle, out crossingRole);
                crossingIndices = Array.AsReadOnly(crossingHandles.Select((set, i) => (set, i))
                    .Where(x => x.set.Contains(n.Source.Handle)).Select(x => x.i).ToArray());
                if (crossingIndices.Count == 0)
                { results.Add(Pending("crossing_lineage_missing", "הרשומה אינה מופיעה כחבר או כגבול בחישוב המורכב.")); continue; }
            }
            results.Add(Bound(State.RuleReview, "physical_rule_review",
                "הרשומה משתתפת בחישוב לפי כללי הפרויקט; הסכום הגולמי אינו כמות לתמחור. יש לבדוק את מסלול כתב הכמויות לפי כללים.") with
            {
                LineId = part.LineId, PartIndex = part.Index, RuleCatalogCode = part.Code,
                RuleUnit = part.Unit, PartKind = part.Kind, ObjectWidth = part.Width,
                Confirmation = part.Confirmation, Review = part.Review,
                CrossingRole = crossingRole, CrossingIndices = crossingIndices,
            });
        }
        return Finish(request.Captured, results);
    }

    private static string? Unsupported(NeutralQuantityRecord n, string role, HashSet<string> hatchRoles)
    {
        if (!string.IsNullOrEmpty(n.Source.Xref)) return "xref_lineage_required";
        if (n.Measurement.Parameters.ContainsKey("cad_array_handle")) return "array_lineage_required";
        if (BoqNeutralRecordAdapter.RecoveredHatchMethods.Contains(n.Measurement.Method)) return "recovery_lineage_required";
        if (n.Source.EntityType.Equals("HATCH", StringComparison.OrdinalIgnoreCase) &&
            (n.Measurement.Method != "hatch-area" || hatchRoles.Contains(role))) return "hatch_aggregate_lineage_required";
        return null;
    }
    private static bool Matches(NeutralQuantityRecord n, BoqRecord b, FrozenSource source)
    {
        var m = n.Measurement; var s = n.Source;
        var expectedUnit = m.Kind switch { "length" => "מטר", "area" => "מ\"ר", "count" => "יחידה", _ => null };
        if (!source.KindsByHandle.TryGetValue(s.Handle, out var kinds)) return false;
        m.Parameters.TryGetValue("cad_block_name_effective", out var effective);
        m.Parameters.TryGetValue("block_name", out var raw);
        var bbox = m.GeometryEvidence is { Length: >= 4 } g ? g.Take(4).ToArray() : null;
        return expectedUnit != null && Units.Parse(m.Unit).SameUnit(Units.Parse(expectedUnit)) &&
            double.IsFinite(m.RawValue) && double.IsFinite(b.Qty) && Bits(m.RawValue) == Bits(b.Qty) &&
            s.DrawingPath == source.Identity.DrawingPath &&
            string.Equals(s.DrawingHash, source.Identity.DrawingSha256, StringComparison.OrdinalIgnoreCase) &&
            n.RunId == source.Identity.RunId && b.Src == source.Identity.Role &&
            b.Handle == s.Handle && b.Kind == m.Kind && b.Layer == (s.Layer ?? "") &&
            b.Etype == s.EntityType.ToUpperInvariant() &&
            b.Closed == (kinds.Contains("area") && kinds.Contains("length")) &&
            b.Block == (!string.IsNullOrWhiteSpace(effective) ? effective : raw ?? "") &&
            ((bbox == null && b.Bbox == null) || (bbox != null && b.Bbox != null &&
                bbox.Length == b.Bbox.Count && bbox.All(double.IsFinite) && b.Bbox.All(double.IsFinite) &&
                bbox.Select(Bits).SequenceEqual(b.Bbox.Select(Bits))));
    }
    private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);
    private static (string, string, string) Key(string role, string handle, string kind) => (role, handle, kind);
    private static bool SameMembers(Frozen[] a, Frozen[] b) =>
        a.Length == b.Length && a.All(x => !string.IsNullOrWhiteSpace(x.Record.RecordId)) &&
        a.Select(x => x.Record.RecordId).Distinct(StringComparer.Ordinal).Count() == a.Length &&
        b.Select(x => x.Record.RecordId).Distinct(StringComparer.Ordinal).Count() == b.Length &&
        a.OrderBy(x => x.Record.RecordId, StringComparer.Ordinal).Select(x => x.Digest)
            .SequenceEqual(b.OrderBy(x => x.Record.RecordId, StringComparer.Ordinal).Select(x => x.Digest));

    private static Frozen Freeze(NeutralQuantityRecord n)
    {
        ArgumentNullException.ThrowIfNull(n);
        var json = JsonSerializer.Serialize(n, Json);
        var copy = JsonSerializer.Deserialize<NeutralQuantityRecord>(json, Json)!;
        if (copy.Source == null || copy.Measurement == null || copy.Measurement.Parameters == null ||
            copy.Classification == null || copy.Source.Handle == null || copy.Source.EntityType == null)
            throw new ArgumentException("Malformed neutral record.");
        return new Frozen(copy, Digest(copy));
    }
    private static Guidance Basic(NeutralQuantityRecord n, State state, string code, string message) =>
        new(n.RecordId, n.Classification.RuleKey, n.Measurement.RawValue, n.Measurement.Unit,
            n.Classification.CandidateCatalogCode, state, code, message);
    private static Snapshot Finish(Stamp stamp, IEnumerable<Guidance> records)
    {
        var copy = Array.AsReadOnly(records.ToArray());
        return new Snapshot(stamp, copy, Digest(new { stamp, records = copy }));
    }
    private static bool Sha(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
    private static bool Text(string? s) => !string.IsNullOrWhiteSpace(s);
    private static bool Valid(Stamp s) => Text(s.ProjectId) && Text(s.ActiveRunId) && Text(s.ActiveDrawingPath) &&
        Text(s.CatalogSnapshotId) && Sha(s.ActiveDrawingSha256) && Sha(s.ScanSha256) && Sha(s.ProfileSha256) &&
        Sha(s.EffectiveProfileSha256) && Sha(s.RulesSha256) && Sha(s.CatalogSha256) && Sha(s.SourceSetSha256) &&
        Sha(s.ResolutionReceiptSha256) && Sha(s.OutcomeSha256);
    private static bool Valid(SourceIdentity s) => Text(s.Role) && Text(s.RunId) && Text(s.DrawingPath) &&
        Sha(s.DrawingSha256) && Sha(s.RecordsArtifactSha256) && Sha(s.RecordObjectSha256) &&
        Sha(s.ManifestSha256) && Sha(s.AuxiliaryEvidenceSha256);

    private static string Digest<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(element, writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Write(JsonElement e, Utf8JsonWriter w)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            w.WriteStartObject();
            foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { w.WritePropertyName(p.Name); Write(p.Value, w); }
            w.WriteEndObject();
        }
        else if (e.ValueKind == JsonValueKind.Array)
        { w.WriteStartArray(); foreach (var v in e.EnumerateArray()) Write(v, w); w.WriteEndArray(); }
        else e.WriteTo(w);
    }
}
