using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Synthetic, metre-based source evidence for approval workflow tests, not native evidence.
/// The CL is (-30,0)..(30,0); a northbound alignment crosses it exactly at (0,0).
/// Capture before opening a review, after constructing its source spans. Never manufacture
/// a PhysicalDecisionKey: the production approval service must derive that itself.
/// </summary>
internal static class SectionSpanPhysicalFixture
{
    internal static void Capture(SectionPlanRecord record,
        SectionProjectionLogic.PresentationAnalysis? preManual = null)
    {
        record.Station ??= 100;
        record.SelectedCrossing = new AlignmentCrossing
        {
            AlignmentName = record.SelectedAlignment!, Point = new[] { 0d, 0d },
            Station = record.Station.Value, TangentDeg = 90, GapDistance = 0,
        };
        if (!record.Cl.SourceEndpoints.SequenceEqual(new[] { -30d, 0d, 30d, 0d }) ||
            !record.Cl.WcsEndpoints.SequenceEqual(record.Cl.SourceEndpoints))
            throw new InvalidOperationException("This fixture requires the declared exact synthetic CL.");

        if (preManual == null)
        {
            var bounds = record.PresentationCoverage.UnresolvedSpans
                .SelectMany(span => new[] { span.FromOffsetM, span.ToOffsetM })
                .Concat(record.PresentationCoverage.ResolvedSpans
                    .SelectMany(span => new[] { span.FromOffsetM, span.ToOffsetM }))
                .Distinct().OrderBy(value => value).ToArray();
            if (bounds.Length < 2 || bounds.Any(value => !double.IsFinite(value) || Math.Abs(value) > 30))
                throw new InvalidOperationException("Supply measured synthetic boundaries inside the CL first.");
            var marks = new List<SectionProjectionLogic.PresentationMark>();
            marks.AddRange(bounds.Select(offset => Mark(record, offset, "curb", "")));
            marks.AddRange(record.PresentationCoverage.ResolvedSpans.Select(span =>
                Mark(record, (span.FromOffsetM + span.ToOffsetM) / 2, "strip", span.Label)));
            preManual = SectionProjectionLogic.AnalyzePresentationCoverage(marks);
        }
        record.PreManualSpanEvidenceDigest = preManual.Summary.EvidenceDigest;
    }

    private static SectionProjectionLogic.PresentationMark Mark(SectionPlanRecord record,
        double offset, string kind, string label)
    {
        var identity = "synthetic-source:" + record.Cl.SourceHandle + ":" + kind + ":" +
            offset.ToString("R", CultureInfo.InvariantCulture);
        return new(offset, kind, label, identity)
        {
            SourceEvidence = new(identity, kind, label, "SYNTHETIC-" + kind, null,
                identity, record.Cl.SourceDrawing, record.Cl.SourceDrawingHash, -offset, 0, 0),
        };
    }
}
