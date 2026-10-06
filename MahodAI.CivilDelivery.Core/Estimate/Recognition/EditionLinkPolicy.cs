using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using EditionItemLink = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.EditionItemLink;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>How a price-list item relates to a library item's reference text. Never an approval by itself.</summary>
public enum EditionMatchKind
{
    /// <summary>Same full description (<see cref="CatalogTextIdentity.SameText"/>) and same unit.</summary>
    ExactText,
    /// <summary>The list's description is the reference cut short: the parameters after the cut are unknown.</summary>
    TruncatedPrefix,
    /// <summary>Different wording, same identity parameters (thickness, profile, class, grade, model) and unit.</summary>
    ConsistentParameters,
    /// <summary>Same unit and shared words, but an identity parameter differs — another item unless the engineer says so.</summary>
    ParameterConflict,
}

public sealed record EditionCandidate(string Code, string Description, string Unit, decimal? Price, EditionMatchKind Kind,
    IReadOnlyList<string> Differences, double WordOverlap);

/// <summary>
/// One library code against the active price list: present as-is, linked by an approved link, or a list of candidates
/// (possibly empty) for the engineer. <see cref="Reference"/> is the library item's own description and unit.
/// </summary>
public sealed record EditionLinkProposal(string LibraryCode, LibraryItemReferences.Reference? Reference, bool PresentInCatalog,
    string? LinkedCode, IReadOnlyList<EditionCandidate> Candidates);

/// <summary>An engineer's choice for one library code (the approval itself carries approver, reason and time).</summary>
public sealed record EditionLinkRequest(string LibraryCode, string CatalogCode, EditionMatchKind Kind);

/// <summary>
/// Links from library recipe codes to the items of another price-list edition. The library was written in one edition
/// (<see cref="LibraryItemReferences"/>); another edition numbers and words its items differently, and neither the
/// number nor shared words prove that two rows are the same item. This policy proposes candidates with the evidence
/// that separates them (<see cref="CatalogTextIdentity"/>), records only engineer-approved links, and applies a link
/// only to the exact price list and item it was approved against.
/// </summary>
public static class EditionLinkPolicy
{
    /// <summary>A profile carrying edition links is written as schema 3 so an older build refuses it instead of erasing them.</summary>
    public const int EditionLinksSchemaVersion = 3;
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Revoked = "revoked";
    public const string IncompleteCode = "SHR-PROFILE-EDITION-LINK-INCOMPLETE";
    public const string DuplicateCode = "SHR-PROFILE-EDITION-LINK-DUPLICATE";
    public const string ListNullCode = "SHR-PROFILE-EDITION-LINKS-NULL";
    private const double MinimumWordOverlap = 0.34;

    public static string EvidenceName(EditionMatchKind kind) => kind switch
    {
        EditionMatchKind.ExactText => "exact_text",
        EditionMatchKind.TruncatedPrefix => "truncated_prefix",
        EditionMatchKind.ConsistentParameters => "consistent_parameters",
        _ => "parameter_conflict",
    };

    private static readonly HashSet<string> Evidences = new(StringComparer.Ordinal)
        { "exact_text", "truncated_prefix", "consistent_parameters", "parameter_conflict" };

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { Active, Superseded, Revoked };

    // ---------------------------------------------------------------- proposals

