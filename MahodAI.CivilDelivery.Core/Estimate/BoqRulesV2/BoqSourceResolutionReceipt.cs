using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.BoqRulesV2;

/// <summary>Pure observations, not a source selector, filesystem reader or approval.
/// The owner supplies a complete inventory and the actual resolver decision. Bytes
/// must be read under its stable-read contract; this class proves no disk freshness.</summary>
public static class BoqSourceResolutionReceipt
{
    public enum ArtifactKind { Manifest, Records, Findings, HatchDiagnostics, ScanMetadata }
    public enum ReadState { Parsed, Missing, Unavailable, NotRead, Unlisted, HashMismatch, ParseFailed }
    public enum SelectionState { Active, Selected, Missing, Rejected, Unavailable }
    public enum InventoryState { Complete, Partial, Unavailable }
    public enum DrawingState { Unchanged, Changed, Unavailable, NotChecked }

    public sealed record Artifact(ArtifactKind Kind, string? Path, ReadState State,
        long? ByteCount, string? ActualSha256, string? ListedSha256, string? ReasonCode);
    public sealed record Parsed<T>(Artifact Evidence, T? Value)
    { public bool Success => Evidence.State == ReadState.Parsed; }
    public sealed record Role(string Id, string Pattern, int RuleOrder);
    /// <summary>Rank is the observed order from the existing resolver, including ties.
    /// We neither invent a tie-break nor claim this order is a deterministic selector.</summary>
    public sealed record Candidate(string RunId, string ManifestPath, string ManifestSha256,
        string DrawingPath, DateTime CompletedAtUtc, int ObservedRank);
    public sealed record Choice(string RoleId, SelectionState State, string? RunId,
        string? DrawingPath, string? ScannedDrawingSha256, DrawingState DrawingState,
        string? CurrentDrawingSha256, string? ReasonCode, DateTime? ScannedAtUtc,
        IReadOnlyList<Candidate> Candidates, IReadOnlyList<Artifact> Artifacts);
    public sealed record Request(string PolicyVersion, string RulesSha256, string ActiveRunId,
        string SourceFolder, InventoryState InventoryState, IReadOnlyList<Artifact> InventoryFailures,
        IReadOnlyList<Role> Roles, IReadOnlyList<Choice> Choices);
    public sealed record Receipt(Request Observed, string Sha256)
    { public bool IsApproval => false; public bool ProvesFreshness => false; }

    /// <summary>Hash and deserialize one private copy of exactly the supplied bytes.
    /// Non-manifest inputs require their manifest-listed hash. Never reads a path.</summary>
    public static Parsed<T> CaptureJson<T>(ArtifactKind kind, string path, ReadOnlySpan<byte> bytes,
        string? listedSha256, JsonSerializerOptions? options = null)
    {
        Text(path, nameof(path));
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown artifact kind.");
        if (listedSha256 != null && !Sha(listedSha256)) throw new ArgumentException("Invalid listed SHA.");
        var copy = bytes.ToArray();
        var actual = Convert.ToHexString(SHA256.HashData(copy));
        var listed = listedSha256?.ToUpperInvariant();
        Parsed<T> Fail(ReadState state, string reason) => new(new(kind,path,state,copy.LongLength,actual,listed,reason),default);
        if (listed == null && kind != ArtifactKind.Manifest) return Fail(ReadState.Unlisted,"not_listed_in_manifest");
        if (listed != null && listed != actual) return Fail(ReadState.HashMismatch,"artifact_hash_mismatch");
        try
        {
            // ReadAllText/the existing stream reader accept a UTF-8 BOM. It remains
            // part of the byte hash but is not a JSON token for the span parser.
            int offset = copy.Length >= 3 && copy[0] == 0xEF && copy[1] == 0xBB && copy[2] == 0xBF ? 3 : 0;
            var value = JsonSerializer.Deserialize<T>(copy.AsSpan(offset), options);
            return value is null ? Fail(ReadState.ParseFailed,"json_null") :
                new(new(kind,path,ReadState.Parsed,copy.LongLength,actual,listed,null),value);
        }
        catch (JsonException) { return Fail(ReadState.ParseFailed,"invalid_json"); }
        catch (NotSupportedException) { return Fail(ReadState.ParseFailed,"parser_unsupported"); }
    }

