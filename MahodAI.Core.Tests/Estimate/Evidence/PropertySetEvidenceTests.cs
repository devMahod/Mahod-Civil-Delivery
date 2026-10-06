using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// SYNTHETIC PropertySet reads (no Autodesk object is involved). A PropertySet value is the object's own statement, never a
/// verified fact; an unread set or property is never absent and never a complete read.
/// </summary>
public sealed class PropertySetEvidenceTests
{
    private const string Incomplete = "unavailable:" + PropertySetEvidence.ReadIncomplete;

    private static PropertySetEvidence.Set PSet(string? name, params (string? Name, string? Value)[] properties) =>
        new(name, properties);

    private static List<(string Name, string Value)> Items(EvidenceValue value)
    {
        using var document = JsonDocument.Parse(value.Json!);
        return document.RootElement.EnumerateArray()
            .Select(item => (item.GetProperty("name").GetString()!, item.GetProperty("value").GetString()!)).ToList();
    }

    private static Dictionary<string, string> Parameters(EvidenceValue value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        EvidenceJson.Write(parameters, EvidenceKeys.Schema, EvidenceValue.Read(EvidenceKeys.SchemaV1));
        EvidenceJson.Write(parameters, EvidenceKeys.PsetComponent, value);
        return parameters;
    }

    private static NeutralQuantityRecord Record(string id, EvidenceValue value) => new()
    {
        RecordId = id, ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
        Source = new QuantitySource { Drawing = "s.dwg", DrawingHash = new string('d', 64), Handle = id, EntityType = "Hatch",
            Layer = "asdasd23423" },
        Measurement = new QuantityMeasurement { Kind = "area", Method = "hatch-area", RawValue = 10, Unit = "מ\"ר",
            Parameters = Parameters(value) },
    };

    [Fact]
    public void NoSetIsAbsentButAnUnreadSetOrPropertyIsNeverAbsentNorAComplete()
    {
        PropertySetEvidence.Build(Array.Empty<PropertySetEvidence.Set>()).Status.Should().Be("absent");
        PropertySetEvidence.Build(Array.Empty<PropertySetEvidence.Set>(), unreadSets: 1).Status
            .Should().Be(Incomplete, "a set that was found but could not be read may say anything");
        var partial = PropertySetEvidence.Build(new[] { PSet("Pavement", ("Material", "אספלט")), PSet("Asset", ("Code", "RD-17")) },
            unreadSets: 1);
        partial.Status.Should().Be(Incomplete);
        partial.Json.Should().BeNull("what was read next to a failed set is never offered as evidence");
        PropertySetEvidence.Build(new[]
            {
                new PropertySetEvidence.Set("Pavement", new (string?, string?)[] { ("Material", "אספלט") }, UnreadProperties: 1),
            })
            .Status.Should().Be(Incomplete);
        PropertySetEvidence.Build(new[] { PSet("  ", ("Material", "אספלט")) }).Status
            .Should().Be(Incomplete, "a set without a readable name is a failed read");
        PropertySetEvidence.Build(new[] { PSet("Pavement", (null, "אספלט")) }).Status
            .Should().Be(Incomplete, "a property without a readable name is a failed read");
    }

    [Fact]
    public void ValuesAreValueTextsTaggedBySetAndPropertyInOneDeterministicOrder()
    {
        var first = PropertySetEvidence.Build(new[]
        {
            PSet("Pavement", ("Thickness", "0.05"), ("Material", "אספלט")),
            PSet("Asset", ("Owner", null), ("Code", "RD-17")),
        });
        var second = PropertySetEvidence.Build(new[]
        {
            PSet("Asset", ("Code", "RD-17"), ("Owner", null)),
            PSet("Pavement", ("Material", "אספלט"), ("Thickness", "0.05")),
        });
        first.Status.Should().Be("read");
        second.Json.Should().Be(first.Json, "the host's order of sets and properties never changes the value");
        Items(first).Should().Equal(("Asset:Code", "RD-17"), ("Pavement:Material", "אספלט"), ("Pavement:Thickness", "0.05"));

        var texts = EvidenceReader.Texts(Parameters(first), EvidenceKeys.PsetComponent);
        texts.Should().NotBeEmpty();
        texts.Should().OnlyContain(text => text.Key == EvidenceKeys.PsetComponent && text.Field == "value");
        texts.Select(text => (text.Tag, text.Text)).Should()
            .Equal(("Asset:Code", "RD-17"), ("Pavement:Material", "אספלט"), ("Pavement:Thickness", "0.05"));
        texts.Select(text => text.Text).Should().NotContain(text => text.Contains("Pavement") || text.Contains("Asset"),
            "a set or property name is the tag of its value, never text of its own");
    }

