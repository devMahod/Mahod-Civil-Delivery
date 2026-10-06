using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Opt-in, host-free replay of a real published quantity artifact. The artifact
    /// stays in LOCALAPPDATA and is never copied into the repository. Set
    /// MHD_ESTIMATE_REPLAY_RUN and MHD_ESTIMATE_REPLAY_PROFILE to execute it.
    /// </summary>
    public sealed class EstimateLiveArtifactReplayTests
    {
        private sealed class ProposalArtifact
        {
            [JsonPropertyName("proposals")]
            public List<MappingProposal> Proposals { get; init; } = new();
        }

        private readonly ITestOutputHelper _output;

        public EstimateLiveArtifactReplayTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void CurrentNeutralRecords_BuildDraftAndRemainExcelFreeWhenBlocked()
        {
            var runDir = Environment.GetEnvironmentVariable("MHD_ESTIMATE_REPLAY_RUN");
            var profilePath = Environment.GetEnvironmentVariable("MHD_ESTIMATE_REPLAY_PROFILE");
            if (string.IsNullOrWhiteSpace(runDir) || string.IsNullOrWhiteSpace(profilePath))
            {
                _output.WriteLine("SKIP: set MHD_ESTIMATE_REPLAY_RUN and MHD_ESTIMATE_REPLAY_PROFILE");
                return;
            }

            runDir = Path.GetFullPath(runDir);
            profilePath = Path.GetFullPath(profilePath);
            var json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };
            json.Converters.Add(new JsonStringEnumConverter());

            var records = JsonSerializer.Deserialize<List<NeutralQuantityRecord>>(
                File.ReadAllText(Path.Combine(runDir, "neutral_quantity_records.json")), json)
                ?? throw new InvalidOperationException("neutral quantity artifact deserialized to null");
            var preflight = JsonSerializer.Deserialize<List<DeliveryFinding>>(
                File.ReadAllText(Path.Combine(runDir, "quantity_preflight.json")), json)
                ?? throw new InvalidOperationException("quantity preflight artifact deserialized to null");
            var proposalArtifact = JsonSerializer.Deserialize<ProposalArtifact>(
                File.ReadAllText(Path.Combine(runDir, "mapping_proposals.json")), json)
                ?? throw new InvalidOperationException("mapping proposal artifact deserialized to null");
            var loaded = ProjectProfileLoader.LoadFromFile(profilePath);
            if (!loaded.IsUsable || loaded.Profile == null)
                throw new InvalidOperationException(
                    "profile unusable: " + string.Join("; ", loaded.Findings.Select(f => f.Code)));
            var profile = loaded.Profile;
            if (!CatalogIdentity.TryGetActiveProfileIdentity(
                    profile, out var identity, out var identityErrors) || identity == null)
                throw new InvalidOperationException(
                    "catalog identity invalid: " + string.Join("; ", identityErrors));
            var catalogPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(profilePath)!, identity.CatalogFile));
            var snapshot = PriceBookXlsxLoader.Load(catalogPath, identity.SnapshotId);
            if (!CatalogIdentity.SnapshotMatches(identity, snapshot))
                throw new InvalidOperationException("catalog bytes do not match the active profile SHA-256");

            var replayScan = new EstimateWorkflowService.ScanResult
            {
                RunId = Path.GetFileName(runDir),
                ProjectProfileId = profile.ProfileId,
                ProfileSource = profilePath,
                SourceDrawing = "6422-live-artifact-replay.dwg",
                DiscoveryMode = true,
                Status = DeliveryStatus.ReviewRequired,
            };
            replayScan.Records.AddRange(records);
            replayScan.Findings.AddRange(preflight);
            var batchCandidates = EstimateWorkflowService.ProvenBatchMappingCandidates(
                replayScan, snapshot, profile, proposalArtifact.Proposals);

            var xlsxBefore = Directory.EnumerateFiles(runDir, "*.xlsx").ToList();
            var ignored = EstimateWorkflowService.ApplyIgnoredRules(records, profile);
            var estimate = EstimateBuilder.Build(
                ignored.PricedRecords, snapshot, profile,
                Path.GetFileName(runDir), preflight);
            estimate.Exclusions.AddRange(ignored.Exclusions);
            estimate.Findings.AddRange(ignored.AuditFindings);
            var blockers = EstimatePreflightPolicy.ExportBlockingReasons(estimate)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(records);
            Assert.NotEmpty(estimate.Lines);
            Assert.NotEmpty(blockers);
            Assert.False(EstimatePreflightPolicy.CanExport(estimate));
            Assert.Equal(xlsxBefore,
                Directory.EnumerateFiles(runDir, "*.xlsx").ToList());

            var globalBlockers = blockers
                .Where(code => !code.StartsWith("line:", StringComparison.Ordinal))
                .ToList();
            var lineBlockerCounts = blockers
                .Where(code => code.StartsWith("line:", StringComparison.Ordinal))
                .Select(code => code.IndexOf(':', "line:".Length) is var split && split >= 0
                    ? code[(split + 1)..]
                    : code)
                .GroupBy(reason => reason, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}={group.Count()}")
                .ToList();

            _output.WriteLine(
                $"records={records.Count}; lines={estimate.Lines.Count}; " +
                $"included={estimate.Lines.Count(line => line.IncludedInTotals)}; " +
                $"unmapped={estimate.Lines.Count(line => line.PriceStatus == PriceStatus.Unmapped)}; " +
                $"missing_price={estimate.Lines.Count(line => line.PriceStatus == PriceStatus.MissingPrice)}; " +
                $"global_blockers={string.Join(",", globalBlockers)}; " +
                $"line_blocker_counts={string.Join(",", lineBlockerCounts)}; xlsx_written=0");
            _output.WriteLine(
                $"proposals={proposalArtifact.Proposals.Count}; " +
                $"rule_groups={records.Select(record => record.Classification.RuleKey ?? "(none)").Distinct(StringComparer.Ordinal).Count()}; " +
                $"proven_batch_candidates={batchCandidates.Count}; " +
                $"candidate_rule_keys={string.Join(",", batchCandidates.Select(candidate => candidate.Approval.RuleKey))}");
        }
    }
}
