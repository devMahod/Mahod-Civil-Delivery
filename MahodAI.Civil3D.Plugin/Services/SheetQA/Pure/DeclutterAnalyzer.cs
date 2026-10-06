using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>
    /// Ranks layers by how much annotation they contribute to a sheet, so the heaviest
    /// candidates for being switched off can be offered to the engineer.
    ///
    /// This stage only measures; it never decides. Whether a layer is essential is a
    /// professional judgement (contour elevations are noise on a utility-coordination
    /// sheet and the whole point on a grading sheet), so the agent classifies
    /// main-vs-secondary from these counts plus the layer names. The numeric share is the
    /// useful signal: a layer that is almost entirely numbers is an elevation/chainage
    /// annotation layer, which is what Ari asked to be able to unload.
    /// </summary>
    public static class DeclutterAnalyzer
    {
        /// <summary>Below this many labels a layer cannot meaningfully declutter the sheet.</summary>
        public const int DefaultMinTextCount = 20;

        public static List<DeclutterCandidate> Analyze(
            IReadOnlyList<SheetTextRecord> texts,
            int minTextCount = DefaultMinTextCount)
        {
            var candidates = new List<DeclutterCandidate>();
            if (texts == null || texts.Count == 0) return candidates;

            foreach (var group in texts.GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase))
            {
                var items = group.ToList();
                if (items.Count < minTextCount) continue;

                int numeric = items.Count(t => IsNumericLabel(t.RawText));

                candidates.Add(new DeclutterCandidate
                {
                    Layer = group.Key,
                    SourceXref = items[0].SourceXref,
                    TextCount = items.Count,
                    NumericTextCount = numeric,
                    NumericShare = items.Count > 0 ? (double)numeric / items.Count : 0
                });
            }

            return candidates
                .OrderByDescending(c => c.TextCount)
                .ToList();
        }

        /// <summary>
        /// True for labels that are just a number — elevations, chainages, diameters. Uses
        /// invariant parsing so a decimal point is a decimal point regardless of locale.
        /// </summary>
        public static bool IsNumericLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var trimmed = raw.Trim().TrimStart('+');
            if (trimmed.Length == 0) return false;

            return double.TryParse(
                trimmed,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _);
        }

        /// <summary>
        /// A dense, almost purely numeric layer is the strongest unload candidate; a dense
        /// but textual layer probably carries information the sheet needs.
        /// </summary>
        public static string SeverityFor(DeclutterCandidate candidate)
        {
            if (candidate.NumericShare >= 0.8 && candidate.TextCount >= 100) return VisualFindingSeverities.High;
            if (candidate.NumericShare >= 0.5 || candidate.TextCount >= 100) return VisualFindingSeverities.Medium;
            return VisualFindingSeverities.Low;
        }
    }
}
