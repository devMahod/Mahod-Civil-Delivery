using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;
using NetTopologySuite.Geometries;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>One interpretation of complete source regions shared by PLAN and APPLY.</summary>
internal static class SectionHatchSpanLabelService
{
    internal const string EvidenceSource = "source-hatch-region";
    /// <summary>b7: whole-span coverage proven locally on the exact span segment of a
    /// globally invalid (self-intersecting) source. Not a region, area or quantity.</summary>
    internal const string LocalCutEvidenceSource = "source-hatch-local-cut";
    private const string PartialConflictSource = "source-hatch-partial-conflict";
    private const string BaselineConflictSource = "source-hatch-baseline-conflict";
    private static readonly GeometryFactory Factory = new();

    internal static bool IsRegionEvidence(string source) =>
        source is EvidenceSource or LocalCutEvidenceSource or PartialConflictSource or BaselineConflictSource;

    /// <summary>One local-cut classification of one span. A null source means the span
    /// endpoints could not be resolved, so no local source can be proven harmless there.</summary>
    internal sealed record LocalCutVerdict(SectionHatchLocalCut.Source? Source, double From, double To,
        SectionHatchLocalCut.Coverage Coverage, string? Evidence, string? Reason);

    /// <summary>
    /// A recognized boundary role is not automatically a named area. Admit only
    /// existing semantic role/label pairs; never derive a label from *HATCH*, a
    /// pattern name, or a curb/ROW boundary. The approved label is retained verbatim.
    /// </summary>
    internal static bool IsSupportedSemanticRule(ProjectionRuleMatch rule)
    {
        var label = rule.Label?.Trim();
        return rule.Kind?.Trim().ToLowerInvariant() switch
        {
            "sidewalk" => label is "מדרכה",
            "bike" => label is "שביל אופניים" or "נתיב אופניים",
            "garden" => label is "גינון" or "רצועת גינון",
            "parking" => label is "חניה" or "רצועת חניה",
            "shoulder" => label is "שול" or "רצועת בטיחות",
            "island" => label is "אי תנועה" or "מפרדה",
            "lane" => label is "נתיב נסיעה" or "נתיבי נסיעה" or "נת\"צ" or "דרך שירות" or "רצועת דרך",
            "strip" => label is "מדרכה" or "שביל אופניים" or "נתיב אופניים" or "גינון" or
                "רצועת גינון" or "חניה" or "רצועת חניה" or "שול" or "רצועת בטיחות" or
                "אי תנועה" or "מפרדה" or "נתיב נסיעה" or "נתיבי נסיעה" or "נת\"צ" or
                "דרך שירות" or "רצועת דרך",
            _ => false,
        };
    }

    internal sealed record Region(
        IReadOnlyList<IReadOnlyList<P2>> Loops,
        SectionRegionCoverageLogic.FillStyle FillStyle,
        string Label,
        string Evidence,
        Envelope Bounds,
        IReadOnlyList<NetTopologySuite.Geometries.Geometry> Boundaries,
        double ApproximationToleranceM,
        IReadOnlyList<SectionHatchBoundaryGeometry.StraightEdge> FilledSeams,
        DeferredSource? Deferred = null);

    // A deferred source is explicitly NOT a globally valid region. Every original
    // loop remains represented, including unreadable loops and unknown bounds.
    internal sealed record SourceLoop(int Index, IReadOnlyList<P2> Points,
        IReadOnlyList<SectionHatchBoundaryGeometry.StraightEdge> StraightEdges,
        double[]? Bounds, string? Failure, double ApproximationToleranceM);

    internal sealed record DeferredSource(IReadOnlyList<SourceLoop> Loops,
        string Layer, string? Xref, string HandlePath, string? DrawingPath,
        string? DrawingHash, string? GlobalFailure = null,
        SectionHatchLocalCut.Source? LocalCut = null, string? LocalCutRefusal = null);

