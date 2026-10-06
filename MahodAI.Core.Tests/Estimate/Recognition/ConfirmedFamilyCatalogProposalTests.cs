using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using FamilyDecision = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>Synthetic approval/measurement fixtures only; no prices, DWG, AI or native host execution.</summary>
public sealed class ConfirmedFamilyCatalogProposalTests
{
    private static EngineerBoqLibrary Library => FamilyFixtures.Library;
    private static NeutralQuantityRecord Record(string layer, string pattern="ANSI31", string? pset=null, string? source=FamilyFixtures.Ha,
        double value=100,string? hash=null)
    {
        var r=FamilyFixtures.HatchArea(layer,value,pattern,pset,source,drawingHash:hash);
        r.Measurement.Parameters[EvidenceKeys.Schema+EvidenceKeys.StatusSuffix]="read";
        r.Classification.RuleKey="layer:"+layer+"|area";
        return r;
    }
    private static FamilyDecision Approve(params NeutralQuantityRecord[] records) =>
        FamilyDecisionPolicy.CreateApproval("road-pavement",EngineerBoqDraftBuilder.RecognitionGroups(records,Library),
            FamilyFixtures.Hatch,Library,FamilyFixtures.Approver,"Synthetic catalog continuity test",FamilyFixtures.T1);
    private static CatalogSnapshot Catalog()
    {
        var c=new CatalogSnapshot{SnapshotId="SYNTHETIC",FileHash=new string('c',64)};
        foreach(var code in Library.Rules.Single(r=>r.Id=="road-pavement").Emits.Select(e=>e.Code).Distinct())
            c.Items[code]=new CatalogItem{Code=code,Description="SYNTHETIC family recipe candidate",UnitRaw="מ\"ר"};
        return c;
    }
    private static List<MappingProposal> Propose(NeutralQuantityRecord r,CatalogEvidenceBridge.Evidence e) =>
        MappingProposalEngine.Propose(new[]{new MappingProposalEngine.DiscoveredGroup(r.Classification.RuleKey!,r.Source.Layer,
            "area","מ\"ר",1,100,null,e)},Catalog());

    [Fact]
    public void ConfirmedMeaningOnTwoRandomLayersFeedsExistingCatalogCandidatesWithoutApproval()
    {
        var a=Record("QZ987");var b=Record("RM456");var decision=Approve(a,b);
        var before=JsonSerializer.Serialize(new[]{a,b});var decisionBefore=JsonSerializer.Serialize(decision);
        var local=CatalogEvidenceBridge.ForProposalGroups(new[]{a,b},Library);
        local.Values.Should().OnlyContain(e=>e.FamilyCandidateCodes.Count==0);
        var result=CatalogEvidenceBridge.ForProposalGroups(new[]{a,b},Library,new[]{decision});
        foreach(var r in new[]{a,b})
        {
            var evidence=result[r.Classification.RuleKey!];
            evidence.RecognisedFamily.Should().Be("road-pavement");
            evidence.ConfirmedFamilyDecisionIds.Should().Equal(decision.DecisionId);
            evidence.FamilyDecisionResolutions.Should().OnlyContain(s=>s.State==FamilyDecisionState.Applied);
            var proposals=Propose(r,evidence);proposals.Should().NotBeEmpty();
            proposals.Should().OnlyContain(p=>p.Status=="PROPOSED_UNAPPROVED");
            proposals.Should().OnlyContain(p=>p.Reasons.Any(reason=>reason.Contains(decision.DecisionId!)&&reason.Contains("לא אישור")));
        }
        JsonSerializer.Serialize(new[]{a,b}).Should().Be(before);
        JsonSerializer.Serialize(decision).Should().Be(decisionBefore);
        decision.ItemApprovals.Should().BeEmpty();
    }

    [Fact]
    public void ReloadAndRemeasurePreserveMeaningButChangedEvidenceStalesOnlyAffectedLayer()
    {
        var a=Record("QZ987");var b=Record("RM456");var decision=Approve(a,b);
        var reloaded=JsonSerializer.Deserialize<FamilyDecision>(JsonSerializer.Serialize(decision))!;
        var changed=Record("QZ987","ANSI32");var stable=Record("RM456",value:125,hash:new string('d',64));
        var result=CatalogEvidenceBridge.ForProposalGroups(new[]{changed,stable},Library,new[]{reloaded});
        result[changed.Classification.RuleKey!].ConfirmedFamilyDecisionIds.Should().BeEmpty();
        result[changed.Classification.RuleKey!].FamilyDecisionResolutions.Should().Contain(s=>s.State==FamilyDecisionState.Stale);
        result[changed.Classification.RuleKey!].FamilyCandidateCodes.Should().BeEmpty();
        result[stable.Classification.RuleKey!].ConfirmedFamilyDecisionIds.Should().Equal(decision.DecisionId);
        reloaded.ItemApprovals.Should().BeEmpty();
    }

    [Fact]
    public void ChangedSourceNamespaceIsNotCoveredByOldApproval()
    {
        var a=Record("QZ987");var decision=Approve(a);var other=Record("QZ987",source:FamilyFixtures.Gm);
        var e=CatalogEvidenceBridge.ForProposalGroups(new[]{other},Library,new[]{decision})[other.Classification.RuleKey!];
        e.ConfirmedFamilyDecisionIds.Should().BeEmpty();e.FamilyCandidateCodes.Should().BeEmpty();
        e.FamilyDecisionResolutions.Should().OnlyContain(r=>r.State!=FamilyDecisionState.Applied);
    }

