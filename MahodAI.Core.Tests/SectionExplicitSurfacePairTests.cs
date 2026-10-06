using System;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Pair = MahodAI.CivilDelivery.Shared.ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair;
using I = MahodAI.CivilDelivery.Shared.SectionSourceSelectionLogic.Identity;

namespace MahodAI.Core.Tests;

public class SectionExplicitSurfacePairTests
{
    private const string Drawing = "125b30f2-68bd-4c70-b9cb-d8c85d849501";
    private static readonly I[] Sources = { new("Terrain survey", "A1"), new("כביש מוצע", "B2") };
    private static Pair Approved() => new()
    {
        DrawingFingerprint = Drawing, AlignmentName = "Main Street", AlignmentHandle = "C3",
        ExistingName = Sources[0].Name, ExistingHandle = "A1", DesignName = Sources[1].Name,
        DesignHandle = "B2", ApprovedBy = "engineer", ApprovedAtUtc = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
    };
    private static SectionSourceSelectionLogic.ExplicitPairResult Resolve(Pair pair, I[]? sources = null,
        string drawing = Drawing, string name = "Main Street", string handle = "C3") =>
        SectionSourceSelectionLogic.SelectExplicitPair(drawing, name, handle, sources ?? Sources, new[] { pair });

    [Fact]
    public void ArbitraryNamesOnlyWorkAfterAnExplicitReviewedPair()
    {
        Assert.False(SectionSourceSelectionLogic.Select(new[] { Sources[0].Name, Sources[1].Name },
            "Main Street", new[] { Sources[0].Name }).IsReady);
        var pair = Approved(); var result = Resolve(pair);
        Assert.True(result.IsValid); Assert.Equal(pair, result.Pair);
        Assert.Equal("Terrain survey", result.Pair!.ExistingName);
        Assert.Equal("כביש מוצע", result.Pair.DesignName);
    }

    [Fact]
    public void AbsentExplicitPairPreservesLegacyAndDoesNotDemandNewApproval()
    {
        var noPair = SectionSourceSelectionLogic.SelectExplicitPair(null, "700", null, Array.Empty<I>(), Array.Empty<Pair>());
        Assert.False(noPair.IsConfigured);
        Assert.True(SectionSourceSelectionLogic.Select(new[] { "MK", "700D@Top@01" }, "700", new[] { "MK" }).IsReady);
    }

    [Theory]
    [InlineData("drawing")] [InlineData("alignment-name")] [InlineData("alignment-handle")]
    [InlineData("existing-name")] [InlineData("existing-handle")] [InlineData("same-source")]
    [InlineData("approval")] [InlineData("date")] [InlineData("empty-guid")]
    public void StaleOrIncompleteExplicitChoiceRefusesWithoutLegacyFallback(string defect)
    {
        var pair = Approved();
        pair = defect switch
        {
            "drawing" => pair with { DrawingFingerprint = Guid.NewGuid().ToString() },
            "alignment-name" => pair with { AlignmentName = "renamed" },
            "alignment-handle" => pair with { AlignmentHandle = "C4" },
            "existing-name" => pair with { ExistingName = "renamed terrain" },
            "existing-handle" => pair with { ExistingHandle = "A2" },
            "same-source" => pair with { DesignName = pair.ExistingName, DesignHandle = pair.ExistingHandle },
            "approval" => pair with { ApprovedBy = null },
            "date" => pair with { ApprovedAtUtc = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) },
            _ => pair with { DrawingFingerprint = Guid.Empty.ToString() },
        };
        var result = Resolve(pair);
        Assert.True(result.IsConfigured); Assert.False(result.IsValid); Assert.NotEmpty(result.Error!);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void NamesAndHandlesMustBothBeUnique(bool duplicateName)
    {
        var other = duplicateName ? new I(Sources[0].Name, "D4") : new I("different name", "A1");
        Assert.False(Resolve(Approved(), new[] { Sources[0], Sources[1], other }).IsValid);
    }

    [Fact]
    public void DuplicateApprovalCannotChooseFirst()
    {
        Assert.False(SectionSourceSelectionLogic.SelectExplicitPair(Drawing, "Main Street", "C3", Sources,
            new[] { Approved(), Approved() }).IsValid);
    }

    [Fact]
    public void ProfileRoundTripPreservesRolesAndSaveIndependentDrawingIdentity()
    {
        var p = new ProjectProfile { ProfileId = "another-project" };
        p.Sections.Sources.SurfacePairs.Add(Approved());
        var bytes = JsonSerializer.Serialize(p);
        var reopened = JsonSerializer.Deserialize<ProjectProfile>(bytes)!;
        Assert.True(Resolve(reopened.Sections.Sources.SurfacePairs[0]).IsValid);
        Assert.Equal(bytes, JsonSerializer.Serialize(p));
        // No drawing SHA is used: a normal save cannot revoke a role decision by itself.
        Assert.Equal(Drawing, reopened.Sections.Sources.SurfacePairs[0].DrawingFingerprint);
    }
}