    internal static Region CreateSourceRegion(IReadOnlyList<SourceLoop> loops,
        SectionRegionCoverageLogic.FillStyle fillStyle, string label, string layer,
        string? xref, string handlePath, string? drawingPath, string? drawingHash,
        SectionHatchLocalCut.RawLoop? localCutRaw = null)
    {
        if (loops.Count == 0 || loops.Select(loop => loop.Index).Distinct().Count() != loops.Count ||
            string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(handlePath) ||
            string.IsNullOrWhiteSpace(drawingPath) || !SectionVehicleDirectionPlanner.IsSha256(drawingHash) ||
            !Enum.IsDefined(fillStyle))
            throw new InvalidOperationException("Source hatch region lacks complete geometry/provenance.");
        var checkedLoops = new List<SourceLoop>();
        foreach (var loop in loops)
        {
            var failure = loop.Failure;
            if (failure == null)
            {
                try
                {
                    _ = CreateRegion(new[] { loop.Points }, fillStyle, label, layer, xref,
                        handlePath, drawingPath, drawingHash, loop.ApproximationToleranceM);
                }
                catch (InvalidOperationException error) { failure = error.Message; }
            }
            checkedLoops.Add(loop with { Failure = failure });
        }
        string? globalFailure = null;
        if (checkedLoops.All(loop => loop.Failure == null))
        {
            try { return Complete(checkedLoops); }
            catch (InvalidOperationException error) { globalFailure = error.Message; }
        }
        var identity = ArtifactHash.Sha256OfText($"deferred-source|{SourceHashForEvidence(xref, drawingHash)}|{drawingPath}|{handlePath}|{fillStyle}|{label}|loops={loops.Count}");
        var (localCut, localCutRefusal) = LocalCutFor(loops, checkedLoops, localCutRaw, identity);
        var source = new DeferredSource(checkedLoops.AsReadOnly(), layer, xref,
            handlePath, drawingPath, drawingHash, globalFailure, localCut, localCutRefusal);
        var bounds = new Envelope();
        foreach (var loop in checkedLoops)
        {
            if (!SectionProjectionFailureScope.HasUsableBounds(loop.Bounds))
            {
                // An unknown loop may affect any span; do not hide it behind the
                // envelope of the loops the reader happened to recover.
                bounds = new Envelope(double.MinValue, double.MaxValue, double.MinValue, double.MaxValue);
                break;
            }
            bounds.ExpandToInclude(new Envelope(loop.Bounds![0], loop.Bounds[2], loop.Bounds[1], loop.Bounds[3]));
        }
        return new Region(Array.Empty<IReadOnlyList<P2>>(), fillStyle, label, identity, bounds,
            Array.Empty<NetTopologySuite.Geometries.Geometry>(), 0,
            Array.Empty<SectionHatchBoundaryGeometry.StraightEdge>(), source);

        Region Complete(IReadOnlyList<SourceLoop> selected) => CreateRegion(
            selected.Select(loop => loop.Points).ToArray(), fillStyle, label, layer, xref,
            handlePath, drawingPath, drawingHash, selected.Max(loop => loop.ApproximationToleranceM),
            selected.Select(loop => loop.StraightEdges).ToArray());
    }

    /// <summary>
    /// b7 eligibility, kept separate from the polygon path: one loop read without any
    /// reader/flag failure, whose ONLY topology failure is an NTS self-intersection, plus
    /// the Core gates (raw native lines/arcs, closure, transform, analytic root). Any other
    /// failure keeps the source on the unchanged fail-closed path.
    /// </summary>
    private static (SectionHatchLocalCut.Source?, string?) LocalCutFor(IReadOnlyList<SourceLoop> loops,
        IReadOnlyList<SourceLoop> checkedLoops, SectionHatchLocalCut.RawLoop? raw, string identity)
    {
        if (raw == null) return (null, null);
        if (loops.Count != 1 || loops[0].Failure != null)
            return (null, "the single loop was not read cleanly");
        if (checkedLoops[0].Failure == null || !FailsOnlyBySelfIntersection(loops[0].Points))
            return (null, "the loop failure is not a self-intersection");
        return SectionHatchLocalCut.TryCreate(raw with { SourceIdentity = identity }, out var source, out var refusal)
            ? (source, null) : (null, refusal);
    }

    private static bool FailsOnlyBySelfIntersection(IReadOnlyList<P2> points)
    {
        if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return false;
        var coordinates = points.Select(point => new Coordinate(point.X, point.Y))
            .Append(new Coordinate(points[0].X, points[0].Y)).ToArray();
        var validity = new NetTopologySuite.Operation.Valid.IsValidOp(Factory.CreatePolygon(coordinates));
        return !validity.IsValid && validity.ValidationError?.ErrorType is
            NetTopologySuite.Operation.Valid.TopologyValidationErrors.SelfIntersection or
            NetTopologySuite.Operation.Valid.TopologyValidationErrors.RingSelfIntersection;
    }

