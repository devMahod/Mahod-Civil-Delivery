using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SelectedProfileCatalogResolutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "MahodAI-TEST-ONLY-selected-profile-catalog-" + Guid.NewGuid().ToString("N"));

    public SelectedProfileCatalogResolutionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    // LoadCatalog itself does not use native state. Do not initialize an extractor
    // or load AutoCAD just to test actual catalog registration/loading/reopening.
    private static EstimateWorkflowService Workflow() =>
        (EstimateWorkflowService)RuntimeHelpers.GetUninitializedObject(typeof(EstimateWorkflowService));

    private string ProfileFile(string directoryName)
    {
        var directory = Path.Combine(_directory, directoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "arbitrary-profile.yaml");
        File.WriteAllText(path, "profile_id: TEST-ONLY-SAME-ID\n");
        return path;
    }

    [Fact]
    public void ExplicitSelectedFileResolvesRelativeBookBesideThatFile_NotSameIdOtherDirectory()
    {
        var a = ProfileFile("A");
        var b = ProfileFile("B");
        var book = Path.Combine(Path.GetDirectoryName(a)!, "registered.xlsx");
        File.WriteAllText(book, "TEST-ONLY exact bytes, not a price approval");
        var hash = ArtifactHash.Sha256OfFile(book);

        EstimateCatalogPathResolver.ResolveFromProfile(a, "registered.xlsx", hash).Should().Be(book);
        EstimateCatalogPathResolver.ResolveFromProfile(b, "registered.xlsx", hash).Should().BeNull();
        EstimateCatalogPathResolver.ResolveFromProfile(a, book, hash).Should().Be(book);
        EstimateCatalogPathResolver.ResolveFromProfile(a, "different-name.xlsx", hash).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("TEST-ONLY-SAME-ID")]
    [InlineData("relative/profile.yaml")]
    [InlineData("C:relative-profile.yaml")]
    public void ExplicitUnknownProfileNeverAdoptsWorkingDirectoryOrIdFallback(string source)
    {
        var a = ProfileFile("A");
        var book = Path.Combine(Path.GetDirectoryName(a)!, "registered.xlsx");
        File.WriteAllText(book, "TEST-ONLY bytes");
        EstimateCatalogPathResolver.ResolveFromProfile(source, book,
            ArtifactHash.Sha256OfFile(book)).Should().BeNull();
    }

    [Fact]
    public void MissingProfileChangedOrMissingCatalogAndInvalidHashRemainUnverified()
    {
        var source = ProfileFile("A");
        var book = Path.Combine(Path.GetDirectoryName(source)!, "registered.xlsx");
        File.WriteAllText(book, "TEST-ONLY original");
        var hash = ArtifactHash.Sha256OfFile(book);
        EstimateCatalogPathResolver.ResolveFromProfile(source, "registered.xlsx", "bad").Should().BeNull();
        EstimateCatalogPathResolver.ResolveFromProfile(source, "registered.xlsx", null).Should().BeNull();
        EstimateCatalogPathResolver.ResolveFromProfile(source, "C:registered.xlsx", hash).Should().BeNull();
        File.AppendAllText(book, " changed");
        EstimateCatalogPathResolver.ResolveFromProfile(source, "registered.xlsx", hash).Should().BeNull();
        File.Delete(book);
        EstimateCatalogPathResolver.ResolveFromProfile(source, "registered.xlsx", hash).Should().BeNull();
        File.Delete(source);
        EstimateCatalogPathResolver.ResolveFromProfile(source, "registered.xlsx", hash).Should().BeNull();
    }

    [Fact]
    public void RegisterSaveReopenAndLoadActualCatalogFromArbitrarySelectedProfile_NoEngineeringApproval()
    {
        var profilePath = Path.Combine(_directory, "chosen-TEST-ONLY.yaml");
        var profile = new ProjectProfile
        {
            ProfileId = "TEST-ONLY-CATALOG-" + Guid.NewGuid().ToString("N"),
            ProjectName = "TEST-ONLY catalog location; no engineering approval",
        };
        PriceBookRegistry.Register(profile, _directory, EstimateFixtures.PriceBookPath,
            "TEST-ONLY software fixture", id: "test-only-imported", makeActive: true);
        ProfileCasTest.Save(profile, profilePath, "TEST-ONLY selected profile", "TEST-ONLY fixture");
        var profileHash = ArtifactHash.Sha256OfFile(profilePath);
        var reopened = ProjectProfileLoader.LoadFromFile(profilePath).Profile!;
        reopened.Should().NotBeNull();
        var catalog = Workflow().LoadCatalog(reopened, profilePath);

        catalog.Findings.Should().BeEmpty();
        catalog.Snapshot.Should().NotBeNull();
        catalog.Snapshot!.Items.Should().HaveCount(8615);
        catalog.Snapshot.FileHash.Should().Be(profile.Estimate.Catalog.CatalogFileHash);
        catalog.Snapshot.Prices["U51.06.1900"].Price.Should().Be(
            EstimateFixtures.Snapshot().Prices["U51.06.1900"].Price);
        reopened.Estimate.QuantitySources.Rules.Should().BeEmpty();
        reopened.Estimate.ProjectOverrides.Should().BeEmpty();
        reopened.Estimate.ApprovedAdjustments.Should().BeEmpty();
        ArtifactHash.Sha256OfFile(profilePath).Should().Be(profileHash, "catalog loading is read-only");

        var anotherProfile = ProfileFile("different-directory-same-id");
        Workflow().LoadCatalog(reopened, anotherProfile).Snapshot.Should().BeNull();
        Workflow().LoadCatalog(reopened, string.Empty).Snapshot.Should().BeNull();
        // An absolute registration continues to work without the optional argument.
        Workflow().LoadCatalog(EstimateFixtures.Profile()).Snapshot.Should().NotBeNull();

        var exactBook = Path.Combine(_directory, "test-only-imported.xlsx");
        File.AppendAllText(exactBook, "TEST-ONLY tampered bytes");
        var changed = Workflow().LoadCatalog(reopened, profilePath);
        changed.Snapshot.Should().BeNull();
        changed.Findings.Should().ContainSingle(f => f.Code == EstimateFindingCodes.PriceSourceUnverified);
        changed.Findings.Single().Message.Should().Contain(profilePath);
    }

    [Fact]
    public void AllProductionCatalogReadsCarrySelectedOrOriginalScanProfilePathIncludingBothExportChecks()
    {
        var root = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin");
        var ui = File.ReadAllText(Path.Combine(root, "CivilDelivery/UI/CivilDeliveryControl.xaml.cs"));
        ui.Should().Contain("LoadCatalog(profile, profileSource)")
            .And.Contain("LoadCatalog(_profile, _scan.ProfileSource)");
        foreach (var partial in new[] { "MappingReview", "ProjectPrice" })
            File.ReadAllText(Path.Combine(root, $"CivilDelivery/UI/CivilDeliveryControl.{partial}.cs"))
                .Should().Contain("LoadCatalog(scope.Profile, scan.ProfileSource)");
        var tools = File.ReadAllText(Path.Combine(root, "Tools/CivilDelivery/EstimateTools.cs"));
        tools.Should().Contain("LoadCatalog(profile, scan.ProfileSource)")
            .And.Contain("LoadCatalog(pending.Profile, pending.Scan.ProfileSource)")
            .And.NotContain("LoadCatalog(pending.Profile)");
        var command = File.ReadAllText(Path.Combine(root, "CivilDelivery/Commands/MhdEstimateCommand.cs"));
        command.Should().Contain("LoadCatalog(profile, scan.ProfileSource)")
            .And.Contain("LoadCatalog(loaded.Profile, loaded.ProfileSource)");
        var workflow = File.ReadAllText(Path.Combine(root, "CivilDelivery/Estimate/EstimateWorkflowService.cs"));
        workflow.Should().Contain("var verifiedCatalog = LoadCatalog(profile, scan.ProfileSource)")
            .And.Contain("var catalogAfterWrite = LoadCatalog(profile, scan.ProfileSource)")
            .And.Contain("if (profileSource != null)");
    }
}
