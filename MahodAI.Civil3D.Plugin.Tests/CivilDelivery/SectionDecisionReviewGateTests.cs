using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public class SectionDecisionReviewGateTests
{
    [Fact]
    public void NarrowSectionPalette_DoesNotForceContentWiderThan540PixelViewport()
    {
        var markup = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(TestPaths.PluginSourceDir,
            "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
        var rootPanel = System.Linq.Enumerable.Single(markup.Root!.Descendants(),
            node => node.Name.LocalName == "DockPanel" && node.Attribute("MinWidth") != null);
        ((double?)rootPanel.Attribute("MinWidth") ?? 0).Should().BeLessThanOrEqualTo(480);
    }

    [Theory]
    [InlineData(false, SectionGuidedActionKind.ApproveRow)]
    [InlineData(true, SectionGuidedActionKind.NameSpans)]
    public void HashBackedGeometryFailure_OffersCurrentDecision_NotApplyOrVerify(
        bool rowApproved, SectionGuidedActionKind expected)
    {
        var plan = Plan();
        var record = plan.Records[0];
        record.PresentationCoverage.RowAuthorityState = rowApproved ? "authoritative" : "suppressed";
        record.PresentationCoverage.RowCandidateSourceKeys.Add(Hash + "|C:/qa/GM.dwg|GM");
        record.PresentationCoverage.UnresolvedSpans.Add(new SectionUnresolvedSpanPlan
        {
            FromOffsetM = -4, ToOffsetM = -1, WidthM = 3,
            LeftKind = "curb", RightKind = "curb", Reason = "no-confident-strip-label",
        });
        var row = new SectionRowViewModel { Record = record };
        var gate = Gate(plan);

        gate.CanResolveRecords.Should().BeTrue();
        gate.CanApply.Should().BeFalse();
        gate.CanVerify.Should().BeFalse();
        gate.CanPreview.Should().BeFalse();
        gate.Reason.Should().Contain("יצירה ואימות נשארים חסומים");
        SectionGuidedActionPolicy.Evaluate(new SectionGuidedActionSnapshot
        {
            HasDrawing = true, ProfileUsable = true, HasPlan = true,
            SelectedRecord = true, PlanRecordCount = 1,
            CanApproveRow = gate.CanResolveRecords && row.CanApproveRowSource,
            CanNameSpans = gate.CanResolveRecords && row.CanNameSpans,
            CanApplySelected = false, CanVerifySelected = false,
        }).Action.Should().Be(expected);
        plan.Status.Should().Be(DeliveryStatus.Blocked);
        record.Status.Should().Be(DeliveryStatus.ReviewRequired);
        plan.Findings[0].Severity.Should().Be(FindingSeverity.Error);
        plan.Findings[0].ResolvedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(SectionFindingCodes.ExternalSourceChanged)]
    [InlineData(SectionFindingCodes.UnitNotMetres)]
    [InlineData(SectionFindingCodes.EvidenceWriteFailed)]
    [InlineData(SectionFindingCodes.XrefTraversalUnresolved)]
    [InlineData(SectionFindingCodes.XrefCycle)]
    [InlineData(SectionFindingCodes.PlanMarksMissing)]
    [InlineData("UNKNOWN-GLOBAL-FAILURE")]
    public void MixedSourceOrGlobalFailure_DoesNotOfferDecisionReview(string code)
    {
        var plan = Plan();
        plan.Findings.Add(new DeliveryFinding
        {
            Code = code, Domain = "sections", Severity = FindingSeverity.Error,
            Title = "Independent source or integrity failure",
        });
        AssertAllBlocked(Gate(plan));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StalePlanOrInvalidProfile_DoesNotOfferDecisionReview(bool stale, bool invalidProfile)
    {
        AssertAllBlocked(WorkflowGate.From(!invalidProfile, Plan(), stale, null, false));
    }

    [Theory]
    [InlineData("units")]
    [InlineData("revision")]
    [InlineData("drawing")]
    [InlineData("provenance")]
    [InlineData("checksum")]
    [InlineData("source-path")]
    public void IncompleteGeometryEvidence_DoesNotOfferDecisionReview(string missing)
    {
        var plan = Plan();
        switch (missing)
        {
            case "units": plan.SourceUnitCode = 0; break;
            case "revision": plan.SourceDatabaseRevision = null; break;
            case "drawing": plan.SourceDrawing = null; break;
            case "provenance": plan.Findings[0].SourceRefs.Clear(); break;
            case "checksum":
                plan.Findings[0].SourceRefs[0] = new ProvenanceRef
                { SourceKind = "xref", SourcePathOrUri = "C:/qa/HA.dwg", DrawingChecksum = "not-a-hash" };
                break;
            case "source-path":
                plan.Findings[0].SourceRefs[0] = new ProvenanceRef
                { SourceKind = "xref", SourcePathOrUri = null, DrawingChecksum = Hash };
                break;
        }
        AssertAllBlocked(Gate(plan));
    }

    [Fact]
    public void MissingProfileHashOrConsumedPlan_DoesNotOfferDecisionReview()
    {
        AssertAllBlocked(Gate(Plan(profileHash: "")));
        AssertAllBlocked(WorkflowGate.From(true, Plan(), false,
            new SectionApplyResult { RunId = "apply", Committed = true }, false));
    }

    [Fact]
    public void EvidencePublicationFailure_RetainsGuidanceRecoveryPriority()
    {
        SectionGuidedActionPolicy.Evaluate(new SectionGuidedActionSnapshot
        {
            HasDrawing = true, ProfileUsable = true, HasPlan = true,
            SelectedRecord = true, PlanRecordCount = 1, EvidenceBlocked = true,
            CanApproveRow = true, CanNameSpans = true,
        }).Action.Should().Be(SectionGuidedActionKind.InspectIssues);
    }

    private static WorkflowGate Gate(SectionPlan plan) =>
        WorkflowGate.From(true, plan, false, null, false);

    [Fact]
    public void GlobalInventoryFailure_IsVisibleOnReadyRow_WithoutMutatingPlan()
    {
        var plan = Plan();
        plan.Findings.Clear();
        plan.Records[0].Status = DeliveryStatus.Ready;
        plan.Records[0].Action = PlanAction.Update;
        plan.Findings.Add(new DeliveryFinding
        {
            Code = SectionFindingCodes.AnnotationInventoryConflict, Domain = "sections",
            Severity = FindingSeverity.Error, Title = "רישום אובייקטים אינו תואם",
            Message = "layer=0; registered=89",
        });
        var gate = Gate(plan);
        gate.GlobalPlanningBlockReason.Should().Contain("רישום אובייקטים");
        var row = new SectionRowViewModel
        {
            Record = plan.Records[0], StatusOverride = DeliveryStatus.Verified,
            GlobalPlanningBlockReason = gate.GlobalPlanningBlockReason,
        };
        row.Status.Should().Be("חסום");
        row.Severity.Should().Be("blocked");
        row.NextStep.Should().Be("טיפול בחסימת התכנון");
        plan.Records[0].Status.Should().Be(DeliveryStatus.Ready);
        plan.Records[0].Action.Should().Be(PlanAction.Update);
        new SectionRowViewModel { Record = plan.Records[0] }.NextStep.Should().Be("מוכן לעדכון");
    }

    [Fact]
    public void AnotherRecordsBlock_IsNotPresentedAsGlobal_AndSignedResolutionClearsGlobalDisplay()
    {
        var plan = Plan();
        plan.Findings.Clear();
        var finding = new DeliveryFinding
        {
            Code = "SOURCE-FAILURE", Domain = "sections", Severity = FindingSeverity.Error,
            Title = "אחר", AffectedRecordIds = new() { "another-row" },
        };
        plan.Findings.Add(finding);
        Gate(plan).GlobalPlanningBlockReason.Should().BeNull();
        finding.AffectedRecordIds.Clear();
        Gate(plan).GlobalPlanningBlockReason.Should().NotBeNull();
        finding.ResolvedAtUtc = DateTime.UtcNow;
        finding.ResolvedBy = "Reviewer";
        Gate(plan).GlobalPlanningBlockReason.Should().NotBeNull("partial resolution cannot clear a block");
        finding.Resolution = "Source corrected and checked";
        Gate(plan).GlobalPlanningBlockReason.Should().BeNull();
    }

    private static void AssertAllBlocked(WorkflowGate gate)
    {
        gate.CanResolveRecords.Should().BeFalse();
        gate.CanApply.Should().BeFalse();
        gate.CanVerify.Should().BeFalse();
    }

    [Fact]
    public void ReadySectionRemainsEditableWithoutTurningItsNamesOrDirectionsIntoUnresolvedWork()
    {
        var record = Plan().Records[0];
        record.Status = DeliveryStatus.Ready; record.Action = PlanAction.Unchanged;
        record.PresentationCoverage.RowAuthorityState = "authoritative";
        record.PresentationCoverage.ResolvedSpans.Add(new SectionResolvedSpanPlan
        {
            FromOffsetM = -4, ToOffsetM = 0, WidthM = 4, Label = "נתיב נסיעה",
            LeftKind = "curb", RightKind = "curb", EvidenceSource = "source-mark", EvidenceDigest = Hash,
        });
        record.TrafficDirections.Add(new SectionTrafficDirectionPlan
        {
            FromOffsetM = -4, ToOffsetM = 0, LaneMidOffsetM = -2, StripLabel = "נתיב נסיעה",
            StripKind = "road", EvidenceMode = "motor", State = "resolved", Reason = "fixture",
            Flow = "along-alignment", DirectionSource = "arrow", DirectionDigest = Hash,
        });
        var row = new SectionRowViewModel { Record = record };
        row.CanEditSpanLabels.Should().BeTrue(); row.CanEditTrafficDirections.Should().BeTrue();
        row.CanNameSpans.Should().BeFalse(); row.CanResolveTrafficDirection.Should().BeFalse();
        record.Action = PlanAction.Excluded;
        row.CanEditSpanLabels.Should().BeFalse(); row.CanEditTrafficDirections.Should().BeFalse();
        record.Action = PlanAction.Unchanged; record.SelectedAlignment = null;
        row.CanEditSpanLabels.Should().BeFalse(); row.CanEditTrafficDirections.Should().BeFalse();
        record.SelectedAlignment = "600"; record.PresentationCoverage.RowAuthorityState = "suppressed";
        row.CanEditSpanLabels.Should().BeFalse();
    }

    private const string Hash = "8F2469EDD9F9D25FCC09C5563FAC1C2BD9B3CE17A9B06D1E9DAE032CF8175315";

    private static SectionPlan Plan(string profileHash = Hash)
    {
        var plan = new SectionPlan
        {
            RunId = "geometry-only-review", ProjectProfileId = "6422",
            ProjectProfileHash = profileHash, SourceDrawing = "C:/qa/host.dwg",
            SourceDatabaseRevision = "current-captured-revision",
            SourceUnitCode = SectionSourceIntegrityLogic.MetresUnitCode,
            Status = DeliveryStatus.Blocked,
        };
        plan.Records.Add(new SectionPlanRecord
        {
            RecordId = "cl-A1", SelectedAlignment = "1000",
            Status = DeliveryStatus.ReviewRequired, Action = PlanAction.ReviewRequired,
            Cl = new ClSourceRecord
            {
                RecordId = "cl-A1", SourceDrawing = "C:/qa/CL.dwg",
                SourceDrawingPath = "C:/qa/CL.dwg", SourceDrawingHash = Hash,
                SourceHandle = "A1", SourceEntityType = "LINE", SourceLayer = "CL",
                SourceEndpoints = new[] { 0d, -10d, 0d, 10d },
                WcsEndpoints = new[] { 0d, -10d, 0d, 10d },
            },
        });
        var failure = new DeliveryFinding
        {
            Code = SectionFindingCodes.ProjectionGeometryUnsupported,
            Domain = "sections", Severity = FindingSeverity.Error,
            Title = "A hatch cannot be read",
            // Legacy 1.2.37 findings did not contain role/bounds; still hash backed.
        };
        failure.SourceRefs.Add(new ProvenanceRef
        {
            SourceKind = "xref", SourcePathOrUri = "C:/qa/HA.dwg",
            DrawingChecksum = Hash, SourceHandle = "B1/C2", EntityType = "Hatch",
        });
        plan.Findings.Add(failure);
        return plan;
    }
}
