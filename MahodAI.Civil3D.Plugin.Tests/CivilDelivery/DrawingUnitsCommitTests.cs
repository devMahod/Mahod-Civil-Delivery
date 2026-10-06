using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// b25 (Codex 16:10 P2-1): the commit after the units review reads the live drawing again — identity, raw unit and the
/// Civil evidence shown — after the dialog and the preview cleanup, against the same original scope. Host-free: the
/// readers are the ones the palette passes in. Synthetic only.
/// </summary>
public sealed class DrawingUnitsCommitTests
{
    private const string Fingerprint = "2b7f4e1a-6c3d-4f8e-9a0b-5d6c7e8f9a0b";
    private const string Drawing = @"C:\SYNTHETIC-ONLY\Commit\Host.dwg";
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Inches = new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 1);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Meters = new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 6);

    private sealed class Live
    {
        public int Raw;
        public string Fingerprint = DrawingUnitsCommitTests.Fingerprint;
        public PhysicalDrawingUnitPolicy.CivilUnitEvidence Civil = Inches;
        public bool CleanupAllowed = true;
        public Action? DuringCleanup;
        public readonly List<string> Log = new();
        public int Clones;
    }

    private static ProjectProfile.DrawingUnitDeclaration Decision(PhysicalDrawingUnitPolicy.CivilUnitEvidence civil) =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Drawing, 0, PhysicalDrawingUnitPolicy.UnitlessMetres, 6,
            "SYNTHETIC TEST REVIEWER", DateTime.UtcNow, "SYNTHETIC: drawn in metres", "SYNTHETIC SOURCE", civil);

    private static ProjectProfile? Commit(Live live, ProjectProfile.DrawingUnitDeclaration? decision, bool revoke,
        PhysicalDrawingUnitPolicy.CivilUnitEvidence shown, ProjectProfile? baseProfile = null) =>
        DrawingUnitDeclarationReview.PrepareCommit(decision, revoke, Fingerprint, Drawing, 0, shown,
            () => { live.Log.Add("identity"); return (live.Raw, live.Fingerprint); },
            () => { live.Log.Add("civil"); return live.Civil; },
            () => live.Log.Add("scope"),
            () => { live.Log.Add("cleanup"); live.DuringCleanup?.Invoke(); return live.CleanupAllowed; },
            () => { live.Log.Add("clone"); live.Clones++; return baseProfile ?? new ProjectProfile(); },
            DateTime.UtcNow);

    [Fact]
    public void TheSameEvidenceIsCommittedAfterEveryCheckInOrder()
    {
        var live = new Live();
        var updated = Commit(live, Decision(Inches), revoke: false, shown: Inches)!;
        Assert.Equal(new[] { "scope", "identity", "cleanup", "scope", "civil", "identity", "scope", "clone" }, live.Log);
        var declaration = Assert.Single(updated.DrawingUnitDeclarations);
        Assert.Equal(PhysicalDrawingUnitPolicy.DigestOf(declaration), Assert.Single(updated.DrawingUnitReviews!).ResolvedByDigest);
    }

    [Fact]
    public void CivilEvidenceThatChangedAfterTheDialogStopsTheCommitBeforeAnyCloneOrLink()
    {
        var live = new Live { Civil = Meters };   // identity unchanged, Civil reads another unit now
        Assert.Throws<InvalidOperationException>(() => Commit(live, Decision(Inches), revoke: false, shown: Inches));
        Assert.Equal(0, live.Clones);
    }

    [Fact]
    public void CivilEvidenceChangedByThePreviewCleanupStopsTheCommit()
    {
        var live = new Live();
        live.DuringCleanup = () => live.Civil = PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC: lost during cleanup");
        Assert.Throws<InvalidOperationException>(() => Commit(live, Decision(Inches), revoke: false, shown: Inches));
        Assert.Equal(0, live.Clones);
    }

    [Fact]
    public void AnIdentityOrRawUnitChangedByTheCleanupStopsTheCommit()
    {
        foreach (var change in new Action<Live>[] { l => l.Raw = 6, l => l.Fingerprint = Guid.NewGuid().ToString("D") })
        {
            var live = new Live();
            live.DuringCleanup = () => change(live);
            Assert.Throws<InvalidOperationException>(() => Commit(live, Decision(Inches), revoke: false, shown: Inches));
            Assert.Equal(0, live.Clones);
        }
    }

    [Fact]
    public void TheSameStatusAndUnitWithAnotherDetailTextIsTheSameEvidence()
    {
        var shown = PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC: first read");
        var live = new Live { Civil = PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC: second read, other words") };
        Assert.NotNull(Commit(live, Decision(shown), revoke: false, shown: shown));
        Assert.Equal(1, live.Clones);
    }

    // Codex 17:57: freshness between the dialog and the save covers unmapped readings too — N→M, a mapped unit or a lost
    // reading after an unmapped one is another evidence, and an unmapped one that stays the same commits.
    [Fact]
    public void AnUnmappedReadingMustStayTheSameValueUntilTheSave()
    {
        var other6 = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(6, "6");
        foreach (var later in new[]
                 {
                     PhysicalDrawingUnitPolicy.CivilUnitsFromApi(3, "3"), PhysicalDrawingUnitPolicy.CivilUnitsFromApi(2, "Meters"),
                     PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC: lost"),
                 })
        {
            var live = new Live { Civil = later };
            Assert.Throws<InvalidOperationException>(() => Commit(live, Decision(other6), revoke: false, shown: other6));
            Assert.Equal(0, live.Clones);
        }
        var same = new Live { Civil = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(6, "6") };
        Assert.NotNull(Commit(same, Decision(other6), revoke: false, shown: other6));
    }

    [Fact]
    public void ARevocationNeedsNoCivilReadingAndRemovesOnlyTheDeclaration()
    {
        var existing = new ProjectProfile();
        var decision = Decision(Inches);
        existing.DrawingUnitDeclarations.Add(decision);
        PhysicalDrawingUnitPolicy.LinkReviews(existing, decision, DateTime.UtcNow);
        var live = new Live { Civil = Meters };
        var updated = Commit(live, null, revoke: true, shown: Inches, baseProfile: existing)!;
        Assert.DoesNotContain("civil", live.Log);
        Assert.Empty(updated.DrawingUnitDeclarations);
        Assert.Single(updated.DrawingUnitReviews!);
    }

    [Fact]
    public void ARefusedCleanupOrADecisionNotBoundToTheShownEvidenceWritesNothing()
    {
        var refused = new Live { CleanupAllowed = false };
        Assert.Null(Commit(refused, Decision(Inches), revoke: false, shown: Inches));
        Assert.Equal(0, refused.Clones);
        Assert.DoesNotContain("civil", refused.Log);

        var mismatch = new Live();
        Assert.Throws<InvalidOperationException>(() => Commit(mismatch, Decision(Meters), revoke: false, shown: Inches));
        Assert.Empty(mismatch.Log);
    }
}
