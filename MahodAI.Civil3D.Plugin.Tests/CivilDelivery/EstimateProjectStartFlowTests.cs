using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Positive first-profile persistence/measurement-review contract; no native scan or real approval.</summary>
public sealed class EstimateProjectStartFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MahodAI-estimate-start-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectProfile _seed;
    private readonly ProjectProfileWriter.ExpectedProfileState _expected;
    private readonly string _target;
    private const string Approver = "SYNTHETIC-START-TEST-NOT-ENGINEERING-AUTHORITY";

    public EstimateProjectStartFlowTests()
    {
        Directory.CreateDirectory(_directory);
        var selected = ActiveProjectProfileService.SelectForDrawing(Path.Combine(_directory, "Greenfield.dwg"), null, null, null);
        var generated = ActiveProjectProfileService.CreateUnconfiguredResult(selected);
        _seed = generated.Profile!;
        _target = Path.Combine(_directory, "new-profile.yaml");
        _expected = ProjectProfileWriter.CaptureExpectedGeneratedState(_seed, generated.ProfileHash!, _target);
    }

    [Fact]
    public void FreshProjectWithoutClOrSectionSources_SavesReloadsAndReachesPositiveMeasurementReview()
    {
        var original = JsonSerializer.Serialize(_seed, SectionsWorkflowService.Json);
        EstimateProjectStartService.NeedsStart(_seed, _expected).Should().BeTrue();
        var saved = EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected,
            new("  SYNTHETIC Greenfield estimate  ", Approver, true, "roads"));
        saved.Should().NotBeNull();
        var loaded = ProjectProfileLoader.LoadFromFile(_target);
        loaded.IsUsable.Should().BeTrue();
        var profile = loaded.Profile!;
        profile.ProfileId.Should().Be(_seed.ProfileId).And.NotBe("6422");
        profile.ProjectName.Should().Be("SYNTHETIC Greenfield estimate");
        profile.Provenance.ApprovedBy.Should().Be(Approver);
        profile.Sections.Cl.LayerPatterns.Should().BeEmpty();
        profile.Sections.Cl.SourceFiles.Should().BeEmpty();
        profile.Sections.Sources.SampledSourceRules.Should().BeEmpty();
        profile.Estimate.Earthworks.Requested.Should().BeNull();
        profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
        profile.Estimate.ProjectOverrides.Should().BeEmpty();
        profile.Estimate.ApprovedAdjustments.Should().BeEmpty();
        profile.Estimate.IgnoredRuleKeys.Should().BeEmpty();
        EstimateWorkflowService.IsCompleteDiscoveryScopeApproved(profile).Should().BeTrue();
        JsonSerializer.Serialize(_seed, SectionsWorkflowService.Json).Should().Be(original, "staging never mutates the loaded seed");
        var reopenedState = ProjectProfileWriter.CaptureExpectedState(_target, loaded.ProfileHash!, _target);
        EstimateProjectStartService.NeedsStart(profile, reopenedState).Should().BeFalse();

        // Known synthetic measurements enter the real scan assembler, not an empty success.
        // This boundary does not stand in for Civil entity extraction.
        var measurements = new[] { Measured(profile.ProfileId, "A1", "Q-731", 12.5), Measured(profile.ProfileId, "A2", "Q-731", 7.5) };
        var scan = EstimateWorkflowService.AssembleScan("SYNTHETIC-START-SCAN", profile.ProfileId,
            Path.Combine(_directory, "Greenfield.dwg"), loaded.ProfileHash, measurements,
            Array.Empty<DeliveryFinding>(), 2, true);
        scan.Records.Should().HaveCount(2);
        scan.Records.Sum(record => record.Measurement.RawValue).Should().Be(20);
        scan.Records.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null && record.Status == DeliveryStatus.ReviewRequired);
        var next = EstimateGuidedActionPolicy.Evaluate(new(true, false, true, false, false,
            true, 1, 1, false, false, false, 0));
        next.Next.Should().Be(EstimateGuidedActionPolicy.Action.LoadCatalog);
        Action final = () => EstimateWorkflowService.RequireFinalEstimateScope(profile);
        final.Should().Throw<InvalidOperationException>("earthworks have not silently been excluded or approved");
    }

    [Theory]
    [InlineData("landscape")]
    [InlineData("roads")]
    public void ADeclaredDisciplineIsSavedAsSchema6AndSelectsItsLibrary(string discipline)
    {
        var saved = EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected,
            new("SYNTHETIC discipline estimate", Approver, true, discipline));
        saved.Should().NotBeNull();
        var loaded = ProjectProfileLoader.LoadFromFile(_target);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code)));
        loaded.Profile!.SchemaVersion.Should().Be(ProjectProfileSchemaPolicy.Schema6);
        loaded.Profile.Estimate.Discipline.Should().Be(discipline);
        _seed.Estimate.Discipline.Should().BeNull("staging never mutates the loaded seed");
        MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.For(loaded.Profile).Should().BeSameAs(discipline == "landscape"
            ? MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.LandscapeV1
            : MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1);
    }

    [Fact]
    public void AnUnknownDisciplineIsRefusedBeforeAnyWrite()
    {
        Action save = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected,
            new("SYNTHETIC", Approver, true, "Landscape"));
        save.Should().Throw<InvalidOperationException>();
        File.Exists(_target).Should().BeFalse();
    }

    [Fact]
    public void CancelAfterEditingDoesNotCreateProfileOrChangeSeed()
    {
        var original = JsonSerializer.Serialize(_seed, SectionsWorkflowService.Json);
        EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected, null).Should().BeNull();
        File.Exists(_target).Should().BeFalse();
        JsonSerializer.Serialize(_seed, SectionsWorkflowService.Json).Should().Be(original);
    }

    [Theory]
    [InlineData("", "engineer", true)]
    [InlineData("project", "", true)]
    [InlineData("project", "engineer", false)]
    public void MissingExplicitDecisionWritesNothing(string name, string approver, bool confirm)
    {
        Action save = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected,
            new(name, approver, confirm));
        save.Should().Throw<InvalidOperationException>();
        File.Exists(_target).Should().BeFalse();
        _seed.Estimate.QuantitySources.SourceScopePolicy.Should().BeNull();
    }

    [Fact]
    public void ConcurrentProfileCreationIsPreservedAndNeverAdoptedAsFreshSeed()
    {
        File.WriteAllText(_target, "newer engineer-owned content");
        Action save = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected,
            new("new name", Approver, true));
        save.Should().Throw<InvalidOperationException>();
        File.ReadAllText(_target).Should().Be("newer engineer-owned content");
    }

    [Fact]
    public void PersistedProfileCannotBeOverwrittenByFirstProjectRoute()
    {
        EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected, new("first", Approver, true, "roads"));
        var loaded = ProjectProfileLoader.LoadFromFile(_target);
        var persisted = ProjectProfileWriter.CaptureExpectedState(_target, loaded.ProfileHash!, _target);
        var before = File.ReadAllText(_target);
        Action save = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), loaded.Profile!, persisted,
            new("replacement", Approver, true));
        save.Should().Throw<InvalidOperationException>();
        File.ReadAllText(_target).Should().Be(before);
    }

    [Fact]
    public void PersistedUndeclaredProjectMustChooseDisciplineWithoutLosingScopeOrOtherDecisions()
    {
        var inventory = new EstimateSourceInventory(Guid.NewGuid().ToString(), Path.Combine(_directory, "SYNTHETIC.dwg"),
            new[] { new EstimateSourceDefinition("host", "SYNTHETIC", Path.Combine(_directory, "SYNTHETIC.dwg"), "host", true),
                new EstimateSourceDefinition(new string('e', 64), "SYNTHETIC EXCLUDED", Path.Combine(_directory, "XREF.dwg"), "resolved") });
        var draft = EstimateSourceSelectionPolicy.CreateDraft(inventory, null); draft[1].Included = false;
        _seed.Estimate.SourceSelection = EstimateSourceSelectionPolicy.Approve(inventory, draft, Approver, DateTime.UtcNow);
        _seed.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.ReviewedSourcesPolicy;
        _seed.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        _seed.Sections.Cl.LayerPatterns.Add("TEST-KEEP-CL");
        ProfileCasTest.Save(_seed, _target, "SYNTHETIC existing decisions", Approver);
        var loaded = ProjectProfileLoader.LoadFromFile(_target); loaded.IsUsable.Should().BeTrue();
        var cas = ProjectProfileWriter.CaptureExpectedState(_target, loaded.ProfileHash!, _target);
        EstimateProjectStartService.NeedsStart(loaded.Profile, cas).Should().BeTrue();
        var before = File.ReadAllBytes(_target);
        EstimateProjectStartService.Save(new EstimateWorkflowService(), loaded.Profile!, cas, null).Should().BeNull();
        File.ReadAllBytes(_target).Should().Equal(before);
        Action missing = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), loaded.Profile!, cas, new("name", Approver, true));
        missing.Should().Throw<InvalidOperationException>().WithMessage(EstimateProjectStartService.DisciplineMissing);
        File.ReadAllBytes(_target).Should().Equal(before);
        var original = JsonSerializer.Serialize(loaded.Profile, SectionsWorkflowService.Json);
        EstimateProjectStartService.Save(new EstimateWorkflowService(), loaded.Profile!, cas, new("SYNTHETIC landscape", Approver, true, "landscape"));
        JsonSerializer.Serialize(loaded.Profile, SectionsWorkflowService.Json).Should().Be(original);
        var after = ProjectProfileLoader.LoadFromFile(_target); after.IsUsable.Should().BeTrue();
        after.Profile!.Estimate.Discipline.Should().Be("landscape");
        after.Profile.ProjectName.Should().Be("SYNTHETIC landscape");
        JsonSerializer.Serialize(after.Profile.Estimate.SourceSelection).Should().Be(JsonSerializer.Serialize(loaded.Profile!.Estimate.SourceSelection));
        JsonSerializer.Serialize(after.Profile.Estimate.QuantitySources).Should().Be(JsonSerializer.Serialize(loaded.Profile.Estimate.QuantitySources));
        JsonSerializer.Serialize(after.Profile.Sections).Should().Be(JsonSerializer.Serialize(loaded.Profile.Sections));
        EstimateProjectStartService.NeedsStart(after.Profile, ProjectProfileWriter.CaptureExpectedState(_target, after.ProfileHash!, _target)).Should().BeFalse();
    }

    [Fact]
    public void ExistingUndeclaredProjectDoesNotAdoptConcurrentBytes()
    {
        ProfileCasTest.Save(_seed, _target, "SYNTHETIC existing seed", Approver);
        var loaded = ProjectProfileLoader.LoadFromFile(_target);
        var cas = ProjectProfileWriter.CaptureExpectedState(_target, loaded.ProfileHash!, _target);
        File.AppendAllText(_target, "\n# SYNTHETIC concurrent edit\n");
        var changed = File.ReadAllBytes(_target);
        Action save = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), loaded.Profile!, cas, new("new", Approver, true, "landscape"));
        save.Should().Throw<InvalidOperationException>(); File.ReadAllBytes(_target).Should().Equal(changed);
    }

    [Fact]
    public void EmptyFreshScanCannotBecomeAnEstimateReadyState()
    {
        var next = EstimateGuidedActionPolicy.Evaluate(new(true, false, true, false, false,
            true, 0, 0, false, false, false, 0));
        next.Next.Should().Be(EstimateGuidedActionPolicy.Action.ReviewSources);
        next.Next.Should().NotBe(EstimateGuidedActionPolicy.Action.Build);
        next.Next.Should().NotBe(EstimateGuidedActionPolicy.Action.Export);
    }

    [Fact]
    public void InMemorySeedChangeCannotBeAdoptedByFirstProfileSave()
    {
        _seed.ProjectName = "other work changed the seed";
        Action save = () => EstimateProjectStartService.Save(new EstimateWorkflowService(), _seed, _expected,
            new("reviewed name", Approver, true));
        save.Should().Throw<InvalidOperationException>();
        File.Exists(_target).Should().BeFalse();
    }

    private static NeutralQuantityRecord Measured(string project, string handle, string layer, double value) => new()
    {
        RecordId = "SYNTHETIC-" + handle, ProjectProfileId = project, RunId = "SYNTHETIC-START-SCAN",
        Source = new QuantitySource { Drawing = "Greenfield.dwg", DrawingHash = new string('a', 64),
            Handle = handle, Layer = layer, EntityType = "LINE" },
        Measurement = new QuantityMeasurement { Kind = "length", Method = "synthetic-known-length", RawValue = value, Unit = "מטר" },
        Classification = new QuantityClassification { RuleKey = "layer:" + layer + "|length" },
        Status = DeliveryStatus.ReviewRequired,
    };

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
