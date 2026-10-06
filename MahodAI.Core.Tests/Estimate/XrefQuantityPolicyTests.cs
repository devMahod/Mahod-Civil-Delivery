using System;
using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class XrefQuantityPolicyTests
    {
        [Fact]
        public void SimilarityTransform_AcceptsRotationReflectionAndUniformScale()
        {
            var result = XrefQuantityPolicy.ValidateSimilarity(
                new[] { 0d, 2d, 0d },
                new[] { 2d, 0d, 0d },
                new[] { 0d, 0d, -2d });

            result.IsSafe.Should().BeTrue();
            result.LengthScale.Should().BeApproximately(2d, 1e-12);
        }

        [Theory]
        [InlineData(2d, 1d, 1d, "non-uniform-scale")]
        [InlineData(1d, 0d, 1d, "degenerate-transform")]
        public void UnsafeTransform_FailsClosed(
            double xScale, double yScale, double zScale, string reason)
        {
            var result = XrefQuantityPolicy.ValidateSimilarity(
                new[] { xScale, 0d, 0d },
                new[] { 0d, yScale, 0d },
                new[] { 0d, 0d, zScale });

            result.IsSafe.Should().BeFalse();
            result.Failure.Should().Be(reason);
        }

        [Fact]
        public void ShearTransform_FailsClosed()
        {
            XrefQuantityPolicy.ValidateSimilarity(
                    new[] { 1d, 0d, 0d },
                    new[] { 0.6d, 0.8d, 0d },
                    new[] { 0d, 0d, 1d })
                .Failure.Should().Be("sheared-transform");
        }

        [Fact]
        public void ExternalSourceFreshness_RejectsChangedMissingAndConflictingIdentity()
        {
            var path = "C:\\drawings\\utilities.dwg";
            var hash = new string('a', 64);
            var source = new EstimateExternalSource
            {
                DrawingPath = path,
                DrawingHash = hash,
                XrefChain = "UT > WATER",
                ReferenceHandlePath = "10/20",
            };

            XrefQuantityPolicy.FreshnessFailure(new[] { source },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [path] = hash,
                    })
                .Should().BeNull();
            XrefQuantityPolicy.FreshnessFailure(new[] { source },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                .Should().StartWith("external-source-unavailable:");
            XrefQuantityPolicy.FreshnessFailure(new[] { source },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [path] = new string('b', 64),
                    })
                .Should().StartWith("external-source-changed:");

            var conflict = new EstimateExternalSource
            {
                DrawingPath = path,
                DrawingHash = new string('c', 64),
                XrefChain = "UT > WATER",
                ReferenceHandlePath = "30/40",
            };
            XrefQuantityPolicy.FreshnessFailure(new[] { source, conflict },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [path] = hash,
                    })
                .Should().StartWith("conflicting-scan-identity:");
        }

        [Fact]
        public void LoadedSnapshot_RequiresStableDiskRevisionAndMatchingLoadedCache()
        {
            var stable = new XrefQuantityPolicy.SourceSnapshotIdentity(
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                7,
                new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).Ticks);

            XrefQuantityPolicy.LoadedSnapshotFailure(stable, stable, stable)
                .Should().BeNull();

            var staleLoaded = stable with
            {
                VersionGuid = "33333333-3333-3333-3333-333333333333",
            };
            XrefQuantityPolicy.LoadedSnapshotFailure(staleLoaded, stable, stable)
                .Should().Be("loaded-xref-stale");

            var changedOnDisk = stable with { NumberOfSaves = 8 };
            XrefQuantityPolicy.LoadedSnapshotFailure(stable, stable, changedOnDisk)
                .Should().Be("disk-xref-changed-during-scan");

            XrefQuantityPolicy.LoadedSnapshotFailure(null, stable, stable)
                .Should().Be("loaded-xref-identity-unavailable");
        }

        [Fact]
        public void ActiveClip_BlocksOnlyWhenTraversalReachesExternalGeometry()
        {
            XrefQuantityPolicy.ActiveClipBlocksExternalTraversal(
                    isExternalReference: false, activeOnReference: true, activeOnAncestor: false)
                .Should().BeFalse("a clipped ordinary symbol is still one count");
            XrefQuantityPolicy.ActiveClipBlocksExternalTraversal(
                    isExternalReference: true, activeOnReference: true, activeOnAncestor: false)
                .Should().BeTrue("a directly clipped XREF cannot be expanded safely");
            XrefQuantityPolicy.ActiveClipBlocksExternalTraversal(
                    isExternalReference: true, activeOnReference: false, activeOnAncestor: true)
                .Should().BeTrue("an ordinary ancestor's clip affects its nested XREF");
            XrefQuantityPolicy.ActiveClipBlocksExternalTraversal(
                    isExternalReference: true, activeOnReference: false, activeOnAncestor: false)
                .Should().BeFalse();
        }

        [Fact]
        public void OverlayVisibility_SkipsOnlyExternalBranchesExcludedFromTheHostGraph()
        {
            // Direct ordinary geometry under a top-level Overlay remains visible.
            XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isExternalReference: false,
                    isOverlayReference: false,
                    isInsideExternalReference: true,
                    hasExternalOverlayAncestor: true)
                .Should().BeFalse();

            // No nested external reference is visible beneath an Overlay ancestor.
            XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isExternalReference: true,
                    isOverlayReference: false,
                    isInsideExternalReference: true,
                    hasExternalOverlayAncestor: true)
                .Should().BeTrue();

            // A nested Overlay is excluded even when its parent was attached.
            XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isExternalReference: true,
                    isOverlayReference: true,
                    isInsideExternalReference: true,
                    hasExternalOverlayAncestor: false)
                .Should().BeTrue();

            // Top-level Overlays and nested Attachments under Attachments are visible.
            XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isExternalReference: true,
                    isOverlayReference: true,
                    isInsideExternalReference: false,
                    hasExternalOverlayAncestor: false)
                .Should().BeFalse();
            XrefQuantityPolicy.IsExternalReferenceExcludedByOverlay(
                    isExternalReference: true,
                    isOverlayReference: false,
                    isInsideExternalReference: true,
                    hasExternalOverlayAncestor: false)
                .Should().BeFalse();
        }
    }
}
