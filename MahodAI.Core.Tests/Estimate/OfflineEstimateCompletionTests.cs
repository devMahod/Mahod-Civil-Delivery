using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Actual Core BUILD -> persisted schema-v2 -> XLSX/audit/manifest readback.
/// All measurements and approval decisions are explicit simulation fixtures.
/// No native SCAN, partial-parser quantities, live profile or engineering approval.
/// Retains outputs in a unique test-owned directory for independent inspection.
/// </summary>
public sealed class OfflineEstimateCompletionTests
{
    private const string Label = "SIMULATION ONLY — synthetic geometry; not a project estimate";
    private const string Approver = "SIMULATION-ONLY-NOT-ENGINEER-AUTHORITY";
    private const string CatalogHash = "90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c";
    private static readonly DateTime DecisionTime = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly string _sourcePath;
    private readonly string _sourceHash;
    private readonly CatalogSnapshot _catalog;
    private readonly ProjectProfile _profile;

    public OfflineEstimateCompletionTests()
    {
        var parent = Environment.GetEnvironmentVariable("MAHOD_OFFLINE_ESTIMATE_EVIDENCE_DIR") ??
            Path.Combine(Path.GetTempPath(), "MahodAI-offline-estimate-e2e");
        _directory = Path.Combine(parent, "simulation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var catalogPath = FindCatalog();
        _catalog = PriceBookXlsxLoader.Load(catalogPath, "nti-urban-082025");
        _catalog.FileHash.Should().Be(CatalogHash);
        _sourcePath = Path.Combine(_directory, "synthetic-source.json");
        File.WriteAllText(_sourcePath, JsonSerializer.Serialize(new
        {
            scope = Label,
            geometry = new
            {
                kerbA = new[] { new[] { 0d, 0d }, new[] { 75.125d, 0d } },
                kerbB = new[] { new[] { 0d, 10d }, new[] { 24.875d, 10d } },
                rackPoints = new[] { new[] { 1d, 20d }, new[] { 2d, 20d }, new[] { 3d, 20d } },
            },
            units = "metres; rack instances are counts",
            native_scan_performed = false,
        }, Json));
        _sourceHash = ArtifactHash.Sha256OfFile(_sourcePath);
        _profile = new ProjectProfile { ProfileId = "OFFLINE-SIMULATION", ProjectName = Label };
        _profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        _profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        _profile.Estimate.Catalog.CatalogFile = catalogPath;
        _profile.Estimate.Catalog.CatalogFileHash = _catalog.FileHash;
        _profile.Estimate.Pricing.PriceBookSnapshotId = _catalog.SnapshotId;
        _profile.Estimate.Pricing.PriceBookHash = _catalog.FileHash;
        _profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
        {
            Id = _catalog.SnapshotId, File = catalogPath, FileHash = _catalog.FileHash,
        });
        _profile.Estimate.Earthworks.Requested = false;
        _profile.Estimate.Earthworks.DecidedBy = Approver;
        _profile.Estimate.Earthworks.DecidedAtUtc = DecisionTime;
        _profile.Estimate.Earthworks.Reason = "Simulation contains no earthworks; no real project scope decision.";
    }

    [Fact]
    public void SerializedMeasurementsAndExplicitMappings_ProduceGroupedRealPriceWorkbookAndNonOverwritingRetry()
    {
        using var geometry = JsonDocument.Parse(File.ReadAllText(_sourcePath));
        var source = geometry.RootElement.GetProperty("geometry");
        static double Length(JsonElement points)
        {
            var dx = points[1][0].GetDouble() - points[0][0].GetDouble();
            var dy = points[1][1].GetDouble() - points[0][1].GetDouble();
            return Math.Sqrt(dx * dx + dy * dy);
        }
        var measured = new List<NeutralQuantityRecord>
        {
            Record("kerb-a", "KERB-NEW", "length", "מטר", Length(source.GetProperty("kerbA"))),
            Record("kerb-b", "KERB-NEW", "length", "מטר", Length(source.GetProperty("kerbB"))),
        };
        for (var index = 0; index < source.GetProperty("rackPoints").GetArrayLength(); index++)
            measured.Add(Record("rack-" + index, "byc", "count", "יח'", 1,
                "מתקן ל-2 אופניים - 100_250-1013471-ח1 גלריה"));
        measured = RoundTrip(measured, "neutral-quantity-records.json");
        var proposals = Propose(measured);
        proposals.Should().Contain(proposal => proposal.ProposedCode == "U40.02.1230" &&
            proposal.EvidenceKind == "heuristic-cad-metadata");
        RoundTrip(proposals, "mapping-proposals.json");
        AssertBlockedWithoutWorkbook(Build(measured, "unapproved"), "unapproved");

        var decisions = RoundTrip(new[]
        {
            Decision("layer:KERB-NEW|length", "U51.06.1900"),
            Decision("layer:byc|count", "U40.02.1230"),
        }, "explicit-simulation-decisions.json");
        var approved = RoundTrip(ApplyFixtureDecisions(measured, decisions), "approved-records.json");
        var estimate = Build(approved, "successful");
        var expected = new Dictionary<string, (decimal Quantity, decimal Price)>
        {
            ["U51.06.1900"] = (100m, _catalog.Prices["U51.06.1900"].Price!.Value),
            ["U40.02.1230"] = (3m, _catalog.Prices["U40.02.1230"].Price!.Value),
        };
        estimate.Lines.Should().HaveCount(5);
        var first = WriteAndReadBack(estimate, expected, "successful");
        var originalHash = ArtifactHash.Sha256OfFile(first.XlsxPath);
        var retry = WriteAndReadBack(estimate, expected, "successful");
        retry.XlsxPath.Should().NotBe(first.XlsxPath);
        ArtifactHash.Sha256OfFile(first.XlsxPath).Should().Be(originalHash);
        WriteSummary("success-and-retry", estimate, first, expected);
    }

