using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>A sample line drawn inside the legend block, with its vertical position.</summary>
    public sealed class LegendSampleLine
    {
        public int ColorIndex { get; set; } = -1;
        public string Linetype { get; set; } = string.Empty;
        public double MidY { get; set; }
    }

    /// <summary>A caption inside the legend block, with its vertical position.</summary>
    public sealed class LegendLabelText
    {
        public string RawLabel { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public double MidY { get; set; }
    }

    /// <summary>One style that is drawn on the plan, grouped for reporting.</summary>
    public sealed class PlanStyleGroup
    {
        public string StyleKey { get; set; } = string.Empty;
        public int ColorIndex { get; set; } = -1;
        public string Linetype { get; set; } = string.Empty;
        public int Count { get; set; }
        public string? SourceXref { get; set; }
        public string Layer { get; set; } = string.Empty;

        /// <summary>Bounding box of one representative curve, so the finding can be circled.</summary>
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }

    /// <summary>Both directions of the legend comparison.</summary>
    public sealed class LegendDiffResult
    {
        /// <summary>Styles drawn on the plan with no matching legend row.</summary>
        public List<PlanStyleGroup> MissingFromLegend { get; set; } = new();

        /// <summary>Legend rows whose style is never drawn on this sheet.</summary>
        public List<LegendRow> OrphanLegendRows { get; set; } = new();

        public int PlanStyleCount { get; set; }
        public int LegendStyleCount { get; set; }
        public int MatchedStyleCount { get; set; }
    }

    /// <summary>
    /// Compares the legend printed on a sheet against what the sheet actually draws.
    ///
    /// The legend is the sheet's own contract — Ari's rule is that the check uses the legend
    /// on that same layout, not a project-wide reference — so the comparison key is the pair
    /// a drafter actually sees: effective colour plus linetype. Rows are matched to captions
    /// by vertical proximity because legends are laid out as rows, and the caption nearest a
    /// sample line's centre-line is its label.
    ///
    /// Scoping matters: a coordination sheet also carries survey and topographic linework
    /// that the legend legitimately never lists. Passing the utility xrefs in
    /// <c>scopeXrefs</c> keeps the "missing from legend" direction to the networks the
    /// legend is actually responsible for; without it the result is dominated by contour
    /// and parcel lines (measured on RD383: 108 styles drawn, only 13 in the legend).
    /// </summary>
    public static class LegendMatcher
    {
        /// <summary>A caption further than this (in legend units) is a different row.</summary>
        public const double DefaultMaxLabelDistance = 5.0;

        /// <summary>Builds the comparison key. Linetype is normalised; colour is the effective ACI index.</summary>
        public static string BuildStyleKey(int colorIndex, string? linetype) =>
            $"{colorIndex}/{NormalizeLinetype(linetype)}";

        /// <summary>
        /// Strips any xref prefix ("survey|BK" → "bk") and lower-cases, so the same linetype
        /// referenced through different xrefs compares equal.
        /// </summary>
        public static string NormalizeLinetype(string? linetype)
        {
            if (string.IsNullOrWhiteSpace(linetype)) return "?";
            var name = linetype.Trim();
            var bar = name.LastIndexOf('|');
            if (bar >= 0 && bar < name.Length - 1) name = name[(bar + 1)..];
            return name.ToLowerInvariant();
        }

        /// <summary>Pairs each legend sample line with the caption closest to it vertically.</summary>
        public static List<LegendRow> PairRows(
            IReadOnlyList<LegendSampleLine> lines,
            IReadOnlyList<LegendLabelText> labels,
            double maxLabelDistance = DefaultMaxLabelDistance)
        {
            var rows = new List<LegendRow>();
            if (lines == null || lines.Count == 0) return rows;

            foreach (var line in lines)
            {
                LegendLabelText? best = null;
                double bestDistance = double.MaxValue;

                if (labels != null)
                {
                    foreach (var label in labels)
                    {
                        var distance = Math.Abs(label.MidY - line.MidY);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = label;
                        }
                    }
                }

                var matched = best != null && bestDistance <= maxLabelDistance;

                rows.Add(new LegendRow
                {
                    ColorIndex = line.ColorIndex,
                    Linetype = line.Linetype,
                    RawLabel = matched ? best!.RawLabel : string.Empty,
                    Label = matched ? best!.Label : string.Empty,
                    LabelDistance = matched ? bestDistance : double.NaN
                });
            }

            return rows;
        }

        /// <summary>
        /// Compares both directions. <paramref name="scopeXrefs"/> limits the
        /// "drawn but not in the legend" direction to the given xrefs (case-insensitive,
        /// matched as a prefix of the source xref name); null compares everything.
        /// </summary>
        public static LegendDiffResult Diff(
            IReadOnlyList<LegendRow> legendRows,
            IReadOnlyList<SheetCurveRecord> planCurves,
            ISet<string>? scopeXrefs = null)
        {
            var result = new LegendDiffResult();

            var legendKeys = new HashSet<string>(
                (legendRows ?? Array.Empty<LegendRow>()).Select(r => r.StyleKey),
                StringComparer.OrdinalIgnoreCase);
            result.LegendStyleCount = legendKeys.Count;

            var groups = new Dictionary<string, PlanStyleGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var curve in planCurves ?? Array.Empty<SheetCurveRecord>())
            {
                if (!InScope(curve.SourceXref, scopeXrefs)) continue;

                var key = curve.StyleKey;
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new PlanStyleGroup
                    {
                        StyleKey = key,
                        ColorIndex = curve.ColorIndex,
                        Linetype = curve.Linetype,
                        SourceXref = curve.SourceXref,
                        Layer = curve.Layer,
                        MinX = curve.MinX,
                        MinY = curve.MinY,
                        MaxX = curve.MaxX,
                        MaxY = curve.MaxY
                    };
                    groups[key] = group;
                }
                group.Count++;
            }

            result.PlanStyleCount = groups.Count;
            result.MatchedStyleCount = groups.Keys.Count(legendKeys.Contains);

            result.MissingFromLegend = groups.Values
                .Where(g => !legendKeys.Contains(g.StyleKey))
                .OrderByDescending(g => g.Count)
                .ToList();

            var drawnKeys = new HashSet<string>(groups.Keys, StringComparer.OrdinalIgnoreCase);
            result.OrphanLegendRows = (legendRows ?? Array.Empty<LegendRow>())
                .Where(r => !drawnKeys.Contains(r.StyleKey))
                .ToList();

            return result;
        }

        private static bool InScope(string? sourceXref, ISet<string>? scopeXrefs)
        {
            if (scopeXrefs == null || scopeXrefs.Count == 0) return true;
            if (string.IsNullOrEmpty(sourceXref)) return false;

            foreach (var scope in scopeXrefs)
            {
                if (string.IsNullOrWhiteSpace(scope)) continue;
                if (sourceXref.StartsWith(scope, StringComparison.OrdinalIgnoreCase) ||
                    sourceXref.Contains(scope, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
