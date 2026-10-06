using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// Regression tests for the verified review findings on the local family classifier (work93b, findings 0–8) and
/// for the infrastructure-domain recognition. Each test names the finding it covers; each failed before the fix.
/// Every fixture below is SYNTHETIC and written for these tests.
/// </summary>
public sealed class ClassifierFixTests
{
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static int _next;

    // ------------------------------------------------------------------ fixtures

    private static NeutralQuantityRecord Rec(string kind, string method, string unit, Dictionary<string, string> parameters,
        string? xref = "SYNTH-MODEL-A", string layer = "asdasd23423")
    {
        var id = "cf" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        return new NeutralQuantityRecord
        {
            RecordId = id,
            ProjectProfileId = "SYNTHETIC",
            RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = id, EntityType = "X",
                Layer = xref == null ? layer : $"{xref}|{layer}", Xref = xref,
            },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = 1, Unit = unit, Parameters = parameters },
        };
    }

    private static NeutralQuantityRecord BlockRec(Dictionary<string, string> p, string? xref = "SYNTH-MODEL-A", string layer = "asdasd23423") =>
        Rec("count", "block-count", "יח'", p, xref, layer);

    private static NeutralQuantityRecord LineRec(Dictionary<string, string> p, string? xref = "SYNTH-MODEL-A", string layer = "asdasd23423") =>
        Rec("length", "polyline-length", "מטר", p, xref, layer);

    private static NeutralQuantityRecord HatchRec(Dictionary<string, string> p) => Rec("area", "hatch-area", "מ\"ר", p);

    private static RecognitionGroupInput Group(string groupId, params NeutralQuantityRecord[] records)
    {
        var first = records[0];
        var kind = first.Measurement.Kind;
        var source = string.IsNullOrWhiteSpace(first.Source.Xref) ? EngineerBoqDraftBuilder.HostSource : first.Source.Xref!;
        return new RecognitionGroupInput(groupId, source, EngineerBoqDraftBuilder.SourceRole(source, Library),
            SectionProjectionLogic.LayerLeaf(first.Source.Layer), kind, first.Measurement.Unit,
            RecognitionBenchmarkAdapter.MethodClassFor(kind, first.Measurement.Method), null, records);
    }

    private static IReadOnlyList<RecognitionProposal> Classify(RecognitionGroupInput group) =>
        LocalFamilyClassifier.Instance.Classify(group, Library, null);

    /// <summary>Evidence with status "read" for every key, under the mahod-evidence/1 schema.</summary>
    private static Dictionary<string, string> Ev(params (string Key, string Value)[] read)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
            [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
        };
        foreach (var (key, value) in read)
        {
            parameters[key] = value;
            parameters[key + EvidenceKeys.StatusSuffix] = "read";
        }
        return parameters;
    }

    private static (string, string) Block(string name) => (EvidenceKeys.BlockNameEffective, name);

    private static (string, string) Attributes(params (string Tag, string Value)[] pairs) =>
        (EvidenceKeys.BlockAttributes, JsonSerializer.Serialize(pairs.Select(p => new { tag = p.Tag, value = p.Value })));

    private static (string, string) Legend(string text, string match) =>
        (EvidenceKeys.LegendRow, JsonSerializer.Serialize(new { text, match, handle = "1F", verified = false }));

    private static (string, string) Nearby(params (string Text, double Distance)[] items) =>
        (EvidenceKeys.NearbyText, JsonSerializer.Serialize(items.Select((t, i) => new { text = t.Text, distance_m = t.Distance, handle = $"T{i}", source = "host" })));

    /// <summary>ev_xref_transform for one insert with uniform scale s (rotation-free), metres in both spaces.</summary>
    private static string UniformTransform(double s)
    {
        var v = s.ToString("R", CultureInfo.InvariantCulture);
        return $"{{\"space\":\"host\",\"chain\":[{{\"xref\":\"SYNTH-SM\",\"matrix\":[{v},0,0,10,0,{v},0,20,0,0,{v},0,0,0,0,1]}}]," +
               $"\"class\":\"uniform\",\"scale\":[{v},{v},{v}],\"units\":{{\"source\":\"Meters\",\"host\":\"Meters\"}}}}";
    }

    private static Dictionary<string, string> WithWidth(Dictionary<string, string> parameters, double width)
    {
        parameters["cad_polyline_constant_width_raw"] = width.ToString("R", CultureInfo.InvariantCulture);
        parameters["cad_entity_database_insunits"] = "Meters";
        return parameters;
    }

    private static IEnumerable<string> Families(TextReading reading) =>
        reading.Hits.SelectMany(h => h.Phrase.Families).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal);

    // ------------------------------------------------------------------ finding 0 and the infrastructure scope

    [Theory]
    [InlineData("קו מים", "utility-water")]
    [InlineData("MEKOROT", "utility-water")]
    [InlineData("ביוב", "utility-sewer")]
    [InlineData("קולחין", "utility-sewer")]
    [InlineData("NIKUZ", "utility-drainage")]
    [InlineData("STORM", "utility-drainage")]
    [InlineData("חשמל", "utility-electric")]
    [InlineData("POWER LINE", "utility-electric")]
    [InlineData("עמוד תאורה", "utility-lighting")]
    [InlineData("TAURA", "utility-lighting")]
    [InlineData("רמזור", "utility-traffic-signals")]
    [InlineData("RAMZOR", "utility-traffic-signals")]
    [InlineData("בזק", "utility-telecom")]
    [InlineData("BEZEK", "utility-telecom")]
    [InlineData("גז", "utility-gas")]
    [InlineData("GAS", "utility-gas")]
    public void AnInfrastructureSubjectProposesItsDomainFamilyByTheMeasurementBasis(string text, string lineFamily)
    {
        var line = Classify(Group("g-line", LineRec(Ev(Attributes(("TYPE", text)))))).Should().ContainSingle().Which;
        var count = Classify(Group("g-count", BlockRec(Ev(Attributes(("TYPE", text)))))).Should().ContainSingle().Which;
        var hatch = Classify(Group("g-hatch", HatchRec(Ev(Attributes(("TYPE", text)))))).Should().ContainSingle().Which;

        line.Status.Should().Be(RecognitionStatus.Proposed);
        line.FamilyId.Should().Be(lineFamily);
        line.CandidateCodes.Should().BeEmpty("an infrastructure domain family proposes no item");
        line.EvidenceRefs.Select(r => r.Key).Should().Equal(EvidenceKeys.BlockAttributes);
        count.Status.Should().Be(RecognitionStatus.Proposed);
        count.FamilyId.Should().Be(lineFamily + FamilySignalTable.StructuresSuffix);
        hatch.Status.Should().Be(RecognitionStatus.Abstained, "no infrastructure family is measured by hatch area");
        hatch.FamilyId.Should().BeNull();
        hatch.MissingDetails.Should().Contain(m => m.Contains(lineFamily, StringComparison.Ordinal));
    }

    [Fact]
    public void InfrastructureTextOnTheObjectThatCannotBeThisMeasurementBlocksAnyOtherChannel()
    {
        // Re-check residual: a drainage attribute on a hatch area used to vanish, letting a touching "מדרכה" decide alone.
        var hatch = Classify(Group("g-hatch-drain", HatchRec(Ev(Attributes(("TYPE", "ניקוז")), Nearby(("מדרכה", 0))))))
            .Should().ContainSingle().Which;
        hatch.Status.Should().Be(RecognitionStatus.Abstained);
        hatch.MissingDetails.Should().Contain(m => m.Contains("utility-drainage", StringComparison.Ordinal) || m.Contains("ניקוז", StringComparison.Ordinal));
        // A drainage word merely nearby a sidewalk hatch is context, not a conflict.
        var context = Classify(Group("g-hatch-near", HatchRec(Ev(Attributes(("TYPE", "מדרכה")), Nearby(("קו ניקוז", 2)))))).Should().ContainSingle().Which;
        context.Status.Should().Be(RecognitionStatus.Proposed);
        context.FamilyId.Should().Be("sidewalk-paving");
    }

    [Fact]
    public void BareGenericWordsNeverDecideAlone()
    {
        foreach (var word in new[] { "ROAD", "כביש", "POWER", "LIGHT", "HOT", "מקורות" })
        {
            var line = Classify(Group("g-bare-" + word, LineRec(Ev(Attributes(("TYPE", word)))))).Should().ContainSingle().Which;
            line.Status.Should().Be(RecognitionStatus.Abstained, word);
        }
        var hatch = Classify(Group("g-road-marking", HatchRec(Ev(Nearby(("ROAD MARKING", 0)))))).Should().ContainSingle().Which;
        hatch.FamilyId.Should().NotBe("road-pavement", "a chevron hatch labelled 'road marking' is not the pavement");
        var narrowed = Classify(Group("g-road-narrowed", HatchRec(Ev(Attributes(("TYPE", "ROAD")), Legend("מיסעה", "pattern"))))).Should().ContainSingle().Which;
        narrowed.FamilyId.Should().Be("road-pavement", "a bare word still narrows another channel");
    }

    [Fact]
    public void AnUnreadableSchemaNeverLetsTheTransformWithholdFamilies()
    {
        var p = WithWidth(Ev(Attributes(("TYPE", "מעבר חצייה"))), 3.0);
        p["ev_schema"] = "mahod-evidence/2";
        p["ev_schema_status"] = "read";
        p[EvidenceKeys.XrefTransform] = "{\"space\":\"host\",\"chain\":[{\"xref\":\"SYNTH-SM\",\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]}]," +
                                         "\"class\":\"rigid\",\"scale\":[1,1,1],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}";
        p[EvidenceKeys.XrefTransform + "_status"] = "read";
        var result = Classify(Group("g-schema-width", LineRec(p, xref: "SYNTH-SM", layer: "TR-MARK-WHT-811"))).Should().ContainSingle().Which;
        result.EvidenceRefs.Select(r => r.Key).Should().NotContain(EvidenceKeys.XrefTransform);
        result.Status.Should().Be(RecognitionStatus.Abstained, "evidence under an unknown schema is not read, so nothing proves a family");
    }

    [Fact]
    public void Finding0_AnInfrastructureSubjectAndARoadsPhraseInOneTextAbstainWithBoth()
    {
        const string drainageAtRoadEdge = "תעלת ניקוז בשפת כביש";
        var attribute = Classify(Group("g-mixed", LineRec(Ev(Attributes(("TYPE", drainageAtRoadEdge)))))).Should().ContainSingle().Which;
        var nearby = Classify(Group("g-mixed-nearby", LineRec(Ev(Nearby((drainageAtRoadEdge, 0)))))).Should().ContainSingle().Which;
        // Another strong channel naming only the roads subject does not narrow a two-subject text to it.
        var withLegend = Classify(Group("g-mixed-legend",
            LineRec(Ev(Attributes(("TYPE", drainageAtRoadEdge)), Legend("שפת מיסעה", "linetype"))))).Should().ContainSingle().Which;
        var pipeCrossing = Classify(Group("g-pipe-crossing",
            LineRec(WithWidth(Ev(Attributes(("TYPE", "PIPE CROSSING"))), 0.2), xref: null))).Should().ContainSingle().Which;
        var wallAtEdge = Classify(Group("g-wall-edge", LineRec(Ev(Attributes(("TYPE", "קיר תומך בשפת מיסעה")))))).Should().ContainSingle().Which;

        foreach (var abstention in new[] { attribute, nearby, withLegend })
        {
            abstention.Status.Should().Be(RecognitionStatus.Abstained);
            abstention.FamilyId.Should().BeNull();
            abstention.CandidateCodes.Should().BeEmpty();
            abstention.Alternatives.Select(a => a.FamilyId).Should().Contain(new[] { "pavement-edge", "utility-drainage" });
            abstention.MissingDetails.Should().Contain(m => m.Contains("יותר מנושא אחד", StringComparison.Ordinal));
        }
        pipeCrossing.Status.Should().Be(RecognitionStatus.Abstained);
        pipeCrossing.FamilyId.Should().BeNull();
        pipeCrossing.Alternatives.Select(a => a.FamilyId).Should().Contain("marking-crossings");
        wallAtEdge.Status.Should().Be(RecognitionStatus.Abstained);
        wallAtEdge.FamilyId.Should().BeNull();
        wallAtEdge.MissingDetails.Should().Contain(m => m.Contains("נושא שאינו בספרייה", StringComparison.Ordinal));
    }

    [Fact]
    public void AGenericCarrierIsNarrowedByItsDomainAndAbstainsWithoutOne()
    {
        var sewerManhole = Classify(Group("g-manhole", BlockRec(Ev(Attributes(("TYPE", "שוחת ביוב")))))).Should().ContainSingle().Which;
        var sewerPipe = Classify(Group("g-sewer-pipe", LineRec(Ev(Attributes(("TYPE", "SEWER PIPE")))))).Should().ContainSingle().Which;
        var barePipe = Classify(Group("g-pipe", LineRec(Ev(Attributes(("TYPE", "PIPE")))))).Should().ContainSingle().Which;
        var twoDomains = Classify(Group("g-two-domains", LineRec(Ev(Attributes(("TYPE", "ביוב ומים")))))).Should().ContainSingle().Which;
        var rainWater = Classify(Group("g-rain", LineRec(Ev(Attributes(("TYPE", "STORM WATER")))))).Should().ContainSingle().Which;
        var existingSource = Classify(Group("g-ut", LineRec(Ev(Attributes(("TYPE", "קו ביוב"))), xref: "UT-SYNTH")))
            .Should().ContainSingle().Which;

        sewerManhole.Status.Should().Be(RecognitionStatus.Proposed);
        sewerManhole.FamilyId.Should().Be("utility-sewer-structures");
        sewerPipe.Status.Should().Be(RecognitionStatus.Proposed);
        sewerPipe.FamilyId.Should().Be("utility-sewer");
        barePipe.Status.Should().Be(RecognitionStatus.Abstained, "a pipe without a domain is not a decision");
        barePipe.FamilyId.Should().BeNull();
        barePipe.Alternatives.Select(a => a.FamilyId).Should().Contain("utility-sewer").And.NotContain("pavement-edge");
        twoDomains.Status.Should().Be(RecognitionStatus.Abstained);
        twoDomains.Alternatives.Select(a => a.FamilyId).Should().Contain(new[] { "utility-water", "utility-sewer" });
        rainWater.Status.Should().Be(RecognitionStatus.Proposed, "storm water is drainage, not a second subject");
        rainWater.FamilyId.Should().Be("utility-drainage");
        existingSource.Status.Should().Be(RecognitionStatus.Abstained, "an existing-utilities source is never new work");
        existingSource.MissingDetails.Should().Contain(m => m.Contains("תשתיות קיימות", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryInfrastructureDomainIsReachableFromTheVocabularyAndExistsInTheLibrary()
    {
        var ids = Library.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var named = FamilySignalTable.Phrases.SelectMany(p => p.Families).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, _, _) in EngineerBoqLibrary.UtilityDomains)
        {
            ids.Should().Contain(new[] { id, id + FamilySignalTable.StructuresSuffix });
            named.Should().Contain(new[] { id, id + FamilySignalTable.StructuresSuffix });
        }
        FamilySignalTable.Phrases.Should().NotContain(p => p.Families.Contains(FamilySignalTable.MixedSubjectMarker),
            "only the classifier marks evidence as naming two subjects");
    }

    // ------------------------------------------------------------------ finding 1

    [Fact]
    public void Finding1_ASingleDroppedYodOrAPrefixBeforeATwoLetterWordIsNotASpellingVariant()
    {
        Families(FamilySignalTable.Read("כבש")).Should().NotContain("road-pavement");
        Families(FamilySignalTable.Read("כבשים")).Should().NotContain("road-pavement");
        Families(FamilySignalTable.Read("רציף")).Should().NotContain("sidewalk-paving");
        FamilySignalTable.Read("לחץ").Hits.Should().BeEmpty();
        FamilySignalTable.Read("מקטין לחץ").Hits.Should().BeEmpty();
        // A doubled yod/vav, a listed variant and a prefix inside a longer phrase still match.
        Families(FamilySignalTable.Read("אופנים")).Should().Equal(Families(FamilySignalTable.Read("אופניים"))).And.NotBeEmpty();
        Families(FamilySignalTable.Read("קו מקווקוו")).Should().Equal("marking-dash-1-1", "marking-dash-3-15", "marking-dash-3-3");
        Families(FamilySignalTable.Read("באי תנועה")).Should().Equal("island-paving");
        Families(FamilySignalTable.Read("החצים")).Should().Equal("bike-arrows", "marking-arrows");

        var ramp = Classify(Group("g-ramp", HatchRec(Ev(Nearby(("כבש נכים", 0)))))).Should().ContainSingle().Which;
        var valve = Classify(Group("g-valve", BlockRec(Ev(Attributes(("TYPE", "מקטין לחץ")))))).Should().ContainSingle().Which;

        ramp.Status.Should().Be(RecognitionStatus.Abstained);
        ramp.FamilyId.Should().BeNull();
        ramp.Alternatives.Select(a => a.FamilyId).Should().NotContain("road-pavement");
        valve.Status.Should().Be(RecognitionStatus.Abstained);
        valve.Alternatives.Should().BeEmpty("pressure is not an arrow");
    }

    // ------------------------------------------------------------------ finding 2

    [Fact]
    public void Finding2_ACutNearbyTextListNeitherProposesAloneNorNarrows()
    {
        (string, string) texts = Nearby(("מדרכה", 1.0), ("12.50", 1.5), ("3.20", 2.0), ("R=5", 2.5), ("1:500", 3.0));
        var complete = Classify(Group("g-nearby-read", HatchRec(Ev(texts)))).Should().ContainSingle().Which;
        var cutParameters = Ev(texts);
        cutParameters[EvidenceKeys.NearbyText + EvidenceKeys.StatusSuffix] = "truncated:9";
        var cut = Classify(Group("g-nearby-cut", HatchRec(cutParameters))).Should().ContainSingle().Which;
        var narrowingParameters = Ev(Attributes(("TYPE", "אבן שפה")), Nearby(("אבן שפה לכביש", 1.2)));
        narrowingParameters[EvidenceKeys.NearbyText + EvidenceKeys.StatusSuffix] = "truncated:9";
        var notNarrowed = Classify(Group("g-nearby-cut-narrow", LineRec(narrowingParameters))).Should().ContainSingle().Which;

        complete.Status.Should().Be(RecognitionStatus.Proposed);
        complete.FamilyId.Should().Be("sidewalk-paving");
        cut.Status.Should().Be(RecognitionStatus.Abstained);
        cut.FamilyId.Should().BeNull();
        cut.Alternatives.Select(a => a.FamilyId).Should().Equal("sidewalk-paving");
        cut.MissingDetails.Should().Contain(m => m.Contains("נקטעה", StringComparison.Ordinal));
        cut.Observed.Should().Contain(o => o.Contains("ראיה קטועה", StringComparison.Ordinal));
        notNarrowed.Status.Should().Be(RecognitionStatus.Abstained);
        notNarrowed.Alternatives.Select(a => a.FamilyId).Should().Equal("curb-road", "curb-island", "curb-lowered", "curb-bike");
        notNarrowed.MissingDetails.Should().Contain(m => m.Contains("נקטעה", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ finding 3

    [Fact]
    public void Finding3_EvidenceOfAnUnknownOrUnreadSchemaIsNeverReadOrCited()
    {
        var attributes = JsonSerializer.Serialize(new[] { new { tag = "TYPE", value = "סככה" } });
        Dictionary<string, string> Parameters(string? schema, string? status)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EvidenceKeys.BlockAttributes] = attributes,
                [EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "read",
            };
            if (schema != null) parameters[EvidenceKeys.Schema] = schema;
            if (status != null) parameters[EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = status;
            return parameters;
        }

        var noStatus = Classify(Group("g-schema-nostatus", BlockRec(Parameters("mahod-evidence/2", null)))).Should().ContainSingle().Which;
        var absent = Classify(Group("g-schema-absent", BlockRec(Parameters(null, "absent")))).Should().ContainSingle().Which;
        var legacy = Classify(Group("g-schema-legacy", BlockRec(Parameters(null, null)))).Should().ContainSingle().Which;

        foreach (var refused in new[] { noStatus, absent })
        {
            refused.Status.Should().Be(RecognitionStatus.Abstained);
            refused.FamilyId.Should().BeNull();
            refused.EvidenceRefs.Should().BeEmpty();
            refused.MissingDetails.Should().Contain(m => m.Contains("mahod-evidence/1", StringComparison.Ordinal));
        }
        legacy.Status.Should().Be(RecognitionStatus.Proposed, "a record with no ev_schema at all is read as before");
        legacy.FamilyId.Should().Be("bus-shelters");

        // An XREF width is host-proven only through ev_xref_transform, which an unknown contract does not let us read.
        var xrefParameters = WithWidth(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EvidenceKeys.Schema] = "mahod-evidence/2",
            [EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read",
            [EvidenceKeys.XrefTransform] = UniformTransform(1),
            [EvidenceKeys.XrefTransform + EvidenceKeys.StatusSuffix] = "read",
            [EvidenceKeys.BlockNameEffective] = "ZEBRA",
        }, 3.0);
        var xref = Classify(Group("g-schema-xref", LineRec(xrefParameters, xref: "SYNTH-SM"))).Should().ContainSingle().Which;

        xref.Status.Should().Be(RecognitionStatus.Abstained);
        xref.Alternatives.Select(a => a.FamilyId).Should().BeEquivalentTo(new[] { "marking-crossings", "marking-crossing-lines" });
        xref.EvidenceRefs.Select(r => r.Key).Should().NotContain(EvidenceKeys.XrefTransform);
        xref.Observed.Should().NotContain(o => o.Contains("רוחב משורטט במארח", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ finding 4

    [Fact]
    public void Finding4_ADashRatioIsReadOnlyFromOneHyphenatedNumberPair()
    {
        Dictionary<string, string> Separation(string layerLinetype)
        {
            var parameters = Ev(Attributes(("TYPE", "קו הפרדה")));
            parameters[EvidenceKeys.EntityLinetype] = "ByLayer";
            parameters[EvidenceKeys.LayerLinetype] = layerLinetype;
            return parameters;
        }

        var prefixed = Classify(Group("g-lt-prefixed", LineRec(Separation("TR_10_3-3")))).Should().ContainSingle().Which;
        var twoPairs = Classify(Group("g-lt-two-pairs", LineRec(Separation("DASH_2-2_3-3")))).Should().ContainSingle().Which;
        var oneToOne = Classify(Group("g-lt-one", LineRec(Separation("DASHED1-1")))).Should().ContainSingle().Which;

        prefixed.Status.Should().Be(RecognitionStatus.Abstained, "10_3 is not a paint-gap ratio; the stated ratio is 3-3");
        prefixed.Alternatives.Select(a => a.FamilyId).Should().Equal("marking-dash-3-3", "marking-lines");
        twoPairs.Status.Should().Be(RecognitionStatus.Abstained, "a name with two number pairs states no ratio");
        twoPairs.Alternatives.Select(a => a.FamilyId).Should()
            .BeEquivalentTo(new[] { "marking-dash-3-3", "marking-dash-3-15", "marking-dash-1-1", "marking-lines" });
        oneToOne.Status.Should().Be(RecognitionStatus.Abstained);
        oneToOne.Alternatives.Select(a => a.FamilyId).Should().BeEquivalentTo(new[] { "marking-dash-1-1", "marking-lines" });
    }

    // ------------------------------------------------------------------ finding 5

    [Fact]
    public void Finding5_TheBenchmarkAdapterRefusesARepeatedOrMissingRecordIdOnceAndKeepsRunning()
    {
        const string input = """
            {"schema_version":"codex-recognition-input-v1","benchmark_id":"unit-synthetic-ids","provenance":"SYNTHETIC",
             "Cases":[{"CaseId":"C","Groups":[
              {"GroupId":"G","Records":[
                {"RecordId":"d","Source":{"Drawing":"s.dwg","Handle":"1","Layer":"zzz","Xref":"SYN"},
                 "Measurement":{"Kind":"count","Method":"synthetic-block-count","RawValue":1,"Unit":"unit",
                   "Parameters":{"cad_block_name_effective":"arrow-D","cad_block_name_effective_status":"read"}}},
                {"RecordId":"d","Source":{"Drawing":"s.dwg","Handle":"2","Layer":"zzz","Xref":"SYN"},
                 "Measurement":{"Kind":"length","Method":"synthetic-polyline-length","RawValue":5,"Unit":"m","Parameters":{}}}]},
              {"GroupId":"H","Records":[
                {"Source":{"Drawing":"s.dwg","Handle":"3","Layer":"zzz","Xref":"SYN"},
                 "Measurement":{"Kind":"count","Method":"synthetic-block-count","RawValue":1,"Unit":"unit",
                   "Parameters":{"cad_block_name_effective":"arrow-D","cad_block_name_effective_status":"read"}}}]},
              {"GroupId":"K","Records":[
                {"RecordId":"k1","Source":{"Drawing":"s.dwg","Handle":"4","Layer":"zzz","Xref":"SYN"},
                 "Measurement":{"Kind":"count","Method":"synthetic-block-count","RawValue":1,"Unit":"unit",
                   "Parameters":{"cad_block_name_effective":"arrow-D","cad_block_name_effective_status":"read"}}}]}
             ]}]}
            """;

        var json = RecognitionBenchmarkAdapter.Run(Encoding.UTF8.GetBytes(input), LocalFamilyClassifier.Instance, Library);

        using var document = JsonDocument.Parse(json);
        var predictions = document.RootElement.GetProperty("Predictions").EnumerateArray().ToList();
        var rows = predictions.SelectMany(p => p.GetProperty("RecordIds").EnumerateArray()
            .Select(id => (Group: p.GetProperty("GroupId").GetString(), Id: id.GetString()))).ToList();
        rows.Should().OnlyHaveUniqueItems();
        rows.Should().BeEquivalentTo(new List<(string? Group, string? Id)> { ("G", "d"), ("H", ""), ("K", "k1") });
        JsonElement Prediction(string groupId) => predictions.Single(p => p.GetProperty("GroupId").GetString() == groupId);
        foreach (var refused in new[] { Prediction("G"), Prediction("H") })
        {
            refused.GetProperty("Status").GetString().Should().Be("abstained");
            refused.GetProperty("FamilyId").ValueKind.Should().Be(JsonValueKind.Null);
            refused.GetProperty("MissingDetails").GetArrayLength().Should().BeGreaterThan(0);
        }
        Prediction("K").GetProperty("FamilyId").GetString().Should().Be("bike-arrows", "the other groups are still classified");
    }

    // ------------------------------------------------------------------ finding 6

    [Fact]
    public void Finding6_ABareGenericWordOnlyNarrows()
    {
        var noise = Classify(Group("g-noise", LineRec(Ev(Attributes(("TYPE", "NOISE BARRIER")))))).Should().ContainSingle().Which;
        var barrier = Classify(Group("g-barrier", LineRec(Ev(Attributes(("TYPE", "BARRIER")))))).Should().ContainSingle().Which;
        var shelter = Classify(Group("g-shelter", BlockRec(Ev(Block("PUBLIC_SHELTER"))))).Should().ContainSingle().Which;
        var reserve = Classify(Group("g-reserve", HatchRec(Ev(Nearby(("ROAD RESERVE", 0)))))).Should().ContainSingle().Which;
        var safety = Classify(Group("g-safety", LineRec(Ev(Attributes(("TYPE", "SAFETY BARRIER")))))).Should().ContainSingle().Which;
        var busShelter = Classify(Group("g-bus-shelter", BlockRec(Ev(Block("BUS_SHELTER"))))).Should().ContainSingle().Which;
        var roadway = Classify(Group("g-roadway", HatchRec(Ev(Nearby(("ROADWAY", 0)))))).Should().ContainSingle().Which;

        foreach (var abstention in new[] { noise, barrier, shelter, reserve })
        {
            abstention.Status.Should().Be(RecognitionStatus.Abstained);
            abstention.FamilyId.Should().BeNull();
            abstention.CandidateCodes.Should().BeEmpty();
        }
        safety.FamilyId.Should().Be("safety-barrier");
        busShelter.FamilyId.Should().Be("bus-shelters");
        roadway.FamilyId.Should().Be("road-pavement");
    }

    // ------------------------------------------------------------------ finding 7

    [Fact]
    public void Finding7_AShortOrLayerBoundLibraryBlockPatternNeedsItsLayerToBeStrong()
    {
        var elsewhere = Classify(Group("g-m-elsewhere", BlockRec(Ev(Block("M")), layer: "zz_01"))).Should().ContainSingle().Which;
        var onItsLayer = Classify(Group("g-m-layer", BlockRec(Ev(Block("M")), layer: "TR-MARK-ARW-YLW"))).Should().ContainSingle().Which;
        var numericCode = Classify(Group("g-813", BlockRec(Ev(Block("813(814)")), layer: "zz_01"))).Should().ContainSingle().Which;
        var bikeArrow = Classify(Group("g-arrow-d", BlockRec(Ev(Block("arrow-D")), layer: "zz_01"))).Should().ContainSingle().Which;

        elsewhere.Status.Should().Be(RecognitionStatus.Abstained);
        elsewhere.FamilyId.Should().BeNull();
        elsewhere.Alternatives.Select(a => a.FamilyId).Should().Equal("marking-m-blocks");
        elsewhere.MissingDetails.Should().Contain(m => m.Contains("רמז תומך", StringComparison.Ordinal));
        onItsLayer.Status.Should().Be(RecognitionStatus.Proposed);
        onItsLayer.FamilyId.Should().Be("marking-m-blocks");
        numericCode.Status.Should().Be(RecognitionStatus.Abstained, "a bare numeric code is never a signal on its own");
        numericCode.Alternatives.Select(a => a.FamilyId).Should().Equal("marking-arrows");
        bikeArrow.Status.Should().Be(RecognitionStatus.Proposed);
        bikeArrow.FamilyId.Should().Be("bike-arrows");
    }

    // ------------------------------------------------------------------ finding 8

    [Fact]
    public void Finding8_AWideLineOnAWidthClassifiedLayerIsNeverProposedAFamilyTheDraftCannotPriceAsPaintedArea()
    {
        var curbPaint = Classify(Group("g-painted-curb",
            LineRec(WithWidth(Ev(Attributes(("TYPE", "צביעת אבני שפה"))), 0.30), xref: null, layer: "TR-MARK-BLU-01"))).Should().ContainSingle().Which;
        var crossing = Classify(Group("g-painted-crossing",
            LineRec(WithWidth(Ev(Attributes(("TYPE", "מעבר חצייה"))), 3.0), xref: null, layer: "TR-MARK-BLU-01"))).Should().ContainSingle().Which;
        var narrowLine = Classify(Group("g-narrow-curb",
            LineRec(WithWidth(Ev(Attributes(("TYPE", "צביעת אבני שפה"))), 0.10), xref: null, layer: "TR-MARK-BLU-01"))).Should().ContainSingle().Which;
        var otherLayer = Classify(Group("g-wide-other-layer",
            LineRec(WithWidth(Ev(Attributes(("TYPE", "צביעת אבני שפה"))), 0.30), xref: null, layer: "zz_02"))).Should().ContainSingle().Which;

        curbPaint.Status.Should().Be(RecognitionStatus.Abstained);
        curbPaint.FamilyId.Should().BeNull();
        curbPaint.Alternatives.Select(a => a.FamilyId).Should().Equal("curb-painting");
        curbPaint.MissingDetails.Should().Contain(m => m.Contains("שטח צבוע", StringComparison.Ordinal));
        crossing.Status.Should().Be(RecognitionStatus.Proposed, "a width-split family is priced as painted area");
        crossing.FamilyId.Should().Be("marking-crossings");
        narrowLine.Status.Should().Be(RecognitionStatus.Proposed, "a 10 cm line stays a length the draft can price");
        narrowLine.FamilyId.Should().Be("curb-painting");
        otherLayer.Status.Should().Be(RecognitionStatus.Proposed, "only width-classified marking layers are re-read by width");
        otherLayer.FamilyId.Should().Be("curb-painting");
    }
}