    [Fact]
    public void ArbitraryLayerNamesAndMixedValidUnits_ReconcileEverySourceRecordInProductWorkbook()
    {
        // Names deliberately carry no engineering meaning. Only the explicit,
        // simulation-scoped catalog binding defines what these measured records mean.
        using var geometry = JsonDocument.Parse(File.ReadAllText(_sourcePath));
        var source = geometry.RootElement.GetProperty("geometry");
        static double SourceLength(JsonElement points) => Math.Sqrt(
            Math.Pow(points[1][0].GetDouble() - points[0][0].GetDouble(), 2) +
            Math.Pow(points[1][1].GetDouble() - points[0][1].GetDouble(), 2));
        var measurements = new List<NeutralQuantityRecord>
        {
            Record("arbitrary-a", "Z-731", "length", "מטר", SourceLength(source.GetProperty("kerbA"))),
            Record("arbitrary-b", "Z-731", "length", "מטר", SourceLength(source.GetProperty("kerbB"))),
        };
        for (var index = 0; index < source.GetProperty("rackPoints").GetArrayLength(); index++)
            measurements.Add(Record("arbitrary-instance-" + index, "ANY-NAME-2044", "count", "יח'", 1,
                "SIMULATION-INSTANCE-NO-AUTO-SEMANTICS"));
        var records = RoundTrip(measurements, "arbitrary-layer-measurements.json");
        AssertBlockedWithoutWorkbook(Build(records, "arbitrary-unapproved"), "arbitrary-unapproved");
        var decisions = RoundTrip(new[]
        {
            Decision("layer:Z-731|length", "U51.06.1900"),
            Decision("layer:ANY-NAME-2044|count", "U40.02.1230"),
        }, "arbitrary-layer-explicit-simulation-decisions.json");
        var approved = RoundTrip(ApplyFixtureDecisions(records, decisions), "arbitrary-layer-approved-records.json");
        var built = Build(approved, "arbitrary-layer-mixed-valid-units");
        built.Lines.Select(line => line.RecordId).OrderBy(id => id).Should()
            .Equal(records.Select(record => record.RecordId).OrderBy(id => id));
        built.Lines.Should().HaveCount(5).And.OnlyContain(line => line.IncludedInTotals);
        var expected = new Dictionary<string, (decimal Quantity, decimal Price)>
        {
            ["U51.06.1900"] = (100m, _catalog.Prices["U51.06.1900"].Price!.Value),
            ["U40.02.1230"] = (3m, _catalog.Prices["U40.02.1230"].Price!.Value),
        };
        var written = WriteAndReadBack(built, expected, "ARBITRARY-LAYERS-MAIN");
        WriteSummary("arbitrary-layer-mixed-valid-units", built, written, expected);
    }

