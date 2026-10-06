using System;
using System.IO;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Read-only resolution against the exact profile file selected by the caller.
/// No profile-id lookup, fallback, cache, registration or approval takes place here.</summary>
internal static class EstimateCatalogPathResolver
{
    internal static string? ResolveFromProfile(string profileSource, string catalogFile, string? expectedHash)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileSource) ||
                !Path.IsPathFullyQualified(profileSource) || !File.Exists(profileSource) ||
                string.IsNullOrWhiteSpace(catalogFile) || !CatalogIdentity.IsValidSha256(expectedHash))
                return null;
            // Reject drive-relative/root-relative paths rather than adopting the
            // process working directory or current drive as hidden project state.
            if (Path.IsPathRooted(catalogFile) && !Path.IsPathFullyQualified(catalogFile))
                return null;
            var path = Path.GetFullPath(Path.IsPathFullyQualified(catalogFile)
                ? catalogFile
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profileSource))!, catalogFile));
            return File.Exists(path) && string.Equals(ArtifactHash.Sha256OfFile(path), expectedHash,
                StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception)
        {
            // Invalid/inaccessible paths remain unverified; a different project's
            // same-named catalog must not be substituted after an I/O failure.
            return null;
        }
    }
}
