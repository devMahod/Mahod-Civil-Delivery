using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Core.Tests;

public class LegacyPlanMarkDefaultCompatibilityTests
{
    // Exact classification/provenance subtree of the native71 active profile,
    // SHA1938B55AD6AB1708A3726224CC4E509316F8BCF2EE58C8B5F96EBC0B41F1CC67.
    // Unrelated approval history is deliberately omitted, not fabricated as authority
    // for these rules. This is an offline source-contract fixture, not a new native run.
    private const string CapturedSubtree = """
        schema_version: 1
        provenance:
          source: 'Input Package v1 (Materials collected by Arthur, 2026-08-18)'
          version: 10
          created_at_utc: 2026-08-18T00:00:00.0000000Z
          created_by: civil-delivery gate-0
          source_hashes:
            CL.dwg: eae8f807734b04bba2497570ec27da431ac7b39f96456e3d50e2ac56b38f1574
            6422-HW-CS.dwg: aaa6a6cd77698d84e672d4cf88d18699ee4f2656c3d0f8de70289e0d5c54d64e
        sections:
          projection:
            plan_mark_rules:
            - layer_pattern: '*TR-ISLAND*'
              xref_pattern:
              label: אי תנועה
              kind: island
              color_index:
            - layer_pattern: '*BIKE*'
              xref_pattern:
              label: שביל אופניים
              kind: bike
              color_index:
            - layer_pattern: END-MDR*
              xref_pattern:
              label: מדרכה
              kind: sidewalk
              color_index:
            - layer_pattern: HW-TRWY*
              xref_pattern:
              label: שפת מיסעה
              kind: lane
              color_index:
            - layer_pattern: '*CURB*'
              xref_pattern:
              label: אבן שפה
              kind: curb
              color_index:
        """;

    private static ProjectProfile Profile() =>
        ProjectProfileLoader.LoadFromText(CapturedSubtree).Profile!;

    private static List<ProjectionRuleConfig> Rules(ProjectProfile p) =>
        p.Sections.Projection.PlanMarkRules.Select(r =>
            new ProjectionRuleConfig(r.LayerPattern, r.XrefPattern, r.Label, r.Kind, r.ColorIndex)
            { Origin = r.Origin }).ToList();

    [Theory]
    [InlineData("TR-INNER-ISLAND-CURBSTONE")]
    [InlineData("SM-CURB-ILND-EX")]
    [InlineData("6422-GM-MODEL-NATAZ|TR-INNER-ISLAND-CURBSTONE")]
    public void CapturedBundleRestoresIslandSemanticsWithoutChangingBoundsOrApprovingSpans(string layer)
    {
        var p = Profile();
        var before = JsonSerializer.Serialize(p);
        var rules = Rules(p);
        Assert.True(IsKnownLegacyPlanMarkDefaultBundle(rules, p.Provenance));
        Assert.Equal("curb", Classify(layer, null, WithObservedPlanMarkDefaults(rules))!.Kind);
        var match = Classify(layer, null, WithObservedPlanMarkDefaults(rules, p.Provenance))!;
        Assert.Equal("island", match.Kind);
        // These are the exact two differing native67→71 offsets. The PLAN did not
        // retain their layer/handle: each observed layer above is an explicit test
        // input, NOT a claim that native source association was recaptured offline.
        const double left = -1.8688894820921147, right = -0.8745668094604736;
        var coverage = AnalyzePresentationCoverage(new[]
        {
            (left, match.Kind, match.Label), (right, match.Kind, match.Label),
        });
        Assert.Empty(coverage.UnresolvedSpans);
        var span = Assert.Single(coverage.StripLabels);
        Assert.Equal(left, span.From);
        Assert.Equal(right, span.To);
        Assert.Equal("אי תנועה", span.Label);
        Assert.Equal(before, JsonSerializer.Serialize(p));
        Assert.Empty(p.Sections.Decisions.SpanLabels);
    }

    public static IEnumerable<object[]> EveryRuleField()
    {
        for (var index = 0; index < 5; index++)
            foreach (var field in new[] { "layer", "xref", "label", "kind", "color", "origin" })
                yield return new object[] { index, field };
    }

    [Theory]
    [MemberData(nameof(EveryRuleField))]
    public void AnyRuleFieldVariationKeepsTheDeclaredOrder(int index, string field)
    {
        var p = Profile(); var rules = Rules(p); var original = rules[index];
        rules[index] = field switch
        {
            "layer" => original with { LayerPattern = original.LayerPattern + " " },
            "xref" => original with { XrefPattern = "" },
            "label" => original with { Label = original.Label + " " },
            "kind" => original with { Kind = original.Kind!.ToUpperInvariant() },
            "color" => original with { ColorIndex = 7 },
            _ => original with { Origin = "project-explicit" },
        };
        Assert.False(IsKnownLegacyPlanMarkDefaultBundle(rules, p.Provenance));
        Assert.Equal(rules, WithObservedPlanMarkDefaults(rules, p.Provenance).Take(rules.Count));
    }

