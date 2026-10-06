using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// b24 (Codex 12:24 C): a unit review need observed once is a technical profile record (schema 7) that survives lost
/// extents, reopen and revocation, and is closed only by the digest of a decision for the same drawing. Synthetic only.
/// </summary>
public sealed class PhysicalDrawingUnitReviewRecordTests : IDisposable
{
    private const string Fingerprint = "5b2c7d1e-9f40-4a3b-8c6d-2e1f0a9b8c7d";
    private const string Host = @"C:\UnitTests\Review\Host.dwg";
    private const string SavedAs = @"C:\UnitTests\Review\Host copy.dwg";
    private static readonly PhysicalDrawingUnitPolicy.HostExtents ItmLike = new(195_496, 628_759, 200_124, 630_468);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhd-unit-review-" + Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_dir, "project-profile.yaml");

    public PhysicalDrawingUnitReviewRecordTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProjectProfile NewProfile() => new()
    {
        ProfileId = "unit-review-test",
        Sections = { Cl = { SourceFiles = { "CL.dwg" }, IntersectionToleranceM = 0.01 } },
    };

    private ProjectProfileWriter.ExpectedProfileState Expected(ProjectProfile profile) => File.Exists(ProfilePath)
        ? ProjectProfileWriter.CaptureExpectedState(ProfilePath, ArtifactHash.Sha256OfText(File.ReadAllText(ProfilePath)), ProfilePath)
        : ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), ProfilePath);

    private ProjectProfile Reload()
    {
        var loaded = ProjectProfileLoader.LoadFromFile(ProfilePath);
        Assert.True(loaded.IsUsable, string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Message)));
        return loaded.Profile!;
    }

    private static PhysicalDrawingUnitPolicy.Resolution Resolve(ProjectProfile profile, string path = Host,
        PhysicalDrawingUnitPolicy.HostExtents? extents = null) =>
        PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, path, profile.DrawingUnitDeclarations,
            hostExtents: extents, reviews: profile.DrawingUnitReviews);

    private static ProjectProfile.DrawingUnitDeclaration ConfirmFeet(string path = Host) =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, path, 2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2,
            "SYNTHETIC TEST REVIEWER", new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), "SYNTHETIC: real feet", "SYNTHETIC SOURCE");

    [Fact]
    public void DetectOnceBlockWithoutExtentsResolveRevokeBlockAndSaveAsNeedsItsOwnReview()
    {
        // 1. observed once → one technical record, no approval fields touched
        var profile = NewProfile();
        var first = Resolve(profile, extents: ItmLike);
        Assert.True(first.NeedsReview);
        var record = PhysicalDrawingUnitPolicy.NewReviewRecord(first, Fingerprint, Host, profile.DrawingUnitReviews, DateTime.UtcNow);
        Assert.NotNull(record);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewSuspicion, record!.Kind);
        profile.DrawingUnitReviews = new() { record };
        ProjectProfileWriter.SaveTechnicalRecord(profile, ProfilePath, "technical: unit review pending (suspicion)", Expected(profile));
        var text = File.ReadAllText(ProfilePath);
        Assert.Contains("# Technical record (no engineering approval)", text);
        Assert.Contains("drawing_unit_reviews:", text);
        Assert.Contains("schema_version: 7", text);
        var reopened = Reload();
        Assert.Null(reopened.Provenance.ApprovedBy);
        Assert.Null(reopened.Provenance.ApprovedAtUtc);
        // identical evidence → nothing new to write
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(Resolve(reopened, extents: ItmLike), Fingerprint, Host,
            reopened.DrawingUnitReviews, DateTime.UtcNow));

        // 2. reopened without readable extents → still blocked by the record
        var blind = Resolve(reopened);
        Assert.False(blind.IsSupported);
        Assert.True(blind.NeedsReview);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewSuspicion, blind.ReviewKind);

        // 3. a decision closes it in the same write
        var decision = ConfirmFeet();
        reopened.DrawingUnitDeclarations.Add(decision);
        PhysicalDrawingUnitPolicy.LinkReviews(reopened, decision, DateTime.UtcNow);
        ProjectProfileWriter.Save(reopened, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(reopened));
        var decided = Reload();
        var resolved = Resolve(decided);
        Assert.True(resolved.IsSupported);
        Assert.Equal("approved-recorded-unit", resolved.Authority);
        Assert.Equal(resolved.DeclarationDigest, Assert.Single(decided.DrawingUnitReviews!).ResolvedByDigest);

        // 4. revoking the decision keeps the record → blocked again, never back to the raw unit
        decided.DrawingUnitDeclarations.Clear();
        ProjectProfileWriter.Save(decided, ProfilePath, "revoke physical host unit declaration", "SYNTHETIC TEST REVIEWER", Expected(decided));
        var revoked = Reload();
        Assert.Single(revoked.DrawingUnitReviews!);
        Assert.True(Resolve(revoked).NeedsReview);

        // 5. SaveAs → the copy needs its own review; a decision for the copy is its own authority
        var copy = Resolve(revoked, SavedAs);
        Assert.Equal(PhysicalDrawingUnitPolicy.IdentityChanged, copy.FailureCode);
        var copyDecision = ConfirmFeet(SavedAs);
        revoked.DrawingUnitDeclarations.Add(copyDecision);
        PhysicalDrawingUnitPolicy.LinkReviews(revoked, copyDecision, DateTime.UtcNow);
        Assert.True(Resolve(revoked, SavedAs).IsSupported);
        Assert.Equal(2, revoked.DrawingUnitReviews!.Count);
    }

    [Fact]
    public void AnOverrideThatDidNotStartFromAReviewStillLeavesTheFactSoRevokingItBlocks()
    {
        var profile = NewProfile();
        var decision = ConfirmFeet();
        profile.DrawingUnitDeclarations.Add(decision);
        PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, DateTime.UtcNow);
        var record = Assert.Single(profile.DrawingUnitReviews!);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewDecision, record.Kind);
        Assert.True(Resolve(profile).IsSupported);
        profile.DrawingUnitDeclarations.Clear();
        Assert.True(Resolve(profile).NeedsReview);
    }

    [Fact]
    public void AFailedTechnicalWriteChangesNothingAndClaimsNothing()
    {
        var profile = NewProfile();
        ProjectProfileWriter.Save(profile, ProfilePath, "initial", "SYNTHETIC TEST REVIEWER", Expected(profile));
        var loaded = Reload();
        var stale = Expected(loaded);
        File.AppendAllText(ProfilePath, "# concurrent change\n");
        var before = File.ReadAllText(ProfilePath);
        var version = loaded.Provenance.Version;
        loaded.DrawingUnitReviews = new() { PhysicalDrawingUnitPolicy.NewReviewRecord(
            Resolve(loaded, extents: ItmLike), Fingerprint, Host, null, DateTime.UtcNow)! };
        Assert.ThrowsAny<Exception>(() =>
            ProjectProfileWriter.SaveTechnicalRecord(loaded, ProfilePath, "technical: unit review pending", stale));
        Assert.Equal(before, File.ReadAllText(ProfilePath));
        Assert.Equal(version, loaded.Provenance.Version);
        Assert.Equal("SYNTHETIC TEST REVIEWER", loaded.Provenance.ApprovedBy);
    }

    [Fact]
    public void ReviewRecordsNeedSchema7AndAreValidated()
    {
        var profile = NewProfile();
        profile.DrawingUnitReviews = new() { PhysicalDrawingUnitPolicy.NewReviewRecord(
            Resolve(profile, extents: ItmLike), Fingerprint, Host, null, DateTime.UtcNow)! };
        ProjectProfileWriter.SaveTechnicalRecord(profile, ProfilePath, "technical", Expected(profile));
        var downgraded = ProjectProfileLoader.LoadFromText(File.ReadAllText(ProfilePath).Replace("schema_version: 7", "schema_version: 6"));
        Assert.False(downgraded.IsUsable);
        Assert.Contains(downgraded.Findings, f => f.Code == "SHR-PROFILE-SCHEMA7-CONTENT");

        var good = profile.DrawingUnitReviews[0];
        ProjectProfile.DrawingUnitReview Copy() => new()
        {
            DrawingFingerprint = good.DrawingFingerprint, DrawingPath = good.DrawingPath, ObservedInsunitsCode = good.ObservedInsunitsCode,
            Kind = good.Kind, Reason = good.Reason, Evidence = good.Evidence, ObservedAtUtc = good.ObservedAtUtc,
            RecordedBy = good.RecordedBy, ResolvedByDigest = good.ResolvedByDigest,
        };
        foreach (var broken in new Action<ProjectProfile.DrawingUnitReview>[]
                 {
                     r => r.Kind = "guessed", r => r.Reason = " ", r => r.ObservedAtUtc = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local),
                     r => r.ResolvedByDigest = "short", r => r.DrawingPath = "relative.dwg", r => r.RecordedBy = null,
                 })
        {
            var bad = Copy(); broken(bad);
            Assert.NotEmpty(PhysicalDrawingUnitPolicy.ValidateReviews(new[] { bad }));
            Assert.Equal(PhysicalDrawingUnitPolicy.ReviewInvalid,
                PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Host, null, reviews: new[] { bad }).FailureCode);
        }
        Assert.NotEmpty(PhysicalDrawingUnitPolicy.ValidateReviews(new[] { Copy(), Copy() }));   // one record per drawing
        // A profile without records keeps its bytes: no key, no schema 7.
        var plain = NewProfile();
        Assert.False(ProjectProfileSchemaPolicy.HasSchema7Content(plain));
        Assert.DoesNotContain("DrawingUnitReviews", System.Text.Json.JsonSerializer.Serialize(plain));
    }
}
