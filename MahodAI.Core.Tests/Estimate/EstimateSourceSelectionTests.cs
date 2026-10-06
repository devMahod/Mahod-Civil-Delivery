using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class EstimateSourceSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_scope_" + Guid.NewGuid().ToString("N"));
    private static EstimateSourceInventory Inventory() => new("51fbc96a-2b2d-4984-9e19-2c03e1451463", @"C:\test\host.dwg", new[]
    {
        new EstimateSourceDefinition("host", "host.dwg", @"C:\test\host.dwg", "host", true),
        new EstimateSourceDefinition(EstimateSourceSelectionPolicy.XrefKey("HW-SIMUN", "mark.dwg", "AB"), "HW-SIMUN", "mark.dwg", "Resolved"),
        new EstimateSourceDefinition(EstimateSourceSelectionPolicy.XrefKey("UNKNOWN", "missing.dwg", "AC"), "UNKNOWN", "missing.dwg", "Unloaded"),
        new EstimateSourceDefinition(EstimateSourceSelectionPolicy.XrefKey("HW-SIMUN|child", "child.dwg", "AD"), "HW-SIMUN|child", "child.dwg", "Unresolved", IsNested: true),
    });
    private static EstimateSourceSelection Selection()
    {
        var draft = EstimateSourceSelectionPolicy.CreateDraft(Inventory(), null);
        draft[1].Included = false;
        draft[2].Category = "landscape";
        return EstimateSourceSelectionPolicy.Approve(Inventory(), draft, "TEST-ONLY engineer", DateTime.UtcNow);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Theory]
    [InlineData("HW-SIMUN", "markings")]
    [InlineData("LANDSCAPE-2026", "landscape")]
    [InlineData("SIMUN-DRAINAGE", "unclassified")]
    [InlineData("SIMUNX", "unclassified")]
    [InlineData("HW-UNKNOWN", "roads")]
    [InlineData("SR-Eilat_TH-MHD", "survey")]
    [InlineData("SV-survey", "survey")]
    [InlineData("SM-PD-293", "markings")]
    [InlineData("HW-NIKUZ", "drainage")]
    [InlineData("DR-PD-293", "drainage")]
    [InlineData("EL-TEURA", "utilities")]
    [InlineData("WTR-SD", "utilities")]
    [InlineData("LA-NOF-GN", "landscape")]
    [InlineData("RD-PD", "roads")]
    [InlineData("SR-ARCH", "unclassified")]
    [InlineData("SM-NIKUZ", "unclassified")]
    [InlineData("TR-Rampa-M30", "unclassified")]
    [InlineData("UT-PD", "unclassified")]
    [InlineData("SRX-NOFA-LAX-DRIVE", "unclassified")]
    public void NameIsOnlyAnUnambiguousSuggestion(string name, string category) =>
        EstimateSourceSelectionPolicy.SuggestCategory(name).Should().Be(category);

    [Fact]
    public void Project984DefinitionNames_AreSuggestionsOnly_AndAllEightRemainIncluded()
    {
        // Names/statuses from the owner's local 984 inventory, not a native scan by this test.
        var expected = new[]
        {
            ("ETCH-AR-MIFLAS-2.40", "architecture", "Resolved"),
            ("HW-MAAR-EV-CD", "roads", "Unloaded"),
            ("SR-Eilat_TH-MHD", "survey", "Unloaded"),
            ("TR-FloorGrd-GM-PD-M30", "roads", "Resolved"),
            ("TR-FloorGrd-GM-SD-M30", "roads", "Unloaded"),
            ("TR-FloorGrd-HA-PD", "unclassified", "Unloaded"),
            ("TR-Rampa-M30", "unclassified", "Unloaded"),
            ("TR-Rampa-M30-new", "unclassified", "Unloaded"),
        };
        var inventory = Inventory() with { Sources = new[] { Inventory().Sources[0] }.Concat(
            expected.Select((e, i) => new EstimateSourceDefinition(
                EstimateSourceSelectionPolicy.XrefKey(e.Item1, e.Item1 + ".dwg", (i + 10).ToString("X")),
                e.Item1, e.Item1 + ".dwg", e.Item3))).ToArray() };
        var draft = EstimateSourceSelectionPolicy.CreateDraft(inventory, null);
        draft.Should().HaveCount(9).And.OnlyContain(s => s.Included);
        foreach (var e in expected) draft.Single(s => s.Name == e.Item1).Category.Should().Be(e.Item2);
        var saved = EstimateSourceSelectionPolicy.Approve(inventory, draft, "SYNTHETIC TEST ONLY", DateTime.UtcNow);
        saved.Sources.Single(s => s.Name == "SR-Eilat_TH-MHD").Category = "landscape";
        saved.Sources.Single(s => s.Name == "SR-Eilat_TH-MHD").Included = false;
        var reopened = EstimateSourceSelectionPolicy.CreateDraft(inventory, saved);
        reopened.Single(s => s.Name == "SR-Eilat_TH-MHD").Category.Should().Be("landscape", "explicit edits override name suggestions");
        reopened.Single(s => s.Name == "SR-Eilat_TH-MHD").Included.Should().BeFalse();
    }

    [Fact]
    public void DefaultIncludesHostUnknownAndUnloaded_NestedInheritsBranch()
    {
        var draft = EstimateSourceSelectionPolicy.CreateDraft(Inventory(), null);
        draft.Should().HaveCount(3).And.OnlyContain(s => s.Included);
        draft.Single(s => s.Name == "HW-SIMUN").Category.Should().Be("markings");
    }

    [Fact]
    public void CancelAndCategoryEditsDoNotMutateTheProfileOrTheCheck()
    {
        var saved = Selection();
        var before = JsonSerializer.Serialize(saved);
        var draft = EstimateSourceSelectionPolicy.CreateDraft(Inventory(), saved);
        draft[1].Category = "drainage";
        draft[1].Included.Should().BeFalse();
        draft[2].Included = false;
        JsonSerializer.Serialize(saved).Should().Be(before);
    }

    [Fact]
    public void NewOrReplacedSourceDefaultsToIncludedAndRequiresReview()
    {
        var saved = Selection();
        var inventory = Inventory();
        var sources = inventory.Sources.ToArray();
        sources[1] = sources[1] with { Key = EstimateSourceSelectionPolicy.XrefKey("HW-SIMUN", "mark.dwg", "FF") };
        var changed = inventory with { Sources = sources };
        EstimateSourceSelectionPolicy.MatchProblems(changed, saved).Should().NotBeEmpty();
        EstimateSourceSelectionPolicy.CreateDraft(changed, saved)[1].Included.Should().BeTrue();
    }

    [Fact]
    public void ChangedNestedSourceOrStatusInvalidatesScopeWithoutDiscardingExplicitChoices()
    {
        var inventory = Inventory();
        var changed = inventory with { Sources = inventory.Sources.Select(s => s.IsNested ? s with { Status = "Resolved" } : s).ToArray() };
        EstimateSourceSelectionPolicy.MatchProblems(changed, Selection()).Should().NotBeEmpty();
        EstimateSourceSelectionPolicy.CreateDraft(changed, Selection())[1].Included.Should().BeFalse();
    }

    [Fact]
    public void ANewHostDoesNotInheritOldExclusions()
    {
        var other = Inventory() with { HostFingerprint = Guid.NewGuid().ToString() };
        EstimateSourceSelectionPolicy.CreateDraft(other, Selection()).Should().OnlyContain(s => s.Included);
    }

    [Fact]
    public void DefinitionOrderDoesNotChangeInventoryHash() =>
        EstimateSourceSelectionPolicy.InventoryHash(Inventory() with { Sources = Inventory().Sources.Reverse().ToArray() })
            .Should().Be(EstimateSourceSelectionPolicy.InventoryHash(Inventory()));

    [Fact]
    public void EmptyScopeAndDuplicateChoicesCannotBeApproved()
    {
        var draft = EstimateSourceSelectionPolicy.CreateDraft(Inventory(), null);
        draft.ForEach(s => s.Included = false);
        Action empty = () => EstimateSourceSelectionPolicy.Approve(Inventory(), draft, "TEST", DateTime.UtcNow);
        empty.Should().Throw<InvalidOperationException>();
        draft[0].Included = true;
        draft.Add(draft[0]);
        Action duplicate = () => EstimateSourceSelectionPolicy.Approve(Inventory(), draft, "TEST", DateTime.UtcNow);
        duplicate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ExclusionNoticeDisclosesNamesAndUnknownCounts()
    {
        EstimateSourceSelectionPolicy.ScopeSummary(Selection()).Should().Contain("HW-SIMUN").And.Contain("לא ידוע");
    }

    [Fact]
    public void RealWriterAndLoaderPreserveSelectionsOverridesOtherDomainsAndEffectiveHash()
    {
        Directory.CreateDirectory(_dir);
        var profile = new ProjectProfile { ProfileId = "TEST-SCOPE", ProjectName = "SYNTHETIC ONLY" };
        profile.Estimate.SourceSelection = Selection();
        profile.Estimate.IgnoredRuleKeys.Add("unchanged-test-rule");
        var path = Path.Combine(_dir, "project-profile.yaml");
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        ProjectProfileWriter.Save(profile, path, "TEST-ONLY source scope", "TEST engineer", expected);
        profile.SchemaVersion.Should().Be(10);
        var reopened = ProjectProfileLoader.LoadFromFile(path);
        reopened.IsUsable.Should().BeTrue(string.Join(";", reopened.Findings.Select(f => f.Code)));
        reopened.Profile!.Estimate.SourceSelection!.Sources[1].Included.Should().BeFalse();
        reopened.Profile.Estimate.SourceSelection.Sources[2].Category.Should().Be("landscape");
        reopened.Profile.Estimate.IgnoredRuleKeys.Should().Equal("unchanged-test-rule");
        EstimateTraceIdentity.EffectiveProfileHash(reopened.Profile).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
        ProjectProfileLoader.LoadFromText(File.ReadAllText(path).Replace("schema_version: 10", "schema_version: 9"))
            .IsUsable.Should().BeFalse();
    }

    [Fact]
    public void LegacyProfileOmitsNewFieldAndDoesNotNeedSchema10()
    {
        var profile = new ProjectProfile();
        JsonSerializer.Serialize(profile).Should().NotContain("SourceSelection");
        ProjectProfileSchemaPolicy.HasSchema10Content(profile).Should().BeFalse();
    }
}
