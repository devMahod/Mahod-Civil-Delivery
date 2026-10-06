using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionMeasuredLabelLayoutSourceContractTests
{
    private static string Service(string name)
    {
        var root = typeof(SectionMeasuredLabelLayoutSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", name));
    }

    [Fact]
    public void ActualApplyMeasuresAfterNativeAlignmentAndBeforeOwnershipFingerprints()
    {
        var source = Service("SectionDecorationService.cs");
        var append = source.IndexOf("btr.AppendEntity(ent)", StringComparison.Ordinal);
        var align = source.IndexOf("justified.AdjustAlignment(db)", StringComparison.Ordinal);
        var measure = source.IndexOf("var measuredLayout = SectionAnnotationPlacementContract.ComputeLabelLayout(", StringComparison.Ordinal);
        var move = source.IndexOf("label.TransformBy(Matrix3d.Displacement(expected - current))", StringComparison.Ordinal);
        var readback = source.IndexOf("SectionAnnotationPlacementContract.RequirePlacedLabelBounds(measuredLayout, created)", StringComparison.Ordinal);
        var persist = source.IndexOf("SectionAnnotationRegistry.Record(", StringComparison.Ordinal);
        append.Should().BeGreaterThan(0);
        align.Should().BeGreaterThan(append);
        measure.Should().BeGreaterThan(align);
        move.Should().BeGreaterThan(measure);
        readback.Should().BeGreaterThan(move);
        persist.Should().BeGreaterThan(readback);
        source.Should().Contain("foreach (var stem in measuredLayout.Leaders)")
            .And.Contain("handles.Add(leader.Handle)")
            .And.Contain("existing-ground-at-axis|elevation=");
    }

    [Fact]
    public void VerificationRecomputesFromPlanAndLiveGround_NotSavedAbsolutePositions()
    {
        var contract = Service("SectionAnnotationPlacementContract.cs");
        contract.Should().Contain("SectionCorePresentationContract.ExpectedFor(record, record.PresentationCoverage)")
            .And.Contain("SectionAnnotationContractLogic.TryVerifySlopeEvidence(surfaces.Design")
            .And.Contain("record.PresentationCoverage.ResolvedSpans.Where")
            .And.Contain("matchingSpans[0].FromOffsetM, matchingSpans[0].ToOffsetM, slope, out live")
            .And.Contain("SlopePosition(view, live.FromOffset, live.ToOffset,")
            .And.Contain("anchor.X - actualAnchor.X, anchor.Y - actualAnchor.Y")
            .And.Contain("entity.GeometricExtents")
            .And.Contain("SectionAnnotationPlacementLogic.TryLayoutLabels")
            .And.Contain("Native text bounds differ from measured layout")
            .And.NotContain("text.TextString.Length");
        var verify = Service("SectionVerifyService.cs");
        verify.Should().Contain("ComputeLabelLayout(")
            .And.Contain("RequirePlacedLabelBounds(")
            .And.Contain("RequireLayoutLeaders(")
            .And.Contain("native_measured_label_layout_exact");
    }
}
