using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Which nearby text names a CL section (1.4.1 D2, project 984; agreed with Codex 06.10 12:56). The 984 CL drawing
    /// writes the section name ("100") on HW-ALGN-SEC-NAME next to each tick and the station elevation ("6.53") on
    /// HW-ALGN-SEC-ELEV even closer; nearest-with-a-digit picked the elevation. Order of evidence:
    /// 1. explicit <c>numbering.label_layer_patterns</c>: binding — only labels on those layers, never a fallback;
    /// 2. otherwise a label on the CL line's own effective layer is preferred within the same source;
    /// 3. otherwise the previous rule (nearest label that contains a digit).
    /// Two different texts at the same nearest distance are not decided by enumeration order: no number is chosen and
    /// the caller reports it. Geometry, station and the nearby-label evidence list are not changed here.
    /// </summary>
    internal static class ClLabelSelection
    {
        public const string AmbiguousCode = "SEC-CL-LABEL-AMBIGUOUS";
        public const double TieToleranceM = 1e-6;

        internal readonly record struct Label(string Text, double Distance, string Layer, string? InstanceKey = null);

        internal readonly record struct Choice(string? Number, bool Ambiguous, IReadOnlyList<string> TiedTexts);

        /// <summary>The reader's handle path of a reference: parent path + "/" + the insertion handle.</summary>
        internal static string HandlePath(string? parentHandlePath, string handle) =>
            string.IsNullOrWhiteSpace(parentHandlePath) ? handle : parentHandlePath + "/" + handle;

        /// <summary>
        /// The instance key of a source entered through an XREF reference (Codex 13:21): the full handle path, so an
        /// XREF inside an ordinary block inserted twice (A0/C5 and B0/C5) is two instances. Only entering an XREF sets
        /// it; ordinary blocks below (tick blocks) keep the key, so text and line in different tick blocks of one XREF
        /// instance stay eligible for each other.
        /// </summary>
        internal static string? InstanceKeyBelow(string? parentInstanceKey, bool enteringXref, string currentHandlePath) =>
            enteringXref ? currentHandlePath : parentInstanceKey;

        internal static Choice Choose(IEnumerable<Label> nearby, string recordLayer, IReadOnlyList<string>? explicitPatterns,
            string? recordInstanceKey = null)
        {
            // Codex 13:14: a label belongs to the same XREF instance (insertion-handle chain), not just the same file.
            var pool = nearby.Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Text.Any(char.IsDigit) &&
                                         string.Equals(l.InstanceKey, recordInstanceKey, StringComparison.Ordinal)).ToList();
            if (explicitPatterns is { Count: > 0 })
                pool = pool.Where(l => ClInstructionReader.MatchesLayer(l.Layer, explicitPatterns)).ToList();
            else if (pool.Any(l => string.Equals(l.Layer, recordLayer, StringComparison.OrdinalIgnoreCase)))
                pool = pool.Where(l => string.Equals(l.Layer, recordLayer, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.Count == 0) return new Choice(null, false, Array.Empty<string>());

            var nearest = pool.Min(l => l.Distance);
            var tied = pool.Where(l => l.Distance - nearest <= TieToleranceM)
                .Select(l => l.Text).Distinct(StringComparer.Ordinal).ToList();
            return tied.Count == 1
                ? new Choice(tied[0], false, tied)
                : new Choice(null, true, tied);
        }
    }
}
