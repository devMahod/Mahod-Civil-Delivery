using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Choice = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.EstimateWorkflowService.ReviewedMappingChoice;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Recorded measurements, real pinned catalog, production picker/decision writers/rebase/build/export.
/// The 297-record HW-CURB group is an explicitly limited offline test case, NEVER the project estimate.
/// Only a unique temporary profile receives synthetic review/price/scope decisions. The original run,
/// profile and DWGs are read-only. Global source failures are retained in a separate blocked replay.
/// No Civil process, live freshness claim, native drawing measurement or release approval.
/// </summary>
public sealed class Recorded57EstimateWorkflowReplayTests
{
    public const string RunDirectory = @"C:\Users\arthurf\AppData\Local\MahodAI_Civil3D\civil-delivery\runs\estimate-extract-20260909-152804-6b2958b1";
    private const string Rule = "layer:HW-CURB|length";
    private const string Code = "U51.06.1900";
    private const string CatalogHash = "90da59809602c4127fcf0b91c3f037c2b98d7b93288beba2c851e3a3550f313c";
    private const string RecordsHash = "fc312c30ca2dffe0caa51a9c964243a0e71f669d71db0e46f45103411ff3ae9c";
    private const string Approver = "SYNTHETIC-REPLAY-ONLY-NOT-ENGINEERING-AUTHORITY";
    private const string Notice = "REPLAY ONLY — 297 recorded HW-CURB measurements; synthetic decisions; NOT project 6422 estimate";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Recorded57FactAttribute : FactAttribute
    {
        public Recorded57FactAttribute()
        {
            if (!File.Exists(Path.Combine(RunDirectory, "neutral_quantity_records.json")))
                Skip = "The pinned Delivery57 native-run artifact is unavailable. Recorded replay was NOT run.";
        }
    }

