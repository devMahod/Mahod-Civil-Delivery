using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Shared;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Read-only source-loop inventory. Unreadable loops remain explicit; no source loop is repaired or dropped.</summary>
internal static class SectionHatchRegionReader
{
    internal const int MaximumLoops = 256;
    internal const int MaximumVertices = 100000;

    internal static SectionHatchSpanLabelService.Region Read(
        Hatch hatch, Matrix3d transform, string layer, string? xref,
        string handlePath, string? drawingPath, string? drawingHash, ProjectionRuleMatch rule)
    {
        if (!SectionHatchSpanLabelService.IsSupportedSemanticRule(rule))
            throw new InvalidOperationException("The classified hatch role/label is not a supported semantic area.");
        if (hatch.NumberOfLoops < 1 || hatch.NumberOfLoops > MaximumLoops)
            throw new InvalidOperationException("Hatch loop count is empty or exceeds the bounded reader limit.");
        var fillStyle = hatch.HatchStyle switch
        {
            HatchStyle.Normal => SectionRegionCoverageLogic.FillStyle.Normal,
            HatchStyle.Outer => SectionRegionCoverageLogic.FillStyle.Outer,
            HatchStyle.Ignore => SectionRegionCoverageLogic.FillStyle.Ignore,
            _ => throw new InvalidOperationException("Unsupported hatch fill style."),
        };
        var sourceLoops = new List<SectionHatchSpanLabelService.SourceLoop>();
        var totalVertices = 0;
        // b7: the raw native edges of a single-loop hatch, kept only for the
        // separate local cut proof. Samples below stay the polygon path's input.
        SectionHatchLocalCut.RawLoop? localCutRaw = null;
        for (var loopIndex = 0; loopIndex < hatch.NumberOfLoops; loopIndex++)
        {
            var loop = hatch.GetLoopAt(loopIndex);
            var unsupported = HatchLoopTypes.NotClosed | HatchLoopTypes.SelfIntersecting |
                              HatchLoopTypes.Duplicate | HatchLoopTypes.Textbox | HatchLoopTypes.TextIsland;
            var points = new List<P2>();
            var straightEdges = new List<SectionHatchBoundaryGeometry.StraightEdge>();
            var curved = false;
            double[]? bounds = null;
            var failure = (loop.LoopType & unsupported) != 0
                ? $"Hatch loop {loopIndex} has unsupported flags {loop.LoopType}." : null;
            try
            {
            if (loop.IsPolyline)
            {
                var raw = new List<SectionHatchBoundaryGeometry.BulgePoint>();
                foreach (BulgeVertex vertex in loop.Polyline)
                {
                    if (raw.Count >= MaximumVertices)
                        throw new InvalidOperationException("Hatch source vertices exceed the bounded reader limit.");
                    var point = vertex.Vertex;
                    raw.Add(new(point.X, point.Y, vertex.Bulge));
                }
                // Read copied OCS scalars directly. A transient Polyline's native
                // point getters/preflight reported zero chords for the 12 HA
                // fixtures whose stored bulged chords are all strictly positive.
                var boundary = SectionHatchBoundaryGeometry.TessellateClosedPolyline(
                    raw, SectionGeometryCollector.MaxLinearScale(transform),
                    SectionGeometryCollector.MaxCurveSagittaM, MaximumVertices - totalVertices);
                curved |= boundary.HasCurves;
                var ocsToWorld = Matrix3d.PlaneToWorld(hatch.Normal);
                foreach (var point in boundary.Points)
                {
                    var world = new Point3d(point.X, point.Y, hatch.Elevation)
                        .TransformBy(ocsToWorld).TransformBy(transform);
                    points.Add(new P2(world.X, world.Y));
                }
                foreach (var edge in boundary.SourceStraightEdges)
                    straightEdges.Add(new(TransformPoint(edge.From), TransformPoint(edge.To)));
                bounds = ConservativeBounds(points, curved ? SectionGeometryCollector.MaxCurveSagittaM : 0);

                P2 TransformPoint(P2 point)
                {
                    var world = new Point3d(point.X, point.Y, hatch.Elevation)
                        .TransformBy(ocsToWorld).TransformBy(transform);
                    return new(world.X, world.Y);
                }
            }
            else
            {
                var ocsToWorld = Matrix3d.PlaneToWorld(hatch.Normal);
                var edgeSamples = new List<IReadOnlyList<P2>>();
                var edgeSampleCount = 0;
                var rawEdges = new List<SectionHatchLocalCut.RawEdge>();
                foreach (Curve2d edge in loop.Curves)
                {
                    var samples = new List<P2>();
                    switch (edge)
                    {
                        case LineSegment2d line:
                            rawEdges.Add(new(SectionHatchLocalCut.EdgeKind.Line,
                                line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y));
                            samples.Add(ToWorld(line.StartPoint));
                            samples.Add(ToWorld(line.EndPoint));
                            straightEdges.Add(new(samples[0], samples[1]));
                            break;
                        case CircularArc2d arc:
                            curved = true;
                            rawEdges.Add(new(SectionHatchLocalCut.EdgeKind.Arc,
                                arc.StartPoint.X, arc.StartPoint.Y, arc.EndPoint.X, arc.EndPoint.Y)
                            {
                                CenterX = arc.Center.X, CenterY = arc.Center.Y, Radius = arc.Radius,
                                StartAngle = arc.StartAngle, EndAngle = arc.EndAngle, Clockwise = arc.IsClockWise,
                                ReferenceX = arc.ReferenceVector.X, ReferenceY = arc.ReferenceVector.Y,
                            });
                            var interval = arc.GetInterval();
                            var sweep = Math.Abs(arc.EndAngle - arc.StartAngle);
                            var count = SectionHatchBoundaryGeometry.ArcSegmentCount(
                                arc.Radius * SectionGeometryCollector.MaxLinearScale(transform), sweep,
                                SectionGeometryCollector.MaxCurveSagittaM);
                            if (count + 1 + edgeSampleCount + totalVertices > MaximumVertices)
                                throw new InvalidOperationException("Hatch tessellation exceeds the bounded reader limit.");
                            for (var sample = 0; sample <= count; sample++)
                                samples.Add(ToWorld(sample == 0 ? arc.StartPoint :
                                    sample == count ? arc.EndPoint :
                                    arc.EvaluatePoint(interval.LowerBound +
                                        (interval.UpperBound - interval.LowerBound) * sample / count)));
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Hatch loop {loopIndex} edge {edge.GetType().Name} is not a supported line/circular arc.");
                    }
                    edgeSamples.Add(samples);
                    edgeSampleCount += samples.Count;
                    if (edgeSampleCount + totalVertices > MaximumVertices)
                        throw new InvalidOperationException("Hatch tessellation exceeds the bounded reader limit.");
                }
                // This envelope includes every raw edge sample, including the
                // directed endpoints of disconnected/flagged loops. Sagitta is
                // bounded in WCS by MaxLinearScale; the padding covers the arcs
                // between samples. Unknown edge types leave bounds unknown.
                bounds = ConservativeBounds(edgeSamples.SelectMany(edge => edge),
                    curved ? SectionGeometryCollector.MaxCurveSagittaM : 0);
                points.AddRange(SectionHatchBoundaryGeometry.JoinClosedEdgesWithEndpointTolerance(
                    edgeSamples, MaximumVertices - totalVertices,
                    SectionHatchBoundaryGeometry.MicrometricEndpointToleranceM));
                if (hatch.NumberOfLoops == 1 && failure == null)
                    localCutRaw = new(rawEdges.AsReadOnly(), loop.Curves.Count, hatch.NumberOfLoops, loopIndex,
                        CaptureComplete: rawEdges.Count == loop.Curves.Count, IsPolyline: false,
                        LoopFlags: loop.LoopType.ToString(), HatchStyle: hatch.HatchStyle.ToString(),
                        hatch.Normal.X, hatch.Normal.Y, hatch.Normal.Z, hatch.Elevation,
                        transform.ToArray(), SourceIdentity: string.Empty);

                P2 ToWorld(Point2d point)
                {
                    var world = new Point3d(point.X, point.Y, hatch.Elevation)
                        .TransformBy(ocsToWorld).TransformBy(transform);
                    return new P2(world.X, world.Y);
                }
            }
            totalVertices += points.Count;
            if (totalVertices > MaximumVertices)
                throw new InvalidOperationException("Hatch tessellation exceeds the bounded reader limit.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                failure = failure == null ? error.Message : failure + " " + error.Message;
                localCutRaw = null;
            }
            sourceLoops.Add(new(loopIndex, points.AsReadOnly(), straightEdges.AsReadOnly(), bounds,
                failure, curved ? SectionGeometryCollector.MaxCurveSagittaM : 0));
        }
        return SectionHatchSpanLabelService.CreateSourceRegion(
            sourceLoops, fillStyle, rule.Label, layer, xref, handlePath, drawingPath, drawingHash, localCutRaw);
    }

    private static double[]? ConservativeBounds(IEnumerable<P2> points, double sagittaM)
    {
        var all = points.ToArray();
        if (all.Length == 0 || all.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return null;
        var pad = sagittaM + SectionHatchBoundaryGeometry.MicrometricEndpointToleranceM;
        var bounds = new[] { all.Min(point => point.X) - pad, all.Min(point => point.Y) - pad,
            all.Max(point => point.X) + pad, all.Max(point => point.Y) + pad };
        return SectionProjectionFailureScope.HasUsableBounds(bounds) ? bounds : null;
    }
}