    public static Artifact NotRead(ArtifactKind kind, string? path, ReadState state, string reasonCode,
        string? listedSha256 = null)
    {
        if (state is not (ReadState.Missing or ReadState.Unavailable or ReadState.NotRead or ReadState.Unlisted))
            throw new ArgumentException("Use captured bytes for read outcomes.");
        if(path!=null) Text(path,nameof(path)); Text(reasonCode,nameof(reasonCode));
        if (!Enum.IsDefined(kind) || (listedSha256 != null && !Sha(listedSha256))) throw new ArgumentException("Invalid observation.");
        return Validate(new(kind,path,state,null,null,listedSha256?.ToUpperInvariant(),reasonCode));
    }

    /// <summary>Freezes a complete role partition and canonicalizes receipt serialization.
    /// Does not select latest, parse source semantics, verify drawings, or run BoQ.</summary>
    public static Receipt Build(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Text(request.PolicyVersion,nameof(request.PolicyVersion)); Text(request.ActiveRunId,nameof(request.ActiveRunId));
        Text(request.SourceFolder,nameof(request.SourceFolder));
        if (!Sha(request.RulesSha256) || !Enum.IsDefined(request.InventoryState)) throw new ArgumentException("Invalid request identity.");
        ArgumentNullException.ThrowIfNull(request.Roles); ArgumentNullException.ThrowIfNull(request.Choices);
        ArgumentNullException.ThrowIfNull(request.InventoryFailures);
        var roles=request.Roles.OrderBy(r=>r.RuleOrder).ToArray();
        if (roles.Length==0 || roles.Any(r=>string.IsNullOrWhiteSpace(r.Id)||string.IsNullOrWhiteSpace(r.Pattern)||r.RuleOrder<0) ||
            roles.Select(r=>r.Id).Distinct(StringComparer.Ordinal).Count()!=roles.Length || roles.Select(r=>r.RuleOrder).Distinct().Count()!=roles.Length)
            throw new ArgumentException("Roles must be distinct and ordered.");
        if (request.Choices.Count!=roles.Length || request.Choices.Select(c=>c.RoleId).Distinct(StringComparer.Ordinal).Count()!=roles.Length ||
            !roles.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal).SetEquals(request.Choices.Select(c=>c.RoleId)))
            throw new ArgumentException("Every declared role needs exactly one observed outcome.");
        var failures=request.InventoryFailures.Select(Validate).OrderBy(a=>a.Path,StringComparer.Ordinal).ThenBy(a=>a.Kind).ToArray();
        if(failures.Select(a=>(a.Path,a.Kind)).Distinct().Count()!=failures.Length) throw new ArgumentException("Duplicate inventory observation.");
        if (failures.Any(a=>a.State==ReadState.Parsed) || (request.InventoryState==InventoryState.Complete && failures.Length!=0))
            throw new ArgumentException("Inventory failures cannot be hidden behind complete discovery.");
        var choices=new List<Choice>();
        foreach(var role in roles)
        {
            var c=request.Choices.Single(c=>c.RoleId==role.Id);
            if (!Enum.IsDefined(c.State)||!Enum.IsDefined(c.DrawingState)||c.Candidates==null||c.Artifacts==null) throw new ArgumentException("Invalid choice.");
            var candidates=c.Candidates.OrderBy(x=>x.ObservedRank).ToArray();
            if (candidates.Any(x=>string.IsNullOrWhiteSpace(x.RunId)||string.IsNullOrWhiteSpace(x.ManifestPath)||!Sha(x.ManifestSha256)||
                    string.IsNullOrWhiteSpace(x.DrawingPath)||x.CompletedAtUtc.Kind!=DateTimeKind.Utc||x.ObservedRank<0) ||
                candidates.Select(x=>x.ObservedRank).Distinct().Count()!=candidates.Length || candidates.Select(x=>x.RunId).Distinct(StringComparer.Ordinal).Count()!=candidates.Length)
                throw new ArgumentException("Invalid candidate observations.");
            var artifacts=c.Artifacts.Select(Validate).OrderBy(a=>a.Kind).ToArray();
            if (!Enum.GetValues<ArtifactKind>().SequenceEqual(artifacts.Select(a=>a.Kind)))
                throw new ArgumentException("Every artifact needs an explicit read state, including optional evidence.");
            if (c.ScannedAtUtc is { } time && time.Kind!=DateTimeKind.Utc) throw new ArgumentException("Scan time must be UTC.");
            if(c.ScannedAtUtc!=null && artifacts.Single(a=>a.Kind==ArtifactKind.ScanMetadata).State!=ReadState.Parsed)
                throw new ArgumentException("Claimed scan time needs read metadata evidence.");
            if (c.State==SelectionState.Missing && (request.InventoryState!=InventoryState.Complete || candidates.Length!=0 || c.RunId!=null))
                throw new ArgumentException("Incomplete/unreadable discovery is not a missing role.");
            if (c.State is SelectionState.Active or SelectionState.Selected)
            {
                Text(c.RunId,nameof(c.RunId)); Text(c.DrawingPath,nameof(c.DrawingPath));
                if (!Sha(c.ScannedDrawingSha256)||!Sha(c.CurrentDrawingSha256)||c.DrawingState!=DrawingState.Unchanged ||
                    !string.Equals(c.ScannedDrawingSha256,c.CurrentDrawingSha256,StringComparison.OrdinalIgnoreCase) ||
                    artifacts.Single(a=>a.Kind==ArtifactKind.Manifest).State!=ReadState.Parsed ||
                    artifacts.Single(a=>a.Kind==ArtifactKind.Records).State!=ReadState.Parsed)
                    throw new ArgumentException("Selected source needs its observed unchanged drawing and parsed manifest/records.");
                if (c.State==SelectionState.Active && c.RunId!=request.ActiveRunId) throw new ArgumentException("Wrong active run.");
                if (c.State==SelectionState.Selected && !candidates.Any(x=>x.RunId==c.RunId&&x.DrawingPath==c.DrawingPath&&
                    x.ManifestPath==artifacts.Single(a=>a.Kind==ArtifactKind.Manifest).Path&&
                    string.Equals(x.ManifestSha256,artifacts.Single(a=>a.Kind==ArtifactKind.Manifest).ActualSha256,StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Selected run/manifest is not in the observed inventory.");
            }
            else Text(c.ReasonCode,nameof(c.ReasonCode));
            choices.Add(c with { Candidates=Array.AsReadOnly(candidates.Select(x=>x with{ManifestSha256=x.ManifestSha256.ToUpperInvariant()}).ToArray()),
                Artifacts=Array.AsReadOnly(artifacts),ScannedDrawingSha256=c.ScannedDrawingSha256?.ToUpperInvariant(),CurrentDrawingSha256=c.CurrentDrawingSha256?.ToUpperInvariant() });
        }
        if(choices.Count(c=>c.State==SelectionState.Active)!=1) throw new ArgumentException("Exactly one active role is required.");
        var frozen=request with { RulesSha256=request.RulesSha256.ToUpperInvariant(),Roles=Array.AsReadOnly(roles),
            Choices=Array.AsReadOnly(choices.ToArray()),InventoryFailures=Array.AsReadOnly(failures) };
        return new(frozen,Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(frozen))));
    }

