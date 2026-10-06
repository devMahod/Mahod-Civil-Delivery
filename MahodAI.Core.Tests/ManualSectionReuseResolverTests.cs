using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests
{
    public class ManualSectionReuseResolverTests
    {
        private static readonly ManualSectionReuseResolver.Point A = new(0, -15);
        private static readonly ManualSectionReuseResolver.Point B = new(0, 20);

        private static ManualSectionReuseResolver.Candidate Candidate(
            string view = "30",
            string alignment = "2000",
            double station = 100,
            IReadOnlyList<ManualSectionReuseResolver.Point>? vertices = null,
            bool stationReadable = true,
            bool geometryReadable = true,
            bool presentationReadable = true,
            bool presentationCompatible = true) => new(
                alignment,
                station,
                stationReadable,
                vertices ?? new[] { A, new ManualSectionReuseResolver.Point(0, 0), B },
                geometryReadable,
                "10",
                "20",
                view,
                "manual-sl",
                "manual-view",
                "Nataly Section",
                Array.Empty<string>(),
                presentationReadable,
                presentationCompatible,
                "test-evidence");

        private static ManualSectionReuseResolver.Decision Resolve(
            params ManualSectionReuseResolver.Candidate[] candidates) =>
            ManualSectionReuseResolver.Resolve(
                "2000", 100, new[] { A, B }, candidates);

        [Fact]
        public void ZeroCandidate_UsesCreatePath()
        {
            var decision = Resolve();

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.None);
            decision.Reason.Should().Be(ManualSectionReuseResolver.DecisionReason.NoCandidate);
            decision.Selected.Should().BeNull();
        }

        [Fact]
        public void OneExactCandidate_IsReused()
        {
            var decision = Resolve(Candidate());

            decision.CanReuse.Should().BeTrue();
            decision.Selected!.SectionViewHandle.Should().Be("30");
        }

        [Fact]
        public void ReversedCivilEndpoints_WithCrossingVertex_AreEquivalent()
        {
            var decision = Resolve(Candidate(vertices: new[]
            {
                B,
                new ManualSectionReuseResolver.Point(0, 0),
                A,
            }));

            decision.CanReuse.Should().BeTrue();
        }

        [Fact]
        public void CandidateOutsideStationTolerance_IsNotAdopted()
        {
            var decision = Resolve(Candidate(station: 100.051));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.None);
        }

        [Fact]
        public void CandidateOnAnotherAlignment_IsIgnored()
        {
            var decision = Resolve(Candidate(alignment: "3000"));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.None);
        }

        [Fact]
        public void TwoViewsAtSameStation_AreReviewRequired_EvenIfBothMatch()
        {
            var decision = Resolve(Candidate("30"), Candidate("31"));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.ReviewRequired);
            decision.Reason.Should().Be(ManualSectionReuseResolver.DecisionReason.Ambiguous);
            decision.RelevantCandidates.Should().HaveCount(2);
        }

        [Fact]
        public void OneExactAndOneWrongGeometry_StillCannotBeGuessed()
        {
            var wrong = Candidate("31", vertices: new[]
            {
                new ManualSectionReuseResolver.Point(10, -15),
                new ManualSectionReuseResolver.Point(10, 20),
            });

            var decision = Resolve(Candidate("30"), wrong);

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.ReviewRequired);
            decision.Reason.Should().Be(ManualSectionReuseResolver.DecisionReason.Ambiguous);
        }

        [Fact]
        public void UniqueGeometryMismatch_IsReviewRequired_NotCreate()
        {
            var decision = Resolve(Candidate(vertices: new[]
            {
                new ManualSectionReuseResolver.Point(1, -15),
                new ManualSectionReuseResolver.Point(1, 20),
            }));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.ReviewRequired);
            decision.Reason.Should().Be(ManualSectionReuseResolver.DecisionReason.GeometryMismatch);
        }

        [Fact]
        public void UnreadableStationOnSelectedAlignment_FailsClosed()
        {
            var decision = Resolve(Candidate(
                station: double.NaN,
                stationReadable: false));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.ReviewRequired);
            decision.Reason.Should().Be(ManualSectionReuseResolver.DecisionReason.StationUnreadable);
        }

        [Fact]
        public void UnreadableGeometryAtStation_FailsClosed()
        {
            var decision = Resolve(Candidate(
                vertices: Array.Empty<ManualSectionReuseResolver.Point>(),
                geometryReadable: false));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.ReviewRequired);
            decision.Reason.Should().Be(ManualSectionReuseResolver.DecisionReason.GeometryUnreadable);
        }

        [Fact]
        public void ReadableButIncompatiblePresentation_UsesManagedCreatePath()
        {
            var decision = Resolve(Candidate(presentationCompatible: false));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.None);
            decision.Reason.Should().Be(
                ManualSectionReuseResolver.DecisionReason.PresentationIncompatible);
            decision.Selected.Should().BeNull("the foreign view must stay untouched");
        }

        [Fact]
        public void UnreadablePresentation_FailsClosed()
        {
            var decision = Resolve(Candidate(presentationReadable: false));

            decision.Kind.Should().Be(ManualSectionReuseResolver.DecisionKind.ReviewRequired);
            decision.Reason.Should().Be(
                ManualSectionReuseResolver.DecisionReason.PresentationUnreadable);
        }
    }
}