    [Fact]
    public void ManualValuesBecomeInvariantTextAndAnUnknownValueIsNotRead()
    {
        PropertySetEvidence.TryValueText(null, out var none).Should().BeTrue("a null value is read and says nothing");
        none.Should().BeNull();
        PropertySetEvidence.TryValueText("אספלט", out var text).Should().BeTrue();
        text.Should().Be("אספלט");
        PropertySetEvidence.TryValueText(true, out var flag).Should().BeTrue();
        flag.Should().Be("true");
        PropertySetEvidence.TryValueText(12, out var integer).Should().BeTrue();
        integer.Should().Be("12");
        PropertySetEvidence.TryValueText(0.1 + 0.2, out var real).Should().BeTrue();
        real.Should().Be("0.3");
        PropertySetEvidence.TryValueText(0.00005, out var small).Should().BeTrue();
        small.Should().Be("0.00005", "a real is never written with an exponent (5E-05)");
        PropertySetEvidence.TryValueText(-0.0000001, out var zero).Should().BeTrue();
        zero.Should().Be("0", "rounded like every evidence number, without a negative zero");
        PropertySetEvidence.TryValueText(double.NaN, out _).Should().BeFalse();
        PropertySetEvidence.TryValueText(new DateTime(2026, 9, 27), out _).Should().BeFalse();
        PropertySetEvidence.TryValueText(new object(), out _).Should().BeFalse();

        var blank = PropertySetEvidence.Build(new[] { PSet("Pavement", ("Material", null), ("Note", "  ")) });
        blank.Status.Should().Be("read", "the entity has a set (not absent), and it says nothing");
        blank.Json.Should().Be("[]");
        EvidenceReader.Texts(Parameters(blank), EvidenceKeys.PsetComponent).Should().BeEmpty();
    }

    [Fact]
    public void ADerivedPropertyLeftUnreadMakesTheValuePartialNeverCompleteOrAbsent()
    {
        var partial = PropertySetEvidence.Build(new[]
        {
            new PropertySetEvidence.Set("Pavement", new (string?, string?)[] { ("Material", "אספלט") }, DerivedProperties: 2),
        });
        partial.Status.Should().Be("truncated:3", "one value was read and two derived properties were observed but not read");
        Items(partial).Should().Equal(("Pavement:Material", "אספלט"));
        CatalogEvidenceBridge.UniformSubjects(new[] { Record("d1", partial), Record("d2", partial) })
            .Should().BeEmpty("a partial read is never a complete subject");

        var onlyDerived = PropertySetEvidence.Build(new[]
        {
            new PropertySetEvidence.Set("Object", Array.Empty<(string?, string?)>(), DerivedProperties: 3),
        });
        onlyDerived.Status.Should().Be("truncated:3", "the set exists and its derived values were not read: not absent, not complete");
        onlyDerived.Json.Should().Be("[]");
    }

    [Fact]
    public void SetsPropertiesAndValuesBeyondTheCapsAreTruncatedNotDropped()
    {
        var nine = Enumerable.Range(0, 9).Select(i => PSet("Set" + i, ("Material", "value " + i))).ToArray();
        var bySets = PropertySetEvidence.Build(nine);
        bySets.Status.Should().Be("truncated:9");
        Items(bySets).Should().HaveCount(PropertySetEvidence.MaxSets);
        Items(bySets).Select(item => item.Name).Should().NotContain("Set8:Material");

        (string?, string?)[] Properties(int count, string value) => Enumerable.Range(0, count)
            .Select(i => ((string?)("P" + i.ToString("00", CultureInfo.InvariantCulture)), (string?)value)).ToArray();
        var byProperties = PropertySetEvidence.Build(new[] { PSet("Pavement", Properties(40, "v")) });
        byProperties.Status.Should().Be("truncated:40");
        Items(byProperties).Should().HaveCount(PropertySetEvidence.MaxPropertiesPerSet);

        var byValues = PropertySetEvidence.Build(Enumerable.Range(0, 3)
            .Select(s => PSet("S" + s, Properties(30, "v" + s))).ToArray());
        byValues.Status.Should().Be("truncated:90");
        Items(byValues).Should().HaveCount(PropertySetEvidence.MaxValues);
        EvidenceReader.Texts(Parameters(byValues), EvidenceKeys.PsetComponent).Should()
            .HaveCount(PropertySetEvidence.MaxValues, "every kept value is visible to the reader, none silently past its cap");
    }

    [Fact]
    public void ACutValueIsUsableButNeverCompleteAndNeverACatalogSubject()
    {
        var longValue = new string('א', PropertySetEvidence.MaxValueChars + 5);
        var cut = PropertySetEvidence.Build(new[] { PSet("Pavement", ("Material", longValue)) });
        cut.Status.Should().Be("truncated:1");
        using (var document = JsonDocument.Parse(cut.Json!))
        {
            var item = document.RootElement[0];
            item.GetProperty("value").GetString().Should().HaveLength(PropertySetEvidence.MaxValueChars);
            item.GetProperty("value_cut").GetBoolean().Should().BeTrue();
        }
        CatalogEvidenceBridge.UniformSubjects(new[] { Record("c1", cut), Record("c2", cut) })
            .Should().BeEmpty("a cut text may differ in what was cut");
    }

