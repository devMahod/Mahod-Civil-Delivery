using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Offline end-to-end source gates for the Civil adapter. Pure direction maths is
    /// covered separately; these checks prevent a correct resolver from being left
    /// unwired in PLAN/APPLY/VERIFY or hidden from the engineer.
    /// </summary>
    public class SectionTrafficDirectionWorkflowSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionTrafficDirectionWorkflowSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Plugin(params string[] parts) =>
            File.ReadAllText(parts.Aggregate(PluginSourceDir, Path.Combine));

        [Fact]
        public void Plan_ResolvesEveryVehicleStripAndUsesBikeOnlyModeForBike()
        {
            var plan = Service("SectionPlanService.cs");
            var model = Plugin("CivilDelivery", "Sections", "Contracts", "SectionPlanModels.cs");

            plan.Should().Contain("SectionTrafficArrowCollector.Collect")
                .And.Contain("FillTrafficDirections(record, analysis, trafficArrows, profile, trafficSources, trafficScopeReason)")
                .And.Contain("trackEvidenceDigest: target.TrackDigest")
                .And.Contain("laneCutFrame: trafficCutFrame")
                .And.Contain("ArrowEvidenceMode.Bicycle")
                .And.Contain("ArrowEvidenceMode.MotorTraffic")
                .And.Contain("SectionFindingCodes.TrafficDirectionUnresolved")
                .And.Contain("record.TrafficDirections.Count !=")
                .And.NotContain("OfficeCarViewForSignedOffset");
            model.Should().Contain("JsonPropertyName(\"traffic_directions\")")
                .And.Contain("direction_digest")
                .And.Contain("evidence_mode");
        }

        [Fact]
        public void AllAutomaticTrafficConsumersUseOneNativeStraightSegmentGate_WithAnExplicitReviewPath()
        {
            var plan = Service("SectionPlanService.cs");
            var adapter = Service("SectionTrafficStraightScopeService.cs");
            plan.Should().Contain("SectionTrafficStraightScopeService.Filter(")
                .And.Contain("record.TrafficStraightScopeEvidence = straightTraffic.CanonicalEvidence")
                .And.Contain("record, projectable, straightTraffic.Evidence, profile, trafficArrows.ExternalSources")
                .And.NotContain("record, projectable, trafficArrows.Evidence, profile, trafficArrows.ExternalSources")
                .And.Contain("record, afterRegions.UnresolvedSpans, trafficArrows")
                .And.Contain("strip.From, strip.To, trafficArrows,")
                .And.Contain("automatic-traffic-scope=")
                .And.Contain("חצים ללא שיוך מוכח לאותו מקטע ישר")
                .And.Contain("מעטפת מרובת מסלולים ללא מיקומים מוכחים");
            adapter.Should().Contain("OpenMode.ForRead")
                .And.Contain("entities.GetEntityByOrder(i)")
                .And.Contain("entity is not CivilDb.AlignmentLine line")
                .And.Contain("line.StartPoint")
                .And.Contain("line.EndPoint")
                .And.Contain("SectionTrafficStraightScopeLogic.Resolve")
                .And.Contain("SectionTrafficStraightScopeLogic.Refuse")
                .And.NotContain("StationToRaw(")
                .And.NotContain("PointLocation(");
        }

        [Fact]
        public void Apply_RestoresExactPlanEvidenceAndRendersOneArrowPerVehicleStrip()
        {
            var decoration = Service("SectionDecorationService.cs");
            var vehicle = Service("SectionVehicleBlockService.cs");

            decoration.Should().Contain("ResolvePlannedDirection(")
                .And.Contain("TryRestoreResolved")
                .And.Contain("trafficArrowBlocks.TryCreate")
                .And.Contain("renderedDirectionArrows")
                .And.Contain("SectionTrafficDirectionArrowService.Evidence")
                .And.Contain("rec.TrafficDirectionArrows.Count != record.TrafficDirections.Count")
                .And.Contain("FormatSchematicVehicleReference")
                .And.Contain("tr, view, spec, midOff, gz, plannedDirection")
                .And.NotContain("OfficeCarViewForSignedOffset");
            vehicle.Should().NotContain("signed offset cannot select a car view")
                .And.Contain("direction.EvidenceMode")
                .And.Contain("direction.OfficeCarView");
        }

        [Fact]
        public void Verify_RequiresExactPlanVehicleAndPinnedOfficeArrowBlock()
        {
            var verify = Service("SectionVerifyService.cs");
            var registry = Service("SectionAnnotationRegistry.cs");

            verify.Should().Contain("planned_traffic_direction_rows_complete")
                .And.Contain("traffic_direction_arrow_evidence_exact")
                .And.Contain("traffic_direction_arrow_handles_live_exact")
                .And.Contain("vehicle_and_visible_arrow_direction_pair_exact")
                .And.Contain("DirectionEvidenceMatches")
                .And.Contain("LiveDirectionArrowMatches")
                .And.Contain("SectionTrafficArrowAssetEvidenceLogic.TryValidateLiveEvidence")
                .And.Contain("nameof(BlockReference)")
                .And.Contain("entity.ColorIndex != expectedColor")
                .And.Contain("SectionTrafficDirectionArrowService.TryComputePlacement(")
                .And.Contain("BlockPlacementMatches(");
            registry.Should().Contain("BlockDefinitionGeometrySha256")
                .And.Contain("BlockDefinitionUsesByBlockColor")
                .And.Contain("BlockScaleFactors")
                .And.Contain("short? ColorIndex");
        }

        [Fact]
        public void PaletteAndAiTool_SaveManualLaneDecisionThenReplan()
        {
            var xaml = Plugin("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
            var ui = Plugin("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
            var tools = Plugin("Tools", "CivilDelivery", "SectionsTools.cs");
            var registry = Plugin("Tools", "ToolRegistry.cs");

            xaml.Should().Contain("x:Name=\"BtnResolveDirection\"")
                .And.Contain("Click=\"OnResolveTrafficDirection\"");
            ui.Should().Contain("new TrafficDirectionDecisionDialog(records, includeResolvedDirections: true)")
                .And.Contain("var records = new[] { selected }")
                .And.Contain("traffic directions selected record:")
                .And.Contain("SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch(")
                .And.Contain("RequireSectionDecisionScope(decisionScope)")
                .And.Contain("expectedState: decisionWrite.ExpectedState")
                .And.Contain("ProjectProfileWriter.Save")
                .And.Contain("OnPlan(this, new RoutedEventArgs())");
            tools.Should().Contain("class ApproveSectionTrafficDirectionTool")
                .And.Contain("SectionDecisionProfileService.ApproveTrafficDirection(")
                .And.NotContain("SectionDecisionProfileService.ApproveEditedTrafficDirectionsBatch")
                .And.Contain("new SectionPlanService().Plan(")
                .And.Contain("CivilDeliverySession.SetPlan");
            registry.Should().Contain("new CivilDelivery.ApproveSectionTrafficDirectionTool()");
        }
    }
}
