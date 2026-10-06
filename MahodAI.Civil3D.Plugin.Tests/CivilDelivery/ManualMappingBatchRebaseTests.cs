using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ManualMappingBatchRebaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcd-manual-rebase-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public ManualMappingBatchRebaseTests(ITestOutputHelper output) => _output = output;
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void SinglePass_EqualsRepeatedSingleDecision_WithSourcesAndFindingsPreserved()
    {
        var fixture = Fixture(9, 6);
        var before = JsonSerializer.Serialize(fixture.Scan);
        var selected = fixture.Keys.Take(5).ToArray();
        var reference = fixture.Scan;
        foreach (var key in selected)
            reference = EstimateWorkflowService.RebaseAfterProfileDecision(reference, fixture.Profile, fixture.Saved, key);
        var actual = EstimateWorkflowService.RebaseAfterProfileDecisions(
            fixture.Scan, fixture.Profile, fixture.Saved, selected);

        JsonSerializer.Serialize(actual.Records).Should().Be(JsonSerializer.Serialize(reference.Records));
        FindingValues(actual).Should().Equal(FindingValues(reference));
        actual.Status.Should().Be(reference.Status);
        actual.ProjectProfileEffectiveHash.Should().Be(reference.ProjectProfileEffectiveHash);
        actual.ProfileWriteState.Should().Be(reference.ProfileWriteState);
        actual.RunId.Should().Be(fixture.Scan.RunId);
        actual.SourceDrawingHash.Should().Be(fixture.Scan.SourceDrawingHash);
        actual.DatabaseRevision.Should().Be(fixture.Scan.DatabaseRevision);
        actual.SourceDbMod.Should().Be(fixture.Scan.SourceDbMod);
        actual.ExternalSources.Should().Equal(fixture.Scan.ExternalSources);
        actual.MaterialAreas.Should().Equal(fixture.Scan.MaterialAreas);
        actual.Findings.Should().Contain(fixture.Scan.Findings[0]);
        actual.Records[0].Source.Should().BeSameAs(fixture.Scan.Records[0].Source);
        actual.Records[0].Measurement.Should().BeSameAs(fixture.Scan.Records[0].Measurement);
        actual.Records[0].Provenance.Should().BeSameAs(fixture.Scan.Records[0].Provenance);
        actual.Records[0].Findings.Should().Contain(fixture.Scan.Records[0].Findings[1]);
        actual.Records.Last().Should().BeSameAs(fixture.Scan.Records.Last());
        JsonSerializer.Serialize(fixture.Scan).Should().Be(before, "rebasing must not mutate the original scan");
    }

    [Fact]
    public void DuplicateKeysAndEmptyBatch_PreserveSingleDecisionSemantics()
    {
        var fixture = Fixture(2, 4);
        var once = EstimateWorkflowService.RebaseAfterProfileDecision(
            fixture.Scan, fixture.Profile, fixture.Saved, fixture.Keys[0]);
        var repeated = EstimateWorkflowService.RebaseAfterProfileDecisions(
            fixture.Scan, fixture.Profile, fixture.Saved,
            new[] { fixture.Keys[0], fixture.Keys[0], " " + fixture.Keys[0] + " ", "", " " });
        JsonSerializer.Serialize(repeated.Records).Should().Be(JsonSerializer.Serialize(once.Records));
        FindingValues(repeated).Should().Equal(FindingValues(once));
        var empty = EstimateWorkflowService.RebaseAfterProfileDecisions(
            fixture.Scan, fixture.Profile, fixture.Saved, Array.Empty<string>());
        var relevanceOnly = EstimateWorkflowService.RebaseAfterProfileDecision(fixture.Scan, fixture.Profile, fixture.Saved);
        JsonSerializer.Serialize(empty.Records).Should().Be(JsonSerializer.Serialize(relevanceOnly.Records));
        FindingValues(empty).Should().Equal(FindingValues(relevanceOnly));
    }

    [Fact]
    public void InvalidLastRule_RejectsBeforeReturningAnyReclassifiedState()
    {
        var fixture = Fixture(3, 5);
        var before = JsonSerializer.Serialize(fixture.Scan);
        var profileBefore = JsonSerializer.Serialize(fixture.Profile);
        var savedBytes = File.ReadAllBytes(fixture.Saved.Path);
        Action act = () => EstimateWorkflowService.RebaseAfterProfileDecisions(
            fixture.Scan, fixture.Profile, fixture.Saved, fixture.Keys.Append("missing-rule"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*not one unique current rule*");
        JsonSerializer.Serialize(fixture.Scan).Should().Be(before);
        JsonSerializer.Serialize(fixture.Profile).Should().Be(profileBefore);
        File.ReadAllBytes(fixture.Saved.Path).Should().Equal(savedBytes);
    }

    [Fact]
    public void SeventyFourThousandRows_And129Selections_RebaseOnceWithoutDroppingRecords()
    {
        // Native cardinalities, synthetic measurements: performance evidence for the
        // batch transition, not a replay or engineering approval of the real drawing.
        const int count = 74_891;
        var fixture = Fixture(129, count / 129, count);
        var timer = Stopwatch.StartNew();
        var actual = EstimateWorkflowService.RebaseAfterProfileDecisions(
            fixture.Scan, fixture.Profile, fixture.Saved, fixture.Keys);
        timer.Stop();
        _output.WriteLine($"Synthetic native-cardinality rebase: {count:N0} records / 129 choices, {timer.Elapsed.TotalSeconds:F3}s.");
        actual.Records.Should().HaveCount(count);
        actual.Records.Select(record => record.RecordId).Should().Equal(fixture.Scan.Records.Select(record => record.RecordId));
        actual.Records.Select(record => record.Measurement.RawValue).Should().Equal(fixture.Scan.Records.Select(record => record.Measurement.RawValue));
        actual.Records.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == "U51.01.0250");
        actual.Findings.Should().Contain(fixture.Scan.Findings[0], "batch classification must not waive a global source failure");
        fixture.Scan.Records.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null);
    }

    private static IEnumerable<string> FindingValues(EstimateWorkflowService.ScanResult scan) =>
        scan.Findings.Select(finding => string.Join("|", finding.Code, finding.Severity,
            finding.Title, finding.Message, finding.RecommendedAction, string.Join(",", finding.AffectedRecordIds)))
            .OrderBy(value => value, StringComparer.Ordinal);

    [Fact]
    public void ReviewedSourceScope_SurvivesScanSerializationAndMappingRebase()
    {
        var fixture = Fixture(2, 3, sourceSelection: ReviewedSources());
        var reopened = JsonSerializer.Deserialize<EstimateWorkflowService.ScanResult>(JsonSerializer.Serialize(fixture.Scan))!;
        reopened.SourceSelectionConfigurationHash.Should().NotBeNullOrEmpty();
        reopened.SourceSelectionConfigurationHash.Should().Be(fixture.Scan.SourceSelectionConfigurationHash);
        var actual = EstimateWorkflowService.RebaseAfterProfileDecisions(reopened, fixture.Profile, fixture.Saved, fixture.Keys);
        actual.SourceSelectionConfigurationHash.Should().Be(reopened.SourceSelectionConfigurationHash);
        actual.Records.Should().HaveCount(6);
        actual.Records.Select(r => r.Measurement.RawValue).Should().Equal(reopened.Records.Select(r => r.Measurement.RawValue));
        actual.Records.Should().OnlyContain(r => r.Classification.CandidateCatalogCode == "U51.01.0250");
        actual.ExternalSources.Should().BeEquivalentTo(reopened.ExternalSources);
        actual.Findings.Should().Contain(reopened.Findings[0]);
    }

    [Theory]
    [InlineData("included")]
    [InlineData("category")]
    [InlineData("inventory")]
    [InlineData("approval")]
    [InlineData("remove")]
    public void ChangedSourceScope_CannotRelabelOldMeasurementsDuringRebase(string change)
    {
        var fixture = Fixture(2, 3, sourceSelection: ReviewedSources());
        var before = JsonSerializer.Serialize(fixture.Scan);
        var selection = fixture.Profile.Estimate.SourceSelection!;
        switch (change)
        {
            case "included": selection.Sources[1].Included = true; break;
            case "category": selection.Sources[1].Category = "landscape"; break;
            case "inventory": selection.InventoryHash = new string('e', 64); break;
            case "approval": selection.ApprovedAtUtc = selection.ApprovedAtUtc!.Value.AddMinutes(1); break;
            case "remove": fixture.Profile.Estimate.SourceSelection = null; break;
        }
        Action act = () => EstimateWorkflowService.RebaseAfterProfileDecisions(fixture.Scan, fixture.Profile, fixture.Saved, fixture.Keys);
        act.Should().Throw<InvalidOperationException>().WithMessage("*נדרשת סריקה חדשה*");
        JsonSerializer.Serialize(fixture.Scan).Should().Be(before);
    }

    [Fact]
    public void LegacyScan_CannotAcquireReviewedSourceScopeWithoutNewMeasurement()
    {
        var fixture = Fixture(2, 3);
        fixture.Scan.SourceSelectionConfigurationHash.Should().BeNull();
        fixture.Profile.Estimate.SourceSelection = ReviewedSources();
        Action act = () => EstimateWorkflowService.RebaseAfterProfileDecision(fixture.Scan, fixture.Profile, fixture.Saved);
        act.Should().Throw<InvalidOperationException>().WithMessage("*נדרשת סריקה חדשה*");
    }

    private static EstimateSourceSelection ReviewedSources() => new()
    {
        HostFingerprint = "00000000-0000-0000-0000-000000000123", HostPath = @"C:\synthetic\host.dwg",
        InventoryHash = new string('d', 64), ApprovedBy = "synthetic-test-engineer",
        ApprovedAtUtc = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc),
        Sources = new()
        {
            new() { Key = "host", Name = "host", Path = @"C:\synthetic\host.dwg", Category = "roads", Included = true },
            new() { Key = new string('f', 64), Name = "background", Path = @"C:\synthetic\background.dwg", Category = "architecture", Included = false },
        },
    };

    private (ProjectProfile Profile, EstimateWorkflowService.ScanResult Scan,
        ProjectProfileWriter.SaveResult Saved, string[] Keys) Fixture(int groups, int perGroup, int? total = null,
            EstimateSourceSelection? sourceSelection = null)
    {
        var profile = EstimateFixtures.Profile();
        profile.Estimate.SourceSelection = sourceSelection;
        var keys = Enumerable.Range(0, groups).Select(index => $"layer:HW-CURB-{index}|length").ToArray();
        foreach (var (key, index) in keys.Select((key, index) => (key, index)))
        {
            var rule = new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = key, LayerPattern = $"HW-CURB-{index}", EntityType = "LWPOLYLINE",
                MeasurementKind = "length", ExpectedUnit = "מטר", CandidateCatalogCode = "U51.01.0250",
            };
            EstimateFixtures.BindApprovalToActiveCatalog(rule);
            profile.Estimate.QuantitySources.Rules.Add(rule);
        }
        var saved = ProfileCasTest.Save(profile, Path.Combine(_root, "profile.yaml"),
            "synthetic batch rebase regression", "test-engineer");
        var records = Enumerable.Range(0, total ?? groups * perGroup).Select(index =>
            EstimateFixtures.Record($"q-{index}", null!, 2.5 + index / 1000d, "מטר",
                handle: index.ToString("X"), ruleKey: keys[index % groups], layer: $"HW-CURB-{index % groups}"))
            .ToList();
        records[0].Findings.Add(new DeliveryFinding
        {
            Code = EstimateFindingCodes.Unmapped, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
            Title = "unmapped", Message = "mapping evidence only",
        });
        records[0].Findings.Add(new DeliveryFinding
        {
            Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = "record source geometry remains unresolved", Message = "must survive classification",
        });
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "synthetic-review", ProjectProfileId = profile.ProfileId,
            ProfileSource = saved.Path, SourceDrawing = @"C:\synthetic\host.dwg",
            ProjectProfileHash = new string('a', 64), DatabaseRevision = "db-before-review",
            SourceDrawingHash = new string('b', 64), SourceDbMod = 0, Records = records,
            DiscoveryMode = true, ScannedEntities = records.Count,
            SourceSelectionConfigurationHash = EstimateWorkflowService.SourceSelectionHash(profile),
            ExternalSources = { new EstimateExternalSource
            {
                DrawingPath = @"C:\synthetic\xref.dwg", DrawingHash = new string('c', 64),
                XrefChain = "host|xref", ReferenceHandlePath = "AA/BB",
            } },
            MaterialAreas = MaterialSectionAreaEvidence.Capture(
                new[] { new CorridorQuantityLogic.ShapeSample(120, "H1", 3.5, "baseline-1") },
                "synthetic-review", @"C:\synthetic\host.dwg", new string('b', 64),
                "AB", "Road", 1, true, "Meters", false).ToList(),
            Findings = { new DeliveryFinding
            {
                Code = "EST-SOURCE-UNRESOLVED", Domain = "estimate", Severity = FindingSeverity.Error,
                Title = "global source failure", Message = "not an approval decision",
            } },
        };
        return (profile, scan, saved, keys);
    }
}
