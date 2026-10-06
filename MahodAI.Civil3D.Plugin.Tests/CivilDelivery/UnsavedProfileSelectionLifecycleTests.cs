using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Composes the production captured-identity loader, session binding and YAML loader.
/// Object tokens and supplied identity snapshots are NOT AutoCAD documents or native
/// SaveAs execution. Only synthetic YAML files are written; no DWG is created/opened.
/// </summary>
public sealed class UnsavedProfileSelectionLifecycleTests
{
    private const string Template = @"C:\SYNTHETIC\Templates\Same-Metric.dwt";

    // LoadProfile is stateless. As in ExistingProjectProfileSelectionTests, bypass
    // native service field initialization, not the real locator/loader behavior.
    private static SectionsWorkflowService Workflow() =>
        (SectionsWorkflowService)RuntimeHelpers.GetUninitializedObject(typeof(SectionsWorkflowService));

    private static SavedDrawingPathPolicy.Identity Unsaved(string name) =>
        SavedDrawingPathPolicy.Evaluate(0, Template, name);

    private static SavedDrawingPathPolicy.Identity Saved(string path) =>
        SavedDrawingPathPolicy.Evaluate(1, path, path);

    private static ActiveProjectProfileService.ActiveLoadResult Load(object document,
        SavedDrawingPathPolicy.Identity identity, string? environmentSelector = null) =>
        ActiveProjectProfileService.LoadForCapturedDrawingIdentity(document, identity, Workflow(),
            environmentSelector: environmentSelector, environmentProfileDirectory: null);

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(),
            "SYNTHETIC-unsaved-profile-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal readonly string Id = "SYNTHETIC-LIFECYCLE-" + Guid.NewGuid().ToString("N");
        internal string ProfileA => Path.Combine(Root, "A", "project.yaml");
        internal string ProfileB => Path.Combine(Root, "B", "project.yaml");
        internal string DrawingA => Path.Combine(Root, "A", "Site.dwg");
        internal string DrawingB => Path.Combine(Root, "B", "Site.dwg");
        private readonly byte[] _beforeA;
        private readonly byte[] _beforeB;
        internal Fixture()
        {
            // Same profile ID deliberately proves selection follows the exact file,
            // rather than rediscovering a different same-ID project after reload.
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileA)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileB)!);
            ProfileCasTest.Save(new ProjectProfile { ProfileId = Id, ProjectName = "SYNTHETIC PROJECT A" },
                ProfileA, "SYNTHETIC lifecycle fixture", "TEST ONLY");
            ProfileCasTest.Save(new ProjectProfile { ProfileId = Id, ProjectName = "SYNTHETIC PROJECT B" },
                ProfileB, "SYNTHETIC lifecycle fixture", "TEST ONLY");
            _beforeA = File.ReadAllBytes(ProfileA);
            _beforeB = File.ReadAllBytes(ProfileB);
        }
        internal void AssertUnchanged()
        {
            File.ReadAllBytes(ProfileA).Should().Equal(_beforeA);
            File.ReadAllBytes(ProfileB).Should().Equal(_beforeB);
            File.Exists(DrawingA).Should().BeFalse("these are identity-policy inputs, not native DWGs");
            File.Exists(DrawingB).Should().BeFalse();
        }
        // Retain the unique synthetic inputs in the isolated runner's private TEMP
        // for evidence. No cleanup of runtime profiles or user/project directories.
        public void Dispose() => AssertUnchanged();
    }

    private static void AssertSeed(ActiveProjectProfileService.ActiveLoadResult result, object document)
    {
        result.IsGeneratedForDrawing.Should().BeTrue();
        result.Profile!.ProfileId.Should().Be(SavedDrawingPathPolicy.UnsavedProfileId(document));
        result.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-DRAWING-SAVE-REQUIRED");
        result.Profile!.Sections.Cl.SourceFiles.Should().BeEmpty();
        result.Profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
        result.ProfileWriteState.Should().NotBeNull();
        result.ProfileWriteState!.SourceExisted.Should().BeFalse();
        result.ProfileWriteState.TargetExisted.Should().BeFalse();
        result.ProfileWriteState.SourceHash.Should().Be(result.ProfileHash);
        result.ProfileWriteState.TargetPath.Should().Be(result.ProfileWriteTarget);
        File.Exists(result.ProfileWriteTarget).Should().BeFalse();
    }

    private static void AssertSelected(ActiveProjectProfileService.ActiveLoadResult result,
        ExistingProjectProfileSelection.Preview preview)
    {
        result.IsUsable.Should().BeTrue();
        result.IsGeneratedForDrawing.Should().BeFalse();
        result.Profile!.ProfileId.Should().Be(preview.ProfileId);
        result.Profile.ProjectName.Should().Be(preview.ProjectName);
        result.ProfileSource.Should().Be(preview.Path);
        result.ProfileWriteTarget.Should().Be(preview.Path);
        result.ProfileHash.Should().Be(preview.Hash);
        result.ProfileWriteState.Should().NotBeNull();
        result.ProfileWriteState!.SourcePath.Should().Be(preview.Path);
        result.ProfileWriteState.TargetPath.Should().Be(preview.Path);
        result.ProfileWriteState.SourceHash.Should().Be(preview.Hash);
        result.ProfileWriteState.TargetHash.Should().Be(preview.Hash);
        result.ProfileWriteState.SourceExisted.Should().BeTrue();
        result.ProfileWriteState.TargetExisted.Should().BeTrue();
    }

    [Fact]
    public void TwoUntitledDocumentsFromSameTemplate_CancelSaveAndPicker_RetainSeparateUnwrittenSeeds()
    {
        using var f = new Fixture();
        var a = new object(); var b = new object();
        var identityA = Unsaved("Drawing1.dwg"); var identityB = Unsaved("Drawing2.dwg");
        var firstA = Load(a, identityA, f.ProfileA);
        var firstB = Load(b, identityB, f.ProfileA);
        AssertSeed(firstA, a); AssertSeed(firstB, b);
        firstA.ProfileHash.Should().NotBe(firstB.ProfileHash);

        // Inspecting a choice and canceling it is not a Bind or a YAML write.
        ExistingProjectProfileSelection.Inspect(f.ProfileA);
        Action bindUntitled = () => ExistingProjectProfileSelection.Bind(a,
            identityA.DrawingPath, ExistingProjectProfileSelection.Inspect(f.ProfileA), null);
        bindUntitled.Should().Throw<InvalidOperationException>();
        SavedDrawingContinuationPolicy.Decide(true, true, identityA.IsSaved)
            .Should().Be(SavedDrawingContinuationDecision.SaveIncomplete);
        var reloadedA = Load(a, Unsaved("Drawing1.dwg"), f.ProfileB);
        AssertSeed(reloadedA, a);
        reloadedA.ProfileHash.Should().Be(firstA.ProfileHash);
        Load(b, identityB).ProfileHash.Should().Be(firstB.ProfileHash);
        ExistingProjectProfileSelection.Current(a).Should().BeNull();
        ExistingProjectProfileSelection.Current(b).Should().BeNull();
        f.AssertUnchanged();
    }

    [Fact]
    public void BothSaveAsPaths_ThenExplicitBind_ReloadTheCorrectSameIdFileWithoutCrossDocumentBleed()
    {
        using var f = new Fixture(); var a = new object(); var b = new object();
        var beforeA = Load(a, Unsaved("Drawing1.dwg"));
        var beforeB = Load(b, Unsaved("Drawing2.dwg"));
        var savedA = Saved(f.DrawingA); var savedB = Saved(f.DrawingB);
        var generatedA = Load(a, savedA); var generatedB = Load(b, savedB);
        generatedA.Profile!.ProfileId.Should().NotBe(beforeA.Profile!.ProfileId);
        generatedB.Profile!.ProfileId.Should().NotBe(beforeB.Profile!.ProfileId);
        generatedA.Profile.ProfileId.Should().NotBe(generatedB.Profile.ProfileId);
        File.Exists(generatedA.ProfileWriteTarget).Should().BeFalse();
        File.Exists(generatedB.ProfileWriteTarget).Should().BeFalse();

        var previewA = ExistingProjectProfileSelection.Inspect(f.ProfileA);
        var previewB = ExistingProjectProfileSelection.Inspect(f.ProfileB);
        ExistingProjectProfileSelection.Bind(a, savedA.DrawingPath, previewA, null);
        AssertSelected(Load(a, savedA, f.ProfileB), previewA);
        AssertSeed(Load(b, Unsaved("Drawing2.dwg"), f.ProfileA), b);
        ExistingProjectProfileSelection.Bind(b, savedB.DrawingPath, previewB, null);
        for (var reload = 0; reload < 2; reload++)
        {
            AssertSelected(Load(a, savedA, f.ProfileB), previewA);
            AssertSelected(Load(b, savedB, f.ProfileA), previewB);
        }
        f.AssertUnchanged();
    }

    [Fact]
    public void BoundDocumentSaveAs_RefusesOldBindingUntilExplicitRechoice_ThenLoadsPositiveAgain()
    {
        using var f = new Fixture(); var a = new object(); var other = new object();
        var previewA = ExistingProjectProfileSelection.Inspect(f.ProfileA);
        var previewB = ExistingProjectProfileSelection.Inspect(f.ProfileB);
        ExistingProjectProfileSelection.Bind(a, f.DrawingA, previewA, null);
        ExistingProjectProfileSelection.Bind(other, f.DrawingA, previewA, null);
        AssertSelected(Load(a, Saved(f.DrawingA)), previewA);
        var prior = ExistingProjectProfileSelection.Current(a);
        var changedPath = Saved(f.DrawingB);
        var refused = Load(a, changedPath, f.ProfileB);
        refused.IsUsable.Should().BeFalse(); refused.Profile.Should().BeNull();
        refused.IsGeneratedForDrawing.Should().BeFalse();
        refused.ProfileWriteState.Should().BeNull();
        refused.Findings.Should().Contain(finding => finding.Code == "SHR-PROFILE-SELECTION-DRAWING-CHANGED");
        ExistingProjectProfileSelection.Current(a).Should().BeSameAs(prior);
        ExistingProjectProfileSelection.Inspect(f.ProfileB); // cancel: still no Bind
        Load(a, changedPath).IsUsable.Should().BeFalse();
        ExistingProjectProfileSelection.Bind(a, changedPath.DrawingPath, previewB, prior);
        AssertSelected(Load(a, changedPath), previewB);
        AssertSelected(Load(a, changedPath), previewB);
        AssertSelected(Load(other, Saved(f.DrawingA)), previewA);
        f.AssertUnchanged();
    }

    [Fact]
    public void FailedOrContradictoryIdentity_DoesNotConsumeBinding_ValidSnapshotLoadsSelectedProfileAgain()
    {
        using var f = new Fixture(); var document = new object();
        var preview = ExistingProjectProfileSelection.Inspect(f.ProfileA);
        ExistingProjectProfileSelection.Bind(document, f.DrawingA, preview, null);
        var binding = ExistingProjectProfileSelection.Current(document);
        foreach (var identity in new[]
        {
            SavedDrawingPathPolicy.Capture(() => throw new IOException("SYNTHETIC getter failure"),
                () => f.DrawingA, () => f.DrawingA),
            SavedDrawingPathPolicy.Evaluate(1, f.DrawingA, f.DrawingB),
        })
        {
            identity.Failure.Should().NotBeNullOrWhiteSpace();
            var result = Load(document, identity, f.ProfileB);
            result.Profile.Should().BeNull(); result.IsUsable.Should().BeFalse();
            result.IsGeneratedForDrawing.Should().BeFalse(); result.ProfileWriteState.Should().BeNull();
            ExistingProjectProfileSelection.Current(document).Should().BeSameAs(binding);
            AssertSelected(Load(document, Saved(f.DrawingA), f.ProfileB), preview);
        }
        f.AssertUnchanged();
    }
}
