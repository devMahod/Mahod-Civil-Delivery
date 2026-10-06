using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;
using Local = MahodAI.CivilDelivery.Shared.SectionHatchLocalCut;

namespace MahodAI.Core.Tests;

/// <summary>
/// b7 local cut contract (CODEX_B7_CUT_CONTRACT_HE 5368B2AA). Positives are the two real
/// closed self-intersecting HA hatches on their real cuts (raw 29BF85A2, PLAN 14235EC1,
/// HA XREF chain 18F15BC1); everything else must stay refused, Unknown or not Full.
/// </summary>
public sealed class SectionHatchLocalCutTests(ITestOutputHelper output)
{
    private const string RawSha = "29BF85A2549258658A7BEAD9DD365EA9FB5C17A296977E343F9A0CC06A17B4DE";
    private const string CutsSha = "B660BE413C559F72ADF3D59FC45557EBC42ACE0C875197E283D5D3637E1D463C";
    private const string PlanSha = "14235EC1DD8B6EB19F5174DAD3F8FCD18679DE825EF52CE7374BF79C603BE8EA";
    private const string XrefSha = "18F15BC1C210485D873A6201D6402DA968FBED4515BF2E46E16F9E817B046FCD";
    // Stand-in identity for the HA source drawing; the evidence binds whatever is supplied.
    private const string DrawingHash = "8f2469ed00000000000000000000000000000000000000000000000000000000";
    private const string Sidewalk = "מדרכה";
    private static readonly double[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    // ------------------------------------------------------------------ pins

    [Fact]
    public void FixturesArePinnedToTheReviewedReceipts()
    {
        Sha(FixturePath("RECEIPT_HATCHRAW_4.json")).Should().Be(RawSha);
        Sha(FixturePath("cuts-plan-14235.json")).Should().Be(CutsSha);
        var cuts = Cuts();
        cuts.GetProperty("raw_sha256").GetString().Should().Be(RawSha);
        cuts.GetProperty("plan_sha256").GetString().Should().Be(PlanSha);
        cuts.GetProperty("xref_chain_sha256").GetString().Should().Be(XrefSha);
        HaMatrix().Should().Equal(Identity, "the HA XREF 8BB299 is an identity insert in host metres");
        cuts.GetProperty("ha_xref").GetProperty("insunits_host").GetInt32().Should().Be(6);
        cuts.GetProperty("ha_xref").GetProperty("insunits_xref").GetInt32().Should().Be(6);
    }

    // ------------------------------------------------------------------ positives (real data)

    [Theory]
    [InlineData("2623AF", "STA-42157", -15.93843022284773, -13.860906284387985)]
    public void ClosedTargets_AreEligible_AndTheirWholeCutIsDecided(string handle, string section, double inFrom, double inTo)
    {
        Local.TryCreate(Raw(handle), out var source, out var refusal).Should().BeTrue(refusal);
        source!.SelfIntersections.Should().NotBeEmpty();
        var cl = Case(section).GetProperty("cl_wcs").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var proof = Local.Prove(source, new P2(cl[0], cl[1]), new P2(cl[2], cl[3]));
        output.WriteLine($"{handle}/{section}: decided={proof.Decided} coverage={proof.Coverage} reason={proof.Reason}");
        foreach (var part in proof.Parts) output.WriteLine($"  {part.Kind} {part.From:F9}..{part.To:F9}");
        output.WriteLine($"  min self-intersection distance to CL: {source.SelfIntersections.Count} roots");
        proof.Decided.Should().BeTrue(proof.Reason);
        proof.Coverage.Should().Be(Local.Coverage.Partial, "the CL crosses the hatch: inside and outside parts both exist");
        proof.Parts.Count(part => part.Kind == Local.PartKind.Zone).Should().Be(2);
        proof.Parts.Should().OnlyContain(part => part.Kind != Local.PartKind.Zone || part.To - part.From < 1e-7,
            "both crossings are transversal; their uncertainty zones are sub-micrometre");
        var inside = proof.Parts.Single(part => part.Kind == Local.PartKind.Inside);
        (inside.To - inside.From).Should().BeApproximately(inTo - inFrom, 1e-6,
            "the decided inside interval matches the independent analytic receipt (CUT_PROOF_CLOSED 4A1DD122)");
        proof.Parts.First().Kind.Should().Be(Local.PartKind.Outside);
        proof.Parts.Last().Kind.Should().Be(Local.PartKind.Outside);
    }

    [Fact]
    public void A6HasAnExactCollinearRetrace_SoItStaysOnTheFailClosedPath()
    {
        // Edges 100/101: 101 runs 2.356 mm back along 100 (lateral 1.6e-11 m). That is an
        // overlap, not a crossing; the contract refuses it (CUT_PROOF_CLOSED missed it).
        Local.TryCreate(Raw("2623A6"), out var source, out var refusal).Should().BeFalse();
        source.Should().BeNull();
        refusal.Should().Be("edges 100/101 overlap collinearly; overlap is unresolved");
        var region = Region("2623A6", Raw("2623A6"));
        region.Deferred!.LocalCut.Should().BeNull();
        region.Deferred.LocalCutRefusal.Should().Be(refusal);
        var (crossings, analysis) = Presentation("STA-41398");
        var verdicts = new List<SectionHatchSpanLabelService.LocalCutVerdict>();
        var overrides = SectionHatchSpanLabelService.Resolve(new[] { region }, crossings, analysis, verdicts);
        verdicts.Should().BeEmpty();
        overrides.Should().NotContain(o => o.Source == SectionHatchSpanLabelService.LocalCutEvidenceSource);
    }

    [Theory]
    [InlineData("2623AF", "STA-42157", -15.936294235007956, -13.863214130883078)]
    public void Consumer_NamesTheProvenSpanWithTheMappedLabelOnly_AndKeepsEveryConflict(
        string handle, string section, double spanFrom, double spanTo)
    {
        var region = Region(handle, Raw(handle));
        region.Deferred.Should().NotBeNull();
        region.Deferred!.LocalCut.Should().NotBeNull(region.Deferred.LocalCutRefusal);
        region.Loops.Should().BeEmpty("no polygon, area or repaired loop is produced");
        region.Deferred.Loops.Single().Failure.Should().Contain("Self-intersection", "the source failure is preserved");
        var cl = Case(section).GetProperty("cl_wcs").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        SectionHatchSpanLabelService.CompleteForSegment(region, new(cl[0], cl[1]), new(cl[2], cl[3]))
            .Should().BeNull("the unchanged polygon path still refuses this source");

        var (crossings, analysis) = Presentation(section);
        var verdicts = new List<SectionHatchSpanLabelService.LocalCutVerdict>();
        var overrides = SectionHatchSpanLabelService.Resolve(new[] { region }, crossings, analysis, verdicts);
        foreach (var v in verdicts) output.WriteLine($"  verdict {v.From:F6}..{v.To:F6} {v.Coverage} {v.Reason}");
        foreach (var o in overrides) output.WriteLine($"  override {o.Offset:F6} {o.Label} {o.Source}");
        var mid = (spanFrom + spanTo) / 2;
        verdicts.Should().NotContain(v => v.Source == null, "every span endpoint resolves to an actual source point");
        verdicts.Should().NotContain(v => v.Coverage == Local.Coverage.Unknown);
        verdicts.Single(v => v.From == spanFrom && v.To == spanTo).Coverage.Should().Be(Local.Coverage.Full);
        var named = overrides.Where(o => o.Source == SectionHatchSpanLabelService.LocalCutEvidenceSource).ToList();
        named.Should().ContainSingle().Which.Should().Match<SpanLabelOverride>(o =>
            Math.Abs(o.Offset - mid) < 1e-12 && o.Label == Sidewalk && o.Evidence!.Length == 64);
        SectionHatchSpanLabelService.IsRegionEvidence(named[0].Source).Should().BeTrue("APPLY re-reads and re-proves it");
        // Any other whole-span label on that span is kept as an explicit conflict, never overridden.
        var baseline = analysis.StripLabels.Where(l => l.From == spanFrom && l.To == spanTo).Select(l => l.Label).ToList();
        var planned = Case(section).GetProperty("resolved_spans").EnumerateArray()
            .Single(s => s.GetProperty("from").GetDouble() == spanFrom && s.GetProperty("to").GetDouble() == spanTo);
        baseline.Should().Equal(new[] { planned.GetProperty("label").GetString() },
            "the fixture reproduces the PLAN's own whole-span label for this span");
        foreach (var other in baseline.Where(label => label != Sidewalk))
            overrides.Should().Contain(o => o.Label == other && o.Source == "source-hatch-baseline-conflict");
        output.WriteLine($"  baseline labels on the span: {string.Join(",", baseline)}");
    }

    [Fact]
    public void Evidence_IsBoundToTheExactSegmentAndSource_AndIsDeterministic()
    {
        Local.TryCreate(Raw("2623AF"), out var source, out _).Should().BeTrue();
        var a = new P2(203645.6150783442, 649127.0384875104);
        var b = new P2(203647.64309805902, 649126.6086071149);
        var first = Local.Prove(source!, a, b);
        Local.Prove(source!, a, b).Evidence.Should().Be(first.Evidence);
        Local.Prove(source!, a, b with { X = b.X + 1e-6 }).Evidence.Should().NotBe(first.Evidence);
        first.Coverage.Should().Be(Local.Coverage.Full, first.Reason);
        Local.TryCreate(Raw("2623AF") with { SourceIdentity = "other-drawing" }, out var moved, out _).Should().BeTrue();
        Local.Prove(moved!, a, b).Evidence.Should().NotBe(first.Evidence, "a different source identity is recomputed, not reused");
        var raw = Raw("2623AF");
        var edges = raw.Edges.ToList();
        edges[10] = edges[10] with { EndX = Math.BitIncrement(edges[10].EndX) };
        edges[11] = edges[11] with { StartX = edges[10].EndX };
        Local.TryCreate(raw with { Edges = edges }, out var edited, out _).Should().BeTrue();
        edited!.CanonicalSha256.Should().NotBe(source!.CanonicalSha256, "every raw scalar is bound");
    }

    // ------------------------------------------------------------------ eligibility refusals

    [Theory]
    [InlineData("262393")]
    [InlineData("26236C")]
    public void TheOpenAndMultiLoopHatchesStayRefused(string handle)
    {
        Local.TryCreate(Raw(handle), out var source, out var refusal).Should().BeFalse();
        source.Should().BeNull();
        output.WriteLine(refusal);
    }

    public static IEnumerable<object[]> Mutations() => new (string Name, Func<Local.RawLoop, Local.RawLoop> Edit)[]
    {
        ("style Normal", r => r with { HatchStyle = "Normal" }),
        ("style Ignore", r => r with { HatchStyle = "Ignore" }),
        ("self-intersecting flag", r => r with { LoopFlags = "External, SelfIntersecting" }),
        ("not-closed flag", r => r with { LoopFlags = "External, NotClosed" }),
        ("second loop", r => r with { DeclaredLoopCount = 2 }),
        ("incomplete capture", r => r with { CaptureComplete = false }),
        ("polyline loop", r => r with { IsPolyline = true }),
        ("missing native edge", r => r with { NativeEdgeCount = r.Edges.Count + 1 }),
        ("dropped edge leaves a gap", r => r with { Edges = r.Edges.Skip(1).ToList(), NativeEdgeCount = r.Edges.Count - 1 }),
        ("non-finite scalar", r => r with { Edges = r.Edges.Select((e, i) => i == 3 ? e with { EndY = double.NaN } : e).ToList() }),
        ("flipped normal", r => r with { NormalZ = -1 }),
        ("non-similar transform", r => r with { TransformRowMajor = new double[] { 1.0000001, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } }),
        ("non-affine transform", r => r with { TransformRowMajor = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 1e-9, 0, 1 } }),
        ("arc angle disagrees with its endpoints", r => r with { Edges = r.Edges.Select(e => e.Kind == Local.EdgeKind.Arc ? e with { EndAngle = e.EndAngle + 1e-3 } : e).ToList() }),
        ("missing identity", r => r with { SourceIdentity = " " }),
    }.Select(m => new object[] { m.Name, m.Edit });

