using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.Services.Overlay;
using MahodAI.Civil3D.Plugin.WebSocket;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Verifies the correlation of on-drawing pins (analysis findings) to fix-plan items.
    /// Pure logic — no AutoCAD runtime. The safety property under test: a pin is only ever
    /// tagged when a confident match exists, and never the WRONG pin.
    /// </summary>
    public class MarkerFixMatcherTests
    {
        private static ProblemMarker Marker(string align, double s1, double? s2 = null, string problem = "")
            => new ProblemMarker
            {
                Alignment = align,
                StationStart = s1,
                StationEnd = s2,
                Problem = problem,
            };

        private static FixPlanItem Item(string id, string objectName, double? targetStation, string description = "")
        {
            JsonElement? toolParams = null;
            if (targetStation.HasValue)
            {
                using var doc = JsonDocument.Parse(
                    $"{{\"target_station\": {targetStation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
                toolParams = doc.RootElement.Clone();
            }
            return new FixPlanItem
            {
                Id = id,
                ObjectName = objectName,
                ToolParams = toolParams,
                Description = description,
            };
        }

        [Fact]
        public void AssignFixItems_MatchesByAlignmentAndStation()
        {
            var markers = new List<ProblemMarker> { Marker("73", 374.13) };
            var items = new List<FixPlanItem> { Item("i1", "73", 374.13) };

            int matched = MarkerFixMatcher.AssignFixItems(markers, items);

            matched.Should().Be(1);
            markers[0].FixItemId.Should().Be("i1");
            markers[0].FixState.Should().Be(MarkerFixState.Fixable);
        }

        [Fact]
        public void AssignFixItems_TwoCurvesSameAlignment_AssignsNearestStation_OneToOne()
        {
            // Two distinct curves on the same alignment; each item must claim its OWN pin.
            var near = Marker("73", 100.0, 130.0);   // mid 115
            var far = Marker("73", 500.0, 540.0);    // mid 520
            var markers = new List<ProblemMarker> { near, far };
            var items = new List<FixPlanItem>
            {
                Item("far", "73", 520.0),
                Item("near", "73", 115.0),
            };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(2);

            near.FixItemId.Should().Be("near");
            far.FixItemId.Should().Be("far");
        }

        [Fact]
        public void AssignFixItems_StationOnly_MatchesAcrossDifferentObjectName()
        {
            // Profile fix: object_name is the profile ("P73"), but the pin was placed on the
            // hosting alignment ("73"). Station-only fallback rescues it.
            var markers = new List<ProblemMarker> { Marker("73", 250.0) };
            var items = new List<FixPlanItem> { Item("i1", "P73", 250.0) };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(1);
            markers[0].FixItemId.Should().Be("i1");
        }

        [Fact]
        public void AssignFixItems_StationTooFar_DoesNotMatch()
        {
            var markers = new List<ProblemMarker> { Marker("73", 100.0) };
            var items = new List<FixPlanItem> { Item("i1", "999", 500.0) };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(0);
            markers[0].FixItemId.Should().BeNull();
            markers[0].FixState.Should().Be(MarkerFixState.None);
        }

        [Fact]
        public void AssignFixItems_SingleCandidateOnAlignment_NoStation_Matches()
        {
            var markers = new List<ProblemMarker> { Marker("Main St", 400.0, problem: "רדיוס קטן מהמינימום") };
            var items = new List<FixPlanItem> { Item("i1", "Main St", null, "רדיוס קטן") };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(1);
            markers[0].FixItemId.Should().Be("i1");
        }

        [Fact]
        public void AssignFixItems_MoreItemsThanMarkers_NeverDoubleAssigns()
        {
            var only = Marker("73", 200.0);
            var markers = new List<ProblemMarker> { only };
            var items = new List<FixPlanItem>
            {
                Item("a", "73", 200.0),
                Item("b", "73", 200.5),   // also within tolerance, but the marker is taken
            };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(1);
            only.FixItemId.Should().Be("a");   // first (station-bearing) item claims it; not overwritten
        }

        [Fact]
        public void AssignFixItems_ResetsPriorTagging()
        {
            var m = Marker("73", 200.0);
            m.FixItemId = "stale";
            m.FixState = MarkerFixState.Fixed;
            var markers = new List<ProblemMarker> { m };

            // No matching item → prior tagging must be wiped, not left stale.
            MarkerFixMatcher.AssignFixItems(markers, new List<FixPlanItem> { Item("x", "other", 9999.0) });

            m.FixItemId.Should().BeNull();
            m.FixState.Should().Be(MarkerFixState.None);
        }

        [Fact]
        public void AssignFixItems_AmbiguousCrossAlignmentStation_DoesNotGreenWrongPin()
        {
            // Two alignments each have a violation near station 100. A profile fix item whose
            // object_name matches neither alignment must NOT bind to either — that would risk
            // greening the wrong alignment's pin.
            var a = Marker("Align1", 100.0);
            var b = Marker("Align2", 100.0);
            var markers = new List<ProblemMarker> { a, b };
            var items = new List<FixPlanItem> { Item("i1", "SomeProfile", 100.0) };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(0);
            a.FixItemId.Should().BeNull();
            b.FixItemId.Should().BeNull();
        }

        [Fact]
        public void AssignFixItems_StationBearingItem_FarFromLoneMarker_DoesNotBind()
        {
            // Alignment A has one marker at st 900; a station-bearing item targets st 100 on A
            // (its real finding produced no pin). It must NOT bind to the far st-900 marker —
            // the 12 m tolerance is a hard guard for station-bearing items.
            var m = Marker("A", 900.0);
            var markers = new List<ProblemMarker> { m };
            var items = new List<FixPlanItem> { Item("i1", "A", 100.0) };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(0);
            m.FixItemId.Should().BeNull();
        }

        [Fact]
        public void AssignFixItems_SameAlignItem_NotStolenByCrossAlignStationItem()
        {
            // Multi-pass guarantee: the same-alignment+station item claims its marker in pass 1
            // before a station-only item can grab it in pass 2.
            var m = Marker("73", 100.0);
            var markers = new List<ProblemMarker> { m };
            var items = new List<FixPlanItem>
            {
                Item("profile", "P73", 100.0),   // would match by station-only in pass 2
                Item("align", "73", 100.0),       // exact same-alignment match in pass 1
            };

            MarkerFixMatcher.AssignFixItems(markers, items).Should().Be(1);
            m.FixItemId.Should().Be("align");     // pass 1 wins
        }

        [Fact]
        public void AssignFixItems_NullOrEmpty_IsSafe()
        {
            var markers = new List<ProblemMarker> { Marker("73", 1.0) };
            MarkerFixMatcher.AssignFixItems(markers, null).Should().Be(0);
            MarkerFixMatcher.AssignFixItems(markers, new List<FixPlanItem>()).Should().Be(0);
            markers[0].FixItemId.Should().BeNull();
        }
    }
}
