using System;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// b26 (LA-40 live, 02.10 17:39; contract Codex 17:57): Civil 2027 names only Feet=30 and Meters=2 in
/// Autodesk.Civil.Settings.DrawingUnitType, so LA-40's setting read as "unreadable". A value the API does not name is now
/// an unmapped observed reading: it proves no unit, it is never "agrees", and it needs an explicit decision in every raw
/// unit — after which the decided standard factor is used. Synthetic only.
/// </summary>
public sealed class PhysicalDrawingUnitCivilApiValueTests : IDisposable
{
    private const string Fingerprint = "8a1b2c3d-4e5f-4a6b-9c7d-0e1f2a3b4c5d";
    private const string Host = @"C:\UnitTests\CivilApi\Host.dwg";
    private static readonly DateTime At = new(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Other6 = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(6, "6");
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Other3 = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(3, "3");
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Other4 = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(4, "4");
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence Unreadable = PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("x");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhd-civil-api-" + Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_dir, "project-profile.yaml");

    public PhysicalDrawingUnitCivilApiValueTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProjectProfile WithDecision(ProjectProfile.DrawingUnitDeclaration decision)
    {
        var profile = new ProjectProfile { ProfileId = "civil-api-test", Sections = { Cl = { SourceFiles = { "CL.dwg" }, IntersectionToleranceM = 0.01 } } };
        profile.DrawingUnitDeclarations.Add(decision);
        if (decision.DecisionKind != null) PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, At);
        return profile;
    }

