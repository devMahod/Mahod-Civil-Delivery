using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class ProjectProfileLoaderTests
    {
        /// <summary>Repo root, derived from the baked-in plugin source dir (same mechanism as the contract suite).</summary>
        private static string RepoRoot()
        {
            var pluginSrc = typeof(ProjectProfileLoaderTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            return Path.GetFullPath(Path.Combine(pluginSrc, ".."));
        }

        [Fact]
        public void Real6422Profile_LoadsAndIsUsable()
        {
            var path = Path.Combine(RepoRoot(), "profiles", "civil-delivery", "6422", "project-profile.yaml");
            var result = ProjectProfileLoader.LoadFromFile(path);

            result.Profile.Should().NotBeNull();
            result.IsUsable.Should().BeTrue(because: string.Join("; ",
                result.Findings.Select(f => $"{f.Code}:{f.Title}")));

            var p = result.Profile!;
            p.ProfileId.Should().Be("6422");
            p.Sections.Cl.SourceFiles.Should().Contain("CL.dwg");
            p.Sections.Cl.IntersectionToleranceM.Should().BeNull("OQ-004 is unresolved — no invented tolerance");
            p.Estimate.ApprovedAdjustments.Should().BeEmpty("0.90 is a candidate, never auto-approved");
            p.Estimate.CandidateAdjustments.Should().ContainSingle(a =>
                a.Factor == 0.90 && a.Status == "UNCONFIRMED");
            p.Estimate.Pricing.PriceBookHash.Should().NotBeNullOrEmpty();
            result.ProfileHash.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void MissingFile_IsErrorFinding()
        {
            var result = ProjectProfileLoader.LoadFromFile(
                Path.Combine(Path.GetTempPath(), "nonexistent-profile.yaml"));
            result.IsUsable.Should().BeFalse();
            result.Findings.Should().ContainSingle(f => f.Code == "SHR-PROFILE-MISSING");
        }

        [Fact]
        public void GarbageYaml_FailsClosed()
        {
            // YamlDotNet maps unstructured scalars onto a default document rather than
            // throwing; the guarantee that matters is fail-closed: garbage can never
            // produce a usable profile.
            var result = ProjectProfileLoader.LoadFromText("::: not yaml {{{{");
            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f => f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void StructurallyBrokenYaml_IsParseErrorFinding()
        {
            var result = ProjectProfileLoader.LoadFromText("profile_id: [unclosed");
            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-INVALID");
        }

        [Fact]
        public void UnapprovedAdjustmentInApprovedList_IsError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  cl:
    source_files: [""CL.dwg""]
estimate:
  approved_adjustments:
    - rule_id: ""sneaky-0.90""
      factor: 0.90
      status: ""UNCONFIRMED""
";
            var result = ProjectProfileLoader.LoadFromText(yaml);
            result.IsUsable.Should().BeFalse(
                "an UNCONFIRMED adjustment must never ride in approved_adjustments");
            result.Findings.Should().Contain(f => f.Code == "EST-ADJUSTMENT-UNVERIFIED");
        }

        [Fact]
        public void ConfirmedWithoutApprover_IsStillError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  cl:
    source_files: [""CL.dwg""]
estimate:
  approved_adjustments:
    - rule_id: ""r1""
      factor: 0.90
      status: ""CONFIRMED""
";
            var result = ProjectProfileLoader.LoadFromText(yaml);
            result.Findings.Should().Contain(f => f.Code == "EST-ADJUSTMENT-UNVERIFIED",
                "CONFIRMED without approver+timestamp is not an approval");
        }

        [Fact]
        public void ConfirmedAdjustmentWithoutSupportedScope_IsProfileError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
estimate:
  approved_adjustments:
    - rule_id: ""unsafe-global""
      factor: 0.90
      status: ""CONFIRMED""
      approved_by: ""nataly""
      approved_at_utc: 2026-09-01T10:00:00Z
      scope: null
";

            var result = ProjectProfileLoader.LoadFromText(yaml);

            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f =>
                f.Code == "EST-ADJUSTMENT-SCOPE-UNSUPPORTED");
        }

        [Fact]
        public void IncompleteIgnoredRuleDecision_IsProfileError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
estimate:
  ignored_rule_decisions:
    - rule_key: ""layer:HELPER|length""
      approved_by: ""nataly""
";

            var result = ProjectProfileLoader.LoadFromText(yaml);

            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f =>
                f.Code == "EST-IGNORED-RULE-DECISION-INCOMPLETE");
        }

        [Fact]
        public void OutOfRangeTolerance_IsError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  cl:
    source_files: [""CL.dwg""]
    intersection_tolerance_m: 99
";
            var result = ProjectProfileLoader.LoadFromText(yaml);
            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-CL-TOLERANCE-RANGE");
        }

        [Fact]
        public void IncompleteOverride_IsError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  cl:
    source_files: [""CL.dwg""]
