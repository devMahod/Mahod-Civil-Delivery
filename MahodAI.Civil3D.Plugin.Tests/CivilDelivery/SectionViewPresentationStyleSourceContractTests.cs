using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionViewPresentationStyleSourceContractTests
    {
        private static string Source()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            return File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionViewPresentationStyleService.cs"));
        }

        [Fact]
        public void BuiltInTypicalSectionStyle_HidesNativeGridAxesAndRepeatedElevations()
        {
            var source = Source();
            source.Should().Contain("MHD-TYPICAL-SECTION-V2")
                .And.Contain("Enum.GetValues(typeof(CivilStyles.SectionViewDisplayStyleType))")
                .And.Contain("var display = style.GetDisplayStylePlan(component)")
                .And.Contain("display.Visible = false")
                .And.Contain("if (display.Visible)",
                    "every component write must be read back before commit")
                // Civil 3D 2027 throws InvalidOperationException when any of these
                // AxisStyle setters is written on the product-owned style.
                .And.NotContain(".ShowTickAndLabel")
                .And.Contain("VerticalExaggeration = 1.0");
        }

        [Fact]
        public void BuiltInStyle_IsProductNamedAndDoesNotModifyAnArbitraryDocumentStyle()
        {
            var source = Source();
            source.Should().Contain("styles.Contains(StyleName)")
                .And.Contain("styles.Add(StyleName)")
                .And.Contain("StyleDescription")
                .And.Contain("SectionView style name collision")
                .And.Contain("it was not modified")
                .And.Contain("if (existed)")
                .And.Contain("style.UpgradeOpen();")
                .And.Contain("SharedResourceLogic.Decide(true, IsCompliant(style)")
                .And.Contain("SectionFindingCodes.SharedResourceChangeRequired")
                .And.Contain("failed its live property read-back")
                .And.NotContain("styles[0]")
                .And.NotContain("First()")
                .And.NotContain("Road Section");
        }

        [Fact]
        public void PlanAndApply_SelectTheOwnedStyleWithoutMutatingOfficeStyles()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var plan = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionPlanService.cs"));
            var apply = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionApplyService.cs"));

            plan.Should().Contain("SectionViewPresentationStyleService.StyleName")
                .And.Contain("PresentationStyleBuiltIn");
            apply.Should().Contain("SectionViewPresentationStyleService.Ensure(")
                .And.Contain("tr, civilDoc, allowModify: !selectedScope")
                .And.Contain("builtInPresentationStyleId")
                .And.Contain("SectionViewPresentationStyleService.Apply(")
                .And.Contain("view, builtInPresentationStyleId")
                .And.NotContain("CivilApplication.ActiveDocument");

            apply.IndexOf("SectionViewPresentationStyleService.Ensure(",
                    StringComparison.Ordinal)
                .Should().BeLessThan(apply.IndexOf("foreach (var record in targets)",
                        StringComparison.Ordinal),
                    "the owned native style must be prepared once per atomic batch, not rewritten for every view");
        }

        [Fact]
        public void BlankBandStyle_ImportsAProvenOwnedEmptyBandSetAndReadsTheViewBack()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var apply = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionApplyService.cs"));
            var decoration = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));

            apply.Should().Contain("SectionDecorationService.ClearBandSet(")
                .And.Contain("tr, civilDoc, view, recordResult");
            decoration.Should().Contain("MHD-NO-NATIVE-BANDS-V2")
                .And.Contain("Mahod-owned empty SectionView band set")
                .And.Contain("civilDoc.Styles.SectionViewBandSetStyles")
                .And.Contain("styles.Contains(EmptyBandSetStyleName)")
                .And.Contain("styles.Add(EmptyBandSetStyleName)")
                .And.Contain("style.Description = EmptyBandSetStyleDescription")
                .And.Contain("Band-set style name collision")
                .And.Contain("style.GetBottomBandSetItems()")
                .And.Contain("style.GetTopBandSetItems()")
                .And.Contain("view.Bands.ImportBandSetStyle(styleId)")
                .And.Contain("AssertViewHasNoBands(view)")
                .And.Contain("view.Bands.GetBottomBandItems()")
                .And.Contain("view.Bands.GetTopBandItems()")
                .And.Contain("catch (Exception ex)")
                .And.Contain("Code = SectionFindingCodes.BandStyleMissing")
                .And.Contain("AffectedRecordIds = { rec.RecordId }");
        }

        [Fact]
        public void BlankBandStyle_NeverCallsCivilNativeBandCollectionSetters()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var helper = source[source.IndexOf("internal static void ClearBandSet", StringComparison.Ordinal)..];
            helper = helper[..helper.IndexOf("internal const double MaxViewSpanM", StringComparison.Ordinal)];

            helper.Should().Contain("ImportBandSetStyle")
                .And.NotContain("SetBottomBandItems")
                .And.NotContain("SetTopBandItems")
                .And.NotContain("RemoveAll()");
        }

        [Fact]
        public void BandCleanupFailure_IsAHardErrorAndRethrownForAtomicRollback()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var helper = source[source.IndexOf("internal static void ClearBandSet", StringComparison.Ordinal)..];
            helper = helper[..helper.IndexOf("internal const double MaxViewSpanM", StringComparison.Ordinal)];

            helper.Should().Contain("catch (Exception ex)")
                .And.Contain("Severity = FindingSeverity.Error")
                .And.Contain("rec.Findings.Add")
                .And.Contain("throw new InvalidOperationException")
                .And.Contain("APPLY must roll back")
                .And.NotContain("Severity = FindingSeverity.Warning");
        }

        [Fact]
        public void BandContractFailure_ReachesBothAtomicAbortGatesBeforeAnyCommit()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var apply = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionApplyService.cs"));
            var tools = File.ReadAllText(Path.Combine(pluginSrc, "Tools", "CivilDelivery",
                "SectionsTools.cs"));

            var directStart = apply.IndexOf("public SectionApplyResult Apply(", StringComparison.Ordinal);
            var directEnd = apply.IndexOf("private void ApplyOne(", directStart, StringComparison.Ordinal);
            var direct = apply.Substring(directStart, directEnd - directStart);
            direct.Should().Contain("batchFailed = !ApplyCore(")
                .And.Contain("tr.Abort()")
                .And.Contain("result.Committed = false")
                .And.Contain("if (batchFailed)");

            var coreStart = apply.IndexOf("public bool ApplyCore(", StringComparison.Ordinal);
            var coreEnd = apply.IndexOf("private static void BlockForStaleScope", coreStart,
                StringComparison.Ordinal);
            var core = apply.Substring(coreStart, coreEnd - coreStart);
            core.Should().Contain("ApplyOne(")
                .And.Contain("catch (Exception ex)")
                .And.Contain("return false; // atomic batch")
                .And.NotContain("tr.Commit()");

            var toolStart = tools.IndexOf("public class ApplySectionsTool", StringComparison.Ordinal);
            var tool = tools[toolStart..];
            tool.Should().Contain("bool coreOk = applyService.ApplyCore(")
                .And.Contain("if (!coreOk)")
                .And.Contain("ToolResult.Fail(")
                .And.Contain("transaction must be aborted");
        }

        [Fact]
        public void ManagedSectionViewStyle_IsRequiredAndReadBackBeforeCommit()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionApplyService.cs"));
            var helper = source[source.IndexOf("private static void TryApplyStyle", StringComparison.Ordinal)..];
            helper = helper[..helper.IndexOf("private static ObjectId? FindAlignment", StringComparison.Ordinal)];

            helper.Should().Contain("Managed section has no planned SectionView style")
                .And.Contain("document-default SectionView style cannot be applied")
                .And.Contain("tr.GetObject(view.StyleId, OpenMode.ForRead)")
                .And.Contain("SectionView style read-back differs")
                .And.Contain("throw new InvalidOperationException");
        }

        [Fact]
        public void ManagedSurfaceStyleAndElevationRangeFailures_AreAtomicErrors()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var verify = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionVerifyService.cs"));
            var surfaceStyles = source[source.IndexOf("private static void ApplySurfaceStyles", StringComparison.Ordinal)..];
            surfaceStyles = surfaceStyles[..surfaceStyles.IndexOf("internal static void TryAttachBandSet", StringComparison.Ordinal)];
            var range = source[source.IndexOf("internal static void ClampElevationRange", StringComparison.Ordinal)..];
            range = range[..range.IndexOf("// -------------------------------------------------------------- helpers", StringComparison.Ordinal)];

            surfaceStyles.Should().Contain("section.StyleName = expected")
                .And.Contain("style read-back differs")
                .And.Contain("Severity = FindingSeverity.Error")
                .And.Contain("throw new InvalidOperationException");
            range.Should().Contain("No sampled existing-ground surface supplied a finite elevation range")
                .And.Contain("view.IsElevationRangeAutomatic")
                // SEC-B3 (review of 1.3.9): the band is the drawn content, not a padded
                // tower: no fixed ±5 m and no stacked bottom/top margins.
                .And.Contain("SectionViewElevationBandLogic.TryCompute(inputs, MaxViewSpanM")
                .And.Contain("SectionViewElevationBandLogic.ArrowHeadroomFor(")
                .And.Contain("record.TrafficDirections.Select(direction => direction.StripKind)")
                .And.Contain("projectedUtilityElevations")
                .And.Contain("rec.ElevationBandEvidence = band.Evidence")
                .And.NotContain("egMin - 5")
                .And.NotContain("egMax + 5")
                .And.NotContain("ManagedViewTopMarginM")
                .And.NotContain("ManagedViewBottomMarginM")
                .And.Contain("elevation range read-back differs")
                .And.Contain("Severity = FindingSeverity.Error")
                .And.Contain("throw new InvalidOperationException");
            verify.Should().Contain("managed_elevation_range_pinned")
                .And.Contain("TryReadElevationRange")
                .And.Contain("!automaticRange")
                .And.Contain("MaxManagedViewEnvelopePaddingM")
                .And.Contain("elevationSpan <= maxPinnedSpan + 0.01")
                .And.Contain("managed_elevation_range_content_bound")
                .And.Contain("SectionViewElevationBandLogic.TryParseAndRecompute(")
                .And.Contain("applyRecord.ElevationBandEvidence");
        }

        [Fact]
        public void ApplyReadsProjectionSourcesOnceBeforeViewsSoTheBandContainsUtilities()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var apply = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionApplyService.cs"));

            var collect = apply.IndexOf("SectionGeometryCollector.Collect(tr, db, profile, _log)", StringComparison.Ordinal);
            var loop = apply.IndexOf("foreach (var record in targets)", collect, StringComparison.Ordinal);
            var decorate = apply.IndexOf("SectionDecorationService.Decorate(", StringComparison.Ordinal);
            collect.Should().BeGreaterThan(0);
            loop.Should().BeGreaterThan(collect, "the band needs the utilities before any view is created");
            decorate.Should().BeGreaterThan(loop);
            apply.Split("SectionGeometryCollector.Collect(").Length.Should().Be(2,
                "one read of the projection sources serves the band and the decorator");
            apply.Should().Contain(".CrossingsFor(collected.Utilities, SectionCutGeometry.RequireFrame(record))")
                .And.Contain("record, projectedUtilityElevations);")
                .And.Contain("collected: collected);");
        }

        [Fact]
        public void ApplyCoverage_UsesTheSourceCutExtents_NotTheDisplayGridPadding()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var coverage = source[source.IndexOf(
                "var presentation = AnalyzePresentationCoverage(", StringComparison.Ordinal)..];
            coverage = coverage[..coverage.IndexOf("var actualCoverage", StringComparison.Ordinal)];

            coverage.Should().Contain("requiredLeftOffset: frame.MinOffset")
                .And.Contain("requiredRightOffset: frame.MaxOffset")
                .And.NotContain("requiredLeftOffset: offMin")
                .And.NotContain("requiredRightOffset: offMax");
            source.Should().Contain("frame.IsContainedInDisplay(offMin, offMax)");
        }

        [Fact]
        public void ConfiguredBandSet_IsImportedReadBackExactlyAndFailsAtomically()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var helper = source[source.IndexOf("internal static void TryAttachBandSet", StringComparison.Ordinal)..];
            helper = helper[..helper.IndexOf("internal static void ClearBandSet", StringComparison.Ordinal)];

            helper.Should().Contain("bandSets.Contains(wanted)")
                .And.Contain("CivilDocument civilDoc")
                .And.Contain("civilDoc.Styles.SectionViewBandSetStyles")
                .And.Contain("if (!view.IsWriteEnabled)")
                .And.Contain("no fallback is permitted")
                .And.Contain("configured.GetBottomBandSetItems()")
                .And.Contain("configured.GetTopBandSetItems()")
                .And.Contain("view.Bands.ImportBandSetStyle(id)")
                .And.Contain("view.Bands.GetBottomBandItems()")
                .And.Contain("view.Bands.GetTopBandItems()")
                .And.Contain("expectedBottom.SequenceEqual(actualBottom")
                .And.Contain("expectedTop.SequenceEqual(actualTop")
                .And.Contain("Severity = FindingSeverity.Error")
                .And.Contain("APPLY must roll back")
                .And.NotContain("Severity = FindingSeverity.Warning")
                .And.NotContain("CivilApplication.ActiveDocument",
                    "a modeless document switch must not supply style ids from another database");
        }

        [Fact]
        public void ReservedAnnotationLayerAndArialTextStyle_AreNormalizedAndReadBack()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var layer = source[source.IndexOf("private static void EnsureLayer", StringComparison.Ordinal)..];
            layer = layer[..layer.IndexOf("private static void EnsureTextStyle", StringComparison.Ordinal)];
            var textStyle = source[source.IndexOf("private static void EnsureTextStyle", StringComparison.Ordinal)..];

            layer.Should().Contain("rec.IsDependent")
                .And.Contain("rec.IsOff = false")
                .And.Contain("rec.IsFrozen = false")
                .And.Contain("rec.IsPlottable = true")
                .And.Contain("rec.IsLocked = false")
                .And.Contain("failed on/unfrozen/plottable/unlocked read-back");
            textStyle.Should().Contain("AnnoTypeface, false, false")
                .And.Contain("rec.BigFontFileName = string.Empty")
                .And.Contain("font.TypeFace")
                .And.Contain("rec.IsShapeFile")
                .And.Contain("failed Arial TrueType read-back")
                .And.NotContain("catch");
        }

        [Fact]
        public void ManagedSurfaceStyles_AreOwnedSourcePinnedAndDesignIsSolid()
        {
            var pluginSrc = typeof(SectionViewPresentationStyleSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services",
                "SectionDecorationService.cs"));
            var ensure = source[source.IndexOf("private static bool EnsureSectionStyles", StringComparison.Ordinal)..];
            ensure = ensure[..ensure.IndexOf("private static void ApplySurfaceStyles", StringComparison.Ordinal)];
            var apply = source[source.IndexOf("private static void ApplySurfaceStyles", StringComparison.Ordinal)..];
            apply = apply[..apply.IndexOf("internal static void TryAttachBandSet", StringComparison.Ordinal)];

            ensure.Should().Contain("SectionSurfaceStyleContractLogic.IsToolOwnedDescription(")
                .And.Contain("spec, style.Description")
                .And.Contain("style.Description = spec.Description")
                .And.Contain("Section style name collision")
                .And.Contain("display.Linetype = spec.Linetype")
                .And.Contain("SectionSurfaceStyleContractLogic.TryValidateLive");
            apply.Should().Contain("record.PlannedSources")
                .And.Contain("plannedSurfaces.Count != 2")
                .And.Contain("expectedBySource")
                .And.Contain("section.SourceName")
                .And.Contain("pair.Value != 1");
        }
    }
}
