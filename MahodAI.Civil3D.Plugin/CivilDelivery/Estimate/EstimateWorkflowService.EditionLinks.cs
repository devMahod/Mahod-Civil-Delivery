using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    /// <summary>
    /// The library's recipe codes against the active, verified price list: which are listed, which an approved link
    /// covers, and the ranked candidates for the rest. Read-only; proposes nothing as approved.
    /// </summary>
    public static IReadOnlyList<EditionLinkProposal> EditionLinkProposals(ProjectProfile profile, CatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        var codes = EngineerBoqLibrary.For(profile).Rules.SelectMany(r => r.Emits).Select(e => e.Code);
        return EditionLinkPolicy.Propose(codes, catalog, profile.Estimate.EditionLinks);
    }

    /// <summary>
    /// Records the engineer's edition links for the profile's ACTIVE price list, through the profile CAS. The whole
    /// batch is validated before the profile changes: the list must be the verified active one, every request must be
    /// a library code with its reference unit, and every link must apply as approved. Nothing prices or approves an
    /// item for a measured group; the links only tell the library which item of this list its recipe line means.
    /// </summary>
    public ProjectProfileWriter.SaveResult SaveEditionLinks(
        ProjectProfile profile,
        CatalogSnapshot catalog,
        IReadOnlyList<EditionLinkRequest> requests,
        string approvedBy,
        string reason,
        string targetPath,
        ProjectProfileWriter.ExpectedProfileState expectedProfileState)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(expectedProfileState);
        var path = RequireProfileWriteTarget(targetPath);
        var approver = approvedBy?.Trim();
        if (string.IsNullOrWhiteSpace(approver) || approver.Any(char.IsControl))
            throw new ArgumentException("שם מאשר בשורה אחת חובה לקישורי מהדורה.", nameof(approvedBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("סיבת האישור חובה לקישורי מהדורה.", nameof(reason));
        if (!CatalogIdentity.TryGetActiveProfileIdentity(profile, out var identity, out _) || identity == null ||
            !CatalogIdentity.SnapshotMatches(identity, catalog))
            throw new InvalidOperationException("קישורי מהדורה נשמרים רק מול המחירון הפעיל והמאומת של הפרופיל; טען את המחירון מחדש.");
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedProfileState);

        var links = profile.Estimate.EditionLinks
            ?? throw new InvalidOperationException("estimate.edition_links is not a list; reload the project profile.");
        var updated = EditionLinkPolicy.Approve(links, requests, catalog, approver, reason.Trim(), DateTime.UtcNow);
        var check = EditionLinkPolicy.WithLibraryAliases(catalog, updated, out var stale);
        foreach (var request in requests)
            if (!string.Equals(check.ResolveLibraryCode(request.LibraryCode), request.CatalogCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"הקישור {request.LibraryCode} → {request.CatalogCode} לא היה חל כפי שאושר ({string.Join("; ", stale)}); לא נשמר דבר.");

        var old = links.ToList();
        try
        {
            links.Clear();
            links.AddRange(updated);
            var errors = EditionLinkPolicy.Validate(profile.Estimate, profile.ProfileId);
            if (errors.Count != 0)
                throw new InvalidOperationException($"{errors[0].Code}: {errors[0].Title}. {errors[0].Message}");
            var summary = $"estimate edition links approved: list={catalog.SnapshotId}; count={requests.Count}; " +
                          "links=" + OneLineForAudit(string.Join(", ", requests.Select(r => r.LibraryCode + "->" + r.CatalogCode)));
            return ProjectProfileWriter.Save(profile, path, summary, approver, expectedProfileState,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["catalog:" + catalog.SnapshotId] = catalog.FileHash });
        }
        catch
        {
            links.Clear();
            links.AddRange(old);
            throw;
        }
    }
}
