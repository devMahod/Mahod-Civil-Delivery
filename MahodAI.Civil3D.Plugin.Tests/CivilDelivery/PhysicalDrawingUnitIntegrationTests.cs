using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Executes host-free production gates/converters and recovery contracts. It does
/// not construct a CAD Database, execute Civil commands or claim live acceptance.
/// </summary>
public sealed class PhysicalDrawingUnitIntegrationTests
{
    private const string Fingerprint = "77a79e6b-edf6-49b6-bf27-631d72a28171";
    private const string Drawing = @"C:\UnitTests\Host.dwg";
    private static readonly DateTime ApprovedAt = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    private static ProjectProfile.DrawingUnitDeclaration Declaration() =>
        PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(
            Fingerprint, Drawing, 0, "Engineer", ApprovedAt, "Verified model is metric", "Project specification");

    private static PhysicalDrawingUnitPolicy.Resolution ApprovedUnits() =>
        PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Drawing, new[] { Declaration() });

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void HostAdapterInvalidNullListAlwaysProducesStructuredBlockingEvidence(int raw)
    {
        var profile = new ProjectProfile { DrawingUnitDeclarations = null! };
        var result = HostDrawingUnitService.Resolve(raw, Fingerprint, Drawing, profile);
        Assert.False(result.IsSupported);
        Assert.Null(result.EffectiveUnitCode);
        Assert.False(result.UsedDeclaration);
        Assert.False(HostDrawingUnitService.Scale(result).IsSupported);
        Assert.NotNull(HostDrawingUnitService.Finding(result, "unit-test"));
        using var evidence = JsonDocument.Parse(result.Evidence);
        Assert.Equal("physical-drawing-unit-v1", evidence.RootElement.GetProperty("contract").GetString());
        Assert.Equal(raw, evidence.RootElement.GetProperty("raw_insunits").GetInt32());
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("effective_unit").ValueKind);
        Assert.Equal("unresolved", evidence.RootElement.GetProperty("authority").GetString());
        Assert.Equal(PhysicalDrawingUnitPolicy.DeclarationInvalid,
            evidence.RootElement.GetProperty("failure_code").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void HostAdapterUsesExplicitUnsavedUnitsButNeverInheritsOtherDrawingApproval(int raw)
    {
        var profile = new ProjectProfile();
        profile.DrawingUnitDeclarations.Add(Declaration());
        const string otherGuid = "e15a3a38-c63e-4533-ab3e-acfa8b5b5743";
        var result = HostDrawingUnitService.Resolve(raw, otherGuid, null, profile);
        Assert.Equal(raw != 0, result.IsSupported);
        Assert.Equal(raw == 6, result.AllowsMetricSections);
        Assert.False(result.UsedDeclaration);
        Assert.Null(result.DeclarationDigest);
        Assert.Equal(raw, result.RawUnitCode);
        using var evidence = JsonDocument.Parse(result.Evidence);
        Assert.Equal(raw, evidence.RootElement.GetProperty("raw_insunits").GetInt32());
        Assert.Single(profile.DrawingUnitDeclarations);
        var sameGuid = HostDrawingUnitService.Resolve(raw, Fingerprint, null, profile);
        Assert.False(sameGuid.IsSupported);
        Assert.Equal(raw == 0 ? PhysicalDrawingUnitPolicy.IdentityInvalid : PhysicalDrawingUnitPolicy.OriginalUnitChanged,
            sameGuid.FailureCode);
    }

    [Fact]
    public void AbsentProfileAndValidEmptyListRetainExplicitUnitsWithoutInventingUnitlessAuthority()
    {
        foreach (var profile in new ProjectProfile?[] { null, new ProjectProfile() })
        {
            Assert.True(HostDrawingUnitService.Resolve(6, Fingerprint, null, profile).AllowsMetricSections);
            Assert.False(HostDrawingUnitService.Resolve(0, Fingerprint, null, profile).IsSupported);
        }
    }

    [Fact]
    public void AUnitlessDeclarationThatCivilContradictsBlocksPricingAndPointsToANewDeclaration()
    {
        // b25 (Codex 15:12): through the host boundary the scan uses — no SI, and no "confirm the recorded unit" advice
        // for a host that records none.
        var profile = new ProjectProfile();
        profile.DrawingUnitDeclarations.Add(PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, Drawing, 0,
            "SYNTHETIC TEST REVIEWER", DateTime.UtcNow, "SYNTHETIC: drawn in metres", "SYNTHETIC SOURCE"));
        var inches = new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 1);
        Assert.True(HostDrawingUnitService.Resolve(0, Fingerprint, Drawing, profile).IsSupported);
        var units = HostDrawingUnitService.Resolve(0, Fingerprint, Drawing, profile, civilUnits: inches);
        Assert.False(units.IsSupported);
        Assert.True(units.NeedsReview);
        var finding = HostDrawingUnitService.Finding(units, "unit-test")!;
        Assert.Contains("הצהר מחדש", finding.RecommendedAction);
        Assert.DoesNotContain("אישור היחידה הרשומה", finding.RecommendedAction);
    }

    [Fact]
    public void LegacyExplicitMetrePlanRemainsEligibleWithoutNewOptionalFields()
    {
        var plan = Plan(declared: false);
        Assert.Null(plan.PhysicalUnitCode);
        Assert.Null(plan.PhysicalUnitDeclarationDigest);
        Assert.Null(plan.SourceDrawingFingerprint);
        Assert.True(SectionInputIntegrityService.HasMetricUnitEvidence(plan));
        Assert.True(SectionInputIntegrityService.MatchesCurrentUnits(plan,
            PhysicalDrawingUnitPolicy.Resolve(6, Fingerprint, Drawing, null), Fingerprint, Drawing));
    }

    [Fact]
    public void LegacyUnitlessPlanDoesNotAcquireAuthorityFromAnApprovalMadeLater()
    {
        var plan = Plan(declared: false);
        plan.SourceUnitCode = 0;
        Assert.False(SectionInputIntegrityService.HasMetricUnitEvidence(plan));
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, ApprovedUnits(), Fingerprint, Drawing));
    }

    [Fact]
    public void DeclaredPlanHasRawZeroAndMetricEvidence_AndMatchesTheCurrentApprovedHost()
    {
        var plan = Plan(declared: true);
        Assert.Equal(0, plan.SourceUnitCode);
        Assert.Equal(6, plan.PhysicalUnitCode);
        Assert.True(SectionInputIntegrityService.HasMetricUnitEvidence(plan));
        Assert.True(SectionInputIntegrityService.MatchesCurrentUnits(plan, ApprovedUnits(),
            "{" + Fingerprint.ToUpperInvariant() + "}", "c:/unittests/child/../HOST.dwg"));
    }

    [Theory]
    [InlineData("missing-digest")]
    [InlineData("bad-digest")]
    [InlineData("missing-guid")]
    [InlineData("empty-guid")]
    [InlineData("relative-path")]
    [InlineData("nonmetric-physical-unit")]
    public void DeclaredPlanNeedsCompleteScopedPhysicalEvidence(string change)
    {
        var plan = Plan(declared: true);
        switch (change)
        {
            case "missing-digest": plan.PhysicalUnitDeclarationDigest = null; break;
            case "bad-digest": plan.PhysicalUnitDeclarationDigest = "short"; break;
            case "missing-guid": plan.SourceDrawingFingerprint = null; break;
            case "empty-guid": plan.SourceDrawingFingerprint = Guid.Empty.ToString(); break;
            case "relative-path": plan.SourceDrawing = "Host.dwg"; break;
            case "nonmetric-physical-unit": plan.PhysicalUnitCode = 4; break;
        }
        Assert.False(SectionInputIntegrityService.HasMetricUnitEvidence(plan));
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, ApprovedUnits(), Fingerprint, Drawing));
    }

    [Theory]
    [InlineData("live-guid")]
    [InlineData("live-path")]
    [InlineData("planned-path")]
    public void UnitGateIndependentlyRejectsDifferentDrawingOrSaveAs(string change)
    {
        var plan = Plan(declared: true);
        var liveGuid = change == "live-guid" ? Guid.NewGuid().ToString() : Fingerprint;
        var livePath = change == "live-path" ? @"C:\UnitTests\SavedAs.dwg" : Drawing;
        if (change == "planned-path") plan.SourceDrawing = @"C:\UnitTests\Another.dwg";
        // Deliberately reuse the approved resolution to isolate the unit gate's
        // own identity check, without relying on a prior outer scope predicate.
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, ApprovedUnits(), liveGuid, livePath));
    }

    [Fact]
    public void ChangedApprovalDigestInvalidatesCurrentUnitGate()
    {
        var plan = Plan(declared: true);
        var changed = Declaration();
        changed.Reason = "New reviewed engineering basis";
        var live = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Drawing, new[] { changed });
        Assert.True(live.AllowsMetricSections);
        Assert.NotEqual(plan.PhysicalUnitDeclarationDigest, live.DeclarationDigest);
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, live, Fingerprint, Drawing));
    }

    [Fact]
    public void ChangedRawUnitCannotBeHiddenByAnEffectiveMetreValue()
    {
        var plan = Plan(declared: true);
        var live = PhysicalDrawingUnitPolicy.Resolve(6, Fingerprint, Drawing, null);
        Assert.True(live.AllowsMetricSections);
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, live, Fingerprint, Drawing));
    }

    [Fact]
    public void HostScaleUsesDeclaredPhysicalUnitsAndKeepsRawUnitEvidence()
    {
        var units = ApprovedUnits();
        var scale = HostDrawingUnitService.Scale(units);
        Assert.True(scale.IsSupported);
        Assert.Equal("מטר", scale.LengthUnit);
        Assert.Equal("מ\"ר", scale.AreaUnit);
        Assert.Equal("מ\"ק", scale.VolumeUnit);
        Assert.Equal(10, scale.Length(10));
        Assert.Equal(12, scale.Area(12));
        Assert.Equal(24, scale.Volume(24));
        Assert.Equal(new[] { 1d, 2d, 3d, 4d }, scale.Bounds(new[] { 1d, 2d, 3d, 4d }));
        Assert.Null(HostDrawingUnitService.Finding(units, "unit-test"));
        Assert.Equal("INSUNITS=0", scale.SourceName); // Raw code is not rewritten as metres.
        using var evidence = JsonDocument.Parse(units.Evidence);
        Assert.Equal(0, evidence.RootElement.GetProperty("raw_insunits").GetInt32());
        Assert.Equal(6, evidence.RootElement.GetProperty("effective_unit").GetInt32());
        Assert.Equal(units.DeclarationDigest, evidence.RootElement.GetProperty("declaration_sha256").GetString());
    }

    [Fact]
    public void HostScaleNormalizesExplicitMillimetresWithoutAllowingMetricSectionAuthoring()
    {
        var units = PhysicalDrawingUnitPolicy.Resolve(4, Fingerprint, Drawing, null);
        var scale = HostDrawingUnitService.Scale(units);
        Assert.Equal(10.0, scale.Length(10_000), 9);
        Assert.Equal(12.0, scale.Area(12_000_000), 9);
        Assert.Equal(24.0, scale.Volume(24_000_000_000), 9);
        Assert.Equal(new[] { 1d, 2d, 3d, 4d }, scale.Bounds(new[] { 1000d, 2000d, 3000d, 4000d }));
        Assert.False(units.AllowsMetricSections);
        Assert.Null(HostDrawingUnitService.Finding(units, "unit-test"));
    }

    [Fact]
    public void UndeclaredUnitlessHostRetainsRawMeasurementLabelsAndBlockingFinding()
    {
        var units = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Drawing, null);
        var scale = HostDrawingUnitService.Scale(units);
        Assert.False(scale.IsSupported);
        Assert.Equal("יחידת שרטוט", scale.LengthUnit);
        Assert.Equal("יחידת שרטוט²", scale.AreaUnit);
        Assert.Equal("יחידת שרטוט³", scale.VolumeUnit);
        Assert.Equal(10, scale.Length(10)); // Raw evidence, not ten metres.
        var finding = HostDrawingUnitService.Finding(units, "unit-test");
        Assert.NotNull(finding);
        Assert.Equal(EstimateFindingCodes.UnitUnknown, finding.Code);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Equal("unit-test", finding.ProjectProfileId);
        Assert.Contains("INSUNITS=0", finding.Message);
        Assert.False(string.IsNullOrWhiteSpace(finding.RecommendedAction));
    }

    [Fact]
    public void InvalidDeclarationIsNotSilentlyIgnoredForAnotherExplicitMetreHost()
    {
        var invalid = Declaration();
        invalid.ApprovedBy = null;
        var units = PhysicalDrawingUnitPolicy.Resolve(6, Guid.NewGuid().ToString(),
            @"C:\UnitTests\Other.dwg", new[] { invalid });
        Assert.False(units.IsSupported); // Profile-wide malformed authority is an error.
        Assert.False(HostDrawingUnitService.Scale(units).IsSupported);
        Assert.NotNull(HostDrawingUnitService.Finding(units, "unit-test"));
    }

    [Fact]
    public void ApprovedUnitlessPlanCanPreviewPresentationReviewWithoutApprovingTheSection()
    {
        var plan = Plan(declared: true);
        var record = plan.Records.Single();
        Assert.True(SectionDiagnosticPreviewPolicy.CanPreview(plan, record));
        Assert.Null(SectionDiagnosticPreviewPolicy.PreviewBlockReason(plan, record));
        Assert.Equal(DeliveryStatus.ReviewRequired, record.Status);
        Assert.Equal(PlanAction.ReviewRequired, record.Action);
    }

    [Fact]
    public void SamePreviewWithoutDeclarationEvidenceRemainsBlocked()
    {
        var plan = Plan(declared: true);
        plan.PhysicalUnitDeclarationDigest = null;
        Assert.False(SectionDiagnosticPreviewPolicy.CanPreview(plan, plan.Records.Single()));
    }

    [Fact]
    public void ExplicitMetreRecoveryPreservesLegacyContractBytesWhenNewFingerprintFieldsAppear()
    {
        var plan = Plan(declared: false);
        var record = plan.Records.Single();
        var original = SectionVerificationRecoveryService.Identity(plan, record);
        Assert.Equal(LegacyContract(plan, record), original.InputContract);
        plan.SourceDrawingFingerprint = Fingerprint;
        plan.PhysicalUnitCode = 6;
        plan.PhysicalUnitEvidence = "new explicit-unit evidence";
        var refreshed = SectionVerificationRecoveryService.Identity(plan, record);
        Assert.Equal(original.InputContract, refreshed.InputContract);
        Assert.Null(SectionVerificationRecoveryPolicy.Rejection(original, refreshed, true, true, true, true));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("fingerprint")]
    [InlineData("effective-unit")]
    public void UnitlessProducerRecoveryBindsTheExactApprovedPhysicalUnitContract(string change)
    {
        var plan = Plan(declared: true);
        var record = plan.Records.Single();
        var original = SectionVerificationRecoveryService.Identity(plan, record);
        switch (change)
        {
            case "digest": plan.PhysicalUnitDeclarationDigest = new string('e', 64); break;
            case "fingerprint": plan.SourceDrawingFingerprint = Guid.NewGuid().ToString(); break;
            case "effective-unit": plan.PhysicalUnitCode = 4; break;
        }
        var current = SectionVerificationRecoveryService.Identity(plan, record);
        Assert.NotEqual(original.InputContract, current.InputContract);
        Assert.NotNull(SectionVerificationRecoveryPolicy.Rejection(original, current, true, true, true, true));
    }

    [Fact]
    public void DeclaredPlanJsonRoundTripPreservesRawEvidenceAndRecoveryIdentity()
    {
        var plan = Plan(declared: true);
        var restored = JsonSerializer.Deserialize<SectionPlan>(
            JsonSerializer.Serialize(plan, SectionsWorkflowService.Json), SectionsWorkflowService.Json)!;
        Assert.Equal(0, restored.SourceUnitCode);
        Assert.Equal(6, restored.PhysicalUnitCode);
        Assert.Equal(plan.PhysicalUnitDeclarationDigest, restored.PhysicalUnitDeclarationDigest);
        Assert.True(SectionInputIntegrityService.MatchesCurrentUnits(restored, ApprovedUnits(), Fingerprint, Drawing));
        Assert.Equal(SectionVerificationRecoveryService.Identity(plan, plan.Records.Single()),
            SectionVerificationRecoveryService.Identity(restored, restored.Records.Single()));
    }

    [Fact]
    public void MappingProfileEditCanRebaseWithoutChangingMeasuredPhysicalUnits()
    {
        var profile = new ProjectProfile { ProfileId = "unit-test" };
        profile.DrawingUnitDeclarations.Add(Declaration());
        var scan = Scan(profile);
        var originalUnitHash = scan.PhysicalUnitConfigurationHash;
        profile.Estimate.QuantitySources.Rules.Add(new()
        {
            RuleKey = "layer:HW-CURB", LayerPattern = "HW-CURB", CandidateCatalogCode = "U51.06.1900",
        });
        Assert.Equal(originalUnitHash, EstimateWorkflowService.UnitConfigurationHash(profile));
        EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile);

        // Real durable-file/hash preconditions, then the production rebase. No
        // CAD Database or measurement reread is used for an ordinary mapping edit.
        var directory = Path.Combine(Path.GetTempPath(), "mhd-unit-rebase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profile.json");
            File.WriteAllText(path, JsonSerializer.Serialize(profile));
            var saved = new ProjectProfileWriter.SaveResult(path, string.Empty, 2,
                ArtifactHash.Sha256OfFile(path));
            var rebased = EstimateWorkflowService.RebaseAfterProfileDecision(scan, profile, saved);
            Assert.Same(scan.PhysicalUnits, rebased.PhysicalUnits);
            Assert.Equal(originalUnitHash, rebased.PhysicalUnitConfigurationHash);
            Assert.Equal(scan.SourceDrawingHash, rebased.SourceDrawingHash);
            Assert.Equal(scan.DatabaseRevision, rebased.DatabaseRevision);
            Assert.Equal(0, rebased.PhysicalUnits!.RawUnitCode);
            Assert.Equal(6, rebased.PhysicalUnits.EffectiveUnitCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("revoke")]
    [InlineData("change")]
    public void UnitDeclarationChangesRequireRescanBeforeRebase(string change)
    {
        var profile = new ProjectProfile { ProfileId = "unit-test" };
        if (change != "add") profile.DrawingUnitDeclarations.Add(Declaration());
        var scan = Scan(profile);
        switch (change)
        {
            case "add": profile.DrawingUnitDeclarations.Add(Declaration()); break;
            case "revoke": profile.DrawingUnitDeclarations.Clear(); break;
            case "change": profile.DrawingUnitDeclarations[0].Reason = "Changed engineering declaration"; break;
        }
        Assert.NotEqual(scan.PhysicalUnitConfigurationHash, EstimateWorkflowService.UnitConfigurationHash(profile));
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile));
        // The unit gate runs before any durable profile publication assumptions.
        var error = Assert.Throws<InvalidOperationException>(() =>
            EstimateWorkflowService.RebaseAfterProfileDecision(scan, profile,
                new ProjectProfileWriter.SaveResult("not-published", string.Empty, 2, new string('a', 64))));
        Assert.Contains("נדרשת סריקה חדשה", error.Message);
    }

    [Fact]
    public void LegacyScanWithoutUnitConfigurationHashToleratesOnlyEmptyDeclarations()
    {
        var profile = new ProjectProfile { ProfileId = "unit-test" };
        var scan = Scan(profile);
        scan.PhysicalUnitConfigurationHash = null;
        EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile);
        profile.DrawingUnitDeclarations.Add(Declaration());
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile));
        profile.DrawingUnitDeclarations = null!;
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile));
    }

    // ---- b24 (E4, la-038-ew): reviewed explicit units across scan, sections and recovery ----

    private static readonly PhysicalDrawingUnitPolicy.HostExtents ItmLike = new(195_496.09, 628_759.46, 200_124.46, 630_468.81);

    private static ProjectProfile.DrawingUnitDeclaration ReviewedFeetHost(string kind, int physical, string reason = "SYNTHETIC: reviewed") =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Drawing, 2, kind, physical,
            "SYNTHETIC TEST REVIEWER", ApprovedAt, reason, "SYNTHETIC TEST SOURCE");

    [Fact]
    public void ASuspectedHostIsMeasuredInDrawingUnitsWithAReviewFinding_NeverAsSI()
    {
        var units = HostDrawingUnitService.Resolve(2, Fingerprint, Drawing, new ProjectProfile(), ItmLike);
        Assert.False(units.IsSupported);
        var scale = HostDrawingUnitService.Scale(units);
        Assert.False(scale.IsSupported);
        Assert.Equal(1.0, scale.LinearToMetres);
        var finding = HostDrawingUnitService.Finding(units, "unit-test")!;
        Assert.Contains("נדרשת בדיקת יחידות", finding.Title);
        Assert.Contains("ITM", finding.Message);
        Assert.Contains("אישור היחידה הרשומה", finding.RecommendedAction);
    }

    [Fact]
    public void ConfirmedFeetPriceAtTheFeetFactorAndStayOutOfSections()
    {
        var profile = new ProjectProfile();
        profile.DrawingUnitDeclarations.Add(ReviewedFeetHost(PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2));
        PhysicalDrawingUnitPolicy.LinkReviews(profile, profile.DrawingUnitDeclarations[0], DateTime.UtcNow);
        var units = HostDrawingUnitService.Resolve(2, Fingerprint, Drawing, profile, ItmLike);
        Assert.True(HostDrawingUnitService.Scale(units).IsSupported);
        Assert.Equal(0.3048, HostDrawingUnitService.Scale(units).LinearToMetres);
        Assert.Null(HostDrawingUnitService.Finding(units, "unit-test"));
        Assert.False(units.AllowsMetricSections);
    }

    private static SectionPlan ReviewedMetresPlan(PhysicalDrawingUnitPolicy.Resolution units)
    {
        var plan = Plan(declared: true);
        plan.SourceUnitCode = 2; plan.SourceUnitName = "Feet";
        plan.PhysicalUnitCode = units.EffectiveUnitCode;
        plan.PhysicalUnitDeclarationDigest = units.DeclarationDigest;
        plan.PhysicalUnitEvidence = units.Evidence;
        return plan;
    }

    [Fact]
    public void MetresReviewedOnAFeetHostPlanVerifyAndBindRecoveryLikeTheUnitlessRoute()
    {
        var units = ResolveLinked(2, Fingerprint, Drawing,
            new[] { ReviewedFeetHost(PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6) }, ItmLike);
        var plan = ReviewedMetresPlan(units);
        Assert.True(SectionInputIntegrityService.HasMetricUnitEvidence(plan));
        Assert.True(SectionInputIntegrityService.MatchesCurrentUnits(plan, units, Fingerprint, Drawing));
        // VERIFY refuses the same plan once the decision changed or was revoked (the host is then suspect again).
        var otherDecision = ResolveLinked(2, Fingerprint, Drawing,
            new[] { ReviewedFeetHost(PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6, "SYNTHETIC: another reason") });
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, otherDecision, Fingerprint, Drawing));
        var revoked = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Drawing,
            Array.Empty<ProjectProfile.DrawingUnitDeclaration>(), hostExtents: ItmLike);
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, revoked, Fingerprint, Drawing));
        Assert.False(SectionInputIntegrityService.MatchesCurrentUnits(plan, units, Fingerprint, @"C:\UnitTests\Copy.dwg"));
        // Recovery binds the exact reviewed authority, as for a unitless host.
        var record = plan.Records.Single();
        var original = SectionVerificationRecoveryService.Identity(plan, record);
        Assert.NotEqual(LegacyContract(plan, record), original.InputContract);
        plan.PhysicalUnitDeclarationDigest = otherDecision.DeclarationDigest;
        Assert.NotEqual(original.InputContract, SectionVerificationRecoveryService.Identity(plan, record).InputContract);
    }

    [Fact]
    public void AReviewedFeetPlanOrAFeetPlanWithoutDecisionHasNoMetricSectionEvidence()
    {
        var feet = ResolveLinked(2, Fingerprint, Drawing,
            new[] { ReviewedFeetHost(PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2) });
        Assert.False(SectionInputIntegrityService.HasMetricUnitEvidence(ReviewedMetresPlan(feet)));
        var undecided = Plan(declared: false);
        undecided.SourceUnitCode = 2;
        Assert.False(SectionInputIntegrityService.HasMetricUnitEvidence(undecided));
    }

    [Fact]
    public void AnyReviewedUnitChangeRequiresARescan_AnUnchangedUnitlessListKeepsItsHash()
    {
        var unitless = new ProjectProfile();
        unitless.DrawingUnitDeclarations.Add(Declaration());
        // The frozen pre-b24 serialization of the same list: no DecisionKind key.
        Assert.DoesNotContain("DecisionKind", JsonSerializer.Serialize(unitless.DrawingUnitDeclarations));
        var reviewed = new ProjectProfile();
        reviewed.DrawingUnitDeclarations.Add(ReviewedFeetHost(PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2));
        var scan = Scan(reviewed);
        EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, reviewed);
        reviewed.DrawingUnitDeclarations[0] = ReviewedFeetHost(PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6);
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, reviewed));
    }


    // b24 (Codex 13:02 §2): a reviewed explicit-unit decision is valid only with the review record naming its digest —
    // written together by the units review, as LinkReviews does here.
    private static PhysicalDrawingUnitPolicy.Resolution ResolveLinked(int raw, string fingerprint, string path,
        IEnumerable<ProjectProfile.DrawingUnitDeclaration> decisions, PhysicalDrawingUnitPolicy.HostExtents? extents = null)
    {
        var profile = new ProjectProfile();
        foreach (var decision in decisions)
        {
            profile.DrawingUnitDeclarations.Add(decision);
            if (decision.DecisionKind != null) PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, DateTime.UtcNow);
        }
        return PhysicalDrawingUnitPolicy.Resolve(raw, fingerprint, path, profile.DrawingUnitDeclarations,
            hostExtents: extents, reviews: profile.DrawingUnitReviews);
    }

    [Fact]
    public void ARecordedReviewIsUnitContentForEveryRescanGuard()
    {
        // Codex 13:02 §3 / 13:11: the null branch (a scan without the hash) and a review-only change both need a rescan.
        var record = PhysicalDrawingUnitPolicy.NewReviewRecord(
            PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Drawing, null, hostExtents: ItmLike), Fingerprint, Drawing, null, DateTime.UtcNow)!;
        var legacyProfile = new ProjectProfile();
        var legacyScan = Scan(legacyProfile);
        legacyScan.PhysicalUnitConfigurationHash = null;
        EstimateWorkflowService.RequireUnchangedPhysicalUnits(legacyScan, legacyProfile);   // no unit content: tolerated
        legacyProfile.DrawingUnitReviews = new() { record };
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireUnchangedPhysicalUnits(legacyScan, legacyProfile));

        var profile = new ProjectProfile();
        var scan = Scan(profile);
        EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile);               // unchanged context stays valid
        profile.DrawingUnitReviews = new() { record };
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireUnchangedPhysicalUnits(scan, profile));
        // A profile without reviews keeps the hash it always had.
        Assert.Equal(ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new ProjectProfile().DrawingUnitDeclarations)),
            EstimateWorkflowService.UnitConfigurationHash(new ProjectProfile()));
        Assert.Equal(ScanUnitEvidence.EmptyUnitConfigurationHash, EstimateWorkflowService.UnitConfigurationHash(new ProjectProfile()));
    }

    [Fact]
    public void TheTrustGuardRefusesAScanWithoutTheContractOrWithACadRecordMissingItsEvidence()
    {
        var units = PhysicalDrawingUnitPolicy.Resolve(6, Fingerprint, Drawing, null);
        NeutralQuantityRecord Cad(bool stamped)
        {
            var p = new Dictionary<string, string> { ["cad_entity_database_insunits"] = "Meters" };
            if (stamped) QuantityPhysicalUnits.Stamp(p, units);
            if (stamped)
                foreach (var key in p.Keys.Where(k => k.StartsWith("physical_", StringComparison.Ordinal)).ToList())
                {   // the metadata reader adds the "cad_" prefix to what Stamp writes
                    p["cad_" + key] = p[key]; p.Remove(key);
                }
            return new NeutralQuantityRecord
            {
                RecordId = "q", ProjectProfileId = "p", RunId = "r",
                Source = new QuantitySource { Drawing = "d", DrawingHash = new string('a', 64), Handle = "1", EntityType = "LINE", Layer = "L" },
                Measurement = new QuantityMeasurement { Kind = "length", Method = "line-length", RawValue = 1, Unit = "מטר", Parameters = p },
            };
        }
        var scan = Scan(new ProjectProfile());
        scan.PhysicalUnits = units;
        scan.Records.Add(Cad(stamped: true));
        scan.PhysicalUnitsContract = ScanUnitEvidence.Contract;
        EstimateWorkflowService.RequireTrustedUnitEvidence(scan, "synthetic");
        scan.PhysicalUnitsContract = null;
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireTrustedUnitEvidence(scan, "synthetic"));
        scan.PhysicalUnitsContract = ScanUnitEvidence.Contract;
        scan.Records.Add(Cad(stamped: false));
        Assert.Throws<InvalidOperationException>(() => EstimateWorkflowService.RequireTrustedUnitEvidence(scan, "synthetic"));
    }

    private static EstimateWorkflowService.ScanResult Scan(ProjectProfile profile) => new()
    {
        RunId = "unit-scan", ProjectProfileId = profile.ProfileId, ProfileSource = @"C:\UnitTests\profile.yaml",
        SourceDrawing = Drawing, SourceDrawingHash = new string('d', 64), DatabaseRevision = Fingerprint + ":12",
        PhysicalUnits = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Drawing, profile.DrawingUnitDeclarations),
        PhysicalUnitConfigurationHash = EstimateWorkflowService.UnitConfigurationHash(profile),
    };

    private static SectionPlan Plan(bool declared)
    {
        var units = ApprovedUnits();
        var plan = new SectionPlan
        {
            RunId = "unit-plan", ProjectProfileId = "unit-test", ProjectProfileHash = new string('b', 64),
            SourceDrawing = Drawing, SourceDatabaseRevision = Fingerprint + ":12",
            SourceUnitCode = declared ? 0 : 6, SourceUnitName = declared ? "Undefined" : "Meters",
            PhysicalUnitCode = declared ? 6 : null,
            PhysicalUnitDeclarationDigest = declared ? units.DeclarationDigest : null,
            SourceDrawingFingerprint = declared ? Fingerprint : null,
            PhysicalUnitEvidence = declared ? units.Evidence : null,
            Status = DeliveryStatus.ReviewRequired,
        };
        var record = new SectionPlanRecord
        {
            RecordId = "row-1", SectionId = "STA-100", LogicalKey = "section:100",
            InputFingerprint = new string('a', 64),
            Cl = new ClSourceRecord
            {
                RecordId = "row-1", SourceDrawing = "CL.dwg", SourceDrawingHash = new string('c', 64),
                SourceHandle = "AB12", SourceEntityType = "LINE", SourceLayer = "CL",
                SourceEndpoints = new[] { 0d, 20d, 0d, -20d }, WcsEndpoints = new[] { 0d, 20d, 0d, -20d },
            },
            SelectedAlignment = "MAIN",
            SelectedCrossing = new AlignmentCrossing
                { AlignmentName = "MAIN", Point = new[] { 0d, 0d }, Station = 100, TangentDeg = 0 },
            Station = 100, LeftExtent = 20, RightExtent = 20,
            PlannedLayoutPosition = new[] { 1000d, 2000d },
            Status = DeliveryStatus.ReviewRequired, Action = PlanAction.ReviewRequired,
        };
        record.PlannedSources.Add(Surface("EG", "A1"));
        record.PlannedSources.Add(Surface("DESIGN", "B2"));
        record.Findings.Add(new DeliveryFinding
        {
            Code = SectionFindingCodes.PresentationCoverageMissing, Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.ReviewRequired, Title = "Strip names await engineering review",
        });
        plan.Records.Add(record);
        return plan;
    }

    private static SectionSourcePlan Surface(string name, string handle) => new()
    {
        SourceName = name, SourceType = "surface", SourceHandle = handle,
        NativeSampleCapability = true, PlannedState = "sampled", Required = true, Status = DeliveryStatus.Ready,
    };

    // Frozen pre-declaration serializer contract. New optional explicit-metre
    // metadata must not invalidate the existing native81 producer solely by bump.
    private static string LegacyContract(SectionPlan plan, SectionPlanRecord record) =>
        ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
        {
            record.InputFingerprint,
            Cl = new { record.Cl.RecordId, record.Cl.SourceDrawing, record.Cl.SourceDrawingHash,
                record.Cl.SourceDrawingPath, record.Cl.SourceHandle, record.Cl.SourceXref,
                record.Cl.SourceEntityType, record.Cl.SourceLayer, record.Cl.SourceEndpoints,
                record.Cl.WcsEndpoints, record.Cl.XrefTransform, record.Cl.Length, record.Cl.AngleDeg,
                record.Cl.CandidateSectionNumber,
                Labels = record.Cl.NearbyLabels.OrderBy(label => label, StringComparer.Ordinal) },
            record.SelectedAlignment, record.SelectedCrossing,
            record.Station, record.SkewDeg, record.LeftExtent, record.RightExtent,
            Sources = record.PlannedSources.Select(s => new { s.SourceName, s.SourceType, s.SourceHandle,
                s.PlannedState, s.Required, s.AdapterRequired, s.NativeSampleCapability, s.StyleMapping }),
            record.ProjectedEntities, record.ProjectedSystems, record.TrafficDirections,
            record.PresentationCoverage, record.PlannedStyles, plan.SourceUnitCode,
        }, SectionsWorkflowService.Json));
}
