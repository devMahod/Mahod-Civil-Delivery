using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// b24 (Codex 12:04 A, repro 728418B3; 12:45 authority): plan geometry is scaled by the physical unit the scan's run
/// resolved — a reviewed unit decision included — and a record counts only when its stamp is self-consistent and equal
/// to its run's verified unit evidence. Before this, a raw-feet host decided as metres measured 10 m but the engine
/// replaced it with 3.048 from the raw INSUNITS chain, and the reverse grew 3.048 m to 10. Synthetic data only.
/// </summary>
public sealed class BoqPhysicalUnitOverrideTests
{
    private const string RulesJson = """
    {
      "schema": "mahod-boq-rules/2", "project": "TEST", "version": "2.1",
      "parameters": [{"id": "road_class", "label": "road", "value": 1}],
      "chapters": [{"id": "10.01", "title": "c"}],
      "measurement": {"length_basis": "plan", "tolerance_m": 0.03, "piece_m": 0.25, "parallel_sin": 0.05},
      "boq": [
        {"id": "K1", "chapter": "10.01", "item": "10.01.0010", "unit": "m", "parts": [
          {"label": "kerb", "src": ["A"], "layers": ["KERB"], "kind": "length", "object_width_m": 0.23, "object_width_note": "two faces"}]}
      ]
    }
    """;

    private static readonly string DigestA = new('a', 64), DigestB = new('b', 64);

    public sealed record Stamp(string? Authority, int? Code, string? Factor, string? Digest);

    private static Dictionary<string, string> Chain(string raw, Stamp? stamp, string handle = "A1")
    {
        var p = new Dictionary<string, string>
        {
            [QuantityPhysicalUnits.RawUnitsKey] = raw,
            [QuantityGeometryEvidence.SegmentsKey] = "0,0;10,0",
            [QuantityGeometryEvidence.SegmentsStatusKey] = QuantityGeometryEvidence.StatusComplete,
            [QuantityGeometryEvidence.SegmentsClosedKey] = "false",
            [QuantityGeometryEvidence.PlanChainKey] = "0,0;10,0",
            [QuantityGeometryEvidence.PlanChainStatusKey] = QuantityGeometryEvidence.StatusComplete,
            [QuantityGeometryEvidence.PlanChainClosedKey] = "false",
        };
        if (stamp?.Authority != null) p[QuantityPhysicalUnits.AuthorityKey] = stamp.Authority;
        if (stamp?.Code != null) p[QuantityPhysicalUnits.UnitCodeKey] = stamp.Code.Value.ToString();
        if (stamp?.Factor != null) p[QuantityPhysicalUnits.MetresPerUnitKey] = stamp.Factor;
        if (stamp?.Digest != null) p[QuantityPhysicalUnits.DigestKey] = stamp.Digest;
        return p;
    }