    [Fact]
    public void ChangedSiblingEvidenceCannotBeHiddenByFilteringMappedRecordsFirst()
    {
        var a=Record("QZ987");var b=Record("QZ987");var decision=Approve(a,b);
        b.Measurement.Parameters[EvidenceKeys.Hatch]=b.Measurement.Parameters[EvidenceKeys.Hatch].Replace("ANSI31","ANSI32");
        b.Classification.CandidateCatalogCode="SYNTHETIC-ALREADY-MAPPED";
        var e=CatalogEvidenceBridge.ForProposalGroups(new[]{a,b},Library,new[]{decision})[a.Classification.RuleKey!];
        e.ConfirmedFamilyDecisionIds.Should().BeEmpty();
        e.FamilyDecisionResolutions.Should().Contain(r=>r.State==FamilyDecisionState.Stale);
        b.Classification.CandidateCatalogCode.Should().Be("SYNTHETIC-ALREADY-MAPPED");
    }

    [Fact]
    public void PartialApprovalCannotSpreadOverAnotherRecognitionGroupSharingProposalKey()
    {
        var a=Record("QZ987");var b=Record("RM456");var decision=Approve(a);
        b.Classification.RuleKey=a.Classification.RuleKey;
        var e=CatalogEvidenceBridge.ForProposalGroups(new[]{a,b},Library,new[]{decision})[a.Classification.RuleKey!];
        e.ConfirmedFamilyDecisionIds.Should().BeEmpty();e.FamilyCandidateCodes.Should().BeEmpty();
        e.FamilyDecisionResolutions.Should().Contain(r=>r.State==FamilyDecisionState.NotCovered);
    }

    [Fact]
    public void MultiItemRecipeIsCandidatesOnlyNotMappingsOrPrices()
    {
        var a=Record("QZ987");var decision=Approve(a);
        var e=CatalogEvidenceBridge.ForProposalGroups(new[]{a},Library,new[]{decision})[a.Classification.RuleKey!];
        e.FamilyCandidateCodes.Count.Should().BeGreaterThan(1);
        var proposals=Propose(a,e);proposals.Should().HaveCountGreaterThan(1);
        proposals.Should().OnlyContain(p=>p.Status=="PROPOSED_UNAPPROVED"&&p.Reasons.Any(r=>r.Contains("מתכון מרובה סעיפים אינו שיוך אוטומטי")));
        a.Classification.CandidateCatalogCode.Should().BeNullOrEmpty();decision.ItemApprovals.Should().BeEmpty();
        decision.ParameterOverrides.Should().BeEmpty();
    }

    [Fact]
    public void NewContradictingCadEvidenceDoesNotGetOverruledBySavedFamily()
    {
        var a=Record("QZ987");var decision=Approve(a);
        var changed=Record("QZ987",pset:"SIDEWALK");
        var e=CatalogEvidenceBridge.ForProposalGroups(new[]{changed},Library,new[]{decision})[changed.Classification.RuleKey!];
        e.ConfirmedFamilyDecisionIds.Should().BeEmpty();
        e.FamilyDecisionResolutions.Should().Contain(r=>r.State==FamilyDecisionState.Stale);
    }

    [Fact]
    public void RevokedOrTamperedApprovalSuppliesNoFamilyCandidates()
    {
        var a=Record("QZ987");var decision=Approve(a);
        var revoked=FamilyDecisionPolicy.Revoke(new[]{decision},decision.DecisionId!,"Synthetic revocation");
        var noDecision=CatalogEvidenceBridge.ForProposalGroups(new[]{a},Library,revoked)[a.Classification.RuleKey!];
        noDecision.ConfirmedFamilyDecisionIds.Should().BeEmpty();noDecision.FamilyCandidateCodes.Should().BeEmpty();
        decision.ApprovedBy=null;
        var tampered=CatalogEvidenceBridge.ForProposalGroups(new[]{a},Library,new[]{decision})[a.Classification.RuleKey!];
        tampered.ConfirmedFamilyDecisionIds.Should().BeEmpty();tampered.FamilyCandidateCodes.Should().BeEmpty();
        tampered.FamilyDecisionResolutions.Should().OnlyContain(r=>r.State!=FamilyDecisionState.Applied);
    }

    [Fact]
    public void AProvenSemanticPartitionNeverBecomesWholeMixedGroupCatalogMeaning()
    {
        NeutralQuantityRecord Counted(string text)
        {
            var r=FamilyFixtures.Rec(FamilyFixtures.Gm,"QZ987","count","block-count",1,"unit",block:"TEST_OBJECT");
            r.Classification.RuleKey="layer:QZ987|count";
            r.Measurement.Parameters[EvidenceKeys.Schema+EvidenceKeys.StatusSuffix]="read";
            r.Measurement.Parameters[EvidenceKeys.BlockAttributes+EvidenceKeys.StatusSuffix]="read";
            r.Measurement.Parameters[EvidenceKeys.BlockAttributes]=JsonSerializer.Serialize(new[]{new{tag="TEST_CLASS",value=text}});
            return r;
        }
        var a=Counted("BUS SHELTER");var b=Counted("BENCH");
        var whole=EngineerBoqDraftBuilder.RecognitionGroups(new[]{a,b},Library).Single();
        var decision=FamilyDecisionPolicy.CreatePartitionApproval("bus-shelters",whole,new[]{a.RecordId},Library,
            FamilyFixtures.Approver,"Synthetic partition only",FamilyFixtures.T1);
        var e=CatalogEvidenceBridge.ForProposalGroups(new[]{a,b},Library,new[]{decision})[a.Classification.RuleKey!];
        e.ConfirmedFamilyDecisionIds.Should().BeEmpty();
        e.FamilyDecisionResolutions.Should().Contain(r=>r.State==FamilyDecisionState.Applied);
        e.FamilyDecisionResolutions.Should().Contain(r=>r.State==FamilyDecisionState.NotCovered);
        e.FamilyCandidateCodes.Should().BeEmpty();
    }
}