    [Fact]
    public void BroadCatalogSearch_PrioritizesCompatibleItemsAcrossEntireBookBeforeLimitingVisibleRows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var catalog = new CatalogSnapshot { SnapshotId = "SEARCH-TEST-ONLY", FileHash = new string('a', 64) };
                for (var index = 0; index < 401; index++)
                {
                    var code = "TEST.AREA." + index.ToString("D4", CultureInfo.InvariantCulture);
                    catalog.Items.Add(code, new() { Code = code, Description = "shared search fixture", UnitRaw = "m2" });
                }
                catalog.Items.Add("TEST.LENGTH.LAST", new()
                {
                    Code = "TEST.LENGTH.LAST", Description = "shared search fixture", UnitRaw = "m",
                });
                var dialog = new CatalogPickerDialog("layer:TEST|length", "TEST", "length", "m", 1, 1,
                    catalog, Array.Empty<MappingProposal>());
                try
                {
                    ((TextBox)dialog.FindName("SearchBox")).Text = "shared search";
                    var grid = (DataGrid)dialog.FindName("Grid");
                    grid.Items.Count.Should().Be(400);
                    var rows = grid.Items.Cast<CatalogPickerDialog.Row>().ToList();
                    rows.First().Code.Should().Be("TEST.LENGTH.LAST",
                        "a compatible catalog item after the first 400 matches must remain discoverable");
                    rows.First().UnitCompatible.Should().BeTrue();
                    grid.SelectedItem.Should().BeNull();
                    dialog.SelectedCode.Should().BeNull("searching must never approve an engineering mapping");
                }
                finally { dialog.Close(); }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue();
        if (failure != null) throw new InvalidOperationException("Broad catalog search failed.", failure);
    }

    [Fact]
    public void IndependentGlobalCorridorMaterialAndSameIdSourceFailuresSurviveBuildWhileEquivalentEvidenceDeduplicates()
    {
        var findings = Enumerable.Range(1, 4).Select(index => new DeliveryFinding
        {
            Code = EstimatePreflightPolicy.CorridorOutOfDateCode, Domain = "estimate",
            Severity = FindingSeverity.ReviewRequired, Title = "Out of date corridor " + index,
            Message = "Rebuild corridor " + index,
        }).Concat(Enumerable.Range(1, 3).Select(index => new DeliveryFinding
        {
            Code = EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode, Domain = "estimate",
            Severity = FindingSeverity.ReviewRequired, Title = "Incomplete material series " + index,
            Message = "Missing station in series " + index,
        })).ToList();
        foreach (var source in new[] { "SOURCE-A.dwg", "SOURCE-B.dwg" })
            findings.Add(new()
            {
                FindingId = "SAME-PRODUCER-ID", Code = EstimateFindingCodes.MeasurementFailed,
                Domain = "estimate", Severity = FindingSeverity.Error,
                Title = "Cannot measure source", Message = "Identical failure message, different source identity",
                SourceRefs = { new() { SourceKind = "drawing", SourcePathOrUri = source, SourceHandle = "A1" } },
            });
        // A duplicate serialized equivalent (same content, potentially different allocation)
        // and a repeated reference must not multiply any blocker.
        var inputs = findings.Concat(new[] { findings[0], JsonSerializer.Deserialize<DeliveryFinding>(JsonSerializer.Serialize(findings[0]))! });
        var result = EstimateBuilder.Build(new[] { EstimateFixtures.Record("safe", "U51.01.0250", 25, "מטר") },
            EstimateFixtures.Snapshot(), EstimateFixtures.Profile(), preflightFindings: inputs);
        result.Findings.Should().HaveCount(9);
        result.Findings.Count(finding => finding.Code == EstimatePreflightPolicy.CorridorOutOfDateCode).Should().Be(4);
        result.Findings.Count(finding => finding.Code == EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode).Should().Be(3);
        result.Findings.Where(finding => finding.FindingId == "SAME-PRODUCER-ID").SelectMany(finding => finding.SourceRefs)
            .Select(source => source.SourcePathOrUri).Should().BeEquivalentTo("SOURCE-A.dwg", "SOURCE-B.dwg");
        result.Lines.Single().Findings.Should().HaveCount(9);
        EstimatePreflightPolicy.BlockingFindings(result).Should().HaveCount(9);
        EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
    }

    [Fact]
    public void EquivalentScanAndRecomputedDuplicateEvidenceKeepsFirstCallerIdentityOnce()
    {
        var records = new[]
        {
            EstimateFixtures.Record("r1", "U51.01.0250", 10, "מטר", handle: "DUP"),
            EstimateFixtures.Record("r2", "U51.01.0250", 10, "מטר", handle: "DUP"),
        };
        var first = DuplicateRiskDetector.Detect(records).Single(finding => finding.Code == EstimateFindingCodes.DuplicateSource);
        var equivalent = DuplicateRiskDetector.Detect(records).Single(finding => finding.Code == EstimateFindingCodes.DuplicateSource);
        equivalent.FindingId.Should().NotBe(first.FindingId);
        var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile(),
            preflightFindings: new[] { first, equivalent });
        result.Findings.Where(finding => finding.Code == EstimateFindingCodes.DuplicateSource).Should()
            .ContainSingle().Which.FindingId.Should().Be(first.FindingId);
        EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
    }

    [Recorded57Fact]
    [Trait("Category", "RecordedRunReplay")]
    public async Task RealMeasuredGroup_PickerReviewPersistenceRebaseCatalogPriceProjectPriceAndExport_WithoutApprovingProject()
    {
        var recordsPath = Path.Combine(RunDirectory, "neutral_quantity_records.json");
        ArtifactHash.Sha256OfFile(recordsPath).Should().Be(RecordsHash);
        var directory = Path.Combine(Environment.GetEnvironmentVariable("MAHOD_RECORDED57_EVIDENCE_DIR") ??
            Path.Combine(Path.GetTempPath(), "MahodAI-recorded57-estimate-replay"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var catalogPath = FindCatalog();
        var catalog = PriceBookXlsxLoader.Load(catalogPath, "nti-urban-082025");
        catalog.FileHash.Should().Be(CatalogHash);
        var selected = new List<NeutralQuantityRecord>();
        var sameLayer = new List<NeutralQuantityRecord>();
        var inputRecords = 0;
        await using (var stream = File.OpenRead(recordsPath))
        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<NeutralQuantityRecord>(stream, Json))
        {
            record.Should().NotBeNull();
            inputRecords++;
            if (record!.Classification.RuleKey == Rule) selected.Add(record);
            if (record.Classification.RuleKey is Rule or "layer:HW-CURB|area") sameLayer.Add(record);
        }
        inputRecords.Should().BeGreaterThan(selected.Count, "this is explicitly a group replay, not the full drawing");
        selected.Should().HaveCount(297);
        sameLayer.Should().HaveCount(302, "the five measured area alternatives must stay visible through review");
        selected.Sum(record => record.Measurement.RawValue).Should().BeApproximately(29287.62954513264, 1e-8);
        selected.Should().OnlyContain(record => record.Classification.CandidateCatalogCode == null &&
            record.Classification.MappingApprovedBy == null);
        var originalRecordsJson = JsonSerializer.Serialize(selected, Json);
        var originalLayerJson = JsonSerializer.Serialize(sameLayer, Json);
        File.WriteAllText(Path.Combine(directory, "recorded-group-unapproved.json"), originalRecordsJson);
        var originalFindings = JsonSerializer.Deserialize<List<DeliveryFinding>>(
            File.ReadAllText(Path.Combine(RunDirectory, "quantity_preflight.json")), Json)!;
        // Preserve the actual recorded XREF insertion identities. A neutral replay is
        // not a live host snapshot, but external rows still need their recorded provenance.
        var headerLines = File.ReadLines(Path.Combine(RunDirectory, "estimate_scan.json"))
            .TakeWhile(line => !line.TrimStart().StartsWith("\"Records\":", StringComparison.Ordinal));
        using var scanHeader = JsonDocument.Parse(string.Join("\n", headerLines) + "\n\"Records\": []\n}");
        var externalSources = scanHeader.RootElement.GetProperty("ExternalSources")
            .Deserialize<List<EstimateExternalSource>>(Json)!;
        var recordedHostPath = scanHeader.RootElement.GetProperty("SourceDrawing").GetString()!;
        var recordedHostHash = scanHeader.RootElement.GetProperty("SourceDrawingHash").GetString()!;
        CatalogIdentity.IsValidSha256(recordedHostHash).Should().BeTrue();
        EstimateResult Build(IReadOnlyList<NeutralQuantityRecord> records, ProjectProfile decisions,
            string id, IEnumerable<DeliveryFinding>? findings = null) => EstimateBuilder.Build(records, catalog, decisions,
                id, findings, EstimateTraceIdentity.InferHeadless(records, decisions) with
                {
                    // Multi-source headless inference cannot invent a master drawing hash.
                    // Use the exact original scan's recorded master, keep neutral replay kind.
                    SourceDrawingPath = recordedHostPath, SourceDrawingHash = recordedHostHash,
                    ExternalSources = externalSources,
                });
        var globalBlockers = originalFindings.Where(finding => EstimatePreflightPolicy.IsBlocking(finding) &&
            finding.AffectedRecordIds.Count == 0).ToList();
        globalBlockers.Should().HaveCount(10);

        var profile = FixtureProfile(catalogPath, catalog);
        var target = Path.Combine(directory, "SYNTHETIC-REPLAY-profile.yaml");
        var scan = FixtureScan(profile, target, sameLayer);
        var workflow = new EstimateWorkflowService();
        var unapproved = Build(selected, profile, "UNAPPROVED-RECORDED-GROUP");
        unapproved.Lines.Should().HaveCount(selected.Count).And.OnlyContain(line => !line.IncludedInTotals);
        EstimatePreflightPolicy.CanExport(unapproved).Should().BeFalse();

        // Actual production picker XAML/handlers are linked into the offline test assembly.
        // Cancellation, incompatible unit, compatible code search, explicit modal approval.
        var selectedCode = ExercisePicker(catalog);
        selectedCode.Should().Be(Code);
        File.Exists(target).Should().BeFalse("selecting a catalog row alone must not persist approval");
        var wrongUnit = catalog.Items.Values.First(item => item.Unit.Canonical == "m2").Code;
        Action badMapping = () => workflow.SaveReviewedMappings(profile, catalog, scan,
            new[] { new Choice(Rule, wrongUnit) }, Approver, target, scan.ProfileWriteState!);
        badMapping.Should().Throw<InvalidOperationException>().WithMessage("*Unit mismatch*");
        profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
        File.Exists(target).Should().BeFalse();

        var saved = workflow.SaveReviewedMappings(profile, catalog, scan,
            new[] { new Choice(Rule, selectedCode) }, Approver, target, scan.ProfileWriteState!);
        profile.Estimate.IgnoredRuleDecisions.Should().BeEmpty("no record or dimension is excluded to pass this replay");
        var reopened = ProjectProfileLoader.LoadFromFile(saved.Path).Profile!;
        reopened.Estimate.QuantitySources.Rules.Should().ContainSingle().Which.ApprovedBy.Should().Be(Approver);
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(scan, reopened, saved, new[] { Rule });
        rebased.Records.Should().HaveCount(sameLayer.Count);
        rebased.Records.Select(record => record.RecordId).Should().Equal(sameLayer.Select(record => record.RecordId));
        for (var index = 0; index < sameLayer.Count; index++)
        {
            rebased.Records[index].Measurement.Should().BeSameAs(sameLayer[index].Measurement);
            rebased.Records[index].Source.Should().BeSameAs(sameLayer[index].Source);
        }
        var mappedRecords = rebased.Records.Where(record => record.Classification.RuleKey == Rule).ToList();
        mappedRecords.Should().HaveCount(297).And.OnlyContain(record => CatalogIdentity.IsClassificationCurrent(record.Classification, catalog));
        mappedRecords.SelectMany(record => record.Findings).Should().NotContain(finding => finding.Code == EstimateFindingCodes.Unmapped);
        rebased.Records.Where(record => record.Classification.RuleKey != Rule).Should().HaveCount(5)
            .And.OnlyContain(record => record.Classification.CandidateCatalogCode == null);
        JsonSerializer.Serialize(sameLayer, Json).Should().Be(originalLayerJson);
        JsonSerializer.Serialize(selected, Json).Should().Be(originalRecordsJson, "rebase must preserve original scan evidence");
        var completeLayer = Build(rebased.Records, reopened, "RECORDED-LAYER-STILL-HAS-UNREVIEWED-AREAS");
        EstimatePreflightPolicy.CanExport(completeLayer).Should().BeFalse();
        completeLayer.Lines.Should().HaveCount(302);
        var expectedQuantity = selected.Sum(record => Math.Round((decimal)record.Measurement.RawValue, 4, MidpointRounding.AwayFromZero));
        var catalogPrice = catalog.Prices[Code].Price!.Value;
        var catalogBuild = Build(mappedRecords, reopened, "RECORDED-GROUP-SYNTHETIC-MAPPING");
        var catalogExport = AssertExport(catalogBuild, catalog, expectedQuantity, catalogPrice, directory, "catalog-price");

        // Carry all actual scan findings forward: a successful group calculation cannot turn
        // unresolved earthworks, corridor/source coverage or actual extraction failures into approval.
        var projectBlocked = Build(mappedRecords, reopened,
            "RECORDED-GROUP-WITH-ORIGINAL-PROJECT-BLOCKERS", originalFindings);
        EstimatePreflightPolicy.CanExport(projectBlocked).Should().BeFalse();
        projectBlocked.Lines.Should().HaveCount(297).And.OnlyContain(line => !line.IncludedInTotals && line.Total == null);
        foreach (var finding in globalBlockers)
            projectBlocked.Findings.Should().Contain(value => value.FindingId == finding.FindingId);
        Action forbiddenExport = () => EstimateExcelWriter.Write(projectBlocked,
            Path.Combine(directory, "MUST-NOT-EXIST"), "NOT-AN-APPROVED-PROJECT");
        forbiddenExport.Should().Throw<InvalidOperationException>();
        Directory.Exists(Path.Combine(directory, "MUST-NOT-EXIST")).Should().BeFalse();

        // Production price context/writer; cancellation is a null decision with byte-identical YAML.
        var context = ProjectPriceApprovalPolicy.CaptureForRecords(reopened, catalog, Code, mappedRecords);
        var beforeCancelHash = ArtifactHash.Sha256OfFile(target);
        ProjectPriceApprovalPolicy.Approve(context, 85.50m, "REPLAY-ONLY-QUOTE", Notice, Approver,
            DateTime.UtcNow, explicitlyApproved: false).Should().BeNull();
        ArtifactHash.Sha256OfFile(target).Should().Be(beforeCancelHash);
        reopened.Estimate.ProjectOverrides.Should().BeEmpty();
        var quote = ProjectPriceApprovalPolicy.Approve(context, 85.50m, "REPLAY-ONLY-QUOTE", Notice, Approver,
            DateTime.UtcNow, explicitlyApproved: true)!;
        var pricedSave = ProjectPriceApprovalWriter.Save(reopened, catalog, quote, target, rebased.ProfileWriteState!);
        var pricedProfile = ProjectProfileLoader.LoadFromFile(target).Profile!;
        var repricedScan = EstimateWorkflowService.RebaseAfterProfileDecision(rebased, pricedProfile, pricedSave);
        var overrideBuild = Build(repricedScan.Records.Where(record => record.Classification.RuleKey == Rule).ToList(),
            pricedProfile, "RECORDED-GROUP-SYNTHETIC-QUOTE");
        overrideBuild.Lines.Should().OnlyContain(line => line.PriceStatus == PriceStatus.ProjectOverride &&
            line.PriceApprovedBy == Approver && line.PriceDecisionSource == "REPLAY-ONLY-QUOTE");
        var overrideExport = AssertExport(overrideBuild, catalog, expectedQuantity, 85.50m, directory, "project-price");
        ArtifactHash.Sha256OfFile(catalogExport.XlsxPath).Should().Be(catalogExport.XlsxHash);
        ArtifactHash.Sha256OfFile(recordsPath).Should().Be(RecordsHash);
        JsonSerializer.Serialize(selected, Json).Should().Be(originalRecordsJson);
        File.WriteAllText(Path.Combine(directory, "verification-summary.json"), JsonSerializer.Serialize(new
        {
            scope = Notice, native_execution = false, real_project_approved = false,
            source = recordsPath, source_sha256 = RecordsHash, input_records = inputRecords,
            recorded_host = recordedHostPath, recorded_host_sha256 = recordedHostHash,
            replay_records = selected.Count, unrounded_quantity = selected.Sum(record => record.Measurement.RawValue),
            retained_unapproved_area_records = 5, entire_layer_export_blocked = !EstimatePreflightPolicy.CanExport(completeLayer),
            four_decimal_boq_quantity = expectedQuantity, catalog_sha256 = catalog.FileHash,
            catalog_price = catalogPrice, catalog_total = catalogBuild.CleanTotal,
            synthetic_project_price = 85.50m, synthetic_project_total = overrideBuild.CleanTotal,
            catalog_workbook = catalogExport.XlsxPath, synthetic_project_price_workbook = overrideExport.XlsxPath,
            original_global_blockers = globalBlockers.Select(finding => new { finding.Code, finding.Title }),
            original_project_export_blocked = !EstimatePreflightPolicy.CanExport(projectBlocked),
            live_profile_modified = false, exclusions_added = pricedProfile.Estimate.IgnoredRuleDecisions.Count,
        }, Json));
    }

    private static ProjectProfile FixtureProfile(string catalogPath, CatalogSnapshot catalog)
    {
        var profile = new ProjectProfile { ProfileId = "6422", ProjectName = Notice };
        profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        profile.Estimate.Catalog.CatalogFile = catalogPath;
        profile.Estimate.Catalog.CatalogFileHash = catalog.FileHash;
        profile.Estimate.Pricing.PriceBookSnapshotId = catalog.SnapshotId;
        profile.Estimate.Pricing.PriceBookHash = catalog.FileHash;
        profile.Estimate.PriceBooks.Add(new() { Id = catalog.SnapshotId, File = catalogPath, FileHash = catalog.FileHash });
        profile.Estimate.Earthworks.Requested = false;
        profile.Estimate.Earthworks.DecidedBy = Approver;
        profile.Estimate.Earthworks.DecidedAtUtc = DateTime.UtcNow;
        profile.Estimate.Earthworks.Reason = "Synthetic test-case scope is exactly one recorded kerb length group; no project earthworks decision.";
        return profile;
    }

    private static EstimateWorkflowService.ScanResult FixtureScan(ProjectProfile profile, string target,
        List<NeutralQuantityRecord> records) => new()
    {
        RunId = "SYNTHETIC-REVIEW-OF-RECORDED57-GROUP", ProjectProfileId = profile.ProfileId,
        ProfileSource = target, SourceDrawing = records[0].Source.DrawingPath!,
        SourceDrawingHash = records[0].Source.DrawingHash, SourceDbMod = null,
        ProjectProfileHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
        ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
        ProfileWriteState = ProfileCasTest.For(profile, target), DatabaseRevision = "OFFLINE-RECORDED-REPLAY",
        Records = records, ScannedEntities = records.Count, DiscoveryMode = true, Status = DeliveryStatus.ReviewRequired,
    };

    private static string ExercisePicker(CatalogSnapshot catalog)
    {
        Exception? failure = null;
        string? selected = null;
        var thread = new Thread(() =>
        {
            try
            {
                CatalogPickerDialog Picker() => new(Rule, "HW-CURB", "length", "מטר", 29287.62954513264, 297,
                    catalog, Array.Empty<MappingProposal>()) { ShowInTaskbar = false, Opacity = 0 };
                var cancelled = Picker();
                ((TextBox)cancelled.FindName("SearchBox")).Text = Code;
                var cancelledGrid = (DataGrid)cancelled.FindName("Grid");
                cancelledGrid.SelectedItem = cancelledGrid.Items.Cast<CatalogPickerDialog.Row>().Single(row => row.Code == Code);
                ((Button)cancelled.FindName("BtnOk")).IsEnabled.Should().BeTrue();
                cancelled.SelectedCode.Should().BeNull();
                cancelled.Close();
                cancelled.SelectedCode.Should().BeNull();

                var dialog = Picker();
                var grid = (DataGrid)dialog.FindName("Grid");
                var search = (TextBox)dialog.FindName("SearchBox");
                var wrongCode = catalog.Items.Values.First(item => item.Unit.Canonical == "m2").Code;
                search.Text = wrongCode;
                grid.SelectedItem = grid.Items.Cast<CatalogPickerDialog.Row>().Single(row => row.Code == wrongCode);
                ((Button)dialog.FindName("BtnOk")).IsEnabled.Should().BeFalse();
                dialog.SelectedCode.Should().BeNull();
                search.Text = Code;
                grid.SelectedItem = grid.Items.Cast<CatalogPickerDialog.Row>().Single(row => row.Code == Code);
                ((Button)dialog.FindName("BtnOk")).IsEnabled.Should().BeTrue();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timer.Tick += (_, _) => { timer.Stop(); dialog.Close(); };
                timer.Start();
                dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    ((Button)dialog.FindName("BtnOk")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                dialog.ShowDialog().Should().BeTrue("the actual button handler completes explicit catalog selection");
                timer.Stop();
                selected = dialog.SelectedCode;
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(25)).Should().BeTrue();
        if (failure != null) throw new InvalidOperationException("Recorded catalog picker replay failed.", failure);
        return selected!;
    }

    private static EstimateExcelWriter.WriteResult AssertExport(EstimateResult result, CatalogSnapshot catalog,
        decimal expectedQuantity, decimal price, string directory, string name)
    {
        result.Lines.Should().HaveCount(297).And.OnlyContain(line => line.IncludedInTotals);
        EstimatePreflightPolicy.ExportBlockingReasons(result).Should().BeEmpty();
        result.SourceSnapshotKind.Should().Be(EstimateBuildContext.NeutralRecordSet);
        result.ScopeNotice.Should().Be(EstimatePreflightPolicy.NeutralRecordScopeNotice);
        CatalogIdentity.ValidateEstimatePricing(result, catalog).Should().BeEmpty();
        var expectedTotal = Math.Round(expectedQuantity * price, 2, MidpointRounding.AwayFromZero);
        result.CleanTotal.Should().Be(expectedTotal);
        var written = EstimateExcelWriter.Write(result, directory, "REPLAY-ONLY-" + name,
            new EstimateExcelWriter.WriteOptions(ProjectTitle: Notice, PreparedBy: Approver));
        using var zip = ZipFile.OpenRead(written.XlsxPath);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var row = xml.Descendants(ns + "row").Single(candidate => candidate.Elements(ns + "c")
            .Any(cell => cell.Descendants(ns + "t").Any(value => value.Value == Code)));
        var index = row.Attribute("r")!.Value;
        XElement Cell(string column) => row.Elements(ns + "c").Single(cell => cell.Attribute("r")!.Value == column + index);
        decimal.Parse(Cell("E").Element(ns + "v")!.Value, CultureInfo.InvariantCulture).Should().Be(expectedQuantity);
        decimal.Parse(Cell("F").Element(ns + "v")!.Value, CultureInfo.InvariantCulture).Should().Be(price);
        Cell("G").Element(ns + "f")!.Value.Should().Be($"ROUND(E{index}*F{index},2)");
        xml.Descendants(ns + "t").Should().Contain(value => value.Value.Contains(Notice));
        xml.Descendants(ns + "t").Should().Contain(value => value.Value == EstimatePreflightPolicy.NeutralRecordScopeNotice);
        using var audit = JsonDocument.Parse(File.ReadAllText(written.AuditPath));
        audit.RootElement.GetProperty("lines").GetArrayLength().Should().Be(297);
        audit.RootElement.GetProperty("clean_total").GetDecimal().Should().Be(expectedTotal);
        return written;
    }

    private static string FindCatalog()
    {
        var path = Path.GetFullPath(Path.Combine(TestPaths.PluginSourceDir, "..",
            "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx"));
        if (File.Exists(path)) return path;
        throw new FileNotFoundException("Pinned NTI catalog fixture was not found.", path);
    }
}
