using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Opt-in measurement of the evidence bridge on a saved real scan (read-only): set MHD_BRIDGE_REAL_RUN_DIR to the run
/// folder, MHD_ENGINEER_DRAFT_PRICEBOOK to the price-list xlsx and optionally MHD_BRIDGE_OUT to keep the JSON summary.
/// Compares the production proposal groups with and without the bridge. Nothing is published, approved or priced.
/// Without the variables the test returns at once.
/// </summary>
public sealed class MappingEvidenceBridgeRealRunTests(ITestOutputHelper output)
{
    [Fact]
    public void RealRunProposalsWithAndWithoutTheBridge()
    {
        var runDir = Environment.GetEnvironmentVariable("MHD_BRIDGE_REAL_RUN_DIR");
        var priceBook = Environment.GetEnvironmentVariable("MHD_ENGINEER_DRAFT_PRICEBOOK");
        if (string.IsNullOrWhiteSpace(runDir) || string.IsNullOrWhiteSpace(priceBook)) return;
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var records = JsonSerializer.Deserialize<List<NeutralQuantityRecord>>(
            File.ReadAllText(Path.Combine(runDir, "neutral_quantity_records.json")), options)!;
        var catalog = PriceBookXlsxLoader.Load(priceBook, "real-opt-in");

        var groups = EstimateWorkflowService.BuildMappingProposalGroups(records, MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.RoadsV1);
        var with = MappingProposalEngine.Propose(groups, catalog);
        var without = MappingProposalEngine.Propose(groups.Select(g => g with { RecognitionEvidence = null }).ToList(), catalog);
        var withByGroup = with.GroupBy(p => p.RuleKey).ToDictionary(g => g.Key, g => g.ToList());
        var withoutByGroup = without.GroupBy(p => p.RuleKey).ToDictionary(g => g.Key, g => g.ToList());
        var familyReason = "זיהוי מקומי הציע";

        var summary = new
        {
            run = Path.GetFileName(runDir.TrimEnd('\\', '/')),
            records = records.Count,
            proposal_groups = groups.Count,
            groups_with_evidence_subjects = groups.Count(g => g.RecognitionEvidence?.Subjects.Count > 0),
            groups_with_recognised_family = groups.Count(g => g.RecognitionEvidence?.RecognisedFamily != null),
            groups_contradicted = groups.Count(g => g.RecognitionEvidence?.Contradicted == true),
            groups_with_proposals_without_bridge = withoutByGroup.Count,
            groups_with_proposals_with_bridge = withByGroup.Count,
            groups_gaining_proposals = withByGroup.Keys.Count(k => !withoutByGroup.ContainsKey(k)),
            groups_losing_proposals = withoutByGroup.Keys.Count(k => !withByGroup.ContainsKey(k)),
            proposals_from_family = with.Count(p => p.Reasons.Any(r => r.Contains(familyReason))),
            recognised_breakdown = groups.Where(g => g.RecognitionEvidence?.RecognisedFamily != null)
                .GroupBy(g => g.RecognitionEvidence!.RecognisedFamily!)
                .Select(family => new
                {
                    family = family.Key,
                    groups = family.Count(),
                    eligible = family.Count(MappingProposalEngine.IsProposalEligible),
                    family_codes = family.First().RecognitionEvidence!.FamilyCandidateCodes,
                    codes_units = family.First().RecognitionEvidence!.FamilyCandidateCodes
                        .Select(code => code + "=" + (catalog.Items.TryGetValue(code, out var item) ? item.UnitRaw : "not-in-catalog")).ToList(),
                    measured_units = family.Select(g => g.MeasuredUnit).Distinct().ToList(),
                    groups_with_family_proposal = family.Count(g => withByGroup.TryGetValue(g.RuleKey, out var list) &&
                                                                    list.Any(p => p.Reasons.Any(r => r.Contains(familyReason)))),
                })
                .OrderByDescending(x => x.groups).ToList(),
            gained = withByGroup.Where(pair => !withoutByGroup.ContainsKey(pair.Key)).Select(pair => new
            {
                rule_key = pair.Key,
                layer = pair.Value[0].Layer,
                objects = pair.Value[0].ObjectCount,
                family = groups.First(g => g.RuleKey == pair.Key).RecognitionEvidence?.RecognisedFamily,
                codes = pair.Value.Select(p => p.ProposedCode + " " + p.CatalogUnit).ToList(),
            }).OrderByDescending(x => x.objects).Take(40).ToList(),
            lost = withoutByGroup.Where(pair => !withByGroup.ContainsKey(pair.Key)).Select(pair => new
            {
                rule_key = pair.Key,
                layer = pair.Value[0].Layer,
                refusal = MappingProposalEngine.EvidenceRefusal(groups.First(g => g.RuleKey == pair.Key)),
            }).Take(40).ToList(),
            contradicted = groups.Where(g => g.RecognitionEvidence?.Contradicted == true)
                .Select(g => new { rule_key = g.RuleKey, layer = g.Layer, objects = g.ObjectCount }).Take(40).ToList(),
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        output.WriteLine(json);
        var outPath = Environment.GetEnvironmentVariable("MHD_BRIDGE_OUT");
        if (!string.IsNullOrWhiteSpace(outPath)) File.WriteAllText(outPath, json);
        Assert.All(with, p => Assert.Equal("PROPOSED_UNAPPROVED", p.Status));
        Assert.All(records, r => Assert.Null(r.Classification.MappingApprovedBy));
    }
}
