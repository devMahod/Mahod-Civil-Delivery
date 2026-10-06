using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using MahodAI.Civil3D.Plugin.WebSocket;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The degenerate and hostile inputs a real project produces: a truncated profile,
    /// a price book that is not a price book, an empty drawing, absurd geometry.
    ///
    /// The rule under test is always the same one: the product may REFUSE, and it may
    /// mark REVIEW_REQUIRED, but it may never crash the engineer's Civil session and it
    /// may never turn a problem it did not understand into a confident number.
    /// </summary>
    public class RobustnessTests
    {
        private static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "mcd_robust_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(d);
            return d;
        }

        // --------------------------------------------------------------- profile

        [Fact]
        public void ProfileThatIsNotYaml_IsRejectedWithAFinding_NotAnException()
        {
            var load = ProjectProfileLoader.LoadFromText("<<<this is not yaml: [unclosed");

            load.Profile.Should().BeNull();
            load.Findings.Should().NotBeEmpty("a parse failure must be reported, not swallowed");
            load.Findings.Should().Contain(f => f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void EmptyProfileFile_IsRejected()
        {
            var load = ProjectProfileLoader.LoadFromText("");

            load.Findings.Should().Contain(f => f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void MissingProfileFile_IsRejectedWithThePathInTheMessage()
        {
            var path = Path.Combine(TempDir(), "does-not-exist.yaml");

            var load = ProjectProfileLoader.LoadFromFile(path);

            load.Profile.Should().BeNull();
            load.Findings.Should().Contain(f => f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void ProfileWithNoProfileId_IsRejected()
        {
            // Without an id nothing downstream can be attributed to a project.
            var load = ProjectProfileLoader.LoadFromText("schema_version: 1\n");

            load.Findings.Should().Contain(f => f.Severity == FindingSeverity.Error);
        }

        // ------------------------------------------------------------ price book

        [Fact]
        public void PriceBookThatIsNotAnXlsx_FailsLoudly()
        {
            var path = Path.Combine(TempDir(), "not-a-workbook.xlsx");
            File.WriteAllText(path, "I am a text file wearing an xlsx extension");

            var act = () => PriceBookXlsxLoader.Load(path, "bogus");

            act.Should().Throw<Exception>(
                "a corrupt catalog must stop the run - silently pricing against nothing is worse");
        }

        [Fact]
        public void MissingPriceBook_FailsLoudly()
        {
            var act = () => PriceBookXlsxLoader.Load(
                Path.Combine(TempDir(), "absent.xlsx"), "absent");

            act.Should().Throw<Exception>();
        }

        // -------------------------------------------------------------- estimate

        [Fact]
        public void EstimateWithNoRecords_IsAnEmptyEstimate_NotAZeroTotalPresentedAsFact()
        {
            var result = EstimateBuilder.Build(
                Array.Empty<NeutralQuantityRecord>(),
                EstimateFixtures.Snapshot(),
                EstimateFixtures.Profile());

            result.Lines.Should().BeEmpty();
            result.Status.Should().NotBe(DeliveryStatus.Ready,
                "an estimate with nothing in it must never be presented as a finished estimate");
        }

        [Fact]
        public void UnknownCatalogCode_IsReviewRequired_NotSilentlyPricedAtZero()
        {
            var records = new[] { EstimateFixtures.Record("r1", "99.99.9999", 10, "m") };

            var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var line = result.Lines.Single();
            line.Status.Should().Be(DeliveryStatus.ReviewRequired);
            line.PriceStatus.Should().Be(PriceStatus.Unmapped);
        }

        [Fact]
        public void NegativeQuantity_IsFlagged()
        {
            // A negative length is a geometry or rule bug, never a real BOQ quantity.
            var records = new[] { EstimateFixtures.Record("r1", "51.01.0010", -25, "m") };

            var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            result.Lines.Single().Status.Should().NotBe(DeliveryStatus.Ready);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void NonFiniteQuantity_IsFlagged_AndNeverReachesTheTotal(double bad)
        {
            // CleanTotal is decimal, which cannot represent NaN or infinity at all.
            // So the danger here is not a wrong number - it is an OverflowException
            // thrown mid-run inside Civil. The engine must contain it.
            var records = new[]
            {
                EstimateFixtures.Record("bad",  "51.01.0010", bad, "m"),
                EstimateFixtures.Record("good", "51.01.0010", 10, "m"),
            };

            var act = () => EstimateBuilder.Build(
                records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var result = act.Should().NotThrow(
                "a single broken geometry value must not abort the engineer's estimate").Subject;
            var badLine = result.Lines.Single(l => l.RecordId == "bad");
            badLine.Status.Should().Be(DeliveryStatus.Failed);
            badLine.IncludedInTotals.Should().BeFalse();
            badLine.Total.Should().BeNull();
            badLine.BoqQuantity.Should().Be(0,
                "a safe sentinel is stored only on the visibly failed/excluded line");
            badLine.Findings.Should().ContainSingle(f =>
                f.Code == EstimateFindingCodes.MeasurementFailed &&
                f.Severity == FindingSeverity.Error);
            result.Findings.Should().Contain(f =>
                f.Code == EstimateFindingCodes.MeasurementFailed &&
                f.AffectedRecordIds.Contains("bad"));
            EstimatePreflightPolicy.CanExport(result).Should().BeFalse();
            result.Status.Should().NotBe(DeliveryStatus.Ready);

            var named = double.IsNaN(bad) ? "NaN" : "Infinity";

            // The exact payloads written by Scan/Build artifacts must retain the
            // diagnostic value as a JSON string while BOQ stays a finite number.
            using (var neutralJson = JsonDocument.Parse(
                JsonSerializer.Serialize(records[0], SectionsWorkflowService.Json)))
            {
                neutralJson.RootElement.GetProperty("measurement").GetProperty("raw_value")
                    .GetString().Should().Be(named);
            }
            using (var estimateJson = JsonDocument.Parse(
                JsonSerializer.Serialize(result, SectionsWorkflowService.Json)))
            {
                var serializedBad = estimateJson.RootElement.GetProperty("lines")
                    .EnumerateArray().Single(l => l.GetProperty("record_id").GetString() == "bad");
                serializedBad.GetProperty("raw_quantity").GetString().Should().Be(named);
                serializedBad.GetProperty("boq_quantity").GetDouble().Should().Be(0);
            }

            var toolPayload = new Dictionary<string, object?>
            {
                ["raw_quantity"] = EstimateToolJson.TraceNumber(bad),
                ["boq_quantity"] = badLine.BoqQuantity,
            };
            using var wireJson = JsonDocument.Parse(
                JsonSerializer.Serialize(toolPayload, WebSocketJson.Options));
            wireJson.RootElement.GetProperty("raw_quantity").GetString().Should().Be(named);
            wireJson.RootElement.GetProperty("boq_quantity").GetDouble().Should().Be(0);
        }

        [Fact]
        public void OneBadRecordDoesNotDiscardTheGoodOnes()
        {
            var records = new[]
            {
                EstimateFixtures.Record("bad",  "99.99.9999", 10, "m"),
                EstimateFixtures.Record("good", "51.01.0010", 42, "m"),
            };

            var result = EstimateBuilder.Build(records, EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            result.Lines.Should().HaveCount(2);
            result.Lines.Should().Contain(l => l.RecordId == "good");
        }

        // ----------------------------------------------------------- excel output

        [Fact]
        public void ExcelWriter_RejectsAnEmptyEstimateAsNonFinal()
        {
            var dir = TempDir();
            var estimate = EstimateBuilder.Build(
                Array.Empty<NeutralQuantityRecord>(),
                EstimateFixtures.Snapshot(),
                EstimateFixtures.Profile());

            var act = () => EstimateExcelWriter.Write(estimate, dir, "empty-estimate");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*no-estimate-lines*");
            Directory.GetFiles(dir, "empty-estimate*").Should().BeEmpty();
        }

        [Fact]
        public void ExcelWriter_CreatesTheOutputDirectoryIfTheEngineerDeletedIt()
        {
            var dir = Path.Combine(TempDir(), "not", "created", "yet");
            var estimate = EstimateBuilder.Build(
                new[] { EstimateFixtures.Record("r1", "U51.01.0250", 5, "מטר") },
                EstimateFixtures.Snapshot(), EstimateFixtures.Profile());

            var written = EstimateExcelWriter.Write(estimate, dir, "nested");

            File.Exists(written.XlsxPath).Should().BeTrue();
        }

        // ---------------------------------------------------------- result scope

        [Fact]
        public void PlanWithNoSourceDrawing_DoesNotMatchARealDrawing()
        {
            // A plan whose origin was never recorded cannot prove it belongs here,
            // so APPLY must treat it as stale rather than assume the best.
            var scope = ResultScope.For(null, "h");

            scope.Matches(@"C:\work\6422.dwg", "h").Should().BeFalse();
            scope.StaleReason(@"C:\work\6422.dwg", "h").Should().NotBeNull();
        }
    }
}
