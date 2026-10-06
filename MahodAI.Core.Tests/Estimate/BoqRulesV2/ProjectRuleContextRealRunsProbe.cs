using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using C = MahodAI.CivilDelivery.Estimate.ProjectRuleRecordContext;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Read-only L05 probe on a real runs store (skipped unless MCD_L05_PROBE_RUN names an active scan run): the same
/// ResolveObserved → adapter → engine → Bind chain the palette uses, summarised per unmapped group. Writes only the summary
/// file named by MCD_L05_PROBE_OUT. Evidence for review, not a unit test of fixed data.
/// </summary>
public sealed class ProjectRuleContextRealRunsProbe
{
    [Fact]
    public void SummariseTheBoundGuidanceOfARealActiveScan()
    {
        var runId = Environment.GetEnvironmentVariable("MCD_L05_PROBE_RUN");
        var output = Environment.GetEnvironmentVariable("MCD_L05_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(output)) return;
        var runsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodAI_Civil3D", "civil-delivery", "runs");
        var json = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        using var scanDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runsRoot, runId, "estimate_scan.json")));
        var root = scanDoc.RootElement;
        var records = root.GetProperty("Records").Deserialize<List<NeutralQuantityRecord>>(json)!;
        var findings = root.GetProperty("Findings").Deserialize<List<DeliveryFinding>>(json)!;
        var active = new BoqRulesSourceResolver.ActiveScan(runId, root.GetProperty("SourceDrawing").GetString()!,
            root.GetProperty("SourceDrawingHash").GetString()!, records, findings)
        { Units = ScanUnitEvidence.Legacy("synthetic fixture: raw INSUNITS route"),
            ScannedAtUtc = root.TryGetProperty("ScannedAtUtc", out var at) && at.TryGetDateTime(out var t) ? t.ToUniversalTime() : null,
            HatchDiagnostics = root.TryGetProperty("HatchDiagnostics", out var hd) && hd.ValueKind == JsonValueKind.Object ? hd.Clone() : null,
        };
        var rules = BoqRuleset.LoadEmbedded6422();
        var observed = BoqRulesSourceResolver.ResolveObserved(rules, active, runsRoot);
        var input = BoqNeutralRecordAdapter.Build(rules, observed.Resolution.Scans);
        input.Warnings.AddRange(observed.Resolution.Notes);
        var result = BoqRulesEngine.Run(rules, input);
        var summary = new Dictionary<string, object?>
        {
            ["run"] = runId,
            ["pending_reasons"] = observed.PendingReasons,
            ["receipt"] = observed.Receipt?.Sha256,
            ["sources"] = observed.Sources.Select(s => new { s.Role, s.RunId, s.RecordsSha256 }).ToList(),
        };
        if (observed.Receipt is { } receipt && observed.PendingReasons.Count == 0)
        {
            var sources = observed.Resolution.Scans.Select(s =>
            {
                var read = observed.Sources.Single(o => o.Role == s.Role && o.RunId == s.RunId);
                return new C.Source(new C.SourceIdentity(s.Role, s.RunId, s.DrawingPath, s.DrawingHash, read.RecordsSha256,
                    C.RecordDigest(s.Records), read.ManifestSha256, C.AuxiliaryDigest(s)), s);
            }).ToList();
            var stamp = new C.Stamp(rules.Project, runId, active.DrawingPath, active.DrawingHash, new string('A', 64),
                new string('B', 64), new string('C', 64), rules.Sha256, "probe", new string('D', 64),
                C.SourceSetDigest(sources.Select(s => s.Identity)), receipt.Sha256, C.OutcomeDigest(result));
            var bound = C.Bind(new C.Request(stamp, stamp, sources, records, result));
            var byId = bound.Records.ToDictionary(g => g.RecordId, StringComparer.Ordinal);
            summary["states"] = bound.Records.GroupBy(g => g.State.ToString()).ToDictionary(g => g.Key, g => g.Count());
            // The proposal gate (04647): which unmapped groups may reach the generic rankers at all.
            var scope = ProjectRuleProposalScope.Evaluate(ProjectRuleProposalScope.Capture(bound, records), stamp, records);
            var unmappedKeys = records.Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))
                .Select(r => r.Classification.RuleKey).Distinct().ToHashSet();
            var unmappedScopes = scope.Groups.Where(g => unmappedKeys.Contains(g.RuleKey)).ToList();
            summary["scope_context_matches"] = scope.ContextMatches;
            summary["unmapped_groups"] = unmappedScopes.Count;
            summary["unmapped_groups_eligible_for_generic"] = unmappedScopes.Count(g => g.GenericProposalEligible);
            summary["unmapped_groups_by_disposition"] = unmappedScopes.GroupBy(g => $"{g.State}/{g.ReasonCode}").ToDictionary(g => g.Key, g => g.Count());
            summary["groups"] = records.Where(r => string.IsNullOrWhiteSpace(r.Classification.CandidateCatalogCode))
                .GroupBy(r => r.Classification.RuleKey ?? "(none)")
                .Select(g =>
                {
                    var states = g.Select(r => byId.GetValueOrDefault(r.RecordId)).ToList();
                    return new
                    {
                        rule_key = g.Key,
                        count = g.Count(),
                        raw = Math.Round(g.Sum(r => r.Measurement.RawValue), 2),
                        states = states.GroupBy(s => s?.State.ToString() ?? "none").ToDictionary(x => x.Key, x => x.Count()),
                        lines = states.Where(s => s?.State == C.State.RuleReview).Select(s => $"{s!.LineId}:{s.RuleCatalogCode}").Distinct().ToList(),
                        reasons = states.Where(s => s != null && s.State is C.State.PendingLineage or C.State.ExcludedReview)
                            .Select(s => s!.ReasonCode).Distinct().ToList(),
                    };
                })
                .OrderByDescending(g => g.raw).ToList();
        }
        File.WriteAllText(output, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