estimate:
  project_overrides:
    - item_code: ""U40.02.2500""
      price: 30000
";
            var result = ProjectProfileLoader.LoadFromText(yaml);
            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f => f.Code == "EST-OVERRIDE-INCOMPLETE",
                "an override without source+reason+approver is a guessed price");
        }

        [Fact]
        public void OverrideWithoutApprovalTimestamp_IsError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  cl:
    source_files: [""CL.dwg""]
estimate:
  project_overrides:
    - item_code: ""U51.01.0250""
      price: 999
      source: ""project quote""
      reason: ""project-specific price""
      approved_by: ""nataly""
";
            var result = ProjectProfileLoader.LoadFromText(yaml);

            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f =>
                f.Code == EstimateFindingCodes.OverrideIncomplete &&
                f.Title.Contains("timestamp", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void EmptyClSources_IsReviewNotError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""7000""
";
            var result = ProjectProfileLoader.LoadFromText(yaml);
            result.IsUsable.Should().BeTrue();
            result.Findings.Should().Contain(f => f.Code == "SHR-PROFILE-CL-SOURCES-EMPTY"
                && f.Severity == FindingSeverity.ReviewRequired);
        }

        [Fact]
        public void Real6422Profile_CarriesTheModelQuantityAndStripRules()
        {
            // The seeded 6422 profile must LOAD — a broken escape in the YAML would
            // silently kill sections and estimate together.
            var path = System.IO.Path.Combine(RepoRoot(), "profiles", "civil-delivery", "6422", "project-profile.yaml");
            var loaded = ProjectProfileLoader.LoadFromFile(path);
            loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Title)));
            var p = loaded.Profile!;

            var rules = p.Estimate.QuantitySources.Rules;
            rules.Should().Contain(r => r.RuleKey == "mahod-default:corridor-base"
                && r.CandidateCatalogCode == "U51.03.0010" && r.ExpectedUnit == "מ\"ק");
            rules.Should().Contain(r => r.RuleKey == "mahod-default:earthworks-cut"
                && r.CandidateCatalogCode == "U51.02.0010");
            rules.Should().Contain(r => r.RuleKey == "mahod-default:earthworks-fill"
                && r.CandidateCatalogCode == "U51.02.0230");
            rules.Should().Contain(r => r.RuleKey == "mahod-default:HW-CURB"
                && r.CandidateCatalogCode == "U51.06.1900");
            rules.Should().Contain(r => r.RuleKey == "mahod-default:CURB-ILND"
                && r.CandidateCatalogCode == "U51.06.2140");
            rules.Should().Contain(r => r.RuleKey == "mahod-default:BIKE-LANE-EDGE"
                && r.CandidateCatalogCode == "U51.06.2930");
            rules.Should().Contain(r => r.RuleKey == "mahod-default:PL-BIKE-EDGE"
                && r.CandidateCatalogCode == "U51.06.2930");
            rules.Should().NotContain(r => (r.LayerPattern ?? "").Contains("Pave"),
                "asphalt item choice (מ\"ר לפי עובי או טון) is the engineer's call");

            var marks = p.Sections.Projection.PlanMarkRules;
            marks.Should().Contain(m => m.Kind == "strip" && m.Label == "נת\"צ");
            marks.Should().Contain(m => m.Kind == "island" && m.LayerPattern == "*TR-ISLAND*");
            marks.Should().Contain(m => m.Kind == "sidewalk" && m.LayerPattern == "END-MDR*");
            marks.Should().Contain(m => m.Kind == "garden" && m.LayerPattern == "*CURB-GRDN*");
            marks.Should().Contain(m => m.Kind == "row" && m.LayerPattern == "ROW_2024-08");
            p.Sections.Projection.RowAuthorities.Should().BeEmpty(
                "GM and photogrammetry ROW sources require an explicit in-panel approval");
            p.Sections.Decisions.SpanLabels.Should().BeEmpty();
        }

        [Fact]
        public void IncompleteSpanLabelDecision_IsAnError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  decisions:
    span_labels:
      - source_drawing_hash: ""BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB""
        source_handle: ""ABC1""
        alignment_name: ""2000""
        from_offset_m: -3
        to_offset_m: 3
        label: ""חניה""
        approved_by: ""nataly""
";

            var result = ProjectProfileLoader.LoadFromText(yaml);

            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f =>
                f.Code == "SHR-SECTION-SPAN-LABEL-INCOMPLETE");
        }

        [Fact]
        public void RowAuthorityWithoutSignature_IsAnError()
        {
            const string yaml = @"
schema_version: 1
profile_id: ""6422""
sections:
  projection:
    row_authorities:
      - source_drawing_sha256: ""AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA""
";

            var result = ProjectProfileLoader.LoadFromText(yaml);

            result.IsUsable.Should().BeFalse();
            result.Findings.Should().Contain(f =>
                f.Code == "SHR-SECTION-ROW-AUTHORITY-INCOMPLETE");
        }

    }
}
