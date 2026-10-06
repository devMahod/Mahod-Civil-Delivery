using System;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Evidence = MahodAI.CivilDelivery.Shared.SectionUnusedOfficeBlockRecoveryPolicy.Evidence;
using View = MahodAI.CivilDelivery.Shared.SectionFurnitureLogic.OfficeCarView;

namespace MahodAI.Core.Tests;

/// <summary>Admission-policy tests only; no native reference query, retirement, import or approval is executed.</summary>
public sealed class SectionUnusedOfficeBlockRecoveryPolicyTests
{
    // Recorded native53 front BD8938 evidence from native53-diag-B-20260909-094153-351a30ee.json.
    // Equality of the source/live entity fingerprint is an input here, not a new independent native read.
    private const string EntityFingerprint = "7dedc64cf33e8eb04c180feac0dd2848b99ae566ded01c31dcfb6bb63056804a";
    private const string StoredGeometry = "d3aa33f9864cf97fe23c66d7e9b76f82627d0a5084b01aeb4989a97fc755369e";
    private const string LiveGeometry = "a877240c603c8f1d8029d772a14b1fc21444e4cfeeb8c40e142924b148813c32";

    private static Evidence Front() => new(
        SelectedScope: true, View: View.Front,
        BlockName: SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(View.Front),
        Comments: SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(View.Front, StoredGeometry),
        SourceEntityFingerprint: EntityFingerprint, ExistingEntityFingerprint: EntityFingerprint,
        OrdinaryLocalDefinition: true, HeaderMatches: true, AttestationMatches: false,
        CompleteActiveReferenceCount: 0, SingletonPurgeRetainedExactDefinition: true,
        RetirementNameAvailable: true);

    private static string? Reject(Evidence evidence) =>
        SectionUnusedOfficeBlockRecoveryPolicy.RetirementRejection(evidence);

