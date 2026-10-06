using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Policy = MahodAI.CivilDelivery.Shared.SectionSampleLineStyleLogic;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionSampleLineStyleContractTests
{
    private static string Source(string name)
    {
        var root = typeof(SectionSampleLineStyleContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", name));
    }

    [Fact]
    public void Native75To76StyleEnumerationDriftDoesNotChangeManagedInputFingerprint()
    {
        var record = new SectionPlanRecord
        {
            RecordId = "cl-7C8F", SectionId = "STA-42676", SelectedAlignment = "2000",
            Station = 42675.8492,
            Cl = new ClSourceRecord
            {
                RecordId = "cl-7C8F", SourceDrawing = "CL.dwg",
                SourceEntityType = "LWPOLYLINE", SourceLayer = "GFC111",
                SourceEndpoints = new[] { 203490.13000870423, 649514.5215541004,
                    203587.0593272824, 649492.7969889605 },
                SourceDrawingHash = "same-dwg", SourceHandle = "7C8F",
                WcsEndpoints = new[] { 203507.8647477932, 649510.5467041765,
                    203568.36383101201, 649496.987171379 },
            },
        };
        var owned = new Policy.OwnedStyle(Policy.OwnedState.Readable, "Standard");
        record.PlannedStyles["sample_line_style"] = Policy.Resolve(null,
            new[] { "Standard", "Road Sample Lines" }, owned).Name!;
        var before = SectionPlanLogic.ComputeFingerprint(record);
        record.PlannedStyles["sample_line_style"] = Policy.Resolve(null,
            new[] { "Road Sample Lines", "Standard" }, owned).Name!;
        SectionPlanLogic.ComputeFingerprint(record).Should().Be(before);
        record.PlannedStyles["sample_line_style"] = Policy.Resolve("Road Sample Lines",
            new[] { "Road Sample Lines", "Standard" }, owned).Name!;
        SectionPlanLogic.ComputeFingerprint(record).Should().NotBe(before,
            "an actual explicit project decision must still invalidate the old managed fingerprint");
    }

    [Fact]
    public void PlanReadsExactOwnedNativeStyleRatherThanHistoricalPlanOrFirstAvailable()
    {
        Source("SectionPlanService.cs").Should()
            .Contain("ReadOwnedSampleLineStyle(tr, db, record, profile.ProfileId,")
            .And.Contain("var styleId = sampleLine.StyleId;")
            .And.Contain("db.GetObjectId(false, handle, 0)")
            .And.Contain("meta.InputFingerprint, evidence.InputFingerprint")
            .And.Contain("lines.Count != 1 || views.Count > 1")
            .And.Contain("owned-sample-line-style-read-failed")
            .And.NotContain("Resolve(\"sample_line_style\",  styles.SampleLineStyle");
    }

    [Fact]
    public void ApplyAssignsOnlyNewManagedSampleLineAndProvesNativeReadback()
    {
        var source = Source("SectionApplyService.cs");
        source.Should().Contain("ApplyPlannedSampleLineStyle(tr, civilDoc, sampleLine, record);")
            .And.Contain("sampleLine.StyleId = expectedId;")
            .And.Contain("var actualId = sampleLine.StyleId;")
            .And.Contain("actualId != expectedId")
            .And.Contain("SectionSampleLineStyleLogic.Matches(styleName, actualStyle?.Name)");
        source.IndexOf("ApplyPlannedSampleLineStyle(tr, civilDoc, sampleLine, record);")
            .Should().BeGreaterThan(source.IndexOf("CivilDb.SampleLine.Create(slName, slgId, points)"));
    }

    [Fact]
    public void VerifyRequiresManagedSampleLineStyleButDoesNotRestyleManualSections()
    {
        var source = Source("SectionVerifyService.cs");
        var start = source.IndexOf("if (!isManualReuse)",
            source.IndexOf("Check(\"sample_line_exists\""));
        var end = source.IndexOf("var sectionChildrenReadable", start);
        var block = source[start..end];
        block.Should().Contain("var styleId = sl.StyleId;")
            .And.Contain("Check(\"sample_line_style\"")
            .And.Contain("SectionSampleLineStyleLogic.Matches(expectedSampleLineStyle, actualSampleLineStyle)")
            .And.NotContain("sl.StyleId =");
    }
}
