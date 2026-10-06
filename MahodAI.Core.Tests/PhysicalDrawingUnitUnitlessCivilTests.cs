using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// b25 (Codex 15:12, found on LA-40: INSUNITS 0 with Civil drawing units in inches): a unitless host is never SI by
/// itself; a new metres decision binds the Civil evidence it was made against; an existing kind-less metres declaration
/// keeps its bytes and digest but is not used while Civil contradicts it; and that contradiction stays recorded when
/// Civil becomes unreadable. Synthetic only.
/// </summary>
public sealed class PhysicalDrawingUnitUnitlessCivilTests : IDisposable
{
    private const string Fingerprint = "3d0e6a2b-71c4-4f19-9b5e-8a7c6d5e4f30";
    private const string Host = @"C:\UnitTests\Unitless\Host.dwg";
    private static readonly DateTime At = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Inches = new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 1);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Meters = new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 6);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Unreadable = PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC: not readable");
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence None = PhysicalDrawingUnitPolicy.CivilUnitEvidence.None;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhd-unitless-civil-" + Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_dir, "project-profile.yaml");

    public PhysicalDrawingUnitUnitlessCivilTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProjectProfile NewProfile() => new()
    {
        ProfileId = "unitless-civil-test",
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

    private static PhysicalDrawingUnitPolicy.Resolution Resolve(ProjectProfile profile,
        PhysicalDrawingUnitPolicy.CivilUnitEvidence civil, bool host = true) =>
        PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Host, profile.DrawingUnitDeclarations,
            isHostDrawing: host, civilUnits: civil, reviews: profile.DrawingUnitReviews);

    private static ProjectProfile.DrawingUnitDeclaration Legacy() =>
        PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, Host, 0,
            "SYNTHETIC TEST REVIEWER", At, "SYNTHETIC: drawn in metres", "SYNTHETIC SOURCE");

    private static ProjectProfile.DrawingUnitDeclaration Reviewed(PhysicalDrawingUnitPolicy.CivilUnitEvidence civil) =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Host, 0, PhysicalDrawingUnitPolicy.UnitlessMetres,
            PhysicalDrawingUnitPolicy.Metres, "SYNTHETIC TEST REVIEWER", At, "SYNTHETIC: drawn in metres", "SYNTHETIC SOURCE", civil);

    private static ProjectProfile WithDecision(ProjectProfile.DrawingUnitDeclaration decision)
    {
        var profile = NewProfile();
        profile.DrawingUnitDeclarations.Add(decision);
        if (decision.DecisionKind != null) PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, At);
        return profile;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AnUndeclaredUnitlessHostIsNeverSiWhateverCivilSays(int civilCase)
    {
        var civil = new[] { Inches, Meters, Unreadable, None }[civilCase];
        var units = Resolve(NewProfile(), civil);
        Assert.False(units.IsSupported);
        Assert.Null(units.EffectiveUnitCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitUnknown, units.FailureCode);
        Assert.False(units.NeedsReview);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ANewMetresDecisionBindsTheCivilEvidenceItWasMadeAgainstAndSurvivesSaveAndReopen(int civilCase)
    {
        var civil = new[] { Meters, Inches, Unreadable, None }[civilCase];
        var decision = Reviewed(civil);
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitlessMetres, decision.DecisionKind);
        Assert.Equal(civil.Status, decision.CivilDrawingUnitStatus);
        Assert.Equal(civil.UnitCode, decision.CivilDrawingUnitCode);
        var profile = WithDecision(decision);
        var record = Assert.Single(profile.DrawingUnitReviews!);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewDecision, record.Kind);

        ProjectProfileWriter.Save(profile, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(profile));
        Assert.Contains("schema_version: 8", File.ReadAllText(ProfilePath));
        Assert.Contains("decision_kind: unitless-metres", File.ReadAllText(ProfilePath));
        var reopened = Reload();
        var units = Resolve(reopened, civil);
        Assert.True(units.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.Metres, units.EffectiveUnitCode);
        Assert.Equal(1.0, units.LinearToMetres);
        Assert.Equal("approved-unitless-host-declaration", units.Authority);
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitlessMetres, units.DecisionKind);
        Assert.Equal(PhysicalDrawingUnitPolicy.DigestOf(decision), units.DeclarationDigest);
        Assert.Equal(units.DeclarationDigest, Assert.Single(reopened.DrawingUnitReviews!).ResolvedByDigest);
        using var evidence = JsonDocument.Parse(units.Evidence);
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitlessMetres, evidence.RootElement.GetProperty("decision_kind").GetString());
        Assert.Equal(civil.Status, evidence.RootElement.GetProperty("civil_drawing_unit_status").GetString());
    }

    [Fact]
    public void ADifferentCivilReadingAfterTheDecisionReopensTheReview()
    {
        var profile = WithDecision(Reviewed(Inches));
        Assert.True(Resolve(profile, Inches).IsSupported);
        foreach (var changed in new[] { Meters, Unreadable, None, new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 2) })
        {
            var units = Resolve(profile, changed);
            Assert.False(units.IsSupported);
            Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, units.FailureCode);
            Assert.True(units.NeedsReview);
        }
        var unreadable = WithDecision(Reviewed(Unreadable));
        Assert.True(Resolve(unreadable, Unreadable).IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, Resolve(unreadable, Meters).FailureCode);
    }

    [Fact]
    public void ANewDecisionWithoutItsReviewRecordIsRefusedEverywhere()
    {
        var profile = NewProfile();
        profile.DrawingUnitDeclarations.Add(Reviewed(Meters));
        Assert.Equal(PhysicalDrawingUnitPolicy.DecisionUnlinked, Resolve(profile, Meters).FailureCode);
        Assert.Throws<ArgumentException>(() =>
            ProjectProfileWriter.Save(profile, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(profile)));
        Assert.False(File.Exists(ProfilePath));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AnExistingKindlessDeclarationStaysAsApprovedWhenCivilDoesNotContradictIt(int civilCase)
    {
        var civil = new[] { Meters, Unreadable, None }[civilCase];
        var legacy = Legacy();
        var digest = PhysicalDrawingUnitPolicy.DigestOf(legacy);
        var profile = WithDecision(legacy);
        Assert.Null(profile.DrawingUnitReviews);
        var units = Resolve(profile, civil);
        Assert.True(units.IsSupported);
        Assert.Null(units.DecisionKind);
        Assert.Equal("approved-unitless-host-declaration", units.Authority);
        Assert.Equal(digest, units.DeclarationDigest);
        // Its bytes stay those of the earlier builds: no kind, no Civil field, no schema above what it had.
        ProjectProfileWriter.Save(profile, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(profile));
        var text = File.ReadAllText(ProfilePath);
        Assert.DoesNotContain("decision_kind", text);
        Assert.DoesNotContain("civil_drawing_unit", text);
        Assert.True(Reload().SchemaVersion < ProjectProfileSchemaPolicy.Schema7);
        Assert.Equal(digest, PhysicalDrawingUnitPolicy.DigestOf(Reload().DrawingUnitDeclarations.Single()));
    }

    [Fact]
    public void AnExistingKindlessDeclarationThatCivilContradictsIsStoppedWithoutChangingIt()
    {
        var legacy = Legacy();
        var before = JsonSerializer.Serialize(legacy);
        var profile = WithDecision(legacy);
        var units = Resolve(profile, Inches);
        Assert.False(units.IsSupported);
        Assert.Null(units.EffectiveUnitCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded, units.FailureCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewConflict, units.ReviewKind);
        Assert.True(units.NeedsReview);
        Assert.Contains("Civil", units.Suspicion);
        Assert.Equal(before, JsonSerializer.Serialize(profile.DrawingUnitDeclarations.Single()));
        // An XREF never inherits either the declaration or the conflict: its own unit only.
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitUnknown, Resolve(profile, Inches, host: false).FailureCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitUnknown, Resolve(WithDecision(Reviewed(Meters)), Meters, host: false).FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AConflictFoundAfterTheReviewWasClosedIsRecordedAgainAndOutlivesAnUnreadableCivil(bool hadClosedRecord)
    {
        // A kind-less metres declaration, optionally with an earlier review of the drawing closed by its digest.
        var legacy = Legacy();
        var profile = WithDecision(legacy);
        if (hadClosedRecord)
        {
            profile.DrawingUnitReviews = new() { new ProjectProfile.DrawingUnitReview
            {
                DrawingFingerprint = Fingerprint, DrawingPath = PhysicalDrawingUnitPolicy.CanonicalDrawingPath(Host),
                ObservedInsunitsCode = 0, Kind = PhysicalDrawingUnitPolicy.ReviewIdentity, Reason = "SYNTHETIC earlier review",
                Evidence = "{}", ObservedAtUtc = At, RecordedBy = PhysicalDrawingUnitPolicy.TechnicalRecorder,
            } };
            PhysicalDrawingUnitPolicy.LinkReviews(profile, legacy, At);
            Assert.Equal(PhysicalDrawingUnitPolicy.DigestOf(legacy), profile.DrawingUnitReviews.Single().ResolvedByDigest);
        }
        ProjectProfileWriter.Save(profile, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(profile));
        var saved = Reload();
        Assert.True(Resolve(saved, Unreadable).IsSupported);

        // Civil now reads inches: a conflict, written once as a technical record (CAS, no approval, declaration untouched).
        var conflict = Resolve(saved, Inches);
        var record = PhysicalDrawingUnitPolicy.NewReviewRecord(conflict, Fingerprint, Host, saved.DrawingUnitReviews, At);
        Assert.NotNull(record);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewConflict, record!.Kind);
        Assert.Null(record.ResolvedByDigest);
        (saved.DrawingUnitReviews ??= new()).Add(record);
        var approvedBy = saved.Provenance.ApprovedBy;
        var declarationBytes = JsonSerializer.Serialize(saved.DrawingUnitDeclarations.Single());
        ProjectProfileWriter.SaveTechnicalRecord(saved, ProfilePath, "technical: unit review pending (conflict)", Expected(saved));
        var recorded = Reload();
        Assert.Equal(hadClosedRecord ? 2 : 1, recorded.DrawingUnitReviews!.Count);
        Assert.Contains(hadClosedRecord ? "schema_version: 8" : "schema_version: 7", File.ReadAllText(ProfilePath));
        Assert.Equal(approvedBy, recorded.Provenance.ApprovedBy);
        Assert.Equal(declarationBytes, JsonSerializer.Serialize(recorded.DrawingUnitDeclarations.Single()));
        Assert.True(PhysicalDrawingUnitPolicy.IsRecorded(Resolve(recorded, Inches), Fingerprint, Host, recorded.DrawingUnitReviews));
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(Resolve(recorded, Inches), Fingerprint, Host, recorded.DrawingUnitReviews, At));

        // Civil unreadable (or absent) later: the open record keeps it blocked — never back to SI by the old declaration.
        foreach (var later in new[] { Unreadable, None, Meters })
        {
            var units = Resolve(recorded, later);
            Assert.False(units.IsSupported);
            Assert.True(units.NeedsReview);
        }

        // Only a new valid decision closes it: every record of the drawing then names the new digest.
        var decision = Reviewed(Inches);
        recorded.DrawingUnitDeclarations.Clear();
        recorded.DrawingUnitDeclarations.Add(decision);
        PhysicalDrawingUnitPolicy.LinkReviews(recorded, decision, At);
        ProjectProfileWriter.Save(recorded, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(recorded));
        var decided = Reload();
        var resolved = Resolve(decided, Inches);
        Assert.True(resolved.IsSupported);
        Assert.All(decided.DrawingUnitReviews!, r => Assert.Equal(resolved.DeclarationDigest, r.ResolvedByDigest));
    }

    // ---- Codex 16:10 P2-2: the same persistence for a Civil-bound decision, not only for the kind-less form ----

    [Theory]
    [InlineData(2, 1)]   // decided against unreadable Civil, Civil now reads inches
    [InlineData(0, 1)]   // decided against Civil metres, now inches
    [InlineData(0, 2)]   // decided against Civil metres, now feet
    public void ANewObservedContradictionOfADecisionIsRecordedAndOutlivesEveryLaterReading(int boundCase, int nowCode)
    {
        var bound = new[] { Meters, Inches, Unreadable }[boundCase];
        var now = new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, nowCode);
        var decision = Reviewed(bound);
        var profile = WithDecision(decision);
        var foreign = PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration("6f1d2c3b-4a59-4e8d-9c7b-1a2b3c4d5e6f",
            @"C:\UnitTests\Unitless\Other.dwg", 0, "SYNTHETIC OTHER", At, "SYNTHETIC other", "SYNTHETIC OTHER SOURCE");
        profile.DrawingUnitDeclarations.Add(foreign);
        ProjectProfileWriter.Save(profile, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(profile));
        var saved = Reload();
        Assert.True(Resolve(saved, bound).IsSupported);
        var declarations = JsonSerializer.Serialize(saved.DrawingUnitDeclarations);
        var digest = PhysicalDrawingUnitPolicy.DigestOf(saved.DrawingUnitDeclarations[0]);

        // A new observed unit that contradicts metres: blocked, and recorded once as a technical conflict.
        var changed = Resolve(saved, now);
        Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, changed.FailureCode);
        Assert.False(changed.Transient);
        var record = PhysicalDrawingUnitPolicy.NewReviewRecord(changed, Fingerprint, Host, saved.DrawingUnitReviews, At);
        Assert.NotNull(record);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewConflict, record!.Kind);
        saved.DrawingUnitReviews!.Add(record);
        ProjectProfileWriter.SaveTechnicalRecord(saved, ProfilePath, "technical: unit review pending (conflict)", Expected(saved));
        var recorded = Reload();
        Assert.Contains("schema_version: 8", File.ReadAllText(ProfilePath));
        Assert.Equal(declarations, JsonSerializer.Serialize(recorded.DrawingUnitDeclarations));   // decision, approver, foreign kept
        Assert.Equal(digest, PhysicalDrawingUnitPolicy.DigestOf(recorded.DrawingUnitDeclarations[0]));
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(Resolve(recorded, now), Fingerprint, Host, recorded.DrawingUnitReviews, At));

        // Unreadable, absent, or back to the evidence it was decided against: still blocked until a new decision.
        foreach (var later in new[] { Unreadable, None, bound, now })
        {
            var units = Resolve(recorded, later);
            Assert.False(units.IsSupported);
            Assert.True(units.NeedsReview);
        }

        // A new authorised decision against what Civil reads now closes every record of the drawing.
        var redecided = Reviewed(now);
        recorded.DrawingUnitDeclarations.RemoveAll(d => PhysicalDrawingUnitPolicy.MatchesIdentity(d, Fingerprint, Host));
        recorded.DrawingUnitDeclarations.Add(redecided);
        PhysicalDrawingUnitPolicy.LinkReviews(recorded, redecided, At);
        ProjectProfileWriter.Save(recorded, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(recorded));
        var closed = Reload();
        var resolved = Resolve(closed, now);
        Assert.True(resolved.IsSupported);
        Assert.Equal(2, closed.DrawingUnitReviews!.Count);
        Assert.All(closed.DrawingUnitReviews, r => Assert.Equal(resolved.DeclarationDigest, r.ResolvedByDigest));
        Assert.Contains(closed.DrawingUnitDeclarations, d => JsonSerializer.Serialize(d) == JsonSerializer.Serialize(foreign));
    }

    [Fact]
    public void ALostOrAgreeingReadingBlocksOnlyWhileItLastsAndIsNeverRecorded()
    {
        var metres = WithDecision(Reviewed(Meters));
        var lost = Resolve(metres, Unreadable);
        Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, lost.FailureCode);
        Assert.True(lost.Transient);
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(lost, Fingerprint, Host, metres.DrawingUnitReviews, At));
        Assert.True(Resolve(metres, Meters).IsSupported);

        var unreadable = WithDecision(Reviewed(Unreadable));
        var agreeing = Resolve(unreadable, Meters);   // a new reading that agrees with metres: blocked, not a conflict
        Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, agreeing.FailureCode);
        Assert.True(agreeing.Transient);
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(agreeing, Fingerprint, Host, unreadable.DrawingUnitReviews, At));
        Assert.True(Resolve(unreadable, Unreadable).IsSupported);
    }

    [Fact]
    public void Schema8CountsOneDrawingWhateverTheGuidFormat()
    {
        ProjectProfile.DrawingUnitReview Record(string fingerprint, string? digest) => new()
        {
            DrawingFingerprint = fingerprint, DrawingPath = Host, ObservedInsunitsCode = 0,
            Kind = PhysicalDrawingUnitPolicy.ReviewConflict, Reason = "SYNTHETIC", Evidence = "{}", ObservedAtUtc = At,
            RecordedBy = PhysicalDrawingUnitPolicy.TechnicalRecorder, ResolvedByDigest = digest,
        };
        var profile = NewProfile();
        profile.DrawingUnitReviews = new()
        {
            Record(Guid.Parse(Fingerprint).ToString("N"), new string('a', 64)),
            Record(Guid.Parse(Fingerprint).ToString("D").ToUpperInvariant(), null),
        };
        Assert.Empty(PhysicalDrawingUnitPolicy.ValidateReviews(profile.DrawingUnitReviews));
        Assert.True(ProjectProfileSchemaPolicy.HasSchema8Content(profile));
    }

    [Fact]
    public void OnlyANewConflictIsRecordedAgainAfterAClosedReview()
    {
        // Everything else keeps the b24 rule: one record per observation, identical evidence writes nothing.
        var decision = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Host, 2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2,
            "SYNTHETIC TEST REVIEWER", At, "SYNTHETIC: real feet", "SYNTHETIC SOURCE", None);
        var profile = WithDecision(decision);
        var closed = profile.DrawingUnitReviews!;
        profile.DrawingUnitDeclarations.Clear();   // revoked: the closed record still blocks the raw unit
        var suspicion = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Host, profile.DrawingUnitDeclarations,
            hostExtents: new(195_496, 628_759, 200_124, 630_468), reviews: closed);
        Assert.True(suspicion.NeedsReview);
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(suspicion, Fingerprint, Host, closed, At));
        var civilConflict = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Host, profile.DrawingUnitDeclarations,
            civilUnits: Meters, reviews: closed);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewConflict, civilConflict.ReviewKind);
        Assert.NotNull(PhysicalDrawingUnitPolicy.NewReviewRecord(civilConflict, Fingerprint, Host, closed, At));
        Assert.False(PhysicalDrawingUnitPolicy.IsRecorded(civilConflict, Fingerprint, Host, closed));
        Assert.True(PhysicalDrawingUnitPolicy.IsRecorded(suspicion, Fingerprint, Host, closed));
    }

    [Fact]
    public void ADrawingMayKeepClosedRecordsButNeverTwoOpenOnes()
    {
        ProjectProfile.DrawingUnitReview Record(string? digest) => new()
        {
            DrawingFingerprint = Fingerprint, DrawingPath = PhysicalDrawingUnitPolicy.CanonicalDrawingPath(Host), ObservedInsunitsCode = 0,
            Kind = PhysicalDrawingUnitPolicy.ReviewConflict, Reason = "SYNTHETIC", Evidence = "{}", ObservedAtUtc = At,
            RecordedBy = PhysicalDrawingUnitPolicy.TechnicalRecorder, ResolvedByDigest = digest,
        };
        var digest = new string('a', 64);
        Assert.Empty(PhysicalDrawingUnitPolicy.ValidateReviews(new[] { Record(digest), Record(null) }));
        Assert.Empty(PhysicalDrawingUnitPolicy.ValidateReviews(new[] { Record(digest), Record(digest) }));
        Assert.NotEmpty(PhysicalDrawingUnitPolicy.ValidateReviews(new[] { Record(null), Record(null) }));
    }

    [Fact]
    public void Schema8IsWrittenOnlyForItsContentAndAnOlderVersionRefusesIt()
    {
        var plain = NewProfile();
        Assert.False(ProjectProfileSchemaPolicy.HasSchema8Content(plain));
        Assert.False(ProjectProfileSchemaPolicy.HasSchema8Content(WithDecision(Legacy())));
        var reviewed = WithDecision(Reviewed(Meters));
        Assert.True(ProjectProfileSchemaPolicy.HasSchema8Content(reviewed));
        ProjectProfileWriter.Save(reviewed, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", Expected(reviewed));
        var text = File.ReadAllText(ProfilePath);
        Assert.Contains("schema_version: 8", text);
        var downgraded = ProjectProfileLoader.LoadFromText(text.Replace("schema_version: 8", "schema_version: 7"));
        Assert.False(downgraded.IsUsable);
        Assert.Contains(downgraded.Findings, f => f.Code == "SHR-PROFILE-SCHEMA8-CONTENT");
        Assert.True(ProjectProfileSchemaPolicy.CurrentSchemaVersion >= 8);
    }
}