    [Fact]
    public void RecordedFrontMismatchCanAdmitUnusedRetirementButDoesNotRestampOrValidateOldDefinition()
    {
        var evidence = Front(); var before = JsonSerializer.Serialize(evidence);
        Reject(evidence).Should().BeNull();
        JsonSerializer.Serialize(evidence).Should().Be(before);
        evidence.Comments.Should().Contain(StoredGeometry).And.NotContain(LiveGeometry);
        SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(
            View.Front, SectionOfficeVehicleAssetEvidenceLogic.FrontSha256[..16],
            evidence.BlockName, evidence.Comments, LiveGeometry, out var error).Should().BeFalse();
        error.Should().Contain("comments");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ExactUnusedSourceEquivalentDefinitionMayBeRetiredForHeaderOrAttestationMismatch(
        bool headerMatches, bool attestationMatches)
    {
        Reject(Front() with { HeaderMatches = headerMatches, AttestationMatches = attestationMatches }).Should().BeNull();
    }

    [Fact]
    public void ExactSourceOnlyLegacyProvenanceMayBeRetiredWithoutInventingGeometryAttestation()
    {
        var evidence = Front() with { Comments = SectionOfficeVehicleAssetEvidenceLogic.SourceComment(View.Front) };
        Reject(evidence).Should().BeNull();
        evidence.Comments.Should().NotContain("geometry_sha256");
    }

    [Fact]
    public void RearUsesItsOwnExactPinnedNameAndComment()
    {
        // Synthetic equal entity fingerprints: this case tests asset routing, not rear geometry.
        var evidence = Front() with
        {
            View = View.Rear,
            BlockName = SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(View.Rear),
            Comments = SectionOfficeVehicleAssetEvidenceLogic.SourceComment(View.Rear),
            SourceEntityFingerprint = new string('a', 64), ExistingEntityFingerprint = new string('a', 64),
        };
        Reject(evidence).Should().BeNull();
    }

    [Fact]
    public void ShaHexCaseDoesNotChangeSourceEntityEquality()
    {
        Reject(Front() with { ExistingEntityFingerprint = EntityFingerprint.ToUpperInvariant() }).Should().BeNull();
    }

    [Theory]
    [InlineData("foreign-name")]
    [InlineData("case-changed-name")]
    [InlineData("foreign-comment")]
    [InlineData("null-comment")]
    [InlineData("wrong-asset-comment")]
    [InlineData("trailing-comment")]
    [InlineData("malformed-geometry-comment")]
    [InlineData("unknown-asset")]
    [InlineData("wrong-asset-name")]
    public void MissingOrAlteredExactToolProvenanceCannotAuthorizeRetirement(string issue)
    {
        var baseline = Front();
        var evidence = issue switch
        {
            "foreign-name" => baseline with { BlockName = "MANUAL-FRONT-CAR" },
            "case-changed-name" => baseline with { BlockName = baseline.BlockName.ToLowerInvariant() },
            "foreign-comment" => baseline with { Comments = "Manually imported office car" },
            "null-comment" => baseline with { Comments = null },
            "wrong-asset-comment" => baseline with { Comments = SectionOfficeVehicleAssetEvidenceLogic.SourceComment(View.Rear) },
            "trailing-comment" => baseline with { Comments = baseline.Comments + "; allow-recovery=true" },
            "malformed-geometry-comment" => baseline with { Comments = SectionOfficeVehicleAssetEvidenceLogic.SourceComment(View.Front) + "; geometry_sha256=unknown" },
            "unknown-asset" => baseline with { View = (View)999 },
            "wrong-asset-name" => baseline with { View = View.Rear },
            _ => throw new ArgumentOutOfRangeException(nameof(issue)),
        };
        Reject(evidence).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing-source")]
    [InlineData("missing-existing")]
    [InlineData("malformed-source")]
    [InlineData("malformed-existing")]
    public void ChangedOrUnprovedEntitiesAreNeverAHeaderOnlyRecovery(string issue)
    {
        var baseline = Front();
        var evidence = issue switch
        {
            "changed" => baseline with { ExistingEntityFingerprint = new string('b', 64) },
            "missing-source" => baseline with { SourceEntityFingerprint = "" },
            "missing-existing" => baseline with { ExistingEntityFingerprint = "" },
            "malformed-source" => baseline with { SourceEntityFingerprint = new string('g', 64) },
            "malformed-existing" => baseline with { ExistingEntityFingerprint = "sha256:" + EntityFingerprint },
            _ => throw new ArgumentOutOfRangeException(nameof(issue)),
        };
        Reject(evidence).Should().Contain("entities");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(97)]
    [InlineData(-1)]
    [InlineData(null)]
    public void AnyActiveOrUnprovedCompleteReferenceCountBlocksRetirement(int? count)
    {
        Reject(Front() with { CompleteActiveReferenceCount = count }).Should().Contain("references");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void EmptyBlockReferenceListAloneDoesNotReplaceExactSingletonPurgeProof(bool? purgeRetained)
    {
        Reject(Front() with { SingletonPurgeRetainedExactDefinition = purgeRetained }).Should().Contain("Purge");
    }

    [Fact]
    public void DynamicAnonymousXrefOrLayoutDefinitionVerdictCannotAuthorizeRetirement()
    {
        Reject(Front() with { OrdinaryLocalDefinition = false }).Should().Contain("Dynamic, anonymous, XREF or layout");
    }

    [Fact]
    public void OccupiedRetirementNameIsNeverOverwritten()
    {
        Reject(Front() with { RetirementNameAvailable = false }).Should().Contain("occupied");
    }

    [Fact]
    public void CurrentMatchingDefinitionMustBeReusedEvenIfUnusedAndPurgeable()
    {
        Reject(Front() with { HeaderMatches = true, AttestationMatches = true }).Should().Contain("reused");
    }

    [Fact]
    public void BatchScopeDoesNotGainSelectedRetirementAuthority()
    {
        Reject(Front() with { SelectedScope = false }).Should().Contain("selected scope");
    }
}
