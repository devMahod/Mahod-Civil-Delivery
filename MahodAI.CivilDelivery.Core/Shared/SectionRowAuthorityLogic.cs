using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Pure ROW provenance policy.  A familiar layer name is evidence that geometry
    /// may be ROW, but never evidence that its source revision is authoritative.
    /// Only one approved source instance may contribute ROW boundaries to a section.
    /// </summary>
    public static class SectionRowAuthorityLogic
    {
        public enum SelectionState
        {
            NoCandidates,
            Suppressed,
            Authoritative,
            Ambiguous,
        }

        public sealed record Source(
            string? SourceDrawingPath,
            string? SourceDrawingSha256,
            string? XrefChain);

        public sealed record Authority(
            string? SourceDrawingSha256,
            string? SourcePathPattern,
            string? XrefPattern,
            string? ApprovedBy,
            DateTime? ApprovedAtUtc);

        public sealed record Selection(
            SelectionState State,
            string? AuthoritativeSourceKey,
            IReadOnlyList<string> CandidateSourceKeys,
            IReadOnlyList<string> MatchingSourceKeys,
            string Reason)
        {
            public bool Accepts(Source source) =>
                State == SelectionState.Authoritative &&
                string.Equals(
                    AuthoritativeSourceKey,
                    SourceKey(source),
                    StringComparison.Ordinal);
        }

        public static Selection Select(
            IEnumerable<Source>? candidates,
            IEnumerable<Authority>? authorities)
        {
            var sources = (candidates ?? Enumerable.Empty<Source>())
                .Where(source => source != null)
                .ToList();
            var sourceKeys = sources.Select(SourceKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (sources.Count == 0)
                return new Selection(
                    SelectionState.NoCandidates, null, sourceKeys,
                    Array.Empty<string>(), "no-row-candidates");

            var approved = (authorities ?? Enumerable.Empty<Authority>())
                .Where(IsCompleteAuthority)
                .ToList();
            if (approved.Count == 0)
                return new Selection(
                    SelectionState.Suppressed, null, sourceKeys,
                    Array.Empty<string>(), "no-complete-row-authority");

            var matching = sources
                .Where(source => approved.Any(authority => Matches(authority, source)))
                .Select(SourceKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (matching.Count == 0)
                return new Selection(
                    SelectionState.Suppressed, null, sourceKeys, matching,
                    "row-candidates-do-not-match-approved-source");
            if (matching.Count != 1)
                return new Selection(
                    SelectionState.Ambiguous, null, sourceKeys, matching,
                    "multiple-approved-row-source-instances");

            return new Selection(
                SelectionState.Authoritative, matching[0], sourceKeys, matching,
                "one-approved-row-source-instance");
        }

        public static bool IsCompleteAuthority(Authority authority) =>
            authority != null &&
            IsSha256(authority.SourceDrawingSha256) &&
            !string.IsNullOrWhiteSpace(authority.ApprovedBy) &&
            authority.ApprovedAtUtc is { } approvedAt && approvedAt != default;

        public static string SourceKey(Source source)
        {
            var hash = Normalize(source.SourceDrawingSha256);
            var path = NormalizePath(source.SourceDrawingPath);
            var chain = Normalize(source.XrefChain);
            return $"{hash}|{path}|{chain}";
        }

        private static bool Matches(Authority authority, Source source)
        {
            if (!IsCompleteAuthority(authority) ||
                !string.Equals(
                    authority.SourceDrawingSha256,
                    source.SourceDrawingSha256,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(authority.SourcePathPattern) &&
                !SectionProjectionLogic.Wildcard(
                    NormalizePath(source.SourceDrawingPath),
                    NormalizePath(authority.SourcePathPattern)))
                return false;

            if (!string.IsNullOrWhiteSpace(authority.XrefPattern) &&
                !MatchesXref(source.XrefChain, authority.XrefPattern))
                return false;

            return true;
        }

        private static bool MatchesXref(string? chain, string pattern)
        {
            if (SectionProjectionLogic.Wildcard(chain ?? string.Empty, pattern))
                return true;
            if (string.IsNullOrWhiteSpace(chain)) return false;
            return chain.Split('>')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Any(part => SectionProjectionLogic.Wildcard(part, pattern));
        }

        private static string NormalizePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = value.Trim().Replace('\\', '/');
            try
            {
                if (!normalized.Contains('*') && !normalized.Contains('?'))
                    normalized = Path.GetFullPath(normalized).Replace('\\', '/');
            }
            catch
            {
                // Keep the readable value.  A malformed pattern simply will not match.
            }
            return normalized.ToUpperInvariant();
        }

        private static string Normalize(string? value) =>
            value?.Trim().ToUpperInvariant() ?? string.Empty;

        private static bool IsSha256(string? value) =>
            value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }
}
