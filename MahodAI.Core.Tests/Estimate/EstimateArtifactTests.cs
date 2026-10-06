using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate
{
    public sealed class EstimateArtifactTests
    {
        [Fact]
        public void SharedFinding_IsCanonicalOnce_AndReferencedByAffectedLines()
        {
            var shared = Finding("shared", "EST-MIXED-DIMENSION-LAYER", "R1", "R2");
            var local = Finding("local", EstimateFindingCodes.Unmapped, "R1");
            var result = Result(
                Line("L0001", "R1", local, shared),
                Line("L0002", "R2", shared));
            result.Findings.Add(shared);

            var artifact = EstimateResultArtifact.From(result);
            var json = JsonSerializer.Serialize(artifact);
            using var parsed = JsonDocument.Parse(json);
            var lines = parsed.RootElement.GetProperty("lines");

            artifact.SchemaVersion.Should().Be(2);
            artifact.Findings.Should().ContainSingle().Which.Should().BeSameAs(shared);
            lines[0].GetProperty("findings").GetArrayLength().Should().Be(1);
            lines[0].GetProperty("findings")[0].GetProperty("finding_id").GetString()
                .Should().Be("local");
            lines[0].GetProperty("finding_refs")[0].GetString().Should().Be("shared");
            lines[1].GetProperty("findings").GetArrayLength().Should().Be(0);
            lines[1].GetProperty("finding_refs")[0].GetString().Should().Be("shared");

            Count(json, "\"finding_id\":\"shared\"").Should().Be(1,
                "the full canonical finding must be emitted once, not once per line");
            Count(json, "\"shared\"").Should().Be(3,
                "one canonical id plus one reference from each affected line");
        }

        [Fact]
        public void ArtifactSize_IsLinear_WhenLargeFindingAffectsManyLines()
        {
            const int count = 1_000;
            var recordIds = Enumerable.Range(0, count).Select(i => $"R{i:D4}").ToArray();
            var shared = Finding("group-finding", EstimateFindingCodes.MixedDimensionLayer, recordIds);
            var result = Result(recordIds
                .Select((id, i) => Line($"L{i + 1:D4}", id, shared))
                .ToArray());
            result.Findings.Add(shared);

            var legacyJson = JsonSerializer.Serialize(result);
            var compactJson = JsonSerializer.Serialize(EstimateResultArtifact.From(result));

            legacyJson.Length.Should().BeGreaterThan(8_000_000,
                "the regression fixture must reproduce quadratic repeated finding payloads");
            compactJson.Length.Should().BeLessThan(450_000);
            compactJson.Length.Should().BeLessThan(legacyJson.Length / 20);
        }

        [Fact]
        public void Projection_PreservesMoneyStatusAndFailClosedEvidence()
        {
            var blocker = Finding("blocker", EstimatePreflightPolicy.EarthworksQtoFailedCode);
            var result = Result(Line("L0001", "R1", blocker));
            result.Findings.Add(blocker);
            result.CleanTotal = 123.45m;
            result.ExcludedLineCount = 1;
            result.Status = DeliveryStatus.Failed;
            result.Exclusions.Add(new EstimateExclusion
            {
                RuleKey = "layer:HELPER|length",
                Reason = "helper geometry",
                ApprovedBy = "nataly",
                ApprovedAtUtc = new System.DateTime(2026, 8, 31, 12, 30, 0, System.DateTimeKind.Utc),
                Sources =
                {
                    new EstimateExclusionSource
                    {
                        RecordId = "R-X",
                        Drawing = "PD.dwg",
                        DrawingHash = new string('b', 64),
                        Handle = "AB12",
                        Layer = "HELPER",
                        MeasurementKind = "length",
                        Unit = "מטר",
                        RawValue = 40,
                    },
                },
            });

            var artifact = EstimateResultArtifact.From(result);

            artifact.RunId.Should().Be(result.RunId);
            artifact.ProjectProfileId.Should().Be(result.ProjectProfileId);
            artifact.PriceBookId.Should().Be(result.PriceBookId);
            artifact.PriceBookHash.Should().Be(result.PriceBookHash);
            artifact.SourceScopePolicy.Should().Be(EstimatePreflightPolicy.DiscoverAllSourceScopePolicy);
            artifact.XrefPolicy.Should().Be(EstimatePreflightPolicy.HostOnlyXrefPolicy);
            artifact.ScopeNotice.Should().Be(EstimatePreflightPolicy.HostOnlyScopeNotice);
            artifact.Exclusions.Should().BeEquivalentTo(result.Exclusions);
            artifact.CleanTotal.Should().Be(123.45m);
            artifact.ExcludedLineCount.Should().Be(1);
            artifact.Status.Should().Be(DeliveryStatus.Failed);
            artifact.Findings.Should().ContainSingle().Which.Code
                .Should().Be(EstimatePreflightPolicy.EarthworksQtoFailedCode);
            artifact.Lines.Should().ContainSingle().Which.FindingRefs
                .Should().Equal("blocker");

            EstimatePreflightPolicy.CanExport(result).Should().BeFalse(
                "artifact projection must not alter the runtime fail-closed result");
        }

        [Fact]
        public void ArtifactLine_PreservesTheV1LineShape_WithOnlyFindingRefsAdded()
        {
            var canonical = Finding("canonical", EstimateFindingCodes.OverlapRisk, "R1");
            var line = Line("L0001", "R1", canonical);
            var result = Result(line);
            result.Findings.Add(canonical);

            using var sourceJson = JsonDocument.Parse(JsonSerializer.Serialize(line));
            using var artifactJson = JsonDocument.Parse(JsonSerializer.Serialize(
                EstimateResultArtifact.From(result).Lines.Single()));
            var source = sourceJson.RootElement;
            var artifact = artifactJson.RootElement;

            var optionalV2Trace = new HashSet<string>(new[]
            {
                "source_drawing", "source_drawing_path", "source_drawing_hash",
                "source_database_revision", "source_handle", "source_entity_type",
                "source_xref",
                "source_civil_identity", "source_station_from", "source_station_to",
                "measurement_method", "mapping_approved_by", "mapping_approved_at_utc",
                "approved_catalog_id", "approved_catalog_hash",
                "approved_catalog_item_fingerprint", "price_decision_source",
                "price_decision_reason", "price_approved_by", "price_approved_at_utc",
            });

            var expectedNames = source.EnumerateObject()
                .Where(p => p.Value.ValueKind != JsonValueKind.Null ||
                            !optionalV2Trace.Contains(p.Name))
                .Select(p => p.Name)
                .Append("finding_refs");
            artifact.EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(expectedNames);

            foreach (var property in source.EnumerateObject().Where(p =>
                         p.Name != "findings" &&
                         (p.Value.ValueKind != JsonValueKind.Null ||
                          !optionalV2Trace.Contains(p.Name))))
            {
                artifact.GetProperty(property.Name).GetRawText()
                    .Should().Be(property.Value.GetRawText(), property.Name);
            }
        }

        [Fact]
        public void V2Reader_RehydratesCanonicalAndInlineFindings_WithSemanticRoundTrip()
        {
            var shared = Finding("shared", EstimateFindingCodes.MixedDimensionLayer, "R1", "R2");
            var local = Finding("local", EstimateFindingCodes.Unmapped, "R1");
            var first = Line("L0001", "R1", local, shared);
            first.CatalogCode = "U51.04.0080";
            first.Description = "Test item";
            first.Price = 12.50m;
            first.PriceStatus = PriceStatus.Priced;
            first.PriceBookId = "nti-test";
            first.Total = 12.50m;
            first.IncludedInTotals = true;
            first.Adjustments.Add(new AdjustmentStep
            {
                RuleId = "round-trip",
                Factor = 1.25,
                Input = 0.8,
                Output = 1,
                Reason = "semantic coverage",
                ApprovedBy = "tester",
                Order = 1,
            });
            var result = Result(first, Line("L0002", "R2", shared));
            result.Findings.Add(shared);
            result.CleanTotal = 12.50m;
            result.ExcludedLineCount = 1;
            result.Status = DeliveryStatus.ReviewRequired;

            var json = JsonSerializer.Serialize(EstimateResultArtifact.From(result));
            var rehydrated = EstimateResultArtifactReader.Read(json);

            rehydrated.Should().BeEquivalentTo(result);
            var canonical = rehydrated.Findings.Single(f => f.FindingId == "shared");
            rehydrated.Lines[0].Findings.Single(f => f.FindingId == "shared")
                .Should().BeSameAs(canonical);
            rehydrated.Lines[1].Findings.Single().Should().BeSameAs(canonical);

            JsonSerializer.Serialize(EstimateResultArtifact.From(rehydrated)).Should().Be(json,
                "reading and re-emitting schema v2 must preserve semantics and compact layout");
            Count(json, "\"finding_id\":\"shared\"").Should().Be(1,
                "rehydration must not require duplicated canonical findings in JSON");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1Reader_AcceptsLegacyInlineFindings_WithOrWithoutExplicitVersion(
            bool explicitVersion)
        {
            var rootFinding = Finding("root", EstimateFindingCodes.OverlapRisk, "R1");
            var inlineFinding = Finding("inline", EstimateFindingCodes.Unmapped, "R1");
            var legacy = Result(Line("L0001", "R1", inlineFinding));
            legacy.Findings.Add(rootFinding);
            legacy.CleanTotal = 44.25m;
            legacy.ExcludedLineCount = 2;
            legacy.Status = DeliveryStatus.Warning;

            var json = JsonSerializer.Serialize(legacy);
            if (explicitVersion)
                json = json.Insert(1, "\"schema_version\":1,");

            EstimateResultArtifactReader.Read(json).Should().BeEquivalentTo(legacy);
        }

        [Fact]
        public void V2Reader_RejectsDanglingFindingReference()
        {
            var shared = Finding("shared", EstimateFindingCodes.OverlapRisk, "R1");
            var result = Result(Line("L0001", "R1", shared));
            result.Findings.Add(shared);
            var artifact = EstimateResultArtifact.From(result);
            artifact.Lines.Single().FindingRefs!.Clear();
            artifact.Lines.Single().FindingRefs!.Add("missing");

            var act = () => EstimateResultArtifactReader.Read(JsonSerializer.Serialize(artifact));

            act.Should().Throw<JsonException>()
                .WithMessage("*references unknown finding_id 'missing'*");
        }

        [Fact]
        public void Reader_RejectsUnsupportedSchemaVersion()
        {
            var act = () => EstimateResultArtifactReader.Read("{\"schema_version\":3}");

            act.Should().Throw<JsonException>()
                .WithMessage("Unsupported estimate schema_version 3.");
        }

        [Fact]
        public void LegacyV2WithoutPersistedScope_RemainsReadableButIsExportUntrusted()
        {
            var oldV2 = new EstimateResultArtifact
            {
                RunId = "old-v2",
                ProjectProfileId = "6422",
                PriceBookId = "nti-test",
                PriceBookHash = new string('a', 64),
                Status = DeliveryStatus.Ready,
            };

            var rehydrated = EstimateResultArtifactReader.Read(
                JsonSerializer.Serialize(oldV2));
            var reasons = EstimatePreflightPolicy.ExportBlockingReasons(rehydrated);

            rehydrated.SourceScopePolicy.Should().BeNull();
            rehydrated.XrefPolicy.Should().BeNull();
            rehydrated.ScopeNotice.Should().BeNull();
            reasons.Should().Contain("source-scope-untrusted")
                .And.Contain("xref-scope-untrusted")
                .And.Contain("scope-notice-missing");
            EstimatePreflightPolicy.CanExport(rehydrated).Should().BeFalse();
        }

        private static EstimateResult Result(params EstimateLine[] lines) => new()
        {
            RunId = "estimate-test-run",
            ProjectProfileId = "6422",
            PriceBookId = "nti-test",
            PriceBookHash = "abc",
            SourceScopePolicy = EstimatePreflightPolicy.DiscoverAllSourceScopePolicy,
            XrefPolicy = EstimatePreflightPolicy.HostOnlyXrefPolicy,
            ScopeNotice = EstimatePreflightPolicy.HostOnlyScopeNotice,
            Lines = lines.ToList(),
        };

        private static EstimateLine Line(
            string lineId, string recordId, params DeliveryFinding[] findings) => new()
        {
            LineId = lineId,
            RecordId = recordId,
            SourceLayer = "TEST",
            RuleKey = "layer:TEST|length",
            Unit = "מטר",
            RawQuantity = 1,
            BoqQuantity = 1,
            PriceStatus = PriceStatus.Unmapped,
            IncludedInTotals = false,
            Status = DeliveryStatus.ReviewRequired,
            Findings = findings.ToList(),
        };

        private static DeliveryFinding Finding(
            string id, string code, params string[] affectedRecordIds) => new()
        {
            FindingId = id,
            Code = code,
            Domain = "estimate",
            Severity = FindingSeverity.ReviewRequired,
            Title = code,
            Message = $"message for {id}",
            AffectedRecordIds = affectedRecordIds.ToList(),
            EvidenceRefs = new List<string> { $"evidence-{id}" },
            RecommendedAction = $"resolve {id}",
            CreatedAtUtc = new System.DateTime(2026, 8, 31, 12, 0, 0, System.DateTimeKind.Utc),
        };

        private static int Count(string value, string needle)
        {
            var count = 0;
            var offset = 0;
            while ((offset = value.IndexOf(needle, offset, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += needle.Length;
            }
            return count;
        }
    }
}
