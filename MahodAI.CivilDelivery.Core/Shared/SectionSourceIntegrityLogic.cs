using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Autodesk-free validation of the immutable inputs behind a section PLAN.
    /// Sections are authored in metres; a drawing revision or external-file change
    /// invalidates the plan instead of allowing PREVIEW/APPLY/VERIFY to reuse it.
    /// </summary>
    public static class SectionSourceIntegrityLogic
    {
        public const int MetresUnitCode = 6;

        public enum FailureKind
        {
            UnitNotMetres,
            RevisionUnavailable,
            RevisionChanged,
            SourcePathUnverifiable,
            SourceEvidenceConflict,
            SourceMissing,
            SourceHashUnverifiable,
            SourceChanged,
            LiveSourceUnavailable,
            LiveSourceChanged,
        }

        public sealed record ExternalSourceSnapshot(
            string? Path,
            string? Sha256,
            string? LiveDatabaseRevision = null,
            bool RequiresLiveDatabase = false);

        public sealed record ExternalSourceProbe(
            bool Exists,
            string? Sha256,
            string? LiveDatabaseRevision = null,
            string? Error = null);

        /// <summary>
        /// Stable Civil source identity used to bind a SampleLineGroup source to the
        /// Section child generated from that exact source.  Display names alone are
        /// insufficient because separate Civil source collections may reuse them.
        /// </summary>
        public sealed record SourceIdentity(string Name, string Kind, string Handle);

        public sealed record Failure(FailureKind Kind, string Message, string? Path = null);

        /// <summary>
        /// Proves that two name collections represent the same exact set.  The
        /// cardinality and uniqueness checks are deliberate: an extra or duplicate
        /// live Civil source must not be hidden by a membership-only comparison.
        /// </summary>
        public static bool ExactNameSet(
            IReadOnlyList<string> expected,
            IReadOnlyList<string> actual) =>
            expected.Count == actual.Count &&
            expected.Count == expected.Distinct(StringComparer.OrdinalIgnoreCase).Count() &&
            actual.Count == actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() &&
            expected.All(name => actual.Count(candidate => string.Equals(
                candidate, name, StringComparison.OrdinalIgnoreCase)) == 1);

        /// <summary>
        /// Exact typed identity/cardinality comparison.  It intentionally rejects
        /// duplicate rows on either side, even when ordinary set equality would hide
        /// the duplicate, so one stale/extra Section child can never verify green.
        /// </summary>
        public static bool ExactSourceIdentitySet(
            IReadOnlyList<SourceIdentity> expected,
            IReadOnlyList<SourceIdentity> actual)
        {
            if (expected.Count != actual.Count ||
                expected.Any(Invalid) || actual.Any(Invalid))
                return false;

            var expectedKeys = expected.Select(Key).ToList();
            var actualKeys = actual.Select(Key).ToList();
            return expectedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                       expectedKeys.Count &&
                   actualKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                       actualKeys.Count &&
                   expectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                       .SetEquals(actualKeys);

            static bool Invalid(SourceIdentity identity) =>
                string.IsNullOrWhiteSpace(identity.Name) ||
                string.IsNullOrWhiteSpace(identity.Kind) ||
                string.IsNullOrWhiteSpace(identity.Handle);

            static string Key(SourceIdentity identity) =>
                identity.Name.Trim() + "\u001f" +
                identity.Kind.Trim().ToLowerInvariant() + "\u001f" +
                identity.Handle.Trim().ToUpperInvariant();
        }

        public static Failure? ValidateMetres(int? plannedUnitCode, int currentUnitCode)
        {
            if (plannedUnitCode != MetresUnitCode)
                return new Failure(
                    FailureKind.UnitNotMetres,
                    $"PLAN unit evidence is {(plannedUnitCode?.ToString() ?? "missing")}; sections require INSUNITS=6 (metres)." );
            if (currentUnitCode != MetresUnitCode)
                return new Failure(
                    FailureKind.UnitNotMetres,
                    $"Current INSUNITS={currentUnitCode}; sections require INSUNITS=6 (metres)." );
            return null;
        }

        public static Failure? ValidateRevision(string? expected, string? actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
                return new Failure(
                    FailureKind.RevisionUnavailable,
                    "A database revision could not be proven; run PLAN again.");
            return string.Equals(expected, actual, StringComparison.Ordinal)
                ? null
                : new Failure(
                    FailureKind.RevisionChanged,
                    "The live drawing database changed after the trusted stage.");
        }

        public static IReadOnlyList<Failure> ValidateExternalSources(
            IEnumerable<ExternalSourceSnapshot>? snapshots,
            Func<string, ExternalSourceProbe> probe)
        {
            ArgumentNullException.ThrowIfNull(probe);
            var failures = new List<Failure>();
            var normalized = new List<(string Path, ExternalSourceSnapshot Snapshot)>();

            foreach (var snapshot in snapshots ?? Array.Empty<ExternalSourceSnapshot>())
            {
                if (string.IsNullOrWhiteSpace(snapshot.Path) || !Path.IsPathFullyQualified(snapshot.Path))
                {
                    failures.Add(new Failure(
                        FailureKind.SourcePathUnverifiable,
                        "An external source does not have a resolved absolute path.",
                        snapshot.Path));
                    continue;
                }

                string full;
                try { full = Path.GetFullPath(snapshot.Path); }
                catch (Exception ex)
                {
                    failures.Add(new Failure(
                        FailureKind.SourcePathUnverifiable,
                        "External source path cannot be normalized: " + ex.Message,
                        snapshot.Path));
                    continue;
                }
                normalized.Add((full, snapshot));
            }

            foreach (var group in normalized.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                var expectedHashes = group.Select(x => NormalizeHash(x.Snapshot.Sha256))
                    .Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var missingHash = group.Any(x => NormalizeHash(x.Snapshot.Sha256) == null);
                var requiresLive = group.Any(x => x.Snapshot.RequiresLiveDatabase);
                var liveRevisions = group.Select(x => x.Snapshot.LiveDatabaseRevision)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).ToList();

                if (missingHash || expectedHashes.Count != 1 ||
                    (requiresLive && liveRevisions.Count != 1))
                {
                    failures.Add(new Failure(
                        FailureKind.SourceEvidenceConflict,
                        "External-source evidence is missing or contradictory.",
                        group.Key));
                    continue;
                }

                ExternalSourceProbe actual;
                try { actual = probe(group.Key); }
                catch (Exception ex)
                {
                    failures.Add(new Failure(
                        FailureKind.SourceHashUnverifiable,
                        "External source could not be verified: " + ex.Message,
                        group.Key));
                    continue;
                }

                if (!actual.Exists)
                {
                    failures.Add(new Failure(
                        FailureKind.SourceMissing,
                        actual.Error ?? "External source is missing.",
                        group.Key));
                    continue;
                }

                var actualHash = NormalizeHash(actual.Sha256);
                if (actualHash == null)
                {
                    failures.Add(new Failure(
                        FailureKind.SourceHashUnverifiable,
                        actual.Error ?? "External source hash is unavailable.",
                        group.Key));
                    continue;
                }
                if (!string.Equals(expectedHashes[0], actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new Failure(
                        FailureKind.SourceChanged,
                        "External source bytes changed after PLAN.",
                        group.Key));
                }

                if (!requiresLive) continue;
                if (string.IsNullOrWhiteSpace(actual.LiveDatabaseRevision))
                {
                    failures.Add(new Failure(
                        FailureKind.LiveSourceUnavailable,
                        "PLAN read this source from an open live drawing, but that live database is no longer available.",
                        group.Key));
                }
                else if (!string.Equals(liveRevisions[0], actual.LiveDatabaseRevision,
                             StringComparison.Ordinal))
                {
                    failures.Add(new Failure(
                        FailureKind.LiveSourceChanged,
                        "The open external drawing changed after PLAN.",
                        group.Key));
                }
            }

            return failures;
        }

        private static string? NormalizeHash(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
                value.Any(c => !Uri.IsHexDigit(c)))
                return null;
            return value.ToLowerInvariant();
        }
    }
}
