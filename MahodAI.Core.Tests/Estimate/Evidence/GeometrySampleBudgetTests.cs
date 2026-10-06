using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// SYNTHETIC records. Geometry previews are budgeted per recognition group — the groups the engineer decides —
/// so a group is never left without a preview because another method on the same layer was drawn first.
/// </summary>
public sealed class GeometrySampleBudgetCollectorFixTests
{
    private static int _next;

    private static NeutralQuantityRecord BudgetFixRecord(string kind, string method, string unit, string layer,
        string? xref = null, string? block = null)
    {
        var parameters = new Dictionary<string, string>();
        if (block != null) parameters[EvidenceKeys.BlockNameEffective] = block;
        EvidenceJson.Write(parameters, EvidenceKeys.GeometrySample, EvidenceValue.Unavailable("sample-budget"));
        var id = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new NeutralQuantityRecord
        {
            RecordId = "q-" + id, ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('b', 64), Handle = id,
                EntityType = "X", Layer = layer, Xref = xref,
            },
            Measurement = new QuantityMeasurement
            {
                Kind = kind, Method = method, RawValue = 10, Unit = unit, Parameters = parameters,
            },
            Classification = new QuantityClassification(),
        };
    }

    private static EvidenceValue BudgetFixLine() =>
        EvidenceJson.GeometrySample(EvidenceShape.Open(new (double X, double Y)[] { (0, 0), (3, 4) }));

    private static bool HasSample(NeutralQuantityRecord record) =>
        EvidenceReader.Status(record.Measurement.Parameters, EvidenceKeys.GeometrySample).Usable;

    [Fact]
    public void HatchesDrawnAfterTheirBoundaryPolylinesStillGetTheirOwnPreview()
    {
        const string layer = "asdasd23423";
        var records = new List<NeutralQuantityRecord>();
        // Model-space order: every boundary polyline first, then the hatches, then lines of the same layer.
        for (var i = 0; i < 5; i++) records.Add(BudgetFixRecord("area", "closed-polyline-area", "מ\"ר", layer));
        for (var i = 0; i < 4; i++) records.Add(BudgetFixRecord("area", "hatch-area", "מ\"ר", layer));
        for (var i = 0; i < 4; i++) records.Add(BudgetFixRecord("length", "polyline-length", "מ'", layer));
        records.Add(BudgetFixRecord("length", "closed-polyline-perimeter", "מ'", layer));
        records.Add(BudgetFixRecord("count", "block-count", "יח'", layer, block: "Q7X1"));
        records.Add(BudgetFixRecord("count", "block-count", "יח'", layer, block: "Z2P9"));
        records.Add(BudgetFixRecord("area", "hatch-area", "מ\"ר", "SYNTHETIC-GM|" + layer, xref: "SYNTHETIC-GM"));

        var budget = new GeometrySampleBudget();
        foreach (var record in records) budget.Offer(record, BudgetFixLine);

        var groups = EngineerBoqDraftBuilder.RecognitionGroups(records, EngineerBoqLibrary.RoadsV1);
        groups.Should().HaveCount(7);
        foreach (var group in groups)
        {
            group.Records.Count(HasSample).Should().BeInRange(1, GeometrySampleBudget.DefaultPerGroup,
                $"group {group.GroupId} is decided by the engineer and needs its own preview");
            GroupPreviewRenderer.Samples(group.Records).Should().NotBeEmpty();
            foreach (var record in group.Records)
                GeometrySampleBudget.GroupKey(record).Should().Be(group.GroupId, "the budget key is the recognition group");
        }
        budget.GroupsWithSample.Should().Be(7);
        budget.Emitted.Should().Be(3 + 3 + 3 + 1 + 1 + 1 + 1);

        // The old key (drawing, xref, layer, kind) gave all 3 host area samples to the boundary polylines.
        records.Where(r => r.Measurement.Method == "hatch-area" && r.Source.Xref == null).Count(HasSample).Should().Be(3);
    }

    [Fact]
    public void AnUnreadableSampleDoesNotUseUpItsGroupsBudget()
    {
        var budget = new GeometrySampleBudget(perGroup: 1);
        var region = BudgetFixRecord("area", "hatch-area", "מ\"ר", "L1");
        var hatch = BudgetFixRecord("area", "hatch-area", "מ\"ר", "L1");
        var third = BudgetFixRecord("area", "hatch-area", "מ\"ר", "L1");

        budget.Offer(region, () => EvidenceValue.Unavailable("approximate-geometry:hatch-loop-unreadable")).Should().BeFalse();
        region.Measurement.Parameters[EvidenceKeys.GeometrySample + "_status"]
            .Should().Be("unavailable:approximate-geometry:hatch-loop-unreadable");
        budget.Offer(hatch, BudgetFixLine).Should().BeTrue();
        HasSample(hatch).Should().BeTrue();
        budget.Offer(third, () => throw new InvalidOperationException("never built")).Should().BeFalse();
        third.Measurement.Parameters[EvidenceKeys.GeometrySample + "_status"].Should().Be("unavailable:sample-budget",
            "a full group leaves the record's status untouched and never builds its sample");
        budget.Emitted.Should().Be(1);

        var failing = BudgetFixRecord("length", "polyline-length", "מ'", "L1");
        budget.Offer(failing, () => throw new InvalidOperationException("boom")).Should().BeFalse();
        failing.Measurement.Parameters[EvidenceKeys.GeometrySample + "_status"]
            .Should().Be("unavailable:InvalidOperationException");
    }
}
