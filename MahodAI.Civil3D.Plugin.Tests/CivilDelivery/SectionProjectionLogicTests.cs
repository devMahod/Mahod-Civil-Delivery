using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The maths behind projecting UT-XREF utilities and plan marks into created
    /// sections — the answer to "the tool says there are no systems while my old
    /// drafted sections show them" (engineer meeting, 2026-08-30). Wrong geometry
    /// here draws a water line on the wrong side of the axis in a client deliverable,
    /// so every property is pinned.
    /// </summary>
    public class SectionProjectionLogicTests
    {
        // -------------------------------------------------------- intersections

        [Fact]
        public void IntersectPolyline_FindsTheCrossingAndInterpolatesZ()
        {
            // sample line along X from (0,0) to (100,0); a 3D utility crosses at x=40
            var hits = IntersectPolyline(
                new P2(0, 0), new P2(100, 0),
                new List<V3> { new(40, -10, 280.0), new(40, 10, 282.0) }, closed: false);

            hits.Should().ContainSingle();
            hits[0].T.Should().BeApproximately(40, 1e-6);
            hits[0].Z.Should().NotBeNull();
            hits[0].Z!.Value.Should().BeApproximately(281.0, 1e-6, "Z interpolates along the crossing edge");
        }

        [Fact]
        public void IntersectPolyline_FlatPolylineYieldsNullElevation()
        {
            var hits = IntersectPolyline(
                new P2(0, 0), new P2(100, 0),
                new List<V3> { new(40, -10, 0), new(40, 10, 0) }, closed: false);

            hits.Should().ContainSingle();
            hits[0].Z.Should().BeNull("a Z of exactly 0 is flat drafting, not an elevation claim");
        }

        [Fact]
        public void IntersectPolyline_MissesAndParallelsYieldNothing()
        {
            IntersectPolyline(new P2(0, 0), new P2(100, 0),
                new List<V3> { new(40, 5, 0), new(60, 15, 0) }, false).Should().BeEmpty("no crossing");
            IntersectPolyline(new P2(0, 0), new P2(100, 0),
                new List<V3> { new(0, 1, 0), new(100, 1, 0) }, false).Should().BeEmpty("parallel");
        }

        [Fact]
        public void IntersectPolyline_ClosedPolylineCountsTheClosingEdge()
        {
            // triangle whose closing edge (2,-5)→(2,5)... build a square crossing twice
            var square = new List<V3> { new(30, -5, 0), new(50, -5, 0), new(50, 5, 0), new(30, 5, 0) };
            var hits = IntersectPolyline(new P2(0, 0), new P2(100, 0), square, closed: true);
            hits.Should().HaveCount(2, "the sample line enters and leaves the closed loop");
            hits.Select(h => h.T).Should().BeEquivalentTo(new[] { 30.0, 50.0 },
                o => o.WithStrictOrdering());
        }

        [Fact]
        public void IntersectPolyline_MergesTwinHitsAtASharedVertex()
        {
            // two edges meeting exactly ON the sample line: one feature, not two
            var verts = new List<V3> { new(40, -10, 0), new(40, 0, 0), new(40, 10, 0) };
            IntersectPolyline(new P2(0, 0), new P2(100, 0), verts, false).Should().ContainSingle();
        }

        // ------------------------------------------------------------ sign facts

        [Fact]
        public void SignedOffset_TowardTheLeftEndpointIsNegative()
        {
            var crossing = new P2(50, 0);
            var leftEnd = new P2(0, 0);

            SignedOffset(crossing, new P2(30, 0), leftEnd).Should().BeApproximately(-20, 1e-9,
                "a point between the crossing and Civil's LEFT end sits left of the axis");
            SignedOffset(crossing, new P2(80, 0), leftEnd).Should().BeApproximately(30, 1e-9);
        }

        [Fact]
        public void SignedOffset_DoesNotDependOnTheDrawnDirectionOfTheClLine()
        {
            // same geometry, CL drawn right-to-left: only the left endpoint decides
            var crossing = new P2(50, 0);
            SignedOffset(crossing, new P2(30, 0), new P2(100, 0)).Should().BeApproximately(20, 1e-9,
                "when Civil says the left end is at x=100, smaller x means RIGHT of the axis");
        }

        // ------------------------------------------------------------- width cap

        [Fact]
        public void CapEndpoints_TrimsOnlyTheLongSide()
        {
            var (ax, ay, bx, by, trimmed) = CapEndpoints(
                new P2(50, 0), new P2(0, 0), new P2(70, 0), maxHalfWidthM: 30);

            trimmed.Should().BeTrue();
            ax.Should().BeApproximately(20, 1e-9, "50 m side capped to 30");
            bx.Should().BeApproximately(70, 1e-9, "20 m side untouched");
            ay.Should().Be(0); by.Should().Be(0);
        }

        [Fact]
        public void CapEndpoints_ShortLineIsUntouched()
        {
            var r = CapEndpoints(new P2(50, 0), new P2(30, 0), new P2(70, 0), 30);
            r.Trimmed.Should().BeFalse();
            r.Ax.Should().Be(30); r.Bx.Should().Be(70);
        }

        [Fact]
        public void CapEndpoints_PreservesTheDrawnDirectionOnSkewedLines()
        {
            // a 45° CL: the capped endpoint stays on the drawn ray
            var r = CapEndpoints(new P2(0, 0), new P2(60, 60), new P2(-10, -10), 30);
            r.Trimmed.Should().BeTrue();
            r.Ax.Should().BeApproximately(30 / Math.Sqrt(2), 1e-9);
            r.Ay.Should().BeApproximately(30 / Math.Sqrt(2), 1e-9);
        }

        // -------------------------------------------------------- classification

        [Theory]
        [InlineData("MAIM10Z", "מים")]
        [InlineData("BIUV315", "ביוב")]
        [InlineData("HASHMAL", "חשמל")]
        [InlineData("BEZEQ", "בזק")]
        [InlineData("hot-tv", "HOT")]
        public void Classify_KnowsTheRealSixFourTwoTwoLayers(string layer, string expected)
        {
            var match = Classify(layer, "UT-3D", new List<ProjectionRuleConfig>());
            match.Should().NotBeNull("these layer names exist on the real 6422 drawing");
            match!.Label.Should().Be(expected);
            match.Kind.Should().Be("utility");
        }

        [Fact]
        public void Classify_UnknownLayersAreNeverGuessedIntoASection()
        {
            Classify("0-EZER", null, new List<ProjectionRuleConfig>()).Should().BeNull();
            Classify("GVULOT", null, new List<ProjectionRuleConfig>()).Should().BeNull();
        }

        [Fact]
        public void Classify_ProfileRuleBeatsTheBuiltInDictionary()
        {
            var rules = new List<ProjectionRuleConfig>
            {
                new("MAIM*", null, "קו מים ראשי", "utility", 140),
            };
            var match = Classify("MAIM16Z", null, rules);
            match!.Label.Should().Be("קו מים ראשי");
            match.ColorIndex.Should().Be(140);
        }

        [Fact]
        public void Classify_XrefPatternScopesARule()
        {
            var rules = new List<ProjectionRuleConfig> { new("*", "UT-*", "מערכת", "utility", null) };
            Classify("ANY-LAYER", "UT-3D", rules).Should().NotBeNull();
            Classify("ANY-LAYER", "BASE-MODEL > UT-3D", rules).Should().NotBeNull(
                "nested traversal retains a chain, and the actual nested XREF must still match");
            Classify("ANY-LAYER", "GM-MODEL", rules).Should().BeNull(
                "a rule scoped to the UT XREF must not claim other XREFs");
        }

        [Theory]
        [InlineData("C-SSWR-PIPE-EX", "ביוב")]
        [InlineData("C-STRM-PIPE-300", "ניקוז")]
        [InlineData("C-WATR-PIPE-MAIN", "מים")]
        [InlineData("S_PHONE_EXIST", "בזק/תקשורת")]
        public void Classify_UsesOnlyTheFourAuditedCutzUtilityFamilies(
            string layer, string expected)
        {
            var match = Classify(layer, "CUTZ-UTILITIES", new List<ProjectionRuleConfig>());

            match.Should().NotBeNull();
            match!.Label.Should().Be(expected);
            match.Kind.Should().Be("utility");
        }

        [Theory]
        [InlineData("DR-PIPE")]
        [InlineData("S_PIPE")]
        [InlineData("S_SUPER_PIPE")]
        public void Classify_DoesNotGuessAmbiguousCutzPipeFamilies(string layer) =>
            Classify(layer, "CUTZ-UTILITIES", new List<ProjectionRuleConfig>())
                .Should().BeNull();

        [Fact]
        public void PreservedV2Profile_GainsObservedNatalyPlanMarkDefaultsAtRuntime()
        {
            // Exact plan_mark_rules from Arthur's managed v2 profile. The installer
            // correctly preserves that approved file, so newly shipped YAML entries
            // alone cannot teach an existing installation these SM/GM conventions.
            var oldActiveRules = new List<ProjectionRuleConfig>
            {
                new("*TR-ISLAND*", null, "אי תנועה", "island", null),
                new("*BIKE*", null, "שביל אופניים", "bike", null),
                new("END-MDR*", null, "מדרכה", "sidewalk", null),
                new("HW-TRWY*", null, "שפת מיסעה", "lane", null),
                new("*CURB*", null, "אבן שפה", "curb", null),
            };
            var preservedSnapshot = oldActiveRules
                .Select(rule => $"{rule.LayerPattern}|{rule.XrefPattern}|{rule.Label}|" +
                                $"{rule.Kind}|{rule.ColorIndex}")
                .ToArray();

            var runtime = WithObservedPlanMarkDefaults(oldActiveRules);

            Classify("SM-CURB-ILND-EX", null, runtime)!.Kind.Should().Be("curb",
                "a shipped convention must not overwrite the profile's explicit wildcard rule");
            Classify("A-MIDRACHA-EX", null, runtime)!.Kind.Should().Be("sidewalk");
            Classify("HW-CURB-GRDN", null, runtime)!.Kind.Should().Be("curb",
                "an engineer can put a more specific garden rule before the broad curb rule");
            Classify("6422-SM-MODEL-NATAZ|HW-HATCH- SIDEWALK", "6422-SM-MODEL-NATAZ", runtime)!
                .Kind.Should().Be("sidewalk", "Natalie's SM models carry this exact XREF-qualified office layer");
            Classify("KAV_NETIVIM_NTZ", null, runtime)!.Kind.Should().Be("strip");
            Classify("6422-SM-MODEL-NATAZ|KAV_NETIVIM_NTZ", "6422-SM-MODEL-NATAZ", runtime)!
                .Kind.Should().Be("strip", "exact profile rules must match the leaf of an XREF-qualified layer");
            Classify("GM-PGVUL-EX", null, runtime)!.Kind.Should().Be("row");
            Classify("ROW_2024-08", "6422-WEST", runtime)!.Kind.Should().Be("row");
            LayerLeaf("OUTER|INNER|HW-CURB").Should().Be("HW-CURB");
            oldActiveRules.Select(rule =>
                    $"{rule.LayerPattern}|{rule.XrefPattern}|{rule.Label}|" +
                    $"{rule.Kind}|{rule.ColorIndex}")
                .Should().Equal(preservedSnapshot,
                    "the runtime overlay must not rewrite the preserved profile");
        }

        [Theory]
        [InlineData("TR-INNER-ISLAND-CURBSTONE", "curb", "אבן שפה")]
        [InlineData("6422-GM-MODEL-NATAZ|TR-INNER-ISLAND-CURBSTONE", "curb", "אבן שפה")]
        [InlineData("TR-GRDN-STONE", "garden", "גבול גינון")]
        [InlineData("6422-GM-MODEL-NATAZ|TR-INNER-GRDN-STONE", "garden", "גבול גינון")]
        public void ExplicitProjectRuleWinsAndUnmatchedOfficeDefaultsRemainAvailable(
            string layer,
            string expectedKind,
            string expectedLabel)
        {
            var runtime = WithObservedPlanMarkDefaults(new List<ProjectionRuleConfig>
            {
                new("*CURB*", null, "אבן שפה", "curb", null),
            });

            var match = Classify(layer, "6422-GM-MODEL-NATAZ", runtime);

            match.Should().NotBeNull();
            match!.Kind.Should().Be(expectedKind);
            match.Label.Should().Be(expectedLabel);
        }

        [Theory]
        [InlineData("HW-HATCH-ILND")]
        [InlineData("HW-HATCH-GARDEN")]
        [InlineData("HW-HATCH-GARDEN-PL")]
        [InlineData("HW-HATCH- SIDEWALK")]
        [InlineData("TR-INNER-CURBSTONE")]
        [InlineData("S_ISLAND")]
        [InlineData("S_GARDEN_STONE")]
        [InlineData("TR-GRDN-STONE-NOTES")]
        public void ExactObservedNatalyBoundaryLeaves_DoNotBroadenToLookalikes(string layer)
        {
            var runtime = WithObservedPlanMarkDefaults(new List<ProjectionRuleConfig>());

            var match = Classify(layer, "6422-GM-MODEL-NATAZ", runtime);

            match?.Kind.Should().NotBe("island").And.NotBe("garden");
        }

        [Fact]
        public void ExplicitSamePatternProfileRuleOverridesObservedRuntimeDefault()
        {
            var explicitRules = new List<ProjectionRuleConfig>
            {
                new("*MIDRACHA*", "SM-*", "מדרכה מאושרת", "mark", 123),
                new("*CURB*", null, "אבן שפה", "curb", null),
            };

            var runtime = WithObservedPlanMarkDefaults(explicitRules);
            var match = Classify("A-MIDRACHA-EX", "SM-MODEL", runtime)!;

            match.Kind.Should().Be("mark");
            match.Label.Should().Be("מדרכה מאושרת");
            match.ColorIndex.Should().Be(123);
            runtime.Count(r => r.LayerPattern == "*MIDRACHA*").Should().Be(1,
                "the fallback is replaced, not duplicated behind the approved rule");
        }

        [Theory]
        [InlineData("TR-INNER-ISLAND-CURBSTONE", "VENDOR-X")]
        [InlineData("VENDOR-X|TR-INNER-GRDN-STONE", "BASE > VENDOR-X")]
        public void AnotherProjectWildcardAndXrefDecisionOverridesObservedConvention(string layer, string xref)
        {
            var rules = new List<ProjectionRuleConfig>
            {
                new("TR-*", "VENDOR-*", "גבול תכנון בפרויקט", "mark", 123),
            };
            var runtime = WithObservedPlanMarkDefaults(rules);
            var match = Classify(layer, xref, runtime)!;
            match.Kind.Should().Be("mark");
            match.Label.Should().Be("גבול תכנון בפרויקט");
            match.ColorIndex.Should().Be(123);
            runtime[0].Should().BeSameAs(rules[0]);
            rules.Should().HaveCount(1, "runtime conventions do not mutate the stored decisions");
        }

        [Fact]
        public void SpecificExplicitGardenBeforeCurbPreservesEngineerSelectedMeaning()
        {
            var runtime = WithObservedPlanMarkDefaults(new List<ProjectionRuleConfig>
            {
                new("*CURB-GRDN*", null, "גינון מאושר", "garden", 3),
                new("*CURB*", null, "אבן שפה", "curb", null),
            });
            Classify("HW-CURB-GRDN", null, runtime)!.Kind.Should().Be("garden");
            Classify("HW-CURB", null, runtime)!.Kind.Should().Be("curb");
        }

        // ------------------------------------------------------------ mark maths

        [Fact]
        public void AdjacentWidths_ProducesTheDimensionRowBetweenMarks()
        {
            var widths = AdjacentWidths(new[] { -6.35, -3.35, -0.35, 2.65 });
            widths.Should().HaveCount(3);
            widths[0].Width.Should().BeApproximately(3.0, 1e-9);
        }

        [Fact]
        public void AdjacentWidths_DropsNoiseAndUnrelatedGaps()
        {
            AdjacentWidths(new[] { 0.0, 0.05, 40.0 }).Should().BeEmpty(
                "5 cm is drafting noise and 40 m is not a lane");
        }

        [Fact]
        public void MergeNearby_CollapsesADuctBankIntoOneLabel()
        {
            var rule = new ProjectionRuleMatch("utility", "חשמל", 1);
            var crossings = Enumerable.Range(0, 5)
                .Select(i => new Crossing(3.0 + i * 0.1, null, "HASHMAL", "UT-3D", rule))
                .ToList();
            MergeNearby(crossings).Should().ContainSingle("five parallel lines 10 cm apart are one duct bank");
        }

        [Fact]
        public void MergeNearby_KeepsTheCrossingThatKnowsItsElevation()
        {
            var rule = new ProjectionRuleMatch("utility", "מים", 5);
            var merged = MergeNearby(new List<Crossing>
            {
                new(3.0, null, "MAIM", null, rule),
                new(3.2, 281.5, "MAIM", null, rule),
            });
            merged.Should().ContainSingle().Which.Elevation.Should().Be(281.5);
        }

        [Fact]
        public void ProjectionEvidenceComparisonRequiresEveryExactLiveEntityKey()
        {
            var comparison = CompareProjectionEvidence(
                new[] { "PRJ:water", "PRJ:sewer" },
                new Dictionary<string, int>
                {
                    ["PRJ:water"] = 2,
                    ["PRJ:foreign"] = 1,
                });

            comparison.IsExact.Should().BeFalse();
            comparison.Missing.Should().Equal("PRJ:sewer");
            comparison.Unexpected.Should().Equal("PRJ:foreign");
        }

        [Fact]
        public void ProjectionEvidenceKeyChangesForDepthAndDrawnRuleColor()
        {
            Crossing C(double? z, short color) => new(
                3, z, "MAIM", "UT", new ProjectionRuleMatch("utility", "מים", color),
                "A1", 10, 20);

            ProjectionEvidenceKey(C(281, 5)).Should().NotBe(ProjectionEvidenceKey(C(282, 5)));
            ProjectionEvidenceKey(C(281, 5)).Should().NotBe(ProjectionEvidenceKey(C(281, 6)));
        }

        [Fact]
        public void ProjectionAnnotationFingerprintDetectsMovedRecoloredOrRelabelledEntity()
        {
            var baseline = new ProjectionAnnotationSemantic(
                "DBText", new[] { 10.0, 20.0, 0.0, 0.7, -Math.PI / 2 },
                7, null, "ByAci", "מים 281.00", null, "MHD-CD-ANNO");
            var hash = ProjectionAnnotationFingerprint(baseline);

            ProjectionAnnotationFingerprint(baseline with { Geometry = new[] { 10.1, 20.0, 0.0, 0.7, -Math.PI / 2 } })
                .Should().NotBe(hash);
            ProjectionAnnotationFingerprint(baseline with { ColorIndex = 5 })
                .Should().NotBe(hash);
            ProjectionAnnotationFingerprint(baseline with { Text = "מים 282.00" })
                .Should().NotBe(hash);
        }

        // --------------------------------------------------------------- wording

        // ---------------- strip naming (engineer call, 31/08: lane-name row like 1039)

        [Theory]
        [InlineData("island", "island", 3.0, "אי תנועה")]
        [InlineData("bike", "curb", 2.5, "שביל אופניים")]
        [InlineData("curb", "sidewalk", 2.2, "מדרכה")]
        [InlineData("lane", "curb", 3.7, "נתיב נסיעה")]
        [InlineData("lane", "lane", 3.5, "נתיב נסיעה")]
        public void StripLabel_NamesConfidentPairs(string left, string right, double w, string expected) =>
            SectionProjectionLogic.StripLabel(left, right, w).Should().Be(expected);

        [Theory]
        [InlineData("curb", "curb", 3.5)]   // could be נת"צ, parking or a median — engineer's call
        [InlineData("lane", "curb", 9.0)]   // too wide for a single travel lane
        [InlineData("lane", "curb", 2.0)]   // too narrow
        public void StripLabel_StaysSilentWhenUnsure(string left, string right, double w) =>
            SectionProjectionLogic.StripLabel(left, right, w).Should().BeNull();

        [Fact]
        public void StripLabels_WalksAdjacentMarksInOffsetOrder()
        {
            var marks = new[]
            {
                (2.9, "curb"), (-6.0, "sidewalk"), (-3.8, "curb"), (7.1, "island"), (9.2, "island"),
            };
            var strips = SectionProjectionLogic.StripLabels(marks);
            strips.Should().Equal(
                (-6.0, -3.8, "מדרכה"),
                (7.1, 9.2, "אי תנועה"));
        }

        [Fact]
        public void StripLabels_SkipsDraftingNoiseWidths() =>
            SectionProjectionLogic.StripLabels(new[] { (0.0, "bike"), (0.4, "curb") })
                .Should().BeEmpty();

        [Fact]
        public void LightingIsReadableOnAWhitePrintBackground()
        {
            // Yellow (ACI 2) vanishes on the white output the engineers deliver on
            // (their drafted sections, 31/08). Lighting/signals print orange now.
            foreach (var layer in new[] { "TEURA-EX", "MYA-4404-Teura-line", "RAMZOR-POLES" })
                SectionProjectionLogic.Classify(layer, null,
                        System.Array.Empty<SectionProjectionLogic.ProjectionRuleConfig>())!
                    .ColorIndex.Should().Be((short)30, layer);
        }

        [Fact]
        public void WidthMarkKinds_CoverTheStripFamilies() =>
            SectionProjectionLogic.WidthMarkKinds.Should().BeEquivalentTo(
                new[]
                {
                    "curb", "lane", "sidewalk", "island", "bike", "garden",
                    "parking", "shoulder", "row",
                });

        [Fact]
        public void PresentationCoverage_ProvesACompleteDimensionAndStripChain()
        {
            var analysis = AnalyzePresentationCoverage(new[]
                {
                    (-6.0, "sidewalk", "מדרכה"),
                    (-3.0, "curb", "אבן שפה"),
                    ( 0.0, "lane", "קו נתיב"),
                    ( 3.0, "lane", "קו נתיב"),
                    ( 6.0, "curb", "אבן שפה"),
                    ( 8.0, "sidewalk", "מדרכה"),
                },
                requiredLeftOffset: -6.0,
                requiredRightOffset: 8.0);

            analysis.Summary.IsComplete.Should().BeTrue();
            analysis.Summary.DimensionMarkCount.Should().Be(6);
            analysis.Summary.WidthSpanCount.Should().Be(5);
            analysis.Summary.NamedStripCount.Should().Be(5);
            analysis.Summary.VehicleStripCount.Should().Be(3);
            analysis.Summary.OfficeCarStripCount.Should().Be(3);
            analysis.Summary.OuterBoundariesProven.Should().BeTrue();
            analysis.Summary.ContinuousWidthChain.Should().BeTrue();
            analysis.Summary.BoundarySource.Should().Be("plan-mark-extents");
            analysis.Summary.EvidenceDigest.Should().HaveLength(64);
        }

        [Fact]
        public void PresentationCoverage_UnknownCurbToCurbCannotPassAsEmptySuccess()
        {
            var analysis = AnalyzePresentationCoverage(new[]
            {
                (-1.75, "curb", "אבן שפה"),
                ( 1.75, "curb", "אבן שפה"),
            });

            analysis.Summary.WidthSpanCount.Should().Be(1);
            analysis.Summary.NamedStripCount.Should().Be(0);
            analysis.Summary.IsComplete.Should().BeFalse();
        }

        [Fact]
        public void PresentationCoverage_RecognizesNatalieScaleStripButRejectsTheFragment()
        {
            var analysis = AnalyzePresentationCoverage(new[]
            {
                (0.0, "sidewalk", "מדרכה"),
                (1.4, "curb", "אבן שפה"),
            });

            analysis.Summary.IsComplete.Should().BeFalse(
                "a valid 1.40m strip is not proof of the full ROW/CL width");
            analysis.Summary.OuterBoundariesProven.Should().BeFalse();
            analysis.StripLabels.Should().ContainSingle()
                .Which.Label.Should().Be("מדרכה");
        }

        [Fact]
        public void PresentationCoverageDigest_ChangesWhenSourceSemanticsChange()
        {
            var a = AnalyzePresentationCoverage(new[]
            {
                (0.0, "lane", "קו"), (3.0, "curb", "שפה"),
            }).Summary.EvidenceDigest;
            var b = AnalyzePresentationCoverage(new[]
            {
                (0.0, "lane", "קו"), (3.1, "curb", "שפה"),
            }).Summary.EvidenceDigest;
            b.Should().NotBe(a);
        }

        [Fact]
        public void SystemsSummary_NamesProjectedSystemsInsteadOfDenyingThem()
        {
            // Bare list: the column is already titled "מערכות", and the old prefix
            // "מוקרנות:" read as a typo of the מקורות utility label right next to it.
            SystemsSummary(
                    new List<string>(), new List<string> { "מים", "חשמל" },
                    UtilityProjectionScanState.Complete, 2, 2)
                .Should().Be("מים, חשמל");
            SystemsSummary(
                    new List<string> { "רשת ביוב" }, new List<string> { "תאורה" },
                    UtilityProjectionScanState.Complete, 1, 1)
                .Should().Be("תאורה · נדגמות: רשת ביוב");
            SystemsSummary(
                    new List<string> { "רשת ביוב" }, new List<string>(),
                    UtilityProjectionScanState.Complete, 8, 0)
                .Should().Be("נדגמות: רשת ביוב · לא נמצאה חציית מערכת XREF בחתך זה");
            SystemsSummary(
                    new List<string>(), new List<string>(),
                    UtilityProjectionScanState.Complete, 0, 0)
                .Should().Be("לא נמצאו מערכות בשרטוט");
        }

        [Fact]
        public void SystemsSummary_DistinguishesNoCrossingFromBlockedAndDrawingAbsence()
        {
            SystemsSummary(
                    new List<string>(), new List<string>(),
                    UtilityProjectionScanState.Complete, 12, 0)
                .Should().Be("לא נמצאה חציית מערכת בחתך זה");
            SystemsSummary(
                    new List<string>(), new List<string>(),
                    UtilityProjectionScanState.Blocked, 0, 0)
                .Should().Be("סריקת מערכות XREF חסומה — לא ניתן לקבוע חציות");
            SystemsSummary(
                    new List<string>(), new List<string>(),
                    UtilityProjectionScanState.Disabled, 0, 0)
                .Should().Be("מערכות XREF לא נסרקו");
            SystemsSummary(
                    new List<string>(), new List<string>(),
                    UtilityProjectionScanState.NotRun, 0, 0)
                .Should().Be("בדיקת מערכות טרם הושלמה");

            ResolveProjectionScanState(projectionEnabled: true, scanBlocked: false)
                .Should().Be(UtilityProjectionScanState.Complete);
            ResolveProjectionScanState(projectionEnabled: true, scanBlocked: true)
                .Should().Be(UtilityProjectionScanState.Blocked);
            ResolveProjectionScanState(projectionEnabled: false, scanBlocked: true)
                .Should().Be(UtilityProjectionScanState.Disabled);
        }

        // --------------------------------------------------- wiring (source level)

        private static string PluginSourceDir =>
            typeof(SectionProjectionLogicTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginSourceDir }.Concat(parts).ToArray()));

        [Fact]
        public void ApplyDecoratesTheViewsAfterTheyReachTheirFinalPositions()
        {
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");

            var arrange = apply.IndexOf("var moved = ArrangeCreatedViews(", StringComparison.Ordinal);
            var decorate = apply.IndexOf("SectionDecorationService.Decorate", StringComparison.Ordinal);
            arrange.Should().BeGreaterThan(0);
            decorate.Should().BeGreaterThan(arrange,
                "annotations drawn before the views move would be left behind in space");
            apply.Should().Contain("SectionAnnotationRegistry.EraseExisting",
                "removing a tool-owned section must remove its annotations with it");
        }

        [Fact]
        public void ApplyAndVerify_FailClosedWhenTheVisibleAnnotationsAreMissing()
        {
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");
            var decoration = Read("CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");
            var verify = Read("CivilDelivery", "Sections", "Services", "SectionVerifyService.cs");
            var registry = Read("CivilDelivery", "Sections", "Services", "SectionAnnotationRegistry.cs");

            apply.Should().Contain("outcome.Decorated != targets.Count")
                .And.Contain("return false;",
                    "an incomplete annotation pass must abort the same atomic APPLY transaction");
            decoration.Should().Contain("rec.Status = DeliveryStatus.Failed")
                .And.Contain("Severity = FindingSeverity.Error");
            verify.Should().Contain("registered_annotations")
                .And.Contain("applyRecord.AnnotationCount > 0");
            registry.Should().Contain("CountExisting",
                "VERIFY must count live registered entities rather than trusting an APPLY number");
        }

        [Fact]
        public void IdempotentReplacement_NeverSwallowsOwnedChildOrAnnotationEraseFailures()
        {
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");
            var helperStart = apply.IndexOf(
                "private static void RemoveToolOwnedSectionObjects", StringComparison.Ordinal);
            helperStart.Should().BeGreaterThan(-1);
            // Stop at this method's own closing brace, not a later helper name:
            // intervening methods may legitimately catch and rethrow unrelated errors.
            const string methodEnd = "\n        }";
            var helperEnd = apply.IndexOf(methodEnd, helperStart, StringComparison.Ordinal);
            helperEnd.Should().BeGreaterThan(helperStart);
            var helper = apply.Substring(helperStart, helperEnd - helperStart + methodEnd.Length);

            helper.Should().Contain("tr.GetObject(svId, OpenMode.ForRead, openErased: false)")
                .And.Contain("if (!sv.IsWriteEnabled) sv.UpgradeOpen()")
                .And.Contain("if (!sv.IsErased)")
                .And.Contain("if (!sl.IsWriteEnabled) sl.UpgradeOpen()")
                .And.Contain("if (!sl.IsErased)")
                .And.Contain("SectionAnnotationRegistry.EraseExisting")
                .And.NotContain("catch",
                    "any owned-object reconciliation failure must bubble and abort the caller-owned transaction");

            helper.IndexOf("sv.Erase()", StringComparison.Ordinal)
                .Should().BeLessThan(helper.IndexOf("sl.Erase()", StringComparison.Ordinal),
                    "owned child views must be proven erased before their parent sample line");
        }

        [Fact]
        public void DecorationFailure_StopsTheAtomicBatchAtTheFirstFailedRecord()
        {
            var decoration = Read(
                "CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");
            var decorateStart = decoration.IndexOf(
                "internal static Outcome Decorate(", StringComparison.Ordinal);
            var decorateEnd = decoration.IndexOf(
                "private static (int Utils, int Marks) DecorateOne", decorateStart,
                StringComparison.Ordinal);
            var method = decoration.Substring(decorateStart, decorateEnd - decorateStart);

            method.Should().Contain("rec.Status = DeliveryStatus.Failed")
                .And.Contain("the atomic APPLY batch must stop immediately")
                .And.Contain("throw new InvalidOperationException(",
                    "continuing native Civil calls after one record failed only increases crash risk");

            var decorateOne = decoration.Substring(decorateEnd);
            decorateOne.Should().Contain("var databaseResident = new List<Entity>()")
                .And.Contain("databaseResident.Add(ent)")
                .And.Contain("finally")
                .And.Contain("entity.Dispose()",
                    "transient DBObjects created before a fail-closed branch must release their native wrappers");
        }

        [Fact]
        public void ProjectedEntitiesAndLayoutHaveExactFailClosedEvidence()
        {
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");
            var decoration = Read("CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");
            var registry = Read("CivilDelivery", "Sections", "Services", "SectionAnnotationRegistry.cs");
            var verify = Read("CivilDelivery", "Sections", "Services", "SectionVerifyService.cs");

            decoration.Should().Contain("CompareProjectionEvidence")
                .And.Contain("TrackProjection")
                .And.Contain("could not map its bottom point")
                .And.Contain("projectionHandles");
            registry.Should().Contain("Registered annotation")
                .And.Contain("could not be erased")
                .And.Contain("ReadProjectionEvidence")
                .And.Contain("P:{projectionKey}");
            verify.Should().Contain("projected_entity_registry_evidence_exact")
                .And.Contain("projected_systems_exact")
                .And.Contain("layout_bounds_evidence")
                .And.Contain("layout_non_overlap");
            apply.Should().Contain("Only {items.Count}/{targets.Count} target views reached layout")
                .And.Contain("return false;",
                    "an unreadable or overlapping target layout must abort the atomic transaction");
        }

        [Fact]
        public void KnownUtilityElevationOutsideView_IsBlockedNotCalledUnknownDepth()
        {
            var decoration = Read(
                "CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");

            decoration.Should().Contain("UtilityElevationOutOfRange")
                .And.Contain("carries known elevation")
                .And.Contain("it cannot be relabelled as unknown depth")
                .And.Contain("c.Elevation is { } knownZ")
                .And.Contain("else // Source geometry is genuinely 2D/flat");
        }

        [Fact]
        public void DimensionOffsetLabels_AreBoundedAndVerifiedPerTrueOffset()
        {
            var decoration = Read(
                "CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");
            var verify = Read(
                "CivilDelivery", "Sections", "Services", "SectionVerifyService.cs");
            var applyContract = Read(
                "CivilDelivery", "Sections", "Contracts", "SectionApplyModels.cs");

            decoration.Should().Contain("TryRotatedBottomLabelLadder")
                .And.Contain("DimensionAnchor")
                .And.Contain("FormatDimensionOffsetLabelReference")
                .And.Contain("could not map its bounded position")
                .And.NotContain("if (bottomTexts[i].ProjectionKey != null)");
            verify.Should().Contain("dimension_offset_plan_apply_exact")
                .And.Contain("dimension_offset_labels_live_exact")
                .And.Contain("TryParseDimensionOffsetLabelReference")
                .And.Contain("label.PlacedOffset >= sv.OffsetLeft");
            applyContract.Should().Contain("JsonPropertyName(\"dimension_offset_labels\")");
        }

        [Fact]
        public void TheSignOfEveryOffsetComesFromCivilNotFromTheDrawnDirection()
        {
            var deco = Read("CivilDelivery", "Sections", "Services", "SectionDecorationService.cs");
            var placement = Read("CivilDelivery", "Sections", "Services",
                "SectionAnnotationPlacementContract.cs");
            deco.Should().Contain("SampleLineVertexSideType.Left",
                "Civil states which end is left; the drawn CL direction proves nothing");
            placement.Should().Contain("FindXYAtOffsetAndElevation",
                "annotation coordinates come from the view's own grid mapping");
        }

        [Fact]
        public void ThePanelNamesProjectedSystems()
        {
            var vm = Read("CivilDelivery", "UI", "CivilDeliveryViewModels.cs");
            vm.Should().Contain("ProjectedSystems");
            vm.Should().NotContain("מוקרנות:",
                "the prefix read as a typo of the מקורות label sitting next to it");
        }

        [Fact]
        public void TheWidthCapRunsBeforeTheFingerprintSoRerunsStayIdempotent()
        {
            var logic = Read("CivilDelivery", "Sections", "Services", "SectionPlanLogic.cs");
            logic.Should().Contain("MaxHalfWidthM is { } maxHalf");
            logic.Should().Contain("SectionProjectionLogic.CapEndpoints");

            var service = Read("CivilDelivery", "Sections", "Services", "SectionPlanService.cs");
            var resolve = service.IndexOf("SectionPlanLogic.ResolveAlignment", StringComparison.Ordinal);
            var fingerprint = service.IndexOf("ComputeFingerprint", StringComparison.Ordinal);
            resolve.Should().BeGreaterThan(0);
            fingerprint.Should().BeGreaterThan(resolve,
                "the fingerprint must hash the CAPPED geometry or every rerun replaces every section");
        }

        [Fact]
        public void NestedXrefTransformsComposeEveryInsertLevel()
        {
            var collector = Read("CivilDelivery", "Sections", "Services", "SectionGeometryCollector.cs");

            collector.Should().Contain("var xform = outerTransform * br.BlockTransform;",
                "nested source geometry must reach host WCS through parent and child inserts");
            collector.Should().Contain("depth + 1, xform",
                "the accumulated transform must be passed into the next recursion level");
            collector.Should().NotContain("var xform = br.BlockTransform;",
                "discarding the parent transform projects a nested XREF in the wrong place");
        }

        [Fact]
        public void LegacyHashBasedOwnershipIsMigratedInsteadOfDuplicated()
        {
            var plan = Read("CivilDelivery", "Sections", "Services", "SectionPlanService.cs");
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");

            plan.Should().Contain("v1 logical keys embedded the mutable CL file hash");
            plan.Should().Contain("inventory.Add(stableKey, exact with { LogicalKeyIsExact = false })",
                "legacy ownership must remain discoverable but can only plan a migration UPDATE");
            apply.Should().Contain("MatchesCurrentOrLegacy");
            apply.Should().Contain("legacyGroupId",
                "the existing v1 sample-line group must be reused before any same-name create");
        }
    }
}
