using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Proposals must accelerate the engineer without ever deciding for them.
    /// The two rules that matter: never propose across units, and never let a
    /// proposal reach the estimate without explicit approval.
    /// </summary>
    public class MappingProposalTests
    {
        // ---------------- regressions from the engineer's screen (31/08/2026)

        [Fact]
        public void WaterLayersAreNeverOfferedSeals()
        {
            // U05.01.0400 ("אטמי מים מ-PVC") reached MAIM 24Z at the engineer's desk
            // (31/08): "מים" was a genuine whole word — the SUBJECT was wrong. A pipe
            // layer may only be offered pipe-subject items; none ⇒ honest "דרוש מיפוי".
            foreach (var layer in new[] { "MAIM 24Z", "MEKOROT 20Z", "WATER 8Z" })
            {
                var proposals = MappingProposalEngine.Propose(
                    new[] { Group(layer, "מטר") }, EstimateFixtures.Snapshot());
                proposals.Should().NotContain(p => p.CatalogDescription!.Contains("אטמ"), layer);
                // No pipe-subject item in the urban catalog ⇒ an EMPTY list is the
                // honest outcome; whatever IS offered must be pipe-subject.
                proposals.Should().NotContain(p =>
                    !p.CatalogDescription!.Contains("צינור") && !p.CatalogDescription.Contains("קו מים") &&
                    !p.CatalogDescription.Contains("קווי מים") && !p.CatalogDescription.Contains("שוח") &&
                    !p.CatalogDescription.Contains("מגוף") && !p.CatalogDescription.Contains("הנחת"), layer);
            }
        }

        [Fact]
        public void Maim24Z_IsNeverOfferedBoltsOrFasteners()
        {
            // Exact regression from the engineer's screen: MAIM24Z -> "ברגים".
            // A coincidental Hebrew substring is not evidence that a water pipe is a
            // fastener. The subject gate must leave the group unmapped instead.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("MAIM24Z", "מטר") }, EstimateFixtures.Snapshot());

            proposals.Should().NotContain(p =>
                p.CatalogDescription!.Contains("בורג") ||
                p.CatalogDescription.Contains("ברגים") ||
                p.CatalogDescription.Contains("מחבר הברגה"));
        }

        [Fact]
        public void CurbLayersAreOnlyOfferedCurbItems()
        {
            // U51.01.0090 (פירוק מסעה) and U08.06.0185 (road crossing) reached
            // CURB-EXST rows — wrong subject again.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("CURB-EXST", "מטר") }, EstimateFixtures.Snapshot());
            proposals.Should().OnlyContain(p => p.CatalogDescription!.Contains("שפה"));
        }

        [Fact]
        public void TheReferenceEstimateItemWinsTheTopSlot()
        {
            // With the office reference loaded, its exact kerb-demolition item must be
            // the FIRST proposal — not an item that merely mentions curbs in passing.
            var reference = ReferenceEstimateLoader.Load(ReferencePath);
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("CURB-EXST", "מטר") }, EstimateFixtures.Snapshot(), reference.CatalogCodes);
            proposals.Should().NotBeEmpty();
            proposals[0].ProposedCode.Should().Be("U51.01.0250");
        }

        [Fact]
        public void NewCurbLayersAreNeverOfferedDemolitionItems()
        {
            // U51.01.0250 (פירוק אבני שפה) was proposed for HW-CURB at the
            // engineer's desk (31/08). Demolition wording is reserved for layers
            // that SAY they are existing (EXST/-EX/קיים).
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("HW-CURB", "מטר") }, EstimateFixtures.Snapshot());
            proposals.Where(p => p.Layer == "HW-CURB")
                .Should().NotContain(p =>
                    p.CatalogDescription!.Contains("פירוק") || p.CatalogDescription.Contains("סתימת"));
        }

        [Fact]
        public void ExistingCurbLayersMayBeOfferedDemolition()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("CURB-EXST", "מטר") }, EstimateFixtures.Snapshot());
            proposals.Should().Contain(p => p.CatalogDescription!.Contains("פירוק"));
        }

        [Fact]
        public void ExistingCurbLayersAreNeverOfferedNewConstruction()
        {
            // Exact regression from the engineer's screen: CURB-EXST -> new kerb.
            // EXST only proves existing state. It can support a removal/maintenance
            // proposal, never a silent decision to construct a replacement.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("CURB-EXST", "מטר") }, EstimateFixtures.Snapshot());

            proposals.Should().OnlyContain(p =>
                p.CatalogDescription!.Contains("פירוק") ||
                p.CatalogDescription.Contains("פרוק") ||
                p.CatalogDescription.Contains("סתימת") ||
                p.CatalogDescription.Contains("ניקוי") ||
                p.CatalogDescription.Contains("העתק") ||
                p.CatalogDescription.Contains("התאמת גובה") ||
                p.CatalogDescription.Contains("אחזק") ||
                p.CatalogDescription.Contains("שיקום") ||
                p.CatalogDescription.Contains("תיקון"));
        }

        [Fact]
        public void BikeLanesAreNeverOfferedParkingFacilities()
        {
            // U40.02.1265 (מתקן חנייה לאופניים) was proposed for HW-BIKE-LANE —
            // "אופניים" appears in the description, wrong subject entirely.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("HW-BIKE-LANE", "מטר") }, EstimateFixtures.Snapshot());
            proposals.Should().NotContain(p => p.CatalogDescription!.Contains("מתקן חנייה") ||
                                               p.CatalogDescription!.Contains("מתקני חנייה"));
        }

        [Fact]
        public void ProvenBikeLayersOfferTheDedicatedBikeEdgeItem()
        {
            var profile = ProjectProfileLoader.LoadFromFile(Path.Combine(
                EstimateFixtures.RepoRoot(), "profiles", "civil-delivery", "6422",
                "project-profile.yaml")).Profile!;
            var snapshot = EstimateFixtures.Snapshot();

            foreach (var layer in new[] { "HW-BIKE-LANE", "PL-BIKE", "SM-XREF|HW-BIKE-LANE" })
            {
                var groups = new[] { Group(layer, "מטר") };
                var combined = EstimateWorkflowService.CuratedRuleProposals(groups, snapshot, profile)
                    .Concat(MappingProposalEngine.Propose(groups, snapshot))
                    .ToList();

                combined.Should().Contain(p => p.ProposedCode == "U51.06.2930", layer);
                combined.Should().NotContain(p =>
                    p.CatalogDescription!.Contains("מתקן חנייה") ||
                    p.CatalogDescription.Contains("מתקני חנייה"), layer);
            }
        }

        [Fact]
        public void ProvenIslandCurbNeverOffersOrdinaryRoadCurb()
        {
            var snapshot = EstimateFixtures.Snapshot();
            var profile = ProjectProfileLoader.LoadFromFile(Path.Combine(
                EstimateFixtures.RepoRoot(), "profiles", "civil-delivery", "6422",
                "project-profile.yaml")).Profile!;
            var groups = new[] { Group("SM-XREF|HW-CURB-ILND", "מטר") };

            var curated = EstimateWorkflowService.CuratedRuleProposals(groups, snapshot, profile);
            curated.Should().Contain(p => p.ProposedCode == "U51.06.2140");
            curated.Should().NotContain(p => p.ProposedCode == "U51.06.1900");
            MappingProposalEngine.Propose(groups, snapshot)
                .Should().OnlyContain(p =>
                    p.CatalogDescription!.Contains("אי תנועה") ||
                    p.CatalogDescription.Contains("עטרה"));
        }

        [Fact]
        public void PreservedOldRuntimeProfileGetsSafeProposalOverlayWithoutMutation()
        {
            var profile = ProjectProfileLoader.LoadFromFile(Path.Combine(
                EstimateFixtures.RepoRoot(), "profiles", "civil-delivery", "6422",
                "project-profile.yaml")).Profile!;
            profile.Estimate.QuantitySources.Rules.RemoveAll(rule =>
                rule.RuleKey is "mahod-default:CURB-ILND" or
                    "mahod-default:BIKE-LANE-EDGE" or
                    "mahod-default:PL-BIKE-EDGE");
            profile.Estimate.QuantitySources.Rules.Add(new()
            {
                RuleKey = "old-runtime:broad-bike",
                LayerPattern = "*BIKE*",
                MeasurementKind = "length",
                CandidateCatalogCode = "U51.06.2930",
                ExpectedUnit = "מטר",
            });
            var before = JsonSerializer.Serialize(profile);
            var snapshot = EstimateFixtures.Snapshot();
            var groups = new[]
            {
                Group("SM-XREF|HW-CURB-ILND", "מטר"),
                Group("HW-BIKE-LANE", "מטר"),
                Group("PL-BIKE", "מטר"),
            };

            var proposals = EstimateWorkflowService.CuratedRuleProposals(
                groups, snapshot, profile);

            proposals.Should().Contain(proposal =>
                proposal.RuleKey == groups[0].RuleKey &&
                proposal.ProposedCode == "U51.06.2140" &&
                proposal.EvidenceKind == "builtin-overlay-v1");
            proposals.Should().Contain(proposal =>
                proposal.RuleKey == groups[1].RuleKey &&
                proposal.ProposedCode == "U51.06.2930");
            proposals.Should().Contain(proposal =>
                proposal.RuleKey == groups[2].RuleKey &&
                proposal.ProposedCode == "U51.06.2930");
            JsonSerializer.Serialize(profile).Should().Be(before,
                "proposal migration-at-read must never edit preserved runtime approvals/CL/ROW state");
        }

        [Fact]
        public void ExactApprovedOrConflictingProfileContractSuppressesBuiltInOverlay()
        {
            var profile = EstimateFixtures.Profile();
            profile.ProfileId = "6422";
            var conflict = new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
            {
                RuleKey = "engineer:curb-island",
                LayerPattern = "*CURB-ILND*",
                MeasurementKind = "length",
                CandidateCatalogCode = "U51.06.1900",
                ExpectedUnit = "מטר",
            };
            profile.Estimate.QuantitySources.Rules.Add(conflict);
            var group = Group("HW-CURB-ILND", "מטר");

            EstimateWorkflowService.CuratedRuleProposals(
                    new[] { group }, EstimateFixtures.Snapshot(), profile)
                .Should().NotContain(proposal =>
                    proposal.EvidenceKind == "builtin-overlay-v1");

            conflict.CandidateCatalogCode = "U51.06.2140";
            conflict.ApprovedBy = "nataly";
            conflict.ApprovedAtUtc = DateTime.UtcNow;
            var snapshot = EstimateFixtures.Snapshot();
            conflict.ApprovedCatalogId = snapshot.SnapshotId;
            conflict.ApprovedCatalogHash = snapshot.FileHash;
            if (snapshot.Items.TryGetValue(
                    conflict.CandidateCatalogCode, out var item))
                conflict.ApprovedCatalogItemFingerprint = CatalogIdentity.ItemFingerprint(item);

            EstimateWorkflowService.CuratedRuleProposals(
                    new[] { group }, EstimateFixtures.Snapshot(), profile)
                .Should().NotContain(proposal =>
                    proposal.EvidenceKind == "builtin-overlay-v1");
        }

        [Fact]
        public void ProvenGardenCurbDoesNotInheritTheRoadCurbDefault()
        {
            var snapshot = EstimateFixtures.Snapshot();
            var profile = ProjectProfileLoader.LoadFromFile(Path.Combine(
                EstimateFixtures.RepoRoot(), "profiles", "civil-delivery", "6422",
                "project-profile.yaml")).Profile!;
            var groups = new[] { Group("6422-GM|HW-CURB-GRDN", "מטר") };

            EstimateWorkflowService.CuratedRuleProposals(groups, snapshot, profile)
                .Should().NotContain(p => p.ProposedCode == "U51.06.1900");
            MappingProposalEngine.Propose(groups, snapshot)
                .Should().BeEmpty(
                    "the active catalog has no length item with both kerb and garden evidence");
        }

        [Fact]
        public void DesignedSewerIsNeverOfferedPipePlugging()
        {
            // U51.01.0900 (סתימת קו ביוב) was proposed for BIUV250 — a NEW designed
            // sewer line. Plugging is maintenance of existing lines.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("BIUV250", "מטר") }, EstimateFixtures.Snapshot());
            proposals.Should().NotContain(p => p.CatalogDescription!.Contains("סתימת"));
        }

        [Fact]
        public void HebrewKeywordsMatchWholeWordsOnly()
        {
            // "מים" sits inside "מקומיים" — which bought a water layer galvanised
            // threaded rods (U02.02.0030) at the engineer's desk.
            MappingProposalEngine.ContainsWord("מוטות הברגה לחיזוקים מקומיים", "מים").Should().BeFalse();
            MappingProposalEngine.ContainsWord("צינור מים בקוטר 24 צול", "מים").Should().BeTrue();
            MappingProposalEngine.ContainsWord("קו מים", "מים").Should().BeTrue();
        }

        [Fact]
        public void UnapprovedCuratedMazaRule_IsTheTopProposal_ButNeverAnApproval()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "mahod-default:corridor-maza",
                    LayerPattern = "corridor:MAZA*",
                    MeasurementKind = "volume",
                    CandidateCatalogCode = "U51.03.0010",
                    ExpectedUnit = "מ\"ק",
                });
            var groups = new[]
            {
                Group("corridor:MAZA", "מ\"ק", kind: "volume", total: 125.5, count: 2),
            };

            var curated = EstimateWorkflowService.CuratedRuleProposals(
                groups, EstimateFixtures.Snapshot(), profile);

            var proposal = curated.Should().ContainSingle().Subject;
            proposal.ProposedCode.Should().Be("U51.03.0010");
            proposal.Status.Should().Be("PROPOSED_UNAPPROVED");
            proposal.Reasons.Should().Contain(r => r.Contains("טרם אושר"));
            CivilQuantityExtractionService.FindApprovedRule(
                    profile, groups[0].RuleKey, groups[0].Layer!, groups[0].MeasurementKind)
                .Should().BeNull("surfacing the curated default must not let it reach pricing");
        }

        [Fact]
        public void CuratedProposal_RequiresExactKindPatternAndUnit()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "mahod-default:corridor-maza",
                    LayerPattern = "corridor:MAZA*",
                    MeasurementKind = "volume",
                    CandidateCatalogCode = "U51.03.0010",
                    ExpectedUnit = "מ\"ק",
                });
            var groups = new[]
            {
                Group("corridor:OTHER", "מ\"ק", kind: "volume"),
                Group("corridor:MAZA", "מטר", kind: "volume"),
                Group("corridor:MAZA", "מ\"ק", kind: "area"),
            };

            EstimateWorkflowService.CuratedRuleProposals(
                    groups, EstimateFixtures.Snapshot(), profile)
                .Should().BeEmpty();
        }

        [Theory]
        [InlineData("MAIM24Z", "U02.02.0030")]
        [InlineData("HW-CURB", "U51.01.0250")]
        [InlineData("BIUV250", "U51.01.0900")]
        [InlineData("HW-BIKE-LANE", "U40.02.1265")]
        [InlineData("CURB-EXST", "U51.06.1900")]
        [InlineData("HW-CURB-KAYAM", "U51.06.1900")]
        public void LegacyCuratedRules_CannotBypassSemanticRegressionGuards(
            string layer, string badCode)
        {
            var snapshot = EstimateFixtures.Snapshot();
            var item = snapshot.Items[badCode];
            var kind = item.Unit.Dimension switch
            {
                UnitDimension.Length => "length",
                UnitDimension.Area => "area",
                UnitDimension.Volume => "volume",
                UnitDimension.Count => "count",
                UnitDimension.Mass => "mass",
                _ => "other",
            };
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "legacy-bad",
                    LayerPattern = layer,
                    MeasurementKind = kind,
                    CandidateCatalogCode = badCode,
                    ExpectedUnit = item.UnitRaw,
                });
            var groups = new[]
            {
                Group(layer, item.UnitRaw, kind: kind),
            };

            EstimateWorkflowService.CuratedRuleProposals(groups, snapshot, profile)
                .Should().NotContain(proposal =>
                    string.Equals(proposal.ProposedCode, badCode,
                        StringComparison.OrdinalIgnoreCase),
                    $"legacy mapping {layer}->{badCode} is a recorded bad proposal; a different semantically-proven built-in hint may still be shown");
        }

        [Theory]
        [InlineData("MAIM24Z", "U02.02.0030")]
        [InlineData("HW-CURB", "U51.01.0250")]
        [InlineData("BIUV250", "U51.01.0900")]
        [InlineData("HW-BIKE-LANE", "U40.02.1265")]
        [InlineData("CURB-EXST", "U51.06.1900")]
        [InlineData("HW-CURB-KAYAM", "U51.06.1900")]
        public void HeuristicProposals_CannotReintroduceRecordedBadMappings(
            string layer, string badCode)
        {
            var snapshot = EstimateFixtures.Snapshot();
            var item = snapshot.Items[badCode];
            var kind = item.Unit.Dimension switch
            {
                UnitDimension.Length => "length",
                UnitDimension.Area => "area",
                UnitDimension.Volume => "volume",
                UnitDimension.Count => "count",
                UnitDimension.Mass => "mass",
                _ => "other",
            };

            MappingProposalEngine.Propose(
                    new[] { Group(layer, item.UnitRaw, kind: kind) }, snapshot)
                .Should().NotContain(p => p.ProposedCode == badCode,
                    $"{layer}->{badCode} is a recorded bad automatic proposal");
        }

        private static string ReferencePath =>
            Path.Combine(EstimateFixtures.RepoRoot(), "fixtures", "civil-delivery", "estimate", "judgment2-golden.xlsx");

        private static MappingProposalEngine.DiscoveredGroup Group(
            string layer, string unit, string kind = "length", double total = 250, int count = 4) =>
            new($"layer:{layer}|{kind}", layer, kind, unit, count, total);

        private static (
            EstimateWorkflowService.ScanResult Scan,
            ProjectProfile Profile,
            CatalogSnapshot Snapshot,
            System.Collections.Generic.List<MappingProposal> Proposals)
            ProvenBatchFixture(DeliveryStatus recordStatus, bool includeUnmapped)
        {
            const string layer = "HW-BIKE-LANE";
            var profile = EstimateFixtures.Profile();
            var snapshot = EstimateFixtures.Snapshot();
            var record = EstimateFixtures.Record(
                "batch-record", null!, 25, "מטר", layer: layer,
                ruleKey: "layer:HW-BIKE-LANE|length");
            record.Status = recordStatus;
            if (includeUnmapped)
                record.Findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "requires mapping",
                    AffectedRecordIds = { record.RecordId },
                });
            var scan = new EstimateWorkflowService.ScanResult
            {
                RunId = "batch-run",
                ProjectProfileId = profile.ProfileId,
                ProfileSource = "profile.yaml",
                SourceDrawing = "Site.dwg",
                DiscoveryMode = true,
                Status = DeliveryStatus.ReviewRequired,
                Records = { record },
            };
            var proposals = EstimateWorkflowService.CuratedRuleProposals(
                new[] { Group(layer, "מטר") }, snapshot, profile);
            return (scan, profile, snapshot, proposals);
        }

        [Fact]
        public void ProvenBatch_RejectsBlockedRecordWithoutExactUnmappedEvidence()
        {
            var fixture = ProvenBatchFixture(
                DeliveryStatus.Blocked, includeUnmapped: false);

            EstimateWorkflowService.ProvenBatchMappingCandidates(
                    fixture.Scan, fixture.Snapshot, fixture.Profile,
                    fixture.Proposals)
                .Should().BeEmpty();
        }

        [Fact]
        public void ProvenBatch_RejectsReadyRecordEvenWhenItCarriesUnmappedFinding()
        {
            var fixture = ProvenBatchFixture(
                DeliveryStatus.Ready, includeUnmapped: true);

            EstimateWorkflowService.ProvenBatchMappingCandidates(
                    fixture.Scan, fixture.Snapshot, fixture.Profile,
                    fixture.Proposals)
                .Should().BeEmpty();
        }

        [Fact]
        public void ProvenBatch_AcceptsOnlyReviewRequiredRecordWithExactUnmappedFinding()
        {
            var fixture = ProvenBatchFixture(
                DeliveryStatus.ReviewRequired, includeUnmapped: true);

            EstimateWorkflowService.ProvenBatchMappingCandidates(
                    fixture.Scan, fixture.Snapshot, fixture.Profile,
                    fixture.Proposals)
                .Should().ContainSingle();
        }

        [Fact]
        public void ReferenceWorkbook_YieldsOnlyCatalogCodes_NoQuantitiesOrPrices()
        {
            var reference = ReferenceEstimateLoader.Load(ReferencePath);

            reference.CatalogCodes.Should().HaveCountGreaterThan(20,
                "the delivered example contains ~31 priced catalog lines");
            reference.CatalogCodes.Should().OnlyContain(c => c.StartsWith("U"));
            reference.CatalogCodes.Should().Contain("U51.01.0250");
            reference.SourceHash.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void MissingReferenceWorkbook_IsAWarningNotACrash()
        {
            var reference = ReferenceEstimateLoader.Load(
                Path.Combine(Path.GetTempPath(), "no-such-reference.xlsx"));

            reference.CatalogCodes.Should().BeEmpty();
            reference.Findings.Should().Contain(f => f.Code == "EST-REFERENCE-MISSING");
        }

        [Fact]
        public void KerbLayerInMeters_ProposesKerbCatalogItems()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("KERB-EXIST", "מטר") },
                EstimateFixtures.Snapshot());

            proposals.Should().NotBeEmpty();
            proposals.Should().OnlyContain(p => p.MeasuredUnit == "מטר");
            // The catalog uses both construct forms; either is a correct kerb match.
            proposals.First().CatalogDescription.Should().MatchRegex("אבן שפה|אבני שפה");
            proposals.First().Reasons.Should().Contain(r => r.Contains("תואם לנוסח הסעיף"), "the engineer reads the reason in Hebrew");
        }

        [Fact]
        public void RealModelLayerNames_ReachTheRightCatalogWording()
        {
            // Layer names as they actually appear in the 6422 model (transliterated Hebrew
            // and English abbreviations). A WALL layer must not be offered only kerbs.
            var wall = MappingProposalEngine.Propose(
                new[] { Group("WALL-EX", "מטר") }, EstimateFixtures.Snapshot());
            if (wall.Count > 0)
                wall.Should().Contain(p => p.CatalogDescription != null &&
                                            (p.CatalogDescription.Contains("קיר") ||
                                             p.CatalogDescription.Contains("פירוק")),
                    "WALL → קיר, -EX → פירוק/קיים");
            if (wall.Any(p => p.CatalogDescription?.Contains("קיר") == true))
                wall.First().CatalogDescription!.Should().Contain("קיר", "the subject (wall) outranks the modifier (demolition)");

            // A layer with no recognisable wording gets NO proposal, even when a past
            // estimate's codes are supplied - reference is a ranking signal, not evidence.
            var station = MappingProposalEngine.Propose(
                new[] { Group("2000+217", "מטר") }, EstimateFixtures.Snapshot(),
                new[] { "U51.01.0250" });
            station.Should().BeEmpty("station-named layers must not be offered kerb demolition");

            var keywords = typeof(MappingProposalEngine)
                .GetMethod("KeywordsFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string[] Kw(string layer) => ((List<string>)keywords.Invoke(null, new object?[] { layer })!).ToArray();

            Kw("BIUV315").Should().Contain("ביוב");
            Kw("MAIM").Should().Contain("מים");
            Kw("HASHMAL").Should().Contain("חשמל");
            Kw("BEZEQ").Should().Contain("תקשורת");
            Kw("NIKUZ-PROP").Should().Contain("ניקוז");
            Kw("WALL-EX").Should().Contain("קיר").And.Contain("פירוק");
            Kw("CURB-EXST").Should().Contain("אבן שפה").And.Contain("פירוק");
            // Short tokens must not fire inside unrelated words.
            Kw("EXCAV-01").Should().NotContain("פירוק", "-EX must not match EXCAV");
        }

        [Fact]
        public void ExactStationLayerWithExistingModifier_ProducesNoProposal()
        {
            // Live 6422 regression: -EX was treated as enough evidence and the
            // section/station layer 1000+225-EX+W received a priced removal proposal.
            var group = Group("1000+225-EX+W", "מטר");

            MappingProposalEngine.Propose(
                    new[] { group }, EstimateFixtures.Snapshot(),
                    new[] { "U51.32.0210", "U51.01.0250" })
                .Should().BeEmpty("station geometry is not a construction quantity");
            MappingProposalEngine.IsProposalEligible(group).Should().BeFalse();
        }

        [Fact]
        public void CuratedLegacyRule_CannotOfferAStationLayer()
        {
            var profile = EstimateFixtures.Profile();
            profile.Estimate.QuantitySources.Rules.Add(
                new ProjectProfile.EstimateProfile.QuantitySourcesProfile.QuantitySourceRule
                {
                    RuleKey = "legacy-station-ex",
                    LayerPattern = "1000+225-EX+W",
                    MeasurementKind = "length",
                    CandidateCatalogCode = "U51.01.0250",
                    ExpectedUnit = "מטר",
                });
            var groups = new[] { Group("1000+225-EX+W", "מטר") };

            EstimateWorkflowService.CuratedRuleProposals(
                    groups, EstimateFixtures.Snapshot(), profile)
                .Should().BeEmpty("curated defaults cannot bypass the non-quantity gate");
        }

        [Fact]
        public void ProposalReasons_AreHebrew()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("KERB-EXIST", "מטר") }, EstimateFixtures.Snapshot());
            proposals.Should().NotBeEmpty();
            foreach (var r in proposals.SelectMany(p => p.Reasons))
                r.Any(c => c >= 0x0590 && c <= 0x05FF).Should().BeTrue($"reason '{r}' is shown to a Hebrew-reading engineer");
        }

        [Fact]
        public void ProposalsNeverCrossUnits()
        {
            // An area layer must never be offered a per-metre catalog item.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("MILLING", "מ\"ר", kind: "area", total: 900) },
                EstimateFixtures.Snapshot());

            proposals.Should().NotBeEmpty();
            foreach (var p in proposals)
            {
                var catalogUnit = Units.Parse(p.CatalogUnit!);
                var measured = Units.Parse(p.MeasuredUnit);
                measured.SameUnit(catalogUnit).Should().BeTrue(
                    $"proposal {p.ProposedCode} would require a silent unit conversion");
            }
        }

        [Fact]
        public void ReferenceCodes_RankHigherThanUnseenCodes()
        {
            var reference = ReferenceEstimateLoader.Load(ReferencePath);

            var withReference = MappingProposalEngine.Propose(
                new[] { Group("KERB-EXIST", "מטר") },
                EstimateFixtures.Snapshot(),
                reference.CatalogCodes);

            withReference.Should().Contain(p => p.ProposedCode == "U51.01.0250",
                "the delivered example used exactly this kerb-demolition item");
            withReference.First(p => p.ProposedCode == "U51.01.0250").Reasons
                .Should().Contain(r => r.Contains("אומדן ייחוס"));
        }

        [Fact]
        public void UnknownUnit_ProducesNoProposalAtAll()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("MYSTERY", "בליץ") },
                EstimateFixtures.Snapshot());

            proposals.Should().BeEmpty("an unrecognised unit must never be guessed into a catalog code");
        }

        [Fact]
        public void LayerWithNoRecognisableMeaning_ProducesNoNoise()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("LAYER-42-XYZ", "מטר") },
                EstimateFixtures.Snapshot());

            proposals.Should().BeEmpty(
                "without textual or reference evidence a proposal would be an invention");
        }

        [Fact]
        public void ProposalsAreCappedAndAlwaysUnapproved()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("KERB-EXIST", "מטר") },
                EstimateFixtures.Snapshot(),
                ReferenceEstimateLoader.Load(ReferencePath).CatalogCodes);

            proposals.Should().HaveCountLessThanOrEqualTo(MappingProposalEngine.MaxProposalsPerGroup);
            proposals.Should().OnlyContain(p => p.Status == "PROPOSED_UNAPPROVED");
        }

        [Fact]
        public void MissingCatalogPrice_IsStatedInTheReasons()
        {
            // Shelters (יח') include U40.02.2500, which has no price in the 08/2025 book.
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("BUS-SHELTER", "יח'", kind: "count", total: 6) },
                EstimateFixtures.Snapshot(),
                ReferenceEstimateLoader.Load(ReferencePath).CatalogCodes);

            var shelter = proposals.FirstOrDefault(p => p.ProposedCode == "U40.02.2500");
            if (shelter != null)
            {
                shelter.Reasons.Should().Contain(r => r.Contains("MISSING"),
                    "the engineer must see a missing price before approving the mapping");
            }
        }

        [Fact]
        public void ProposalCarriesTheMeasuredEvidence()
        {
            var proposals = MappingProposalEngine.Propose(
                new[] { Group("KERB-EXIST", "מטר", total: 383.5, count: 7) },
                EstimateFixtures.Snapshot());

            var p = proposals.First();
            p.ObjectCount.Should().Be(7);
            p.TotalQuantity.Should().Be(383.5);
            p.RuleKey.Should().Be("layer:KERB-EXIST|length");
        }
    }
}