    [Theory]
    [MemberData(nameof(Mutations))]
    public void EveryEligibilityGateRefuses(string name, Func<Local.RawLoop, Local.RawLoop> edit)
    {
        Local.TryCreate(Raw("2623AF"), out _, out _).Should().BeTrue("the unmodified source is eligible");
        Local.TryCreate(edit(Raw("2623AF")), out var source, out var refusal).Should().BeFalse(name);
        source.Should().BeNull();
        output.WriteLine($"{name}: {refusal}");
    }

    [Fact]
    public void ASimpleLoopIsNotTheSupportedFailure_AndOverlapsAreUnresolved()
    {
        Local.TryCreate(Lines((0, 0), (10, 0), (10, 10), (0, 10)), out _, out var simple).Should().BeFalse();
        simple.Should().Contain("no analytically identified self-intersection");
        // Collinear retrace: (0,0)->(10,0)->(5,0)... overlaps itself.
        Local.TryCreate(Lines((0, 0), (10, 0), (5, 0), (5, 5)), out _, out var collinear).Should().BeFalse();
        collinear.Should().Contain("overlap");
        // Two coincident arcs on one circle with overlapping sweeps.
        var arcs = new List<Local.RawEdge>
        {
            Arc(0, 0, 5, 0, 2, false), Arc(0, 0, 5, -2, 1, true),
            new(Local.EdgeKind.Line, Polar(5, 1).X, Polar(5, 1).Y, 5, 0),
        };
        Local.TryCreate(Loop(arcs), out _, out var coincident).Should().BeFalse();
        coincident.Should().Contain("coincident");
    }

