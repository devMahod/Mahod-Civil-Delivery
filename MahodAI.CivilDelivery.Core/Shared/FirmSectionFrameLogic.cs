using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Decodes the coordinate frame of one MahodCivilNet section block (the firm's closed
/// section program) from the entities the caller lifts off its layers: the HW-CS-CL
/// centreline (offset 0), the HW-CS-TABL elevation ruler (whole-number texts beside the
/// frame) and the HW-CS-D top width chain (tick lines with one width text between each
/// consecutive pair).
///
/// Typical sections by clicking on the plan (feature/typical-sections, 2026-10): the firm
/// draws the block, Civil Delivery writes a Hebrew strip name above every width in its
/// chain. For that it must know where offset 0 is and how many block units one metre is.
/// The firm draws 1:1 in metres today, but nothing in the block says so, and a block in a
/// 1:200 sheet or from a later MahodCivilNet would silently put every name on the wrong
/// strip by the scale factor. So the scale is MEASURED, never assumed: each consecutive
/// tick pair against the width text printed between its ticks, and every pair must agree
/// with one common scale (within 1 % plus the rounding of the printed text, since "0.31"
/// may stand for 0.3149 m). The vertical frame comes from the ruler labels the same way.
/// Any contradiction fails closed: no scale, no segments, and a finding code the panel
/// shows instead of a misplaced name.
///
/// Pure logic (no Autodesk types, no I/O). The caller extracts the entity data in the
/// block's own coordinates and draws only what this class returns.
/// </summary>
public static class FirmSectionFrameLogic
{
    /// <summary>Relative agreement every measured tick pair must show with the common scale.</summary>
    public const double ScaleAgreementTolerance = 0.01;

    /// <summary>Ticks closer than this (block units) are one tick extracted twice, not a width.</summary>
    public const double CoincidentTickTolerance = 1e-6;

    /// <summary>Finding codes. Plain strings so the panel and the tests compare them as data.</summary>
    public static class Finding
    {
        /// <summary>No HW-CS-CL centreline: offsets cannot be placed, so no segments.</summary>
        public const string NoCentreline = "frame-no-centreline";

        /// <summary>Fewer than two distinct HW-CS-D ticks: there is no chain to measure.</summary>
        public const string NoChain = "frame-no-chain";

        /// <summary>Ticks exist but no pair carries exactly one readable width: no scale.</summary>
        public const string NoChainWidths = "frame-no-chain-widths";

        /// <summary>At least one measured pair contradicts the common scale: no scale, no segments.</summary>
        public const string ScaleInconsistent = "frame-scale-inconsistent";

        /// <summary>A tick pair has no width text; its segment carries the measured width and an empty text.</summary>
        public const string ChainTextMissing = "frame-chain-text-missing";

        /// <summary>A tick pair has several width texts and the measured width does not single one out.</summary>
        public const string ChainTextAmbiguous = "frame-chain-text-ambiguous";

        /// <summary>A chain text is not a width ("3.60", "3.60 m", "3.60 מ'" are).</summary>
        public const string ChainTextUnparsable = "frame-chain-text-unparsable";

        /// <summary>Fewer than two ruler labels at distinct elevations: no vertical frame.</summary>
        public const string NoRuler = "frame-no-ruler";

        /// <summary>The ruler labels do not sit on one upward linear elevation axis: no vertical frame.</summary>
        public const string RulerInconsistent = "frame-ruler-inconsistent";

        /// <summary>A non-finite coordinate was dropped from the input.</summary>
        public const string InvalidInput = "frame-input-invalid";
    }

    /// <summary>
    /// Entity data of one block, all in the block's own coordinates. Ruler labels pair the
    /// elevation a HW-CS-TABL text reads with the Y it sits at; chain ticks are the X of
    /// every HW-CS-D tick line; chain texts are every HW-CS-D DBText with its X.
    /// </summary>
    public sealed record FirmSectionFrameInput(
        double? CentrelineX,
        IReadOnlyList<(double Elevation, double Y)> RulerLabels,
        IReadOnlyList<double> ChainTickXs,
        IReadOnlyList<(double X, string Text)> ChainTexts);

    /// <summary>
    /// One consecutive tick pair of the chain, left to right, in signed metres from the
    /// centreline (left negative). WidthM is the width the firm printed; only when the pair
    /// has no readable text is it the width measured from the ticks, with WidthText empty.
    /// </summary>
    public sealed record ChainSegment(double FromOffsetM, double ToOffsetM, double WidthM, string WidthText)
    {
        /// <summary>Block X of the width text this segment was read from; null when the pair had none.</summary>
        public double? WidthTextX { get; init; }

        public double LengthM => ToOffsetM - FromOffsetM;
    }

    public sealed record FirmSectionFrame(
        double? CentrelineX,
        double? HorizontalScale,
        double? VerticalScale,
        double? Datum,
        IReadOnlyList<ChainSegment> Segments,
        IReadOnlyList<string> Findings)
    {
        public bool HasHorizontalFrame => CentrelineX is { } && HorizontalScale is > 0;

        public bool HasVerticalFrame => Datum is { } && VerticalScale is > 0;

        /// <summary>Signed metres from the centreline of a block X; null without a horizontal frame.</summary>
        public double? OffsetOf(double x) =>
            HasHorizontalFrame ? (x - CentrelineX!.Value) / HorizontalScale!.Value : null;

        /// <summary>Block X of a signed offset in metres; null without a horizontal frame.</summary>
        public double? XOf(double offsetM) =>
            HasHorizontalFrame ? CentrelineX!.Value + offsetM * HorizontalScale!.Value : null;

        /// <summary>Elevation of a block Y; null without a vertical frame.</summary>
        public double? ElevationOf(double y) =>
            HasVerticalFrame ? (y - Datum!.Value) / VerticalScale!.Value : null;

        /// <summary>Block Y of an elevation; null without a vertical frame.</summary>
        public double? YOf(double elevation) =>
            HasVerticalFrame ? Datum!.Value + elevation * VerticalScale!.Value : null;
    }

    public static FirmSectionFrame Decode(FirmSectionFrameInput input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        var findings = new List<string>();

        var centreline = input.CentrelineX;
        if (centreline is { } rawCentreline && !double.IsFinite(rawCentreline))
        {
            findings.Add(Finding.InvalidInput);
            centreline = null;
        }
        if (centreline == null) findings.Add(Finding.NoCentreline);

        var (verticalScale, datum) = DecodeRuler(input.RulerLabels, findings);
        var ticks = NormalizeTicks(input.ChainTickXs, findings);
        var texts = NormalizeTexts(input.ChainTexts, findings);

        double? horizontalScale = null;
        var segments = new List<ChainSegment>();
        if (ticks.Count < 2)
        {
            findings.Add(Finding.NoChain);
        }
        else
        {
            var pairs = new List<TickPair>();
            for (var i = 0; i < ticks.Count - 1; i++)
            {
                var from = ticks[i];
                var to = ticks[i + 1];
                pairs.Add(new TickPair(from, to, texts
                    .Where(text => text.X > from + CoincidentTickTolerance &&
                                   text.X < to - CoincidentTickTolerance)
                    .ToList()));
            }

            // Only a pair with exactly one readable width measures the scale. A pair with
            // several texts is resolved afterwards against the scale the others agree on.
            var measured = pairs.Where(pair => pair.ReadableTexts.Count == 1).ToList();
            if (measured.Count == 0)
            {
                findings.Add(Finding.NoChainWidths);
            }
            else
            {
                var scale = measured.Sum(pair => pair.Length) /
                            measured.Sum(pair => pair.ReadableTexts[0].WidthM);
                var consistent = scale > 0 && measured.All(pair =>
                    Agrees(pair.Length / scale, pair.ReadableTexts[0]));
                if (!consistent)
                {
                    findings.Add(Finding.ScaleInconsistent);
                }
                else
                {
                    horizontalScale = scale;
                    if (centreline is { } centrelineX)
                        segments.AddRange(pairs.Select(pair => Segment(pair, centrelineX, scale, findings)));
                }
            }
        }

        return new FirmSectionFrame(centreline, horizontalScale, verticalScale, datum,
            segments, findings.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Reads a chain width text: a decimal number, optionally followed by a unit ("m",
    /// "מ'", "מטר") or spaces, invariant culture. Decimals is the printed precision, so the
    /// caller can allow for the rounding the firm applied ("0.31" is anything in 0.305..0.315).
    /// </summary>
    public static bool TryParseWidthM(string? text, out double widthM, out int decimals)
    {
        widthM = 0;
        decimals = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = WidthPattern.Match(text);
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
            return false;
        widthM = value;
        decimals = match.Groups["fraction"].Success ? match.Groups["fraction"].Value.Length : 0;
        return true;
    }

    private static readonly Regex WidthPattern = new(
        @"^\s*(?<value>\d+(?:\.(?<fraction>\d+))?)\s*[\p{L}'""׳״.]*\s*$",
        RegexOptions.CultureInvariant);

    private sealed record ChainText(double X, string Text, double WidthM, int Decimals);

    private sealed record TickPair(double From, double To, IReadOnlyList<ChainText> ReadableTexts)
    {
        public double Length => To - From;
    }

    private static bool Agrees(double measuredWidthM, ChainText text) =>
        Math.Abs(measuredWidthM - text.WidthM) <=
        ScaleAgreementTolerance * text.WidthM + 0.5 * Math.Pow(10, -text.Decimals) + 1e-9;

    private static ChainSegment Segment(TickPair pair, double centrelineX, double scale, List<string> findings)
    {
        var fromOffset = (pair.From - centrelineX) / scale;
        var toOffset = (pair.To - centrelineX) / scale;
        var measuredWidth = pair.Length / scale;

        ChainText? text = null;
        if (pair.ReadableTexts.Count == 1)
        {
            text = pair.ReadableTexts[0];
        }
        else if (pair.ReadableTexts.Count > 1)
        {
            var agreeing = pair.ReadableTexts.Where(candidate => Agrees(measuredWidth, candidate)).ToList();
            if (agreeing.Count == 1) text = agreeing[0];
            else findings.Add(Finding.ChainTextAmbiguous);
        }
        else
        {
            findings.Add(Finding.ChainTextMissing);
        }

        return text == null
            ? new ChainSegment(fromOffset, toOffset, measuredWidth, string.Empty)
            : new ChainSegment(fromOffset, toOffset, text.WidthM, text.Text) { WidthTextX = text.X };
    }

    private static (double? Scale, double? Datum) DecodeRuler(
        IReadOnlyList<(double Elevation, double Y)>? labels, List<string> findings)
    {
        var all = labels ?? Array.Empty<(double Elevation, double Y)>();
        var valid = all.Where(label => double.IsFinite(label.Elevation) && double.IsFinite(label.Y)).ToList();
        if (valid.Count != all.Count) findings.Add(Finding.InvalidInput);

        var ordered = valid.OrderBy(label => label.Elevation).ThenBy(label => label.Y).ToList();
        if (ordered.Count < 2 || ordered[^1].Elevation - ordered[0].Elevation <= 0)
        {
            findings.Add(Finding.NoRuler);
            return (null, null);
        }

        var low = ordered[0];
        var high = ordered[^1];
        var scale = (high.Y - low.Y) / (high.Elevation - low.Elevation);
        var datum = low.Y - scale * low.Elevation;
        var tolerance = ScaleAgreementTolerance * Math.Abs(high.Y - low.Y) + 1e-9;
        // The ruler must go up, and every label must sit on the line the two extremes define.
        if (!(scale > 0) ||
            ordered.Any(label => Math.Abs(label.Y - (datum + scale * label.Elevation)) > tolerance))
        {
            findings.Add(Finding.RulerInconsistent);
            return (null, null);
        }
        return (scale, datum);
    }

    private static List<double> NormalizeTicks(IReadOnlyList<double>? ticks, List<string> findings)
    {
        var all = ticks ?? Array.Empty<double>();
        var valid = all.Where(double.IsFinite).OrderBy(x => x).ToList();
        if (valid.Count != all.Count) findings.Add(Finding.InvalidInput);

        var distinct = new List<double>();
        foreach (var x in valid)
        {
            if (distinct.Count == 0 || x - distinct[^1] > CoincidentTickTolerance)
                distinct.Add(x);
        }
        return distinct;
    }

    private static List<ChainText> NormalizeTexts(
        IReadOnlyList<(double X, string Text)>? texts, List<string> findings)
    {
        var result = new List<ChainText>();
        foreach (var (x, text) in texts ?? Array.Empty<(double X, string Text)>())
        {
            if (!double.IsFinite(x))
            {
                findings.Add(Finding.InvalidInput);
                continue;
            }
            if (!TryParseWidthM(text, out var widthM, out var decimals))
            {
                findings.Add(Finding.ChainTextUnparsable);
                continue;
            }
            result.Add(new ChainText(x, text.Trim(), widthM, decimals));
        }
        return result.OrderBy(text => text.X).ToList();
    }
}