    private static Artifact Validate(Artifact a)
    {
        ArgumentNullException.ThrowIfNull(a); if(a.Path!=null) Text(a.Path,nameof(a.Path));
        if(!Enum.IsDefined(a.Kind)||!Enum.IsDefined(a.State)||(a.ListedSha256!=null&&!Sha(a.ListedSha256))) throw new ArgumentException("Invalid artifact observation.");
        bool read=a.State is ReadState.Parsed or ReadState.HashMismatch or ReadState.ParseFailed || a.ActualSha256!=null;
        if(read) Text(a.Path,nameof(a.Path));
        if(read && (a.ByteCount is null or <0 || !Sha(a.ActualSha256))) throw new ArgumentException("Read bytes need a hash and length.");
        if(!read && (a.ByteCount!=null||a.ActualSha256!=null)) throw new ArgumentException("Unread source has no byte identity.");
        if((a.State is ReadState.Missing or ReadState.Unavailable or ReadState.NotRead) && read)
            throw new ArgumentException("Unread state cannot claim captured bytes.");
        if(a.State==ReadState.Unlisted && a.ListedSha256!=null) throw new ArgumentException("Unlisted artifact cannot claim a listed hash.");
        if(a.State==ReadState.HashMismatch && (a.ListedSha256==null||string.Equals(a.ListedSha256,a.ActualSha256,StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Hash mismatch needs different actual/listed hashes.");
        if((a.State is ReadState.Parsed or ReadState.ParseFailed) && ((a.Kind!=ArtifactKind.Manifest && a.ListedSha256==null) ||
            (a.ListedSha256!=null&&!string.Equals(a.ListedSha256,a.ActualSha256,StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Parsed artifact does not match listed bytes.");
        if(a.State!=ReadState.Parsed) Text(a.ReasonCode,nameof(a.ReasonCode));
        return a with{ActualSha256=a.ActualSha256?.ToUpperInvariant(),ListedSha256=a.ListedSha256?.ToUpperInvariant()};
    }
    private static bool Sha(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
    private static void Text(string? value,string name) { if(string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing "+name); }
}