    [Fact]
    public void APlaceholderTemplateValueIsMarkedAndNeverEvidenceWhileARealValueBesideItIs()
    {
        // Codex int-b reproduction: "Pay item for asphalt" reached EvidenceReader and UniformSubjects as a value.
        var mixed = PropertySetEvidence.Build(new[] { PSet("Pavement", ("PayItem", "Pay item for asphalt"), ("Material", "אספלט")) });
        using (var document = JsonDocument.Parse(mixed.Json!))
        {
            var items = document.RootElement.EnumerateArray().ToList();
            items.Single(i => i.GetProperty("name").GetString() == "Pavement:PayItem").GetProperty("placeholder").GetBoolean()
                .Should().BeTrue();
            items.Single(i => i.GetProperty("name").GetString() == "Pavement:Material").TryGetProperty("placeholder", out _)
                .Should().BeFalse("a real value is never marked");
        }
        EvidenceReader.Texts(Parameters(mixed), EvidenceKeys.PsetComponent)
            .Should().ContainSingle().Which.Should().Match<EvidenceText>(t => t.Text == "אספלט" && t.Tag == "Pavement:Material");

        var only = PropertySetEvidence.Build(new[] { PSet("Pavement", ("PayItem", "  pay ITEM for asphalt")) });
        EvidenceReader.Texts(Parameters(only), EvidenceKeys.PsetComponent).Should().BeEmpty();
        CatalogEvidenceBridge.UniformSubjects(new[] { Record("ph1", only), Record("ph2", only) }).Should().BeEmpty();
        CatalogEvidenceBridge.UniformSubjects(new[] { Record("ph3", mixed), Record("ph4", mixed) })
            .Should().ContainSingle().Which.Value.Should().Be("אספלט");
        PropertySetEvidence.IsPlaceholder("Payment item").Should().BeFalse();
    }

    [Fact]
    public void AYesNoValueIsNeverASubjectAndThePropertyIsNamedBesideItsValue()
    {
        // int-b review: a TrueFalse property ("true") admitted an AI family request and could ground its proposal.
        var library = MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1;
        var yesNo = PropertySetEvidence.Build(new[] { PSet("Flags", ("IsNew", "true"), ("Demolish", "FALSE")) });
        var onlyYesNo = new[] { Record("yn1", yesNo), Record("yn2", yesNo) };
        CatalogEvidenceBridge.UniformSubjects(onlyYesNo).Should().BeEmpty();
        var refused = FamilyRecognitionAssist.Prepare(
            MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.RecognitionGroups(onlyYesNo, library).Single(), library, null);
        refused.Request.Should().BeNull("a yes/no says whether, never what the object is");
        refused.AbstentionMessage.Should().StartWith("אין לקבוצה ראיה קריאה");
        EvidenceReader.Texts(Parameters(yesNo), EvidenceKeys.PsetComponent).Should().HaveCount(2, "it stays readable evidence");

        // Positive control: a real value beside the yes/no is a subject, and the engineer sees which property said it.
        var mixed = PropertySetEvidence.Build(new[] { PSet("Pavement", ("Material", "אספלט"), ("IsNew", "true")) });
        var records = new[] { Record("ps1", mixed), Record("ps2", mixed) };
        CatalogEvidenceBridge.UniformSubjects(records).Should().ContainSingle().Which.Value.Should().Be("אספלט");
        var group = MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.RecognitionGroups(records, library).Single();
        FamilyRecognitionAssist.Prepare(group, library, null).Request.Should().NotBeNull();
        LocalFamilyClassifier.Instance.Classify(group, library, null).SelectMany(p => p.Observed)
            .Should().Contain("PropertySet Pavement:Material: אספלט");
        PropertySetEvidence.IsYesNo(" True ").Should().BeTrue();
        PropertySetEvidence.IsYesNo("trueform").Should().BeFalse();
    }

    [Fact]
    public void TheCatalogBridgePicksATextIdenticalOnEveryRecordAndNeverANumericValue()
    {
        // A small real, identical on every record, must still read as a number (never "5E-05").
        PropertySetEvidence.TryValueText(0.00005, out var slope).Should().BeTrue();
        EvidenceValue Pavement(string code) => PropertySetEvidence.Build(new[]
        {
            PSet("Pavement", ("Material", "אספלט"), ("Code", code), ("Layers", "2"), ("Slope", slope)),
        });
        var texts = EvidenceReader.Texts(Parameters(Pavement("RD-17")), EvidenceKeys.PsetComponent);
        texts.Should().Contain(text => text.Tag == "Pavement:Layers" && text.Field == "value" && text.Text == "2",
            "a numeric value is kept, as a value");
        texts.Should().Contain(text => text.Tag == "Pavement:Slope" && text.Text == "0.00005");

        var subjects = CatalogEvidenceBridge.UniformSubjects(new[] { Record("p1", Pavement("RD-17")), Record("p2", Pavement("RD-18")) });
        subjects.Should().ContainSingle().Which.Should().Be(
            new CatalogEvidenceBridge.Subject(EvidenceKeys.PsetComponent, "value", "Pavement:Material", "אספלט", 2));
        subjects[0].Label.Should().Be("PropertySet Pavement:Material");
    }
}
