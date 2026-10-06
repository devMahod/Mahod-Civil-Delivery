using System;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public sealed class SectionSpanAndRowDecisionTests
    {
        private const string ClHash =
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        private const string GmHash =
            "46E79D5725145026D09443CE48C599BE994F21119FD33A37BEF44A31A262FF51";
        private const string PhotoHash =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        private static SectionPlanRecord Record(string handle = "ABC1") => new()
        {
            RecordId = "row-" + handle,
            SelectedAlignment = "2000",
            Cl = new ClSourceRecord
            {
                RecordId = "row-" + handle,
                SourceDrawing = "CL.dwg",
                SourceDrawingHash = ClHash,
                SourceHandle = handle,
                SourceEntityType = "LINE",
                SourceLayer = "GFC111",
                SourceEndpoints = new[] { -30d, 0d, 30d, 0d },
                WcsEndpoints = new[] { -30d, 0d, 30d, 0d },
            },
            PresentationCoverage = new SectionPresentationCoveragePlan
            {
                RowAuthorityState = "nocandidates",
            },
        };

        private static SectionUnresolvedSpanPlan Span(double from, double to) => new()
        {
            FromOffsetM = from,
            ToOffsetM = to,
            WidthM = to - from,
            LeftKind = "curb",
            RightKind = "curb",
            Reason = "no-confident-strip-label",
        };

        [Fact]
        public void SpanApproval_IsAtomicAndPersistsEveryCurrentSpan()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            var left = Span(-6, -3);
            var right = Span(3, 6);
            record.PresentationCoverage.UnresolvedSpans.AddRange(new[] { left, right });
            SectionSpanPhysicalFixture.Capture(record);

            var count = SectionDecisionProfileService.ApproveSpanLabels(
                profile, record,
                new[]
                {
                    new SectionDecisionProfileService.SpanLabelApproval(left, "חניה"),
                    new SectionDecisionProfileService.SpanLabelApproval(right, "גינון"),
                },
                "nataly", new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

            count.Should().Be(2);
            profile.Sections.Decisions.SpanLabels.Should().HaveCount(2);
            profile.Sections.Decisions.SpanLabels.Should().OnlyContain(decision =>
                decision.SourceDrawingHash == ClHash &&
                decision.SourceHandle == "ABC1" &&
                decision.AlignmentName == "2000" &&
                decision.ApprovedBy == "nataly" &&
                decision.ApprovedAtUtc.HasValue);
        }

        [Fact]
        public void PartialSpanApproval_IsRejectedWithoutMutation()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            var left = Span(-6, -3);
            record.PresentationCoverage.UnresolvedSpans.AddRange(
                new[] { left, Span(3, 6) });
            SectionSpanPhysicalFixture.Capture(record);

            var act = () => SectionDecisionProfileService.ApproveSpanLabels(
                profile, record,
                new[] { new SectionDecisionProfileService.SpanLabelApproval(left, "חניה") },
                "nataly", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>();
            profile.Sections.Decisions.SpanLabels.Should().BeEmpty();
        }

        [Fact]
        public void SelectedSpanApproval_PersistsOnlyCheckedRowsAndLeavesOthersOpen()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            var left = Span(-6, -3);
            var right = Span(3, 6);
            record.PresentationCoverage.UnresolvedSpans.AddRange(new[] { left, right });
            SectionSpanPhysicalFixture.Capture(record);

            var count = SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(
                profile, new[] { record },
                new[]
                {
                    new SectionDecisionProfileService.SpanLabelBatchApproval(
                        record, left, "חניה"),
                },
                "nataly", new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

            count.Should().Be(1);
            profile.Sections.Decisions.SpanLabels.Should().ContainSingle()
                .Which.FromOffsetM.Should().Be(-6);
            record.PresentationCoverage.UnresolvedSpans.Should().HaveCount(2,
                "the immutable PLAN remains the audit source until the next PLAN");
        }

        [Fact]
        public void SelectedSpanApproval_InvalidOrDuplicateCheckedRowIsAtomic()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            profile.Sections.Decisions.SpanLabels.Add(new()
            {
                SourceDrawingHash = ClHash,
                SourceHandle = "OLD1",
                AlignmentName = "2000",
                FromOffsetM = -1,
                ToOffsetM = 1,
                Label = "קיים",
                ApprovedBy = "old",
                ApprovedAtUtc = DateTime.UtcNow,
            });
            var record = Record();
            var span = Span(-6, -3);
            record.PresentationCoverage.UnresolvedSpans.Add(span);
            SectionSpanPhysicalFixture.Capture(record);

            var act = () => SectionDecisionProfileService.ApproveSelectedSpanLabelsBatch(
                profile, new[] { record },
                new[]
                {
                    new SectionDecisionProfileService.SpanLabelBatchApproval(record, span, "חניה"),
                    new SectionDecisionProfileService.SpanLabelBatchApproval(record, span, "גינון"),
                },
                "nataly", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>();
            profile.Sections.Decisions.SpanLabels.Should().ContainSingle()
                .Which.SourceHandle.Should().Be("OLD1");
        }

        [Fact]
        public void MultiSectionSpanBatch_ValidatesEverythingThenWritesOnce()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var first = Record();
            var second = Record("ABC2");
            var firstSpan = Span(-6, -3);
            var secondSpan = Span(3, 6);
            first.PresentationCoverage.UnresolvedSpans.Add(firstSpan);
            second.PresentationCoverage.UnresolvedSpans.Add(secondSpan);
            SectionSpanPhysicalFixture.Capture(first);
            SectionSpanPhysicalFixture.Capture(second);

            var count = SectionDecisionProfileService.ApproveSpanLabelsBatch(
                profile,
                new[] { first, second },
                new[]
                {
                    new SectionDecisionProfileService.SpanLabelBatchApproval(
                        first, firstSpan, "חניה"),
                    new SectionDecisionProfileService.SpanLabelBatchApproval(
                        second, secondSpan, "גינון"),
                },
                "nataly", new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

            count.Should().Be(2);
            profile.Sections.Decisions.SpanLabels.Should().HaveCount(2);
            profile.Sections.Decisions.SpanLabels.Select(item => item.SourceHandle)
                .Should().BeEquivalentTo("ABC1", "ABC2");
        }

        [Fact]
        public void MultiSectionSpanBatch_MissingOrDuplicateRowLeavesProfileUntouched()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            profile.Sections.Decisions.SpanLabels.Add(new()
            {
                SourceDrawingHash = ClHash,
                SourceHandle = "OLD1",
                AlignmentName = "2000",
                FromOffsetM = -1,
                ToOffsetM = 1,
                Label = "קיים",
                ApprovedBy = "old",
                ApprovedAtUtc = DateTime.UtcNow,
            });
            var first = Record();
            var second = Record("ABC2");
            var firstSpan = Span(-6, -3);
            var secondSpan = Span(3, 6);
            first.PresentationCoverage.UnresolvedSpans.Add(firstSpan);
            second.PresentationCoverage.UnresolvedSpans.Add(secondSpan);
            SectionSpanPhysicalFixture.Capture(first);
            SectionSpanPhysicalFixture.Capture(second);

            var act = () => SectionDecisionProfileService.ApproveSpanLabelsBatch(
                profile,
                new[] { first, second },
                new[]
                {
                    new SectionDecisionProfileService.SpanLabelBatchApproval(
                        first, firstSpan, "חניה"),
                    new SectionDecisionProfileService.SpanLabelBatchApproval(
                        first, firstSpan, "גינון"),
                },
                "nataly", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>();
            profile.Sections.Decisions.SpanLabels.Should().ContainSingle()
                .Which.SourceHandle.Should().Be("OLD1");
        }

        [Fact]
        public void SpanApproval_WaitsForRowAuthorityInsteadOfSavingStaleBoundaries()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            record.PresentationCoverage.RowAuthorityState = "ambiguous";
            var span = Span(-3, 3);
            record.PresentationCoverage.UnresolvedSpans.Add(span);
            SectionSpanPhysicalFixture.Capture(record);

            var act = () => SectionDecisionProfileService.ApproveSpanLabels(
                profile, record,
                new[] { new SectionDecisionProfileService.SpanLabelApproval(span, "גינון") },
                "nataly", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>().WithMessage("*ROW authority*");
            profile.Sections.Decisions.SpanLabels.Should().BeEmpty();
        }

        [Fact]
        public void ExclusionRejectsStyleWarningsButAcceptsEngineeringScopeFindings()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            record.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.StyleMissing,
                Severity = FindingSeverity.Warning,
                Domain = "sections",
                Title = "style",
            });

            var style = () => SectionDecisionProfileService.ApproveExclusions(
                profile, new[] { record }, SectionFindingCodes.StyleMissing,
                "style is not a scope reason", "nataly", DateTime.UtcNow);
            style.Should().Throw<InvalidOperationException>();

            record.Findings.Add(new DeliveryFinding
            {
                Code = SectionFindingCodes.PlanMarksMissing,
                Severity = FindingSeverity.ReviewRequired,
                Domain = "sections",
                Title = "no plan source",
            });
            SectionDecisionProfileService.ApproveExclusions(
                profile, new[] { record }, SectionFindingCodes.PlanMarksMissing,
                "CL is outside the approved scope", "nataly", DateTime.UtcNow)
                .Should().Be(1);
        }

        [Fact]
        public void WideCurbEnvelope_CannotBeApprovedAsOneInventedVehicleLane()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            var wide = Span(-6, 6);
            record.PresentationCoverage.UnresolvedSpans.Add(wide);
            SectionSpanPhysicalFixture.Capture(record);

            var act = () => SectionDecisionProfileService.ApproveSpanLabels(
                profile, record,
                new[]
                {
                    new SectionDecisionProfileService.SpanLabelApproval(
                        wide, "נתיב נסיעה"),
                },
                "nataly", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>();
            profile.Sections.Decisions.SpanLabels.Should().BeEmpty();
        }

        [Fact]
        public void RowApproval_UsesExactCurrentCandidateAndRemovesCompetitor()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            var gmKey = $"{GmHash}|C:/GM.DWG|GM";
            var photoKey = $"{PhotoHash}|C:/PHOTO.DWG|PHOTO";
            record.PresentationCoverage.RowCandidateSourceKeys.AddRange(
                new[] { gmKey, photoKey });
            profile.Sections.Projection.RowAuthorities.Add(
                new ProjectProfile.SectionsProfile.ProjectionProfile.RowAuthority
                {
                    SourceDrawingSha256 = PhotoHash,
                    ApprovedBy = "old",
                    ApprovedAtUtc = DateTime.UtcNow,
                });

            var approved = SectionDecisionProfileService.ApproveRowAuthority(
                profile, record, gmKey, "nataly", DateTime.UtcNow);

            approved.SourceDrawingSha256.Should().Be(GmHash);
            approved.SourcePathPattern.Should().Be("C:/GM.DWG");
            approved.XrefPattern.Should().Be("GM");
            profile.Sections.Projection.RowAuthorities.Should().ContainSingle()
                .Which.SourceDrawingSha256.Should().Be(GmHash);
        }

        [Fact]
        public void RowApproval_RejectsAKeyNotShownByCurrentPlan()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };
            var record = Record();
            record.PresentationCoverage.RowCandidateSourceKeys.Add(
                $"{GmHash}|C:/GM.DWG|GM");

            var act = () => SectionDecisionProfileService.ApproveRowAuthority(
                profile, record, $"{PhotoHash}|C:/PHOTO.DWG|PHOTO",
                "nataly", DateTime.UtcNow);

            act.Should().Throw<InvalidOperationException>();
            profile.Sections.Projection.RowAuthorities.Should().BeEmpty();
        }

        [Fact]
        public void PlanSuggestionProjection_StoresReviewEvidenceWithoutChangingReadiness()
        {
            var first = Record();
            first.Station = 100;
            first.PresentationCoverage.ResolvedSpans.Add(new SectionResolvedSpanPlan
            {
                FromOffsetM = 3,
                ToOffsetM = 6,
                WidthM = 3,
                LeftKind = "curb",
                RightKind = "curb",
                Label = "נתיב נסיעה",
                EvidenceSource = "source-mark",
                EvidenceDigest = new string('A', 64),
            });
            var second = Record("ABC2");
            second.Station = 120;
            second.PresentationCoverage.ResolvedSpans.Add(new SectionResolvedSpanPlan
            {
                FromOffsetM = 3.1,
                ToOffsetM = 6.2,
                WidthM = 3.1,
                LeftKind = "curb",
                RightKind = "curb",
                Label = "נתיב נסיעה",
                EvidenceSource = "traffic-arrow",
                EvidenceDigest = new string('B', 64),
            });
            var target = Record("ABC3");
            target.Station = 140;
            target.Status = DeliveryStatus.ReviewRequired;
            var unresolved = Span(3, 6.1);
            target.PresentationCoverage.UnresolvedSpans.Add(unresolved);

            SectionPlanService.PopulateSpanSuggestions(new[] { first, second, target });

            unresolved.SuggestedLabel.Should().Be("נתיב נסיעה");
            unresolved.SuggestionConfidence.Should().Be("high");
            unresolved.StrongReviewCandidate.Should().BeTrue();
            unresolved.SuggestionEvidence.Should().HaveCount(2);
            target.Status.Should().Be(DeliveryStatus.ReviewRequired,
                "a suggestion is not an approval");
            target.PresentationCoverage.UnresolvedSpans.Should().ContainSingle();
        }
    }
}
