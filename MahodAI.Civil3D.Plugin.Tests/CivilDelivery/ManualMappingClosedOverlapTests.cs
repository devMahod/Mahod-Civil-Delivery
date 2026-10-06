using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Choice = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService.ReviewedMappingChoice;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ManualMappingClosedOverlapTests : IDisposable
{
    private const string Area = "layer:HW-CS-TABL|area";
    private const string Length = "layer:HW-CS-TABL|length";
    private const string AreaCode = "U51.01.0090";
    private const string LengthCode = "U51.01.0250";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcd-closed-partial-review-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeHwCsTablShapeCannotMapAreaAndPerimeterWhenLengthAlsoContainsOpen850C65(bool reverse)
    {
        var fixture = Fixture();
        ClosedPolylineAlternativePolicy.FindUnambiguousExactPairs(fixture.Scan.Records).Should().BeEmpty();
        if (reverse) fixture.Scan.Records.Reverse();
        RejectUnchanged(fixture, new[] { new Choice(Area, AreaCode), new Choice(Length, LengthCode) });
    }

    [Theory]
    [InlineData(Area)]
    [InlineData(Length)]
    public void PreviouslyApprovedOppositeDimensionCannotBeHiddenByASeparateBatch(string approvedKey)
    {
        var fixture = Fixture(approvedKey);
        var chosen = approvedKey == Area ? Length : Area;
        RejectUnchanged(fixture, new[] { new Choice(chosen, chosen == Area ? AreaCode : LengthCode) });
    }

    [Theory]
    [InlineData(Area)]
    [InlineData(Length)]
    public void OneChoiceCanBeRecordedWithoutInventingWholeGroupExclusionOrLosingOpenRecord(string selected)
    {
        var fixture = Fixture();
        var saved = Save(fixture, new[] { new Choice(selected, selected == Area ? AreaCode : LengthCode) });
        fixture.Profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(
            fixture.Scan, fixture.Profile, saved, new[] { selected });
        rebased.Records.Should().HaveCount(3);
        var open = rebased.Records.Single(record => record.Source.Handle == "850C65");
        open.Measurement.Method.Should().Be("polyline-length");
        open.Measurement.RawValue.Should().Be(60);
        open.Measurement.Should().BeSameAs(fixture.Scan.Records.Single(record => record.Source.Handle == "850C65").Measurement);
        rebased.Findings.Should().Contain(finding => finding.Code == EstimateFindingCodes.MixedDimensionLayer);
        rebased.Findings.Should().Contain(fixture.Scan.Findings[1], "source evidence is not resolved by the mapping decision");
    }

    [Fact]
    public void DistinctTransformedXrefInstancesSharingLeafHandleAreNotInventedAsOneClosedSource()
    {
        var fixture = Fixture(separateXrefs: true);
        Save(fixture, new[] { new Choice(Area, AreaCode), new Choice(Length, LengthCode) });
        fixture.Profile.Estimate.QuantitySources.Rules.Should().HaveCount(2);
        fixture.Profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        fixture.Scan.Records.Should().HaveCount(3);
    }

    private void RejectUnchanged(FixtureData fixture, IReadOnlyList<Choice> choices)
    {
        var profile = JsonSerializer.Serialize(fixture.Profile);
        var scan = JsonSerializer.Serialize(fixture.Scan);
        Action act = () => Save(fixture, choices);
        act.Should().Throw<InvalidOperationException>().WithMessage("*both area and perimeter*850C64*");
        JsonSerializer.Serialize(fixture.Profile).Should().Be(profile);
        JsonSerializer.Serialize(fixture.Scan).Should().Be(scan);
        File.Exists(fixture.Scan.ProfileSource).Should().BeFalse();
    }

    private static ProjectProfileWriter.SaveResult Save(FixtureData fixture, IReadOnlyList<Choice> choices) =>
        new EstimateWorkflowService().SaveReviewedMappings(fixture.Profile, EstimateFixtures.Snapshot(),
            fixture.Scan, choices, "SYNTHETIC REVIEW ONLY", fixture.Scan.ProfileSource, fixture.Scan.ProfileWriteState!);

    private sealed record FixtureData(ProjectProfile Profile, EstimateWorkflowService.ScanResult Scan);

    private FixtureData Fixture(string? approvedKey = null, bool separateXrefs = false)
    {
        var profile = EstimateFixtures.Profile();
        if (approvedKey != null)
        {
            var rule = new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = approvedKey, LayerPattern = "HW-CS-TABL", EntityType = "LWPOLYLINE",
                MeasurementKind = approvedKey == Area ? "area" : "length",
                ExpectedUnit = approvedKey == Area ? "m2" : "m",
                CandidateCatalogCode = approvedKey == Area ? AreaCode : LengthCode,
            };
            EstimateFixtures.BindApprovalToActiveCatalog(rule);
            profile.Estimate.QuantitySources.Rules.Add(rule);
        }
        // Recorded handles/values/methods from the native 09:27 scan; other identities
        // are explicit synthetic local fixtures. No native profile or scan is edited.
        var records = new List<NeutralQuantityRecord>
        {
            EstimateFixtures.Record("q-disc-850C64-area", approvedKey == Area ? AreaCode : null!,
                240, "m2", "area", "850C64", xref: separateXrefs ? "A/first" : null,
                ruleKey: Area, layer: "HW-CS-TABL", method: "closed-polyline-area"),
            EstimateFixtures.Record("q-disc-850C64-length", approvedKey == Length ? LengthCode : null!,
                128, "m", "length", "850C64", xref: separateXrefs ? "B/second" : null,
                ruleKey: Length, layer: "HW-CS-TABL", method: "closed-polyline-perimeter"),
            EstimateFixtures.Record("q-disc-850C65-length", approvedKey == Length ? LengthCode : null!,
                60, "m", "length", "850C65", ruleKey: Length, layer: "HW-CS-TABL", method: "polyline-length"),
        };
        var path = Path.Combine(_root, "simulation-only.yaml");
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "synthetic-hw-cs-tabl", ProjectProfileId = profile.ProfileId,
            ProfileSource = path, SourceDrawing = @"C:\synthetic\host.dwg",
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProfileWriteState = ProfileCasTest.For(profile, path), Records = records,
            DiscoveryMode = true,
            Findings =
            {
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MixedDimensionLayer, Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired, Title = "Closed 850C64 has area and perimeter",
                    AffectedRecordIds = { "q-disc-850C64-area", "q-disc-850C64-length" },
                },
                new DeliveryFinding
                {
                    Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate",
                    Severity = FindingSeverity.Error, Title = "Unrelated source failure stays unresolved",
                },
            },
        };
        return new FixtureData(profile, scan);
    }
}
