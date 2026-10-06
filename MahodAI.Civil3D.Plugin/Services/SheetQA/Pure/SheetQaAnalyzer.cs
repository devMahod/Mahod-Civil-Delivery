using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>Which checks to run. Any combination; all four by default.</summary>
    [Flags]
    public enum SheetQaChecks
    {
        None = 0,
        TextOverlap = 1,
        RotatedText = 2,
        Declutter = 4,
        Legend = 8,
        All = TextOverlap | RotatedText | Declutter | Legend
    }

    public sealed class SheetQaOptions
    {
        public SheetQaChecks Checks { get; set; } = SheetQaChecks.All;

        public double MinOverlapRatio { get; set; } = TextOverlapDetector.DefaultMinOverlapRatio;

        /// <summary>Per-category ceiling on findings. Truncation is always reported, never silent.</summary>
        public int MaxFindingsPerCheck { get; set; } = 60;

        public int MinDeclutterTextCount { get; set; } = DeclutterAnalyzer.DefaultMinTextCount;

        /// <summary>Xrefs the legend is responsible for; null compares every xref.</summary>
        public ISet<string>? UtilityXrefs { get; set; }
    }

    /// <summary>What a single layout's scan produced.</summary>
    public sealed class SheetQaResult
    {
        public string Layout { get; set; } = string.Empty;

        public List<VisualFinding> Findings { get; set; } = new();

        public List<DeclutterCandidate> DeclutterCandidates { get; set; } = new();

        public LegendDiffResult? Legend { get; set; }

        public SheetQaStats Stats { get; set; } = new();

        /// <summary>Known simplifications of this run, surfaced so nobody reads it as gospel.</summary>
        public List<string> Approximations { get; set; } = new();

        /// <summary>Per-category counts BEFORE the cap, so a truncated run still reports the truth.</summary>
        public Dictionary<string, int> TotalsByCategory { get; set; } = new();

        /// <summary>Categories where the cap actually bit.</summary>
        public List<string> TruncatedCategories { get; set; } = new();
    }

    public sealed class SheetQaStats
    {
        public int VisibleTexts { get; set; }
        public int PaperTexts { get; set; }
        public int ModelTexts { get; set; }
        public int VisibleCurves { get; set; }
        public int XrefsWalked { get; set; }
        public int LegendRows { get; set; }
        public int HiddenLayerSkipped { get; set; }
        public int OutsideViewportSkipped { get; set; }
        public int UnreadableEntities { get; set; }
    }

    /// <summary>
    /// Runs the four sheet checks over one layout's already-extracted content and turns
    /// them into findings with stable ids.
    ///
    /// Everything here is pure arithmetic over plain records: no AutoCAD types, so the whole
    /// detection surface is unit-testable without a Civil 3D host, and the extraction stage
    /// stays the only place that has to cope with the drawing database.
    /// </summary>
    public static class SheetQaAnalyzer
    {
        public static SheetQaResult Analyze(
            string layoutName,
            IReadOnlyList<SheetTextRecord> texts,
            IReadOnlyList<SheetCurveRecord> curves,
            IReadOnlyList<LegendRow> legendRows,
            SheetQaOptions? options = null)
        {
            options ??= new SheetQaOptions();
            texts ??= Array.Empty<SheetTextRecord>();
            curves ??= Array.Empty<SheetCurveRecord>();
            legendRows ??= Array.Empty<LegendRow>();

            var ids = new FindingIdGenerator(layoutName);
            var result = new SheetQaResult
            {
                Layout = layoutName,
                Stats =
                {
                    VisibleTexts = texts.Count,
                    PaperTexts = texts.Count(t => t.Space == SheetSpace.Paper),
                    ModelTexts = texts.Count(t => t.Space == SheetSpace.Model),
                    VisibleCurves = curves.Count,
                    LegendRows = legendRows.Count
                }
            };

            if (options.Checks.HasFlag(SheetQaChecks.TextOverlap))
            {
                AddOverlapFindings(result, ids, texts, options);
            }

            if (options.Checks.HasFlag(SheetQaChecks.RotatedText))
            {
                AddRotationFindings(result, ids, texts, options);
            }

            if (options.Checks.HasFlag(SheetQaChecks.Declutter))
            {
                AddDeclutterFindings(result, ids, texts, options);
            }

            if (options.Checks.HasFlag(SheetQaChecks.Legend))
            {
                AddLegendFindings(result, ids, curves, legendRows, options);
            }

            return result;
        }

        private static void AddOverlapFindings(
            SheetQaResult result, FindingIdGenerator ids,
            IReadOnlyList<SheetTextRecord> texts, SheetQaOptions options)
        {
            var pairs = TextOverlapDetector.Detect(texts, options.MinOverlapRatio);
            Record(result, VisualFindingCategories.TextOverlap, pairs.Count, options.MaxFindingsPerCheck);

            foreach (var pair in pairs.Take(options.MaxFindingsPerCheck))
            {
                var width = pair.MaxX - pair.MinX;
                var height = pair.MaxY - pair.MinY;

                result.Findings.Add(new VisualFinding
                {
                    Id = ids.Next(),
                    Category = VisualFindingCategories.TextOverlap,
                    Severity = TextOverlapDetector.SeverityFor(pair.Ratio),
                    Layout = result.Layout,
                    Space = pair.First.Space,
                    CenterX = (pair.MinX + pair.MaxX) / 2,
                    CenterY = (pair.MinY + pair.MaxY) / 2,
                    Radius = Math.Max(Math.Max(width, height) * 0.65, SmallestUsefulRadius(pair.First)),
                    HasLocation = true,
                    Title = "Text on text",
                    Detail = $"\"{Shorten(pair.First.DisplayText)}\" overlaps \"{Shorten(pair.Second.DisplayText)}\" " +
                             $"({pair.Ratio * 100:F0}% of the smaller label)",
                    Layer = pair.First.Layer,
                    SourceXref = pair.First.SourceXref,
                    Handles = CollectHandles(pair.First, pair.Second)
                });
            }
        }

        private static void AddRotationFindings(
            SheetQaResult result, FindingIdGenerator ids,
            IReadOnlyList<SheetTextRecord> texts, SheetQaOptions options)
        {
            var unreadable = RotationClassifier.FindUnreadable(texts);
            Record(result, VisualFindingCategories.RotatedText, unreadable.Count, options.MaxFindingsPerCheck);

            foreach (var text in unreadable.Take(options.MaxFindingsPerCheck))
            {
                result.Findings.Add(new VisualFinding
                {
                    Id = ids.Next(),
                    Category = VisualFindingCategories.RotatedText,
                    Severity = RotationClassifier.SeverityFor(text.RotationDeg),
                    Layout = result.Layout,
                    Space = text.Space,
                    CenterX = (text.MinX + text.MaxX) / 2,
                    CenterY = (text.MinY + text.MaxY) / 2,
                    Radius = Math.Max(Math.Max(text.Width, text.BoxHeight) * 0.65, SmallestUsefulRadius(text)),
                    HasLocation = true,
                    Title = "Upside-down text",
                    Detail = $"\"{Shorten(text.DisplayText)}\" is rotated {text.RotationDeg:F0}° on the sheet",
                    Layer = text.Layer,
                    SourceXref = text.SourceXref,
                    Handles = CollectHandles(text)
                });
            }
        }

        private static void AddDeclutterFindings(
            SheetQaResult result, FindingIdGenerator ids,
            IReadOnlyList<SheetTextRecord> texts, SheetQaOptions options)
        {
            var candidates = DeclutterAnalyzer.Analyze(texts, options.MinDeclutterTextCount);
            result.DeclutterCandidates = candidates.Take(options.MaxFindingsPerCheck).ToList();
            Record(result, VisualFindingCategories.DeclutterCandidate, candidates.Count, options.MaxFindingsPerCheck);

            foreach (var candidate in result.DeclutterCandidates)
            {
                result.Findings.Add(new VisualFinding
                {
                    Id = ids.Next(),
                    Category = VisualFindingCategories.DeclutterCandidate,
                    Severity = DeclutterAnalyzer.SeverityFor(candidate),
                    Layout = result.Layout,
                    Space = SheetSpace.Model,
                    HasLocation = false,
                    Title = "Declutter candidate",
                    Detail = $"Layer '{candidate.Layer}' contributes {candidate.TextCount} labels " +
                             $"({candidate.NumericShare * 100:F0}% numeric)",
                    Layer = candidate.Layer,
                    SourceXref = candidate.SourceXref
                });
            }
        }

        private static void AddLegendFindings(
            SheetQaResult result, FindingIdGenerator ids,
            IReadOnlyList<SheetCurveRecord> curves, IReadOnlyList<LegendRow> legendRows,
            SheetQaOptions options)
        {
            var diff = LegendMatcher.Diff(legendRows, curves, options.UtilityXrefs);
            result.Legend = diff;

            Record(result, VisualFindingCategories.LegendMissingRow, diff.MissingFromLegend.Count, options.MaxFindingsPerCheck);
            foreach (var group in diff.MissingFromLegend.Take(options.MaxFindingsPerCheck))
            {
                var width = group.MaxX - group.MinX;
                var height = group.MaxY - group.MinY;

                result.Findings.Add(new VisualFinding
                {
                    Id = ids.Next(),
                    Category = VisualFindingCategories.LegendMissingRow,
                    Severity = group.Count >= 20 ? VisualFindingSeverities.Medium : VisualFindingSeverities.Low,
                    Layout = result.Layout,
                    Space = SheetSpace.Model,
                    CenterX = (group.MinX + group.MaxX) / 2,
                    CenterY = (group.MinY + group.MaxY) / 2,
                    Radius = Math.Max(Math.Min(Math.Max(width, height) * 0.4, 60.0), 5.0),
                    HasLocation = true,
                    Title = "Not identified in legend",
                    Detail = $"{group.Count} lines drawn as colour {group.ColorIndex} / {group.Linetype} " +
                             $"have no legend row (layer '{group.Layer}')",
                    Layer = group.Layer,
                    SourceXref = group.SourceXref
                });
            }

            Record(result, VisualFindingCategories.LegendOrphanRow, diff.OrphanLegendRows.Count, options.MaxFindingsPerCheck);
            foreach (var row in diff.OrphanLegendRows.Take(options.MaxFindingsPerCheck))
            {
                result.Findings.Add(new VisualFinding
                {
                    Id = ids.Next(),
                    Category = VisualFindingCategories.LegendOrphanRow,
                    Severity = VisualFindingSeverities.Low,
                    Layout = result.Layout,
                    Space = SheetSpace.Paper,
                    HasLocation = false,
                    Title = "Orphan legend row",
                    Detail = string.IsNullOrWhiteSpace(row.Label)
                        ? $"Legend row colour {row.ColorIndex} / {row.Linetype} is not drawn on this sheet"
                        : $"Legend row \"{Shorten(row.Label)}\" (colour {row.ColorIndex} / {row.Linetype}) is not drawn on this sheet"
                });
            }
        }

        /// <summary>
        /// Records the true count for a category and notes when the cap truncated it, so a
        /// capped run can never be mistaken for a clean one.
        /// </summary>
        private static void Record(SheetQaResult result, string category, int total, int cap)
        {
            result.TotalsByCategory[category] = total;
            if (total > cap && !result.TruncatedCategories.Contains(category))
            {
                result.TruncatedCategories.Add(category);
            }
        }

        /// <summary>
        /// A circle must stay visible at plot scale even around a tiny label, so it never
        /// shrinks below a couple of text heights.
        /// </summary>
        private static double SmallestUsefulRadius(SheetTextRecord text) =>
            text.Height > 0 ? text.Height * 2 : 1.0;

        private static List<string> CollectHandles(params SheetTextRecord[] texts) =>
            texts.Where(t => !string.IsNullOrEmpty(t.Handle))
                 .Select(t => t.Handle!)
                 .Distinct()
                 .ToList();

        private static string Shorten(string? value, int max = 40)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var clean = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return clean.Length <= max ? clean : clean[..max] + "…";
        }
    }
}
