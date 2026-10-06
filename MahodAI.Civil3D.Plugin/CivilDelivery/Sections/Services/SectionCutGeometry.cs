using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>One shared PLAN/APPLY intersection and along-cut measurement path.</summary>
internal static class SectionCutGeometry
{
    internal static bool TryFrame(SectionPlanRecord record, out SectionCutFrame? frame)
    {
        frame = null;
        var e = record.Cl.WcsEndpoints;
        var crossing = record.SelectedCrossing;
        return e.Length == 4 && crossing?.Point is { Length: 2 } &&
            SectionCutFrame.TryCreate(new(e[0], e[1]), new(e[2], e[3]),
                new(crossing.Point[0], crossing.Point[1]), crossing.TangentDeg, out frame);
    }

    internal static SectionCutFrame RequireFrame(SectionPlanRecord record) =>
        TryFrame(record, out var frame) ? frame! : throw new InvalidOperationException(
            "The selected CL does not define a finite straight cut straddling its alignment crossing.");

    internal static List<Crossing> CrossingsFor(
        IEnumerable<SectionGeometryCollector.CollectedLine> lines, SectionCutFrame frame) =>
        MergeNearby(DimensionCrossingsFor(lines, frame));

    /// <summary>Raw measured crossings: display clustering may not choose a dimension face.</summary>
    internal static List<Crossing> DimensionCrossingsFor(
        IEnumerable<SectionGeometryCollector.CollectedLine> lines, SectionCutFrame frame)
    {
        // Always traverse LEFT to RIGHT. Apart from offset parity, this prevents
        // reversed source CLs choosing another representative of nearby hits.
        var a = frame.LeftEndpoint;
        var b = a == frame.EndpointA ? frame.EndpointB : frame.EndpointA;
        var dx = (b.X - a.X) / frame.Width;
        var dy = (b.Y - a.Y) / frame.Width;
        var crossings = new List<Crossing>();
        foreach (var line in lines)
        foreach (var (t, z) in IntersectPolyline(a, b, line.Verts, line.Closed))
        {
            var point = new P2(a.X + dx * t, a.Y + dy * t);
            crossings.Add(new Crossing(frame.OffsetOf(point), z, line.Layer,
                line.Xref, line.Rule, line.SourceHandle, point.X, point.Y)
            {
                SourceDrawingPath = line.SourceDrawingPath,
                SourceDrawingHash = SourceHashForEvidence(line.Xref, line.SourceDrawingHash),
            });
        }
        return crossings;
    }
}
