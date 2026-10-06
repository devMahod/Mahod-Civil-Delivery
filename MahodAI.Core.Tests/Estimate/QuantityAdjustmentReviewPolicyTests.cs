using System.IO;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class QuantityAdjustmentReviewPolicyTests
{
    private const string Key = "layer:SYNTHETIC-UNKNOWN|length";
    private static readonly DateTime When = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);
    private static ProjectProfile Profile() => new() { ProfileId = "TEST-ONLY", ProjectName = "COUNTERFACTUAL TEST ONLY" };
    private static NeutralQuantityRecord Record(string id, double raw, string key = Key, string unit = "m", string? layer = null) => new()
    {
        RecordId = id, ProjectProfileId = "TEST-ONLY", RunId = "TEST-ONLY",
        Source = new() { Drawing = "TEST-ONLY.dwg", DrawingPath = @"C:\TEST-ONLY\source.dwg", DrawingHash = new string('a', 64), Handle = id, EntityType = "LINE", Layer = layer ?? "SYNTHETIC-UNKNOWN" },
        Measurement = new() { Kind = "length", Method = "line-length", Unit = unit, RawValue = raw },
        Classification = new() { RuleKey = key },
    };
    private static QuantityAdjustmentReviewPolicy.Decision Decision(QuantityAdjustmentReviewPolicy.Context context,
        double factor = 1.2, bool remove = false) => QuantityAdjustmentReviewPolicy.Approve(context, factor, remove,
            "TEST ONLY: explicit measured-length basis", "TEST ONLY document", "TEST ONLY decision", "TEST REVIEWER", When, true)!;
    private static string Target()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mahod-quantity-factor-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); return Path.Combine(dir, "SYNTHETIC-ONLY-profile.yaml");
    }

    [Fact]
    public void PositiveSaveReopenEditRemovePreservesRawOtherRulesAndUnrelatedGroups()
    {
        var profile = Profile(); var records = new[] { Record("A1", 10), Record("A2", 20) };
        var original = JsonSerializer.Serialize(records); var target = Target();
        profile.Estimate.ApprovedAdjustments.Add(new()
        { RuleId = "unrelated-authored", Scope = "rule:layer:OTHER|length", Factor = 2, Order = 1, Status = "CONFIRMED", Source = "OTHER", Reason = "keep", ApprovedBy = "OTHER", ApprovedAtUtc = When });
        profile.Estimate.CandidateAdjustments.Add(new() { RuleId = "unconfirmed", Factor = .9, Status = "UNCONFIRMED" });
        var untouched = JsonSerializer.Serialize(profile.Estimate.ApprovedAdjustments);
        var candidate = JsonSerializer.Serialize(profile.Estimate.CandidateAdjustments);
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, Key, records);
        QuantityAdjustmentReviewPolicy.PreviewChange(context, 1.2, false).Should().Be(new QuantityAdjustmentReviewPolicy.Preview(30, 30, 36, 2));
        var state = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), target);
        var saved = QuantityAdjustmentReviewPolicy.Save(profile, records, Decision(context), target, state);
        var reloaded = ProjectProfileLoader.LoadFromFile(saved.Path).Profile!;
        var ownedId = QuantityAdjustmentReviewPolicy.OwnedRuleId(Key);
        var owned = reloaded.Estimate.ApprovedAdjustments.Single(rule => rule.RuleId == ownedId);
        owned.Factor.Should().Be(1.2); owned.Scope.Should().Be("rule:" + Key);
        owned.ApprovedBy.Should().Be("TEST REVIEWER"); owned.ApprovedAtUtc.Should().Be(When);
        JsonSerializer.Serialize(reloaded.Estimate.CandidateAdjustments).Should().Be(candidate);
        AdjustmentEngine.Apply(10, reloaded.Estimate.ApprovedAdjustments, Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(), "Z1", "layer:OTHER|length").BoqValue.Should().Be(20);
        foreach (var factor in new[] { 1.5, 1.0 })
        {
            context = QuantityAdjustmentReviewPolicy.Capture(reloaded, Key, records);
            var removing = factor == 1.0;
            var preview = QuantityAdjustmentReviewPolicy.PreviewChange(context, factor, removing);
            preview.AfterTotal.Should().Be(removing ? 30 : 45);
            var expected = ProjectProfileWriter.CaptureExpectedState(target, saved.NewHash, target);
            saved = QuantityAdjustmentReviewPolicy.Save(reloaded, records, Decision(context, factor, removing), target, expected);
            File.Exists(saved.BackupPath).Should().BeTrue();
            reloaded = ProjectProfileLoader.LoadFromFile(target).Profile!;
        }
        JsonSerializer.Serialize(reloaded.Estimate.ApprovedAdjustments).Should().Be(untouched);
        JsonSerializer.Serialize(reloaded.Estimate.CandidateAdjustments).Should().Be(candidate);
        JsonSerializer.Serialize(records).Should().Be(original, "no original quantity/source/classification can be changed");
    }

    [Fact]
    public void ExistingRecordAndCatalogFactorsAreVisibleAndAccumulateInTheRealEngineOrder()
    {
        var profile = Profile(); var records = new[] { Record("A1", 10), Record("A2", 20) };
        records[0].Classification.CandidateCatalogCode = "TEST.CODE";
        profile.Estimate.ApprovedAdjustments.AddRange(new[]
        {
            new ProjectProfile.EstimateProfile.AdjustmentRule { RuleId = "record-factor", Factor = 2, Scope = "record:A1", Order = 1, Status = "CONFIRMED", ApprovedBy = "TEST", ApprovedAtUtc = When },
            new ProjectProfile.EstimateProfile.AdjustmentRule { RuleId = "catalog-factor", Factor = 3, Scope = "catalog:TEST.CODE", Order = 2, Status = "CONFIRMED", ApprovedBy = "TEST", ApprovedAtUtc = When },
        });
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, Key, records);
        context.ExistingRules.Should().HaveCount(2);
        QuantityAdjustmentReviewPolicy.PreviewChange(context, .5, false).Should().Be(new QuantityAdjustmentReviewPolicy.Preview(30, 80, 40, 2));
    }

    [Fact]
    public void CaseVariantsUseTheSameEngineScopeStableIdAndCompletePreviewWithoutAnUnrelatedGroup()
    {
        const string selectedKey = "layer:Road|length";
        var profile = Profile();
        var all = new[] { Record("A1", 10, selectedKey, layer: "Road"), Record("A2", 20, "layer:ROAD|length", layer: "ROAD"), Record("B1", 50, "layer:OTHER|length") };
        var selected = all.Where(record => string.Equals(record.Classification.RuleKey, selectedKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, selectedKey, selected);
        context.Inputs.Should().HaveCount(2); context.Layer.Should().Contain("Road").And.Contain("ROAD");
        QuantityAdjustmentReviewPolicy.OwnedRuleId(selectedKey).Should().Be(QuantityAdjustmentReviewPolicy.OwnedRuleId("layer:ROAD|length"));
        QuantityAdjustmentReviewPolicy.PreviewChange(context, 2, false).AfterTotal.Should().Be(60);
        var target = Target();
        QuantityAdjustmentReviewPolicy.Save(profile, selected, Decision(context, 2), target,
            ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), target));
        var reload = ProjectProfileLoader.LoadFromFile(target).Profile!;
        var values = all.Select(record => AdjustmentEngine.Apply(record.Measurement.RawValue, reload.Estimate.ApprovedAdjustments,
            Array.Empty<ProjectProfile.EstimateProfile.AdjustmentRule>(), record.RecordId, record.Classification.RuleKey).BoqValue).ToArray();
        values.Should().Equal(20, 40, 50);
        QuantityAdjustmentReviewPolicy.Capture(reload, "layer:ROAD|length", selected).Owned.Should().NotBeNull();
    }

    [Fact]
    public void SavedFactorFeedsRealBuilderAndPartialExportWithoutApprovingUnrelatedLinesOrEarthworks()
    {
        var profile = Profile();
        profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC-ONLY-CATALOG", FileHash = new string('b', 64) };
        catalog.Items["TEST.1"] = new() { Code = "TEST.1", Description = "SYNTHETIC ONLY", UnitRaw = "m" };
        catalog.Prices["TEST.1"] = new() { Code = "TEST.1", Price = 12.5m, PriceBookId = catalog.SnapshotId, SourceHash = catalog.FileHash };
        var selected = new[] { Record("A1", 10), Record("A2", 20) };
        foreach (var record in selected)
        {
            record.Classification.CandidateCatalogCode = "TEST.1";
            record.Classification.ApprovedCatalogId = catalog.SnapshotId;
            record.Classification.ApprovedCatalogHash = catalog.FileHash;
            record.Classification.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items["TEST.1"]);
            record.Classification.MappingApprovedBy = "COUNTERFACTUAL TEST ONLY"; record.Classification.MappingApprovedAtUtc = When;
        }
        var original = JsonSerializer.Serialize(selected);
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, Key, selected); var target = Target();
        var saved = QuantityAdjustmentReviewPolicy.Save(profile, selected, Decision(context), target,
            ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), target));
        var reloaded = ProjectProfileLoader.LoadFromFile(saved.Path).Profile!;
        var unrelated = Record("B1", 7, "layer:UNMAPPED|length");
        var findings = new[] { new DeliveryFinding { Code = EstimatePreflightPolicy.EarthworksNotAssessedCode,
            Domain = "estimate", Severity = FindingSeverity.ReviewRequired, Title = "TEST ONLY — earthworks undecided" } };
        var built = EstimateBuilder.Build(selected.Append(unrelated).ToArray(), catalog, reloaded,
            "SYNTHETIC-FACTOR-NOT-PROJECT-APPROVAL", findings);
        built.CleanTotal.Should().Be(450); built.Lines.Should().HaveCount(3);
        built.Lines.Take(2).Select(line => line.RawQuantity).Should().Equal(10, 20);
        built.Lines.Take(2).Select(line => line.BoqQuantity).Should().Equal(12, 24);
        built.Lines.Take(2).Should().OnlyContain(line => line.Adjustments.Count == 1 && line.IncludedInTotals);
        built.Lines.Last().IncludedInTotals.Should().BeFalse(); built.Lines.Last().Total.Should().BeNull();
        built.Findings.Should().Contain(findings); built.Exclusions.Should().BeEmpty();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeTrue();
        var written = EstimateExcelWriter.WritePartialPricedDraft(built, Path.GetDirectoryName(target)!, "SYNTHETIC-FACTOR-PARTIAL");
        File.Exists(written.XlsxPath).Should().BeTrue();
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        audit.RootElement.GetProperty("clean_total").GetDecimal().Should().Be(450);
        JsonSerializer.Serialize(selected).Should().Be(original);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidFactorCannotBecomeADecision(double factor)
    {
        var context = QuantityAdjustmentReviewPolicy.Capture(Profile(), Key, new[] { Record("A1", 10) });
        Action act = () => Decision(context, factor); act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CancelAndIncompleteEvidenceCannotWriteOrApproveCandidate()
    {
        var profile = Profile(); var records = new[] { Record("A1", 10) };
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, Key, records);
        QuantityAdjustmentReviewPolicy.Approve(context, .5, false, null, null, null, null, default, false).Should().BeNull();
        Action incomplete = () => QuantityAdjustmentReviewPolicy.Approve(context, .5, false, "", "TEST", "TEST", "TEST", When, true);
        incomplete.Should().Throw<InvalidOperationException>(); profile.Estimate.ApprovedAdjustments.Should().BeEmpty();
        Action remove = () => Decision(context, remove: true); remove.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("unowned-id")] [InlineData("duplicate")] [InlineData("unsupported")] [InlineData("candidate-id")]
    public void ConflictsAndUnownedIdentifiersAreNotSilentlyReplaced(string fault)
    {
        var profile = Profile();
        var value = new ProjectProfile.EstimateProfile.AdjustmentRule
        { RuleId = fault == "unowned-id" ? QuantityAdjustmentReviewPolicy.OwnedRuleId(Key) : "AUTHORED", Factor = 2, Scope = "rule:" + Key, Order = 1, Status = "CONFIRMED", ApprovedBy = "OTHER", ApprovedAtUtc = When };
        if (fault == "unsupported") value.Scope = "global";
        if (fault == "candidate-id")
        { value.RuleId = QuantityAdjustmentReviewPolicy.OwnedRuleId(Key); profile.Estimate.CandidateAdjustments.Add(value); }
        else profile.Estimate.ApprovedAdjustments.Add(value);
        if (fault == "duplicate") profile.Estimate.ApprovedAdjustments.Add(value);
        var before = EstimateTraceIdentity.EffectiveProfileHash(profile);
        Action capture = () => QuantityAdjustmentReviewPolicy.Capture(profile, Key, new[] { Record("A1", 10) });
        capture.Should().Throw<InvalidOperationException>(); EstimateTraceIdentity.EffectiveProfileHash(profile).Should().Be(before);
    }

    [Fact]
    public void StaleProfileRecordAndCasAreRejectedWithoutMutatingRules()
    {
        var profile = Profile(); var records = new[] { Record("A1", 10) }; var target = Target();
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, Key, records);
        var decision = Decision(context);
        var state = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), target);
        records[0] = Record("A1", 11);
        Action staleRecord = () => QuantityAdjustmentReviewPolicy.Save(profile, records, decision, target, state);
        staleRecord.Should().Throw<InvalidOperationException>(); File.Exists(target).Should().BeFalse();
        records[0] = Record("A1", 10);
        profile.ProjectName = "changed";
        Action staleProfile = () => QuantityAdjustmentReviewPolicy.Save(profile, records, decision, target, state);
        staleProfile.Should().Throw<InvalidOperationException>(); profile.Estimate.ApprovedAdjustments.Should().BeEmpty();
        profile.ProjectName = "COUNTERFACTUAL TEST ONLY";
        File.WriteAllText(target, "CONCURRENT TEST FILE");
        Action staleCas = () => QuantityAdjustmentReviewPolicy.Save(profile, records, decision, target, state);
        staleCas.Should().Throw<InvalidOperationException>(); File.ReadAllText(target).Should().Be("CONCURRENT TEST FILE");
        profile.Estimate.ApprovedAdjustments.Should().BeEmpty();
    }

    [Fact]
    public void MixedGroupUnitsMissingRawAndOverflowStayBlocked()
    {
        var profile = Profile(); var records = new[] { Record("A1", 10), Record("A2", 20) };
        records[1] = Record("A2", 20, unit: "m2");
        Action mixed = () => QuantityAdjustmentReviewPolicy.Capture(profile, Key, records); mixed.Should().Throw<InvalidOperationException>();
        records[1] = Record("A2", 0);
        mixed.Should().Throw<InvalidOperationException>();
        var context = QuantityAdjustmentReviewPolicy.Capture(profile, Key, new[] { Record("A1", 10) });
        Action overflow = () => Decision(context, double.MaxValue); overflow.Should().Throw<InvalidOperationException>();
    }
}