    internal static Region? CompleteForSegment(Region source, P2 start, P2 end)
    {
        if (source.Deferred is not { } deferred) return source;
        var endpoints = new[] { start.X, start.Y, end.X, end.Y };
        // Every omitted loop must have a finite conservative envelope disjoint
        // from the ENTIRE segment, not merely from its midpoint or endpoints.
        if (deferred.Loops.Any(loop => !SectionProjectionFailureScope.HasUsableBounds(loop.Bounds))) return null;
        var relevant = deferred.Loops.Where(loop =>
            SectionProjectionFailureScope.MayIntersect(loop.Bounds, endpoints)).ToArray();
        if (relevant.Length == 0 || relevant.Any(loop => loop.Failure != null)) return null;
        try
        {
            var region = CreateRegion(relevant.Select(loop => loop.Points).ToArray(), source.FillStyle,
                source.Label, deferred.Layer, deferred.Xref, deferred.HandlePath,
                deferred.DrawingPath, deferred.DrawingHash, relevant.Max(loop => loop.ApproximationToleranceM),
                relevant.Select(loop => loop.StraightEdges).ToArray());
            // Bind the full source identity, exact span and retained loop indices.
            // A changed source, hole or cut cannot reuse an earlier subset proof.
            return region with { Evidence = ArtifactHash.Sha256OfText(FormattableString.Invariant(
                $"{source.Evidence}|{region.Evidence}|segment={start.X:R},{start.Y:R},{end.X:R},{end.Y:R}|included={string.Join(",", relevant.Select(loop => loop.Index))}")) };
        }
        catch (InvalidOperationException) { return null; }
    }

