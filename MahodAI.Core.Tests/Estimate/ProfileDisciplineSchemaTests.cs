using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Schema 6 (Codex 01:29, 02/10): estimate.discipline through the real YAML writer and loader. A legacy profile keeps its
/// bytes, schema and roads library; a declared discipline is written as schema 6 and survives reopen; the same content under
/// an older schema or an unknown value is refused; the library follows the declaration and never falls back to roads.
/// </summary>
public sealed class ProfileDisciplineSchemaTests : IDisposable
{
    private const string Approver = "TEST-ONLY engineer";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_schema6_" + Guid.NewGuid().ToString("N")[..8]);

    public ProfileDisciplineSchemaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProjectProfile Profile(string? discipline = null)
    {
        var profile = new ProjectProfile { ProfileId = "SCHEMA6-FIXTURE", ProjectName = "SIMULATION ONLY" };
        profile.Estimate.Discipline = discipline;
        return profile;
    }

    private string Save(ProjectProfile profile)
    {
        var path = Path.Combine(_dir, "profile", "project-profile.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var expected = File.Exists(path)
            ? ProjectProfileWriter.CaptureExpectedState(path, ArtifactHash.Sha256OfText(File.ReadAllText(path)), path)
            : ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        ProjectProfileWriter.Save(profile, path, "schema 6 (synthetic)", Approver, expected);
        return path;
    }

    [Fact]
    public void ALegacyProfileKeepsItsSchemaBytesAndTheRoadsLibrary()
    {
        var profile = Profile();
        var path = Save(profile);
        profile.SchemaVersion.Should().Be(1);
        File.ReadAllText(path).Should().Contain("schema_version: 1").And.NotContain("discipline");
        System.Text.Json.JsonSerializer.Serialize(profile).Should().NotContain("Discipline");
        EngineerBoqLibrary.For(profile).Should().BeSameAs(EngineerBoqLibrary.RoadsV1);
    }

    [Theory]
    [InlineData("landscape")]
    [InlineData("roads")]
    public void ADeclaredDisciplineIsSchema6AndSurvivesReopen(string discipline)
    {
        var profile = Profile(discipline);
        var path = Save(profile);
        profile.SchemaVersion.Should().Be(ProjectProfileSchemaPolicy.Schema6);
        File.ReadAllText(path).Should().Contain("schema_version: 6").And.Contain("discipline: " + discipline);
        var loaded = ProjectProfileLoader.LoadFromFile(path);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code)));
        loaded.Profile!.Estimate.Discipline.Should().Be(discipline);
        EstimateTraceIdentity.EffectiveProfileHash(loaded.Profile).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
        EngineerBoqLibrary.For(loaded.Profile).Should().BeSameAs(discipline == "landscape" ? EngineerBoqLibrary.LandscapeV1 : EngineerBoqLibrary.RoadsV1);
    }

    [Fact]
    public void ADisciplineUnderAnOlderSchemaIsRefused()
    {
        var path = Save(Profile("landscape"));
        var downgraded = File.ReadAllText(path).Replace("schema_version: 6", "schema_version: 5");
        var loaded = ProjectProfileLoader.LoadFromText(downgraded);
        loaded.IsUsable.Should().BeFalse();
        loaded.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-SCHEMA6-CONTENT");
    }

    [Theory]
    [InlineData("Landscape")]
    [InlineData("gardens")]
    [InlineData("")]
    public void AnUnknownDisciplineIsRefusedAndNeverReadAsRoads(string discipline)
    {
        var loaded = ProjectProfileLoader.LoadFromText($"schema_version: 6\nprofile_id: X\nestimate:\n  discipline: '{discipline}'\n");
        loaded.IsUsable.Should().BeFalse();
        loaded.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-DISCIPLINE-UNKNOWN");
        var act = () => EngineerBoqLibrary.For(Profile(discipline));
        act.Should().Throw<InvalidOperationException>();
    }
}
