using System.IO;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Schema 5 (Codex contract E7F6811B) through the real registry, YAML writer and loader: a profile without the new
/// content keeps its exact bytes and schema; an explicit sheet/column choice survives save and reopen and is the only
/// reading used afterwards; another reading of the same bytes never reuses an id; unsupported versions and malformed
/// content are refused. Workbooks are TEST-ONLY synthetic sheets.
/// </summary>
public sealed class PriceBookMappingProfileSchemaTests : IDisposable
{
    private const string Code = "51.01.0250";
    private const string Approver = "TEST-ONLY engineer";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_schema5_" + Guid.NewGuid().ToString("N")[..8]);

    public PriceBookMappingProfileSchemaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProjectProfile Profile() => new() { ProfileId = "SCHEMA5-FIXTURE", ProjectName = "SIMULATION ONLY" };

    /// <summary>Two price columns: D is the previous price (30), E the current one (95).</summary>
    private string TwoPrices(string name = "two-prices.xlsx")
    {
        var book = new MiniXlsx.Workbook { SheetName = "TEST-ONLY" };
        string[][] rows =
        {
            new[] { "קוד", "תיאור", "יחידה", "מחיר קודם", "מחיר" },
            new[] { Code, "TEST-ONLY curb", "מטר", "30", "95" },
        };
        for (var r = 0; r < rows.Length; r++)
        {
            var row = new MiniXlsx.OutRow(r + 1);
            for (var c = 0; c < rows[r].Length; c++) row.Text(((char)('A' + c)).ToString(), rows[r][c], 0);
            book.Rows.Add(row);
        }
        var path = Path.Combine(_dir, "incoming", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MiniXlsx.Write(book, path);
        return path;
    }

    private PriceBookXlsxLoader.ColumnMapping Price(string path, string priceColumn)
    {
        var automatic = PriceBookXlsxLoader.Inspect(path);
        return new PriceBookXlsxLoader.ColumnMapping(automatic.FileHash, "TEST-ONLY", 1, "A", "B", "C", priceColumn);
    }

    private string Save(ProjectProfile profile, string name = "project-profile.yaml")
    {
        var path = Path.Combine(_dir, "profile", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var expected = File.Exists(path)
            ? ProjectProfileWriter.CaptureExpectedState(path, ArtifactHash.Sha256OfText(File.ReadAllText(path)), path)
            : ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        ProjectProfileWriter.Save(profile, path, "schema 5 (synthetic)", Approver, expected);
        return path;
    }

    private static ProjectProfile Reload(string path)
    {
        var loaded = ProjectProfileLoader.LoadFromFile(path);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Message)));
        return loaded.Profile!;
    }

    private string ProfileDir => Path.Combine(_dir, "profile");

    [Fact]
    public void AProfileWithoutMappingsOrScopedApprovalsKeepsItsSchemaAndHasNoNewKeys()
    {
        var profile = Profile();
        var hashBefore = EstimateTraceIdentity.EffectiveProfileHash(profile);
        var path = Save(profile);
        profile.SchemaVersion.Should().Be(1);
        var yaml = File.ReadAllText(path);
        yaml.Should().Contain("schema_version: 1").And.NotContain("mapping").And.NotContain("scoped_catalog_approvals");
        System.Text.Json.JsonSerializer.Serialize(profile).Should().NotContain("\"Mapping\"").And.NotContain("ScopedCatalogApprovals");
        EstimateTraceIdentity.EffectiveProfileHash(Reload(path)).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
        hashBefore.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AnExplicitChoiceIsStoredAsSchema5_SurvivesReopen_AndIsTheReadingUsedForActivationAndLoad()
    {
        var source = TwoPrices();
        var profile = Profile();
        var result = PriceBookRegistry.Register(profile, ProfileDir, source, Approver, makeActive: true,
            expectedInspectionHash: PriceBookXlsxLoader.Inspect(source).FileHash, mapping: Price(source, "E"));
        result.Entry.Mapping!.PriceColumn.Should().Be("E");

        var path = Save(profile);
        profile.SchemaVersion.Should().Be(ProjectProfileSchemaPolicy.Schema5);
        File.ReadAllText(path).Should().Contain("schema_version: 5").And.Contain("price_column: E");

        var reloaded = Reload(path);
        EstimateTraceIdentity.EffectiveProfileHash(reloaded).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
        var entry = reloaded.Estimate.PriceBooks.Single();
        PriceBookRegistry.SameMapping(entry.Mapping, result.Entry.Mapping).Should().BeTrue();
        var stored = Path.Combine(ProfileDir, entry.File!);
        PriceBookXlsxLoader.Load(stored, entry.Id!, PriceBookRegistry.LoaderMapping(entry)!).Prices[Code].Price.Should().Be(95m);
        PriceBookRegistry.MakeActive(reloaded, entry.Id!, ProfileDir);   // activation re-reads with the stored choice
    }

    [Fact]
    public void AnotherReadingOfTheSameBytesNeverReusesAnId()
    {
        var source = TwoPrices();
        var profile = Profile();
        var first = PriceBookRegistry.Register(profile, ProfileDir, source, Approver, mapping: Price(source, "E"));

        var explicitId = () => PriceBookRegistry.Register(profile, ProfileDir, source, Approver, id: first.Entry.Id,
            mapping: Price(source, "D"));
        explicitId.Should().Throw<InvalidOperationException>().WithMessage("*קריאת עמודות אחרת*");
        profile.Estimate.PriceBooks.Should().ContainSingle("the refusal changes nothing");

        var second = PriceBookRegistry.Register(profile, ProfileDir, source, Approver, mapping: Price(source, "D"));
        second.Entry.Id.Should().Be(first.Entry.Id + "-2");
        second.Entry.FileHash.Should().Be(first.Entry.FileHash);
        var again = PriceBookRegistry.Register(profile, ProfileDir, source, Approver, mapping: Price(source, "E"));
        again.Entry.Id.Should().Be(first.Entry.Id, "the same bytes read the same way are the same entry");
        profile.Estimate.PriceBooks.Should().HaveCount(2);
    }

    [Fact]
    public void AMappingThatDoesNotBelongToThePreviewedBytesIsRefusedBeforeAnything()
    {
        var source = TwoPrices();
        var profile = Profile();
        var foreign = new PriceBookXlsxLoader.ColumnMapping(new string('c', 64), "TEST-ONLY", 1, "A", "B", "C", "E");
        var act = () => PriceBookRegistry.Register(profile, ProfileDir, source, Approver,
            expectedInspectionHash: PriceBookXlsxLoader.Inspect(source).FileHash, mapping: foreign);
        act.Should().Throw<ArgumentException>();
        profile.Estimate.PriceBooks.Should().BeEmpty();
        Directory.Exists(ProfileDir).Should().BeFalse("no directory or copy is created before the binding holds");
    }

    [Theory]
    [InlineData(0)]
    // b23 pinned 7 here; schema 7 is known since b24 (reviewed explicit-INSUNITS unit decisions).
    [InlineData(ProjectProfileSchemaPolicy.CurrentSchemaVersion + 1)]
    public void UnsupportedVersionsAreNeitherWrittenNorLoaded(int version)
    {
        var profile = Profile();
        profile.SchemaVersion = version;
        var path = Path.Combine(_dir, "profile", "future.yaml");
        var save = () => ProjectProfileWriter.Save(profile, path, "future", Approver,
            ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path));
        save.Should().Throw<ArgumentException>().WithMessage("*schema_version*");
        File.Exists(path).Should().BeFalse();
        profile.SchemaVersion.Should().Be(version);

        var loaded = ProjectProfileLoader.LoadFromText($"schema_version: {version}\nprofile_id: X\n");
        loaded.IsUsable.Should().BeFalse();
        loaded.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-SCHEMA-VERSION");
    }

    [Fact]
    public void Schema5ContentUnderAnOlderVersionIsRefusedOnLoad()
    {
        var source = TwoPrices();
        var profile = Profile();
        PriceBookRegistry.Register(profile, ProfileDir, source, Approver, mapping: Price(source, "E"));
        var path = Save(profile);
        var downgraded = File.ReadAllText(path).Replace("schema_version: 5", "schema_version: 4");
        var loaded = ProjectProfileLoader.LoadFromText(downgraded);
        loaded.IsUsable.Should().BeFalse();
        loaded.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-SCHEMA5-CONTENT");
    }

    [Fact]
    public void AMalformedMappingIsRefusedBeforeTheProfileIsWritten()
    {
        var source = TwoPrices();
        var profile = Profile();
        PriceBookRegistry.Register(profile, ProfileDir, source, Approver, mapping: Price(source, "E"));
        profile.Estimate.PriceBooks.Single().Mapping!.UnitColumn = "E";   // the same column as the price
        var path = Path.Combine(_dir, "profile", "malformed.yaml");
        var save = () => ProjectProfileWriter.Save(profile, path, "malformed", Approver,
            ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path));
        save.Should().Throw<ArgumentException>().WithMessage("*SHR-PROFILE-PRICEBOOK-MAPPING*");
        File.Exists(path).Should().BeFalse();
        profile.SchemaVersion.Should().Be(1, "a refused save never bumps the schema");
    }

    [Fact]
    public void AnUnnamedNewEditionWithTheSameSuggestedIdGetsTheNextFreeId()
    {
        var first = TwoPrices("book-072026.xlsx");
        var profile = Profile();
        var a = PriceBookRegistry.Register(profile, ProfileDir, first, Approver, mapping: Price(first, "E"));
        // Same suggested id (same name/month), different bytes: a corrected edition published in the same month.
        var second = Path.Combine(_dir, "incoming2", "book-072026.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        var book = new MiniXlsx.Workbook { SheetName = "TEST-ONLY" };
        var header = new MiniXlsx.OutRow(1);
        foreach (var (c, t) in new[] { ("A", "קוד"), ("B", "תיאור"), ("C", "יחידה"), ("D", "מחיר") }) header.Text(c, t, 0);
        var item = new MiniXlsx.OutRow(2);
        foreach (var (c, t) in new[] { ("A", Code), ("B", "TEST-ONLY curb"), ("C", "מטר"), ("D", "99") }) item.Text(c, t, 0);
        book.Rows.Add(header);
        book.Rows.Add(item);
        MiniXlsx.Write(book, second);

        var b = PriceBookRegistry.Register(profile, ProfileDir, second, Approver);
        b.Entry.Id.Should().Be(a.Entry.Id + "-2");
        b.Entry.Mapping.Should().BeNull();
        File.Exists(Path.Combine(ProfileDir, a.Entry.Id + ".xlsx")).Should().BeTrue();
        File.Exists(Path.Combine(ProfileDir, b.Entry.Id + ".xlsx")).Should().BeTrue();
    }

    // TEST-ONLY authority-shaped DTOs; these hashes do not prove a scan, quantity or monetary approval.
    private static ProjectProfile ScopedProfile(DateTime approvedAt)
    {
        var profile = Profile();
        var hash = new string('a', 64);
        profile.Estimate.ScopedCatalogApprovals = new()
        {
            new ProjectProfile.EstimateProfile.ScopedCatalogApproval
            {
                ApprovalId = hash, Status = "active", WholeGroupId = "TEST-ONLY-group", RawRuleKey = "TEST-ONLY-rule",
                PartitionHash = hash, MemberRecordIds = new() { "TEST-ONLY-record" }, MembersHash = hash,
                SpecificationHash = hash, EvidenceHash = hash, SourceScopeHash = hash, Reason = "TEST-ONLY reason",
                ItemApproval = new()
                {
                    CatalogCode = Code, ApprovedCatalogId = "TEST-ONLY-catalog", ApprovedCatalogHash = hash,
                    ApprovedCatalogItemFingerprint = hash, ExpectedUnit = "m", ApprovedBy = Approver,
                    ApprovedAtUtc = approvedAt,
                },
            },
        };
        return profile;
    }

    private static string JsonState(ProjectProfile profile) => System.Text.Json.JsonSerializer.Serialize(profile);

    private static ProjectProfileWriter.ExpectedProfileState GeneratedExpected(ProjectProfile profile, string target) =>
        ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), target);

    [Fact]
    public void AScopedUtcApprovalSurvivesSaveReopenWithTheSameEffectiveHash()
    {
        var time = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        var profile = ScopedProfile(time);
        var path = Save(profile, "scoped-utc.yaml");
        var reloaded = Reload(path);
        profile.SchemaVersion.Should().Be(5);
        var approval = reloaded.Estimate.ScopedCatalogApprovals.Should().ContainSingle().Subject;
        approval.ItemApproval.ApprovedAtUtc.Should().Be(time);
        approval.ItemApproval.ApprovedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        approval.MemberRecordIds.Should().Equal("TEST-ONLY-record");
        EstimateTraceIdentity.EffectiveProfileHash(reloaded).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
    }

    // Deliberately policy-neutral until the owner chooses refusal or transactional normalization.
    // Neither branch permits a successful save whose in-memory hash differs from the reopened file.
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void AScopedNonUtcTimeIsRefusedBeforeMutationOrNormalizedToAStableUtcRoundtrip(DateTimeKind kind)
    {
        var time = new DateTime(2026, 10, 1, 3, 0, 0, kind);
        var profile = ScopedProfile(time);
        var before = JsonState(profile);
        var target = Path.Combine(_dir, "nonutc-" + kind, "profile.yaml");
        var expected = GeneratedExpected(profile, target);
        try
        {
            // Do not use Save helper: it creates a directory before calling the real writer.
            ProjectProfileWriter.Save(profile, target, "TEST-ONLY non-UTC", Approver, expected);
        }
        catch (ArgumentException error)
        {
            error.Message.Should().Contain("SHR-PROFILE-SCOPED-APPROVAL");
            JsonState(profile).Should().Be(before);
            Directory.Exists(Path.GetDirectoryName(target)).Should().BeFalse();
            return;
        }

        var expectedTime = MahodAI.CivilDelivery.Estimate.Recognition.FamilyDecisionPolicy.ToUtc(time);
        var savedTime = profile.Estimate.ScopedCatalogApprovals!.Single().ItemApproval.ApprovedAtUtc!.Value;
        savedTime.Kind.Should().Be(DateTimeKind.Utc);
        savedTime.Should().Be(expectedTime, "normalization must preserve the agreed instant, not relabel Local");
        var reloaded = Reload(target);
        reloaded.Estimate.ScopedCatalogApprovals!.Single().ItemApproval.ApprovedAtUtc.Should().Be(expectedTime);
        EstimateTraceIdentity.EffectiveProfileHash(reloaded).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void AScopedNonUtcTimeLeavesNoMutationWhenSaveIsRefusedOrFails(DateTimeKind kind)
    {
        var profile = ScopedProfile(new DateTime(2026, 10, 1, 3, 0, 0, kind));
        var before = JsonState(profile);
        var blocker = Path.Combine(_dir, "TEST-ONLY-file-" + kind);
        File.WriteAllText(blocker, "TEST-ONLY preserve this file");
        var target = Path.Combine(blocker, "profile.yaml");
        var expected = GeneratedExpected(profile, target);
        Exception? failure = null;
        try { ProjectProfileWriter.Save(profile, target, "TEST-ONLY rollback", Approver, expected); }
        catch (Exception error) { failure = error; }

        failure.Should().NotBeNull();
        (failure is ArgumentException or IOException).Should().BeTrue();
        if (failure is ArgumentException)
            failure.Message.Should().Contain("SHR-PROFILE-SCOPED-APPROVAL");
        JsonState(profile).Should().Be(before, "normalizing before a failing save must not leave an approved-looking change");
        File.ReadAllText(blocker).Should().Be("TEST-ONLY preserve this file");
        File.Exists(target).Should().BeFalse();
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("revoked")]
    public void AScopedHistoryEntryWithoutItsRequiredReferenceOrReasonIsRefusedBeforeMutation(string status)
    {
        var profile = ScopedProfile(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));
        profile.Estimate.ScopedCatalogApprovals!.Single().Status = status;
        var before = JsonState(profile);
        var target = Path.Combine(_dir, "invalid-history-" + status, "profile.yaml");
        var expected = GeneratedExpected(profile, target);
        var save = () => ProjectProfileWriter.Save(profile, target, "TEST-ONLY invalid history", Approver, expected);
        save.Should().Throw<ArgumentException>().WithMessage("*SHR-PROFILE-SCOPED-APPROVAL*");
        JsonState(profile).Should().Be(before);
        Directory.Exists(Path.GetDirectoryName(target)).Should().BeFalse();

        // An independently supplied YAML file is also refused; Save refusal alone is not loader coverage.
        profile.SchemaVersion = 5;
        var yaml = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(YamlDotNet.Serialization.DefaultValuesHandling.Preserve)
            .Build().Serialize(profile);
        var loaded = ProjectProfileLoader.LoadFromText(yaml);
        loaded.IsUsable.Should().BeFalse();
        loaded.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-SCOPED-APPROVAL");
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("revoked")]
    public void AScopedHistoryEntryWithItsRequiredReferenceOrReasonSurvivesSaveReopen(string status)
    {
        var profile = ScopedProfile(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));
        var approval = profile.Estimate.ScopedCatalogApprovals!.Single();
        approval.Status = status;
        if (status == "superseded")
        {
            // Keep the predecessor and a real successor DTO in the same synthetic history.
            var successor = ScopedProfile(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc))
                .Estimate.ScopedCatalogApprovals!.Single();
            successor.ApprovalId = new string('b', 64);
            successor.Reason = "TEST-ONLY replacement";
            approval.SupersededBy = successor.ApprovalId;
            profile.Estimate.ScopedCatalogApprovals!.Add(successor);
        }
        else
            approval.RevokedReason = "TEST-ONLY explicit withdrawal";

        var path = Save(profile, "valid-history-" + status + ".yaml");
        var reloaded = Reload(path);
        var history = reloaded.Estimate.ScopedCatalogApprovals!;
        history.Should().HaveCount(status == "superseded" ? 2 : 1);
        var original = history.Single(a => a.ApprovalId == approval.ApprovalId);
        original.Status.Should().Be(status);
        original.SupersededBy.Should().Be(approval.SupersededBy);
        original.RevokedReason.Should().Be(approval.RevokedReason);
        EstimateTraceIdentity.EffectiveProfileHash(reloaded).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
    }
}