    internal static Region CreateRegion(
        IReadOnlyList<IReadOnlyList<P2>> loops,
        SectionRegionCoverageLogic.FillStyle fillStyle,
        string label, string layer, string? xref, string handlePath,
        string? drawingPath, string? drawingHash, double approximationToleranceM = 0,
        IReadOnlyList<IReadOnlyList<SectionHatchBoundaryGeometry.StraightEdge>>? sourceStraightEdges = null)
    {
        if (loops.Count == 0 || string.IsNullOrWhiteSpace(label) ||
            string.IsNullOrWhiteSpace(handlePath) || string.IsNullOrWhiteSpace(drawingPath) ||
            !SectionVehicleDirectionPlanner.IsSha256(drawingHash) ||
            !double.IsFinite(approximationToleranceM) || approximationToleranceM < 0 ||
            !Enum.IsDefined(fillStyle))
            throw new InvalidOperationException("Source hatch region lacks complete geometry/provenance.");
        var boundaries = new List<NetTopologySuite.Geometries.Geometry>();
        var polygons = new List<Polygon>();
        var bounds = new Envelope();
        var immutableLoops = new List<IReadOnlyList<P2>>();
        for (var loopIndex = 0; loopIndex < loops.Count; loopIndex++)
        {
            var loop = loops[loopIndex];
            if (loop.Count < 3 || loop.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                throw new InvalidOperationException("A source hatch loop is empty or non-finite.");
            var points = loop.ToArray();
            var coordinates = points.Select(point => new Coordinate(point.X, point.Y))
                .Append(new Coordinate(points[0].X, points[0].Y)).ToArray();
            var polygon = Factory.CreatePolygon(coordinates);
            var validity = new NetTopologySuite.Operation.Valid.IsValidOp(polygon);
            if (!validity.IsValid || polygon.IsEmpty || polygon.Area <= 1e-12)
                throw new InvalidOperationException($"A source hatch loop is not a valid simple closed region; loop={loopIndex}; " +
                    $"reason={validity.ValidationError?.ToString() ?? "empty or zero-area loop"}.");
            var boundary = polygon.Boundary;
            boundaries.Add(boundary);
            polygons.Add(polygon);
            bounds.ExpandToInclude(polygon.EnvelopeInternal);
            immutableLoops.Add(Array.AsReadOnly(points));
        }
        var filledSeams = new List<SectionHatchBoundaryGeometry.StraightEdge>();
        for (var i = 0; i < boundaries.Count; i++)
        for (var j = 0; j < i; j++)
        {
            var contact = boundaries[i].Intersection(boundaries[j]);
            if (contact.IsEmpty) continue;
            // Only proven top-level adjacent components may share a seam.
            // Contained/touching holes, crossing or overlapping interiors, point
            // contacts and tolerance-dependent near matches remain unsupported.
            var topLevel = !polygons.Where((_, index) => index != i && index != j)
                .Any(polygon => polygon.Contains(polygons[i].InteriorPoint) ||
                                polygon.Contains(polygons[j].InteriorPoint));
            var exactSeams = sourceStraightEdges?.Count == loops.Count
                ? sourceStraightEdges[i].Where(a => sourceStraightEdges[j].Any(b =>
                    (a.From == b.To && a.To == b.From) || (a.From == b.From && a.To == b.To))).ToList()
                : new List<SectionHatchBoundaryGeometry.StraightEdge>();
            var seamGeometry = Factory.CreateMultiLineString(exactSeams.Select(edge =>
                Factory.CreateLineString(new[] { new Coordinate(edge.From.X, edge.From.Y),
                    new Coordinate(edge.To.X, edge.To.Y) })).ToArray());
            if (!topLevel || !polygons[i].Relate(polygons[j]).Matches("F********") ||
                contact.Dimension != Dimension.Curve || exactSeams.Count == 0 ||
                !contact.Difference(seamGeometry).IsEmpty || !seamGeometry.Difference(contact).IsEmpty)
                throw new InvalidOperationException("Source hatch loops intersect or touch without a proven disjoint straight seam; hole topology is ambiguous.");
            filledSeams.AddRange(exactSeams);
        }
        if (filledSeams.Count > 0)
        {
            var internalSeams = Factory.CreateMultiLineString(filledSeams.Select(edge =>
                Factory.CreateLineString(new[] { new Coordinate(edge.From.X, edge.From.Y),
                    new Coordinate(edge.To.X, edge.To.Y) })).ToArray());
            // The shared seam is filled interior, not a curved outer boundary.
            boundaries = boundaries.Select(boundary => boundary.Difference(internalSeams)).ToList();
        }
        var canonical = string.Join("\n", new[]
        {
            // Match the existing dimension-source contract: host save bytes are not
            // geometry. Keep external hashes and exact local geometry/provenance.
            $"source-hatch-region-v2|{fillStyle}|{label}|{layer}|{xref}|{handlePath}|{drawingPath}|{SourceHashForEvidence(xref, drawingHash)}",
            FormattableString.Invariant($"approximation|{approximationToleranceM:R}"),
        }.Concat(immutableLoops.SelectMany((loop, index) =>
            new[] { $"loop|{index}" }.Concat(loop.Select(point =>
                FormattableString.Invariant($"{point.X:R}|{point.Y:R}")))))
            .Concat(filledSeams.Select(edge => FormattableString.Invariant(
                $"filled-straight-seam|{edge.From.X:R}|{edge.From.Y:R}|{edge.To.X:R}|{edge.To.Y:R}"))));
        return new Region(immutableLoops.AsReadOnly(), fillStyle, label,
            ArtifactHash.Sha256OfText(canonical), bounds, boundaries.AsReadOnly(), approximationToleranceM,
            filledSeams.AsReadOnly());
    }

    internal static List<SpanLabelOverride> Resolve(
        IReadOnlyList<Region> regions, IReadOnlyList<Crossing> crossings,
        IReadOnlyList<UnresolvedSpan> spans, ICollection<LocalCutVerdict>? localVerdicts = null) =>
        ResolveCore(regions, crossings, spans, Array.Empty<(double, double, string)>(), localVerdicts);

    internal static List<SpanLabelOverride> Resolve(
        IReadOnlyList<Region> regions, IReadOnlyList<Crossing> crossings,
        PresentationAnalysis presentation, ICollection<LocalCutVerdict>? localVerdicts = null) =>
        ResolveCore(regions, crossings, presentation.WidthSpans.Select(span =>
            new UnresolvedSpan(span.From, span.To, span.Width,
                presentation.DimensionMarks.Single(mark => mark.Offset == span.From).Kind,
                presentation.DimensionMarks.Single(mark => mark.Offset == span.To).Kind,
                "source-region-review")).ToList(), presentation.StripLabels, localVerdicts);

    private static List<SpanLabelOverride> ResolveCore(
        IReadOnlyList<Region> regions, IReadOnlyList<Crossing> crossings,
        IReadOnlyList<UnresolvedSpan> spans, IReadOnlyList<(double From, double To, string Label)> knownLabels,
        ICollection<LocalCutVerdict>? localVerdicts)
    {
        var results = new List<SpanLabelOverride>();
        var anyLocal = regions.Any(region => region.Deferred?.LocalCut != null);
        foreach (var span in spans)
        {
            var from = PointAt(span.From);
            var to = PointAt(span.To);
            if (from is not { } start || to is not { } end)
            {
                if (anyLocal) localVerdicts?.Add(new(null, span.From, span.To,
                    SectionHatchLocalCut.Coverage.Unknown, null, "span endpoints are not resolvable"));
                continue;
            }
            var spanBounds = new Envelope(start.X, end.X, start.Y, end.Y);
            var full = new List<(string Label, string Evidence, string Source)>();
            var partial = new List<(string Label, string Evidence)>();
            foreach (var source in regions)
            {
                if (!source.Bounds.Intersects(spanBounds)) continue;
                var region = CompleteForSegment(source, start, end);
                if (region == null)
                {
                    // b7: a separate local proof on this exact span segment. Full may
                    // name only with the source's own mapped label; Partial is conflict
                    // evidence only; Unknown is recorded so the topology finding stays.
                    if (source.Deferred?.LocalCut is not { } localCut) continue;
                    var proof = SectionHatchLocalCut.Prove(localCut, start, end);
                    localVerdicts?.Add(new(localCut, span.From, span.To, proof.Coverage, proof.Evidence, proof.Reason));
                    if (proof.Coverage == SectionHatchLocalCut.Coverage.Full)
                        full.Add((source.Label, proof.Evidence, LocalCutEvidenceSource));
                    else if (proof.Coverage == SectionHatchLocalCut.Coverage.Partial)
                        partial.Add((source.Label, proof.Evidence));
                    continue;
                }
                var coverage = SectionRegionCoverageLogic.ClassifySegment(start, end, region.Loops, region.FillStyle,
                    region.FilledSeams);
                if (coverage == SectionRegionCoverageLogic.Coverage.None) continue;
                if (region.ApproximationToleranceM > 0)
                {
                    // Chords may shrink a hole. A clearance guard makes curved
                    // approximations conservative, including at span endpoints.
                    var segment = Factory.CreateLineString(new[]
                    {
                        new Coordinate(start.X, start.Y), new Coordinate(end.X, end.Y),
                    });
                    if (region.Boundaries.Any(boundary =>
                            boundary.Distance(segment) <= region.ApproximationToleranceM))
                        coverage = SectionRegionCoverageLogic.Coverage.Partial;
                }
                if (coverage == SectionRegionCoverageLogic.Coverage.Full)
                    full.Add((region.Label, region.Evidence, EvidenceSource));
                else partial.Add((region.Label, region.Evidence));
            }
            foreach (var region in full)
                results.Add(new SpanLabelOverride((span.From + span.To) / 2,
                    region.Label, region.Source, region.Evidence));

            var baseline = knownLabels.Where(label => Math.Abs(label.From - span.From) <= 1e-9 &&
                Math.Abs(label.To - span.To) <= 1e-9).Select(label => label.Label).Distinct().ToList();
            var wholeLabels = full.Select(region => region.Label).Concat(baseline).Distinct().ToList();
            var conflictingParts = partial.Where(region => wholeLabels.Any(label => label != region.Label)).ToList();
            foreach (var region in conflictingParts)
                results.Add(new SpanLabelOverride((span.From + span.To) / 2,
                    region.Label, PartialConflictSource, region.Evidence));
            // The partial label above is conflict evidence only: always retain the
            // competing whole-span label. It can never name a span by itself.
            foreach (var label in baseline.Where(label =>
                         full.Any(region => region.Label != label) ||
                         conflictingParts.Any(region => region.Label != label)))
            {
                results.Add(new SpanLabelOverride((span.From + span.To) / 2,
                    label, BaselineConflictSource, ArtifactHash.Sha256OfText(
                        FormattableString.Invariant($"{span.From:R}|{span.To:R}|{span.LeftKind}|{span.RightKind}|{label}"))));
            }
        }
        // Do not pick a winner among conflicting source labels; the common
        // presentation analysis explicitly preserves that ambiguity.
        return results.OrderBy(item => item.Offset).ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Evidence, StringComparer.Ordinal).ToList();

        P2? PointAt(double offset)
        {
            var matches = crossings.Where(mark => Math.Abs(mark.Offset - offset) <= 1e-8 &&
                    mark.WcsX is { } x && double.IsFinite(x) && mark.WcsY is { } y && double.IsFinite(y))
                .OrderBy(mark => Math.Abs(mark.Offset - offset))
                .ThenBy(mark => mark.WcsX).ThenBy(mark => mark.WcsY)
                .Select(mark => new P2(mark.WcsX!.Value, mark.WcsY!.Value)).ToList();
            if (matches.Count == 0) return null;
            var selected = matches[0];
            // Coincident CAD intersections can differ by sub-micrometre rounding.
            // Retain an actual source point, never average distinct endpoints.
            return matches.All(point => Math.Abs(point.X - selected.X) <= 1e-7 &&
                                        Math.Abs(point.Y - selected.Y) <= 1e-7)
                ? selected : null;
        }
    }
}
