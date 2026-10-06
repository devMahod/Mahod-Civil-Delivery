using System.Globalization;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// The bridge from readable recognition evidence (ev_*) to the existing catalog-candidate search. Every fixture is
/// SYNTHETIC and written for these tests: a TEST catalog with TEST descriptions, no price list, profile, AI or DWG.
/// A proposal here is a candidate only (never approval, a price or a scope decision).
/// </summary>
public sealed class CatalogEvidenceBridgeContractTests
{
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static int _next;

    private static NeutralQuantityRecord Block(string layer, params (string Key, string Value, string Status)[] evidence)
    {
        var id = "cb" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
            [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
        };
        foreach (var (key, value, status) in evidence)
        {
            if (value.Length > 0) parameters[key] = value;
            parameters[key + EvidenceKeys.StatusSuffix] = status;
        }
        return new NeutralQuantityRecord
        {
            RecordId = id,
            ProjectProfileId = "SYNTHETIC",
            RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('b', 64), Handle = id, EntityType = "BlockReference",
                Layer = layer,
            },
            Measurement = new QuantityMeasurement
            {
                Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'", Parameters = parameters,
            },
        };
    }

    private static (string, string, string) Attribute(string tag, string value) =>
        (EvidenceKeys.BlockAttributes, $"[{{\"tag\":\"{tag}\",\"value\":\"{value}\",\"invisible\":false}}]", "read");

    private static (string, string, string) Property(string name, string value) =>
        (EvidenceKeys.BlockProps, $"[{{\"name\":\"{name}\",\"value\":\"{value}\"}}]", "read");

    private static readonly string[] ShelterFamilyCodes = Library.Rules.Single(rule => rule.Id == "bus-shelters").Emits
        .Select(emit => emit.Code).ToArray();

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "TEST-ONLY", FileHash = new string('c', 64) };
        void Item(string code, string description, string unit) =>
            catalog.Items.Add(code, new CatalogItem { Code = code, Description = description, UnitRaw = unit });
        foreach (var code in ShelterFamilyCodes) Item(code, "TEST-ONLY פריט משפחה ללא מילת נושא", "יח'");
        Item("TEST-SHELTER-KW", "אספקה והתקנה של סככה לתחנת אוטובוס TEST-ONLY", "יח'");
        Item("TEST-WRONG-UNIT", "סככה לתחנת אוטובוס TEST-ONLY", "מטר");
        Item("TEST-OTHER-SUBJECT", "ספסל TEST-ONLY", "יח'");
        Item("TEST-VALVE", "מגוף למים TEST-ONLY", "יח'");
        return catalog;
    }

    private static MappingProposalEngine.DiscoveredGroup Group(NeutralQuantityRecord[] records, bool bridge = true)
    {
        var first = records[0];
        return new MappingProposalEngine.DiscoveredGroup(
            "layer:" + first.Source.Layer + "|count", first.Source.Layer, "count", "יח'", records.Length, records.Length,
            QuantityCadMetadataPolicy.Summarize(records.Select(record => record.Measurement)),
            bridge ? CatalogEvidenceBridge.For(records, Library) : null);
    }

    [Fact]
    public void ObjectAttributesOnARandomLayerReachExistingCatalogCandidatesWithTheirCitation()
    {
        var records = new[] { Block("QX971", Attribute("DESC", "SHELTER")), Block("QX971", Attribute("DESC", "SHELTER")) };
        var catalog = Catalog();

        MappingProposalEngine.Propose(new[] { Group(records, bridge: false) }, catalog)
            .Should().BeEmpty("control: without the bridge the evidence never reaches the search");

        var evidence = CatalogEvidenceBridge.For(records, Library);
        evidence.Contradicted.Should().BeFalse();
        evidence.Subjects.Should().ContainSingle().Which.Should().Be(
            new CatalogEvidenceBridge.Subject(EvidenceKeys.BlockAttributes, "value", "DESC", "SHELTER", 2));
        var proposals = MappingProposalEngine.Propose(new[] { Group(records) }, catalog);
        var codes = proposals.Select(p => p.ProposedCode).ToList();
        codes.Should().Contain("TEST-SHELTER-KW");
        codes.Should().NotContain(new[] { "TEST-WRONG-UNIT", "TEST-OTHER-SUBJECT", "TEST-VALVE" });
        proposals.Single(p => p.ProposedCode == "TEST-SHELTER-KW").Reasons
            .Should().Contain(reason => reason.Contains("ev_block_attributes") && reason.Contains("זהה בכל 2 העצמים") &&
                                        reason.Contains("לא אישור"));
        catalog.Items.Keys.Should().Contain(new[] { "TEST-SHELTER-KW", "TEST-WRONG-UNIT" }, "the catalog is never changed");
    }

    [Fact]
    public void ARecognisedFamilyOffersItsOwnLibraryItemsAsCandidatesOnly()
    {
        // "SHELTER" alone is ambiguous for recognition (a bus shelter or another shelter); "BUS SHELTER" is not.
        var records = new[] { Block("QX971", Attribute("DESC", "BUS SHELTER")), Block("QX971", Attribute("DESC", "BUS SHELTER")) };
        var evidence = CatalogEvidenceBridge.For(records, Library);
        evidence.RecognisedFamily.Should().Be("bus-shelters");
        evidence.FamilyCandidateCodes.Should().BeEquivalentTo(ShelterFamilyCodes);
        evidence.FamilyEvidenceKeys.Should().Contain(EvidenceKeys.BlockAttributes);

        var proposals = MappingProposalEngine.Propose(new[] { Group(records) }, Catalog());
        foreach (var code in ShelterFamilyCodes)
            proposals.Should().ContainSingle(p => p.ProposedCode == code).Which.Reasons
                .Should().Contain(reason => reason.Contains("bus-shelters") && reason.Contains("הצעה בלבד"));
    }

    [Fact]
    public void ATextThatDiffersBetweenRecordsOrIsCutOrUnreadIsNotASubject()
    {
        CatalogEvidenceBridge.UniformSubjects(new[]
        {
            Block("QX971", Attribute("DESC", "SHELTER")), Block("QX971", Attribute("DESC", "BENCH")),
        }).Should().BeEmpty("a value that differs between the records does not say what the group is");

        CatalogEvidenceBridge.UniformSubjects(new[]
        {
            Block("QX971", Attribute("DESC", "SHELTER")),
            Block("QX971", (EvidenceKeys.BlockAttributes, "[{\"tag\":\"DESC\",\"value\":\"SHELTER\"}]", "truncated:9")),
        }).Should().BeEmpty("a cut list may have dropped the value that differs");

        CatalogEvidenceBridge.UniformSubjects(new[]
        {
            Block("QX971", Attribute("DESC", "SHELTER")),
            Block("QX971", (EvidenceKeys.BlockAttributes, string.Empty, "unavailable:eAccessViolation")),
        }).Should().BeEmpty("an unread value is not an agreeing one");

        CatalogEvidenceBridge.UniformSubjects(new[]
        {
            Block("QX971", Attribute("LEN", "3.50")), Block("QX971", Attribute("LEN", "3.50")),
        }).Should().BeEmpty("a number says nothing about what the object is");
    }

    [Fact]
    public void NearbyTextIsNeverASubjectOfTheObject()
    {
        var nearby = (EvidenceKeys.NearbyText,
            "[{\"text\":\"SHELTER\",\"distance_m\":1,\"handle\":\"T1\",\"source\":\"host\",\"kind\":\"text\"}]", "read");
        CatalogEvidenceBridge.UniformSubjects(new[] { Block("QX971", nearby), Block("QX971", nearby) })
            .Should().BeEmpty("a text near the object is a spatial candidate, not the object's own statement");
    }

    [Fact]
    public void ContradictingObjectEvidenceStopsCandidatesEvenWhenABlockNameWouldMatch()
    {
        var blockName = (EvidenceKeys.BlockNameEffective, "\"SHELTER\"", "read");
        var legacyName = ("cad_block_name_effective", "SHELTER", "read");
        NeutralQuantityRecord Conflicting() => Block("QX971", Attribute("DESC", "WATER"), Property("TYPE", "SEWER"),
            blockName, legacyName);
        var records = new[] { Conflicting(), Conflicting() };
        var catalog = Catalog();

        MappingProposalEngine.Propose(new[] { Group(records, bridge: false) }, catalog).Select(p => p.ProposedCode)
            .Should().Contain("TEST-SHELTER-KW", "control: the block name alone does reach a shelter item");

        var local = LocalFamilyClassifier.Instance.Classify(EngineerBoqDraftBuilder.RecognitionGroups(records, Library).Single(),
            Library, null);
        local.Should().OnlyContain(p => p.Status == RecognitionStatus.Abstained);
        local.Should().Contain(p => p.EvidenceContradicts);

        var group = Group(records);
        group.RecognitionEvidence!.Contradicted.Should().BeTrue();
        MappingProposalEngine.Propose(new[] { group }, catalog).Should().BeEmpty(
            "the object's own evidence names water and sewer: a block name that matches an item must not break that tie");
        MappingProposalEngine.EvidenceRefusal(group).Should().NotBeNull().And.Contain("סותרות").And.Contain("המדידות לא הוחרגו");
    }

    [Fact]
    public void ATextNamingTwoSubjectsIsReportedAsMixedAndStopsCandidates()
    {
        var records = new[]
        {
            Block("QX971", Attribute("DESC", "WATER SEWER")), Block("QX971", Attribute("DESC", "WATER SEWER")),
        };
        var local = LocalFamilyClassifier.Instance.Classify(EngineerBoqDraftBuilder.RecognitionGroups(records, Library).Single(),
            Library, null);
        local.Should().Contain(p => p.AbstainKind == RecognitionProposal.AbstainMixedSubjects);
        MappingProposalEngine.Propose(new[] { Group(records) }, Catalog()).Should().BeEmpty();
    }

    [Fact]
    public void AProposedLocalFamilyCarriesNoAbstainKind()
    {
        var records = new[] { Block("QX971", Attribute("DESC", "BUS SHELTER")) };
        var local = LocalFamilyClassifier.Instance.Classify(EngineerBoqDraftBuilder.RecognitionGroups(records, Library).Single(),
            Library, null);
        local.Should().ContainSingle().Which.Should().Match<RecognitionProposal>(p =>
            p.Status == RecognitionStatus.Proposed && p.AbstainKind == null && !p.EvidenceContradicts);
    }

    private static NeutralQuantityRecord WithSchema(NeutralQuantityRecord record, string? schema, string? status)
    {
        var parameters = record.Measurement.Parameters;
        parameters.Remove(EvidenceKeys.Schema);
        parameters.Remove(EvidenceKeys.Schema + EvidenceKeys.StatusSuffix);
        if (schema != null) parameters[EvidenceKeys.Schema] = schema;
        if (status != null) parameters[EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = status;
        return record;
    }

    [Theory]
    [InlineData("\"mahod-evidence/1\"", null)]
    [InlineData("\"mahod-evidence/1\"", "unavailable:x")]
    [InlineData("\"mahod-evidence/9\"", "read")]
    public void ObjectEvidenceUnderAnUnreadOrUnknownSchemaIsNeverASubjectOrACandidate(string schema, string? status)
    {
        var records = new[]
        {
            WithSchema(Block("QX971", Attribute("DESC", "BUS SHELTER")), schema, status),
            WithSchema(Block("QX971", Attribute("DESC", "BUS SHELTER")), schema, status),
        };
        var evidence = CatalogEvidenceBridge.For(records, Library);
        evidence.Subjects.Should().BeEmpty("the classifier does not read these ev_* either");
        evidence.FamilyCandidateCodes.Should().BeEmpty();
        MappingProposalEngine.Propose(new[] { Group(records) }, Catalog()).Should().BeEmpty();
    }

    [Fact]
    public void ALegacyRecordWithoutAnySchemaIsReadAsTheClassifierReadsIt()
    {
        var records = new[]
        {
            WithSchema(Block("QX971", Attribute("DESC", "BUS SHELTER")), null, null),
            WithSchema(Block("QX971", Attribute("DESC", "BUS SHELTER")), null, null),
        };
        CatalogEvidenceBridge.For(records, Library).Subjects.Should().ContainSingle();
    }

    [Fact]
    public void AFamilyRestingOnCutEvidenceGivesNoAutomaticCandidates()
    {
        var cut = (EvidenceKeys.BlockAttributes, "[{\"tag\":\"DESC\",\"value\":\"BUS SHELTER\"}]", "truncated:6");
        var records = new[] { Block("QX971", cut), Block("QX971", cut) };
        var evidence = CatalogEvidenceBridge.For(records, Library);
        evidence.Subjects.Should().BeEmpty("a cut list may have dropped the value that differs");
        evidence.FamilyCandidateCodes.Should().BeEmpty();
        if (evidence.RecognisedFamily != null)
            evidence.FamilyWithheld.Should().Contain("קטועה").And.Contain(EvidenceKeys.BlockAttributes);
        var proposals = MappingProposalEngine.Propose(new[] { Group(records) }, Catalog());
        proposals.Select(p => p.ProposedCode).Should().NotContain(ShelterFamilyCodes);
    }

    [Fact]
    public void ABusStopPropertyOnARandomLayerKeepsTheBusContextButBuysNoOtherStation()
    {
        // Real evidence (SM handle B0AD, run estimate-extract-20260928-120821-ed8505e9): all 115 objects carry the dynamic
        // property Visibility1='505 BUS stop' and their layer and block names are numbers. The rows are the real 07/2026
        // wording; the live run also offered the speed-detector station, only because "BUS" bought the bare word "תחנת".
        var records = Enumerable.Range(0, 3).Select(_ => Block("QX505", Property("Visibility1", "505 BUS stop"))).ToArray();
        var catalog = new CatalogSnapshot { SnapshotId = "TEST-ONLY", FileHash = new string('c', 64) };
        void Item(string code, string description) =>
            catalog.Items.Add(code, new CatalogItem { Code = code, Description = description, UnitRaw = "יח'" });
        Item("40.02.5427", "שלט זיהוי לסככת אוטובוס");
        Item("40.02.0180", "תכנון וביצוע תחנת אוטובוס ע\"פ מפרט והדמייה מצורפים - פרויקט שילובים");
        Item("08.01.5124", "תחנת גלאי מהירות (2 גלאים בתחנה)");

        var proposals = MappingProposalEngine.Propose(new[] { Group(records) }, catalog);

        proposals.Select(p => p.ProposedCode).Should().BeEquivalentTo(new[] { "40.02.5427", "40.02.0180" });
        proposals.Should().OnlyContain(p => p.Reasons.Any(r => r.Contains("505 BUS stop") && r.Contains("לא אישור")));
        proposals.Select(p => p.Score).Distinct().Should().ContainSingle(
            "the property says bus stop, not sign, shelter or a full stop: neither candidate outranks the other");
    }
}