    // ------------------------------------------------------------------ proof negatives

    [Fact]
    public void CodexShallowCrossingCounterexample_IsNeverFull()
    {
        // Codex 05:10: the first edge crosses y=0 at x=40 with sin~8e-10. An angle threshold
        // dismissed it and called the whole CL inside. The crossing must be kept.
        Local.TryCreate(Lines((-100, -1.12e-7), (100, 4.8e-8), (100, 10), (-100, 12), (100, 12), (-100, 10)),
            out var source, out var refusal).Should().BeTrue(refusal);
        var whole = Local.Prove(source!, new(-80, 0), new(80, 0));
        foreach (var part in whole.Parts) output.WriteLine($"  {part.Kind} {part.From:F6}..{part.To:F6}");
        whole.Coverage.Should().NotBe(Local.Coverage.Full);
        whole.Coverage.Should().Be(Local.Coverage.Partial);
        var zone = whole.Parts.Single(part => part.Kind == Local.PartKind.Zone);
        (zone.From - 80).Should().BeLessThan(40);
        (zone.To - 80).Should().BeGreaterThan(40, "the uncertainty zone contains the true crossing at x=40");
        // The edge runs within the coverage tolerance of y=0 for |x-40| <= 12.5: no Full there.
        Local.Prove(source!, new(55, 0), new(80, 0)).Coverage.Should().Be(Local.Coverage.None);
        Local.Prove(source!, new(-80, 0), new(25, 0)).Coverage.Should().Be(Local.Coverage.Full);
        Local.Prove(source!, new(-80, 0), new(38, 0)).Coverage.Should().NotBe(Local.Coverage.Full, "contact, not coverage");
        Local.Prove(source!, new(-80, 0), new(39, 0)).Coverage.Should().NotBe(Local.Coverage.Full);
        Local.Prove(source!, new(30, 0), new(50, 0)).Coverage.Should().NotBe(Local.Coverage.Full);
        Local.Prove(source!, new(42, 0), new(80, 0)).Coverage.Should().NotBe(Local.Coverage.None, "contact is not a decided absence");
    }

