using System;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SavedDrawingPathPolicyTests
{
    private const string Dwt = @"C:\Templates\Autodesk Civil 3D (Metric) NCS.dwt";
    private const string Dwg = @"C:\SYNTHETIC\Project\Site.dwg";
    private static readonly string Hash = new('a', 64);

    [Theory]
    [InlineData("Drawing1.dwg")]
    [InlineData("Drawing2.dwg")]
    public void NewDocumentNeverReadsOrHashesItsTemplate_AndRoutesToSaveAs(string name)
    {
        var filenameReads = 0;
        var hashReads = 0;
        var identity = SavedDrawingPathPolicy.Capture(() => 0, () => name,
            () => { filenameReads++; return Dwt; });
        identity.IsSaved.Should().BeFalse();
        identity.NeedsSaveAs.Should().BeTrue();
        identity.DrawingPath.Should().BeEmpty();
        identity.Failure.Should().BeNull();
        Action hash = () => identity.ReadSavedHash(_ => { hashReads++; return Hash; });
        hash.Should().Throw<InvalidOperationException>();
        filenameReads.Should().Be(0);
        hashReads.Should().Be(0);
        EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath, "", 0, identity.Failure)
            .Action.Should().Be(EstimateSourceSnapshotPolicy.ScanSourceAction.SaveAsAndScan);
        EstimateSourceSnapshotPolicy.InitialFailure("", 0).Should().NotBeNull(
            "the actual profile-decision guard requires a saved hash before any persistence");
    }

    [Theory]
    [InlineData(Dwt, Dwt)]
    [InlineData(Dwt, Dwg)]
    [InlineData(Dwg, Dwt)]
    public void ExplicitlyOpenedTemplateStillRequiresSaveAs(string database, string document)
    {
        var identity = SavedDrawingPathPolicy.Evaluate(1, database, document);
        identity.NeedsSaveAs.Should().BeTrue();
        identity.IsSaved.Should().BeFalse();
        identity.DrawingPath.Should().BeEmpty();
        Action read = () => identity.ReadSavedHash(_ => throw new Exception("must not read a template"));
        read.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    public void UnknownNamedStateNeverUsesEvenMatchingExistingDwgPaths(int? titled)
    {
        var identity = SavedDrawingPathPolicy.Evaluate(titled, Dwg, Dwg);
        identity.IsSaved.Should().BeFalse();
        identity.NeedsSaveAs.Should().BeFalse();
        identity.Failure.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null, Dwg)]
    [InlineData(Dwg, null)]
    [InlineData("Drawing1.dwg", Dwg)]
    [InlineData(Dwg, "Drawing1.dwg")]
    [InlineData(@"C:Site.dwg", Dwg)]
    [InlineData(@"\SYNTHETIC\Site.dwg", Dwg)]
    [InlineData(@"C:\SYNTHETIC\Site.dxf", @"C:\SYNTHETIC\Site.dxf")]
    [InlineData(@"C:\SYNTHETIC\Other\Site.dwg", Dwg)]
    public void MissingRelativeNonDwgOrContradictoryPathsRefuse(string? database, string? document)
    {
        var identity = SavedDrawingPathPolicy.Evaluate(1, database, document);
        identity.IsSaved.Should().BeFalse();
        identity.NeedsSaveAs.Should().BeFalse();
        identity.Failure.Should().NotBeNullOrWhiteSpace();
        var hashReads = 0;
        Action read = () => identity.ReadSavedHash(_ => { hashReads++; return Hash; });
        read.Should().Throw<InvalidOperationException>();
        hashReads.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryRequiredNativeGetterFailureIsExplicitAndNeverBecomesSaved(int failingGetter)
    {
        var identity = SavedDrawingPathPolicy.Capture(
            () => failingGetter == 0 ? throw new InvalidOperationException("DWGTITLED unreadable") : 1,
            () => failingGetter == 1 ? throw new InvalidOperationException("Document.Name unreadable") : Dwg,
            () => failingGetter == 2 ? throw new InvalidOperationException("Filename unreadable") : Dwg);
        identity.IsSaved.Should().BeFalse();
        identity.NeedsSaveAs.Should().BeFalse();
        identity.Failure.Should().Contain("unreadable");
    }

    [Fact]
    public void RefusedIdentityStopsBeforeDbmodRead_ScanAndProfileGuardsBothBlockInsteadOfOfferingSave()
    {
        foreach (var identity in new[]
        {
            SavedDrawingPathPolicy.Evaluate(1, Dwg, @"C:\SYNTHETIC\Different.dwg"),
            SavedDrawingPathPolicy.Capture(() => throw new InvalidOperationException("DWGTITLED failed"),
                () => Dwg, () => Dwg),
        })
        {
            identity.Failure.Should().NotBeNullOrWhiteSpace();
            // CaptureLive returns at this identity failure before reading DBMOD.
            // Source wiring separately locks that exact native-adapter return shape.
            var readiness = EstimateSourceSnapshotPolicy.ForScan("", "", null, identity.Failure);
            readiness.Action.Should().Be(EstimateSourceSnapshotPolicy.ScanSourceAction.Blocked);
            readiness.CanSaveAndResume.Should().BeFalse();
            (identity.Failure ?? EstimateSourceSnapshotPolicy.InitialFailure("", null))
                .Should().NotBeNull("CaptureReadySource must refuse before a profile modal or persistence");
        }
    }

    [Fact]
    public void TrueSavedCanonicalDwgRetainsDiskHashAndStrictDbmodGates()
    {
        var identity = SavedDrawingPathPolicy.Evaluate(1,
            @"c:\synthetic\Project\unused\..\SITE.DWG", Dwg);
        identity.IsSaved.Should().BeTrue();
        identity.DrawingPath.Should().Be(Dwg);
        var readPath = "";
        identity.ReadSavedHash(path => { readPath = path; return Hash; }).Should().Be(Hash);
        readPath.Should().Be(Dwg);
        EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath, Hash, 0).IsReady.Should().BeTrue();
        EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath, Hash, 16).IsReady.Should().BeFalse();
        EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath, "", 0).IsReady.Should().BeFalse();
    }

    [Fact]
    public void Original6422SavedPathIsUnchanged()
    {
        const string path = @"C:\Users\arthurf\MahodCivilDelivery_Work\6422-local-mirror\Civil3d\PD\6422-CIVIL-WEST.natali-060926.native54.dwg";
        var identity = SavedDrawingPathPolicy.Evaluate(1, path, path);
        identity.IsSaved.Should().BeTrue();
        identity.DrawingPath.Should().Be(path);
    }

    [Fact]
    public void TwoDocumentsFromSameTemplateHaveSeparateSessionSeedsAndCannotPassApprovalGate()
    {
        var first = new object();
        var second = new object();
        var firstId = SavedDrawingPathPolicy.UnsavedProfileId(first);
        firstId.Should().Be(SavedDrawingPathPolicy.UnsavedProfileId(first));
        firstId.Should().NotBe(SavedDrawingPathPolicy.UnsavedProfileId(second));
        firstId.Should().StartWith("drawing-unsaved-").And.NotContain("metric");
        foreach (var name in new[] { "Drawing1.dwg", "Drawing2.dwg" })
        {
            var identity = SavedDrawingPathPolicy.Evaluate(0, Dwt, name);
            EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath, "", 0).IsReady.Should().BeFalse();
            EstimateSourceSnapshotPolicy.InitialFailure("", 0).Should().NotBeNull();
        }
    }

    [Fact]
    public void CancelSaveAsCannotConsumeOrResumeProfilePersistence_EvenIfOldCommandArrives()
    {
        var identity = SavedDrawingPathPolicy.Evaluate(0, Dwt, "Drawing1.dwg");
        var readiness = EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath, "", 0);
        var writes = 0;
        // These are the same token/readiness policies invoked before the palette's
        // actual ReloadProfile + continuation. No native save or file write is simulated.
        if (SavedDrawingContinuationPolicy.CanConsume(null, "old-save", false) &&
            SavedDrawingContinuationPolicy.Decide(false, true, readiness.IsReady) == SavedDrawingContinuationDecision.Resume)
            writes++;
        writes.Should().Be(0);
        SavedDrawingContinuationPolicy.Decide(true, true, readiness.IsReady)
            .Should().Be(SavedDrawingContinuationDecision.SaveIncomplete);
    }

    [Fact]
    public void CompletedSaveAsUsesOnlyNewDwgIdentityAndCanResume_DifferentPathsStayDistinct()
    {
        var original = SavedDrawingPathPolicy.Evaluate(0, Dwt, "Drawing1.dwg");
        original.DrawingPath.Should().BeEmpty();
        var first = SavedDrawingPathPolicy.Evaluate(1, Dwg, Dwg);
        const string other = @"C:\SYNTHETIC\OtherProject\Site.dwg";
        var second = SavedDrawingPathPolicy.Evaluate(1, other, other);
        first.DrawingPath.Should().NotBe(second.DrawingPath);
        foreach (var identity in new[] { first, second })
        {
            var readiness = EstimateSourceSnapshotPolicy.ForScan(identity.DrawingPath,
                identity.ReadSavedHash(_ => Hash), 0);
            SavedDrawingContinuationPolicy.Decide(true, true, readiness.IsReady)
                .Should().Be(SavedDrawingContinuationDecision.Resume);
        }
    }
}
