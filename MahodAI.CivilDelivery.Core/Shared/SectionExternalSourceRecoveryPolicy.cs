using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Compares already published source proofs; never opens an old source path.
/// A successful comparison permits new verification, not reuse of a prior green
/// result. The caller must still prove current source bytes/live state and geometry.
/// </summary>
public static class SectionExternalSourceRecoveryPolicy
{
    public sealed record SourceProof(string Path, string Name, string Sha256,
        IReadOnlyList<string> Roles, string? Chain, bool RequiresLiveDatabase, string? LiveDatabaseRevision);
    public sealed record Relocation(string OriginalPath, string CurrentPath, string Sha256, string SourceName);
    public sealed record Result(bool Equivalent, string? Reason, IReadOnlyList<Relocation> Relocations);

    public static Result Compare(IReadOnlyList<SourceProof> producer, IReadOnlyList<SourceProof> current)
    {
        Result Reject(string reason) => new(false, reason, Array.Empty<Relocation>());
        if (producer.Count != current.Count)
            return Reject("External source evidence is missing or extra; update the section before verifying.");
        var oldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < producer.Count; i++)
        {
            if (!Valid(producer[i]) || !Valid(current[i]))
                return Reject("External source identity is incomplete or invalid.");
            if (!oldPaths.Add(PathKey(producer[i].Path)) || !newPaths.TryAdd(PathKey(current[i].Path), i))
                return Reject("External source paths are duplicated; one-to-one source authority is ambiguous.");
        }

        var unmatched = new List<SourceProof>();
        var used = new HashSet<int>();
        foreach (var source in producer)
        {
            if (!newPaths.TryGetValue(PathKey(source.Path), out var index))
            {
                unmatched.Add(source);
                continue;
            }
            // Exact paths retain the original contract: hash and live-database
            // state. Roles/name/chain were not previously contracted here and may
            // legitimately change when a newer reader supports an old HA loop.
            if (!SameContentAndLiveState(source, current[index]))
                return Reject($"External source bytes or live-database state changed at '{source.Path}'; update the section before verifying.");
            used.Add(index);
        }

        var relocations = new List<Relocation>();
        foreach (var source in unmatched)
        {
            var candidates = Enumerable.Range(0, current.Count).Where(index => !used.Contains(index) &&
                RelocationContextMatches(source, current[index])).ToList();
            if (candidates.Count != 1)
                return Reject($"External source relocation is missing or ambiguous for '{source.Path}'; expected one byte-identical source with the same name, roles and XREF chain.");
            var index = candidates[0];
            used.Add(index);
            relocations.Add(new(source.Path, current[index].Path, source.Sha256, source.Name));
        }
        return used.Count == current.Count
            ? new(true, null, relocations)
            : Reject("External source evidence was not matched one-to-one.");
    }

    private static bool SameContentAndLiveState(SourceProof a, SourceProof b) =>
        string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase) &&
        a.RequiresLiveDatabase == b.RequiresLiveDatabase &&
        string.Equals(a.LiveDatabaseRevision, b.LiveDatabaseRevision, StringComparison.Ordinal);

    private static bool RelocationContextMatches(SourceProof a, SourceProof b) =>
        SameContentAndLiveState(a, b) &&
        !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Chain) &&
        a.Roles is { Count: > 0 } && b.Roles is { Count: > 0 } &&
        a.Roles.All(role => !string.IsNullOrWhiteSpace(role)) &&
        b.Roles.All(role => !string.IsNullOrWhiteSpace(role)) &&
        string.Equals(Leaf(a.Path), Leaf(b.Path), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Name, Leaf(a.Path), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(b.Name, Leaf(b.Path), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Chain, b.Chain, StringComparison.Ordinal) &&
        a.Roles.OrderBy(role => role, StringComparer.Ordinal)
            .SequenceEqual(b.Roles.OrderBy(role => role, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool Valid(SourceProof source) =>
        ResolvedPath(source.Path) && source.Sha256 is { Length: 64 } &&
        source.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') &&
        (!source.RequiresLiveDatabase || !string.IsNullOrWhiteSpace(source.LiveDatabaseRevision));

    private static bool ResolvedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var key = PathKey(path);
        var absolute = key.Length > 3 && char.IsAsciiLetter(key[0]) && key[1] == ':' && key[2] == '/' ||
            key.StartsWith("//", StringComparison.Ordinal) && key.Split('/').Length >= 5;
        return absolute && key.Split('/').All(part => part is not "." and not "..") && Leaf(key).Length > 0;
    }

    private static string PathKey(string path) => path.Replace('\\', '/');
    private static string Leaf(string path)
    {
        var key = PathKey(path);
        return key[(key.LastIndexOf('/') + 1)..];
    }
}