    private static PhysicalDrawingUnitPolicy.Resolution Resolve(ProjectProfile profile, int raw, PhysicalDrawingUnitPolicy.CivilUnitEvidence civil) =>
        PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, Host, profile.DrawingUnitDeclarations, civilUnits: civil, reviews: profile.DrawingUnitReviews);

    private static ProjectProfile.DrawingUnitDeclaration UnitlessMetres(PhysicalDrawingUnitPolicy.CivilUnitEvidence civil) =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Host, 0, PhysicalDrawingUnitPolicy.UnitlessMetres, 6,
            "SYNTHETIC TEST REVIEWER", At, "SYNTHETIC: drawn in metres", "SYNTHETIC SOURCE", civil);

    private static ProjectProfile.DrawingUnitDeclaration Confirm(int raw, PhysicalDrawingUnitPolicy.CivilUnitEvidence civil) =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Host, raw, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, raw,
            "SYNTHETIC TEST REVIEWER", At, "SYNTHETIC: the recorded unit is right", "SYNTHETIC SOURCE", civil);

    [Fact]
    public void TheApiValueGivesMetresFeetOrAnUnmappedReadingNeverAGuess()
    {
        Assert.Equal(new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 6),
            PhysicalDrawingUnitPolicy.CivilUnitsFromApi(2, "Meters"));
        Assert.Equal(new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 2),
            PhysicalDrawingUnitPolicy.CivilUnitsFromApi(30, "Feet"));
        Assert.Equal(PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther, Other6.Status);
        Assert.Equal(6, Other6.UnitCode);
        Assert.Equal("DrawingUnitType=6", Other6.Detail);
        Assert.True(Other6.IsValid);
        Assert.False(new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther, 2).IsValid);
        Assert.False(new PhysicalDrawingUnitPolicy.CivilUnitEvidence(PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther, null).IsValid);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.3048)]
    [InlineData(0.001)]
    [InlineData(0.0254)]
    public void AnUnmappedReadingNeverAgreesWithAnyUnit(double metresPerUnit)
    {
        Assert.Equal(PhysicalDrawingUnitPolicy.CivilComparison.Unmapped, PhysicalDrawingUnitPolicy.CompareCivil(Other6, metresPerUnit));
        Assert.True(PhysicalDrawingUnitPolicy.NeedsCivilReview(Other6, metresPerUnit));
        Assert.Equal(PhysicalDrawingUnitPolicy.CivilComparison.NoEvidence, PhysicalDrawingUnitPolicy.CompareCivil(Unreadable, metresPerUnit));
        Assert.Equal(PhysicalDrawingUnitPolicy.CivilComparison.NoEvidence,
            PhysicalDrawingUnitPolicy.CompareCivil(PhysicalDrawingUnitPolicy.CivilUnitEvidence.None, metresPerUnit));
        Assert.False(PhysicalDrawingUnitPolicy.NeedsCivilReview(Unreadable, metresPerUnit));
        var metres = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(2, "Meters");
        Assert.Equal(metresPerUnit == 1.0 ? PhysicalDrawingUnitPolicy.CivilComparison.Agrees : PhysicalDrawingUnitPolicy.CivilComparison.Contradicts,
            PhysicalDrawingUnitPolicy.CompareCivil(metres, metresPerUnit));
    }

    [Fact]
    public void AnExistingMetresDeclarationIsNotPassedSilentlyWhenCivilIsUnmapped()
    {
        var legacy = PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, Host, 0,
            "SYNTHETIC TEST REVIEWER", At, "SYNTHETIC: drawn in metres", "SYNTHETIC SOURCE");
        var units = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Host, new[] { legacy }, civilUnits: Other6);
        Assert.False(units.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded, units.FailureCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewConflict, units.ReviewKind);
        Assert.Contains("אינן מאפשרות לאשר", units.Suspicion);
        Assert.DoesNotContain("סותרות", units.Suspicion);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]   // raw metres + unmapped: never automatic SI
    [InlineData(4)]   // raw millimetres + unmapped: never automatic SI
    public void AnUndeclaredExplicitUnitWithAnUnmappedReadingNeedsAnExplicitDecision(int raw)
    {
        var units = PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, Host, null, civilUnits: Other6);
        Assert.False(units.IsSupported);
        Assert.Null(units.EffectiveUnitCode);
        Assert.True(units.NeedsReview);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewConflict, units.ReviewKind);
        Assert.NotNull(PhysicalDrawingUnitPolicy.NewReviewRecord(units, Fingerprint, Host, null, At));
    }

    [Theory]
    [InlineData(4, 0.001)]
    [InlineData(6, 1.0)]
    public void AnExplicitDecisionAgainstTheUnmappedReadingUsesItsStandardFactor(int raw, double factor)
    {
        // The positive path (Codex 17:57): decision + source + approver + the evidence it was made against → supported.
        var civil = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(raw + 100, (raw + 100).ToString());   // an unmapped value
        Assert.Equal(PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther, civil.Status);
        Assert.False(PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, Host, null, civilUnits: civil).IsSupported);
        var profile = WithDecision(Confirm(raw, civil));
        var units = Resolve(profile, raw, civil);
        Assert.True(units.IsSupported);
        Assert.Equal(factor, units.LinearToMetres);
        Assert.Equal("approved-recorded-unit", units.Authority);
        var unitless = Resolve(WithDecision(UnitlessMetres(Other6)), 0, Other6);
        Assert.True(unitless.IsSupported);
        Assert.Equal(1.0, unitless.LinearToMetres);
    }

    [Theory]
    [InlineData(0, 6)]   // decided metres
    [InlineData(4, 4)]   // decided millimetres
    public void ANewUnmappedValueAfterADecisionIsRecordedAndOutlivesAnUnreadableReading(int raw, int bound)
    {
        var boundEvidence = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(bound, bound.ToString());
        var profile = WithDecision(raw == 0 ? UnitlessMetres(boundEvidence) : Confirm(raw, boundEvidence));
        Assert.True(Resolve(profile, raw, boundEvidence).IsSupported);
        var changed = Resolve(profile, raw, Other3);
        Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, changed.FailureCode);
        Assert.False(changed.Transient, "a new value the API does not name is recorded, whatever unit was decided");
        var record = PhysicalDrawingUnitPolicy.NewReviewRecord(changed, Fingerprint, Host, profile.DrawingUnitReviews, At);
        Assert.NotNull(record);
        profile.DrawingUnitReviews!.Add(record!);
        foreach (var later in new[] { Unreadable, PhysicalDrawingUnitPolicy.CivilUnitEvidence.None, boundEvidence, Other3 })
            Assert.False(Resolve(profile, raw, later).IsSupported);
        var lost = Resolve(WithDecision(raw == 0 ? UnitlessMetres(boundEvidence) : Confirm(raw, boundEvidence)), raw, Unreadable);
        Assert.True(lost.Transient, "a lost reading blocks while it lasts and is not recorded");
        Assert.Null(PhysicalDrawingUnitPolicy.NewReviewRecord(lost, Fingerprint, Host, profile.DrawingUnitReviews, At));
    }

    [Fact]
    public void Schema9IsWrittenForAnUnmappedBindingAndAnOlderVersionRefusesIt()
    {
        var profile = WithDecision(UnitlessMetres(Other6));
        Assert.True(ProjectProfileSchemaPolicy.HasSchema9Content(profile));
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), ProfilePath);
        ProjectProfileWriter.Save(profile, ProfilePath, "physical host units", "SYNTHETIC TEST REVIEWER", expected);
        var text = File.ReadAllText(ProfilePath);
        Assert.Contains("schema_version: 9", text);
        Assert.Contains("civil_drawing_unit_status: observed-other", text);
        var reloaded = ProjectProfileLoader.LoadFromFile(ProfilePath);
        Assert.True(reloaded.IsUsable, string.Join("; ", reloaded.Findings.Select(f => f.Code)));
        Assert.True(Resolve(reloaded.Profile!, 0, Other6).IsSupported);
        Assert.False(Resolve(reloaded.Profile!, 0, Other4).IsSupported, "another unmapped value is another reading");
        var downgraded = ProjectProfileLoader.LoadFromText(text.Replace("schema_version: 9", "schema_version: 8"));
        Assert.False(downgraded.IsUsable);
        Assert.Contains(downgraded.Findings, f => f.Code == "SHR-PROFILE-SCHEMA9-CONTENT");
        Assert.False(ProjectProfileSchemaPolicy.HasSchema9Content(WithDecision(UnitlessMetres(PhysicalDrawingUnitPolicy.CivilUnitEvidence.None))));
    }
}