    private static NeutralQuantityRecord Line(Dictionary<string, string> parameters, double si, string handle = "A1", string unit = "מטר") => new()
    {
        RecordId = $"q-{handle}-length", ProjectProfileId = "p", RunId = "run",
        Source = new QuantitySource { Drawing = "x-A-.dwg", DrawingHash = new string('b', 64), Handle = handle, EntityType = "LINE", Layer = "KERB" },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "line-length", RawValue = si, Unit = unit,
            GeometryEvidence = new[] { 0.0, 0.0, 10.0, 0.0 }, Parameters = parameters,
        },
    };

    private static ScanUnitEvidence Bound(int raw, int effective, double factor, string authority, string? digest) =>
        ScanUnitEvidence.Bound(new ScanPhysicalUnits(true, raw, effective, factor, authority, digest));

    private static (BoqInputSet Input, BoqEngineResult Result) Run(ScanUnitEvidence units, params NeutralQuantityRecord[] records)
    {
        var rules = BoqRuleset.Parse(RulesJson);
        var input = BoqNeutralRecordAdapter.Build(rules, new[]
        {
            new BoqNeutralRecordAdapter.SourceScan("A", "run-a", @"C:\synthetic-only\x-A-.dwg", new string('b', 64),
                records, Array.Empty<DeliveryFinding>()) { Units = units },
        });
        return (input, BoqRulesEngine.Run(rules, input));
    }

    public static IEnumerable<object[]> PositiveCases() => new[]
    {
        // raw feet decided metres (repro 1) and raw metres decided feet (repro 2)
        new object[] { "Feet", Bound(2, 6, 1.0, "approved-different-unit", DigestA), new Stamp("approved-different-unit", 6, "1", DigestA), 10.0, 10.0 },
        new object[] { "Meters", Bound(6, 2, 0.3048, "approved-different-unit", DigestA), new Stamp("approved-different-unit", 2, "0.3048", DigestA), 3.048, 3.048 },
        // explicit feet without a decision; feet confirmed by a decision
        new object[] { "Feet", Bound(2, 2, 0.3048, "explicit-insunits", null), new Stamp("explicit-insunits", 2, "0.3048", null), 3.048, 3.048 },
        new object[] { "Feet", Bound(2, 2, 0.3048, "approved-recorded-unit", DigestA), new Stamp("approved-recorded-unit", 2, "0.3048", DigestA), 3.048, 3.048 },
    };

    [Theory]
    [MemberData(nameof(PositiveCases))]
    public void ThePrimaryQuantityUsesTheRunsPhysicalFactor(string raw, ScanUnitEvidence units, Stamp stamp, double si, double expected)
    {
        var record = Line(Chain(raw, stamp), si);
        BoqNeutralRecordAdapter.ScanUnitRefusal(units, new[] { record }).Should().BeNull();
        var (input, result) = Run(units, record);
        input.LengthGeometry[("A", "A1")].Segments.Sum(s => s.Length).Should().BeApproximately(expected, 1e-9);
        var kerb = result.Parts.Single(p => p.LineId == "K1");
        kerb.Base.Should().BeApproximately(expected, 1e-9, "the engine replaces the base by the measured plan length");
        kerb.DrawnSum.Should().BeApproximately(si, 1e-9);
        kerb.NoGeometry.Should().Be(0);
        input.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Feet", 3.048, 3.048)]       // a scan proven to predate the contract: its raw INSUNITS
    [InlineData("Undefined", 10.0, 10.0)]    // old unitless host declared metres
    public void APositivelyIdentifiedLegacyScanKeepsItsRawRoute(string raw, double si, double expected)
    {
        var (input, result) = Run(ScanUnitEvidence.Legacy("synthetic: written before the unit contract"), Line(Chain(raw, null), si));
        input.LengthGeometry[("A", "A1")].Segments.Sum(s => s.Length).Should().BeApproximately(expected, 1e-9);
        result.Parts.Single(p => p.LineId == "K1").Base.Should().BeApproximately(expected, 1e-9);
    }

    public static IEnumerable<object[]> RefusedCases() => new[]
    {
        // a new (Bound) scan whose stamps were all removed — never legacy
        new object[] { "stripped", Bound(2, 6, 1.0, "approved-different-unit", DigestA), new[] { Line(Chain("Feet", null), 10.0) } },
        // two host records of one run that disagree
        new object[] { "mixed", Bound(2, 6, 1.0, "approved-different-unit", DigestA), new[]
        {
            Line(Chain("Feet", new Stamp("approved-different-unit", 6, "1", DigestA)), 10.0, "A1"),
            Line(Chain("Feet", new Stamp("explicit-insunits", 2, "0.3048", null), "A2"), 3.048, "A2"),
        } },
        // the same factor under another decision digest
        new object[] { "foreign digest", Bound(2, 6, 1.0, "approved-different-unit", DigestA),
            new[] { Line(Chain("Feet", new Stamp("approved-different-unit", 6, "1", DigestB)), 10.0) } },
        // a factor that is not the decided unit's standard factor
        new object[] { "factor not the decision's", Bound(2, 6, 1.0, "approved-different-unit", DigestA),
            new[] { Line(Chain("Feet", new Stamp("approved-different-unit", 6, "0.3048", DigestA)), 10.0) } },
        // unresolved authority carrying a factor
        new object[] { "unresolved + factor", Bound(2, 6, 1.0, "approved-different-unit", DigestA),
            new[] { Line(Chain("Feet", new Stamp("review-needed", 6, "1", null)), 10.0) } },
        // the run's own evidence is inconsistent (approved without a digest)
        new object[] { "inconsistent run", Bound(2, 6, 1.0, "approved-different-unit", null),
            new[] { Line(Chain("Feet", new Stamp("approved-different-unit", 6, "1", null)), 10.0) } },
        // unknown run evidence — unread, unlisted or unrecognised
        new object[] { "unknown run", ScanUnitEvidence.Unknown("synthetic: header not read"),
            new[] { Line(Chain("Feet", new Stamp("explicit-insunits", 2, "0.3048", null)), 3.048) } },
        // a legacy run whose records carry new stamps
        new object[] { "legacy with stamps", ScanUnitEvidence.Legacy("synthetic"),
            new[] { Line(Chain("Feet", new Stamp("explicit-insunits", 2, "0.3048", null)), 3.048) } },
    };

    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void EvidenceThatDoesNotHoldRefusesTheScanAndGivesNoGeometry(string _, ScanUnitEvidence units, NeutralQuantityRecord[] records)
    {
        BoqNeutralRecordAdapter.ScanUnitRefusal(units, records).Should().NotBeNull();
        var (input, _) = Run(units, records);
        input.LengthGeometry.Should().NotContainKey(("A", "A1"));
        input.Geometry.Should().BeEmpty("a refused scan contributes no classification geometry either");
        input.Warnings.Should().Contain(w => w.Contains("נדרשת סריקה חדשה") || w.Contains("לא הוכרעו"));
    }

    [Fact]
    public void ARecordStampIsAcceptedOnlyWhenSelfConsistent()
    {
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", new Stamp(null, null, "7", null)), false).Should().BeNull("a bare factor is no authority");
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", new Stamp("review-needed", 6, "1", null)), false).Should().BeNull();
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", new Stamp("approved-different-unit", 6, "1", null)), false).Should().BeNull("a decision needs its digest");
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", new Stamp("explicit-insunits", 2, "0.3048", DigestA)), false).Should().BeNull("no decision, no digest");
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", new Stamp("approved-different-unit", 6, "0.3048", DigestA)), false).Should().BeNull("not metres' factor");
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", new Stamp("approved-different-unit", 6, "1", DigestA)), false).Should().Be(1.0);
        QuantityPhysicalUnits.MetresPerUnit(Chain("Feet", null), false).Should().Be(0.3048, "a record from before the contract keeps its raw unit");
    }

    [Fact]
    public void TheRunHeaderIdentifiesTheContractPositively()
    {
        var bound = new ScanPhysicalUnits(true, 2, 6, 1.0, "approved-different-unit", DigestA);
        var legacyFeet = new ScanPhysicalUnits(true, 2, 2, 0.3048, null, null);
        var legacyUnitless = new ScanPhysicalUnits(true, 0, 6, 1.0, null, DigestA);   // a unitless host declared metres
        ScanUnitEvidence.FromHeader(true, ScanUnitEvidence.Contract, bound, true, true).State.Should().Be(ScanUnitEvidenceState.Bound);
        ScanUnitEvidence.FromHeader(false, null, legacyFeet, false, false).State.Should().Be(ScanUnitEvidenceState.Legacy);
        ScanUnitEvidence.FromHeader(false, null, legacyUnitless, false, false).State.Should().Be(ScanUnitEvidenceState.Legacy);
        // Present but malformed is not "never written" (Codex 13:21): an authority or a contract of any value or type.
        ScanUnitEvidence.FromHeader(false, null, legacyFeet, authorityPresent: true, authorityIsText: false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(false, null, legacyFeet, authorityPresent: true, authorityIsText: true).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(contractPresent: true, null, legacyFeet, false, false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(true, "scan-physical-units/9", bound, true, true).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(true, ScanUnitEvidence.Contract, bound, true, authorityIsText: false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        // Pre-contract units must be coherent under the historical contract.
        ScanUnitEvidence.FromHeader(false, null, legacyFeet with { IsSupported = false }, false, false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(false, null, legacyFeet with { LinearToMetres = 7 }, false, false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(false, null, legacyFeet with { EffectiveUnitCode = 6, LinearToMetres = 1 }, false, false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(false, null, legacyUnitless with { DeclarationDigest = null }, false, false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        ScanUnitEvidence.FromHeader(false, null, null, false, false).State.Should().Be(ScanUnitEvidenceState.Unknown);
        // An in-memory scan without the contract has no verified reader behind it.
        ScanUnitEvidence.OfScan(null, PhysicalDrawingUnitPolicy.Resolve(2, null, null, null)).State.Should().Be(ScanUnitEvidenceState.Unknown);
    }

    [Fact]
    public void ACadRecordThatLostAllItsUnitEvidenceStillRefusesTheScan()
    {
        // Codex 13:21 P1: two faces of one 10 m kerb, 0.17 apart, width 0.23; raw feet decided metres.
        var units = Bound(2, 6, 1.0, "approved-different-unit", DigestA);
        NeutralQuantityRecord Face(string handle, double y, bool evidence)
        {
            var p = Chain("Feet", evidence ? new Stamp("approved-different-unit", 6, "1", DigestA) : null, handle);
            p[QuantityGeometryEvidence.PlanChainKey] = p[QuantityGeometryEvidence.SegmentsKey] = $"0,{y};10,{y}";
            if (!evidence) p.Remove(QuantityPhysicalUnits.RawUnitsKey);   // stamp and raw unit both gone, CAD chains kept
            return Line(p, 10.0, handle);
        }
        var full = new[] { Face("F1", 0, true), Face("F2", 0.17, true) };
        BoqNeutralRecordAdapter.ScanUnitRefusal(units, full).Should().BeNull();
        var kerb = Run(units, full).Result.Parts.Single(p => p.LineId == "K1");
        kerb.Base.Should().BeApproximately(10.0, 1e-9, "two faces of one object count once");
        kerb.DrawnSum.Should().BeApproximately(20.0, 1e-9);

        var stripped = new[] { Face("F1", 0, false), Face("F2", 0.17, false) };
        BoqNeutralRecordAdapter.ScanUnitRefusal(units, stripped).Should().NotBeNull("missing evidence is never an exemption");
        Run(units, stripped).Input.LengthGeometry.Should().BeEmpty();
        // A truly non-CAD record (no CAD evidence at all) is not a unit-dependent record.
        QuantityPhysicalUnits.IsUnitDependent(new Dictionary<string, string> { ["corridor_volume"] = "1" }).Should().BeFalse();
    }

    [Fact]
    public void ArcDensityFollowsThePhysicalFactor()
    {
        // Codex 12:04: radius 100 du drawn in metres — the raw feet factor gave 1.636 m chords, the physical one ≤ 0.5 m.
        double MaxChord(double metresPerUnit)
        {
            QuantityGeometryEvidence.TessellateCircle(0, 0, 100, metresPerUnit, out var points)
                .Should().Be(QuantityGeometryEvidence.StatusComplete);
            var max = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var (a, b) = (points[i], points[(i + 1) % points.Count]);
                max = Math.Max(max, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
            }
            return max;
        }
        MaxChord(1.0).Should().BeLessThanOrEqualTo(0.5);
        MaxChord(0.3048).Should().BeGreaterThan(0.5, "the raw feet factor under-tessellates a metric drawing");
    }

    [Fact]
    public void DrawnWidthsCountOnlyWhenOneDrawingUnitIsOnePhysicalMetre()
    {
        Dictionary<string, string> Width(string raw, Stamp? stamp)
        {
            var p = Chain(raw, stamp);
            p["cad_polyline_constant_width_raw"] = "0.15";
            return p;
        }
        var draft = typeof(MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder);
        MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.DrawnWidth(
            Width("Feet", new Stamp("approved-different-unit", 6, "1", DigestA))).Should().Be(0.15);
        MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.DrawnWidth(
            Width("Meters", new Stamp("approved-different-unit", 2, "0.3048", DigestA))).Should().BeNull();
        MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.DrawnWidth(
            Width("Meters", new Stamp("review-needed", 6, "1", null))).Should().BeNull();
        MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.DrawnWidth(Width("Meters", null)).Should().Be(0.15);
        MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqDraftBuilder.DrawnWidth(Width("Feet", null)).Should().BeNull();
        draft.Should().NotBeNull();
    }
}
