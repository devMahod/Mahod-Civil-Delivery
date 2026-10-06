using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Civil keeps several drawings open in one session. A result computed in one
    /// drawing must never act on another, and an edited profile must invalidate the
    /// plan that was built from the old one.
    /// </summary>
    public class ResultScopeTests
    {
        private const string DwgA = @"C:\work\HW-CIVIL-CL.dwg";
        private const string DwgB = @"C:\work\6422-HW-CS.dwg";

        [Fact]
        public void SameDrawingAndProfile_IsCurrent()
        {
            var scope = ResultScope.For(DwgA, "hash-1");

            scope.Matches(DwgA, "hash-1").Should().BeTrue();
            scope.StaleReason(DwgA, "hash-1").Should().BeNull();
        }

        [Fact]
        public void DifferentDrawing_IsStaleWithAnActionableReason()
        {
            var scope = ResultScope.For(DwgA, "hash-1");

            scope.Matches(DwgB, "hash-1").Should().BeFalse();
            scope.StaleReason(DwgB, "hash-1").Should().Contain("שרטוט אחר");
        }

        [Fact]
        public void EditedProfile_IsStaleEvenInTheSameDrawing()
        {
            var scope = ResultScope.For(DwgA, "hash-1");

            scope.Matches(DwgA, "hash-2").Should().BeFalse();
            scope.StaleReason(DwgA, "hash-2").Should().Contain("פרופיל");
        }

        [Fact]
        public void DrawingComparisonIsCaseInsensitive()
        {
            // Windows paths differ only in case between sessions; that is the same file.
            var scope = ResultScope.For(DwgA, "hash-1");
            scope.Matches(DwgA.ToUpperInvariant(), "hash-1").Should().BeTrue();
        }

        [Fact]
        public void ProfileComparisonIsExact()
        {
            // A hash differing only in case is a different hash, not a formatting quirk.
            var scope = ResultScope.For(DwgA, "abcdef");
            scope.Matches(DwgA, "ABCDEF").Should().BeFalse();
        }

        [Fact]
        public void UnsavedDrawing_StillScopesCorrectly()
        {
            var scope = ResultScope.For(null, "hash-1");

            scope.Matches(null, "hash-1").Should().BeTrue();
            scope.Matches(DwgA, "hash-1").Should().BeFalse(
                "a plan from an unsaved drawing must not apply to a saved one");
        }

        [Fact]
        public void DrawingMismatchIsReportedBeforeProfileMismatch()
        {
            // Both changed: the drawing is the more alarming fact, so it leads.
            var scope = ResultScope.For(DwgA, "hash-1");
            scope.StaleReason(DwgB, "hash-2").Should().Contain("שרטוט אחר");
        }

        [Fact]
        public void SharedSectionScopeGateRejectsDocumentAndProfileSwitches()
        {
            var plan = new SectionPlan
            {
                RunId = "plan",
                ProjectProfileId = "6422",
                ProjectProfileHash = "hash-1",
                SourceDrawing = DwgA,
            };
            var profile = new ProjectProfile { ProfileId = "6422" };

            SectionPlanLogic.ScopeStaleReason(plan, DwgB, profile, "hash-1")
                .Should().Contain("שרטוט אחר");
            SectionPlanLogic.ScopeStaleReason(plan, DwgA, profile, "hash-2")
                .Should().Contain("פרופיל");
            SectionPlanLogic.ScopeStaleReason(plan, DwgA,
                    new ProjectProfile { ProfileId = "other" }, "hash-1")
                .Should().Contain("פרופיל");
        }

        [Theory]
        [InlineData(null, "hash-1")]
        [InlineData("", "hash-1")]
        [InlineData("hash-1", null)]
        [InlineData("hash-1", "")]
        public void SharedSectionScopeGateFailsClosedOnUnprovenProfileHash(
            string? plannedHash, string? currentHash)
        {
            var plan = new SectionPlan
            {
                RunId = "plan", ProjectProfileId = "6422",
                ProjectProfileHash = plannedHash, SourceDrawing = DwgA,
            };
            SectionPlanLogic.ScopeStaleReason(
                    plan, DwgA, new ProjectProfile { ProfileId = "6422" }, currentHash)
                .Should().NotBeNull();
        }

        [Fact]
        public void TwoUnsavedDocumentsHaveDifferentScopeIdentities()
        {
            DrawingScopeIdentity.FromParts(null, "Drawing1.dwg", "db-a")
                .Should().NotBe(DrawingScopeIdentity.FromParts(null, "Drawing2.dwg", "db-b"));
            DrawingScopeIdentity.FromParts(null, null, "db-a")
                .Should().NotBe(DrawingScopeIdentity.FromParts(null, null, "db-b"));
        }

        [Fact]
        public void EstimateScanCannotReplacePendingSectionProfileContext()
        {
            var sectionProfile = new ProjectProfile { ProfileId = "sections-a" };
            var plan = new SectionPlan
            {
                RunId = "sections-plan",
                ProjectProfileId = sectionProfile.ProfileId,
                ProjectProfileHash = "sections-hash",
                SourceDrawing = DwgA,
            };
            CivilDeliverySession.SetPlan(
                plan, sectionProfile, "sections-hash", @"C:\test\sections-profile.yaml");

            var estimateProfile = new ProjectProfile { ProfileId = "estimate-b" };
            var estimateScan = EstimateWorkflowService.AssembleScan(
                "estimate-scan", estimateProfile.ProfileId, DwgB, "estimate-hash",
                System.Array.Empty<MahodAI.CivilDelivery.Estimate.NeutralQuantityRecord>(),
                System.Array.Empty<DeliveryFinding>(), 0, false);
            CivilDeliverySession.SetScan(
                estimateScan, estimateProfile, "estimate-hash", @"C:\test\estimate-profile.yaml");

            var sections = CivilDeliverySession.GetSectionsContext();
            sections.Plan.Should().BeSameAs(plan);
            sections.Profile.Should().BeSameAs(sectionProfile);
            sections.ProfileHash.Should().Be("sections-hash");
            sections.ProfileWriteTarget.Should().Be(@"C:\test\sections-profile.yaml");
        }
    }
}