    [Fact]
    public void ScopeLabel_MustMatchTheActualSnapshotKind_WithoutBypassingSourceGates()
    {
        var records = ApplyFixtureDecisions(new[] { Record("scope", "KERB-NEW", "length", "מטר", 10) },
            new[] { Decision("layer:KERB-NEW|length", "U51.06.1900") });
        var estimate = Build(records, "scope-label");
        EstimatePreflightPolicy.CanExport(estimate).Should().BeTrue();
        static EstimateResult WithNotice(EstimateResult value, string notice)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(EstimateResultArtifact.From(value), Json))!;
            node["scope_notice"] = notice;
            return EstimateResultArtifactReader.Read(node.ToJsonString(), Json);
        }
        EstimatePreflightPolicy.ExportBlockingReasons(WithNotice(estimate, EstimatePreflightPolicy.CompleteScopeNotice))
            .Should().Contain("scope-notice-missing");

        var neutral = EstimateTraceIdentity.InferHeadless(records, _profile);
        var live = EstimateBuilder.Build(records, _catalog, _profile, "SIMULATION-LIVE-CONTEXT-CONTRACT",
            traceContext: neutral with { SourceSnapshotKind = EstimateBuildContext.CivilLiveSaved, SourceDbMod = 0 });
        live.ScopeNotice.Should().Be(EstimatePreflightPolicy.CompleteScopeNotice);
        EstimatePreflightPolicy.CanExport(live).Should().BeTrue();
        EstimatePreflightPolicy.ExportBlockingReasons(WithNotice(live, EstimatePreflightPolicy.NeutralRecordScopeNotice))
            .Should().Contain("scope-notice-missing");
        // Explicit context-contract fixture only: no Civil session was opened.
        var written = EstimateExcelWriter.Write(live, _directory, "SIMULATION-LIVE-HEADER-CONTRACT",
            new EstimateExcelWriter.WriteOptions(ProjectTitle: Label));
        using var zip = ZipFile.OpenRead(written.XlsxPath);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var texts = xml.Descendants(ns + "t").Select(text => text.Value).ToList();
        texts.Should().Contain(EstimatePreflightPolicy.CompleteScopeNotice);
        using var identityStream = zip.GetEntry("xl/worksheets/sheet5.xml")!.Open();
        var identity = XDocument.Load(identityStream);
        identity.Descendants(ns + "t").Should().Contain(text => text.Value.Contains("SHA-256"));
        identity.Descendants(ns + "t").Should().Contain(text => text.Value == live.SourceDrawingHash);
        texts.Should().Contain(text => text.Contains("כמויות מהגיאומטריה בשרטוט"));
        texts.Should().NotContain(EstimatePreflightPolicy.NeutralRecordScopeNotice);
    }

    [Fact]
    public void ReviewedSources_SurviveBuildArtifactAndWorkbook_WithExplicitExclusionsAndUnchangedPrice()
    {
        // Synthetic source inventory, not a native SCAN or real engineering scope approval.
        var inventory = new EstimateSourceInventory("51fbc96a-2b2d-4984-9e19-2c03e1451463", _sourcePath, new[]
        {
            new EstimateSourceDefinition("host", "SIMULATION host", _sourcePath, "host", true),
            new EstimateSourceDefinition(EstimateSourceSelectionPolicy.XrefKey("SIMULATION architecture excluded", "not-measured.dwg", "AA"),
                "SIMULATION architecture excluded", "not-measured.dwg", "Unloaded"),
        });
        var choices = EstimateSourceSelectionPolicy.CreateDraft(inventory, null);
        choices[1].Included = false;
        choices[1].Category = "architecture";
        _profile.Estimate.SourceSelection = EstimateSourceSelectionPolicy.Approve(inventory, choices, Approver, DecisionTime);
        _profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.ReviewedSourcesPolicy;
        var records = ApplyFixtureDecisions(new[] { Record("scoped", "KERB-NEW", "length", "מטר", 10) },
            new[] { Decision("layer:KERB-NEW|length", "U51.06.1900") });
        var estimate = Build(records, "reviewed-source-scope");
        EstimatePreflightPolicy.CanExport(estimate).Should().BeTrue(string.Join(";", EstimatePreflightPolicy.ExportBlockingReasons(estimate)));
        estimate.Lines.Should().ContainSingle().Which.BoqQuantity.Should().Be(10);
        estimate.CleanTotal.Should().Be(decimal.Round(10m * _catalog.Prices["U51.06.1900"].Price!.Value, 2, MidpointRounding.AwayFromZero));
        var reopened = RoundTripResult(estimate, "reviewed-source-result.json");
        reopened.SourceSelection!.Sources[1].Included.Should().BeFalse();
        reopened.ScopeNotice.Should().StartWith(EstimatePreflightPolicy.NeutralRecordScopeNotice).And.Contain("מוחרגים שלא נמדדו");
        var output = EstimateExcelWriter.Write(reopened, _directory, "SIMULATION-SCOPED",
            new EstimateExcelWriter.WriteOptions(ProjectTitle: Label));
        using var zip = ZipFile.OpenRead(output.XlsxPath);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string[] Texts(string entry)
        {
            using var stream = zip.GetEntry(entry)!.Open();
            return XDocument.Load(stream).Descendants(ns + "t").Select(t => t.Value).ToArray();
        }
        Texts("xl/worksheets/sheet1.xml").Should().Contain(reopened.ScopeNotice!);
        var identity = Texts("xl/worksheets/sheet5.xml");
        identity.Should().Contain("SIMULATION architecture excluded").And.Contain("not-measured.dwg");
        identity.Should().Contain(t => t.Contains(Approver));
        identity.Should().Contain(t => t.Contains("לא ידוע"));
        // Removing or changing the source-scope claim must not create an apparently complete export.
        var node = JsonNode.Parse(JsonSerializer.Serialize(EstimateResultArtifact.From(reopened), Json))!;
        node["source_selection"] = null;
        var tampered = EstimateResultArtifactReader.Read(node.ToJsonString(), Json);
        EstimatePreflightPolicy.CanExport(tampered).Should().BeFalse();
    }

    [Fact]
    public void GeneralMeasurementsAndRealCatalogMappingsSurviveUndecidedEarthworks_ButExportDoesNot()
    {
        var measured = RoundTrip(new[] { Record("general-kerb", "KERB-NEW", "length", "מטר", 75.125) },
            "general-measurement-before-scope.json");
        var approved = ApplyFixtureDecisions(measured,
            new[] { Decision("layer:KERB-NEW|length", "U51.06.1900") });
        _profile.Estimate.Earthworks.Requested = null;
        _profile.Estimate.Earthworks.DecidedBy = null;
        _profile.Estimate.Earthworks.DecidedAtUtc = null;
        _profile.Estimate.Earthworks.Reason = null;
        var notAssessed = new DeliveryFinding
        {
            Domain = "estimate", Code = EstimatePreflightPolicy.EarthworksNotAssessedCode,
            Severity = FindingSeverity.ReviewRequired, Title = "Earthworks not assessed; never zero/excluded",
        };
        var review = EstimateBuilder.Build(approved, _catalog, _profile, "general-before-earthworks",
            new[] { notAssessed });
        review = RoundTripResult(review, "general-before-earthworks-result.json");
        review.Lines.Should().ContainSingle().Which.BoqQuantity.Should().Be(75.125);
        review.Lines.Should().OnlyContain(line => line.MeasurementMethod == "explicit-synthetic-fixture-length");
        review.Findings.Should().Contain(finding => finding.Code == EstimatePreflightPolicy.EarthworksNotAssessedCode);
        AssertBlockedWithoutWorkbook(review, "undecided-earthworks");
        _profile.Estimate.Earthworks.Requested.Should().BeNull();

        // A fresh simulation-only explicit decision and fresh BUILD resolve the
        // finding. Neither zero quantities nor editing the old finding is a retry.
        _profile.Estimate.Earthworks.Requested = false;
        _profile.Estimate.Earthworks.DecidedBy = Approver;
        _profile.Estimate.Earthworks.DecidedAtUtc = DecisionTime;
        _profile.Estimate.Earthworks.Reason = "Synthetic kerb reference only; no real scope approval";
        WriteAndReadBack(Build(approved, "scope-retry"),
            new() { ["U51.06.1900"] = (75.125m, _catalog.Prices["U51.06.1900"].Price!.Value) }, "scope-retry");
    }

    [Fact]
    public void MixedMetadata_HasNoDefaultOrExport_UntilFreshEvidenceAndExplicitDecision()
    {
        var mixed = RoundTrip(new List<NeutralQuantityRecord>
        {
            Record("mixed-a", "Q742", "count", "יח'", 1, "BENCH"),
            Record("mixed-b", "Q742", "count", "יח'", 1, "TREE"),
        }, "mixed-records.json");
        Propose(mixed).Should().BeEmpty();
        var blocked = Build(mixed, "mixed");
        blocked.Lines.Should().OnlyContain(line => !line.IncludedInTotals && line.Total == null);
        AssertBlockedWithoutWorkbook(blocked, "mixed");

        var corrected = RoundTrip(new List<NeutralQuantityRecord>
        {
            Record("fresh-a", "Q742", "count", "יח'", 1, "מתקן אופניים"),
            Record("fresh-b", "Q742", "count", "יח'", 1, "מתקן אופניים"),
        }, "fresh-records-after-review.json");
        Propose(corrected).Should().Contain(proposal => proposal.ProposedCode == "U40.02.1230");
        var ready = Build(ApplyFixtureDecisions(corrected,
            new[] { Decision("layer:Q742|count", "U40.02.1230") }), "mixed-retry");
        WriteAndReadBack(ready, new() { ["U40.02.1230"] = (2m, _catalog.Prices["U40.02.1230"].Price!.Value) }, "mixed-retry");
    }

    [Fact]
    public void WrongUnitBinding_IsBlocked_AndFreshCorrectUnitRetriesWithoutConversion()
    {
        var malformed = ApplyFixtureDecisions(new[] { Record("wrong-unit", "KERB-NEW", "area", "מ\"ר", 12.5) },
            new[] { Decision("layer:KERB-NEW|area", "U51.06.1900") });
        var blocked = Build(RoundTrip(malformed, "malformed-unit-records.json"), "wrong-unit");
        blocked.Lines.Single().Findings.Should().Contain(finding => finding.Code == EstimateFindingCodes.UnitMismatch);
        blocked.Lines.Single().Total.Should().BeNull();
        AssertBlockedWithoutWorkbook(blocked, "wrong-unit");

        var repaired = ApplyFixtureDecisions(new[] { Record("fresh-length", "KERB-NEW", "length", "מטר", 12.5) },
            new[] { Decision("layer:KERB-NEW|length", "U51.06.1900") });
        WriteAndReadBack(Build(repaired, "unit-retry"),
            new() { ["U51.06.1900"] = (12.5m, _catalog.Prices["U51.06.1900"].Price!.Value) }, "unit-retry");
    }

    [Fact]
    public void MissingSourceHash_CannotExport_EvenIfPriceAndMappingExist_AndFreshTraceRetries()
    {
        var missing = Record("missing-source", "KERB-NEW", "length", "מטר", 10, sourceHash: "");
        var broken = Build(ApplyFixtureDecisions(new[] { missing },
            new[] { Decision("layer:KERB-NEW|length", "U51.06.1900") }), "missing-source");
        EstimatePreflightPolicy.ExportBlockingReasons(broken).Should()
            .Contain(reason => reason.Contains("source-drawing-hash-missing"));
        AssertBlockedWithoutWorkbook(broken, "missing-source");

        var fresh = ApplyFixtureDecisions(new[] { Record("fresh-source", "KERB-NEW", "length", "מטר", 10) },
            new[] { Decision("layer:KERB-NEW|length", "U51.06.1900") });
        WriteAndReadBack(Build(fresh, "source-retry"),
            new() { ["U51.06.1900"] = (10m, _catalog.Prices["U51.06.1900"].Price!.Value) }, "source-retry");
    }

    [Fact]
    public void CatalogBoundQuote_ChangedHashItemOrUnitNeverFallsBackToValidCatalogPrice()
    {
        const string code = "U51.06.1900";
        using var geometry = JsonDocument.Parse(File.ReadAllText(_sourcePath));
        var points = geometry.RootElement.GetProperty("geometry").GetProperty("kerbA");
        var measuredLength = Math.Sqrt(Math.Pow(points[1][0].GetDouble() - points[0][0].GetDouble(), 2) +
            Math.Pow(points[1][1].GetDouble() - points[0][1].GetDouble(), 2));
        var records = ApplyFixtureDecisions(new[] { Record("bound-price-kerb", "ANY-PRICE-LAYER", "length", "מטר", measuredLength) },
            new[] { Decision("layer:ANY-PRICE-LAYER|length", code) });
        var context = ProjectPriceApprovalPolicy.Capture(_profile, _catalog, code, "מטר");
        var approval = ProjectPriceApprovalPolicy.Approve(context, 85.50m, "SIMULATION-QUOTE-BOUND",
            "Explicit synthetic quote solely to exercise exact item binding", Approver, DecisionTime, true)!;
        _profile.Estimate.ProjectOverrides.Add(new()
        {
            ItemCode = context.ItemCode, Price = approval.Price, Source = approval.Source, Reason = approval.Reason,
            ApprovedBy = approval.ApprovedBy, ApprovedAtUtc = approval.ApprovedAtUtc,
            ApprovedCatalogId = context.CatalogId, ApprovedCatalogHash = context.CatalogHash,
            ApprovedCatalogItemFingerprint = context.ItemFingerprint, ExpectedUnit = context.Unit,
        });
        var valid = Build(records, "catalog-bound-quote");
        valid.Lines.Single().PriceStatus.Should().Be(PriceStatus.ProjectOverride);
        valid.Lines.Single().Price.Should().Be(85.50m);
        WriteAndReadBack(valid, new() { [code] = ((decimal)measuredLength, 85.50m) }, "catalog-bound-quote");
        foreach (var mutation in new[] { "hash", "item", "unit", "partial" })
        {
            var profile = RoundTrip(_profile, "price-binding-" + mutation + "-profile.json");
            var price = profile.Estimate.ProjectOverrides.Single();
            if (mutation == "hash") price.ApprovedCatalogHash = new string('b', 64);
            if (mutation == "item") price.ApprovedCatalogItemFingerprint = new string('c', 64);
            if (mutation == "unit") price.ExpectedUnit = "m2";
            if (mutation == "partial") price.ApprovedCatalogId = null;
            var blocked = Build(records, "price-binding-" + mutation, profile);
            blocked.Lines.Single().Price.Should().BeNull("an invalid quote must not fall back to the valid catalog price");
            blocked.Lines.Single().PriceStatus.Should().Be(PriceStatus.MissingPrice);
            blocked.Lines.Single().Findings.Should().Contain(f => f.Code == EstimateFindingCodes.PriceSourceUnverified);
            AssertBlockedWithoutWorkbook(blocked, "price-binding-" + mutation);
        }
    }

    [Fact]
    public void RealMissingPriceAndIncompleteOverride_Block_UntilExplicitSimulationQuoteAndRetry()
    {
        const string code = "U51.32.0810";
        (_catalog.Prices.TryGetValue(code, out var price) ? price.Price : null).Should().BeNull();
        var records = ApplyFixtureDecisions(new[] { Record("arrow", "TR-MARK-ARW-BL", "count", "יח'", 3, "arrow-D") },
            new[] { Decision("layer:TR-MARK-ARW-BL|count", code) });
        var missing = Build(records, "missing-price");
        missing.Lines.Single().Price.Should().BeNull();
        missing.Lines.Single().Total.Should().BeNull();
        missing.Lines.Single().PriceStatus.Should().Be(PriceStatus.MissingPrice);
        AssertBlockedWithoutWorkbook(missing, "missing-price");

        _profile.Estimate.ProjectOverrides.Add(new ProjectProfile.EstimateProfile.PriceOverride
        {
            ItemCode = code, Price = 85.50m, Source = "SIMULATION-ONLY-QUOTE",
            Reason = "Explicit test input, not a real price or procurement authority",
        });
        var incomplete = Build(records, "incomplete-quote");
        incomplete.Lines.Single().Findings.Should().Contain(finding => finding.Code == EstimateFindingCodes.OverrideIncomplete);
        AssertBlockedWithoutWorkbook(incomplete, "incomplete-quote");

        _profile.Estimate.ProjectOverrides.Single().ApprovedBy = Approver;
        _profile.Estimate.ProjectOverrides.Single().ApprovedAtUtc = DecisionTime;
        _profile.Estimate.ProjectOverrides.Single().ApprovedCatalogId = _catalog.SnapshotId;
        _profile.Estimate.ProjectOverrides.Single().ApprovedCatalogHash = _catalog.FileHash;
        _profile.Estimate.ProjectOverrides.Single().ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(_catalog.Items[code]);
        _profile.Estimate.ProjectOverrides.Single().ExpectedUnit = _catalog.Items[code].Unit.Canonical;
        var wrongCatalog = RoundTrip(_profile, "mismatched-simulation-quote-profile.json");
        wrongCatalog.Estimate.ProjectOverrides.Single().ApprovedCatalogHash = new string('b', 64);
        AssertBlockedWithoutWorkbook(Build(records, "mismatched-quote", wrongCatalog), "mismatched-quote");
        var profile = RoundTrip(_profile, "explicit-simulation-quote-profile.json");
        var ready = Build(records, "price-retry", profile);
        ready.Lines.Single().PriceStatus.Should().Be(PriceStatus.ProjectOverride);
        ready.Lines.Single().PriceDecisionSource.Should().Be("SIMULATION-ONLY-QUOTE");
        WriteAndReadBack(ready, new() { [code] = (3m, 85.50m) }, "price-retry");
    }

    private EstimateResult Build(IReadOnlyList<NeutralQuantityRecord> records, string name, ProjectProfile? profile = null)
    {
        var built = EstimateBuilder.Build(records, _catalog, profile ?? _profile, "SIMULATION-" + name);
        return RoundTripResult(built, name + "-estimate-result.json");
    }

    private EstimateResult RoundTripResult(EstimateResult built, string filename)
    {
        var path = Path.Combine(_directory, filename);
        File.WriteAllText(path, JsonSerializer.Serialize(EstimateResultArtifact.From(built), Json));
        var restored = EstimateResultArtifactReader.Read(File.ReadAllText(path), Json);
        restored.CleanTotal.Should().Be(built.CleanTotal);
        restored.Status.Should().Be(built.Status);
        restored.Lines.Select(line => (line.RecordId, line.Status, line.PriceStatus, line.Total))
            .Should().Equal(built.Lines.Select(line => (line.RecordId, line.Status, line.PriceStatus, line.Total)));
        return restored;
    }

    private void AssertBlockedWithoutWorkbook(EstimateResult estimate, string name)
    {
        EstimatePreflightPolicy.CanExport(estimate).Should().BeFalse();
        var output = Path.Combine(_directory, name + "-blocked-output");
        Action attempt = () => EstimateExcelWriter.Write(estimate, output, "MUST-NOT-EXIST");
        attempt.Should().Throw<InvalidOperationException>();
        Directory.Exists(output).Should().BeFalse("export must validate before creating a final-looking package");
        File.WriteAllText(Path.Combine(_directory, name + "-blockers.json"),
            JsonSerializer.Serialize(EstimatePreflightPolicy.ExportBlockingReasons(estimate), Json));
    }

    private EstimateExcelWriter.WriteResult WriteAndReadBack(EstimateResult estimate,
        Dictionary<string, (decimal Quantity, decimal Price)> expected, string name)
    {
        estimate.Status.Should().Be(DeliveryStatus.Ready);
        EstimatePreflightPolicy.ExportBlockingReasons(estimate).Should().BeEmpty();
        CatalogIdentity.ValidateEstimatePricing(estimate, _catalog).Should().BeEmpty();
        estimate.SourceSnapshotKind.Should().Be(EstimateBuildContext.NeutralRecordSet);
        estimate.ScopeNotice.Should().Be(EstimatePreflightPolicy.NeutralRecordScopeNotice);
        estimate.SourceDbMod.Should().BeNull();
        var total = expected.Sum(pair => Math.Round(pair.Value.Quantity * pair.Value.Price, 2, MidpointRounding.AwayFromZero));
        estimate.CleanTotal.Should().Be(total);
        var written = EstimateExcelWriter.Write(estimate, _directory, name + "-SIMULATION-ONLY",
            new EstimateExcelWriter.WriteOptions(ProjectTitle: Label, DrawingName: "synthetic-source.json", PreparedBy: Approver));
        using var zip = ZipFile.OpenRead(written.XlsxPath);
        using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = sheet.Descendants(ns + "row").ToList();
        using var traceStream = zip.GetEntry("xl/worksheets/sheet4.xml")!.Open();
        var trace = XDocument.Load(traceStream);
        var traceRows = trace.Descendants(ns + "row").ToList();
        using var stylesStream = zip.GetEntry("xl/styles.xml")!.Open();
        var styles = XDocument.Load(stylesStream).Root!.Element(ns + "cellXfs")!.Elements(ns + "xf").ToList();
        void AssertWrapAndHeight(XElement row, XElement cell)
        {
            var styleIndex = int.Parse(cell.Attribute("s")!.Value, CultureInfo.InvariantCulture);
            styles[styleIndex].Element(ns + "alignment")!.Attribute("wrapText")!.Value.Should().Be("1");
            row.Attribute("customHeight")!.Value.Should().Be("1");
            double.Parse(row.Attribute("ht")!.Value, CultureInfo.InvariantCulture).Should().BeGreaterThan(30);
        }
        var independentlyCalculated = 0m;
        foreach (var (code, value) in expected)
        {
            var row = rows.Single(candidate => candidate.Elements(ns + "c").Any(cell =>
                cell.Descendants(ns + "t").Any(text => text.Value == code)));
            var index = row.Attribute("r")!.Value;
            XElement Cell(string column) => row.Elements(ns + "c").Single(cell => cell.Attribute("r")!.Value == column + index);
            var quantity = decimal.Parse(Cell("E").Element(ns + "v")!.Value, CultureInfo.InvariantCulture);
            var price = decimal.Parse(Cell("F").Element(ns + "v")!.Value, CultureInfo.InvariantCulture);
            AssertWrapAndHeight(row, Cell("C"));
            Cell("C").Descendants(ns + "t").Single().Value.Should().StartWith(_catalog.Items[code].Description);
            foreach (var column in new[] { "E", "F", "G" })
                styles[int.Parse(Cell(column).Attribute("s")!.Value, CultureInfo.InvariantCulture)]
                    .Element(ns + "alignment")!.Attribute("vertical")!.Value.Should().Be("top");
            quantity.Should().Be(value.Quantity);
            price.Should().Be(value.Price);
            Cell("G").Element(ns + "f")!.Value.Should().Be($"ROUND(E{index}*F{index},2)");
            independentlyCalculated += Math.Round(quantity * price, 2, MidpointRounding.AwayFromZero);
        }
        independentlyCalculated.Should().Be(total);
        foreach (var row in traceRows.Where(row => row.Elements(ns + "c").Any(cell =>
                     cell.Descendants(ns + "t").Any(text => text.Value.StartsWith("L000")))))
        foreach (var cell in row.Elements(ns + "c")) AssertWrapAndHeight(row, cell);
        foreach (var line in estimate.Lines)
        {
            var traceRow = traceRows.Single(row => row.Elements(ns + "c").Any(cell =>
                cell.Descendants(ns + "t").Any(text => text.Value == $"{line.LineId} / {line.RecordId}")));
            var rowId = traceRow.Attribute("r")!.Value;
            var provenance = traceRow.Elements(ns + "c").Single(cell => cell.Attribute("r")!.Value == "H" + rowId)
                .Descendants(ns + "t").Single().Value;
            if (line.PriceStatus == PriceStatus.Priced)
                provenance.Should().StartWith("מחירון ").And.Contain(_catalog.SnapshotId)
                    .And.Contain(_catalog.FileHash).And.Contain(line.CatalogCode!)
                    .And.NotContain("@?").And.NotContain("אישור:");
            else if (line.PriceStatus == PriceStatus.ProjectOverride)
                provenance.Should().StartWith("מחיר פרויקט;").And.Contain(line.PriceDecisionSource!)
                    .And.Contain(line.PriceApprovedBy!).And.NotContain("@?");
        }
        sheet.Descendants(ns + "c").Should().NotContain(cell => (string?)cell.Attribute("t") == "e");
        sheet.Descendants(ns + "t").Should().Contain(text => text.Value.Contains(Label));
        sheet.Descendants(ns + "t").Should().Contain(text => text.Value == EstimatePreflightPolicy.NeutralRecordScopeNotice);
        sheet.Descendants(ns + "t").Should().NotContain(text => text.Value.Contains("RECURSIVE VERIFIED SOURCES") ||
            text.Value.Contains("DWG SHA-256") || text.Value.Contains("כמויות מהגיאומטריה בשרטוט"));
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        audit.RootElement.GetProperty("clean_total").GetDecimal().Should().Be(total);
        audit.RootElement.GetProperty("status").GetString().Should().Be("Ready");
        audit.RootElement.GetProperty("lines").GetArrayLength().Should().Be(estimate.Lines.Count);
        audit.RootElement.GetProperty("source_snapshot_kind").GetString().Should().Be(EstimateBuildContext.NeutralRecordSet);
        audit.RootElement.GetProperty("scope_notice").GetString().Should().Be(EstimatePreflightPolicy.NeutralRecordScopeNotice);
        audit.RootElement.GetProperty("source_drawing_hash").GetString().Should().Be(_sourceHash);
        audit.RootElement.GetProperty("price_book_hash").GetString().Should().Be(CatalogHash);
        using var manifest = JsonDocument.Parse(File.ReadAllText(written.ManifestPath));
        foreach (var (key, path) in new[] { ("workbook", written.XlsxPath), ("audit", written.AuditPath) })
            manifest.RootElement.GetProperty(key).GetProperty("sha256").GetString().Should().Be(ArtifactHash.Sha256OfFile(path));
        ArtifactHash.Sha256OfFile(written.ManifestPath).Should().Be(written.ManifestHash);
        return written;
    }

    private List<MappingProposal> Propose(IEnumerable<NeutralQuantityRecord> records) => MappingProposalEngine.Propose(
        records.GroupBy(record => record.Classification.RuleKey!).Select(group => new MappingProposalEngine.DiscoveredGroup(
            group.Key, group.First().Source.Layer, group.First().Measurement.Kind, group.First().Measurement.Unit,
            group.Count(), group.Sum(record => record.Measurement.RawValue),
            QuantityCadMetadataPolicy.Summarize(group.Select(record => record.Measurement)))), _catalog);

    public sealed record SimulationDecision(string RuleKey, string Code, string CatalogId, string CatalogHash,
        string ItemFingerprint, string ApprovedBy, DateTime ApprovedAtUtc);

    private SimulationDecision Decision(string rule, string code) => new(rule, code, _catalog.SnapshotId,
        _catalog.FileHash, CatalogIdentity.ItemFingerprint(_catalog.Items[code]), Approver, DecisionTime);

    private List<NeutralQuantityRecord> ApplyFixtureDecisions(IEnumerable<NeutralQuantityRecord> records,
        IReadOnlyList<SimulationDecision> decisions) => records.Select(record =>
    {
        // Test-only fixture construction, NOT the production engineer approval UI.
        var decision = decisions.Single(choice => choice.RuleKey == record.Classification.RuleKey);
        decision.CatalogHash.Should().Be(_catalog.FileHash);
        return new NeutralQuantityRecord
        {
            RecordId = record.RecordId, ProjectProfileId = record.ProjectProfileId, RunId = record.RunId,
            Source = record.Source, Measurement = record.Measurement,
            Classification = new QuantityClassification
            {
                RuleKey = decision.RuleKey, CandidateCatalogCode = decision.Code,
                ApprovedCatalogId = decision.CatalogId, ApprovedCatalogHash = decision.CatalogHash,
                ApprovedCatalogItemFingerprint = decision.ItemFingerprint,
                MappingApprovedBy = decision.ApprovedBy, MappingApprovedAtUtc = decision.ApprovedAtUtc,
            },
        };
    }).ToList();

    private NeutralQuantityRecord Record(string id, string layer, string kind, string unit, double quantity,
        string? blockName = null, string? sourceHash = null) => new()
    {
        RecordId = id, ProjectProfileId = _profile.ProfileId, RunId = "SIMULATION-SYNTHETIC-SOURCE",
        Source = new QuantitySource
        {
            Drawing = "synthetic-source.json", DrawingPath = _sourcePath, DrawingHash = sourceHash ?? _sourceHash,
            Handle = "SIM-" + id, EntityType = blockName == null ? "SYNTHETIC-POLYLINE" : "SYNTHETIC-INSERT", Layer = layer,
        },
        Measurement = new QuantityMeasurement
        {
            Kind = kind, Unit = unit, RawValue = quantity, Method = "explicit-synthetic-fixture-" + kind,
            Parameters = blockName == null ? new() : new() { ["cad_block_name_effective"] = blockName },
        },
        Classification = new QuantityClassification { RuleKey = $"layer:{layer}|{kind}" },
    };

    private T RoundTrip<T>(T value, string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    }

    private void WriteSummary(string scenario, EstimateResult estimate, EstimateExcelWriter.WriteResult written,
        Dictionary<string, (decimal Quantity, decimal Price)> expected) => File.WriteAllText(
        Path.Combine(_directory, "verification-summary.json"), JsonSerializer.Serialize(new
        {
            scenario, scope = Label, workbook = written.XlsxPath, audit = written.AuditPath, manifest = written.ManifestPath,
            clean_total = estimate.CleanTotal, source_kind = estimate.SourceSnapshotKind,
            catalog_hash = _catalog.FileHash,
            rows = expected.Select(pair => new { code = pair.Key, quantity = pair.Value.Quantity, price = pair.Value.Price,
                total = Math.Round(pair.Value.Quantity * pair.Value.Price, 2, MidpointRounding.AwayFromZero) }),
        }, Json));

    private static string FindCatalog()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        foreach (var root in new[] { directory.FullName, Path.Combine(directory.FullName, "MahodAI-Plugin") })
        {
            var path = Path.Combine(root, "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Pinned NTI workbook fixture was not found.");
    }
}
