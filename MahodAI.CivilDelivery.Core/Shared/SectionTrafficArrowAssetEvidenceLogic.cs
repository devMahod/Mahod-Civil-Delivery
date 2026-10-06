using System;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Exact host-free contract for Nataly's third approved office block,
    /// HW-ARRW-01.dwg.  VERIFY combines this pin with geometry recomputed from the
    /// live block definition; a protected-looking name/comment alone never passes.
    /// </summary>
    public static class SectionTrafficArrowAssetEvidenceLogic
    {
        public const string SourceSha256 =
            "3e18667013887ca89a64aa043b5eb4fcb7195631fdd18d841fe24e46f96b2943";
        public const string ProtectedBlockName =
            "MHD-CD-TRAFFIC-ARROW-3E18667013887CA89A64AA043B5EB4FCB7195631FDD18D841FE24E46F96B2943";
        public const string SourceComment =
            "MahodAI Civil Delivery embedded office traffic arrow sha256=" + SourceSha256;

        public static string ProvenanceComment(string geometrySha256)
        {
            if (!SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(
                    geometrySha256))
                throw new ArgumentException(
                    "Traffic-arrow geometry fingerprint must be lowercase SHA-256.",
                    nameof(geometrySha256));
            return SourceComment + "; geometry_sha256=" + geometrySha256;
        }

        public static bool TryValidateLiveEvidence(
            string? annotationAssetSha256,
            string? annotationGeometrySha256,
            string? blockDefinitionName,
            string? blockDefinitionComments,
            string? recomputedGeometrySha256,
            bool definitionUsesByBlockColor,
            out string error) =>
            TryValidateLiveEvidence(annotationAssetSha256, annotationGeometrySha256, blockDefinitionName,
                blockDefinitionComments, recomputedGeometrySha256, definitionUsesByBlockColor,
                liveGeometryMatchesPinnedSource: false, out error);

        /// <summary>
        /// Same contract; <paramref name="liveGeometryMatchesPinnedSource"/> is the host's stable proof that the live
        /// definition is the audited (ByBlock-normalized) source geometry with the canonical header. With it, a
        /// stamped geometry hash that differs only because derived extents drifted in the graphics cache still binds.
        /// </summary>
        public static bool TryValidateLiveEvidence(
            string? annotationAssetSha256,
            string? annotationGeometrySha256,
            string? blockDefinitionName,
            string? blockDefinitionComments,
            string? recomputedGeometrySha256,
            bool definitionUsesByBlockColor,
            bool liveGeometryMatchesPinnedSource,
            out string error)
        {
            if (!string.Equals(
                    annotationAssetSha256, SourceSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "annotation does not pin the approved office arrow bytes";
                return false;
            }
            if (!string.Equals(
                    blockDefinitionName, ProtectedBlockName,
                    StringComparison.Ordinal))
            {
                error = "traffic-arrow protected definition name is not exact";
                return false;
            }
            if (!SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(
                    recomputedGeometrySha256) ||
                (!string.Equals(
                     annotationGeometrySha256, recomputedGeometrySha256,
                     StringComparison.OrdinalIgnoreCase) &&
                 !(liveGeometryMatchesPinnedSource &&
                   SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(annotationGeometrySha256))))
            {
                error = "traffic-arrow live geometry does not match annotation evidence";
                return false;
            }
            if (!string.Equals(
                    blockDefinitionComments,
                    ProvenanceComment(recomputedGeometrySha256!),
                    StringComparison.Ordinal) &&
                !(liveGeometryMatchesPinnedSource && IsExactProvenanceGrammar(blockDefinitionComments)))
            {
                error = "traffic-arrow comments do not bind source and live geometry";
                return false;
            }
            if (!definitionUsesByBlockColor)
            {
                error = "traffic-arrow definition cannot inherit the required strip color";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>Exactly "&lt;source comment&gt;; geometry_sha256=&lt;64 lowercase hex&gt;" — nothing more.</summary>
        public static bool IsExactProvenanceGrammar(string? comments)
        {
            const string prefix = SourceComment + "; geometry_sha256=";
            return comments != null &&
                   comments.StartsWith(prefix, StringComparison.Ordinal) &&
                   SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(comments.Substring(prefix.Length));
        }
    }
}