    /// <summary>
    /// For each library code: present in <paramref name="catalog"/>, linked by an applicable approved link, or ranked
    /// candidates of the same unit (exact text, then truncated prefix, then consistent parameters, then parameter
    /// conflicts with what differs). A candidate needs a word overlap of at least a third with the reference unless
    /// its text is exact or a prefix. Nothing here is an approval.
    /// </summary>
    public static IReadOnlyList<EditionLinkProposal> Propose(IEnumerable<string> libraryCodes, CatalogSnapshot catalog,
        IReadOnlyList<EditionItemLink>? links, int maxCandidates = 5)
    {
        ArgumentNullException.ThrowIfNull(libraryCodes);
        ArgumentNullException.ThrowIfNull(catalog);
        var linked = WithLibraryAliases(catalog, links, out _).LibraryAliases;
        var items = catalog.Items.Values.Select(item => new
        {
            Item = item,
            Text = CatalogTextIdentity.Normalize(item.Description),
            Unit = CatalogTextIdentity.UnitKey(item.UnitRaw),
            Parameters = CatalogTextIdentity.Parameters(item.Description),
            Words = CatalogTextIdentity.Words(item.Description),
        }).ToList();
        var result = new List<EditionLinkProposal>();
        foreach (var code in libraryCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c, StringComparer.Ordinal))
        {
            LibraryItemReferences.Items.TryGetValue(code, out var reference);
            if (catalog.Items.ContainsKey(code) || reference == null)
            {
                result.Add(new EditionLinkProposal(code, reference, catalog.Items.ContainsKey(code), null, Array.Empty<EditionCandidate>()));
                continue;
            }
            var text = CatalogTextIdentity.Normalize(reference.Description);
            var unit = CatalogTextIdentity.UnitKey(reference.Unit);
            var parameters = CatalogTextIdentity.Parameters(reference.Description);
            var words = CatalogTextIdentity.Words(reference.Description);
            var candidates = new List<EditionCandidate>();
            foreach (var entry in items.Where(e => e.Unit == unit))
            {
                var overlap = words.Count + entry.Words.Count == 0 ? 0
                    : (double)words.Intersect(entry.Words).Count() / words.Union(entry.Words).Count();
                EditionMatchKind kind;
                var differences = new List<string>();
                if (CatalogTextIdentity.SameText(entry.Item.Description, reference.Description)) kind = EditionMatchKind.ExactText;
                else if (CatalogTextIdentity.IsTruncatedPrefixOf(entry.Item.Description, reference.Description))
                {
                    kind = EditionMatchKind.TruncatedPrefix;
                    differences.Add("התיאור במחירון קטוע; מה שאחרי החיתוך אינו ידוע");
                }
                else if (overlap < MinimumWordOverlap) continue;
                else if (entry.Parameters.SetEquals(parameters)) kind = EditionMatchKind.ConsistentParameters;
                else
                {
                    kind = EditionMatchKind.ParameterConflict;
                    differences.AddRange(parameters.Except(entry.Parameters).OrderBy(p => p, StringComparer.Ordinal).Select(p => "בספרייה: " + p));
                    differences.AddRange(entry.Parameters.Except(parameters).OrderBy(p => p, StringComparer.Ordinal).Select(p => "במהדורה: " + p));
                }
                catalog.Prices.TryGetValue(entry.Item.Code, out var price);
                candidates.Add(new EditionCandidate(entry.Item.Code, entry.Item.Description, entry.Item.UnitRaw,
                    price is { IsMissing: false } ? price.Price : null, kind, differences, Math.Round(overlap, 3)));
            }
            var ranked = candidates.OrderBy(c => c.Kind).ThenByDescending(c => c.WordOverlap).ThenBy(c => c.Code, StringComparer.Ordinal)
                .Take(Math.Max(1, maxCandidates)).ToList();
            result.Add(new EditionLinkProposal(code, reference, false,
                linked.TryGetValue(code, out var linkedCode) ? linkedCode : null, ranked));
        }
        return result;
    }

    /// <summary>Library codes of the recipes that the active price list neither lists nor links.</summary>
    public static IReadOnlyList<string> Unresolved(EngineerBoqLibrary library, CatalogSnapshot catalog) =>
        library.Rules.SelectMany(r => r.Emits).Select(e => e.Code).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(code => catalog.ResolveLibraryCode(code) == null).OrderBy(c => c, StringComparer.Ordinal).ToList();

    // ---------------------------------------------------------------- application

    /// <summary>
    /// The price list with the aliases of its applicable links: active, approved against this list's id and file
    /// hash, one per library code, pointing to a listed item whose fingerprint is unchanged and whose unit is the
    /// reference unit. Every other active link for this list is returned in <paramref name="stale"/> and not applied;
    /// links of other lists are ignored.
    /// </summary>
    public static CatalogSnapshot WithLibraryAliases(CatalogSnapshot catalog, IReadOnlyList<EditionItemLink>? links,
        out IReadOnlyList<string> stale)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var problems = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mine = (links ?? Array.Empty<EditionItemLink>())
            .Where(l => l != null && string.Equals(l.Status?.Trim(), Active, StringComparison.Ordinal) &&
                        string.Equals(l.CatalogId?.Trim(), catalog.SnapshotId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(l.CatalogHash?.Trim(), catalog.FileHash, StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.LibraryCode?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var group in mine)
        {
            var link = group.First();
            if (group.Count() > 1)
            {
                problems.Add($"{group.Key}: {group.Count()} קישורים פעילים לאותו מחירון — אף אחד לא הוחל");
                continue;
            }
            if (!LibraryItemReferences.Items.TryGetValue(group.Key, out var reference))
                problems.Add($"{group.Key}: אינו קוד של הספרייה — הקישור לא הוחל");
            else if (string.IsNullOrWhiteSpace(link.CatalogCode) || !catalog.Items.TryGetValue(link.CatalogCode.Trim(), out var item))
                problems.Add($"{group.Key} → {link.CatalogCode}: הסעיף אינו במחירון — הקישור לא הוחל");
            else if (!string.Equals(link.CatalogItemFingerprint, CatalogIdentity.ItemFingerprint(item), StringComparison.OrdinalIgnoreCase))
                problems.Add($"{group.Key} → {item.Code}: תיאור או יחידת הסעיף השתנו מאז האישור — הקישור לא הוחל");
            else if (CatalogTextIdentity.UnitKey(item.UnitRaw) != CatalogTextIdentity.UnitKey(reference.Unit))
                problems.Add($"{group.Key} → {item.Code}: יחידה שונה מיחידת הספרייה — הקישור לא הוחל");
            else aliases[group.Key] = item.Code;
        }
        stale = problems;
        // Rebuilt every time: an already linked snapshot whose link was revoked, duplicated or changed loses its alias.
        if (aliases.Count == 0 && catalog.LibraryAliases.Count == 0) return catalog;
        return new CatalogSnapshot
        {
            SnapshotId = catalog.SnapshotId,
            FileHash = catalog.FileHash,
            PublicationNote = catalog.PublicationNote,
            Items = catalog.Items,
            Prices = catalog.Prices,
            LibraryAliases = aliases,
        };
    }

    // ---------------------------------------------------------------- recording

    /// <summary>
    /// The links after approving <paramref name="requests"/> against <paramref name="catalog"/>: each request adds an
    /// active link and supersedes the active link of the same library code for the same list. A request is refused
    /// unless its library code has a reference, its item is listed and has the reference unit. Nothing else changes.
    /// </summary>
    public static List<EditionItemLink> Approve(IReadOnlyList<EditionItemLink>? existing, IReadOnlyList<EditionLinkRequest> requests,
        CatalogSnapshot catalog, string approvedBy, string reason, DateTime approvedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(catalog);
        if (requests.Count == 0) throw new ArgumentException("לא נבחרו קישורים לאישור.", nameof(requests));
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("שם המאשר חובה.", nameof(approvedBy));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("סיבת האישור חובה.", nameof(reason));
        if (requests.GroupBy(r => r.LibraryCode, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("אותו קוד ספרייה נבחר יותר מפעם אחת.", nameof(requests));
        var result = (existing ?? Array.Empty<EditionItemLink>()).Select(Copy).ToList();
        foreach (var request in requests)
        {
            if (!LibraryItemReferences.Items.TryGetValue(request.LibraryCode, out var reference))
                throw new ArgumentException($"'{request.LibraryCode}' אינו קוד של הספרייה.", nameof(requests));
            if (!catalog.Items.TryGetValue(request.CatalogCode, out var item))
                throw new ArgumentException($"הסעיף '{request.CatalogCode}' אינו במחירון הפעיל.", nameof(requests));
            if (CatalogTextIdentity.UnitKey(item.UnitRaw) != CatalogTextIdentity.UnitKey(reference.Unit))
                throw new ArgumentException($"יחידת '{item.Code}' ({item.UnitRaw.Trim()}) שונה מיחידת '{request.LibraryCode}' ({reference.Unit}).",
                    nameof(requests));
            foreach (var previous in result.Where(l => string.Equals(l.Status, Active, StringComparison.Ordinal) &&
                                                       string.Equals(l.LibraryCode, request.LibraryCode, StringComparison.OrdinalIgnoreCase) &&
                                                       string.Equals(l.CatalogId, catalog.SnapshotId, StringComparison.OrdinalIgnoreCase) &&
                                                       string.Equals(l.CatalogHash, catalog.FileHash, StringComparison.OrdinalIgnoreCase)))
                previous.Status = Superseded;
            result.Add(new EditionItemLink
            {
                LibraryCode = request.LibraryCode.Trim(),
                CatalogCode = item.Code,
                CatalogId = catalog.SnapshotId,
                CatalogHash = catalog.FileHash,
                CatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item),
                Evidence = EvidenceName(request.Kind),
                Status = Active,
                Reason = reason.Trim(),
                ApprovedBy = approvedBy.Trim(),
                ApprovedAtUtc = DateTime.SpecifyKind(approvedAtUtc, DateTimeKind.Utc),
            });
        }
        return result;
    }

    private static EditionItemLink Copy(EditionItemLink link) => new()
    {
        LibraryCode = link.LibraryCode, CatalogCode = link.CatalogCode, CatalogId = link.CatalogId, CatalogHash = link.CatalogHash,
        CatalogItemFingerprint = link.CatalogItemFingerprint, Evidence = link.Evidence, Status = link.Status, Reason = link.Reason,
        ApprovedBy = link.ApprovedBy, ApprovedAtUtc = link.ApprovedAtUtc,
    };

    // ---------------------------------------------------------------- profile contract

    /// <summary>Gives link timestamps UTC kind after a YAML load, as for family decisions.</summary>
    public static void NormalizeTimestamps(ProjectProfile.EstimateProfile? estimate)
    {
        foreach (var link in estimate?.EditionLinks ?? new List<EditionItemLink>())
            if (link?.ApprovedAtUtc is { } time)
                link.ApprovedAtUtc = time.Kind == DateTimeKind.Utc ? time : DateTime.SpecifyKind(time, DateTimeKind.Utc);
    }

    /// <summary>Structural errors of estimate.edition_links; the loader and the writer both refuse them.</summary>
    public static List<DeliveryFinding> Validate(ProjectProfile.EstimateProfile estimate, string? profileId)
    {
        var findings = new List<DeliveryFinding>();
        void Add(string code, string title, string message) => findings.Add(new DeliveryFinding
        {
            Code = code, Domain = "shared", Severity = FindingSeverity.Error, Title = title, Message = message, ProjectProfileId = profileId,
        });
        if (estimate.EditionLinks == null)
        {
            Add(ListNullCode, "estimate.edition_links must be a list", "Use an empty list when no edition link has been approved.");
            return findings;
        }
        for (var i = 0; i < estimate.EditionLinks.Count; i++)
        {
            var link = estimate.EditionLinks[i];
            var problems = new List<string>();
            if (link == null) problems.Add("entry is null");
            else
            {
                if (string.IsNullOrWhiteSpace(link.LibraryCode)) problems.Add("library_code");
                if (string.IsNullOrWhiteSpace(link.CatalogCode)) problems.Add("catalog_code");
                if (string.IsNullOrWhiteSpace(link.CatalogId)) problems.Add("catalog_id");
                if (!CatalogIdentity.IsValidSha256(link.CatalogHash)) problems.Add("catalog_hash");
                if (!CatalogIdentity.IsValidSha256(link.CatalogItemFingerprint)) problems.Add("catalog_item_fingerprint");
                if (link.Evidence == null || !Evidences.Contains(link.Evidence.Trim())) problems.Add("evidence");
                if (link.Status == null || !Statuses.Contains(link.Status.Trim())) problems.Add("status");
                if (string.IsNullOrWhiteSpace(link.Reason)) problems.Add("reason");
                if (string.IsNullOrWhiteSpace(link.ApprovedBy)) problems.Add("approved_by");
                if (link.ApprovedAtUtc == null) problems.Add("approved_at_utc");
            }
            if (problems.Count > 0)
                Add(IncompleteCode, $"estimate.edition_links[{i}] is incomplete",
                    "Missing or invalid: " + string.Join(", ", problems) + ". An edition link requires the library code, the item and the " +
                    "price list it was approved against (id, hash, item fingerprint), its evidence, status, reason, named approver and UTC time.");
        }
        foreach (var duplicate in estimate.EditionLinks
                     .Where(l => l != null && string.Equals(l.Status?.Trim(), Active, StringComparison.Ordinal))
                     .GroupBy(l => $"{l.LibraryCode?.Trim().ToUpperInvariant()}|{l.CatalogId?.Trim().ToLowerInvariant()}|{l.CatalogHash?.Trim().ToLowerInvariant()}",
                         StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            Add(DuplicateCode, "estimate.edition_links has more than one active link for one library code and price list",
                $"'{duplicate.Key}' is active {duplicate.Count()} times; approve a new link (it supersedes) instead of editing the file.");
        return findings;
    }
}
