using System;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free, exact provenance contract for the two office vehicle assets.
    /// A protected block name/comment is not proof on its own: VERIFY must also
    /// supply the fingerprint recomputed from the live block definition.
    /// </summary>
    public static class SectionOfficeVehicleAssetEvidenceLogic
    {
        public const string RearSha256 =
            "e2823d50d85e2f669944cdb68b2cc87f44090801cda75a86f66a6cc11ea38968";
        public const string FrontSha256 =
            "412e0e6f226271aa1a66a2749f598c2a2f13a237d94d0eb567e07ec206924428";

        private const string CommentPrefix =
            "MahodAI Civil Delivery embedded office asset sha256=";

        public static string SourceSha256(SectionFurnitureLogic.OfficeCarView view) =>
            view switch
            {
                SectionFurnitureLogic.OfficeCarView.Rear => RearSha256,
                SectionFurnitureLogic.OfficeCarView.Front => FrontSha256,
                _ => throw new ArgumentOutOfRangeException(nameof(view)),
            };

        public static string ProtectedBlockName(
            SectionFurnitureLogic.OfficeCarView view)
        {
            var token = view == SectionFurnitureLogic.OfficeCarView.Front
                ? "FRONT"
                : "REAR";
            return $"MHD-CD-CAR-{token}-{SourceSha256(view).ToUpperInvariant()}";
        }

        public static string SourceComment(
            SectionFurnitureLogic.OfficeCarView view) =>
            CommentPrefix + SourceSha256(view);

        public static string ProvenanceComment(
            SectionFurnitureLogic.OfficeCarView view,
            string geometrySha256)
        {
            if (!IsLowerHexSha256(geometrySha256))
                throw new ArgumentException(
                    "Geometry fingerprint must be a lowercase SHA-256 value.",
                    nameof(geometrySha256));
            return SourceComment(view) + "; geometry_sha256=" + geometrySha256;
        }

        /// <summary>
        /// Validates the complete live contract.  There is deliberately no prefix
        /// or starts-with acceptance: the pinned source SHA, exact protected name,
        /// exact comment grammar, annotation detail, and recomputed geometry SHA
        /// must all agree.
        /// </summary>
        public static bool TryValidateLiveEvidence(
            SectionFurnitureLogic.OfficeCarView view,
            string? annotationDetail,
            string? blockDefinitionName,
            string? blockDefinitionComments,
            string? recomputedGeometrySha256,
            out string error) =>
            TryValidateLiveEvidence(view, annotationDetail, blockDefinitionName, blockDefinitionComments,
                recomputedGeometrySha256, liveGeometryMatchesPinnedSource: false, out error);

        /// <summary>
        /// Same contract; <paramref name="liveGeometryMatchesPinnedSource"/> is the host's stable proof that the live
        /// definition is the audited source geometry with the canonical header. With it, a comment in the exact tool
        /// grammar whose stamped geometry hash differs only because derived extents drifted is still bound.
        /// </summary>
        public static bool TryValidateLiveEvidence(
            SectionFurnitureLogic.OfficeCarView view,
            string? annotationDetail,
            string? blockDefinitionName,
            string? blockDefinitionComments,
            string? recomputedGeometrySha256,
            bool liveGeometryMatchesPinnedSource,
            out string error)
        {
            var sourceSha = SourceSha256(view);
            if (!string.Equals(
                    annotationDetail,
                    sourceSha.Substring(0, 16),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "annotation detail does not identify the pinned office asset";
                return false;
            }

            if (!string.Equals(
                    blockDefinitionName,
                    ProtectedBlockName(view),
                    StringComparison.Ordinal))
            {
                error = "protected block definition name is not exact";
                return false;
            }

            if (!IsLowerHexSha256(recomputedGeometrySha256))
            {
                error = "live block geometry fingerprint is missing or malformed";
                return false;
            }

            var expectedComments = ProvenanceComment(view, recomputedGeometrySha256!);
            if (!string.Equals(
                    blockDefinitionComments,
                    expectedComments,
                    StringComparison.Ordinal) &&
                !(liveGeometryMatchesPinnedSource && IsExactProvenanceGrammar(view, blockDefinitionComments)))
            {
                error = "block comments do not bind the pinned source and live geometry";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>Exactly "&lt;source comment&gt;; geometry_sha256=&lt;64 lowercase hex&gt;" for this view — nothing more.</summary>
        public static bool IsExactProvenanceGrammar(
            SectionFurnitureLogic.OfficeCarView view, string? comments)
        {
            var prefix = SourceComment(view) + "; geometry_sha256=";
            return comments != null &&
                   comments.StartsWith(prefix, StringComparison.Ordinal) &&
                   IsLowerHexSha256(comments.Substring(prefix.Length));
        }

        public static bool IsLowerHexSha256(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (var c in value)
            {
                if (c is >= '0' and <= '9') continue;
                if (c is >= 'a' and <= 'f') continue;
                return false;
            }
            return true;
        }
    }
}
