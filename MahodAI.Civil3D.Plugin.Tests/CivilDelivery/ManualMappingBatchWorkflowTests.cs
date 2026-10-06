using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;
using Batch = MahodAI.Civil3D.Plugin.Tests.CivilDelivery.ManualMappingBatchDialogTests;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Synthetic new drawing/layer identities and explicit TEST reviewer in a unique temp
/// profile. Uses real dialog, profile writer/reopen, rebase, builder and XLSX writer.
/// No native scan, drawing, real engineering consent or production profile writes.
/// </summary>
public sealed class ManualMappingBatchWorkflowTests(ITestOutputHelper output)
{
    private const string ProfileId = "SYNTHETIC-NEW-PROJECT-BATCH-ONLY";
    private const string Drawing = @"C:\SYNTHETIC-ONLY\arbitrary-new-drawing.dwg";
    private const string Reviewer = "SYNTHETIC TEST ONLY - NOT ENGINEERING AUTHORITY";
    private static readonly string DrawingHash = new('b', 64);

    private static NeutralQuantityRecord Record(string key, string handle, double quantity) => new()
    {
        RecordId = "TEST-" + handle, ProjectProfileId = ProfileId, RunId = "SYNTHETIC-BATCH-SCAN",
        Source = new()
        {
            Drawing = "arbitrary-new-drawing.dwg", DrawingPath = Drawing, DrawingHash = DrawingHash,
            Handle = handle, EntityType = "LINE", Layer = key.Split(':')[1].Split('|')[0],
        },
        Measurement = new() { Kind = "length", Method = "line-length", RawValue = quantity, Unit = "m" },
        Classification = new() { RuleKey = key }, Status = DeliveryStatus.ReviewRequired,
        Findings =
        {
            new() { Code = EstimateFindingCodes.Unmapped, Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
                Title = "SYNTHETIC undecided mapping", AffectedRecordIds = { "TEST-" + handle } },
        },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitBatchSaveReopensAndReusesExactRulesThenPricesAndExportsWithoutHidingCoverage(bool unresolvedCoverage)
    {
        var evidenceParent = Environment.GetEnvironmentVariable("MHD_BATCH_MAPPING_EVIDENCE_DIR") ??
            Path.Combine(Path.GetTempPath(), "MahodAI-SYNTHETIC-batch-workflow");
        var directory = Path.Combine(evidenceParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "SYNTHETIC-ONLY-project.yaml");
        var catalog = Batch.Catalog();
        var profile = new ProjectProfile { ProfileId = ProfileId, ProjectName = "SYNTHETIC ONLY - not a project approval" };
        profile.Estimate.QuantitySources.SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy;
        profile.Estimate.QuantitySources.XrefPolicy = EstimatePreflightPolicy.IncludeXrefsPolicy;
        profile.Estimate.Catalog.CatalogFile = "SYNTHETIC-ONLY-catalog.xlsx";
        profile.Estimate.Catalog.CatalogFileHash = catalog.FileHash;
        profile.Estimate.Pricing.PriceBookSnapshotId = catalog.SnapshotId;
        profile.Estimate.Pricing.PriceBookHash = catalog.FileHash;
        profile.Estimate.PriceBooks.Add(new() { Id = catalog.SnapshotId, File = "SYNTHETIC-ONLY-catalog.xlsx", FileHash = catalog.FileHash });
        var records = new List<NeutralQuantityRecord> { Record(Batch.First, "A1", 20), Record(Batch.Second, "B2", 30) };
        var findings = new List<DeliveryFinding>();
        if (unresolvedCoverage)
        {
            records.Add(Record("layer:UNREVIEWED-GAMMA|length", "C3", 7));
            findings.Add(new()
            {
                FindingId = "SYNTHETIC-EARTHWORKS-UNDECIDED", Code = EstimatePreflightPolicy.EarthworksNotAssessedCode,
                Domain = "estimate", Severity = FindingSeverity.ReviewRequired,
                Title = "SYNTHETIC earthworks scope remains undecided, not zero and not excluded",
                RecommendedAction = "Engineer must decide actual scope; this test makes no such choice.",
            });
        }
        var recordBefore = JsonSerializer.Serialize(records); var findingBefore = JsonSerializer.Serialize(findings);
        var initialVersion = profile.Provenance.Version;
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "SYNTHETIC-BATCH-SCAN", ProjectProfileId = ProfileId, ProfileSource = target,
            SourceDrawing = Drawing, SourceDrawingHash = DrawingHash, SourceDbMod = 0,
            DatabaseRevision = "SYNTHETIC-REVISION", ProjectProfileHash = DrawingHash,
            ProjectProfileEffectiveHash = EstimateTraceIdentity.EffectiveProfileHash(profile),
            ProfileWriteState = ProfileCasTest.For(profile, target), Records = records, Findings = findings,
            Status = DeliveryStatus.ReviewRequired, DiscoveryMode = true,
        };
        ProjectProfileWriter.SaveResult? saved = null;
        var calls = 0;
        Batch.RunSta(() =>
        {
            var dialog = new Dialog(new[] { Batch.Group(Batch.First), Batch.Group(Batch.Second) }, catalog, (choices, reviewer) =>
            {
                calls++;
                saved = new EstimateWorkflowService().SaveReviewedMappings(profile, catalog, scan,
                    choices.Select(choice => new EstimateWorkflowService.ReviewedMappingChoice(choice.RuleKey, choice.CatalogCode,
                        choice.ExcludedAlternativeRuleKey)).ToArray(), reviewer, target, scan.ProfileWriteState!);
            });
            try
            {
                Batch.Row(dialog, Batch.First).MarkedForBatch = Batch.Row(dialog, Batch.Second).MarkedForBatch = true;
                Batch.SelectCode(dialog); dialog.StageMarkedGroups().Should().BeTrue();
                File.Exists(target).Should().BeFalse(); profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
                dialog.Approver.Text = Reviewer; dialog.Confirm.IsChecked = true;
                dialog.TryConfirm().Should().BeTrue();
            }
            finally { dialog.Close(); }
        });
        calls.Should().Be(1); saved.Should().NotBeNull(); saved!.NewVersion.Should().Be(initialVersion + 1);
        var reopened = ProjectProfileLoader.LoadFromFile(target).Profile!;
        reopened.ProfileId.Should().Be(ProfileId); reopened.Estimate.QuantitySources.Rules.Should().HaveCount(2);
        reopened.Estimate.IgnoredRuleDecisions.Should().BeEmpty(); reopened.Estimate.ProjectOverrides.Should().BeEmpty();
        foreach (var key in new[] { Batch.First, Batch.Second })
        {
            var rule = CivilQuantityExtractionService.ResolveApprovedRule(reopened, key, Batch.Group(key).Layer, "length").Rule;
            rule.Should().NotBeNull("the saved exact group is reusable on a later scan of this project");
            rule!.CandidateCatalogCode.Should().Be(Batch.Code); rule.ApprovedBy.Should().Be(Reviewer);
            rule.ApprovedCatalogItemFingerprint.Should().Be(CatalogIdentity.ItemFingerprint(catalog.Items[Batch.Code]));
        }
        var rebased = EstimateWorkflowService.RebaseAfterProfileDecisions(scan, reopened, saved, new[] { Batch.First, Batch.Second });
        JsonSerializer.Serialize(records).Should().Be(recordBefore); JsonSerializer.Serialize(findings).Should().Be(findingBefore);
        rebased.Records.Should().HaveCount(records.Count);
        rebased.Records.Select(record => JsonSerializer.Serialize(record.Measurement))
            .Should().Equal(records.Select(record => JsonSerializer.Serialize(record.Measurement)));
        foreach (var finding in findings) rebased.Findings.Should().Contain(candidate => ReferenceEquals(candidate, finding));
        var built = EstimateBuilder.Build(rebased.Records, catalog, reopened, "SYNTHETIC-BATCH-BUILD",
            rebased.Findings, EstimateTraceIdentity.InferHeadless(rebased.Records, reopened));
        built.CleanTotal.Should().Be(625m); built.Lines.Count(line => line.IncludedInTotals).Should().Be(2);
        built.Lines.Where(line => line.IncludedInTotals).Should().OnlyContain(line => line.Status == DeliveryStatus.Ready);
        built.Exclusions.Should().BeEmpty();
        var options = new EstimateExcelWriter.WriteOptions(ProjectTitle: "SYNTHETIC TEST ONLY - NO PROJECT AUTHORITY", PreparedBy: Reviewer);
        EstimateExcelWriter.WriteResult artifact;
        if (unresolvedCoverage)
        {
            EstimatePreflightPolicy.CanExport(built).Should().BeFalse();
            var draft = EstimatePartialPricedDraftPolicy.Evaluate(built);
            draft.CanExport.Should().BeTrue(string.Join("; ", draft.BlockingReasons)); draft.Subtotal.Should().Be(625m);
            built.Lines.Should().HaveCount(3); built.Lines.Single(line => line.RecordId == "TEST-C3").IncludedInTotals.Should().BeFalse();
            Action full = () => EstimateExcelWriter.Write(built, directory, "MUST-NOT-FULL-EXPORT", options);
            full.Should().Throw<InvalidOperationException>();
            artifact = EstimateExcelWriter.WritePartialPricedDraft(built, directory, "SYNTHETIC-PARTIAL", options);
        }
        else
        {
            EstimatePreflightPolicy.CanExport(built).Should().BeTrue(string.Join("; ", EstimatePreflightPolicy.ExportBlockingReasons(built)));
            artifact = EstimateExcelWriter.Write(built, directory, "SYNTHETIC-COMPLETE-FIXTURE", options);
        }
        ArtifactHash.Sha256OfFile(artifact.XlsxPath).Should().Be(artifact.XlsxHash);
        using var audit = JsonDocument.Parse(File.ReadAllText(artifact.AuditPath));
        audit.RootElement.GetProperty("clean_total").GetDecimal().Should().Be(625m);
        audit.RootElement.GetProperty("lines").GetArrayLength().Should().Be(records.Count);
        if (unresolvedCoverage)
        {
            audit.RootElement.GetProperty("full_estimate_export_allowed").GetBoolean().Should().BeFalse();
            audit.RootElement.GetProperty("findings").EnumerateArray().Should().Contain(finding =>
                finding.GetProperty("code").GetString() == EstimatePreflightPolicy.EarthworksNotAssessedCode);
        }
        using var zip = ZipFile.OpenRead(artifact.XlsxPath);
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(sheet); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var text = string.Join("\n", xml.Descendants(ns + "t").Select(node => node.Value));
        text.Should().Contain("SYNTHETIC TEST ONLY").And.Contain(Batch.Code);
        xml.Descendants(ns + "f").Should().Contain(formula => formula.Value.StartsWith("ROUND(E", StringComparison.Ordinal));
        if (unresolvedCoverage)
        {
            text.Should().Contain(EstimatePartialPricedDraftPolicy.DraftNotice);
            using var findingSheet = zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();
            var findingXml = XDocument.Load(findingSheet);
            var findingText = string.Join("\n", findingXml.Descendants(ns + "t").Select(node => node.Value));
            findingText.Should().Contain(findings[0].Title).And.Contain(findings[0].Code,
                "unresolved earthworks remains visible on the dedicated findings sheet, not silently omitted");
        }
        output.WriteLine("SYNTHETIC ONLY workbook: " + artifact.XlsxPath);
        output.WriteLine("SYNTHETIC ONLY audit: " + artifact.AuditPath);
    }
}
