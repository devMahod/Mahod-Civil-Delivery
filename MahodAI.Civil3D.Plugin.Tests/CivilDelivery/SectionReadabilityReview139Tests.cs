using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Independent audit of 1.3.9 (30/09), section findings fixed in code:
/// SEC-B3 content band, SEC-B4 closed chain, SEC-M2 located datum, SEC-M3 utility
/// marker/label, SEC-m3 title + legend, SEC-B1 ROW disclosure. PLAN-derived core
/// inventory is exercised at runtime; host code is pinned by source contracts.
/// </summary>
public sealed class SectionReadabilityReview139Tests
{
    private static string Service(string name)
    {
        var root = typeof(SectionReadabilityReview139Tests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", name));
    }

    private static SectionPlanRecord Record(bool withUtility = true)
    {
        var record = new SectionPlanRecord
        {
            RecordId = "cl-7C8F",
            SectionId = "STA-42676",
            SelectedAlignment = "2000",
            Station = 42675.85,
            Cl = new ClSourceRecord
            {
                RecordId = "cl-7C8F",
                SourceDrawing = "CL.dwg",
                SourceDrawingHash = "hash-cl",
                SourceHandle = "7C8F",
                SourceEntityType = "LINE",
                SourceLayer = "CL",
                SourceEndpoints = new[] { 0.0, 10.0, 0.0, -10.0 },
                WcsEndpoints = new[] { 0.0, 10.0, 0.0, -10.0 },
            },
        };
        // Marks: curb face -6.5/-6.3, named -6.3..-3.1, curb pieces -3.1/-2.9/-2.8,
        // named -2.8..0.2, island nose 0.2..0.5.
        var offsets = new[] { -6.5, -6.3, -3.1, -2.9, -2.8, 0.2, 0.5 };
        var coverage = record.PresentationCoverage;
        foreach (var offset in offsets)
        {
            coverage.DimensionOffsets.Add(offset);
            coverage.DimensionMarks.Add(new SectionDimensionMarkPlan
            {
                OffsetM = offset, Kind = "curb", Label = "אבן שפה", ColorIndex = 3,
            });
        }
        coverage.DimensionMarkCount = offsets.Length;
        coverage.ResolvedSpans.Add(Span(-6.3, -3.1, "מדרכה"));
        coverage.ResolvedSpans.Add(Span(-2.8, 0.2, "נתיב נסיעה"));
        coverage.WidthSpanCount = 2;
        coverage.NamedStripCount = 2;
        if (withUtility)
            record.ProjectedEntities.Add(new ProjectedEntityPlan
            {
                ProjectionKey = "k", SystemLabel = "חשמל", SourceLayer = "UT|EL",
                SourceHandle = "1A", IntersectionWcs = new[] { 0.0, 0.0 },
            });
        return record;

        static SectionResolvedSpanPlan Span(double from, double to, string label) => new()
        {
            FromOffsetM = from, ToOffsetM = to, WidthM = to - from,
            LeftKind = "curb", RightKind = "curb", Label = label,
            EvidenceSource = "manual-profile", EvidenceDigest = new string('a', 64),
        };
    }

    [Fact]
    public void CoreInventory_ClosesTheWidthChain_AndCarriesOverallWidthAndChainLine()
    {
        var record = Record();
        var expected = SectionCorePresentationContract.ExpectedFor(record, record.PresentationCoverage);

        var gaps = expected.Where(e => e.Kind == SectionCorePresentationContract.GapWidthLabel).ToList();
        gaps.Select(g => g.Text).Should().Equal("0.20", "0.30", "0.30");
        var named = expected.Where(e => e.Kind == SectionCorePresentationContract.WidthLabel).ToList();
        var total = gaps.Concat(named).Sum(e => e.To!.Value - e.From!.Value);
        total.Should().BeApproximately(7.0, 1e-9, "every piece between the outer marks is dimensioned");
        expected.Should().ContainSingle(e => e.Kind == SectionCorePresentationContract.OverallWidthLabel)
            .Which.Text.Should().Be("רוחב כולל 7.00");
        expected.Should().ContainSingle(e => e.Kind == SectionCorePresentationContract.DimensionChainLine)
            .Which.ColorIndex.Should().Be((short)8);
        expected.Select(e => e.SemanticKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CoreInventory_TitleNamesAlignmentAndChainage_AndLegendIsDrawn()
    {
        var record = Record();
        var expected = SectionCorePresentationContract.ExpectedFor(record, record.PresentationCoverage);

        expected.Should().ContainSingle(e => e.Kind == SectionCorePresentationContract.Title)
            .Which.Text.Should().Be("חתך STA-42676 · תוואי 2000 · 42+675.85");
        expected.Should().ContainSingle(e => e.Kind == SectionCorePresentationContract.LegendSurfaces)
            .Which.Text.Should().Be(SectionDrawingTextLogic.LegendSurfaces);
        expected.Should().ContainSingle(e => e.Kind == SectionCorePresentationContract.LegendUtilities)
            .Which.Text.Should().Contain("לא קוטר");

        var withoutUtilities = Record(withUtility: false);
        SectionCorePresentationContract.ExpectedFor(withoutUtilities, withoutUtilities.PresentationCoverage)
            .Should().NotContain(e => e.Kind == SectionCorePresentationContract.LegendUtilities);
    }

    [Fact]
    public void CoreInventory_RefusesAChainThatDoesNotClose()
    {
        var record = Record();
        record.PresentationCoverage.ResolvedSpans[0] = new SectionResolvedSpanPlan
        {
            FromOffsetM = -6.25, ToOffsetM = -3.1, WidthM = 3.15,
            LeftKind = "curb", RightKind = "curb", Label = "מדרכה",
            EvidenceSource = "manual-profile", EvidenceDigest = new string('a', 64),
        };
        var act = () => SectionCorePresentationContract.ExpectedFor(record, record.PresentationCoverage);
        act.Should().Throw<InvalidOperationException>().WithMessage("*dimension chain does not close*");
    }

    [Fact]
    public void Decoration_DrawsEveryNewCoreRowFromThePlanInventory()
    {
        var decoration = Service("SectionDecorationService.cs");
        decoration.Should().Contain("SectionCorePresentationContract.TitleText(record)")
            .And.Contain("item.Kind is SectionCorePresentationContract.GapWidthLabel")
            .And.Contain("SectionAnnotationPlacementContract.DimensionChainLine(")
            .And.Contain("SectionAnnotationPlacementContract.OverallWidthLabelPosition(")
            .And.Contain("SectionAnnotationPlacementContract.LegendPosition(")
            .And.NotContain("var titleText = $\"חתך {sectionTitle}\"");
        var placement = Service("SectionAnnotationPlacementContract.cs");
        placement.Should().Contain("SectionCorePresentationContract.GapWidthLabel => (1,")
            .And.Contain("SectionCorePresentationContract.OverallWidthLabel => (3,")
            .And.Contain("SectionCorePresentationContract.LegendUtilities => (5,")
            .And.Contain("SectionCorePresentationContract.LegendSurfaces => (6,")
            .And.Contain("SectionCorePresentationContract.Title => (7,");
        var verify = Service("SectionVerifyService.cs");
        verify.Should().Contain("case SectionCorePresentationContract.GapWidthLabel:")
            .And.Contain("case SectionCorePresentationContract.DimensionChainLine:")
            .And.Contain("case SectionCorePresentationContract.OverallWidthLabel:")
            .And.Contain("case SectionCorePresentationContract.LegendSurfaces:")
            .And.Contain("case SectionCorePresentationContract.LegendUtilities:");
    }

    [Fact]
    public void DatumAndUtilityTexts_SayWhatTheyAre()
    {
        var decoration = Service("SectionDecorationService.cs");
        var verify = Service("SectionVerifyService.cs");
        decoration.Should().Contain("SectionDrawingTextLogic.DatumText(datumElevation)")
            .And.NotContain("$\"רום קיים {datumElevation:F2}\"")
            .And.Contain("new Circle(zp, Vector3d.ZAxis, SectionDrawingTextLogic.UtilityMarkerRadius)")
            .And.NotContain("new Circle(zp, Vector3d.ZAxis, 0.6)")
            .And.Contain("SectionDrawingTextLogic.UtilityLabel(c.Rule.Label, z)")
            .And.Contain("Brighten(text, bottomTexts[i].ColorIndex)");
        verify.Should().Contain("SectionDrawingTextLogic.DatumText(datumEvidence.Elevation)")
            .And.NotContain("$\"רום קיים {datumEvidence.Elevation:F2}\"");
        // The single-datum gate counts texts that start with "רום "; the new
        // utility label must never be counted as a second reference elevation.
        SectionDrawingTextLogic.DatumText(285.35).Should().StartWith("רום ");
        SectionDrawingTextLogic.UtilityLabel("תאורה", 284.27).Should().NotStartWith("רום ");
    }

    [Theory]
    [InlineData("plan-mark-extents", "authoritative", "זכות דרך לא נמצאה במקור המאושר")]
    [InlineData("plan-mark-extents", "suppressed", "טרם אושר")]
    [InlineData("row-incomplete", "authoritative", "בצד אחד בלבד")]
    public void RowDisclosure_IsShownWheneverTheSectionDoesNotEndAtARowLine(
        string boundary, string authority, string expected)
    {
        var record = Record();
        record.PresentationCoverage.BoundarySource = boundary;
        record.PresentationCoverage.RowAuthorityState = authority;
        new SectionRowViewModel { Record = record }.Boundary.Should().Contain(expected);
    }

    [Fact]
    public void RowDisclosure_IsSilentForATwoSidedRowEnvelope()
    {
        var record = Record();
        record.PresentationCoverage.BoundarySource = "row";
        record.PresentationCoverage.RowAuthorityState = "authoritative";
        new SectionRowViewModel { Record = record }.Boundary.Should().BeNull();
        var control = File.ReadAllText(Path.Combine(typeof(SectionReadabilityReview139Tests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!,
            "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
        control.Should().Contain("if (row.Boundary is { } boundary)");
    }

    [Fact]
    public void BusLaneEdgeSpan_ExplainsWhyItNeedsANameInTheDialog()
    {
        var model = File.ReadAllText(Path.Combine(typeof(SectionReadabilityReview139Tests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!,
            "CivilDelivery", "UI", "SpanLabelDecisionModel.cs"));
        model.Should().Contain("SectionProjectionLogic.BusLaneLineEdgeReason =>");
        SpanLabelDecisionModel.LabelChoices.Should().Contain("נת\"צ");
    }
}
