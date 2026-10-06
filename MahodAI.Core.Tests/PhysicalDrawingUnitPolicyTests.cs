using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class PhysicalDrawingUnitPolicyTests
{
    private const string Fingerprint = "984aba52-b4e2-4a8e-a6a8-bf4a9b851eb2";
    private const string DrawingPath = @"C:\UnitTests\Host.dwg";
    private static readonly DateTime ApprovedAt = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    private static ProjectProfile.DrawingUnitDeclaration Approved() =>
        PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(
            Fingerprint, DrawingPath, 0, "Engineer", ApprovedAt,
            "Reviewed survey coordinates and project units", "Project CAD specification, section 4.2");

    private static PhysicalDrawingUnitPolicy.Resolution Resolve(
        ProjectProfile.DrawingUnitDeclaration declaration, int raw = 0) =>
        PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, DrawingPath, new[] { declaration }, reviews: Links(declaration));

    // b24 (Codex 13:02 §2): a reviewed explicit-unit decision is valid only with the review record that names its digest;
    // the units review writes both together, as LinkReviews does here.
    private static List<ProjectProfile.DrawingUnitReview> Links(params ProjectProfile.DrawingUnitDeclaration[] decisions)
    {
        var profile = new ProjectProfile();
        foreach (var decision in decisions)
        {
            profile.DrawingUnitDeclarations.Add(decision);
            if (decision.DecisionKind != null) PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, ApprovedAt);
        }
        return profile.DrawingUnitReviews ?? new List<ProjectProfile.DrawingUnitReview>();
    }

    private static PhysicalDrawingUnitPolicy.Resolution ResolveLinked(int raw, string path,
        IEnumerable<ProjectProfile.DrawingUnitDeclaration> decisions, PhysicalDrawingUnitPolicy.HostExtents? extents = null,
        PhysicalDrawingUnitPolicy.CivilUnitEvidence? civil = null)
    {
        var list = decisions.ToArray();
        return PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, path, list, hostExtents: extents, civilUnits: civil, reviews: Links(list));
    }

    [Fact]
    public void DefaultProfileAndDeclaration_DoNotInventPhysicalMetres()
    {
        Assert.Empty(new ProjectProfile().DrawingUnitDeclarations);
        var declaration = new ProjectProfile.DrawingUnitDeclaration();
        Assert.Null(declaration.OriginalInsunitsCode);
        Assert.Null(declaration.PhysicalUnitCode);
        Assert.Null(declaration.MetresPerUnit);
        Assert.False(declaration.Approved);
    }

    [Fact]
    public void UnitlessWithoutProfileOrDeclaration_RemainsBlockedAndSerializable()
    {
        foreach (var declarations in new IEnumerable<ProjectProfile.DrawingUnitDeclaration>?[]
                 { null, Array.Empty<ProjectProfile.DrawingUnitDeclaration>() })
        {
            var resolution = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath, declarations);
            Assert.False(resolution.IsSupported);
            Assert.False(resolution.AllowsMetricSections);
            Assert.Equal(0, resolution.RawUnitCode);
            Assert.Null(resolution.EffectiveUnitCode);
            Assert.Null(resolution.DeclarationDigest);
            Assert.True(double.IsFinite(resolution.LinearToMetres));
            Assert.Equal(PhysicalDrawingUnitPolicy.UnitUnknown, resolution.FailureCode);
            _ = JsonSerializer.Serialize(resolution); // No NaN/Infinity in failure evidence.
        }
    }

    [Fact]
    public void ApprovedUnitlessHost_PreservesRawZeroAndProducesMetricQuantitiesAndEvidence()
    {
        var resolution = Resolve(Approved());
        Assert.True(resolution.IsSupported);
        Assert.True(resolution.AllowsMetricSections);
        Assert.True(resolution.UsedDeclaration);
        Assert.Equal(0, resolution.RawUnitCode);
        Assert.Equal(6, resolution.EffectiveUnitCode);
        Assert.Equal(1.0, resolution.LinearToMetres);
        Assert.Equal(10.0, 10 * resolution.LinearToMetres);
        Assert.Equal(12.0, 12 * Math.Pow(resolution.LinearToMetres, 2));
        Assert.Equal(24.0, 24 * Math.Pow(resolution.LinearToMetres, 3));
        Assert.Matches("^[a-f0-9]{64}$", resolution.DeclarationDigest!);
        using var evidence = JsonDocument.Parse(resolution.Evidence);
        Assert.Equal(0, evidence.RootElement.GetProperty("raw_insunits").GetInt32());
        Assert.Equal(6, evidence.RootElement.GetProperty("effective_unit").GetInt32());
        Assert.Equal(resolution.DeclarationDigest,
            evidence.RootElement.GetProperty("declaration_sha256").GetString());
        Assert.Equal("Engineer", evidence.RootElement.GetProperty("approved_by").GetString());
    }

    [Fact]
    public void ExplicitPhysicalUnitsRetainExistingConversions_OnlyMetresAllowSections()
    {
        for (var code = 1; code <= 24; code++)
        {
            var resolution = PhysicalDrawingUnitPolicy.Resolve(code, null, null, null);
            Assert.True(resolution.IsSupported);
            Assert.Equal(code, resolution.RawUnitCode);
            Assert.Equal(code, resolution.EffectiveUnitCode);
            Assert.True(double.IsFinite(resolution.LinearToMetres));
            Assert.True(resolution.LinearToMetres > 0);
            Assert.Equal(code == 6, resolution.AllowsMetricSections);
            Assert.False(resolution.UsedDeclaration);
            Assert.Null(resolution.DeclarationDigest);
        }
        var mm = PhysicalDrawingUnitPolicy.Resolve(4, null, null, null);
        Assert.Equal(10.0, 10_000 * mm.LinearToMetres, 9);
        Assert.Equal(12.0, 12_000_000 * Math.Pow(mm.LinearToMetres, 2), 9);
        Assert.Equal(24.0, 24_000_000_000 * Math.Pow(mm.LinearToMetres, 3), 9);
        Assert.Equal(0.3048, PhysicalDrawingUnitPolicy.Resolve(2, null, null, null).LinearToMetres);
        Assert.Equal(1200d / 3937d, PhysicalDrawingUnitPolicy.Resolve(21, null, null, null).LinearToMetres);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Drawing1.dwg")]
    [InlineData(@"C:\UnitTests\Template.dwt")]
    public void UnsavedExplicitUnitsIgnoreOnlyProvenUnrelatedDeclarations(string? unsavedPath)
    {
        const string otherGuid = "e15a3a38-c63e-4533-ab3e-acfa8b5b5743";
        var declarations = new[] { Approved() };
        for (var raw = 1; raw <= 24; raw++)
        {
            var expected = PhysicalDrawingUnitPolicy.Resolve(raw, otherGuid, unsavedPath, null);
            var actual = PhysicalDrawingUnitPolicy.Resolve(raw, otherGuid, unsavedPath, declarations);
            Assert.Equal(expected, actual);
            Assert.True(actual.IsSupported);
            Assert.False(actual.UsedDeclaration);
            Assert.Equal(raw == 6, actual.AllowsMetricSections);
            using var evidence = JsonDocument.Parse(actual.Evidence);
            Assert.Equal("explicit-insunits", evidence.RootElement.GetProperty("authority").GetString());
        }
        Assert.Single(declarations); // Resolution never creates or changes approval.
        Assert.Equal(DrawingPath.ToUpperInvariant(), declarations[0].DrawingPath);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void MissingPathCannotHideChangedInsunitsOnTheSameDeclaredGuid(int raw)
    {
        var result = PhysicalDrawingUnitPolicy.Resolve(raw, "{" + Fingerprint.ToUpperInvariant() + "}",
            null, new[] { Approved() });
        Assert.False(result.IsSupported);
        Assert.False(result.UsedDeclaration);
        Assert.Null(result.EffectiveUnitCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.OriginalUnitChanged, result.FailureCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingGuidCannotProveExistingDeclarationsUnrelated(string? fingerprint)
    {
        var result = PhysicalDrawingUnitPolicy.Resolve(6, fingerprint, null, new[] { Approved() });
        Assert.False(result.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.IdentityInvalid, result.FailureCode);
    }

    [Fact]
    public void UnsavedUnitlessAndMalformedDeclarationDoNotGainMetreAuthority()
    {
        const string otherGuid = "e15a3a38-c63e-4533-ab3e-acfa8b5b5743";
        foreach (var guid in new[] { Fingerprint, otherGuid })
        {
            var result = PhysicalDrawingUnitPolicy.Resolve(0, guid, null, new[] { Approved() });
            Assert.False(result.IsSupported);
            Assert.False(result.UsedDeclaration);
            Assert.Null(result.EffectiveUnitCode);
            Assert.Equal(PhysicalDrawingUnitPolicy.IdentityInvalid, result.FailureCode);
        }
        var invalid = Approved();
        invalid.Approved = false;
        var explicitResult = PhysicalDrawingUnitPolicy.Resolve(6, otherGuid, null, new[] { invalid });
        Assert.False(explicitResult.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.DeclarationInvalid, explicitResult.FailureCode);
    }

    [Fact]
    public void AllPolicyFailureFamiliesHaveStructuredUnsupportedEvidence()
    {
        var invalid = Approved();
        invalid.Reason = null;
        var results = new[]
        {
            PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath, null),
            PhysicalDrawingUnitPolicy.Resolve(25, Fingerprint, DrawingPath, null),
            PhysicalDrawingUnitPolicy.Resolve(6, null, null, new[] { Approved() }),
            PhysicalDrawingUnitPolicy.Resolve(6, Fingerprint, null, new[] { Approved() }),
            Resolve(Approved(), 6), Resolve(invalid),
            PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath, new[] { Approved(), Approved() }),
            PhysicalDrawingUnitPolicy.Failure(0, PhysicalDrawingUnitPolicy.DeclarationInvalid, "Invalid list"),
        };
        foreach (var result in results)
        {
            Assert.False(result.IsSupported);
            Assert.False(result.UsedDeclaration);
            Assert.Null(result.EffectiveUnitCode);
            Assert.Null(result.DeclarationDigest);
            Assert.Equal(1.0, result.LinearToMetres);
            using var evidence = JsonDocument.Parse(result.Evidence);
            Assert.Equal("physical-drawing-unit-v1", evidence.RootElement.GetProperty("contract").GetString());
            Assert.Equal(result.RawUnitCode, evidence.RootElement.GetProperty("raw_insunits").GetInt32());
            Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("effective_unit").ValueKind);
            Assert.Equal("unresolved", evidence.RootElement.GetProperty("authority").GetString());
            Assert.Equal(result.FailureCode, evidence.RootElement.GetProperty("failure_code").GetString());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(25)]
    [InlineData(int.MaxValue)]
    public void UnsupportedExplicitCodesRemainBlocked(int code)
    {
        var result = PhysicalDrawingUnitPolicy.Resolve(code, Fingerprint, DrawingPath, null);
        Assert.False(result.IsSupported);
        Assert.Null(result.EffectiveUnitCode);
        _ = JsonSerializer.Serialize(result);
    }

    [Fact]
    public void GuidAndPathAreBothRequired_SaveAsOrDifferentDrawingCannotInheritApproval()
    {
        var declarations = new[] { Approved() };
        var wrongGuid = PhysicalDrawingUnitPolicy.Resolve(0, Guid.NewGuid().ToString(), DrawingPath, declarations);
        var saveAs = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, @"C:\UnitTests\SavedAs.dwg", declarations);
        Assert.False(wrongGuid.IsSupported);
        Assert.False(saveAs.IsSupported);
        Assert.Null(wrongGuid.DeclarationDigest);
        Assert.Null(saveAs.DeclarationDigest);
        Assert.True(PhysicalDrawingUnitPolicy.Resolve(6, Guid.NewGuid().ToString(), DrawingPath, declarations).IsSupported);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void ChangedOriginalInsunitsInvalidatesScopedApproval_EvenIfNowMetres(int raw)
    {
        var result = Resolve(Approved(), raw);
        Assert.False(result.IsSupported);
        Assert.Equal(raw, result.RawUnitCode);
        Assert.Null(result.EffectiveUnitCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.OriginalUnitChanged, result.FailureCode);
    }

    [Fact]
    public void HostDeclarationNeverAuthorizesUnitlessXref_EvenWithIdenticalCopiedGuidAndPath()
    {
        var declarations = new[] { Approved() };
        Assert.False(PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath,
            declarations, isHostDrawing: false).IsSupported);
        var mm = PhysicalDrawingUnitPolicy.Resolve(4, Fingerprint, DrawingPath,
            declarations, isHostDrawing: false);
        Assert.True(mm.IsSupported);
        Assert.False(mm.UsedDeclaration);
        Assert.Equal(0.001, mm.LinearToMetres);
    }

    [Fact]
    public void CanonicalIdentityAndDigestAreStableAcrossGuidFormattingPathSpellingAndCulture()
    {
        var declaration = Approved();
        var original = Resolve(declaration).DeclarationDigest;
        declaration.DrawingFingerprint = "{" + Fingerprint.ToUpperInvariant() + "}";
        declaration.DrawingPath = "c:/unittests/sub/../HOST.dwg";
        declaration.ApprovedBy = " Engineer ";
        declaration.Reason = " " + declaration.Reason + " ";
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(original, Resolve(declaration).DeclarationDigest);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void EveryApprovalChangeInvalidatesDeclarationDigestAndEffectiveProfileHash()
    {
        foreach (var mutate in new Action<ProjectProfile.DrawingUnitDeclaration>[]
                 {
                     d => d.Reason += " revised", d => d.Source += " revised",
                     d => d.ApprovedBy = "Reviewer2", d => d.ApprovedAtUtc = ApprovedAt.AddMinutes(1),
                 })
        {
            var profile = Profile();
            profile.DrawingUnitDeclarations.Add(Approved());
            var digest = Resolve(profile.DrawingUnitDeclarations[0]).DeclarationDigest;
            var effectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile);
            mutate(profile.DrawingUnitDeclarations[0]);
            Assert.NotEqual(digest, Resolve(profile.DrawingUnitDeclarations[0]).DeclarationDigest);
            Assert.NotEqual(effectiveHash, EstimateTraceIdentity.EffectiveProfileHash(profile));
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonMetricOrNonfiniteFactorsCannotTurnUnitlessIntoApprovedSI(double factor)
    {
        var declaration = Approved();
        declaration.MetresPerUnit = factor;
        var result = Resolve(declaration);
        Assert.False(result.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.DeclarationInvalid, result.FailureCode);
        _ = JsonSerializer.Serialize(result);
    }

    [Fact]
    public void MissingOrInvalidApprovalMetadataFailsClosedWithoutExceptions()
    {
        foreach (var mutate in new Action<ProjectProfile.DrawingUnitDeclaration>[]
                 {
                     d => d.DrawingFingerprint = null, d => d.DrawingFingerprint = Guid.Empty.ToString(),
                     d => d.DrawingFingerprint = "not-a-guid", d => d.DrawingPath = "relative.dwg",
                     d => d.OriginalInsunitsCode = null, d => d.OriginalInsunitsCode = 6,
                     d => d.PhysicalUnitCode = null, d => d.PhysicalUnitCode = 4,
                     d => d.MetresPerUnit = null, d => d.Approved = false,
                     d => d.ApprovedBy = " ", d => d.ApprovedAtUtc = null,
                     d => d.ApprovedAtUtc = DateTime.SpecifyKind(ApprovedAt, DateTimeKind.Unspecified),
                     d => d.ApprovedAtUtc = DateTime.SpecifyKind(ApprovedAt, DateTimeKind.Local),
                     d => d.ApprovedAtUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
                     d => d.Reason = " ", d => d.Source = null,
                 })
        {
            var declaration = Approved();
            mutate(declaration);
            Assert.Contains(PhysicalDrawingUnitPolicy.ValidateDeclarations(new[] { declaration }),
                f => f.Code == PhysicalDrawingUnitPolicy.DeclarationInvalid);
            Assert.False(Resolve(declaration).IsSupported);
        }
    }

    [Theory]
    [InlineData("relative.dwg")]
    [InlineData("C:relative.dwg")]
    [InlineData("C:\\temp\\host.dwt")]
    [InlineData("C:\\%PROJECT%\\host.dwg")]
    [InlineData("C:\\temp\\*.dwg")]
    [InlineData("C:\\temp\\host.dwg:other.dwg")]
    [InlineData("\\\\?\\C:\\temp\\host.dwg")]
    [InlineData("file:///C:/temp/host.dwg")]
    public void UnsavedOrAmbiguousPathsAreNotAnAuthority(string path) =>
        Assert.Null(PhysicalDrawingUnitPolicy.CanonicalDrawingPath(path));

    [Fact]
    public void DuplicateDeclarationsAreRejectedEvenWhenIdenticalOrSpelledDifferently()
    {
        var first = Approved();
        var second = Approved();
        second.DrawingPath = "c:/unittests/child/../host.dwg";
        second.DrawingFingerprint = "{" + Fingerprint.ToUpperInvariant() + "}";
        var entries = new[] { first, second };
        Assert.Contains(PhysicalDrawingUnitPolicy.ValidateDeclarations(entries),
            f => f.Code == PhysicalDrawingUnitPolicy.DeclarationDuplicate);
        Assert.False(PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath, entries).IsSupported);
    }

    [Fact]
    public void TwoDifferentHostsCanHaveIndependentApprovals()
    {
        var first = Approved();
        var second = PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(
            Fingerprint, @"C:\UnitTests\Other.dwg", 0, "Other engineer", ApprovedAt, "other reason", "other source");
        Assert.Empty(PhysicalDrawingUnitPolicy.ValidateDeclarations(new[] { first, second }));
        Assert.True(PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath,
            new[] { first, second }).IsSupported);
        Assert.NotEqual(Resolve(first).DeclarationDigest,
            PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, second.DrawingPath,
                new[] { first, second }).DeclarationDigest);
    }

    [Fact]
    public void ExplicitNullDeclarationListAndNullItemsAreControlledProfileFindings()
    {
        var profile = Profile();
        profile.DrawingUnitDeclarations = null!;
        Assert.Contains(ProjectProfileLoader.Validate(profile),
            f => f.Code == PhysicalDrawingUnitPolicy.DeclarationInvalid);
        profile.DrawingUnitDeclarations = new() { null! };
        Assert.Contains(ProjectProfileLoader.Validate(profile),
            f => f.Code == PhysicalDrawingUnitPolicy.DeclarationInvalid);
        Assert.False(PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath,
            profile.DrawingUnitDeclarations).IsSupported);
        var loaded = ProjectProfileLoader.LoadFromText("schema_version: 1\nprofile_id: unit-test\ndrawing_unit_declarations: null\n");
        Assert.False(loaded.IsUsable);
    }

    [Fact]
    public void ApprovalFactoryRejectsNonUnitlessHostAndIncompleteMetadata()
    {
        Assert.Throws<ArgumentException>(() => PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(
            Fingerprint, DrawingPath, 6, "Engineer", ApprovedAt, "reason", "source"));
        Assert.Throws<ArgumentException>(() => PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(
            Fingerprint, DrawingPath, 0, "Engineer", ApprovedAt, "reason", " "));
        Assert.Throws<ArgumentException>(() => PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(
            Fingerprint, DrawingPath, 0, "Engineer", DateTime.SpecifyKind(ApprovedAt, DateTimeKind.Unspecified), "reason", "source"));
    }

    [Fact]
    public void SaveReloadPreservesRawZeroApprovalAndDigest_NoPerSaveDwgHashRequired()
    {
        var profile = Profile();
        profile.DrawingUnitDeclarations.Add(Approved());
        var before = Resolve(profile.DrawingUnitDeclarations[0]);
        var directory = Path.Combine(Path.GetTempPath(), "mhd-unit-declaration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project-profile.yaml");
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
            ProjectProfileWriter.Save(profile, path, "approved unitless host physical metres", "Engineer", expected);
            var loaded = ProjectProfileLoader.LoadFromFile(path);
            Assert.True(loaded.IsUsable, string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Message)));
            var declaration = Assert.Single(loaded.Profile!.DrawingUnitDeclarations);
            Assert.Equal(0, declaration.OriginalInsunitsCode);
            Assert.Equal(DateTimeKind.Utc, declaration.ApprovedAtUtc!.Value.Kind);
            Assert.Equal(before.DeclarationDigest, Resolve(declaration).DeclarationDigest);
            Assert.True(Resolve(declaration).AllowsMetricSections);
            Assert.Contains("original_insunits_code: 0", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void WriterRejectsInvalidUnitDecisionBeforePublishingOrMutatingProvenance()
    {
        var profile = Profile();
        var directory = Path.Combine(Path.GetTempPath(), "mhd-invalid-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project-profile.yaml");
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
            var version = profile.Provenance.Version;
            profile.DrawingUnitDeclarations.Add(Approved());
            profile.DrawingUnitDeclarations[0].MetresPerUnit = double.NaN;
            Assert.Throws<ArgumentException>(() =>
            {
                ProjectProfileWriter.Save(profile, path, "invalid unit declaration", "Engineer", expected);
            });
            Assert.Equal(version, profile.Provenance.Version);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---- b24 (E4, la-038-ew): explicit INSUNITS under review — Codex contract 10:57 / 11:14 ----

    // la-038-ew-qt receipt (header 9524DC82): INSUNITS=2, extents E 195,496–200,124 / N 628,759–630,468.
    private static readonly PhysicalDrawingUnitPolicy.HostExtents ItmLike = new(195_496.09, 628_759.46, 200_124.46, 630_468.81);

    private static ProjectProfile.DrawingUnitDeclaration Reviewed(int raw, string kind, int physical, string reason = "SYNTHETIC: reviewed") =>
        PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, DrawingPath, raw, kind, physical,
            "SYNTHETIC TEST REVIEWER", ApprovedAt, reason, "SYNTHETIC TEST SOURCE");

    [Fact]
    public void CoordinatesInTheItmMetreRangeMakeAnExplicitFeetHostReviewNeeded_NotMetres()
    {
        var resolution = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, DrawingPath,
            Array.Empty<ProjectProfile.DrawingUnitDeclaration>(), hostExtents: ItmLike);
        Assert.False(resolution.IsSupported);
        Assert.False(resolution.AllowsMetricSections);
        Assert.Null(resolution.EffectiveUnitCode);
        Assert.Equal(2, resolution.RawUnitCode);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded, resolution.FailureCode);
        Assert.Equal("review-needed", resolution.Authority);
        Assert.Contains("ITM", resolution.Suspicion);
        Assert.Contains("חשד בלבד", resolution.Suspicion);
        Assert.True(double.IsFinite(resolution.LinearToMetres));
        using var evidence = JsonDocument.Parse(resolution.Evidence);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("effective_unit").ValueKind);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded, evidence.RootElement.GetProperty("failure_code").GetString());
    }

    public static IEnumerable<object?[]> NoSuspicionCases() => new[]
    {
        new object?[] { 6, ItmLike, true },                                                       // recorded metres
        new object?[] { 2, new PhysicalDrawingUnitPolicy.HostExtents(0, 0, 5_000, 5_000), true }, // real local feet
        new object?[] { 2, null, true },                                                          // no extents
        new object?[] { 2, new PhysicalDrawingUnitPolicy.HostExtents(1e20, 1e20, -1e20, -1e20), true }, // empty drawing
        new object?[] { 2, new PhysicalDrawingUnitPolicy.HostExtents(double.NaN, 628_759, 200_124, 630_468), true },
        new object?[] { 2, new PhysicalDrawingUnitPolicy.HostExtents(195_496, 628_759, 900_000, 630_468), true }, // partly outside
        new object?[] { 0, ItmLike, false },                                                      // unitless: unchanged
    };

    [Theory]
    [MemberData(nameof(NoSuspicionCases))]
    public void MissingInvalidOrConsistentExtentsAreNoSuspicionAndNoProof(int raw, PhysicalDrawingUnitPolicy.HostExtents? extents, bool supported)
    {
        var resolution = PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, DrawingPath,
            Array.Empty<ProjectProfile.DrawingUnitDeclaration>(), hostExtents: extents);
        Assert.Equal(supported, resolution.IsSupported);
        Assert.Null(resolution.Suspicion);
        Assert.NotEqual(PhysicalDrawingUnitPolicy.ReviewNeeded, resolution.FailureCode);
        if (supported)
        {
            // Byte-identical to the explicit-unit evidence every existing consumer compares against.
            Assert.Equal(PhysicalDrawingUnitPolicy.Resolve(raw, null, null, null).Evidence, resolution.Evidence);
            Assert.Equal("explicit-insunits", resolution.Authority);
        }
    }

    [Fact]
    public void AnXrefIsNeverSuspectedOrReviewedThroughTheHost()
    {
        var declarations = new[] { Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6) };
        var xref = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, DrawingPath, declarations,
            isHostDrawing: false, hostExtents: ItmLike);
        Assert.True(xref.IsSupported);
        Assert.False(xref.UsedDeclaration);
        Assert.Equal(0.3048, xref.LinearToMetres);
        // A millimetre source under the same GUID/path is scaled once, by its own unit.
        var mm = PhysicalDrawingUnitPolicy.Resolve(4, Fingerprint, DrawingPath, declarations, isHostDrawing: false);
        Assert.Equal(0.001, mm.LinearToMetres);
        Assert.Null(mm.DeclarationDigest);
    }

    [Fact]
    public void TrueFeetWithTheSameExtentsCompletesOnlyByConfirmingTheRecordedUnit()
    {
        var declaration = Reviewed(2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2);
        Assert.Equal(0.3048, declaration.MetresPerUnit);
        var resolution = ResolveLinked(2, DrawingPath, new[] { declaration }, ItmLike);
        Assert.True(resolution.IsSupported);
        Assert.True(resolution.UsedDeclaration);
        Assert.Equal(2, resolution.EffectiveUnitCode);
        Assert.Equal(0.3048, resolution.LinearToMetres);
        Assert.False(resolution.AllowsMetricSections, "feet are not a section unit, even when confirmed");
        Assert.Equal("approved-recorded-unit", resolution.Authority);
        Assert.Null(resolution.Suspicion);
        using var evidence = JsonDocument.Parse(resolution.Evidence);
        Assert.Equal("approved-recorded-unit", evidence.RootElement.GetProperty("authority").GetString());
        Assert.Equal(PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, evidence.RootElement.GetProperty("decision_kind").GetString());
        Assert.Equal(resolution.DeclarationDigest, evidence.RootElement.GetProperty("declaration_sha256").GetString());
    }

    [Fact]
    public void MetresRecordedByEvidenceOnAnExplicitFeetHostResolveAtFactorOne()
    {
        var resolution = ResolveLinked(2, DrawingPath,
            new[] { Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6) }, ItmLike);
        Assert.True(resolution.IsSupported);
        Assert.True(resolution.AllowsMetricSections);
        Assert.Equal(2, resolution.RawUnitCode);
        Assert.Equal(6, resolution.EffectiveUnitCode);
        Assert.Equal(1.0, resolution.LinearToMetres);
        Assert.Equal("approved-different-unit", resolution.Authority);
        Assert.Matches("^[a-f0-9]{64}$", resolution.DeclarationDigest!);
    }

    [Fact]
    public void TheUnitlessDeclarationKeepsItsDigestEvidenceYamlAndJson()
    {
        var declaration = Approved();
        Assert.Null(declaration.DecisionKind);
        var frozenDigest = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
        {
            contract = "physical-drawing-unit-v1",
            drawing_fingerprint = Fingerprint,
            drawing_path = PhysicalDrawingUnitPolicy.CanonicalDrawingPath(DrawingPath),
            original_insunits = (int?)0,
            physical_unit = (int?)6, metres_per_unit = (double?)1.0,
            approved = true, approved_by = "Engineer",
            approved_at_utc = ApprovedAt.ToString("O", CultureInfo.InvariantCulture),
            reason = "Reviewed survey coordinates and project units", source = "Project CAD specification, section 4.2",
        }));
        var resolution = Resolve(declaration);
        Assert.Equal(frozenDigest, resolution.DeclarationDigest);
        Assert.Equal("approved-unitless-host-declaration", resolution.Authority);
        Assert.DoesNotContain("decision_kind", resolution.Evidence);
        // The scan's unit-configuration hash serializes the list; an unchanged declaration must not change it.
        Assert.DoesNotContain("DecisionKind", JsonSerializer.Serialize(new[] { declaration }));
    }

    [Fact]
    public void TheDecisionKindAndEveryReviewFieldAreBoundIntoTheDigest()
    {
        var confirmFeet = ResolveLinked(2, DrawingPath,
            new[] { Reviewed(2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2) });
        var metres = ResolveLinked(2, DrawingPath,
            new[] { Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6) });
        var otherReason = ResolveLinked(2, DrawingPath,
            new[] { Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6, "SYNTHETIC: another reason") });
        Assert.Equal(3, new[] { confirmFeet.DeclarationDigest, metres.DeclarationDigest, otherReason.DeclarationDigest }
            .Distinct().Count());
    }

    public static IEnumerable<object?[]> InconsistentReviews() => new[]
    {
        new object?[] { 2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 6, null },     // "confirm" another unit
        new object?[] { 2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2, 1.0 },      // nonstandard factor
        new object?[] { 2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 2, null },   // "different" = same
        new object?[] { 2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 3, null },   // miles: not declarable
        new object?[] { 2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6, 0.3048 }, // free factor
        new object?[] { 2, "metres-by-guess", 6, null },                                 // unknown kind
        new object?[] { 0, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 0, null },     // unitless has its own route
        new object?[] { 0, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6, null },
        new object?[] { 2, null, 6, null },                                              // the unitless form on feet
    };

    [Theory]
    [MemberData(nameof(InconsistentReviews))]
    public void InconsistentReviewedUnitsAreInvalidAndGrantNothing(int raw, string? kind, int physical, double? factor)
    {
        var declaration = new ProjectProfile.DrawingUnitDeclaration
        {
            DrawingFingerprint = Fingerprint, DrawingPath = DrawingPath, OriginalInsunitsCode = raw,
            PhysicalUnitCode = physical, DecisionKind = kind,
            MetresPerUnit = factor ?? PhysicalDrawingUnitPolicy.StandardMetresPerUnit(physical) ?? 1.0,
            Approved = true, ApprovedBy = "SYNTHETIC", ApprovedAtUtc = ApprovedAt, Reason = "r", Source = "s",
        };
        Assert.Contains(PhysicalDrawingUnitPolicy.ValidateDeclarations(new[] { declaration }),
            f => f.Code == PhysicalDrawingUnitPolicy.DeclarationInvalid);
        var resolution = PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, DrawingPath, new[] { declaration });
        Assert.False(resolution.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.DeclarationInvalid, resolution.FailureCode);
        if (factor == null && kind != "metres-by-guess")
            Assert.Throws<ArgumentException>(() => PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(
                Fingerprint, DrawingPath, raw, kind, physical, "SYNTHETIC", ApprovedAt, "r", "s"));
    }

    [Fact]
    public void ChangedRawOrSaveAsNeverInheritsAReviewedExplicitUnit()
    {
        var declarations = new[] { Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6) };
        var changedRaw = PhysicalDrawingUnitPolicy.Resolve(4, Fingerprint, DrawingPath, declarations, hostExtents: ItmLike);
        Assert.False(changedRaw.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.OriginalUnitChanged, changedRaw.FailureCode);
        var savedAs = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, @"C:\UnitTests\Copy of Host.dwg", declarations, hostExtents: ItmLike);
        Assert.False(savedAs.UsedDeclaration);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded, savedAs.FailureCode);
    }

    [Fact]
    public void EveryDeclarableUnitUsesTheResolversOwnStandardFactor()
    {
        foreach (var code in PhysicalDrawingUnitPolicy.DeclarableUnits)
            Assert.Equal(PhysicalDrawingUnitPolicy.Resolve(code, null, null, null).LinearToMetres,
                PhysicalDrawingUnitPolicy.StandardMetresPerUnit(code));
        Assert.Contains(PhysicalDrawingUnitPolicy.Metres, PhysicalDrawingUnitPolicy.DeclarableUnits);
        Assert.Null(PhysicalDrawingUnitPolicy.StandardMetresPerUnit(0));
        Assert.Null(PhysicalDrawingUnitPolicy.StandardMetresPerUnit(25));
    }

    [Fact]
    public void SaveReloadPreservesAReviewedExplicitUnitDecisionAndDigest()
    {
        var profile = Profile();
        profile.DrawingUnitDeclarations.Add(Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6));
        PhysicalDrawingUnitPolicy.LinkReviews(profile, profile.DrawingUnitDeclarations[0], ApprovedAt);
        var before = Resolve(profile.DrawingUnitDeclarations[0], raw: 2);
        var directory = Path.Combine(Path.GetTempPath(), "mhd-reviewed-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project-profile.yaml");
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
            ProjectProfileWriter.Save(profile, path, "reviewed explicit host unit", "SYNTHETIC TEST REVIEWER", expected);
            var loaded = ProjectProfileLoader.LoadFromFile(path);
            Assert.True(loaded.IsUsable, string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Message)));
            var declaration = Assert.Single(loaded.Profile!.DrawingUnitDeclarations);
            Assert.Equal(PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, declaration.DecisionKind);
            Assert.Equal(2, declaration.OriginalInsunitsCode);
            Assert.Equal(before.DeclarationDigest, Resolve(declaration, raw: 2).DeclarationDigest);
            Assert.True(Resolve(declaration, raw: 2).AllowsMetricSections);
            Assert.Contains("decision_kind: different-physical-unit", File.ReadAllText(path));
            // Schema 7: a schema-6 reader (b23) refuses the file instead of ignoring the review (Codex 11:18).
            Assert.Equal(ProjectProfileSchemaPolicy.Schema7, loaded.Profile.SchemaVersion);
            Assert.Contains("schema_version: 7", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AReviewedUnitDecisionUnderAnOlderSchemaIsRefusedNotIgnored()
    {
        var profile = Profile();
        profile.DrawingUnitDeclarations.Add(Reviewed(2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2));
        PhysicalDrawingUnitPolicy.LinkReviews(profile, profile.DrawingUnitDeclarations[0], ApprovedAt);
        var directory = Path.Combine(Path.GetTempPath(), "mhd-reviewed-unit-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project-profile.yaml");
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
            ProjectProfileWriter.Save(profile, path, "reviewed explicit host unit", "SYNTHETIC TEST REVIEWER", expected);
            var downgraded = File.ReadAllText(path).Replace("schema_version: 7", "schema_version: 6");
            var loaded = ProjectProfileLoader.LoadFromText(downgraded);
            Assert.False(loaded.IsUsable);
            Assert.Contains(loaded.Findings, f => f.Code == "SHR-PROFILE-SCHEMA7-CONTENT" && f.Severity == FindingSeverity.Error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TheUnitlessDeclarationYamlHasNoDecisionKind()
    {
        var profile = Profile();
        profile.DrawingUnitDeclarations.Add(Approved());
        var directory = Path.Combine(Path.GetTempPath(), "mhd-unitless-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project-profile.yaml");
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
            ProjectProfileWriter.Save(profile, path, "approved unitless host physical metres", "Engineer", expected);
            Assert.DoesNotContain("decision_kind", File.ReadAllText(path));
            Assert.True(profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema7, "a unitless declaration alone keeps its schema");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---- b24 (Codex 11:48): a decision on a related identity never gives way silently to the recorded unit ----

    public static IEnumerable<object?[]> SaveAsAfterAChangedUnit() => new[]
    {
        new object?[] { 6, 2, null },                                                        // metres recorded, feet decided
        new object?[] { 6, 2, new PhysicalDrawingUnitPolicy.HostExtents(0, 0, 500, 500) },
        new object?[] { 2, 6, null },                                                        // feet recorded, metres decided
        new object?[] { 2, 6, new PhysicalDrawingUnitPolicy.HostExtents(0, 0, 500, 500) },
    };

    [Theory]
    [MemberData(nameof(SaveAsAfterAChangedUnit))]
    public void AfterSaveAsADecisionThatChangedTheUnitNeedsANewReview(int raw, int decided, PhysicalDrawingUnitPolicy.HostExtents? extents)
    {
        var declarations = new[] { Reviewed(raw, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, decided) };
        var savedAs = PhysicalDrawingUnitPolicy.Resolve(raw, Fingerprint, @"C:\UnitTests\SavedAs.dwg", declarations, hostExtents: extents);
        Assert.False(savedAs.IsSupported);
        Assert.True(savedAs.NeedsReview);
        Assert.Equal(PhysicalDrawingUnitPolicy.IdentityChanged, savedAs.FailureCode);
        Assert.Null(savedAs.DeclarationDigest);
        Assert.Contains("שרטוט קשור", savedAs.Suspicion);
        // The same for another GUID written at the decided path (a replaced file).
        var replaced = PhysicalDrawingUnitPolicy.Resolve(raw, Guid.NewGuid().ToString(), DrawingPath, declarations, hostExtents: extents);
        Assert.Equal(PhysicalDrawingUnitPolicy.IdentityChanged, replaced.FailureCode);
        // A new decision for the new file is its own authority.
        var newDecision = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, @"C:\UnitTests\SavedAs.dwg", raw,
            PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, raw, "SYNTHETIC TEST REVIEWER", ApprovedAt, "reviewed the copy", "SYNTHETIC SOURCE");
        var reviewed = ResolveLinked(raw, @"C:\UnitTests\SavedAs.dwg", declarations.Append(newDecision), extents);
        Assert.True(reviewed.IsSupported);
        Assert.Equal(raw, reviewed.EffectiveUnitCode);
        Assert.Equal("approved-recorded-unit", reviewed.Authority);
    }

    [Fact]
    public void AnUnrelatedDrawingOrARelatedDecisionThatAgreesKeepsTheOrdinaryRoute()
    {
        var declarations = new[] { Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6) };
        var unrelated = PhysicalDrawingUnitPolicy.Resolve(2, Guid.NewGuid().ToString(), @"C:\UnitTests\Other.dwg", declarations);
        Assert.True(unrelated.IsSupported);
        Assert.Equal("explicit-insunits", unrelated.Authority);
        var agreeing = new[] { Reviewed(2, PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2) };
        var copy = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, @"C:\UnitTests\SavedAs.dwg", agreeing);
        Assert.True(copy.IsSupported);
        Assert.Equal(0.3048, copy.LinearToMetres);
        Assert.False(copy.UsedDeclaration);
        // b23 contract kept: an explicit-metre drawing at the path of a unitless metres declaration stays metres.
        Assert.True(PhysicalDrawingUnitPolicy.Resolve(6, Guid.NewGuid().ToString(), DrawingPath, new[] { Approved() }).IsSupported);
    }

    // ---- b24 (Codex 12:04 D): Civil drawing units are evidence with a status, never a unit on their own ----

    private static PhysicalDrawingUnitPolicy.CivilUnitEvidence Civil(int code) => new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, code);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence CivilUnreadable = PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC");

    [Theory]
    [InlineData("Feet", "observed", 2)]
    [InlineData("Meters", "observed", 6)]
    [InlineData("Millimeters", "observed", 4)]
    [InlineData("Centimeters", "observed", 5)]
    [InlineData("Decimeters", "observed", 14)]
    [InlineData("Inches", "observed", 1)]
    [InlineData("Miles", "unreadable", null)]
    [InlineData(null, "unreadable", null)]
    public void CivilUnitsAreReadByTheirEnumNameNeverByANumber(string? name, string status, int? code)
    {
        var evidence = PhysicalDrawingUnitPolicy.CivilUnitsFromName(name);
        Assert.Equal(status, evidence.Status);
        Assert.Equal(code, evidence.UnitCode);
        Assert.True(evidence.IsValid);
    }

    [Fact]
    public void ACivilUnitThatContradictsInsunitsIsAReviewNotEitherUnit()
    {
        var none = Array.Empty<ProjectProfile.DrawingUnitDeclaration>();
        var conflict = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, DrawingPath, none, civilUnits: Civil(6));
        Assert.False(conflict.IsSupported);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded, conflict.FailureCode);
        Assert.Contains("Civil", conflict.Suspicion);
        Assert.Equal(PhysicalDrawingUnitPolicy.ReviewNeeded,
            PhysicalDrawingUnitPolicy.Resolve(6, Fingerprint, DrawingPath, none, civilUnits: Civil(2)).FailureCode);
        // Agreement, an unreadable or an absent Civil value: the recorded unit, with no claim either way.
        foreach (var civil in new[] { Civil(2), CivilUnreadable, PhysicalDrawingUnitPolicy.CivilUnitEvidence.None })
        {
            var feet = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, DrawingPath, none, civilUnits: civil);
            Assert.True(feet.IsSupported);
            Assert.Equal(0.3048, feet.LinearToMetres);
        }
        // Civil metres alone never make a unitless drawing metres.
        Assert.Equal(PhysicalDrawingUnitPolicy.UnitUnknown,
            PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, DrawingPath, none, civilUnits: Civil(6)).FailureCode);
    }

    public static IEnumerable<object[]> ChangedCivilEvidence() => new[]
    {
        new object[] { "observed feet → unreadable", 2, -1 },
        new object[] { "observed feet → metres", 2, 6 },
        new object[] { "unreadable → observed feet", -1, 2 },
        new object[] { "not applicable → observed feet", 0, 2 },
    };

    [Theory]
    [MemberData(nameof(ChangedCivilEvidence))]
    public void ADecisionReopensWhenTheCivilEvidenceItReliedOnChangesOrIsLost(string _, int atDecision, int now)
    {
        PhysicalDrawingUnitPolicy.CivilUnitEvidence Of(int code) => code switch
        {
            -1 => CivilUnreadable, 0 => PhysicalDrawingUnitPolicy.CivilUnitEvidence.None, _ => Civil(code),
        };
        var decision = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, DrawingPath, 2,
            PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2, "SYNTHETIC TEST REVIEWER", ApprovedAt, "r", "s", Of(atDecision));
        var same = ResolveLinked(2, DrawingPath, new[] { decision }, civil: Of(atDecision));
        Assert.True(same.IsSupported);
        var changed = ResolveLinked(2, DrawingPath, new[] { decision }, civil: Of(now));
        Assert.False(changed.IsSupported);
        Assert.True(changed.NeedsReview);
        Assert.Equal(PhysicalDrawingUnitPolicy.EvidenceChanged, changed.FailureCode);
        Assert.Null(changed.DeclarationDigest);
    }

    [Fact]
    public void TheCivilEvidenceIsPartOfTheDecisionAndItsDigest_TheUnitlessFormCarriesNone()
    {
        var feet = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, DrawingPath, 2,
            PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2, "SYNTHETIC", ApprovedAt, "r", "s", Civil(2));
        var unreadable = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, DrawingPath, 2,
            PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2, "SYNTHETIC", ApprovedAt, "r", "s", CivilUnreadable);
        Assert.Equal("observed", feet.CivilDrawingUnitStatus); Assert.Equal(2, feet.CivilDrawingUnitCode);
        Assert.NotEqual(ResolveLinked(2, DrawingPath, new[] { feet }, civil: Civil(2)).DeclarationDigest,
            ResolveLinked(2, DrawingPath, new[] { unreadable }, civil: CivilUnreadable).DeclarationDigest);
        Assert.Null(Approved().CivilDrawingUnitStatus);
        Assert.Null(Approved().CivilDrawingUnitCode);
        foreach (var broken in new Action<ProjectProfile.DrawingUnitDeclaration>[]
                 {
                     d => d.CivilDrawingUnitStatus = null,
                     d => d.CivilDrawingUnitStatus = "guessed",
                     d => d.CivilDrawingUnitCode = null,
                     d => d.CivilDrawingUnitCode = 25,
                 })
        {
            var copy = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, DrawingPath, 2,
                PhysicalDrawingUnitPolicy.ConfirmRecordedUnit, 2, "SYNTHETIC", ApprovedAt, "r", "s", Civil(2));
            broken(copy);
            Assert.NotEmpty(PhysicalDrawingUnitPolicy.ValidateDeclarations(new[] { copy }));
        }
        var legacyWithCivil = Approved(); legacyWithCivil.CivilDrawingUnitStatus = "observed"; legacyWithCivil.CivilDrawingUnitCode = 6;
        Assert.NotEmpty(PhysicalDrawingUnitPolicy.ValidateDeclarations(new[] { legacyWithCivil }));
    }

    // ---- b24 (Codex 13:02 §2): a reviewed decision without its review record is refused, never completed silently ----

    [Fact]
    public void AReviewedDecisionWithoutItsReviewRecordIsRefusedAtResolveWriterAndLoader()
    {
        var decision = Reviewed(2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6);
        var unlinked = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, DrawingPath, new[] { decision });
        Assert.False(unlinked.IsSupported);
        Assert.True(unlinked.NeedsReview);
        Assert.Equal(PhysicalDrawingUnitPolicy.DecisionUnlinked, unlinked.FailureCode);
        Assert.Contains("הכרע מחדש", unlinked.Suspicion);
        Assert.True(ResolveLinked(2, DrawingPath, new[] { decision }).IsSupported, "LinkReviews makes the same decision valid");

        var profile = Profile();
        profile.DrawingUnitDeclarations.Add(decision);
        var directory = Path.Combine(Path.GetTempPath(), "mhd-unlinked-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project-profile.yaml");
            var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
            var refused = Assert.Throws<ArgumentException>(() =>
                ProjectProfileWriter.Save(profile, path, "unlinked decision", "SYNTHETIC TEST REVIEWER", expected));
            Assert.Contains("review record", refused.Message);
            Assert.False(File.Exists(path));
            // The loader keeps such a file usable (so the units review can repair it) and reports the gap.
            PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, ApprovedAt);
            ProjectProfileWriter.Save(profile, path, "linked decision", "SYNTHETIC TEST REVIEWER", expected);
            var text = File.ReadAllText(path);
            var start = text.IndexOf("drawing_unit_reviews:", StringComparison.Ordinal);
            var broken = text[..start];   // the review list removed — as an edited or partial file would arrive
            var loaded = ProjectProfileLoader.LoadFromText(broken);
            Assert.True(loaded.IsUsable, string.Join("; ", loaded.Findings.Select(f => f.Code)));
            Assert.Contains(loaded.Findings, f => f.Code == PhysicalDrawingUnitPolicy.DecisionUnlinked && f.Severity == FindingSeverity.ReviewRequired);
            Assert.Equal(PhysicalDrawingUnitPolicy.DecisionUnlinked,
                PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, DrawingPath, loaded.Profile!.DrawingUnitDeclarations,
                    reviews: loaded.Profile.DrawingUnitReviews).FailureCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
        // The unitless declaration needs no record, as before.
        Assert.Empty(PhysicalDrawingUnitPolicy.ValidateDecisionLinks(new[] { Approved() }, null));
    }

    private static ProjectProfile Profile() => new()
    {
        ProfileId = "physical-unit-test",
        Sections = { Cl = { SourceFiles = { "CL.dwg" }, IntersectionToleranceM = 0.01 } },
    };
}
