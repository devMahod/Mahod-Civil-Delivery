using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>SYNTHETIC legend rows and record styles. A legend row is a candidate for what a style means, never a fact.</summary>
public sealed class LegendEvidenceTests
{
    private static readonly string Red = EffectiveColorPolicy.AciToRgbText(1)!;

    private static Dictionary<string, string> Style(string? color, string? entityLinetype, string? layerLinetype = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (color != null)
        {
            parameters[EvidenceKeys.ColorEffective] = color;
            parameters[EvidenceKeys.ColorEffective + EvidenceKeys.StatusSuffix] = "read";
        }
        if (entityLinetype != null) parameters[EvidenceKeys.EntityLinetype] = entityLinetype;
        if (layerLinetype != null) parameters[EvidenceKeys.LayerLinetype] = layerLinetype;
        return parameters;
    }

    private static LegendEntry Row(string text, int aci, string linetype, bool byName = true, string layout = "A1") =>
        new(text, aci, linetype, layout, byName ? "MIKRA" : "TITLE-7", byName);

    private static List<(string Text, string Match)> Rows(EvidenceValue value)
    {
        using var document = JsonDocument.Parse(value.Json!);
        return document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(row => (row.GetProperty("text").GetString()!, row.GetProperty("match").GetString()!)).ToList();
    }

    [Fact]
    public void ADistinctiveLineTypeAndColourPairInANamedLegendIsALinetypeMatch()
    {
        var legend = new[] { Row("קו ביוב מתוכנן", 1, "dashed"), Row("קו מים מתוכנן", 1, "center"), Row("גבול", 3, "dashed") };
        var value = LegendEvidence.ForRecord(legend, null, Style(Red, "ByLayer", "SYNTH-GM|DASHED"));
        value.Status.Should().Be("read");
        Rows(value).Should().Equal(("קו ביוב מתוכנן", "linetype"));
    }

    [Fact]
    public void APlainLineTypeMatchIsOnlyAColourAssociationAndAGuessedLegendOnlyAGuess()
    {
        var legend = new[] { Row("אבן שפה", 1, "continuous"), Row("סימון", 1, "dashed", byName: false) };
        Rows(LegendEvidence.ForRecord(legend, null, Style(Red, "Continuous"))).Should().Equal(("אבן שפה", "color"));
        Rows(LegendEvidence.ForRecord(legend, null, Style(Red, "DASHED"))).Should().Equal(("סימון", "legend-guess"));
    }

    [Fact]
    public void NoLegendIsAbsentButAnUnreadLegendOrStyleIsNeverAbsent()
    {
        LegendEvidence.ForRecord(Array.Empty<LegendEntry>(), null, Style(Red, "DASHED")).Status.Should().Be("absent");
        LegendEvidence.ForRecord(new[] { Row("x", 1, "dashed") }, "legend-time-budget", Style(Red, "DASHED")).Status
            .Should().Be("unavailable:legend-time-budget");
        LegendEvidence.ForRecord(new[] { Row("x", 1, "dashed") }, null, Style(null, "DASHED")).Status
            .Should().Be("unavailable:record-style-unread");
        LegendEvidence.ForRecord(new[] { Row("x", 1, "dashed") }, null, Style(Red, "ByBlock")).Status
            .Should().Be("unavailable:record-style-unread", "a ByBlock line type depends on the INSERT and is not read here");
        LegendEvidence.ForRecord(new[] { Row("x", 3, "dashed") }, null, Style(Red, "DASHED")).Status
            .Should().Be("absent", "a legend exists and no row has this style");
    }

    [Fact]
    public void ATrueColourLegendSampleNeverMatchesByIndexAndRepeatsAcrossSheetsAreOneRow()
    {
        LegendEvidence.ForRecord(new[] { Row("x", -1, "dashed") }, null, Style("0,0,0", "DASHED")).Status.Should().Be("absent");
        var repeated = new[] { Row("קו ביוב", 1, "dashed", layout: "A1"), Row("קו ביוב", 1, "dashed", layout: "A2") };
        Rows(LegendEvidence.ForRecord(repeated, null, Style(Red, "dashed"))).Should().HaveCount(1);
    }

    [Fact]
    public void RowsBeyondTheCapAreTruncatedNotDropped()
    {
        var legend = Enumerable.Range(0, 10).Select(i => Row("row " + i, 1, "dashed")).ToArray();
        var value = LegendEvidence.ForRecord(legend, null, Style(Red, "DASHED"));
        value.Status.Should().Be("truncated:10");
        Rows(value).Should().HaveCount(LegendEvidence.MaxRows);
    }

    [Fact]
    public void TheClassifierReadsALinetypeRowAsStrongEvidenceAndTheBridgeOnlyShapeMatchedRowsAsSubjects()
    {
        var byLinetype = LegendEvidence.ForRecord(new[] { Row("קו ביוב מתוכנן", 1, "dashed") }, null, Style(Red, "DASHED"));
        var byColour = LegendEvidence.ForRecord(new[] { Row("קו ביוב מתוכנן", 1, "continuous") }, null, Style(Red, "Continuous"));
        NeutralQuantityRecord Line(EvidenceValue legend, int n)
        {
            var parameters = Style(Red, "DASHED");
            parameters[EvidenceKeys.Schema] = EvidenceKeys.SchemaV1;
            parameters[EvidenceKeys.Schema + EvidenceKeys.StatusSuffix] = "read";
            EvidenceJson.Write(parameters, EvidenceKeys.LegendRow, legend);
            return new NeutralQuantityRecord
            {
                RecordId = "lg" + n, ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
                Source = new QuantitySource { Drawing = "s.dwg", DrawingHash = new string('d', 64), Handle = "L" + n, EntityType = "Polyline",
                    Layer = "asdasd23423" },
                Measurement = new QuantityMeasurement { Kind = "length", Method = "polyline-length", RawValue = 10, Unit = "מטר",
                    Parameters = parameters },
            };
        }
        CatalogEvidenceBridge.UniformSubjects(new[] { Line(byLinetype, 1), Line(byLinetype, 2) })
            .Should().ContainSingle().Which.Value.Should().Be("קו ביוב מתוכנן");
        CatalogEvidenceBridge.UniformSubjects(new[] { Line(byColour, 3), Line(byColour, 4) })
            .Should().BeEmpty("a colour association narrows; it does not say what the object is");

        var library = EngineerBoqLibrary.RoadsV1;
        var group = EngineerBoqDraftBuilder.RecognitionGroups(new[] { Line(byLinetype, 5) }, library).Single();
        LocalFamilyClassifier.Instance.Classify(group, library, null).Should().ContainSingle().Which.Should()
            .Match<RecognitionProposal>(p => p.Status == RecognitionStatus.Proposed && p.FamilyId == "utility-sewer" &&
                                             p.EvidenceRefs.Any(r => r.Key == EvidenceKeys.LegendRow));
    }
}
