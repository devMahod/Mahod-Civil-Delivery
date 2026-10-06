using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Host-free contracts for VERIFY. These guard the Civil API open-mode boundary
    /// and manual-vs-managed ownership split without loading AutoCAD at test runtime.
    /// </summary>
    public class SectionVerifyHardeningContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionVerifyHardeningContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Between(string source, string start, string end)
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            from.Should().BeGreaterThanOrEqualTo(0);
            to.Should().BeGreaterThan(from);
            return source.Substring(from, to - from);
        }

        [Fact]
        public void ManualReuse_VerifiesForeignIdentityAndPreservation_AndOurDecorationOverlap()
        {
            var verify = Read("SectionVerifyService.cs");

            verify.Should().Contain("manual_apply_sample_line_handle_exact")
                .And.Contain("manual_apply_group_handle_exact")
                .And.Contain("manual_apply_section_view_handle_exact")
                .And.Contain("manual_sample_line_remains_foreign")
                .And.Contain("manual_section_view_remains_foreign")
                .And.Contain("manual_view_references_exact_sample_line")
                .And.Contain("manual_section_view_style_preserved")
                .And.Contain("manual_band_styles_preserved")
                .And.Contain("manual_single_datum_presentation_compatible")
                .And.Contain("if (!isManualReuse)")
                .And.NotContain("if (planRecord?.ManualSectionReuse != null ||",
                    "the foreign Civil view is preserved, but Mahod's decorations around it still need overlap proof")
                .And.Contain("SectionViewOverlapService.Inspect(");
        }

        [Fact]
        public void ManagedViews_RetainStrictOwnershipSourceStyleAndLayoutReadBack()
        {
            var verify = Read("SectionVerifyService.cs");

            verify.Should().Contain("sample_line_group_ownership")
                .And.Contain("sample_line_ownership")
                .And.Contain("section_view_ownership")
                .And.Contain("SampledSourceNamesReadOnly(db, slgId)")
                .And.Contain("managed_section_view_style_exact")
                .And.Contain("managed_band_contract")
                .And.Contain("layout_bounds_evidence")
                .And.Contain("layout_view_location_evidence")
                .And.Contain("LayoutEvidenceContract.Verify(")
                .And.Contain("AddSelectedNonOverlapChecks(db, tr, plan, applied, result)")
                .And.NotContain("planRecord?.ManualSectionReuse != null")
                .And.Contain("SectionPlanLogic.HasValidExplicitExclusion(planRecord)")
                .And.Contain("SectionViewOverlapService.Inspect(")
                .And.Contain("applyRecord.LogicalKey")
                .And.Contain("layout_non_overlap");
        }

        [Fact]
        public void AnnotationContract_FailsClosedOnDatumVehiclesLayerAndLegacyArtifacts()
        {
            var verify = Read("SectionVerifyService.cs");
            var registry = Read("SectionAnnotationRegistry.cs");

            verify.Should().Contain("annotation_registry_readable")
                .And.Contain("registered_annotations_owned_layer")
                .And.Contain("annotation_contract_version")
                .And.Contain("vehicle_evidence_complete")
                .And.Contain("datum_evidence_valid")
                .And.Contain("single_registered_datum_reference")
                .And.Contain("vehicle_evidence_valid")
                .And.Contain("office_vehicle_blocks_live_exact")
                .And.Contain("office_car_fallback_absent")
                .And.Contain("vehicle_fallback_style_live");
            registry.Should().Contain("ReadAnnotationContractEvidence")
                .And.Contain("Duplicate annotation handle")
                .And.Contain("BlockDefinitionComments")
                .And.Contain("BlockDefinitionGeometrySha256")
                .And.Contain("SectionVehicleBlockService.GeometryFingerprint");
            verify.Should().Contain("SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence")
                .And.NotContain("sha.StartsWith(item.Detail");
        }

        [Fact]
        public void Verify_RecomputesEveryPrimaryAnnotationSemanticFingerprint()
        {
            var verify = Read("SectionVerifyService.cs");
            var registry = Read("SectionAnnotationRegistry.cs");

            registry.Should().Contain("$\"A:{hex}:{fingerprint}\"")
                .And.Contain("RegisteredFingerprint")
                .And.Contain("LiveFingerprint")
                .And.Contain("SectionProjectionAnnotationSemantics.Fingerprint(entity)")
                .And.Contain("case DBText dbText")
                .And.Contain("case Polyline polyline")
                .And.Contain("case BlockReference block");
            verify.Should().Contain("registered_annotations_unchanged_since_apply")
                .And.Contain("IsSha256(entry.RegisteredFingerprint)")
                .And.Contain("entry.RegisteredFingerprint, entry.LiveFingerprint");
        }

        [Fact]
        public void Verify_RecomputesManagedAnnotationPlacementFromExactLiveGround()
        {
            var verify = Read("SectionVerifyService.cs");
            var decoration = Read("SectionDecorationService.cs");
            var placement = Read("SectionAnnotationPlacementContract.cs");
            var vehicle = Read("SectionVehicleBlockService.cs");
            var arrow = Read("SectionTrafficDirectionArrowService.cs");

            verify.Should().Contain("annotation_ground_sources_live_exact")
                .And.Contain("TextPlacementMatches(")
                .And.Contain("BlockPlacementMatches(")
                .And.Contain("SlopeEvidenceMatchesLiveDesign(")
                .And.Contain("SchematicVehicleGeometryMatches(")
                .And.Contain("datum_matches_live_existing_ground")
                .And.Contain("SectionAnnotationPlacementContract.DimensionLabelPosition")
                .And.Contain("SectionAnnotationPlacementContract.DatumPosition");
            decoration.Should().Contain("SectionAnnotationPlacementContract.ReadSurfaceChains(")
                .And.Contain("SectionAnnotationPlacementContract.SlopePosition(")
                .And.Contain("SectionAnnotationPlacementContract.DimensionLabelPosition(")
                .And.Contain("SectionAnnotationPlacementContract.DatumPosition(");
            placement.Should().Contain("ResolveLiveSourceIdentityStrict")
                .And.Contain("counts[0] != 1 || counts[1] != 1")
                .And.Contain("SectionFurnitureLogic.NormalizeSectionPoints");
            vehicle.Should().Contain("SectionAnnotationPlacementLogic.TryCarPlacement(");
            arrow.Should().Contain("SectionAnnotationPlacementLogic.TryArrowPlacement(");
        }

        [Fact]
        public void AnnotationInventory_IsBidirectionalAndRequiredByPlanApplyVerify()
        {
            var registry = Read("SectionAnnotationRegistry.cs");
            var plan = Read("SectionPlanService.cs");
            var apply = Read("SectionApplyService.cs");
            var verify = Read("SectionVerifyService.cs");
            var decoration = Read("SectionDecorationService.cs");

            registry.Should().Contain("ValidateOwnedLayerInventory(")
                .And.Contain("AnnotationInventoryLogic.Evaluate(")
                .And.Contain("owned annotation layer contains unregistered entity")
                .And.Contain("registeredEvidence")
                .And.Contain("layerEvidence")
                .And.Contain("var legacyRaw = !value.StartsWith(\"A:\"")
                .And.Contain("if (proof.LegacyRaw)")
                .And.Contain("OwnershipState.LegacyAbsent")
                .And.Contain("ownershipValid = registeredFingerprint != null")
                .And.Contain("SectionOwnershipService.Read(tr, entity)");
            decoration.Should().Contain("Role = \"annotation\"")
                .And.Contain("InputFingerprint = fingerprint")
                .And.Contain("SectionAnnotationRegistry.Record(");
            plan.Should().Contain("SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db)")
                .And.Contain("SectionFindingCodes.AnnotationInventoryConflict");
            apply.Should().Contain("SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db)")
                .And.Contain("SectionFindingCodes.AnnotationInventoryConflict");
            verify.Should().Contain("SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db)")
                .And.Contain("registered_annotations_ownership_exact");
        }

        [Fact]
        public void AnnotationErase_PreflightsEveryFingerprintBeforeFirstWrite()
        {
            var registry = Read("SectionAnnotationRegistry.cs");
            var erase = Between(
                registry,
                "internal static int EraseExisting",
                "/// <summary>Records the annotation handles");

            erase.Should().Contain("var entities = new List<(ObjectId Id, string Hex)>")
                .And.Contain("RequireUniquePrimaryRegistryHandles(tr, dict)")
                .And.Contain("TryParsePrimaryEntry(value, out var primaryHex")
                .And.Contain("registeredFingerprint != null")
                .And.Contain("projectionFingerprint ?? string.Empty")
                .And.Contain("SectionProjectionAnnotationSemantics.Fingerprint(entity)")
                .And.Contain("registeredFingerprint, liveFingerprint")
                .And.Contain("SectionAnnotationResourceContracts.IsKnownAnnotationLayer")
                .And.Contain("foreach (var projection in projections)")
                .And.Contain("foreach (var item in entities)");
            erase.IndexOf("foreach (var item in entities)", StringComparison.Ordinal)
                .Should().BeLessThan(erase.IndexOf("OpenMode.ForWrite", StringComparison.Ordinal),
                    "no entity is opened for write until every registry entry is authorised");
            erase.Should().Contain("Invalid annotation entry")
                .And.Contain("Invalid projection annotation entry")
                .And.Contain("Registered annotation {hex} is on foreign layer")
                .And.Contain("registeredFingerprint != null")
                .And.Contain("fingerprints[hex] = liveFingerprint")
                .And.NotContain("ValidateOwnedLayerInventory(tr, db)",
                    "the global inventory is performed once by ApplyCore, not once per record");
        }

        [Fact]
        public void Verify_LiveChecksOwnedLayerAndTrueTypeTextStyle()
        {
            var verify = Read("SectionVerifyService.cs");

            // 1.2.27: the live layer/text-style reads go through the same Core contract
            // APPLY normalizes against (visibility + colour 7 + opacity; Arial + width
            // factor 1 + oblique 0 + not vertical/mirrored), so VERIFY cannot stay green
            // on a resource APPLY would have refused.
            verify.Should().Contain("annotation_layer_visible_plottable")
                .And.Contain("SectionDecorationService.ReadLayerState(tr, db, layer)")
                .And.Contain("SectionAnnotationResourceContracts.ValidateLayer(layerState)")
                .And.Contain("annotation_text_style_arial_truetype")
                .And.Contain("SectionDecorationService.ReadTextStyleState(style)")
                .And.Contain("SectionAnnotationResourceContracts.ValidateTextStyle(textStyleState)")
                .And.Contain("annotation_linetypes_semantics_exact")
                .And.Contain("SectionDecorationService.InvalidLinetypes(tr, db)")
                .And.Contain("registered_text_uses_owned_truetype_style")
                .And.Contain("entry.TextStyleName");
        }

        [Fact]
        public void Verify_MapsExactPlannedEgFgSourcesAndKeepsManualForeignPresentationTruthful()
        {
            var verify = Read("SectionVerifyService.cs");

            verify.Should().Contain("planned_surface_source_to_live_section_exact")
                .And.Contain("SectionSourceIntegrityLogic.ExactNameSet(")
                .And.Contain("manual_eg_fg_sources_visible_distinct")
                .And.Contain("liveExisting.Visible && liveDesign.Visible")
                .And.Contain("liveExisting.StyleName, liveDesign.StyleName")
                .And.Contain("managed_eg_fg_styles_live_exact")
                .And.Contain("SectionSurfaceStyleContractLogic.TryValidateLive");
        }

        [Theory]
        [InlineData(new[] { "EG", "FG" }, new[] { "EG", "FG" }, true)]
        [InlineData(new[] { "EG", "FG" }, new[] { "fg", "eg" }, true)]
        [InlineData(new[] { "EG", "FG" }, new[] { "EG", "FG", "EXTRA" }, false)]
        [InlineData(new[] { "EG", "FG" }, new[] { "EG", "EG" }, false)]
        [InlineData(new[] { "EG", "FG" }, new[] { "EG" }, false)]
        public void SurfaceSourceSet_RequiresExactPairAndRejectsExtraOrDuplicate(
            string[] planned, string[] live, bool expected)
        {
            SectionSourceIntegrityLogic.ExactNameSet(planned, live)
                .Should().Be(expected);
        }

        [Fact]
        public void RegistryContractRead_UsesOnlyForReadObjects()
        {
            var registry = Read("SectionAnnotationRegistry.cs");
            var method = Between(
                registry,
                "internal static AnnotationRegistryReadResult ReadAnnotationContractEvidence",
                "internal static AnnotationEnvelopeEvidence ReadEnvelope(");

            method.Should().Contain("OpenMode.ForRead")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain("UpgradeOpen")
                .And.NotContain(".Erase(")
                .And.NotContain(".Commit(")
                .And.NotContain("RepairDeadEntries(");
            // A new explicit APPLY-only writer elsewhere in the registry class is
            // not part of this read method and must never become a VERIFY fallback.
            var verify = Read("SectionVerifyService.cs");
            verify.Should().Contain("SectionAnnotationRegistry.ValidateOwnedLayerInventory(tr, db)")
                .And.Contain("if (!annotationInventory.IsValid)")
                .And.NotContain("RepairDeadEntries(")
                .And.NotContain("EligibleDeadAnnotationRecoveryKeys(");
        }

        [Fact]
        public void CorePresentation_IsPlanDerivedHandleBoundAndLiveRecomputed()
        {
            var contract = Read("SectionCorePresentationContract.cs");
            var placement = Read("SectionAnnotationPlacementContract.cs");
            var apply = Read("SectionDecorationService.cs");
            var verify = Read("SectionVerifyService.cs");

            contract.Should().Contain("coverage.DimensionMarks.Count != coverage.DimensionMarkCount")
                .And.Contain("mark.ColorIndex < 1 || mark.ColorIndex > 255")
                .And.Contain("coverage.ResolvedSpans.Count != coverage.WidthSpanCount")
                .And.Contain("DimensionTopTick")
                .And.Contain("DimensionBottomTick")
                .And.Contain("RowLine")
                .And.Contain("WidthLabel")
                .And.Contain("StripLabel")
                .And.Contain("semantic key is not unique");
            placement.Should().Contain("SectionAnnotationPlacementLogic.AxisLine")
                .And.Contain("SectionAnnotationPlacementLogic.TopTick")
                .And.Contain("SectionAnnotationPlacementLogic.BottomTick")
                .And.Contain("SectionAnnotationPlacementLogic.WidthLabelPosition")
                .And.Contain("SectionAnnotationPlacementLogic.StripLabelPosition");
            apply.Should().Contain("SectionCorePresentationContract.ExpectedFor(")
                .And.Contain("rec.CorePresentationAnnotations.AddRange(")
                .And.Contain("presentation.DimensionMarks")
                .And.Contain("TrackCore(NewLine(topTick.Start, topTick.End")
                .And.Contain("TrackCore(NewLine(bottomTick.Start, bottomTick.End")
                .And.Contain("var tickColor = plannedMark.ColorIndex")
                .And.NotContain("new Point3d(tp.X, tp.Y - 2.0",
                    "APPLY must not carry a second placement formula");
            verify.Should().Contain("core_presentation_semantics_exact")
                .And.Contain("core_presentation_geometry_live_exact")
                .And.Contain("CorePresentationMatches(")
                .And.Contain("LinePlacementMatches(")
                .And.Contain("SectionAnnotationPlacementContract.RowLine(")
                .And.Contain("SectionAnnotationPlacementContract.WidthLabelPosition(");
            verify.Should().Contain("expected.ColorIndex is { } topColor")
                .And.Contain("PresentationColorMatches(")
                .And.Contain("entity.TextStyleName, SectionDecorationService.AnnoTextStyle")
                .And.Contain("entity.EntityType, nameof(DBText)");
        }

        [Fact]
        public void SectionSourcesWriteOpen_RemainsIsolatedToAbortedHelper()
        {
            var verify = Read("SectionVerifyService.cs");
            var source = Read("SectionSourceService.cs");
            var helper = Between(
                source,
                "public static SampledSourceSnapshot SampledSourceNamesReadOnly",
                "private static string? ResolveName");

            verify.Should().Contain("if (manualReuse == null)")
                .And.Contain("SampledSourceNamesReadOnly(db, slgId)")
                .And.NotContain("GetSectionSources(");
            helper.Should().Contain("OpenMode.ForWrite")
                .And.Contain("ReadSampledSourcesStrict(sourceTr, group)")
                .And.Contain("SampledSourceSnapshot.Invalid")
                .And.Contain("sourceTr.Abort()")
                .And.NotContain("sourceTr.Commit()");
        }
    }
}
