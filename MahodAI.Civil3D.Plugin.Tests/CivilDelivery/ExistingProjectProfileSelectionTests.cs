using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class ExistingProjectProfileSelectionTests
{
    // LoadForDrawing exercises the production LoadProfile method, which is stateless
    // and uses only ProfileLocator + ProjectProfileLoader. Skip native field
    // initialization, not the loader: no AutoCAD service/method is called or mocked.
    private static SectionsWorkflowService OfflineProfileWorkflow() =>
        (SectionsWorkflowService)RuntimeHelpers.GetUninitializedObject(typeof(SectionsWorkflowService));

    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "SYNTHETIC-profile-selection-" + Guid.NewGuid().ToString("N"));
        internal string PathName => Path.Combine(DirectoryPath, "SYNTHETIC-project.yaml");
        internal readonly ProjectProfile Profile = new() { ProfileId = "SYNTHETIC-PORTABLE", ProjectName = "SYNTHETIC TEST ONLY" };
        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            var catalog = ManualMappingBatchDialogTests.Catalog();
            Profile.Estimate.Catalog.CatalogFile = "SYNTHETIC-catalog.xlsx";
            Profile.Estimate.Catalog.CatalogFileHash = catalog.FileHash;
            Profile.Estimate.Pricing.PriceBookSnapshotId = catalog.SnapshotId;
            Profile.Estimate.Pricing.PriceBookHash = catalog.FileHash;
            Profile.Estimate.PriceBooks.Add(new() { Id = catalog.SnapshotId, File = "SYNTHETIC-catalog.xlsx", FileHash = catalog.FileHash });
            Profile.Estimate.QuantitySources.Rules.Add(new()
            {
                RuleKey = "layer:ZZ-NOVEL|length", LayerPattern = "ZZ-NOVEL", MeasurementKind = "length", EntityType = "LINE",
                ExpectedUnit = "m", CandidateCatalogCode = ManualMappingBatchDialogTests.Code, ApprovedBy = "SYNTHETIC TEST ONLY",
                ApprovedAtUtc = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
                ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = catalog.FileHash,
                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[ManualMappingBatchDialogTests.Code]),
            });
            ProfileCasTest.Save(Profile, PathName, "SYNTHETIC prior project", "SYNTHETIC TEST ONLY");
        }
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    [Fact]
    public void ExplicitChoiceReusesSavedRuleOnlyForChosenDocumentAndDoesNotWriteAnything()
    {
        using var f = new Fixture(); var documentA = new object(); var documentB = new object();
        var before = File.ReadAllBytes(f.PathName);
        var preview = ExistingProjectProfileSelection.Inspect(f.PathName);
        preview.Decisions.Should().Contain("עם חתימת מאשר: 1");
        ExistingProjectProfileSelection.Current(documentA).Should().BeNull("inspection is not selection");
        ExistingProjectProfileSelection.Bind(documentA, @"C:\SYNTHETIC\Campus-East.dwg", preview, null);
        ExistingProjectProfileSelection.Resolve(documentA, @"C:\SYNTHETIC\Campus-East.dwg", null).Should().Be(f.PathName);
        ExistingProjectProfileSelection.Resolve(documentB, @"C:\SYNTHETIC\Campus-East.dwg", null).Should().BeNull("another document needs consent");
        var loaded = ActiveProjectProfileService.LoadForDrawing(@"C:\SYNTHETIC\Campus-East.dwg", null,
            OfflineProfileWorkflow(), ExistingProjectProfileSelection.Resolve(documentA, @"C:\SYNTHETIC\Campus-East.dwg", null));
        loaded.ProfileSource.Should().Be(f.PathName); loaded.ProfileWriteTarget.Should().Be(f.PathName);
        loaded.IsUsable.Should().BeTrue(); loaded.IsGeneratedForDrawing.Should().BeFalse();
        CivilQuantityExtractionService.ResolveApprovedRule(loaded.Profile!, "layer:ZZ-NOVEL|length", "ZZ-NOVEL", "length")
            .Rule!.CandidateCatalogCode.Should().Be(ManualMappingBatchDialogTests.Code);
        loaded.Profile!.Estimate.IgnoredRuleDecisions.Should().BeEmpty();
        File.ReadAllBytes(f.PathName).Should().Equal(before);
    }

    [Fact]
    public void SaveAsRefusesBindingAndExplicitToolSelectorStillWins()
    {
        using var f = new Fixture(); var document = new object();
        var preview = ExistingProjectProfileSelection.Inspect(f.PathName);
        ExistingProjectProfileSelection.Bind(document, @"C:\SYNTHETIC\Campus-East.dwg", preview, null);
        Action changed = () => ExistingProjectProfileSelection.Resolve(document, @"C:\SYNTHETIC\Campus-West.dwg", null);
        changed.Should().Throw<InvalidOperationException>().WithMessage("*בחר מחדש*");
        ExistingProjectProfileSelection.Resolve(document, @"C:\SYNTHETIC\Campus-West.dwg", "explicit-tool-profile").Should().Be("explicit-tool-profile");
        ExistingProjectProfileSelection.Bind(document, @"C:\SYNTHETIC\Campus-West.dwg", preview, ExistingProjectProfileSelection.Current(document));
        ExistingProjectProfileSelection.Resolve(document, @"C:\SYNTHETIC\Campus-West.dwg", null).Should().Be(f.PathName);
        Action differentProject = () => ExistingProjectProfileSelection.RequireProfileIdentity(ExistingProjectProfileSelection.Current(document)!, "ANOTHER-PROJECT");
        differentProject.Should().Throw<InvalidOperationException>().WithMessage("*זהות הפרויקט*");
    }

    [Fact]
    public void ChangedProfileAndCompetingSelectionBothRefuseWithoutReplacingBinding()
    {
        using var f = new Fixture(); var document = new object(); var preview = ExistingProjectProfileSelection.Inspect(f.PathName);
        ExistingProjectProfileSelection.Bind(document, @"C:\SYNTHETIC\Campus.dwg", preview, null);
        var original = ExistingProjectProfileSelection.Current(document);
        Action competing = () => ExistingProjectProfileSelection.Bind(document, @"C:\SYNTHETIC\Campus.dwg", preview, null);
        competing.Should().Throw<InvalidOperationException>().WithMessage("*השתנתה בזמן*");
        f.Profile.ProjectName = "SYNTHETIC competing writer";
        ProfileCasTest.Save(f.Profile, f.PathName, "SYNTHETIC changed", "TEST ONLY");
        var changedBytes = File.ReadAllBytes(f.PathName);
        Action stale = () => ExistingProjectProfileSelection.Bind(document, @"C:\SYNTHETIC\Campus.dwg", preview, original);
        stale.Should().Throw<InvalidOperationException>().WithMessage("*השתנה מאז*");
        ExistingProjectProfileSelection.Current(document).Should().BeSameAs(original);
        File.ReadAllBytes(f.PathName).Should().Equal(changedBytes);
    }

    [Fact]
    public void MissingSelectedFileNeverFallsBackToGeneratedOrShippedProfile()
    {
        using var f = new Fixture(); var document = new object(); var preview = ExistingProjectProfileSelection.Inspect(f.PathName);
        ExistingProjectProfileSelection.Bind(document, @"C:\SYNTHETIC\Campus.dwg", preview, null);
        File.Move(f.PathName, f.PathName + ".preserved");
        var selected = ExistingProjectProfileSelection.Resolve(document, @"C:\SYNTHETIC\Campus.dwg", null);
        selected.Should().Be(f.PathName);
        var result = ActiveProjectProfileService.LoadForDrawing(@"C:\SYNTHETIC\Campus.dwg", null, OfflineProfileWorkflow(), selected);
        result.IsUsable.Should().BeFalse(); result.IsGeneratedForDrawing.Should().BeFalse(); result.Profile.Should().BeNull();
    }

    [Theory]
    [InlineData(@"\\server\projects\profile.yaml")]
    [InlineData(@"\\?\C:\local\profile.yaml")]
    [InlineData(@"relative-profile.yaml")]
    [InlineData(@"C:\SYNTHETIC\MahodAI.bundle\profiles\project-profile.yaml")]
    public void UnsafeOrProductTargetsAreRefusedBeforeRead(string path)
    {
        Action inspect = () => ExistingProjectProfileSelection.Inspect(path);
        inspect.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ControllerRequiresConsentFreshDrawingAndOriginalSelectionBeforeResetAndBind()
    {
        var root = EstimateFixtures.RepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "MahodAI.Civil3D.Plugin/CivilDelivery/UI/CivilDeliveryControl.ProfileSelection.cs"));
        source.Should().Contain("if (!review.Accepted) return;").And.Contain("ReferenceEquals(document, Doc())")
            .And.Contain("DrawingRevisionTracker.Capture(document.Database)").And.Contain("ReferenceEquals(prior, ExistingProjectProfileSelection.Current(document))");
        source.IndexOf("if (!review.Accepted)", StringComparison.Ordinal).Should().BeLessThan(source.IndexOf("ResetForExplicitProjectProfileSelection", StringComparison.Ordinal));
        source.IndexOf("RequireUnchanged(preview)", StringComparison.Ordinal).Should().BeLessThan(source.IndexOf("ResetForExplicitProjectProfileSelection", StringComparison.Ordinal));
        source.Should().NotContain("SetEnvironmentVariable").And.NotContain("ProjectProfileWriter.Save");
    }
}