    [Theory]
    [InlineData("order")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("origin-empty")]
    [InlineData("origin-unknown")]
    public void AnyBundleVariationIsNotAnImplicitMigration(string variation)
    {
        var p = Profile(); var rules = Rules(p);
        switch (variation)
        {
            case "order": (rules[0], rules[1]) = (rules[1], rules[0]); break;
            case "extra": rules.Add(new("*", null, "engineer", "mark", null)); break;
            case "missing": rules.RemoveAt(0); break;
            case "duplicate": rules[1] = rules[0]; break;
            default: rules[4] = rules[4] with { Origin = variation == "origin-empty" ? "" : "future-origin" }; break;
        }
        Assert.False(IsKnownLegacyPlanMarkDefaultBundle(rules, p.Provenance));
        Assert.Equal(rules, WithObservedPlanMarkDefaults(rules, p.Provenance).Take(rules.Count));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("creator")]
    [InlineData("date")]
    [InlineData("date-kind")]
    [InlineData("source")]
    [InlineData("cl-hash")]
    [InlineData("hw-hash")]
    [InlineData("missing-hash")]
    public void UnknownProvenanceNeverPromotesDefaults(string variation)
    {
        var p = Profile(); var provenance = p.Provenance;
        switch (variation)
        {
            case "creator": provenance.CreatedBy = "engineer"; break;
            case "date": provenance.CreatedAtUtc = provenance.CreatedAtUtc!.Value.AddDays(1); break;
            case "date-kind": provenance.CreatedAtUtc = DateTime.SpecifyKind(provenance.CreatedAtUtc!.Value, DateTimeKind.Unspecified); break;
            case "source": provenance.Source = "Another project's copied rules"; break;
            case "cl-hash": provenance.SourceHashes["CL.dwg"] = new string('A', 64); break;
            case "hw-hash": provenance.SourceHashes["6422-HW-CS.dwg"] = new string('B', 64); break;
            case "missing-hash": provenance.SourceHashes.Remove("CL.dwg"); break;
        }
        if (variation == "missing") provenance = null!;
        var rules = Rules(p);
        Assert.False(IsKnownLegacyPlanMarkDefaultBundle(rules, provenance));
        Assert.Equal("curb", Classify("TR-INNER-ISLAND-CURBSTONE", null,
            WithObservedPlanMarkDefaults(rules, provenance))!.Kind);
    }

    [Fact]
    public void ProjectIdentityAndLaterUnrelatedApprovalsAreNotRuleAuthority()
    {
        var p = Profile(); p.ProfileId = "private-other-name";
        p.Provenance.Version = 99;
        p.Provenance.Source += " | ROW source authority: unrelated reviewed source";
        p.Provenance.ApprovedBy = "Engineer approving ROW only";
        p.Provenance.ApprovedAtUtc = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(IsKnownLegacyPlanMarkDefaultBundle(Rules(p), p.Provenance));
        var rules = Rules(p);
        rules.Insert(0, new("TR-*", "VENDOR-*", "explicit project mark", "mark", 123));
        var match = Classify("TR-INNER-ISLAND-CURBSTONE", "VENDOR-X",
            WithObservedPlanMarkDefaults(rules, p.Provenance))!;
        Assert.Equal("mark", match.Kind);
        Assert.Equal("explicit project mark", match.Label);
        Assert.Equal(123, match.ColorIndex);
    }

    [Fact]
    public void ExplicitOriginRoundTripsAndDefeatsEvenAnOtherwiseIdenticalLegacyBundle()
    {
        var p = Profile();
        p.Sections.Projection.PlanMarkRules[4].Origin = "project-explicit";
        var yaml = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build().Serialize(p);
        var loaded = ProjectProfileLoader.LoadFromText(yaml).Profile!;
        Assert.Equal("project-explicit", loaded.Sections.Projection.PlanMarkRules[4].Origin);
        Assert.False(IsKnownLegacyPlanMarkDefaultBundle(Rules(loaded), loaded.Provenance));
        Assert.Equal("curb", Classify("TR-INNER-ISLAND-CURBSTONE", null,
            WithObservedPlanMarkDefaults(Rules(loaded), loaded.Provenance))!.Kind);
    }
}
