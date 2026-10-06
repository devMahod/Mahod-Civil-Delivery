using System.IO;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// estimate.family_decisions through the real YAML writer and loader: every field survives, the effective hash of
/// the saved in-memory profile equals that of the reloaded file, the schema gate and the structural checks hold, and
/// staleness is never a loader error. All data is SYNTHETIC.
/// </summary>
public sealed class FamilyDecisionProfileRoundTripTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mhd-family-decisions-" + Guid.NewGuid().ToString("N"));

    public FamilyDecisionProfileRoundTripTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // This directory is created uniquely by this fixture, never a product profile directory.
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static ProjectProfile Profile() => new() { ProfileId = "FAMILY-FIXTURE", ProjectName = "SIMULATION ONLY" };

    private string SaveNew(ProjectProfile profile, string name = "project-profile.yaml")
    {
        var path = Path.Combine(_directory, name);
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
            profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        ProjectProfileWriter.Save(profile, path, "family decisions (synthetic)", FamilyFixtures.Approver, expected);
        return path;
    }

    private static ProjectProfile Load(string path)
    {
        var loaded = ProjectProfileLoader.LoadFromFile(path);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.ConvertAll(f => f.Code + ":" + f.Title + ":" + f.Message)));
        return loaded.Profile!;
    }

    [Fact]
    public void YamlRoundTripPreservesEveryFieldAndTheEffectiveProfileHash()
    {
        var profile = Profile();
        var catalog = FamilyFixtures.Catalog(new string('a', 64));
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, pset: "ASF-5-19-70"));
        var when = new DateTime(2026, 9, 27, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
        var decision = FamilyDecisionPolicy.CreateApproval("road-pavement", new[] { a },
            new[] { EvidenceKeys.Hatch, EvidenceKeys.PsetComponent }, FamilyFixtures.Library, FamilyFixtures.Approver,
            "שורה אחת\r\nושורה שנייה — \"ציטוט\": זיהוי", when);
        // Overrides and item approvals are their own authority: added explicitly, then the content is re-identified.
        decision.ParameterOverrides.Add(new ProjectProfile.EstimateProfile.FamilyParameterOverride
        {
            Key = "FULL_DEPTH_SHARE", Value = 1.0 / 3.0, Reason = "חלק לכל העומק לפי חתך טיפוסי",
            ApprovedBy = FamilyFixtures.Approver, ApprovedAtUtc = when.AddTicks(7),
        });
        decision.ItemApprovals.Add(FamilyDecisionPolicy.CreateItemApproval("TEST.1", catalog, "מ\"ר", FamilyFixtures.Approver, when));
        decision.DecisionId = FamilyDecisionPolicy.DecisionId(decision);
        profile.Estimate.FamilyDecisions.Add(decision);

        var path = SaveNew(profile);

        profile.SchemaVersion.Should().Be(FamilyDecisionPolicy.FamilyDecisionsSchemaVersion);
        File.ReadAllText(path).Should().Contain("schema_version: 2").And.Contain("family_decisions:");
        var reloaded = Load(path);
        reloaded.SchemaVersion.Should().Be(2);
        EstimateTraceIdentity.EffectiveProfileHash(reloaded).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile),
            "a family save must not make the rebased scan look stale");
        JsonSerializer.Serialize(reloaded.Estimate.FamilyDecisions)
            .Should().Be(JsonSerializer.Serialize(profile.Estimate.FamilyDecisions));

        var back = reloaded.Estimate.FamilyDecisions.Should().ContainSingle().Which;
        back.DecisionId.Should().Be(decision.DecisionId).And.Be(FamilyDecisionPolicy.DecisionId(back));
        back.ApprovedAtUtc.Should().Be(when);
        back.ApprovedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        back.Reason.Should().Be("שורה אחת ושורה שנייה — \"ציטוט\": זיהוי");
        back.ParameterOverrides.Should().ContainSingle().Which.Value.Should().Be(1.0 / 3.0);
        back.ParameterOverrides[0].ApprovedAtUtc.Should().Be(when.AddTicks(7));
        back.ItemApprovals.Should().ContainSingle();
        FamilyDecisionPolicy.IsItemApprovalCurrent(back.ItemApprovals[0], catalog).Should().BeTrue();
        back.Selectors.Should().ContainSingle().Which.EvidenceFingerprint.Should().Be(decision.Selectors[0].EvidenceFingerprint);
        FamilyDecisionPolicy.Resolve(reloaded.Estimate.FamilyDecisions, new[] { a }, FamilyFixtures.Library)
            .Single().State.Should().Be(FamilyDecisionState.Applied);
    }

    [Fact]
    public void CodexC_ItemApprovalSurvivesForPOnly_PriceListChangeInvalidatesIt_FamilyReapprovalNeverResurrectsIt()
    {
        var profile = Profile();
        var book = FamilyFixtures.Catalog(new string('a', 64));
        var groupP = FamilyFixtures.Group("P", FamilyFixtures.HatchArea("asdasd23423", 100));
        var groupQ = FamilyFixtures.Group("Q", FamilyFixtures.HatchArea("qwe987", 50, hatch: "AR-CONC"));
        var p = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, groupP);
        p.ItemApprovals.Add(FamilyDecisionPolicy.CreateItemApproval("TEST.1", book, "מ\"ר", FamilyFixtures.Approver, FamilyFixtures.T1));
        p.DecisionId = FamilyDecisionPolicy.DecisionId(p);
        var q = FamilyFixtures.Approve("sidewalk-paving", FamilyFixtures.T1, FamilyFixtures.Hatch, groupQ);
        profile.Estimate.FamilyDecisions.AddRange(new[] { p, q });

        var reloaded = Load(SaveNew(profile));

        var savedP = reloaded.Estimate.FamilyDecisions.Single(d => d.FamilyId == "road-pavement");
        var savedQ = reloaded.Estimate.FamilyDecisions.Single(d => d.FamilyId == "sidewalk-paving");
        savedP.ItemApprovals.Should().ContainSingle();
        FamilyDecisionPolicy.IsItemApprovalCurrent(savedP.ItemApprovals[0], book).Should().BeTrue();
        savedQ.ItemApprovals.Should().BeEmpty("Q's items stay proposals: a family approval never approves items");
        FamilyDecisionPolicy.Resolve(reloaded.Estimate.FamilyDecisions, new[] { groupP, groupQ }, FamilyFixtures.Library)
            .Should().OnlyContain(r => r.State == FamilyDecisionState.Applied);

        var newBook = FamilyFixtures.Catalog(new string('b', 64));
        FamilyDecisionPolicy.IsItemApprovalCurrent(savedP.ItemApprovals[0], newBook).Should().BeFalse();
        FamilyDecisionPolicy.Resolve(reloaded.Estimate.FamilyDecisions, new[] { groupP }, FamilyFixtures.Library)
            .Single().State.Should().Be(FamilyDecisionState.Applied, "a price-list change does not invalidate an unchanged family");

        var reapproved = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T2, FamilyFixtures.Hatch, groupP);
        reapproved.ItemApprovals.Should().BeEmpty();
        var history = FamilyDecisionPolicy.Supersede(reloaded.Estimate.FamilyDecisions, savedP.DecisionId!, reapproved);
        history.Where(d => d.Status == FamilyDecisionPolicy.Active).SelectMany(d => d.ItemApprovals)
            .Should().NotContain(item => FamilyDecisionPolicy.IsItemApprovalCurrent(item, newBook));
        var old = history.Single(d => d.DecisionId == savedP.DecisionId);
        old.Status.Should().Be(FamilyDecisionPolicy.Superseded);
        old.ItemApprovals.Should().ContainSingle("history keeps what was approved");
        FamilyDecisionPolicy.IsItemApprovalCurrent(old.ItemApprovals[0], newBook).Should().BeFalse();
        FamilyDecisionPolicy.Resolve(history, new[] { groupP }, FamilyFixtures.Library).Single().DecisionId
            .Should().Be(reapproved.DecisionId);
    }

    [Fact]
    public void WriterRefusesAnEditedDecisionBeforeAnyMutation()
    {
        var profile = Profile();
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch,
            FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100)));
        decision.Reason = "edited after approval";
        profile.Estimate.FamilyDecisions.Add(decision);
        var path = Path.Combine(_directory, "edited-must-not-exist.yaml");
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(
            profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        var version = profile.Provenance.Version;

        var act = () => ProjectProfileWriter.Save(profile, path, "edited", FamilyFixtures.Approver, expected);

        act.Should().Throw<ArgumentException>().WithMessage($"*{FamilyDecisionPolicy.IdMismatchCode}*");
        File.Exists(path).Should().BeFalse();
        profile.SchemaVersion.Should().Be(1);
        profile.Provenance.Version.Should().Be(version);
    }

    [Fact]
    public void WithoutFamilyDecisionsTheProfileStaysSchema1()
    {
        var profile = Profile();
        var path = SaveNew(profile);

        profile.SchemaVersion.Should().Be(1);
        File.ReadAllText(path).Should().Contain("schema_version: 1");
        var reloaded = Load(path);
        reloaded.Estimate.FamilyDecisions.Should().BeEmpty();
        EstimateTraceIdentity.EffectiveProfileHash(reloaded).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
    }

    [Fact]
    public void LoaderAcceptsSchema2AndRefusesAnUnknownSchema()
    {
        ProjectProfileLoader.LoadFromText("schema_version: 2\nprofile_id: schema-two\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // Schema 3 carries estimate.edition_links (EditionLinkPolicy) and is known to this build.
        ProjectProfileLoader.LoadFromText("schema_version: 3\nprofile_id: schema-three\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // Schema 4 carries a family selector's visual binding (FamilyVisualBindingPolicy) and is known to this build.
        FamilyVisualBindingPolicy.ProfileSchemaVersion.Should().Be(4);
        ProjectProfileLoader.LoadFromText("schema_version: 4\nprofile_id: schema-four\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // The same gate is what makes a schema-1-only build refuse a schema-2 profile instead of erasing its
        // family decisions on the next save.
        // Schema 5 carries price-book column mappings / scoped approvals (ProjectProfileSchemaPolicy) and is known.
        ProjectProfileLoader.LoadFromText("schema_version: 5\nprofile_id: schema-five\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // Schema 6 carries estimate.discipline (roads / landscape) and is known to this build.
        ProjectProfileLoader.LoadFromText("schema_version: 6\nprofile_id: schema-six\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // Schema 7 carries reviewed explicit-INSUNITS unit decisions (b24). The b23 build pinned 7 as refused here — that
        // is the old reader refusing a schema-7 profile instead of ignoring its unit review.
        ProjectProfileLoader.LoadFromText("schema_version: 7\nprofile_id: schema-seven\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // Schema 8 carries Civil-bound unitless decisions and later unit-review records (b25); b24 pinned 8 as refused.
        ProjectProfileLoader.LoadFromText("schema_version: 8\nprofile_id: schema-eight\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // Schema 9 carries a unit decision bound to a Civil reading the API does not name (b26); b25 pinned 9 as refused.
        ProjectProfileLoader.LoadFromText("schema_version: 9\nprofile_id: schema-nine\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        // 1.4.1: schema 10 (Codex, estimate source scope) and 11 (Claude, CL scope / station markers) are known;
        // b34 pinned 10 as refused. The merged build refuses 12.
        ProjectProfileLoader.LoadFromText("schema_version: 10\nprofile_id: schema-ten\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        ProjectProfileLoader.LoadFromText("schema_version: 11\nprofile_id: schema-eleven\n").Findings
            .Should().NotContain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
        var future = ProjectProfileLoader.LoadFromText(
            $"schema_version: {ProjectProfileSchemaPolicy.CurrentSchemaVersion + 1}\nprofile_id: future-schema\n");
        future.IsUsable.Should().BeFalse();
        future.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION" && f.Severity == FindingSeverity.Error);
    }

    [Theory]
    [InlineData("family_decisions", FamilyDecisionPolicy.ListNullCode)]
    [InlineData("ignored_rule_decisions", "EST-IGNORED-RULE-DECISION-LIST-NULL")]
    [InlineData("approved_adjustments", "EST-APPROVED-ADJUSTMENTS-LIST-NULL")]
    [InlineData("project_overrides", "EST-PROJECT-OVERRIDES-LIST-NULL")]
    [InlineData("price_books", "EST-PRICE-BOOKS-LIST-NULL")]
    public void ExplicitNullDecisionListIsAControlledErrorNotACrash(string key, string code)
    {
        var result = ProjectProfileLoader.LoadFromText(
            $"schema_version: 1\nprofile_id: null-lists\nestimate:\n  {key}: null\n");

        result.Profile.Should().NotBeNull();
        result.IsUsable.Should().BeFalse();
        result.Findings.Should().Contain(f => f.Code == code && f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public void StalenessIsNeverALoaderError()
    {
        var profile = Profile();
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        decision.FamilyId = "family-of-another-library";
        decision.RuleVersion = new string('f', 64);
        decision.DecisionId = FamilyDecisionPolicy.DecisionId(decision);
        profile.Estimate.FamilyDecisions.Add(decision);

        var reloaded = Load(SaveNew(profile));

        reloaded.Estimate.FamilyDecisions.Should().ContainSingle();
        var resolved = FamilyDecisionPolicy.Resolve(reloaded.Estimate.FamilyDecisions, new[] { a }, FamilyFixtures.Library).Single();
        resolved.State.Should().Be(FamilyDecisionState.Stale);
        resolved.StaleReason.Should().Be(FamilyDecisionPolicy.StaleLibraryMissing);
    }

    [Fact]
    public void AnIncompleteOrEditedDecisionInTheFileMakesTheProfileUnusable()
    {
        // Written with the writer's own YAML settings but without its validation, as a hand edit would be.
        static string Yaml(ProjectProfile profile) => new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build().Serialize(profile);
        var group = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));

        var withoutApprover = Profile();
        withoutApprover.SchemaVersion = 2;
        var incomplete = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, group);
        incomplete.ApprovedBy = null;
        withoutApprover.Estimate.FamilyDecisions.Add(incomplete);
        var first = ProjectProfileLoader.LoadFromText(Yaml(withoutApprover));
        first.IsUsable.Should().BeFalse();
        first.Findings.Should().Contain(f => f.Code == FamilyDecisionPolicy.IncompleteCode && f.Severity == FindingSeverity.Error);

        var edited = Profile();
        edited.SchemaVersion = 2;
        var widened = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, group);
        widened.Selectors[0].LayerLeaf = "another-layer";
        edited.Estimate.FamilyDecisions.Add(widened);
        var second = ProjectProfileLoader.LoadFromText(Yaml(edited));
        second.IsUsable.Should().BeFalse();
        second.Findings.Should().Contain(f => f.Code == FamilyDecisionPolicy.IdMismatchCode && f.Severity == FindingSeverity.Error);
    }
}
