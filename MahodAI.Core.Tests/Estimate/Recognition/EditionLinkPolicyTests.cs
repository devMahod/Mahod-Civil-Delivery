using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using EditionItemLink = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.EditionItemLink;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// Library recipe codes (NTI urban 08/2025) against the NTI unified 07/2026 list the engineers price with. The fixture
/// holds REAL unified rows (faithful import of the registered XLS, read by the product loader); links and quantities
/// are synthetic. The same number is not the same item, and shared words or a cut description are not an identity.
/// </summary>
public sealed class EditionLinkPolicyTests : IDisposable
{
    private const string Engineer = "SYNTHETIC engineer";
    private static readonly DateTime When = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mhd-edition-links-" + Guid.NewGuid().ToString("N"));

    public EditionLinkPolicyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static CatalogSnapshot Unified([CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "fixtures", "civil-delivery",
            "estimate", "nti-unified-2026-07-candidates.json"));
        var bytes = File.ReadAllBytes(path);
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be("9FB2F12E299DBAA1FEA618174452689C4DC67332BCCB67A52B68D094B2770BB9");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        root.GetProperty("source_sha256").GetString().Should().Be("1797086f48504d3fa3f3aa541a8757e00e5fdad17cc2b3ee7e709110be6dcb2b");
        var catalog = new CatalogSnapshot { SnapshotId = "nti-unified-2026-07", FileHash = root.GetProperty("source_sha256").GetString()! };
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var code = item.GetProperty("code").GetString()!;
            catalog.Items[code] = new CatalogItem
            {
                Code = code, Description = item.GetProperty("description").GetString()!, UnitRaw = item.GetProperty("unit").GetString()!,
            };
            var price = item.GetProperty("price");
            catalog.Prices[code] = new PriceRecord
            {
                Code = code, PriceBookId = catalog.SnapshotId, Price = price.ValueKind == JsonValueKind.Number ? price.GetDecimal() : null,
            };
        }
        return catalog;
    }

    private static IReadOnlyList<string> LibraryCodes =>
        EngineerBoqLibrary.RoadsV1.Rules.SelectMany(r => r.Emits).Select(e => e.Code).Distinct().ToList();

    private static EditionLinkProposal Proposal(IEnumerable<EditionLinkProposal> all, string code) => all.Single(p => p.LibraryCode == code);

    [Fact]
    public void EveryLibraryCodeHasItsReferenceTextFromTheListItWasWrittenIn()
    {
        LibraryCodes.Should().OnlyContain(code => LibraryItemReferences.Items.ContainsKey(code));
        LibraryItemReferences.SourceSha256.Should().Be("90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c");
    }

    [Fact]
    public void TheSameNumberIsNotTheSameItem()
    {
        var catalog = Unified();
        catalog.Items["51.02.0110"].Description.Should().Contain("חפירה", "the unified row with U51.02.0110's number is an excavation");
        var compaction = Proposal(EditionLinkPolicy.Propose(LibraryCodes, catalog, null), "U51.02.0110");
        compaction.PresentInCatalog.Should().BeFalse();
        compaction.Candidates.Should().NotContain(c => c.Code == "51.02.0110");
        compaction.Candidates[0].Should().Match<EditionCandidate>(c => c.Code == "51.02.0041" && c.Kind == EditionMatchKind.ExactText,
            "the same full text with dropped spaces (\"הידוקקרקע\", \"לעומק40\") is the same item");
        catalog.ResolveLibraryCode("U51.02.0110").Should().BeNull("nothing links without an engineer");
    }

    [Theory]
    [InlineData("U51.01.2000", "51.01.1080")]
    [InlineData("U51.03.0010", "51.03.0010")]
    [InlineData("U51.04.0130", "51.04.0130")]
    [InlineData("U51.04.1820", "51.04.1150")]
    [InlineData("U51.04.2310", "51.04.1440")]
    [InlineData("U51.04.2400", "51.04.1460")]
    [InlineData("U51.04.2410", "51.04.1470")]
    [InlineData("U51.04.2530", "51.04.1539")]
    [InlineData("U51.31.0010", "51.31.2002")]
    [InlineData("U51.31.0410", "51.31.2205")]
    [InlineData("U51.32.0210", "51.32.1852")]
    [InlineData("U51.33.2330", "51.33.4008")]
    public void AnExactTextIsTheFirstCandidateButStillOnlyACandidate(string library, string unified)
    {
        var proposal = Proposal(EditionLinkPolicy.Propose(LibraryCodes, Unified(), null), library);
        proposal.Candidates[0].Code.Should().Be(unified);
        proposal.Candidates[0].Kind.Should().Be(EditionMatchKind.ExactText);
        proposal.LinkedCode.Should().BeNull();
    }

    [Theory]
    [InlineData("U51.04.2310", "51.04.1430", "בספרייה: עובי:4 ס\"מ", "במהדורה: עובי:3 ס\"מ")]
    [InlineData("U51.31.0410", "51.31.2202", "בספרייה: קוטר:4 אינץ'", "במהדורה: קוטר:3 אינץ'")]
    [InlineData("U51.33.2330", "51.33.4006", "בספרייה: A2", "במהדורה: A1")]
    [InlineData("U51.31.0010", "51.31.2003", "בספרייה: עד", "במהדורה: מעל")]
    [InlineData("U51.04.2530", "51.04.1530", "בספרייה: עומק:4.1-8.0 ס\"מ", "במהדורה: עומק:2.1-4.0 ס\"מ")]
    [InlineData("U51.03.0010", "51.03.0020", "בספרייה: סוג:א", "במהדורה: סוג:ב")]
    public void ADifferentThicknessDiameterTypeOrQualifierIsAConflictThatNamesTheDifference(
        string library, string unified, string ours, string theirs)
    {
        var candidate = Proposal(EditionLinkPolicy.Propose(LibraryCodes, Unified(), null), library).Candidates.Single(c => c.Code == unified);
        candidate.Kind.Should().Be(EditionMatchKind.ParameterConflict);
        candidate.Differences.Should().Contain(new[] { ours, theirs });
    }

    [Fact]
    public void ANumberKeepsItsUnitAndRoleSoTheSameDigitsInAnotherMeaningDiffer()
    {
        // Codex review 15:55: a bare set of numbers made these look like "consistent parameters".
        CatalogTextIdentity.Parameters("קו ברוחב 10 ס\"מ").Should().NotBeEquivalentTo(CatalogTextIdentity.Parameters("קו ברוחב 10 מ\"מ"));
        CatalogTextIdentity.Parameters("אבן ברוחב 10 ובגובה 20").Should()
            .NotBeEquivalentTo(CatalogTextIdentity.Parameters("אבן ברוחב 20 ובגובה 10"));
        CatalogTextIdentity.Parameters("אבן ברוחב 10 ובגובה 20").Should().BeEquivalentTo("רוחב:10", "גובה:20");
        CatalogTextIdentity.Parameters("בחתך 10/20 ס''מ").Should().NotBeEquivalentTo(CatalogTextIdentity.Parameters("בחתך 20/10 ס\"מ"));
        CatalogTextIdentity.Parameters("בעובי 6 ס''מ").Should().BeEquivalentTo(CatalogTextIdentity.Parameters("בעובי 6 ס\"מ"),
            "quote spelling is not a difference");
    }

    [Fact]
    public void TheRoleSurvivesShelAndABoundBelongsToItsNumber()
    {
        // Codex review 16:2x: "בעובי של", "בגובה של עד", "לעומק של2" lost the role. The texts are the library's own.
        CatalogTextIdentity.Parameters(LibraryItemReferences.Items["U40.02.2430"].Description).Should().Contain("עובי:2.5 מ\"מ");
        var curb = CatalogTextIdentity.Parameters(LibraryItemReferences.Items["U51.06.1900"].Description);
        curb.Should().Contain("גובה:עד 15 ס\"מ").And.Contain("17/25 ס\"מ");
        CatalogTextIdentity.Parameters("חפירה לעומק של2 מ'").Should().BeEquivalentTo("עומק:2 מ'");
        CatalogTextIdentity.Parameters("בגובה של עד 15 ס\"מ").Should().NotBeEquivalentTo(CatalogTextIdentity.Parameters("בגובה של מעל 15 ס\"מ"));
        CatalogTextIdentity.Parameters("בגובה של עד 15 ס\"מ").Should().NotBeEquivalentTo(CatalogTextIdentity.Parameters("בגובה 15 ס\"מ"));
        CatalogTextIdentity.Parameters("בעובי של שכבה 5 ס\"מ").Should().Contain("5 ס\"מ", "a role not followed by its number is not guessed");
    }

    [Fact]
    public void ALinkedSnapshotLosesItsAliasWhenTheLinkIsRevokedOrItsItemChanges()
    {
        var raw = Unified();
        var links = EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.01.2000", "51.01.1080", EditionMatchKind.ExactText) },
            raw, Engineer, "טקסט זהה", When);
        var linked = EditionLinkPolicy.WithLibraryAliases(raw, links, out _);
        linked.ResolveLibraryCode("U51.01.2000").Should().Be("51.01.1080");

        links[0].Status = EditionLinkPolicy.Revoked;
        EditionLinkPolicy.WithLibraryAliases(linked, links, out _).ResolveLibraryCode("U51.01.2000").Should().BeNull();

        links[0].Status = EditionLinkPolicy.Active;
        links[0].CatalogItemFingerprint = new string('f', 64);
        EditionLinkPolicy.WithLibraryAliases(linked, links, out var stale).ResolveLibraryCode("U51.01.2000").Should().BeNull();
        stale.Should().ContainSingle();
    }

    [Fact]
    public void ACutDescriptionIsAnAmbiguityAndACurbWithoutItsProfileHasNoGoodCandidate()
    {
        var proposals = EditionLinkPolicy.Propose(LibraryCodes, Unified(), null);
        var gardenCurb = Proposal(proposals, "U51.06.3060");
        gardenCurb.Candidates.Should().NotContain(c => c.Kind == EditionMatchKind.ExactText);
        gardenCurb.Candidates.Where(c => c.Kind == EditionMatchKind.TruncatedPrefix).Select(c => c.Code)
            .Should().BeEquivalentTo("51.06.1578", "51.06.1582");
        Proposal(proposals, "U51.06.1900").Candidates.Should().OnlyContain(c => c.Kind == EditionMatchKind.ParameterConflict,
            "no 17/25 road curb row exists in the unified list: an engineering choice, not a match");
    }

    [Fact]
    public void CandidatesNeverChangeTheUnitAndAListedLibraryCodeNeedsNoLink()
    {
        var catalog = Unified();
        foreach (var proposal in EditionLinkPolicy.Propose(LibraryCodes, catalog, null))
            proposal.Candidates.Should().OnlyContain(c =>
                CatalogTextIdentity.UnitKey(c.Unit) == CatalogTextIdentity.UnitKey(LibraryItemReferences.Items[proposal.LibraryCode].Unit));
        catalog.Items["U51.06.3060"] = new CatalogItem { Code = "U51.06.3060", Description = "SYNTHETIC listed", UnitRaw = "מטר" };
        var listed = Proposal(EditionLinkPolicy.Propose(LibraryCodes, catalog, null), "U51.06.3060");
        listed.PresentInCatalog.Should().BeTrue();
        listed.Candidates.Should().BeEmpty();
        catalog.ResolveLibraryCode("U51.06.3060").Should().Be("U51.06.3060");
    }

    [Fact]
    public void AnApprovedLinkAppliesOnlyToItsListAndItemAndIsSupersededNotEdited()
    {
        var catalog = Unified();
        var links = EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.06.3060", "51.06.1578", EditionMatchKind.TruncatedPrefix) },
            catalog, Engineer, "גוון אפור לפי תכנית הפיתוח", When);
        var link = links.Should().ContainSingle().Which;
        link.Evidence.Should().Be("truncated_prefix");
        link.CatalogItemFingerprint.Should().Be(CatalogIdentity.ItemFingerprint(catalog.Items["51.06.1578"]));

        var linked = EditionLinkPolicy.WithLibraryAliases(catalog, links, out var stale);
        stale.Should().BeEmpty();
        linked.SnapshotId.Should().Be(catalog.SnapshotId);
        linked.FileHash.Should().Be(catalog.FileHash);
        linked.ResolveLibraryCode("U51.06.3060").Should().Be("51.06.1578");

        var otherEdition = new CatalogSnapshot { SnapshotId = catalog.SnapshotId, FileHash = new string('e', 64), Items = catalog.Items, Prices = catalog.Prices };
        EditionLinkPolicy.WithLibraryAliases(otherEdition, links, out var none).ResolveLibraryCode("U51.06.3060").Should().BeNull();
        none.Should().BeEmpty("a link of another edition is not stale here, just not this list's");

        var edited = new CatalogSnapshot { SnapshotId = catalog.SnapshotId, FileHash = catalog.FileHash, Prices = catalog.Prices };
        foreach (var pair in catalog.Items) edited.Items[pair.Key] = pair.Value;
        edited.Items["51.06.1578"] = new CatalogItem { Code = "51.06.1578", Description = "אבן גן אחרת", UnitRaw = "מטר" };
        EditionLinkPolicy.WithLibraryAliases(edited, links, out var changed).ResolveLibraryCode("U51.06.3060").Should().BeNull();
        changed.Should().ContainSingle().Which.Should().Contain("השתנו");

        var again = EditionLinkPolicy.Approve(links, new[] { new EditionLinkRequest("U51.06.3060", "51.06.1582", EditionMatchKind.TruncatedPrefix) },
            catalog, Engineer, "גוון אחר", When.AddMinutes(1));
        again.Select(l => (l.CatalogCode, l.Status)).Should().Equal(("51.06.1578", "superseded"), ("51.06.1582", "active"));
        EditionLinkPolicy.WithLibraryAliases(catalog, again, out _).ResolveLibraryCode("U51.06.3060").Should().Be("51.06.1582");
    }

    [Fact]
    public void ALinkAcrossUnitsOrForANonLibraryCodeIsRefusedAndTwoActiveLinksApplyNeither()
    {
        var catalog = Unified();
        FluentActions.Invoking(() => EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.02.0110", "51.02.0110", EditionMatchKind.ParameterConflict) },
            catalog, Engineer, "x", When)).Should().Throw<ArgumentException>().WithMessage("*יחידת*");
        FluentActions.Invoking(() => EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U99.99.9999", "51.01.1080", EditionMatchKind.ExactText) },
            catalog, Engineer, "x", When)).Should().Throw<ArgumentException>().WithMessage("*אינו קוד של הספרייה*");
        FluentActions.Invoking(() => EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.01.2000", "51.01.1080", EditionMatchKind.ExactText) },
            catalog, " ", "x", When)).Should().Throw<ArgumentException>();

        var one = EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.01.2000", "51.01.1080", EditionMatchKind.ExactText) }, catalog, Engineer, "x", When);
        var twice = one.Concat(one.Select(l => new EditionItemLink
        {
            LibraryCode = l.LibraryCode, CatalogCode = l.CatalogCode, CatalogId = l.CatalogId, CatalogHash = l.CatalogHash,
            CatalogItemFingerprint = l.CatalogItemFingerprint, Evidence = l.Evidence, Status = l.Status, Reason = l.Reason,
            ApprovedBy = l.ApprovedBy, ApprovedAtUtc = l.ApprovedAtUtc,
        })).ToList();
        EditionLinkPolicy.WithLibraryAliases(catalog, twice, out var stale).ResolveLibraryCode("U51.01.2000").Should().BeNull();
        stale.Should().ContainSingle().Which.Should().Contain("2 קישורים");
        EditionLinkPolicy.Validate(new ProjectProfile.EstimateProfile { EditionLinks = twice }, "P")
            .Should().ContainSingle(f => f.Code == EditionLinkPolicy.DuplicateCode);
    }

    [Fact]
    public void TheDraftNamesTheMissingLibraryItemAndPricesTheLinkedEditionItem()
    {
        var catalog = Unified();
        var record = new NeutralQuantityRecord
        {
            RecordId = "q-edition-1", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = "1", EntityType = "LWPOLYLINE",
                Layer = "6422-GM-MODEL-NATAZ|TR-GRDN-STONE", Xref = "6422-GM-MODEL-NATAZ",
            },
            Measurement = new QuantityMeasurement { Kind = "length", Method = "polyline-length+xref-transform", RawValue = 120, Unit = "מטר" },
            Classification = new QuantityClassification(),
        };
        EngineerBoqDraft Draft(CatalogSnapshot list) => EngineerBoqDraftBuilder.Build(new[] { record }, Array.Empty<DeliveryFinding>(), list,
            new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "נת\"י אחוד 07/2026", Array.Empty<string>()));

        var unlinked = Draft(catalog);
        unlinked.Warnings.Should().Contain(w => w.Topic == "סעיף הספרייה אינו במהדורה הפעילה" && w.Message.Contains("U51.06.3060") &&
                                                w.Message.Contains("נת\"י אחוד 07/2026"));
        unlinked.Lines.Should().ContainSingle(l => l.Emit.Code == "U51.06.3060" && l.Item == null && l.Price == null);

        var links = EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.06.3060", "51.06.1578", EditionMatchKind.TruncatedPrefix) },
            catalog, Engineer, "SYNTHETIC", When);
        var linked = Draft(EditionLinkPolicy.WithLibraryAliases(catalog, links, out _));
        var line = linked.Lines.Should().ContainSingle(l => l.Emit.Code == "51.06.1578").Which;
        line.Item.Should().NotBeNull();
        line.Price.Should().Be(catalog.Prices["51.06.1578"].Price);
        line.Emit.Note.Should().Contain("U51.06.3060 → 51.06.1578");
        linked.Warnings.Should().NotContain(w => w.Topic == "סעיף הספרייה אינו במהדורה הפעילה");
    }

    [Fact]
    public void AParameterGatedApprovalThroughALinkNamesTheApprovedItemAndTheLibraryLine()
    {
        // Live E2-T1 (02.10): 801-(3-3) approved as 51.32.1852 through the link U51.32.0210 -> 51.32.1852; the warning named
        // the library code instead of the approved item (F-3).
        var catalog = Unified();
        var record = new NeutralQuantityRecord
        {
            RecordId = "q-edition-gated", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = "2", EntityType = "LWPOLYLINE",
                Layer = "6422-GM-466-ALBX-MHD|TR-MARK-WHT-801-(3-3)", Xref = "6422-GM-466-ALBX-MHD",
            },
            Measurement = new QuantityMeasurement { Kind = "length", Method = "polyline-length+xref-transform", RawValue = 100, Unit = "מטר" },
            Classification = new QuantityClassification
            {
                RuleKey = "SYNTHETIC", CandidateCatalogCode = "51.32.1852", ApprovedCatalogId = catalog.SnapshotId,
                ApprovedCatalogHash = catalog.FileHash, ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items["51.32.1852"]),
                MappingApprovedBy = Engineer, MappingApprovedAtUtc = When,
            },
        };
        var links = EditionLinkPolicy.Approve(null, new[] { new EditionLinkRequest("U51.32.0210", "51.32.1852", EditionMatchKind.ExactText) },
            catalog, Engineer, "SYNTHETIC", When);
        var draft = EngineerBoqDraftBuilder.Build(new[] { record }, Array.Empty<DeliveryFinding>(),
            EditionLinkPolicy.WithLibraryAliases(catalog, links, out _), new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "נת\"י אחוד 07/2026", Array.Empty<string>()));

        var warning = draft.Warnings.Should().ContainSingle(w => w.Topic == "שיוך מאושר תלוי בפרמטר").Which;
        var plain = new string(warning.Message.Where(c => c is not ('‎' or '‏') && (c < '‪' || c > '‮') &&
                                                          (c < '⁦' || c > '⁩')).ToArray());
        plain.Should().Contain("אושר בפרופיל הפריט 51.32.1852 (בספרייה: U51.32.0210),");
    }

    [Fact]
    public void LinksSurviveTheYamlRoundTripAsSchemaThreeAndAnIncompleteLinkIsRefused()
    {
        var catalog = Unified();
        var profile = new ProjectProfile { ProfileId = "EDITION-FIXTURE", ProjectName = "SIMULATION ONLY" };
        profile.Estimate.EditionLinks = EditionLinkPolicy.Approve(null,
            new[] { new EditionLinkRequest("U51.01.2000", "51.01.1080", EditionMatchKind.ExactText) }, catalog, Engineer, "טקסט זהה", When);
        var path = Path.Combine(_directory, "project-profile.yaml");
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        ProjectProfileWriter.Save(profile, path, "edition links (synthetic)", Engineer, expected);

        profile.SchemaVersion.Should().Be(EditionLinkPolicy.EditionLinksSchemaVersion);
        File.ReadAllText(path).Should().Contain("schema_version: 3").And.Contain("edition_links:");
        var loaded = ProjectProfileLoader.LoadFromFile(path);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Title)));
        JsonSerializer.Serialize(loaded.Profile!.Estimate.EditionLinks).Should().Be(JsonSerializer.Serialize(profile.Estimate.EditionLinks));
        EstimateTraceIdentity.EffectiveProfileHash(loaded.Profile).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));

        var incomplete = new ProjectProfile.EstimateProfile { EditionLinks = { new EditionItemLink { LibraryCode = "U51.01.2000", Status = "active" } } };
        EditionLinkPolicy.Validate(incomplete, "P").Should().ContainSingle(f => f.Code == EditionLinkPolicy.IncompleteCode);
    }

    [Fact]
    public void AGenericWaitingAreaNoLongerNamesABusStop()
    {
        FamilySignalTable.Read("רחבת הערכות").Hits.SelectMany(hit => hit.Phrase.Families).Should().NotContain("bus-stop-blocks");
        FamilySignalTable.Read("TAMRUR BUS ST").Hits.SelectMany(hit => hit.Phrase.Families).Should().Contain("bus-stop-blocks");
    }
}