    // ------------------------------------------------------------------ numeric domain (Codex 09:40 / candidate 551C5BF7)

    [Fact]
    public void AfProofIsBitIdenticalToB7_TheNumericGateChangesNothingInsideItsDomain()
    {
        // Golden values computed with the b7 kernel (09CBE588) before the precision gate.
        Local.TryCreate(Raw("2623AF"), out var source, out var refusal).Should().BeTrue(refusal);
        source!.CanonicalSha256.Should().Be("0a06efe52f45000bb4aec6c6b92eb31bd7a5b7482d34cc8d7d938f6d3c65997f");
        source.SelfIntersections.Select(r => FormattableString.Invariant($"{r.EdgeA}/{r.EdgeB}:{r.Point.X:R},{r.Point.Y:R}:{r.UncertaintyM:R}"))
            .Should().Equal("0/20:203646.24606125374,649194.3889789218:3.002799086232889E-09");
        var cl = Case("STA-42157").GetProperty("cl_wcs").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Local.Prove(source, new(cl[0], cl[1]), new(cl[2], cl[3])).Evidence.Should()
            .Be("f1d377ed48913fe9175b102e23ab5d75060e124ea8e13ed27f7b188ff82ddf44");
        Local.Prove(source, new(203645.6150783442, 649127.0384875104), new(203647.64309805902, 649126.6086071149))
            .Evidence.Should().Be("b4263f451ae6749c41bb2b088e0dc67034bba460b00f9652fdac768af4e0e332");
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1e5)]
    [InlineData(1e6)]
    public void TranslatedCounterexample_InsideTheDomain_KeepsTheRoot(double ty)
    {
        Local.TryCreate(Translated(CounterexampleLoop(), ty), out var source, out var refusal).Should().BeTrue(refusal);
        var proof = Local.Prove(source!, new(-80, ty), new(80, ty));
        proof.Coverage.Should().Be(Local.Coverage.Partial);
        var zone = proof.Parts.Single(part => part.Kind == Local.PartKind.Zone);
        (zone.From - 80).Should().BeLessThan(40);
        (zone.To - 80).Should().BeGreaterThan(40, "the zone must still contain the true crossing at x=40");
    }

    [Theory]
    [InlineData(1e7)]
    [InlineData(1e8)]
    [InlineData(1e9)]
    [InlineData(1e10)]
    public void TranslatedCounterexample_OutsideTheDomain_IsRefusedNeverFull(double ty)
    {
        // Codex 09:40: Y=1e9 rounded the -1.12e-7 offsets away and produced a false Full over 160 m.
        Local.TryCreate(Translated(CounterexampleLoop(), ty), out var source, out var refusal).Should().BeFalse();
        source.Should().BeNull();
        refusal.Should().Contain("numeric precision domain");
    }

    [Fact]
    public void LargeRawWithCancellingTranslation_AndALargeCut_AreNotDecided()
    {
        var shifted = CounterexampleLoop();
        shifted = shifted with { Edges = shifted.Edges.Select(e => e with { StartY = e.StartY + 1e9, EndY = e.EndY + 1e9 }).ToList() };
        Local.TryCreate(Translated(shifted, -1e9), out _, out var refusal).Should().BeFalse("large raw operands cancel only in the result");
        refusal.Should().Contain("numeric precision domain");
        Local.TryCreate(CounterexampleLoop(), out var source, out _).Should().BeTrue();
        var far = Local.Prove(source!, new(-80, 2e6), new(80, 2e6));
        far.Decided.Should().BeFalse();
        far.Coverage.Should().Be(Local.Coverage.Unknown, "a cut outside the domain is Unknown, not a thrown exception or Full");
    }

    private static Local.RawLoop CounterexampleLoop() =>
        Lines((-100, -1.12e-7), (100, 4.8e-8), (100, 10), (-100, 12), (100, 12), (-100, 10));

    private static Local.RawLoop Translated(Local.RawLoop raw, double ty) =>
        raw with { TransformRowMajor = new[] { 1d, 0, 0, 0, 0, 1, 0, ty, 0, 0, 1, 0, 0, 0, 0, 1 } };

    [Theory]
    [InlineData("collinear with an edge", 10, -5, 10, 15)]
    [InlineData("through two vertices", -1, 10, 11, 10)]
    [InlineData("through the self-intersection", -1, 5, 11, 5)]
    [InlineData("contact within the coverage tolerance", 10.000000005, 2, 10.000000005, 8)]
    [InlineData("vertex within the recognition tolerance", 10.0000005, -1, 10.0000005, 11)]
    public void DegenerateContactsStayUnknown(string name, double x0, double y0, double x1, double y1)
    {
        Local.TryCreate(Bowtie(), out var source, out var refusal).Should().BeTrue(refusal);
        var proof = Local.Prove(source!, new(x0, y0), new(x1, y1));
        output.WriteLine($"{name}: {proof.Reason}");
        proof.Decided.Should().BeFalse(name);
        proof.Coverage.Should().Be(Local.Coverage.Unknown);
    }

    [Fact]
    public void ACleanCutOfTheBowtieIsDecided_AndBeyondTheToleranceIsNone()
    {
        Local.TryCreate(Bowtie(), out var source, out _).Should().BeTrue();
        var lobe = Local.Prove(source!, new(1, 3), new(1, 7));
        lobe.Coverage.Should().Be(Local.Coverage.Full, lobe.Reason);
        var across = Local.Prove(source!, new(-1, 2), new(11, 2));
        across.Decided.Should().BeTrue(across.Reason);
        across.Coverage.Should().Be(Local.Coverage.Partial);
        Local.Prove(source!, new(10.001, -1), new(10.001, 11)).Coverage.Should().Be(Local.Coverage.None);
        Local.Prove(source!, new(1, 3), new(1, 3 + 1e-8)).Coverage.Should().Be(Local.Coverage.Unknown, "an empty proof is not clean");
    }

    [Fact]
    public void TangentArcStaysUnknown()
    {
        // Bowtie whose right edge is a semicircle bulging to x=15 (centre (10,5), r=5).
        var edges = new List<Local.RawEdge>
        {
            new(Local.EdgeKind.Line, 0, 0, 10, 10),
            Arc(10, 5, 5, -Math.PI / 2, Math.PI, true),
            new(Local.EdgeKind.Line, 10, 0, 0, 10),
            new(Local.EdgeKind.Line, 0, 10, 0, 0),
        };
        Local.TryCreate(Loop(edges), out var source, out var refusal).Should().BeTrue(refusal);
        var tangent = Local.Prove(source!, new(15, -1), new(15, 11));
        output.WriteLine(tangent.Reason);
        tangent.Coverage.Should().Be(Local.Coverage.Unknown);
        Local.Prove(source!, new(14, -1), new(14, 11)).Decided.Should().BeTrue("a transversal cut of the same arc is decided");
    }

    [Fact]
    public void ParityAndWindingDisagreement_IsUndecided()
    {
        // Pentagram: the central pentagon has winding 2 (non-zero inside) but even parity.
        var star = Enumerable.Range(0, 5).Select(k => Polar(10, Math.PI / 2 + k * 4 * Math.PI / 5)).ToArray();
        Local.TryCreate(Lines(star.Select(p => (p.X, p.Y)).ToArray()), out var source, out var refusal).Should().BeTrue(refusal);
        var proof = Local.Prove(source!, new(-1, -0.5), new(1, -0.5));
        output.WriteLine(proof.Reason);
        proof.Decided.Should().BeFalse();
        proof.Coverage.Should().Be(Local.Coverage.Unknown);
        proof.Parts.Should().Contain(part => part.Kind == Local.PartKind.Undecided);
    }

    // ------------------------------------------------------------------ failure scope

    [Fact]
    public void OnlyTheExactProvenFindingStopsBlocking_UnrelatedFailuresStillBlock()
    {
        var region = Finding("plan-region"); var utility = Finding("projected-utility");
        var cut = new SectionProjectionFailureScope.Cut("r1", new[] { 0d, 5, 10, 5 }, UnresolvedSpanCount: 3)
        { LocallyProvenFindingIds = new[] { region.FindingId } };
        var plan = SectionProjectionFailureScope.ForPlan(region, new[] { cut });
        plan.Severity.Should().Be(FindingSeverity.Warning);
        plan.Code.Should().Be(SectionFindingCodes.ProjectionRegionLocalCutProven);
        plan.AffectedRecordIds.Should().Equal("r1");
        var other = SectionProjectionFailureScope.ForPlan(utility, new[] { cut });
        other.Severity.Should().Be(FindingSeverity.Error, "a proof for one source never waives another");
        other.AffectedRecordIds.Should().Equal("r1");
        var second = new SectionProjectionFailureScope.Cut("r2", new[] { 0d, 6, 10, 6 }, UnresolvedSpanCount: 3);
        var mixed = SectionProjectionFailureScope.ForPlan(region, new[] { cut, second });
        mixed.Severity.Should().Be(FindingSeverity.Error);
        mixed.AffectedRecordIds.Should().Equal(new[] { "r2" }, "a cut without its own proof stays blocked");
        var local = SectionProjectionFailureScope.ForLocalCut(region, "r1", new string('a', 64));
        local.Severity.Should().Be(FindingSeverity.Warning);
        local.Message.Should().Be(region.Message, "the source failure text is preserved");
        local.EvidenceRefs.Should().Contain($"{Local.Method}:{new string('a', 64)}");
    }

    [Fact]
    public void PlanAndApplyUseTheOneSharedLocalCutRule()
    {
        var plan = PluginSource("SectionPlanService.cs");
        plan.Should().Contain("LocallyProvenFailures(record, affectingProjectionFailures, projectable, profile)")
            .And.Contain("!locallyProven.ContainsKey(f.FindingId)")
            .And.Contain("SectionHatchSpanLabelService.Resolve(projectable.PlanRegions, merged, analysis, verdicts)");
        var apply = PluginSource("SectionDecorationService.cs");
        apply.Should().Contain("SectionPlanService.LocallyProvenFailures(target, affecting, collected, profile)")
            .And.Contain("!locallyProven.ContainsKey(f.FindingId)")
            .And.Contain("SectionProjectionFailureScope.ForLocalCut(f, target.RecordId, proof)");
        apply.IndexOf("ForLocalCut(f, target.RecordId, proof)", StringComparison.Ordinal)
            .Should().BeLessThan(apply.IndexOf("if (failures.Count == 0) continue;", StringComparison.Ordinal),
                "the apply-side warning is recorded before the blocking decision");
        PluginSource("SectionGeometryCollector.cs").Should()
            .Contain("{loop.Failure}\", loop.Bounds)", "the failure message itself is unchanged")
            .And.Contain("result.LocalCutSources[finding.FindingId] = partial.LocalCut;");
    }

    // ------------------------------------------------------------------ helpers

    private static string PluginSource(string name, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..",
            "MahodAI.Civil3D.Plugin", "CivilDelivery", "Sections", "Services", name)));

    private static DeliveryFinding Finding(string role) => new()
    {
        Code = SectionFindingCodes.ProjectionGeometryUnsupported, Domain = "sections", Severity = FindingSeverity.Error,
        Title = "t", Message = $"role={role}; reason=source-region=partial", ProjectionRole = role,
        SourceBoundsWcs = new[] { 0d, 0, 10, 10 },
    };

    private static Local.RawLoop Bowtie() => Lines((0, 0), (10, 10), (10, 0), (0, 10));

    private static Local.RawLoop Lines(params (double X, double Y)[] points) =>
        Loop(points.Select((p, i) => new Local.RawEdge(Local.EdgeKind.Line, p.X, p.Y,
            points[(i + 1) % points.Length].X, points[(i + 1) % points.Length].Y)).ToList());

    private static Local.RawLoop Loop(IReadOnlyList<Local.RawEdge> edges) => new(edges, edges.Count, 1, 0, true, false,
        "External", "Outer", 0, 0, 1, 0, Identity, "test-loop");

    private static P2 Polar(double r, double angle) => new(r * Math.Cos(angle), r * Math.Sin(angle));

    /// <summary>A native-style arc: angles from +X, counter-clockwise unless clockwise.</summary>
    private static Local.RawEdge Arc(double cx, double cy, double r, double startAngle, double sweep, bool clockwise)
    {
        var dir = clockwise ? -1 : 1;
        var s = new P2(cx + r * Math.Cos(dir * startAngle), cy + r * Math.Sin(dir * startAngle));
        var e = new P2(cx + r * Math.Cos(dir * (startAngle + sweep)), cy + r * Math.Sin(dir * (startAngle + sweep)));
        return new(Local.EdgeKind.Arc, s.X, s.Y, e.X, e.Y)
        {
            CenterX = cx, CenterY = cy, Radius = r, StartAngle = startAngle, EndAngle = startAngle + sweep,
            Clockwise = clockwise, ReferenceX = 1, ReferenceY = 0,
        };
    }

    private static string FixturePath(string name, [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "fixtures", "section-hatch-0110", name));

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static JsonElement Json(string name, string sha)
    {
        var bytes = File.ReadAllBytes(FixturePath(name));
        Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(sha);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static JsonElement Cuts() => Json("cuts-plan-14235.json", CutsSha);
    private static JsonElement Case(string section) =>
        Cuts().GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("section").GetString() == section);
    private static double[] HaMatrix() =>
        Cuts().GetProperty("ha_xref").GetProperty("matrix_row_major").EnumerateArray().Select(v => v.GetDouble()).ToArray();

    /// <summary>The native receipt of one HA hatch as the reader would hand it to the Core.</summary>
    private static Local.RawLoop Raw(string handle)
    {
        var hatch = Json("RECEIPT_HATCHRAW_4.json", RawSha).GetProperty("hatches").EnumerateArray()
            .Single(h => h.GetProperty("handle").GetString() == handle);
        var loop = hatch.GetProperty("loops")[0];
        var edges = loop.GetProperty("edges").EnumerateArray().Select(e =>
        {
            double[] P(string name) => e.GetProperty(name).EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var (s, t) = (P("start"), P("end"));
            if (e.GetProperty("type").GetString() == "LineSegment2d")
                return new Local.RawEdge(Local.EdgeKind.Line, s[0], s[1], t[0], t[1]);
            e.GetProperty("type").GetString().Should().Be("CircularArc2d");
            var (c, reference) = (P("center"), P("referenceVector"));
            return new Local.RawEdge(Local.EdgeKind.Arc, s[0], s[1], t[0], t[1])
            {
                CenterX = c[0], CenterY = c[1], Radius = e.GetProperty("radius").GetDouble(),
                StartAngle = e.GetProperty("startAngle").GetDouble(), EndAngle = e.GetProperty("endAngle").GetDouble(),
                Clockwise = e.GetProperty("clockwise").GetBoolean(), ReferenceX = reference[0], ReferenceY = reference[1],
            };
        }).ToList();
        var normal = hatch.GetProperty("normal").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        return new Local.RawLoop(edges, edges.Count, hatch.GetProperty("loopCount").GetInt32(), 0,
            loop.GetProperty("readStatus").GetString() == "ok", loop.GetProperty("isPolyline").GetBoolean(),
            loop.GetProperty("flags").GetString()!, hatch.GetProperty("style").GetString()!,
            normal[0], normal[1], normal[2], hatch.GetProperty("elevation").GetDouble(), HaMatrix(), "fixture:8BB299/" + handle);
    }

    /// <summary>Mirrors SectionHatchRegionReader's edge-loop path: same tessellation and
    /// endpoint join, same unsupported-flag rule, then the product CreateSourceRegion.</summary>
    private static SectionHatchSpanLabelService.Region Region(string handle, Local.RawLoop raw)
    {
        var samples = raw.Edges.Select(e =>
        {
            if (e.Kind == Local.EdgeKind.Line) return (IReadOnlyList<P2>)new[] { new P2(e.StartX, e.StartY), new P2(e.EndX, e.EndY) };
            var count = SectionHatchBoundaryGeometry.ArcSegmentCount(e.Radius, Math.Abs(e.EndAngle - e.StartAngle), 0.005);
            var reference = Math.Atan2(e.ReferenceY, e.ReferenceX); var dir = e.Clockwise ? -1 : 1;
            return Enumerable.Range(0, count + 1).Select(k => k == 0 ? new P2(e.StartX, e.StartY) : k == count
                ? new P2(e.EndX, e.EndY)
                : new P2(e.CenterX + e.Radius * Math.Cos(reference + dir * (e.StartAngle + (e.EndAngle - e.StartAngle) * k / count)),
                    e.CenterY + e.Radius * Math.Sin(reference + dir * (e.StartAngle + (e.EndAngle - e.StartAngle) * k / count))))
                .ToArray();
        }).ToList();
        var all = samples.SelectMany(s => s).ToArray();
        var pad = 0.005 + SectionHatchBoundaryGeometry.MicrometricEndpointToleranceM;
        var bounds = new[] { all.Min(p => p.X) - pad, all.Min(p => p.Y) - pad, all.Max(p => p.X) + pad, all.Max(p => p.Y) + pad };
        var points = SectionHatchBoundaryGeometry.JoinClosedEdgesWithEndpointTolerance(samples, 100000,
            SectionHatchBoundaryGeometry.MicrometricEndpointToleranceM);
        var failure = raw.LoopFlags.Contains("NotClosed") ? $"Hatch loop 0 has unsupported flags {raw.LoopFlags}." : null;
        var loop = new SectionHatchSpanLabelService.SourceLoop(0, points, Array.Empty<SectionHatchBoundaryGeometry.StraightEdge>(),
            bounds, failure, 0.005);
        return SectionHatchSpanLabelService.CreateSourceRegion(new[] { loop }, SectionRegionCoverageLogic.FillStyle.Outer,
            Sidewalk, "6422-HA-MODEL-NATAZ|HW_HA_SIDEWALK", "6422-HA-MODEL-NATAZ", "8BB299/" + handle,
            @"C:\fixture\6422-HA-MODEL-NATAZ.dwg", DrawingHash, raw);
    }

    private static (List<Crossing> Crossings, PresentationAnalysis Analysis) Presentation(string section)
    {
        var marks = Case(section).GetProperty("marks").EnumerateArray().Select(m => (
            Offset: m.GetProperty("offset").GetDouble(), Kind: m.GetProperty("kind").GetString()!,
            Label: m.GetProperty("label").GetString()!,
            Wcs: m.GetProperty("wcs").EnumerateArray().Select(v => v.GetDouble()).ToArray())).ToList();
        var crossings = marks.Select((m, i) => new Crossing(m.Offset, null, "fixture", "GM",
            new(m.Kind, m.Label, 7), $"m{i}", m.Wcs[0], m.Wcs[1])).ToList();
        var analysis = AnalyzePresentationCoverage(marks.Select(m => (m.Offset, m.Kind, m.Label)));
        return (crossings, analysis);
    }
}
