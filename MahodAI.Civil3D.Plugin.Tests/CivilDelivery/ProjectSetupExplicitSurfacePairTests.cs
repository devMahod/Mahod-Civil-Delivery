using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Pair = MahodAI.CivilDelivery.Shared.ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public class ProjectSetupExplicitSurfacePairTests
{
    private const string Drawing = "125b30f2-68bd-4c70-b9cb-d8c85d849501";
    private static ProjectSetupScan Scan()
    {
        var s = new ProjectSetupScan { RunId = "synthetic-surface-pair", ProjectProfileId = "new-road",
            Drawing = @"C:\synthetic-only\NewRoad.dwg", DrawingFingerprint = Drawing };
        s.Alignments.Add(new() { Name = "Main Street", Handle = "C3", Length = 100, EndStation = 100 });
        s.ClLayerCandidates.Add(new() { Layer = "arbitrary-cut-線", TwoPointCount = 1 });
        s.Sources.Add(new() { Name = "Terrain survey", Kind = "surface", Handle = "A1" });
        s.Sources.Add(new() { Name = "כביש מוצע", Kind = "surface", Handle = "B2" });
        return s;
    }
    private static Pair Choice() => new() { AlignmentName = "Main Street", AlignmentHandle = "C3",
        ExistingName = "Terrain survey", ExistingHandle = "A1", DesignName = "כביש מוצע", DesignHandle = "B2" };
    private static ProjectSetupSelection Selection(Pair? pair = null, bool legacyCaller = false, bool clearPairs = false,
        string? approver = "engineer") => new()
    {
        ClLayers = { "arbitrary-cut-線" }, AllowedAlignments = { "Main Street" },
        SampledSources = { ["Terrain survey"] = "surface", ["כביש מוצע"] = "surface" },
        SurfacePairs = legacyCaller ? null : clearPairs ? new() : new() { pair ?? Choice() }, ApprovedBy = approver,
    };
    private static ProjectProfile Profile() => new() { ProfileId = "new-road" };

    [Theory]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData(null, true)]     // no surface pair: the setup guard itself must refuse
    [InlineData("   ", true)]
    public void AMissingApproverRefusesTheSetupInsteadOfTakingTheWindowsAccount(string? approver, bool withoutPairs)
    {
        // b24 (Codex 11:18): no fallback — nothing is written without a confirmed approver.
        var target = Path.Combine(Path.GetTempPath(), "surface-pair-" + Guid.NewGuid().ToString("N"), "profile.yaml");
        var profile = Profile();
        var selection = Selection(legacyCaller: withoutPairs, approver: approver);
        var refused = Assert.Throws<InvalidOperationException>(() =>
            ProjectSetupService.SaveCoreForContractTests(profile, selection, Scan(), target));
        Assert.Contains("מאשר", refused.Message);
        Assert.False(File.Exists(target));
        Assert.Empty(profile.Sections.Sources.SurfacePairs);
        if (!withoutPairs)
            Assert.Throws<InvalidOperationException>(() => ProjectSetupService.ValidateSurfacePairs(selection, Scan()));
    }

    [Fact]
    public void SavePersistsCurrentApprovedPairThenReloadsIt_WithoutInventingNames()
    {
        var target = Path.Combine(Path.GetTempPath(), "surface-pair-" + Guid.NewGuid().ToString("N"), "profile.yaml");
        var profile = Profile();
        ProjectSetupService.SaveCoreForContractTests(profile, Selection(), Scan(), target);
        Assert.True(File.Exists(target));
        var pair = Assert.Single(profile.Sections.Sources.SurfacePairs);
        Assert.Equal(Drawing, pair.DrawingFingerprint); Assert.Equal("engineer", pair.ApprovedBy);
        Assert.Equal(DateTimeKind.Utc, pair.ApprovedAtUtc!.Value.Kind);
        var reopened = ProjectProfileLoader.LoadFromFile(target).Profile;
        Assert.NotNull(reopened); Assert.Equal(pair, Assert.Single(reopened!.Sections.Sources.SurfacePairs));
        Assert.Equal(DateTimeKind.Utc, reopened.Sections.Sources.SurfacePairs[0].ApprovedAtUtc!.Value.Kind);
        Assert.True(SectionSourceSelectionLogic.SelectExplicitPair(Drawing, pair.AlignmentName, pair.AlignmentHandle,
            Scan().Sources.Select(s => new SectionSourceSelectionLogic.Identity(s.Name, s.Handle!)).ToArray(),
            reopened.Sections.Sources.SurfacePairs).IsValid);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("wrong-kind")] [InlineData("changed-handle")]
    public void InvalidScanIdentityDoesNotMutateOrPublish(string failure)
    {
        var profile = Profile(); var scan = Scan(); var selection = Selection();
        if (failure == "duplicate") scan.Sources.Add(new() { Name = "Terrain survey", Kind = "surface", Handle = "D4" });
        if (failure == "wrong-kind") { scan.Sources.Clear(); scan.Sources.Add(new() { Name = "Terrain survey", Kind = "corridor", Handle = "A1" }); }
        if (failure == "changed-handle") selection = Selection(Choice() with { ExistingHandle = "A2" });
        var before = JsonSerializer.Serialize(profile);
        var target = Path.Combine(Path.GetTempPath(), "surface-refuse-" + Guid.NewGuid().ToString("N") + ".yaml");
        Assert.Throws<InvalidOperationException>(() => ProjectSetupService.SaveCoreForContractTests(profile, selection, scan, target));
        Assert.False(File.Exists(target)); Assert.Equal(before, JsonSerializer.Serialize(profile));
    }

    [Fact]
    public void PublicationFailureRestoresPairAndAllOtherProfileState()
    {
        var profile = Profile(); profile.Sections.Sources.SurfacePairs.Add(Choice() with { ApprovedBy = "old" });
        var before = JsonSerializer.Serialize(profile);
        var directory = Path.Combine(Path.GetTempPath(), "surface-write-refuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Assert.ThrowsAny<Exception>(() => ProjectSetupService.SaveCoreForContractTests(profile, Selection(), Scan(), directory));
        Assert.Equal(before, JsonSerializer.Serialize(profile));
    }

    [Fact]
    public void StaleProfileCompareAndSwapRefusesBeforeChangingThePair()
    {
        var profile = Profile();
        var target = Path.Combine(Path.GetTempPath(), "surface-cas-" + Guid.NewGuid().ToString("N") + ".yaml");
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile,
            MahodAI.CivilDelivery.Estimate.EstimateTraceIdentity.EffectiveProfileHash(profile), target);
        profile.Sections.Sources.SurfacePairs.Add(Choice() with { ApprovedBy = "concurrent engineer" });
        var changed = JsonSerializer.Serialize(profile);
        Assert.ThrowsAny<Exception>(() => ProjectSetupService.SaveCoreForContractTests(profile, Selection(), Scan(), target, expected));
        Assert.Equal(changed, JsonSerializer.Serialize(profile)); Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void LegacyCallerPreservesPairs_ExplicitReturnToLegacyRemovesOnlySelectedAlignment(bool legacyCaller)
    {
        var profile = Profile();
        var old = Choice() with { ApprovedBy = "previous engineer" };
        var unrelated = old with { AlignmentName = "Other Street", AlignmentHandle = "E5" };
        profile.Sections.Sources.SurfacePairs.AddRange(new[] { old, unrelated });
        var selection = Selection(legacyCaller: legacyCaller, clearPairs: !legacyCaller);
        var target = Path.Combine(Path.GetTempPath(), "surface-legacy-" + Guid.NewGuid().ToString("N") + ".yaml");
        ProjectSetupService.SaveCoreForContractTests(profile, selection, Scan(), target);
        Assert.Contains(unrelated, profile.Sections.Sources.SurfacePairs);
        Assert.Equal(legacyCaller, profile.Sections.Sources.SurfacePairs.Contains(old));
    }

    [Fact]
    public void ConsumerUsesExactPairAndRoleSwapChangesFingerprint()
    {
        var profile = Profile(); var pair = Assert.Single(ProjectSetupService.ValidateSurfacePairs(Selection(), Scan()));
        profile.Sections.Sources.SurfacePairs.Add(pair);
        var record = Record(pair);
        Assert.Equal(pair, SectionSurfacePairPlanLogic.RequireExplicit(record, profile, Drawing));
        var first = SectionPlanLogic.ComputeFingerprint(record);
        record.ExplicitSurfacePair = pair with { ExistingName = pair.DesignName, ExistingHandle = pair.DesignHandle,
            DesignName = pair.ExistingName, DesignHandle = pair.ExistingHandle };
        Assert.NotEqual(first, SectionPlanLogic.ComputeFingerprint(record));
        Assert.Throws<InvalidOperationException>(() => SectionSurfacePairPlanLogic.RequireExplicit(record, profile, Drawing));
    }
    private static SectionPlanRecord Record(Pair pair) => new()
    {
        RecordId = "test-cut", SelectedAlignment = pair.AlignmentName, ExplicitSurfacePair = pair,
        Cl = new ClSourceRecord { RecordId = "test-cut", SourceHandle = "D4", SourceDrawing = "synthetic.dwg",
            SourceDrawingHash = new string('A', 64), SourceEntityType = "LINE", SourceLayer = "arbitrary-cut-線",
            SourceEndpoints = new double[] { 0, -10, 0, 10 }, WcsEndpoints = new double[] { 0, -10, 0, 10 } },
        PlannedSources = { new() { SourceName = pair.ExistingName!, SourceHandle = pair.ExistingHandle,
            SourceType = "surface", PlannedState = "sampled", Required = true },
            new() { SourceName = pair.DesignName!, SourceHandle = pair.DesignHandle,
                SourceType = "surface", PlannedState = "sampled", Required = true } },
    };

    [Fact]
    public void ActualDialogDoesNotAutoselectRoles_ThenAllowsArbitraryNamesAndCancelWithoutMutation() => Sta(() =>
    {
        var profile = Profile(); var scan = Scan(); var dialog = new ProjectSetupReviewDialog(scan, profile);
        dialog.Layers.Values.Single().IsChecked = true; dialog.Alignments.Values.Single().IsChecked = true;
        var pair = Assert.Single(dialog.SurfacePairs).Value;
        Assert.NotEqual(true, pair.Enabled.IsChecked); Assert.Null(pair.Existing.SelectedItem); Assert.Null(pair.Design.SelectedItem);
        pair.Enabled.IsChecked = true; pair.Existing.SelectedItem = scan.Sources[0]; pair.Design.SelectedItem = scan.Sources[0];
        dialog.Confirm.IsChecked = true; Assert.False(dialog.Save.IsEnabled);
        pair.Design.SelectedItem = scan.Sources[1]; Assert.False(dialog.Confirm.IsChecked);
        dialog.Confirm.IsChecked = true; Assert.True(dialog.Save.IsEnabled);
        dialog.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(dialog.ApprovedSelection); Assert.Empty(profile.Sections.Sources.SurfacePairs);
    });

    [Theory]
    [InlineData(790, 730)] [InlineData(530, 540)]
    public void ExplicitPairControlsAndSaveRemainReachableInTheActualDialog(int width, int height) => Sta(() =>
    {
        var scan = Scan(); var profile = Profile(); var dialog = new ProjectSetupReviewDialog(scan, profile);
        try
        {
            dialog.Layers.Values.Single().IsChecked = true; dialog.Alignments.Values.Single().IsChecked = true;
            dialog.SurfacePairReview.IsExpanded = true;
            var pair = Assert.Single(dialog.SurfacePairs).Value;
            pair.Enabled.IsChecked = true; pair.Existing.SelectedItem = scan.Sources[0]; pair.Design.SelectedItem = scan.Sources[1];
            dialog.Confirm.IsChecked = true;
            var frame = UnshownDialogRender.Attach(dialog, width, height);
            dialog.BodyScroll.ScrollToBottom(); frame.UpdateLayout();
            foreach (var element in new FrameworkElement[] { pair.Enabled, pair.Existing, pair.Design })
                UnshownDialogRender.AssertWithin(element, dialog.BodyScroll);
            foreach (var element in new FrameworkElement[] { dialog.Save, dialog.Cancel, dialog.Confirm, dialog.Validation })
                UnshownDialogRender.AssertWithin(element, frame);
            Assert.True(dialog.Save.IsEnabled); Assert.False(dialog.IsVisible);
            UnshownDialogRender.Save(frame, "MHD_SURFACE_PAIR_RENDER_DIR", $"explicit-surfaces-{width}x{height}");
            dialog.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var choice = Assert.Single(dialog.ApprovedSelection!.SurfacePairs!);
            Assert.Equal("Terrain survey", choice.ExistingName); Assert.Equal("כביש מוצע", choice.DesignName);
            Assert.Null(choice.ApprovedAtUtc); Assert.Empty(profile.Sections.Sources.SurfacePairs);
        }
        finally { dialog.Close(); }
    });
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
