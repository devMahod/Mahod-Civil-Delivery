using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// The ten coverage-gap shapes are transcribed from native62's published preflight
/// (estimate-extract-20260910-101153-80274c7f). Quantities, source identities, item
/// and approvals below are SYNTHETIC and isolated: never a live 6422 approval.
/// </summary>
public sealed class PartialPricedDraftTests
{
    private const string Code = "S51.06.1900";
    private const string Gm = @"C:\SYNTHETIC-ONLY\GM.dwg";
    private static readonly string Hash = new('a', 64);
    private static readonly string CatalogHash = new('b', 64);
    private static readonly DateTime ApprovedAt = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    private static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC-NOT-PROJECT-AUTHORITY", FileHash = CatalogHash };
        catalog.Items[Code] = new() { Code = Code, Description = "SYNTHETIC ONLY — independent curb length", UnitRaw = "מטר" };
        catalog.Prices[Code] = new() { Code = Code, Price = 12.50m, PriceBookId = catalog.SnapshotId, SourceHash = catalog.FileHash };
        return catalog;
    }

    private static ProjectProfile Profile() => new()
    {
        ProfileId = "SYNTHETIC-ONLY", ProjectName = "SIMULATION — not a 6422 estimate",
        Estimate = new()
        {
            QuantitySources = new()
            {
                SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy,
                XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy,
            },
        },
    };

    private static NeutralQuantityRecord Record(string id = "101", double quantity = 20, bool approved = true,
        string method = "line-length+xref-transform", string unit = "מטר")
    {
        var catalog = Catalog();
        return new()
        {
            RecordId = id, ProjectProfileId = "SYNTHETIC-ONLY", RunId = "SYNTHETIC-SCAN",
            Source = new()
            {
                Drawing = "GM.dwg", DrawingPath = Gm, DrawingHash = Hash, Handle = "A/" + id,
                Xref = "GM", EntityType = "LINE", Layer = approved ? "HW-CURB" : "UNKNOWN-SYNTHETIC",
            },
            Measurement = new() { Kind = "length", Method = method, RawValue = quantity, Unit = unit },
            Classification = new()
            {
                RuleKey = "layer:" + (approved ? "HW-CURB" : "UNKNOWN-SYNTHETIC") + "|length",
                CandidateCatalogCode = approved ? Code : null,
                ApprovedCatalogId = approved ? catalog.SnapshotId : null,
                ApprovedCatalogHash = approved ? CatalogHash : null,
                ApprovedCatalogItemFingerprint = approved ? CatalogIdentity.ItemFingerprint(catalog.Items[Code]) : null,
                MappingApprovedBy = approved ? "SYNTHETIC-TEST-NOT-ENGINEER-APPROVAL" : null,
                MappingApprovedAtUtc = approved ? ApprovedAt : null,
            },
        };
    }

    private static ProvenanceRef Source(string handle = "B/505", string? path = @"C:\SYNTHETIC-ONLY\HA.dwg",
        string? hash = null, string xref = "HA") => new()
    {
        SourceKind = "xref", SourcePathOrUri = path, DrawingChecksum = hash ?? Hash,
        SourceHandle = handle, XrefPath = xref, EntityType = "HATCH", Layer = "SYNTHETIC-FAILED-HATCH",
        MeasurementMethod = "hatch-area", RunId = "SYNTHETIC-SCAN",
    };

    private static DeliveryFinding Finding(string code, string title, params ProvenanceRef[] sources) => new()
    {
        Code = code, Domain = "estimate", Severity = code == EstimateFindingCodes.MeasurementFailed ? FindingSeverity.Error : FindingSeverity.ReviewRequired,
        Title = title, Message = "SYNTHETIC coverage-pattern replay; incomplete source is not zero.",
        RecommendedAction = "Inspect the exact source; no invented area, price or scope exclusion.", SourceRefs = sources.ToList(),
    };

    private static List<DeliveryFinding> Native62Pattern()
    {
        var findings = new List<DeliveryFinding>
        {
            Finding(EstimateFindingCodes.UnsupportedEntityCoverage, "437 unsupported objects — native62 count pattern",
                Enumerable.Range(1, 437).Select(index => Source("B/" + (0x1000 + index).ToString("X"))).ToArray()),
            Finding(EstimateFindingCodes.MeasurementFailed, "81 unmeasured objects — native62 count pattern",
                Enumerable.Range(1, 81).Select(index => Source("B/" + (0x2000 + index).ToString("X"))).ToArray()),
        };
        findings.AddRange(new[] { "2000", "3000", "600", "1000" }.Select(name =>
            Finding(EstimatePreflightPolicy.CorridorOutOfDateCode, "Out-of-date corridor " + name)));
        findings.AddRange(new[] { "2000-DES", "700", "750" }.Select(name =>
            Finding(EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode, "Incomplete material coverage " + name)));
        findings.Add(Finding(EstimatePreflightPolicy.EarthworksNotAssessedCode, "Earthworks undecided — not zero, not excluded"));
        return findings;
    }

    private static EstimateResult Build(IReadOnlyList<NeutralQuantityRecord> records, IEnumerable<DeliveryFinding> findings,
        ProjectProfile? profile = null)
    {
        profile ??= Profile();
        var context = EstimateTraceIdentity.InferHeadless(records, profile) with
        {
            ExternalSources = new[] { new EstimateExternalSource { DrawingPath = Gm, DrawingHash = Hash, XrefChain = "GM", ReferenceHandlePath = "A" } },
        };
        return EstimateBuilder.Build(records, Catalog(), profile, "SYNTHETIC-PARTIAL-PRICED-DRAFT", findings, context);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("scheduled")]
    public void FullyReadRegionCoverageGapRetainsFullBlockButAllowsIndependentCurbPartialDraft(string pattern)
    {
        // Native84 refused the incomplete 2000-DES endpoint schedule. These are
        // synthetic full reads of each known missing-coverage shape, not claims
        // about which getter/count the actual native region will return.
        var observed = pattern switch
        {
            "empty" => Array.Empty<double>(), "single" => new[] { 0d },
            "start" => new[] { 5d, 10d }, "end" => new[] { 0d, 5d },
            _ => new[] { 0d, 10d },
        };
        var error = ((Action)(() => CorridorRegionQuantityPolicy.RequireSchedule(
            new("synthetic-baseline/region-001", 0, 10), new[] { 0d, 5d, 10d }, observed)))
            .Should().Throw<CorridorRegionQuantityPolicy.CoverageIncompleteException>().Which;
        var finding = SyntheticRegionFinding(EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode,
            error.Message);
        var built = Build(new[] { Record() }, new[] { finding });
        built.Lines.Single().IncludedInTotals.Should().BeTrue();
        built.Lines.Single().RawQuantity.Should().Be(20);
        built.CleanTotal.Should().Be(250m);
        var partial = EstimatePartialPricedDraftPolicy.Evaluate(built);
        partial.CanExport.Should().BeTrue(string.Join(", ",partial.BlockingReasons));
        partial.EligibleLineCount.Should().Be(1);
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        built.Findings.Should().Contain(f => f.Code == finding.Code && f.Message == error.Message &&
            f.SourceRefs.Single().SourceHandle == "31063");
        built.Exclusions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("native-api")]
    [InlineData("shape-area")]
    [InlineData("station-getter")]
    [InlineData("duplicate")]
    [InlineData("nonfinite")]
    [InlineData("outside")]
    public void RealRegionReadOrInvalidDataFailureStillBlocksIndependentCurbPartialDraft(string defect)
    {
        if (defect is "duplicate" or "nonfinite" or "outside")
        {
            var observed = defect switch { "duplicate" => new[] { 0d, 0d, 10d },
                "nonfinite" => new[] { 0d, double.NaN, 10d }, _ => new[] { -1d } };
            ((Action)(() => CorridorRegionQuantityPolicy.RequireSchedule(
                new("r",0,10),new[]{0d,10d},observed))).Should().ThrowExactly<ArgumentException>();
        }
        // Actual API exceptions remain this code through the adapter's generic
        // catch, never through its typed missing-coverage catch (source guard).
        var finding = SyntheticRegionFinding(EstimatePreflightPolicy.CorridorMaterialReadFailedCode,
            "SYNTHETIC unreadable or invalid region: " + defect);
        var built = Build(new[] { Record() }, new[] { finding });
        built.Lines.Single().IncludedInTotals.Should().BeFalse();
        built.CleanTotal.Should().Be(0);
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeFalse();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        built.Findings.Should().Contain(f => f.Code == finding.Code);
        built.Exclusions.Should().BeEmpty();
    }

    [Fact]
    public void CoverageGapWithUnprovenSourceIdentityCannotClearTheIndependentCurb()
    {
        var finding = SyntheticRegionFinding(EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode,
            "SYNTHETIC missing coverage with malformed source identity", sourceIdentityValid: false);
        var built = Build(new[] { Record() }, new[] { finding });
        built.Lines.Single().IncludedInTotals.Should().BeFalse();
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeFalse();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        built.Exclusions.Should().BeEmpty();
    }

    private static DeliveryFinding SyntheticRegionFinding(string code, string message, bool sourceIdentityValid = true) => new()
    {
        Code=code, Domain="estimate", Severity=code == EstimatePreflightPolicy.CorridorMaterialReadFailedCode
            ? FindingSeverity.Error : FindingSeverity.ReviewRequired,
        Title="SYNTHETIC ONLY — incomplete or unreadable corridor region", Message=message,
        SourceRefs=new() { new() { SourceKind="civil-model",SourcePathOrUri=@"C:\SYNTHETIC-ONLY\HOST.dwg",
            DrawingChecksum=sourceIdentityValid ? Hash : null, SourceHandle="31063", EntityType="CORRIDOR",Layer="C-ROAD-CORR",
            MeasurementMethod="civil-model-quantity",RunId="SYNTHETIC-SCAN" } },
    };

    [Theory]
    [InlineData(0.000028446202578447058)]
    [InlineData(0.00002319711931338026)]
    public void NativeGmTinyPositiveValues_RoundedToCanonicalZeroRemainTracedEligibleLines(double raw)
    {
        // Exact measured values of native GM handles C6397 and 10C98C; identity,
        // catalog and approval are isolated SYNTHETIC test data, not project consent.
        var built = Build(new[] { Record(quantity: raw) }, Native62Pattern());
        var line = built.Lines.Single();
        line.RawQuantity.Should().Be(raw); line.BoqQuantity.Should().Be(0);
        line.IncludedInTotals.Should().BeTrue(); line.Total.Should().Be(0);
        var draft = EstimatePartialPricedDraftPolicy.Evaluate(built);
        draft.CanExport.Should().BeTrue(string.Join(", ", draft.BlockingReasons));
        draft.EligibleLineCount.Should().Be(1); draft.Subtotal.Should().Be(0);
        built.Exclusions.Should().BeEmpty(); EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
    }

    [Fact]
    public void HalfQuantumRoundsAwayFromZero_AndCannotBeReplacedByZero()
    {
        var built = Build(new[] { Record(quantity: 0.00005) }, Native62Pattern());
        built.Lines.Single().BoqQuantity.Should().Be(0.0001);
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeTrue();
        built.Lines.Single().BoqQuantity = 0;
        built.Lines.Single().Total = 0; built.CleanTotal = 0;
        EstimatePartialPricedDraftPolicy.Evaluate(built).BlockingReasons.Should().Contain("draft:included-line-not-eligible");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-0.00001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ForgedBoqZeroCannotHideInvalidOrNonzeroCanonicalRaw(double tamperedRaw)
    {
        var built = Build(new[] { Record(quantity: 0.00002) }, Native62Pattern());
        built.Lines.Single().RawQuantity = tamperedRaw;
        EstimatePartialPricedDraftPolicy.Evaluate(built).BlockingReasons.Should().Contain("draft:included-line-not-eligible");
    }

    [Fact]
    public void CanonicalZeroFromRealApprovedAdjustmentEngineChain_IsEligibleWithoutExclusion()
    {
        var built = AdjustedRoundedZero();
        var line = built.Lines.Single();
        line.RawQuantity.Should().Be(1); line.BoqQuantity.Should().Be(0); line.Total.Should().Be(0);
        line.Adjustments.Should().HaveCount(2);
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeTrue();
        built.Exclusions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("factor-zero")]
    [InlineData("factor-negative")]
    [InlineData("factor-nan")]
    [InlineData("authority")]
    [InlineData("date")]
    [InlineData("order")]
    [InlineData("rule-id")]
    [InlineData("missing-step")]
    public void CanonicalZeroWithTamperedAdjustmentChain_IsNotEligible(string defect)
    {
        var built = AdjustedRoundedZero();
        var line = built.Lines.Single();
        var original = line.Adjustments[1];
        if (defect == "missing-step") line.Adjustments.RemoveAt(0);
        else line.Adjustments[1] = new AdjustmentStep
        {
            RuleId = defect == "rule-id" ? line.Adjustments[0].RuleId : original.RuleId,
            Factor = defect switch { "factor-zero" => 0, "factor-negative" => -0.02, "factor-nan" => double.NaN, _ => original.Factor },
            Input = defect == "input" ? 0.002 : original.Input,
            Output = defect == "output" ? 0.00003 : original.Output,
            ApprovedBy = defect == "authority" ? null : original.ApprovedBy,
            ApprovedAtUtc = defect == "date" ? null : original.ApprovedAtUtc,
            Order = defect == "order" ? 0 : original.Order,
        };
        EstimatePartialPricedDraftPolicy.Evaluate(built).BlockingReasons.Should().Contain("draft:included-line-not-eligible");
    }

    private static EstimateResult AdjustedRoundedZero()
    {
        var profile = Profile();
        foreach (var (id, factor, order) in new[] { ("SYNTHETIC-A", 0.001, 10), ("SYNTHETIC-B", 0.02, 20) })
            profile.Estimate.ApprovedAdjustments.Add(new()
            {
                RuleId = id, Factor = factor, Order = order, Status = "CONFIRMED",
                Scope = "rule:layer:HW-CURB|length", ApprovedBy = "SYNTHETIC-TEST-NOT-PROJECT-AUTHORITY",
                ApprovedAtUtc = ApprovedAt, Reason = "Isolated adjustment chain proof only", Source = "SYNTHETIC-ONLY",
            });
        return Build(new[] { Record(quantity: 1) }, Native62Pattern(), profile);
    }

    [Fact]
    public void Native62TenGapPattern_KeepsIndependentMoney_UnknownDependencyAndFullExportStayBlocked()
    {
        var findings = Native62Pattern();
        var records = new[] { Record(), Record("102", 30), Record("103", 10, method: "unknown-dependent-length"), Record("104", 5, approved: false) };
        var built = Build(records, findings);
        built.Lines.Take(2).Should().OnlyContain(line => line.Status == DeliveryStatus.Ready && line.IncludedInTotals && line.Total != null);
        built.Lines.Skip(2).Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null);
        built.CleanTotal.Should().Be(625m);
        built.Status.Should().Be(DeliveryStatus.Failed);
        built.Findings.Should().Contain(findings);
        findings.Should().OnlyContain(finding => finding.ResolvedAtUtc == null && finding.ResolvedBy == null);
        Profile().Estimate.Earthworks.Requested.Should().BeNull();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        var draft = EstimatePartialPricedDraftPolicy.Evaluate(built);
        draft.CanExport.Should().BeTrue(string.Join(", ", draft.BlockingReasons));
        draft.EligibleLineCount.Should().Be(2); draft.UnresolvedLineCount.Should().Be(2); draft.Subtotal.Should().Be(625m);
        draft.ProjectBlockingFindingCount.Should().BeGreaterThanOrEqualTo(10);
    }

    [Theory]
    [InlineData("same-source")]
    [InlineData("same-source-other-insertion")]
    [InlineData("conflicting-hash")]
    [InlineData("missing-path")]
    [InlineData("missing-sha")]
    [InlineData("unknown-code")]
    [InlineData("source-security")]
    [InlineData("explicit-id")]
    [InlineData("id-source-disagreement")]
    [InlineData("malformed-corridor-source")]
    public void RelatedOrAmbiguousFailuresNeverEnterThePartialSubtotal(string scenario)
    {
        var source = scenario switch
        {
            "same-source" or "id-source-disagreement" => Source("A/101", Gm, xref: "GM"),
            "same-source-other-insertion" => Source("F/101", Gm, xref: "OTHER-GM-INSERTION"),
            "conflicting-hash" => Source("A/777", Gm, new string('c', 64), "GM"),
            "missing-path" or "malformed-corridor-source" => Source(path: null),
            "missing-sha" => Source(hash: ""),
            _ => Source(),
        };
        var code = scenario == "unknown-code" ? "EST-UNKNOWN-DEPENDENCY" : scenario == "source-security" ? "SEC-XREF-TRANSFORM-INVALID" :
            scenario == "malformed-corridor-source" ? EstimatePreflightPolicy.CorridorOutOfDateCode : EstimateFindingCodes.MeasurementFailed;
        var finding = Finding(code, scenario, source);
        if (scenario == "explicit-id") finding.AffectedRecordIds.Add("101");
        if (scenario is "id-source-disagreement" or "source-security") finding.AffectedRecordIds.Add("other-record");
        var built = Build(new[] { Record() }, new[] { finding });
        var line = built.Lines.Single();
        line.Total.Should().BeNull(); line.IncludedInTotals.Should().BeFalse();
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeFalse();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
    }

    [Fact]
    public void EqualLeafInAnotherDrawingIsNotAHostHandleMatch_AndMissingXrefTransformMethodIsNotIndependent()
    {
        var finding = Finding(EstimateFindingCodes.MeasurementFailed, "other verified file", Source("B/101"));
        var built = Build(new[] { Record() }, new[] { finding });
        built.Lines.Single().Total.Should().Be(250m);
        Build(new[] { Record(method: "line-length") }, new[] { finding }).Lines.Single().Total.Should().BeNull();
        var corridor = built.Lines.Single(); corridor.SourceEntityType = "CORRIDOR"; corridor.MeasurementMethod = "corridor-qto-avg-end-area";
        EstimateFindingImpactPolicy.BlocksLine(Native62Pattern()[2], corridor).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, "מטר")]
    [InlineData(true, "m2")]
    public void NoApprovalOrWrongUnitDoesNotProduceDraftMoney(bool approved, string unit)
    {
        var built = Build(new[] { Record(approved: approved, unit: unit) }, Native62Pattern());
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeFalse();
        built.Lines.Single().Total.Should().BeNull();
    }

    [Fact]
    public void PresentationLabelsIndependentMoneyAsDraft_NotFullReady_AndHidesRelatedFailedPrice()
    {
        var record = Record(); var findings = Native62Pattern(); var built = Build(new[] { record }, findings);
        var state = EstimateQuantityPresentationPolicy.Evaluate(new[] { record }, findings, Code, Catalog(), built, false, true, false);
        state.Summary.Should().Be("מתומחר לטיוטה"); state.PriceDisplay.Should().Be(12.5m.ToString("N2"));
        state.MeasurementNeedsReview.Should().BeFalse(); state.BuiltReady.Should().BeFalse(); state.Severity.Should().Be("review");
        var related = Finding(EstimateFindingCodes.MeasurementFailed, "same object", Source("A/101", Gm, xref: "GM"));
        var failed = Build(new[] { record }, new[] { related });
        EstimateQuantityPresentationPolicy.Evaluate(new[] { record }, new[] { related }, Code, Catalog(), failed, false, true, false)
            .PriceDisplay.Should().BeNull();
    }

    [Theory]
    [InlineData(EstimatePreflightPolicy.CorridorOutOfDateCode, false)]
    [InlineData(EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode, false)]
    [InlineData(EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode, true)]
    public void NewCivilModelProvenanceDoesNotPoisonIndependentGeometry_ButParentAndUnknownIdentityRemainBlocked(string code, bool subcontext)
    {
        // Exact current CorridorFailureProvenance.Source / ReadFailures field
        // shape. Identity values and all approvals remain synthetic test inputs.
        ProvenanceRef Origin(string? handle) => new()
        {
            SourceKind = "civil-model", SourcePathOrUri = @"C:\SYNTHETIC-ONLY\host.dwg", DrawingChecksum = Hash,
            SourceHandle = handle, EntityType = "CORRIDOR", Layer = "CORRIDOR-LAYER", RunId = "SYNTHETIC-SCAN", ToolVersion = "SYNTHETIC-TEST",
            MeasurementMethod = subcontext ? "civil-model-quantity:shape-area" : "civil-model-quantity",
            SourceSubentityPath = subcontext ? "baseline-006@60:Base" : null,
        };
        var finding = Finding(code, "2000-DES / incomplete material coverage", Origin("A101"));
        var built = Build(new[] { Record() }, new[] { finding });
        built.Lines.Single().Total.Should().Be(250m);
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeTrue();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        built.Findings.Should().Contain(finding); finding.ResolvedAtUtc.Should().BeNull();
        var related = built.Lines.Single(); related.SourceDrawingPath = @"C:\SYNTHETIC-ONLY\host.dwg";
        related.SourceHandle = "A101"; related.SourceXref = null; related.MeasurementMethod = "line-length";
        // Same parent identity blocks even if a malformed producer claims it was a direct line.
        EstimateFindingImpactPolicy.BlocksLine(finding, related).Should().BeTrue();
        var unknown = Finding(code, "Unknown parent handle must remain unknown", Origin(null));
        Build(new[] { Record() }, new[] { unknown }).Lines.Single().Total.Should().BeNull();
        var readFailure = Finding(EstimatePreflightPolicy.CorridorMaterialReadFailedCode, "Unread model dependency", Origin("A101"));
        Build(new[] { Record() }, new[] { readFailure }).Lines.Single().Total.Should().BeNull();
    }

    [Fact]
    public void PartialWorkbookSeparatesSummaryAndPricedTrace_FullAuditRetainsAllRowsAndFindings_AndNeverOverwrites()
    {
        var records = new[] { Record(), Record("102", 30), Record("103", 10, method: "unknown-dependent-length"), Record("104", 5, approved: false) };
        var findings = Native62Pattern(); var built = Build(records, findings);
        var parent = Environment.GetEnvironmentVariable("MAHOD_PARTIAL_DRAFT_EVIDENCE_DIR") ?? Path.Combine(Path.GetTempPath(), "MahodAI-partial-priced-draft-tests");
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var options = new EstimateExcelWriter.WriteOptions(ProjectTitle: "SYNTHETIC ONLY — NOT a 6422 engineering estimate");
        var first = EstimateExcelWriter.WritePartialPricedDraft(built, directory, "SYNTHETIC-PARTIAL-PRICED-DRAFT", options);
        var originalHash = ArtifactHash.Sha256OfFile(first.XlsxPath);
        var retry = EstimateExcelWriter.WritePartialPricedDraft(built, directory, "SYNTHETIC-PARTIAL-PRICED-DRAFT", options);
        retry.XlsxPath.Should().NotBe(first.XlsxPath); ArtifactHash.Sha256OfFile(first.XlsxPath).Should().Be(originalHash);
        using var zip = ZipFile.OpenRead(first.XlsxPath);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var text = string.Join("\n", xml.Descendants(ns + "t").Select(node => node.Value));
        text.Should().Contain(EstimatePartialPricedDraftPolicy.DraftNotice).And.Contain(EstimatePartialPricedDraftPolicy.SubtotalNotice);
        using var findingStream = zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();
        var findingsXml = XDocument.Load(findingStream);
        var findingText = string.Join("\n", findingsXml.Descendants(ns + "t").Select(node => node.Value));
        foreach (var code in findings.Select(finding => finding.Code).Distinct()) findingText.Should().Contain(code);
        using var traceStream = zip.GetEntry("xl/worksheets/sheet4.xml")!.Open();
        var traceXml = XDocument.Load(traceStream);
        var traceIds = traceXml.Descendants(ns + "t").Select(node => node.Value).Where(value => value.Contains(" / ")).ToList();
        foreach (var line in built.Lines.Where(line => line.IncludedInTotals)) traceIds.Should().Contain($"{line.LineId} / {line.RecordId}");
        foreach (var line in built.Lines.Where(line => !line.IncludedInTotals)) traceIds.Should().NotContain($"{line.LineId} / {line.RecordId}");
        var pricedRow = xml.Descendants(ns + "row").Single(row => row.Elements(ns + "c").Any(cell => cell.Element(ns + "f")?.Value.StartsWith("ROUND(E", StringComparison.Ordinal) == true));
        var rowId = pricedRow.Attribute("r")!.Value;
        string Value(string col) => pricedRow.Elements(ns + "c").Single(cell => cell.Attribute("r")!.Value == col + rowId).Element(ns + "v")!.Value;
        decimal.Parse(Value("E"), CultureInfo.InvariantCulture).Should().Be(50m);
        decimal.Parse(Value("F"), CultureInfo.InvariantCulture).Should().Be(12.5m);
        pricedRow.Descendants(ns + "f").Single().Value.Should().Be($"ROUND(E{rowId}*F{rowId},2)");
        using var audit = JsonDocument.Parse(File.ReadAllText(first.AuditPath));
        audit.RootElement.GetProperty("schema_version").GetInt32().Should().Be(3);
        audit.RootElement.GetProperty("package_kind").GetString().Should().Be(EstimatePartialPricedDraftPolicy.PackageKind);
        audit.RootElement.GetProperty("clean_total").GetDecimal().Should().Be(625m);
        audit.RootElement.GetProperty("full_estimate_export_allowed").GetBoolean().Should().BeFalse();
        audit.RootElement.GetProperty("lines").GetArrayLength().Should().Be(4);
        audit.RootElement.GetProperty("findings").GetArrayLength().Should().BeGreaterThanOrEqualTo(10);
        foreach (var finding in findings)
            audit.RootElement.GetProperty("findings").EnumerateArray().Should().Contain(row =>
                row.GetProperty("finding_id").GetString() == finding.FindingId && row.GetProperty("title").GetString() == finding.Title);
        audit.RootElement.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("record_id").GetString())
            .Should().BeEquivalentTo(records.Select(record => record.RecordId));
        audit.RootElement.GetProperty("exclusions").GetArrayLength().Should().Be(0);
        var blockedAuditLine = audit.RootElement.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("record_id").GetString() == "103");
        blockedAuditLine.GetProperty("finding_refs").GetArrayLength().Should().BeGreaterThanOrEqualTo(10);
        blockedAuditLine.GetProperty("findings").EnumerateArray().Should().NotContain(finding =>
            finding.GetProperty("code").GetString() == EstimateFindingCodes.UnsupportedEntityCoverage);
        new FileInfo(first.AuditPath).Length.Should().BeLessThan(1_500_000);
        using var manifest = JsonDocument.Parse(File.ReadAllText(first.ManifestPath));
        manifest.RootElement.GetProperty("package_kind").GetString().Should().Be(EstimatePartialPricedDraftPolicy.PackageKind);
        manifest.RootElement.GetProperty("workbook").GetProperty("sha256").GetString().Should().Be(originalHash);
        Action fullExport = () => EstimateExcelWriter.Write(built, directory, "MUST-NOT-EXPORT-FULL");
        fullExport.Should().Throw<InvalidOperationException>();
        Directory.GetFiles(directory, "MUST-NOT-EXPORT-FULL*").Should().BeEmpty();
    }

    [Fact]
    public void PartialRegistryRefusesConflictingFindingIds_WhileFullAuditKeepsSchemaTwo()
    {
        var first = Finding(EstimatePreflightPolicy.CorridorOutOfDateCode, "First corridor");
        var second = new DeliveryFinding
        {
            FindingId = first.FindingId, Code = first.Code, Domain = first.Domain, Severity = first.Severity,
            Title = "Second independent corridor",
        };
        var built = Build(new[] { Record() }, new[] { first, second });
        var directory = Path.Combine(Path.GetTempPath(), "MahodAI-partial-priced-draft-tests", Guid.NewGuid().ToString("N"));
        Action partial = () => EstimateExcelWriter.WritePartialPricedDraft(built, directory, "CONFLICT-MUST-NOT-WRITE");
        partial.Should().Throw<InvalidOperationException>().WithMessage("*conflicting finding identities*");
        Directory.Exists(directory).Should().BeFalse();
        var complete = Build(new[] { Record() }, Array.Empty<DeliveryFinding>());
        var written = EstimateExcelWriter.Write(complete, directory, "SYNTHETIC-COMPAT-FULL");
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        audit.RootElement.GetProperty("schema_version").GetInt32().Should().Be(2);
    }

    [Fact]
    [Trait("Category", "RecordedRunReplay")]
    public async Task Native62ActualPublishedPreflight_IndependentRecordedCurbWithIsolatedApprovalRetainsMoneyAndAllCoverageGaps()
    {
        const string run = "estimate-extract-20260910-101153-80274c7f";
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "runs", run);
        var preflightPath = Path.Combine(directory, "quantity_preflight.json");
        ArtifactHash.Sha256OfFile(preflightPath).Should().Be("7ae9e9403808849b9f7b519df9f70830f8f17c00e7bdd73f5ad1dd82ad394500");
        var findings = JsonSerializer.Deserialize<List<DeliveryFinding>>(File.ReadAllText(preflightPath))!;
        findings.Count(finding => finding.AffectedRecordIds.Count == 0 && EstimatePreflightPolicy.IsBlocking(finding)).Should().Be(10);
        var records = new List<NeutralQuantityRecord>();
        // Stream only to the first two small, direct, real curb records. Do not load
        // or rehash the entire 333MB artifact just to repeat its small acceptance case.
        await using (var stream = File.OpenRead(Path.Combine(directory, "neutral_quantity_records.json")))
        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<NeutralQuantityRecord>(stream))
        {
            if (record?.Classification.RuleKey == "layer:HW-CURB|length" &&
                record.Measurement.Method is "line-length+xref-transform" or "polyline-length+xref-transform" &&
                record.Measurement.RawValue is > 0 and < 100 &&
                !findings.Any(finding => EstimatePreflightPolicy.IsBlocking(finding) && finding.AffectedRecordIds.Contains(record.RecordId)))
            {
                records.Add(record);
                if (records.Count == 2) break;
            }
        }
        records.Should().HaveCount(2); records.Should().OnlyContain(record => record.RunId == run);
        var originalRecords = records;
        var original = JsonSerializer.Serialize(records);
        var catalog = Catalog();
        foreach (var record in records)
            record.Classification.CandidateCatalogCode.Should().BeNull();
        // This isolated approval simulation mirrors the record transition in
        // EstimateWorkflowService.WithApprovedClassification, not a durable profile
        // approval or native workflow. Classification-only mutation leaves the
        // original EST-UNMAPPED/ReviewRequired evidence unresolved and is invalid.
        records = records.Select(record => SimulatePublishedApprovalTransition(record, catalog)).ToList();
        var context = EstimateTraceIdentity.InferHeadless(records, Profile()) with
        {
            ExternalSources = records.Select(record => new EstimateExternalSource
            {
                DrawingPath = record.Source.DrawingPath!, DrawingHash = record.Source.DrawingHash,
                XrefChain = record.Source.Xref!, ReferenceHandlePath = record.Source.Handle[..record.Source.Handle.LastIndexOf('/')],
            }).DistinctBy(source => (source.DrawingPath, source.XrefChain, source.ReferenceHandlePath)).ToArray(),
        };
        var built = EstimateBuilder.Build(records, catalog, Profile(), "SYNTHETIC-NATIVE62-SMALL-REPLAY", findings, context);
        built.Lines.Should().HaveCount(2).And.OnlyContain(line => line.IncludedInTotals && line.Total != null);
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeTrue();
        built.Findings.Should().Contain(findings);
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
        built.CleanTotal.Should().Be(Math.Round(records.Sum(record => Math.Round((decimal)record.Measurement.RawValue, 4,
            MidpointRounding.AwayFromZero)) * 12.50m, 2, MidpointRounding.AwayFromZero));
        original.Should().NotContain("SYNTHETIC-ONLY-REPLAY-NOT-PROJECT-APPROVAL");
        JsonSerializer.Serialize(originalRecords).Should().Be(original);
        // No files or active project/profile/catalog decisions were written.
    }

    [Fact]
    public void SimulatedApprovalTransition_ClearsOnlyUnmappedAndPreservesOtherMeasurementBlocker()
    {
        var input = Record(approved: false);
        input.Status = DeliveryStatus.ReviewRequired;
        input.Findings.Add(new DeliveryFinding { Code = EstimateFindingCodes.Unmapped, Domain = "estimate",
            Severity = FindingSeverity.ReviewRequired, Title = "SYNTHETIC unresolved mapping", AffectedRecordIds = { input.RecordId } });
        var measurementFailure = new DeliveryFinding { Code = EstimateFindingCodes.MeasurementFailed, Domain = "estimate",
            Severity = FindingSeverity.Error, Title = "SYNTHETIC measured-source failure", AffectedRecordIds = { input.RecordId } };
        input.Findings.Add(measurementFailure);
        var before = JsonSerializer.Serialize(input);
        var approved = SimulatePublishedApprovalTransition(input, Catalog());
        approved.Classification.CandidateCatalogCode.Should().Be(Code);
        approved.Findings.Should().ContainSingle().Which.Should().BeSameAs(measurementFailure);
        approved.Status.Should().Be(DeliveryStatus.Failed);
        JsonSerializer.Serialize(input).Should().Be(before);
        var built = Build(new[] { approved }, Array.Empty<DeliveryFinding>());
        built.Lines.Single().IncludedInTotals.Should().BeFalse();
        built.Lines.Single().Total.Should().BeNull();
        built.Lines.Single().Findings.Should().Contain(measurementFailure);
        EstimatePartialPricedDraftPolicy.Evaluate(built).CanExport.Should().BeFalse();
        EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
    }

    private static NeutralQuantityRecord SimulatePublishedApprovalTransition(NeutralQuantityRecord record, CatalogSnapshot catalog)
    {
        // Same state transition as EstimateWorkflowService.WithApprovedClassification:
        // remove only the now-resolved mapping finding, derive status from all remaining
        // findings, and retain source, measurement and provenance without modifying input.
        var findings = record.Findings.Where(f => f.Code != EstimateFindingCodes.Unmapped).ToList();
        return new NeutralQuantityRecord
        {
            SchemaVersion = record.SchemaVersion, RecordId = record.RecordId, ProjectProfileId = record.ProjectProfileId,
            RunId = record.RunId, Source = record.Source, Measurement = record.Measurement, Provenance = record.Provenance,
            Status = DeliveryStatusRules.CapByFindings(DeliveryStatus.Ready, findings), Findings = findings,
            Classification = new QuantityClassification
            {
                SourceClass = record.Classification.SourceClass, RuleKey = record.Classification.RuleKey,
                CandidateCatalogCode = Code, ApprovedCatalogId = catalog.SnapshotId, ApprovedCatalogHash = catalog.FileHash,
                ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(catalog.Items[Code]),
                MappingApprovedBy = "SYNTHETIC-ONLY-REPLAY-NOT-PROJECT-APPROVAL", MappingApprovedAtUtc = ApprovedAt,
                Tags = record.Classification.Tags.Where(tag => !string.Equals(tag, "discovered", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(tag, "rule-classified", StringComparison.OrdinalIgnoreCase)).Concat(new[] { "rule-classified" })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            },
        };
    }
}
