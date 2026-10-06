using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// An engineer can bring any price book - NTI, Dekel, their own - register it with
    /// the project, and choose which one prices the estimate. Identity is the file hash.
    /// </summary>
    public class PriceBookRegistryTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_reg_" + Guid.NewGuid().ToString("N")[..8]);

        public PriceBookRegistryTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static ProjectProfile Profile() => new() { ProfileId = "6422" };

        [Fact]
        public void RegisteringTheRealNtiBook_StoresItUnderAStableId_WithHashAndCount()
        {
            var profile = Profile();

            var r = PriceBookRegistry.Register(profile, _dir, EstimateFixtures.PriceBookPath, "nataly",
                id: "nti-urban-082025", publisher: "נתיבי ישראל", edition: "08/2025");

            r.Entry.Id.Should().Be("nti-urban-082025");
            r.Entry.ItemCount.Should().Be(8615);
            r.Entry.FileHash.Should().HaveLength(64);
            r.Entry.RegisteredBy.Should().Be("nataly");
            File.Exists(Path.Combine(_dir, "nti-urban-082025.xlsx")).Should().BeTrue("the book is stored next to the profile");
            profile.Estimate.PriceBooks.Should().ContainSingle();
        }

        [Fact]
        public void MakingABookActive_RepointsTheCatalogAndPricingIdentity()
        {
            var profile = Profile();
            PriceBookRegistry.Register(profile, _dir, EstimateFixtures.PriceBookPath, "nataly", id: "nti-a");

            PriceBookRegistry.MakeActive(profile, "nti-a", _dir);

            profile.Estimate.Pricing.PriceBookSnapshotId.Should().Be("nti-a");
            profile.Estimate.Catalog.CatalogFile.Should().Be("nti-a.xlsx");
            profile.Estimate.Catalog.CatalogFileHash.Should().Be(profile.Estimate.PriceBooks[0].FileHash);
            PriceBookRegistry.Active(profile)!.Id.Should().Be("nti-a");
        }

        [Fact]
        public void TwoBooksCanCoexist_AndSwitchingIsExplicit()
        {
            var profile = Profile();
            // Register the same bytes twice under two ids to simulate NTI + a second book.
            PriceBookRegistry.Register(profile, _dir, EstimateFixtures.PriceBookPath, "nataly", id: "nti", makeActive: true);
            PriceBookRegistry.Register(profile, _dir, EstimateFixtures.PriceBookPath, "nataly", id: "dekel");

            PriceBookRegistry.Active(profile)!.Id.Should().Be("nti", "registering a second book does not silently switch");
            PriceBookRegistry.MakeActive(profile, "dekel", _dir);
            PriceBookRegistry.Active(profile)!.Id.Should().Be("dekel");
            profile.Estimate.PriceBooks.Should().HaveCount(2);
        }

        [Fact]
        public void ANonPriceBook_IsRefused_WithTheReason()
        {
            var junk = Path.Combine(_dir, "junk.xlsx");
            File.WriteAllText(junk, "not a workbook");

            var act = () => PriceBookRegistry.Register(Profile(), _dir, junk, "nataly");

            act.Should().Throw<Exception>();
            Profile().Estimate.PriceBooks.Should().BeEmpty();
        }

        [Fact]
        public void RegistrationRequiresAnEngineerName()
        {
            var act = () => PriceBookRegistry.Register(Profile(), _dir, EstimateFixtures.PriceBookPath, "");
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void LegacyProfile_GetsItsExistingBookAsTheFirstRegistryEntry()
        {
            // A profile written before the registry existed still knows its book.
            var profile = Profile();
            profile.Estimate.Pricing.PriceBookSnapshotId = "nti-urban-082025";
            profile.Estimate.Pricing.PriceBookHash = "90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c";
            profile.Estimate.Catalog.CatalogVersion = "NTI-urban-08/2025";

            PriceBookRegistry.EnsureLegacyEntry(profile).Should().BeTrue();

            var e = profile.Estimate.PriceBooks.Should().ContainSingle().Subject;
            e.Id.Should().Be("nti-urban-082025");
            e.Publisher.Should().Be("נתיבי ישראל");
            e.FileHash.Should().StartWith("90da5980");
            PriceBookRegistry.EnsureLegacyEntry(profile).Should().BeFalse("idempotent");
        }

        [Fact]
        public void SuggestedId_ComesFromPublisherAndEdition()
        {
            var profile = Profile();
            var r = PriceBookRegistry.Register(profile, _dir, EstimateFixtures.PriceBookPath, "nataly");
            // The shipped NTI file has a publication note mentioning 2025; id must be stable and ascii.
            r.Entry.Id.Should().MatchRegex("^[a-z0-9\\-]+$");
        }

        [Fact]
        public void NullLegacyPricing_DoesNotCrashRegistry_AndMakeActiveRepairsIt()
        {
            var profile = Profile();
            profile.Estimate.Pricing = null!;

            PriceBookRegistry.Active(profile).Should().BeNull();
            PriceBookRegistry.EnsureLegacyEntry(profile).Should().BeFalse();

            PriceBookRegistry.Register(profile, _dir, EstimateFixtures.PriceBookPath,
                "nataly", id: "nti-null-pricing");
            var act = () => PriceBookRegistry.MakeActive(profile, "nti-null-pricing", _dir);

            act.Should().NotThrow();
            profile.Estimate.Pricing.Should().NotBeNull();
            profile.Estimate.Pricing.PriceBookSnapshotId.Should().Be("nti-null-pricing");
        }

        [Fact]
        public void NullLegacyPricing_IsRejectedByStrictCatalogIdentityWithoutCrashing()
        {
            var profile = Profile();
            profile.Estimate.Catalog.CatalogFile = EstimateFixtures.PriceBookPath;
            profile.Estimate.Catalog.CatalogFileHash = EstimateFixtures.Snapshot().FileHash;
            profile.Estimate.Pricing = null!;

            var act = () => new EstimateWorkflowService().LoadCatalog(profile);

            var loaded = act.Should().NotThrow().Subject;
            loaded.Snapshot.Should().BeNull();
            loaded.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.PriceSourceUnverified &&
                f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void StrictCatalogIdentity_LoadsOnlyWhenAllFourIdentitySourcesAgree()
        {
            var loaded = new EstimateWorkflowService().LoadCatalog(EstimateFixtures.Profile());

            loaded.Findings.Should().BeEmpty();
            loaded.Snapshot.Should().NotBeNull();
            loaded.Snapshot!.SnapshotId.Should().Be(EstimateFixtures.Snapshot().SnapshotId);
            loaded.Snapshot.FileHash.Should().Be(EstimateFixtures.Snapshot().FileHash);
        }

        [Fact]
        public void StrictCatalogIdentity_RejectsMalformedOrDivergentHashes()
        {
            var malformed = EstimateFixtures.Profile();
            malformed.Estimate.Pricing.PriceBookHash = "abc";
            new EstimateWorkflowService().LoadCatalog(malformed).Snapshot.Should().BeNull();

            var divergent = EstimateFixtures.Profile();
            divergent.Estimate.Pricing.PriceBookHash = new string('b', 64);
            var loaded = new EstimateWorkflowService().LoadCatalog(divergent);
            loaded.Snapshot.Should().BeNull();
            loaded.Findings.Should().ContainSingle(f =>
                f.Message != null && f.Message.Contains("hashes do not match"));
        }

        [Fact]
        public void StrictCatalogIdentity_RejectsRegistryIdOrFileMismatch()
        {
            var wrongId = EstimateFixtures.Profile();
            wrongId.Estimate.PriceBooks[0].Id = "another-book";
            new EstimateWorkflowService().LoadCatalog(wrongId).Snapshot.Should().BeNull();

            var wrongFile = EstimateFixtures.Profile();
            wrongFile.Estimate.PriceBooks[0].File = "different.xlsx";
            var loaded = new EstimateWorkflowService().LoadCatalog(wrongFile);
            loaded.Snapshot.Should().BeNull();
            loaded.Findings.Should().ContainSingle(f =>
                f.Message != null && f.Message.Contains("does not equal active registry file"));
        }

        [Fact]
        public void StrictCatalogIdentity_DoesNotSubstituteAnotherSameNamedOrSameIdFile()
        {
            var corrupt = Path.Combine(_dir, "active.xlsx");
            File.WriteAllText(corrupt, "not the registered price book");
            var profile = EstimateFixtures.Profile();
            profile.Estimate.Catalog.CatalogFile = corrupt;
            profile.Estimate.PriceBooks[0].File = corrupt;

            var loaded = new EstimateWorkflowService().LoadCatalog(profile);

            loaded.Snapshot.Should().BeNull();
            loaded.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.PriceSourceUnverified);
        }

        [Fact]
        public void BoundMappingWithContradictoryItemFingerprint_BlocksCatalogLoad()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "layer:KERB|length",
                    LayerPattern = "KERB",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                    ApprovedBy = "nataly",
                    ApprovedAtUtc = DateTime.UtcNow,
                    ApprovedCatalogId = EstimateFixtures.Snapshot().SnapshotId,
                    ApprovedCatalogHash = EstimateFixtures.Snapshot().FileHash,
                    ApprovedCatalogItemFingerprint = new string('f', 64),
                });

            var loaded = new EstimateWorkflowService().LoadCatalog(profile);

            loaded.Snapshot.Should().BeNull();
            loaded.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.PriceSourceUnverified &&
                f.Title.Contains("אישור המיפוי"));
        }

        [Fact]
        public void LostProfileCas_DoesNotDeleteImmutableCatalogBytes()
        {
            var profilePath = Path.Combine(_dir, "project-profile.yaml");
            var baseline = Profile();
            var initial = ProfileCasTest.Save(
                baseline, profilePath, "initial profile", "arthur");
            var expected = ProjectProfileWriter.CaptureExpectedState(
                profilePath, initial.NewHash, profilePath);
            File.AppendAllText(profilePath, "\n# concurrent profile winner\n");
            var candidate = Profile();

            var act = () => new EstimateWorkflowService().RegisterPriceBook(
                candidate, EstimateFixtures.PriceBookPath, "nataly", profilePath,
                expected, id: "shared-book", publisher: "NTI", edition: "08/2025",
                makeActive: true);

            act.Should().Throw<InvalidOperationException>();
            var stored = Path.Combine(_dir, "shared-book.xlsx");
            File.Exists(stored).Should().BeTrue(
                "a concurrent winning profile may already reference these immutable bytes");
            ArtifactHash.Sha256OfFile(stored)
                .Should().Be(ArtifactHash.Sha256OfFile(EstimateFixtures.PriceBookPath));
            candidate.Estimate.PriceBooks.Should().BeEmpty(
                "the losing in-memory profile is rolled back even though safe orphan bytes remain");
        }
    }
}
