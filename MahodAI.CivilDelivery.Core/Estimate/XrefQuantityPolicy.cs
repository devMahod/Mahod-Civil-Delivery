using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Pure safety rules used by the Civil adapter before an XREF measurement can
    /// enter the neutral-record boundary.  Similarity transforms (translation,
    /// rotation/reflection and one uniform scale) preserve the length/area semantics
    /// used by the supported adapters.  Shear, degenerate axes and non-uniform scale
    /// are blocked rather than approximated.
    /// </summary>
    public static class XrefQuantityPolicy
    {
        public sealed record TransformCheck(bool IsSafe, double LengthScale, string? Failure);

        /// <summary>
        /// Save identity stored inside a DWG. FingerprintGuid identifies the drawing;
        /// VersionGuid, save count and update time identify the saved revision.  A
        /// loaded XREF is measurable only when all four agree with a stable read of
        /// the file on disk.
        /// </summary>
        public sealed record SourceSnapshotIdentity(
            string? FingerprintGuid,
            string? VersionGuid,
            int NumberOfSaves,
            long UpdatedTicks);

        public static TransformCheck ValidateSimilarity(
            IReadOnlyList<double> xAxis,
            IReadOnlyList<double> yAxis,
            IReadOnlyList<double> zAxis,
            double tolerance = 1e-9)
        {
            if (xAxis.Count != 3 || yAxis.Count != 3 || zAxis.Count != 3)
                return new TransformCheck(false, 0, "basis-vector-size");
            if (tolerance <= 0 || !double.IsFinite(tolerance))
                throw new ArgumentOutOfRangeException(nameof(tolerance));

            var axes = new[] { xAxis, yAxis, zAxis };
            if (axes.SelectMany(axis => axis).Any(value => !double.IsFinite(value)))
                return new TransformCheck(false, 0, "non-finite-transform");

            var lengths = axes.Select(Length).ToArray();
            if (lengths.Any(length => !double.IsFinite(length) || length <= tolerance))
                return new TransformCheck(false, 0, "degenerate-transform");

            var scale = lengths.Average();
            var relativeTolerance = tolerance * Math.Max(1.0, scale);
            if (lengths.Any(length => Math.Abs(length - scale) > relativeTolerance))
                return new TransformCheck(false, 0, "non-uniform-scale");

            for (var left = 0; left < axes.Length; left++)
            for (var right = left + 1; right < axes.Length; right++)
            {
                var normalizedDot = Math.Abs(Dot(axes[left], axes[right])) /
                                    (lengths[left] * lengths[right]);
                if (!double.IsFinite(normalizedDot) || normalizedDot > tolerance)
                    return new TransformCheck(false, 0, "sheared-transform");
            }

            return new TransformCheck(true, scale, null);
        }

        public static string? FreshnessFailure(
            IReadOnlyList<EstimateExternalSource> scanned,
            IReadOnlyDictionary<string, string> currentHashes)
        {
            ArgumentNullException.ThrowIfNull(scanned);
            ArgumentNullException.ThrowIfNull(currentHashes);

            foreach (var group in scanned.GroupBy(source => source.DrawingPath,
                         StringComparer.OrdinalIgnoreCase))
            {
                var identities = group.Select(source => source.DrawingHash)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (identities.Count != 1 || string.IsNullOrWhiteSpace(identities[0]))
                    return $"conflicting-scan-identity:{group.Key}";
                if (!currentHashes.TryGetValue(group.Key, out var current) ||
                    string.IsNullOrWhiteSpace(current))
                    return $"external-source-unavailable:{group.Key}";
                if (!string.Equals(identities[0], current, StringComparison.OrdinalIgnoreCase))
                    return $"external-source-changed:{group.Key}";
            }

            return null;
        }

        /// <summary>
        /// Proves that the geometry cached by AutoCAD is the same saved revision that
        /// was hashed on disk.  The two disk captures bracket the hashing operation;
        /// any missing identity or change during that window fails closed.
        /// </summary>
        public static string? LoadedSnapshotFailure(
            SourceSnapshotIdentity? loaded,
            SourceSnapshotIdentity? diskBeforeHash,
            SourceSnapshotIdentity? diskAfterHash)
        {
            if (!HasCompleteIdentity(loaded)) return "loaded-xref-identity-unavailable";
            if (!HasCompleteIdentity(diskBeforeHash) || !HasCompleteIdentity(diskAfterHash))
                return "disk-xref-identity-unavailable";
            if (!SameSnapshot(diskBeforeHash!, diskAfterHash!))
                return "disk-xref-changed-during-scan";
            if (!SameSnapshot(loaded!, diskAfterHash!))
                return "loaded-xref-stale";
            return null;
        }

        public static bool SameSnapshot(
            SourceSnapshotIdentity left,
            SourceSnapshotIdentity right) =>
            SameGuid(left.FingerprintGuid, right.FingerprintGuid) &&
            SameGuid(left.VersionGuid, right.VersionGuid) &&
            left.NumberOfSaves == right.NumberOfSaves &&
            left.UpdatedTicks == right.UpdatedTicks;

        /// <summary>
        /// A clip affects quantities only when traversal reaches external geometry.
        /// Counting an ordinary clipped symbol remains one; an XREF clipped directly
        /// or through an ordinary ancestor cannot be expanded safely.
        /// </summary>
        public static bool ActiveClipBlocksExternalTraversal(
            bool isExternalReference,
            bool activeOnReference,
            bool activeOnAncestor) =>
            isExternalReference && (activeOnReference || activeOnAncestor);

        /// <summary>
        /// AutoCAD does not display nested references beneath an external reference
        /// inserted as Overlay. A nested reference whose own type is Overlay is
        /// likewise excluded when its immediate drawing is itself an XREF. Skipping
        /// those non-visible branches avoids treating their unloaded state as a host
        /// drawing failure while retaining fail-closed handling for visible attached
        /// branches and every top-level XREF.
        /// </summary>
        public static bool IsExternalReferenceExcludedByOverlay(
            bool isExternalReference,
            bool isOverlayReference,
            bool isInsideExternalReference,
            bool hasExternalOverlayAncestor) =>
            isExternalReference &&
            (hasExternalOverlayAncestor ||
             (isInsideExternalReference && isOverlayReference));

        private static bool HasCompleteIdentity(SourceSnapshotIdentity? identity) =>
            identity != null &&
            IsGuid(identity.FingerprintGuid) &&
            IsGuid(identity.VersionGuid) &&
            identity.NumberOfSaves >= 0 &&
            identity.UpdatedTicks > 0;

        private static bool SameGuid(string? left, string? right) =>
            Guid.TryParse(left, out var leftGuid) &&
            Guid.TryParse(right, out var rightGuid) &&
            leftGuid == rightGuid;

        private static bool IsGuid(string? value) => Guid.TryParse(value, out _);

        private static double Length(IReadOnlyList<double> vector) =>
            Math.Sqrt(Dot(vector, vector));

        private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
            left[0] * right[0] + left[1] * right[1] + left[2] * right[2];
    }
}
