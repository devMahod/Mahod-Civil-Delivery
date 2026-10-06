using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Canonical semantic inventory for the deterministic section presentation that
    /// is neither a projected utility nor surface-grounded furniture. APPLY binds
    /// every row to one handle; VERIFY rebuilds these rows solely from PLAN.
    /// </summary>
    internal static class SectionCorePresentationContract
    {
        internal const string AxisLine = "axis-line";
        internal const string AxisLabel = "axis-label";
        internal const string Title = "title";
        internal const string RowLine = "row-line";
        internal const string RowLabel = "row-label";
        internal const string DimensionTopTick = "dimension-top-tick";
        internal const string DimensionBottomTick = "dimension-bottom-tick";
        internal const string WidthLabel = "width-label";
        internal const string StripLabel = "strip-label";
        // Review of 1.3.9 (30/09): closed dimension chain (SEC-B4) and the drawn
        // legend (SEC-m3/SEC-M3). All derived from PLAN only, like every row above.
        internal const string GapWidthLabel = "gap-width-label";
        internal const string DimensionChainLine = "dimension-chain-line";
        internal const string OverallWidthLabel = "overall-width-label";
        internal const string LegendSurfaces = "legend-surfaces";
        internal const string LegendUtilities = "legend-utilities";

        /// <summary>Text height of a gap-piece width (narrow curb/island pieces).</summary>
        internal const double GapWidthTextHeight = 0.62;
        internal const double OverallWidthTextHeight = 0.75;
        internal const double LegendTextHeight = 0.62;

        internal sealed record Expected(
            string Kind,
            string SemanticKey,
            string? Text = null,
            double? Offset = null,
            double? From = null,
            double? To = null,
            string? MarkKind = null,
            short? ColorIndex = null);

        internal static string Key(string kind, params object?[] values) =>
            string.Join("|", new[] { kind }.Concat(values.Select(Canonical)));

        internal static IReadOnlyList<Expected> ExpectedFor(
            SectionPlanRecord record, SectionPresentationCoveragePlan coverage)
        {
            if (coverage.DimensionMarks.Count != coverage.DimensionMarkCount ||
                coverage.DimensionOffsets.Count != coverage.DimensionMarkCount ||
                coverage.DimensionMarks.Count != coverage.DimensionOffsets.Count)
                throw new InvalidOperationException(
                    "PLAN dimension-mark semantic evidence is incomplete.");
            for (var i = 0; i < coverage.DimensionMarks.Count; i++)
            {
                var mark = coverage.DimensionMarks[i];
                if (!double.IsFinite(mark.OffsetM) ||
                    Math.Abs(mark.OffsetM - coverage.DimensionOffsets[i]) > 0.0005 ||
                    string.IsNullOrWhiteSpace(mark.Kind) || mark.Label == null ||
                    mark.ColorIndex < 1 || mark.ColorIndex > 255)
                    throw new InvalidOperationException(
                        "PLAN dimension-mark semantic evidence is invalid or reordered.");
            }

            var titleText = TitleText(record);
            var expected = new List<Expected>
            {
                new(AxisLine, Key(AxisLine)),
                new(AxisLabel, Key(AxisLabel, "ציר"), "ציר"),
                new(Title, Key(Title, titleText), titleText),
                new(LegendSurfaces,
                    Key(LegendSurfaces, MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.LegendSurfaces),
                    MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.LegendSurfaces),
            };
            if (record.ProjectedEntities.Count > 0)
                expected.Add(new Expected(LegendUtilities,
                    Key(LegendUtilities, MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.LegendUtilities),
                    MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.LegendUtilities));

            foreach (var mark in coverage.DimensionMarks)
            {
                expected.Add(new Expected(
                    DimensionTopTick,
                    Key(DimensionTopTick, mark.OffsetM, mark.Kind, mark.ColorIndex),
                    Offset: mark.OffsetM, MarkKind: mark.Kind,
                    ColorIndex: mark.ColorIndex));
                expected.Add(new Expected(
                    DimensionBottomTick,
                    Key(DimensionBottomTick, mark.OffsetM, mark.Kind, (short)8),
                    Offset: mark.OffsetM, MarkKind: mark.Kind,
                    ColorIndex: 8));
                if (string.Equals(mark.Kind, "row", StringComparison.Ordinal))
                {
                    expected.Add(new Expected(
                        RowLine, Key(RowLine, mark.OffsetM), Offset: mark.OffsetM));
                    expected.Add(new Expected(
                        RowLabel, Key(RowLabel, mark.OffsetM, "זכות דרך"), "זכות דרך",
                        Offset: mark.OffsetM));
                }
            }

            if (coverage.ResolvedSpans.Count != coverage.WidthSpanCount ||
                coverage.ResolvedSpans.Count != coverage.NamedStripCount)
                throw new InvalidOperationException(
                    "PLAN resolved-span semantic evidence is incomplete.");
            foreach (var span in coverage.ResolvedSpans)
            {
                if (!double.IsFinite(span.FromOffsetM) || !double.IsFinite(span.ToOffsetM) ||
                    !double.IsFinite(span.WidthM) || span.ToOffsetM <= span.FromOffsetM ||
                    string.IsNullOrWhiteSpace(span.Label))
                    throw new InvalidOperationException(
                        "PLAN resolved-span semantic evidence is invalid.");
                var widthText = span.WidthM.ToString("F2", CultureInfo.InvariantCulture);
                expected.Add(new Expected(
                    WidthLabel, Key(WidthLabel, span.FromOffsetM, span.ToOffsetM, widthText),
                    widthText, From: span.FromOffsetM, To: span.ToOffsetM));
                expected.Add(new Expected(
                    StripLabel, Key(StripLabel, span.FromOffsetM, span.ToOffsetM, span.Label),
                    span.Label, From: span.FromOffsetM, To: span.ToOffsetM));
            }

            // SEC-B4: the width row is a CLOSED chain. Every run of marks between named
            // strips (curb faces, island noses) gets its own width, and the chain one
            // overall width, so the drawn widths add up to the section extent.
            if (coverage.DimensionMarks.Count >= 2)
            {
                if (!MahodAI.CivilDelivery.Shared.SectionDimensionChainLogic.TryBuild(
                        coverage.DimensionMarks.Select(mark => mark.OffsetM).ToList(),
                        coverage.ResolvedSpans.Select(span => (span.FromOffsetM, span.ToOffsetM)).ToList(),
                        out var chain, out var chainError) || chain == null)
                    throw new InvalidOperationException(
                        "PLAN dimension chain does not close: " + chainError);
                foreach (var piece in chain.Pieces.Where(candidate => !candidate.IsNamedStrip))
                {
                    var gapText = MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.GapWidthText(piece.Width);
                    expected.Add(new Expected(
                        GapWidthLabel, Key(GapWidthLabel, piece.From, piece.To, gapText),
                        gapText, From: piece.From, To: piece.To));
                }
                expected.Add(new Expected(
                    DimensionChainLine, Key(DimensionChainLine, chain.From, chain.To),
                    From: chain.From, To: chain.To, ColorIndex: 8));
                var overallText = MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.OverallWidthText(
                    chain.OverallWidth);
                expected.Add(new Expected(
                    OverallWidthLabel, Key(OverallWidthLabel, chain.From, chain.To, overallText),
                    overallText, From: chain.From, To: chain.To));
            }

            var duplicate = expected.GroupBy(item => item.SemanticKey, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() != 1);
            if (duplicate != null)
                throw new InvalidOperationException(
                    $"PLAN core-presentation semantic key is not unique: {duplicate.Key}.");
            return expected;
        }

        /// <summary>
        /// SEC-m3: the drawn title names the alignment and the Israeli chainage from
        /// PLAN ("חתך STA-12145 · תוואי 2000 · 12+145.43"). APPLY and VERIFY share it.
        /// </summary>
        internal static string TitleText(SectionPlanRecord record) =>
            MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.Title(
                record.SectionId ?? record.Cl.CandidateSectionNumber ?? record.RecordId,
                record.SelectedAlignment, record.Station);

        private static string Canonical(object? value) => value switch
        {
            null => string.Empty,
            double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
            float f when float.IsFinite(f) => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
