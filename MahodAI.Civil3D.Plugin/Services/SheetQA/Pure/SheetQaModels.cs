using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>
    /// Which space a record was collected from. Findings in <see cref="Model"/> are marked
    /// with geometry in model space (seen through the layout's viewport); findings in
    /// <see cref="Paper"/> are marked in that layout's paper space.
    ///
    /// Serialised by name: the protocol documents <c>space</c> as "Paper"|"Model", and the
    /// shared WebSocket serialiser registers no enum converter, so without this attribute
    /// the wire would carry 0/1 and the fixture would be a lie.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SheetSpace
    {
        Paper,
        Model
    }

    /// <summary>
    /// One piece of text visible on a sheet, reduced to plain numbers so the detectors
    /// stay Autodesk-free (and therefore testable in MahodAI.Core.Tests).
    ///
    /// The bounding box is axis-aligned in the space's own coordinates and is already
    /// transformed out of any xref block reference. <see cref="RotationDeg"/> is the
    /// APPARENT rotation on the sheet (0-360), i.e. after the xref transform, which is
    /// what decides readability — not the entity's own stored rotation.
    /// </summary>
    public sealed class SheetTextRecord
    {
        public string RawText { get; set; } = string.Empty;

        /// <summary>Human-readable text after Hebrew CAD decoding (see <see cref="HebrewCadTextDecoder"/>).</summary>
        public string DisplayText { get; set; } = string.Empty;

        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        /// <summary>Apparent rotation on the sheet, normalised to [0, 360).</summary>
        public double RotationDeg { get; set; }

        public double Height { get; set; }

        /// <summary>
        /// Layer name as reported by the entity. For xref content this is ALREADY the
        /// host-prefixed "xrefName|layerName" — never prefix it a second time.
        /// </summary>
        public string Layer { get; set; } = string.Empty;

        /// <summary>Xref block name when the text lives inside an xref, else null.</summary>
        public string? SourceXref { get; set; }

        public SheetSpace Space { get; set; }

        public string? Handle { get; set; }

        public double Width => MaxX - MinX;

        public double BoxHeight => MaxY - MinY;

        public double Area => Width * BoxHeight;
    }

    /// <summary>
    /// One piece of visible linework, reduced to what the legend comparison needs:
    /// the EFFECTIVE colour and linetype (ByLayer already resolved through the host
    /// layer table) plus a bounding box so a finding can be marked.
    /// </summary>
    public sealed class SheetCurveRecord
    {
        /// <summary>Effective ACI colour index, or -1 when it could not be resolved.</summary>
        public int ColorIndex { get; set; } = -1;

        /// <summary>Effective linetype base name (no xref prefix), e.g. "BK", "Continuous".</summary>
        public string Linetype { get; set; } = string.Empty;

        public string Layer { get; set; } = string.Empty;

        public string? SourceXref { get; set; }

        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        public string? Handle { get; set; }

        /// <summary>The key the legend comparison matches on: "colour / linetype".</summary>
        public string StyleKey => LegendMatcher.BuildStyleKey(ColorIndex, Linetype);
    }

    /// <summary>
    /// One row of the sheet's legend: a sample line with its Hebrew caption. The caption
    /// is paired to the line by vertical proximity (legends are laid out in rows).
    /// </summary>
    public sealed class LegendRow
    {
        public int ColorIndex { get; set; } = -1;

        public string Linetype { get; set; } = string.Empty;

        public string RawLabel { get; set; } = string.Empty;

        /// <summary>Caption after Hebrew CAD decoding.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Vertical distance to the caption that was paired with this line.</summary>
        public double LabelDistance { get; set; }

        public string StyleKey => LegendMatcher.BuildStyleKey(ColorIndex, Linetype);
    }

    /// <summary>
    /// Finding categories. Kept as constants rather than an enum so they serialise to the
    /// exact snake_case strings the protocol fixture declares, with no converter needed.
    /// </summary>
    public static class VisualFindingCategories
    {
        public const string TextOverlap = "text_overlap";
        public const string RotatedText = "rotated_text";
        public const string DeclutterCandidate = "declutter_candidate";

        /// <summary>Drawn on the plan but absent from this sheet's legend.</summary>
        public const string LegendMissingRow = "legend_missing_row";

        /// <summary>Present in the legend but never drawn on this sheet.</summary>
        public const string LegendOrphanRow = "legend_orphan_row";

        /// <summary>Cancellation X marks whose colour does not match their network line.</summary>
        public const string XConvention = "x_convention";
    }

    public static class VisualFindingSeverities
    {
        public const string High = "high";
        public const string Medium = "medium";
        public const string Low = "low";
    }

    /// <summary>
    /// A single visual-QA finding. Carries everything the markup writer needs to draw a
    /// circle and everything the agent needs to describe it in Hebrew.
    /// </summary>
    public sealed class VisualFinding
    {
        /// <summary>Stable per-run id, e.g. "VS-3001-01-007". Matches the label drawn in the DWG.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>One of <see cref="VisualFindingCategories"/>.</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>One of <see cref="VisualFindingSeverities"/>.</summary>
        public string Severity { get; set; } = VisualFindingSeverities.Medium;

        public string Layout { get; set; } = string.Empty;

        public SheetSpace Space { get; set; }

        /// <summary>Centre of the area to circle. Meaningless for declutter findings (no geometry).</summary>
        public double CenterX { get; set; }
        public double CenterY { get; set; }

        /// <summary>Suggested circle radius in the space's units.</summary>
        public double Radius { get; set; }

        /// <summary>True when the finding has a place on the sheet worth circling.</summary>
        public bool HasLocation { get; set; }

        /// <summary>Short machine-generated title; the agent may replace it with a Hebrew one.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Supporting detail (the texts involved, the counts, the style key…).</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>Handles of the entities involved, for a later fix pass.</summary>
        public List<string> Handles { get; set; } = new();

        public string? Layer { get; set; }

        public string? SourceXref { get; set; }

        /// <summary>Handle of the circle drawn for this finding, set by the markup writer.</summary>
        public string? MarkerHandle { get; set; }
    }

    /// <summary>
    /// A layer that is a candidate for being switched off to reduce sheet clutter.
    /// The agent decides main-vs-secondary; this is the raw evidence.
    /// </summary>
    public sealed class DeclutterCandidate
    {
        public string Layer { get; set; } = string.Empty;

        public string? SourceXref { get; set; }

        public int TextCount { get; set; }

        /// <summary>How many of those texts are pure numbers (elevation marks, chainages…).</summary>
        public int NumericTextCount { get; set; }

        /// <summary>0..1 — a high share of numeric text is the elevation-annotation signature.</summary>
        public double NumericShare { get; set; }
    }
}
