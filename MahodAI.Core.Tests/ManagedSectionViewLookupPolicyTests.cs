using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.ManagedSectionViewLookupPolicy;

namespace MahodAI.Core.Tests
{
    public sealed class ManagedSectionViewLookupPolicyTests
    {
        private static OwnershipMetadata Meta(string role, string profile = "6422", string fingerprint = "fp") => new()
        {
            Feature = "sections", Role = role, ProjectProfileId = profile,
            RunId = "old-apply", LogicalKey = "logical", InputFingerprint = fingerprint,
            CreatedByToolVersion = "test", SourceClHandle = "C1", SourceClDrawingHash = "cl-hash",
        };
        private static Candidate[] Pair() => new[]
        {
            new Candidate("A1", false, Meta("sample-line")),
            new Candidate("B1", true, Meta("section-view"), "A1"),
        };
        private static Result Lookup(Candidate[] candidates, bool readable = true, string current = "host.dwg") =>
            Resolve("host.dwg", current, "6422", "logical", "fp", "C1", "cl-hash", readable, candidates);

        [Fact]
        public void FreshUnchangedPlanCanFindOldApplyByExactOwnershipWithoutOldApplyArtifact()
        {
            var result = Lookup(Pair());
            result.Status.Should().Be(State.Found);
            result.SectionViewHandle.Should().Be("B1");
        }

        [Fact]
        public void SameHandlesInAnotherDrawingAreNotDisplayAuthority()
        {
            Lookup(Pair(), current: "other.dwg").Status.Should().Be(State.WrongDrawing);
        }

        [Fact]
        public void DuplicateViewNeverChoosesFirst()
        {
            var pair = Pair();
            var result = Lookup(new[] { pair[0], pair[1], new Candidate("B2", true, Meta("section-view"), "A1") });
            result.Status.Should().Be(State.Ambiguous);
            result.SectionViewHandle.Should().BeNull();
        }

        [Fact]
        public void DuplicateSampleLineAlsoPreventsClaimingUniqueOwnership()
        {
            var pair = Pair();
            Lookup(new[] { pair[0], pair[1], new Candidate("A2", false, Meta("sample-line")) })
                .Status.Should().Be(State.Ambiguous);
        }

        [Fact]
        public void UnreadableInventoryCannotProveUniquenessEvenIfOnePairWasSeen()
        {
            Lookup(Pair(), readable: false).Status.Should().Be(State.Unreadable);
        }

        [Fact]
        public void MissingOrOrphanedPairRemainsExplicitlyMissing()
        {
            Lookup(System.Array.Empty<Candidate>()).Status.Should().Be(State.Missing);
            Lookup(new[] { Pair()[1] }).Status.Should().Be(State.Missing);
        }

        [Fact]
        public void UnlinkedViewAndWrongFingerprintOrProfileAreRejected()
        {
            var pair = Pair();
            Lookup(new[] { pair[0], pair[1] with { ParentSampleLineHandle = "A2" } })
                .Status.Should().Be(State.OwnershipMismatch);
            Lookup(new[] { pair[0], pair[1] with { Ownership = Meta("section-view", fingerprint: "changed") } })
                .Status.Should().Be(State.OwnershipMismatch);
            Lookup(new[] { pair[0], pair[1] with { Ownership = Meta("section-view", profile: "another") } })
                .Status.Should().Be(State.OwnershipMismatch);
        }

        [Fact]
        public void NativeViewWithWrongOwnedRoleCannotMasqueradeAsASectionView()
        {
            Lookup(new[] { Pair()[0], new Candidate("B1", true, Meta("sample-line"), "A1") })
                .Status.Should().Be(State.OwnershipMismatch);
        }

        [Fact]
        public void IncompleteClIdentityNeverFindsAViewByNameOrHandleAlone()
        {
            Resolve("host.dwg", "host.dwg", "6422", "logical", "fp", "C1", null, true, Pair())
                .Status.Should().Be(State.IncompleteIdentity);
        }

        [Theory]
        [InlineData("different-handle", "cl-hash")]
        [InlineData("C1", "different-hash")]
        public void AMatchingLogicalKeyCannotOverrideTheExactClSource(string handle, string hash)
        {
            Resolve("host.dwg", "host.dwg", "6422", "logical", "fp", handle, hash, true, Pair())
                .Status.Should().Be(State.OwnershipMismatch);
        }
    }
}
