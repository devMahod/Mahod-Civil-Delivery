using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Host-free wiring proof for SEC-11.  The numerical plot rules have pure Core
    /// tests; these assertions ensure the Civil and WPF routes cannot regress to the
    /// old misleading "preview" that merely zoomed to CL lines.
    /// </summary>
    public class SectionPreviewSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionPreviewSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) =>
            File.ReadAllText(parts.Aggregate(PluginSourceDir, Path.Combine));

        [Fact]
        public void Preview_SamplesExactPlannedSurfacesAndProjectedEntities()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");

            service.Should().Contain("planned.SourceHandle")
                .And.Contain("OpenMode.ForRead")
                .And.Contain("surface.FindElevationAtXY(point.X, point.Y)")
                .And.Contain("record.ProjectedEntities")
                .And.Contain("record.PlannedLayoutPosition")
                .And.Contain("TryCreateElevationWindow")
                .And.Contain("eg.TryElevationAtOffset(0.0, out var existingGroundAtAxis)")
                // SEC-M2 (review of 1.3.9): same wording as the managed section.
                .And.Contain("SectionDrawingTextLogic.DatumText(existingGroundAtAxis)")
                .And.NotContain("רום קיים {existingGroundAtAxis:F2}")
                .And.NotContain("DATUM {window.Datum}",
                    "the one stated elevation is measured EG at the axis, never the plot-window floor")
                .And.Contain("לא הוצגה סכימה מומצאת");
        }

        [Fact]
        public void Preview_CreatesOnlyTransientDrawables_NeverDwgObjects()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");

            service.Should().Contain("TransientManager.CurrentTransientManager")
                .And.Contain("tm.AddTransient(")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain("AppendEntity")
                .And.NotContain("AddNewlyCreatedDBObject")
                .And.NotContain("SampleLine.Create")
                .And.NotContain("SectionView.Create")
                .And.NotContain(".Commit()",
                    "the preview service is incapable of persisting a database transaction");
        }

        [Fact]
        public void ModelessPreview_LocksDocumentAndAbortsReadTransaction()
        {
            var workflow = Read("CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs");
            var start = workflow.IndexOf("public SectionPreviewDisplay Preview(", StringComparison.Ordinal);
            var end = workflow.IndexOf("public void ClearPreview", start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1);
            end.Should().BeGreaterThan(start);
            var preview = workflow.Substring(start, end - start);

            preview.Should().Contain("using (doc.LockDocument())")
                .And.Contain("StartTransaction()")
                .And.Contain("tr.Abort()")
                .And.NotContain("tr.Commit()");
        }

        [Fact]
        public void Palette_PreviewsSelectedRowAndZoomsToReturnedSectionExtents()
        {
            var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
            var start = ui.IndexOf("private void OnPreview", StringComparison.Ordinal);
            var end = ui.IndexOf("private void OnClearPreview", start, StringComparison.Ordinal);
            var preview = ui.Substring(start, end - start);

            preview.Should().Contain("selected.Record.RecordId")
                .And.Contain("ZoomPreviewTightly(doc, preview.Extents)")
                .And.NotContain("ZoomToClLines",
                    "the preview target is the plotted section, not its source line in plan");

            ui.Should().Contain("NativeViewZoomService.TryZoom(doc, ext, margin: 1.10)")
                .And.Contain("if (!ZoomPreviewTightly(doc, preview.Extents))",
                    "preview must use the current tiled viewport's WCS/DCS contract and report navigation failure");
            var zoom = Read("CivilDelivery", "Estimate", "NativeViewZoomService.cs");
            zoom.Should().Contain("view.Width / view.Height")
                .And.Contain("ed.UpdateScreen()")
                .And.NotContain("GetSystemVariable(\"SCREENSIZE\")",
                    "whole drawing pixel aspect is not the active viewport aspect in a split model view");

            var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
            xaml.Should().Contain("Content=\"בדיקת גאומטריה\"")
                .And.Contain("מהמשטחים האמיתיים")
                .And.Contain("אינה אישור")
                .And.Contain("אינה תצוגת התוצר הסופי");
        }

        [Fact]
        public void AiPreview_DescribesARealSection_NotClMarkers()
        {
            var tools = Read("Tools", "CivilDelivery", "SectionsTools.cs");
            var start = tools.IndexOf("public class PreviewSectionsTool", StringComparison.Ordinal);
            var end = tools.IndexOf("public class ApplySectionsTool", start, StringComparison.Ordinal);
            var preview = tools.Substring(start, end - start);

            preview.Should().Contain("existing/design surfaces")
                .And.Contain("projected utilities")
                .And.Contain("record_id")
                .And.NotContain("CL lines, crossing markers");
        }

        [Fact]
        public void PreviewabilityGate_ExcludesManualReuse_InServiceAndPalette()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");
            var policy = Read("CivilDelivery", "Sections", "Services",
                "SectionDiagnosticPreviewPolicy.cs");
            var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");

            service.Should().Contain(".Where(record => CanPreview(plan, record))",
                    "an implicit AI preview must apply the same plan-aware closed policy");
            policy.Should().Contain("record.ManualSectionReuse != null")
                .And.Contain("יש להשתמש ב'הצג חתך'",
                    "an explicitly selected reused view must receive an actionable explanation");
            ui.Should().Contain("SectionPreviewService.CanPreview(_plan, previewRow.Record)",
                    "the palette must not enable Preview for a reused manual SectionView")
                .And.Contain("SectionPreviewService.PreviewBlockReason(_plan, selected.Record)",
                    "the click guard must enforce the same predicate as the button state");
        }

        [Fact]
        public void DiagnosticReviewPreview_UsesClosedPolicyLayout_WithoutRelaxingApply()
        {
            var policy = Read("CivilDelivery", "Sections", "Services",
                "SectionDiagnosticPreviewPolicy.cs");
            var plan = Read("CivilDelivery", "Sections", "Services", "SectionPlanService.cs");
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");

            policy.Should().Contain("AllowedReviewFindingCodes")
                .And.Contain("SectionFindingCodes.PresentationCoverageMissing")
                .And.Contain("SectionFindingCodes.TrafficDirectionUnresolved")
                .And.Contain("!AllowedReviewFindingCodes.Contains(finding.Code)")
                .And.Contain("plan.Status is DeliveryStatus.Blocked or DeliveryStatus.Failed")
                .And.Contain("record.Status == DeliveryStatus.ReviewRequired");
            plan.Should().Contain(".Where(SectionDiagnosticPreviewPolicy.CanAssignLayout)");
            apply.Should().Contain("SectionPlanLogic.UnresolvedBatchRecords(plan)")
                .And.Contain(".Where(r => r.Status == DeliveryStatus.Ready)",
                    "diagnostic preview eligibility must never become APPLY eligibility");
        }

        [Fact]
        public void FailedReplacementPreview_RefreshesUiFromActualTransientState()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");
            var workflow = Read("CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs");
            var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
            var start = ui.IndexOf("private void OnPreview", StringComparison.Ordinal);
            var end = ui.IndexOf("private void OnClearPreview", start, StringComparison.Ordinal);
            var preview = ui.Substring(start, end - start);

            service.Should().Contain("public bool HasActivePreview => _transients.Count > 0;");
            workflow.Should().Contain("public bool HasActivePreview => _previewService.HasActivePreview;");
            preview.Should().Contain("_previewShown = _sections.HasActivePreview;")
                .And.NotContain("_previewShown = true;",
                    "a previous success flag becomes stale when ShowPreview clears it and the replacement fails");
        }

        [Fact]
        public void Preview_IsComputedInsideReadTransaction_ButDisplayedOnlyAfterDispose()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");
            var workflow = Read("CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs");
            var tools = Read("Tools", "CivilDelivery", "SectionsTools.cs");

            var prepareStart = service.IndexOf(
                "public PreparedSectionPreview PreparePreview(", StringComparison.Ordinal);
            var publishStart = service.IndexOf(
                "public SectionPreviewDisplay PublishPreview(", prepareStart, StringComparison.Ordinal);
            var prepare = service.Substring(prepareStart, publishStart - prepareStart);
            prepare.Should().Contain("SampleExactSurface(")
                .And.NotContain("AddTransient(")
                .And.NotContain("ClearPreview(",
                    "sampling an uncommitted Civil read must have no visible side effect");

            var workflowStart = workflow.IndexOf(
                "public SectionPreviewDisplay Preview(", StringComparison.Ordinal);
            var workflowEnd = workflow.IndexOf("public void ClearPreview", workflowStart,
                StringComparison.Ordinal);
            var direct = workflow.Substring(workflowStart, workflowEnd - workflowStart);
            direct.IndexOf("_previewService.PreparePreview", StringComparison.Ordinal)
                .Should().BeLessThan(direct.IndexOf("tr.Abort();", StringComparison.Ordinal));
            direct.IndexOf("_previewService.PublishPreview", StringComparison.Ordinal)
                .Should().BeGreaterThan(direct.IndexOf(
                    "// This is intentionally outside the transaction using-scope", StringComparison.Ordinal));

            var aiStart = tools.IndexOf("public class PreviewSectionsTool", StringComparison.Ordinal);
            var callback = tools.IndexOf("public void OnReadOnlyTransactionClosed(", aiStart,
                StringComparison.Ordinal);
            var aiEnd = tools.IndexOf("public class ApplySectionsTool", callback,
                StringComparison.Ordinal);
            var execute = tools.Substring(aiStart, callback - aiStart);
            var publication = tools.Substring(callback, aiEnd - callback);
            execute.Should().Contain("Preview.PreparePreview(")
                .And.NotContain("Preview.PublishPreview(")
                .And.NotContain("Preview.ClearPreview();");
            publication.Should().Contain("Preview.PublishPreview(prepared)")
                .And.Contain("Preview.ClearPreview();");
            tools.Should().Contain(
                "PreviewSectionsTool : DrawingToolBase, IReadOnlyTransactionClosedObserver");
        }

        [Fact]
        public void PreviewReplacementAndClear_RetainFailedTransientHandlesFailClosed()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");

            service.Should().Contain("The old preview remains visible until")
                .And.Contain("foreach (var drawable in next)")
                .And.Contain("var previous = _transients.ToList();")
                .And.Contain("EraseWithoutLosingHandles(")
                .And.Contain("_transients.AddRange(previous);")
                .And.Contain("SectionPreviewCleanup.Clear(")
                .And.Contain("EnterOwnerCleanupContext")
                .And.Contain("GetDocument(_ownerDatabase)")
                .And.Contain("SectionPreviewCleanup.RequireActiveOwner(");
        }

        [Fact]
        public void Preview_HebrewUsesTransientTrueTypeMText_NotDefaultShxDbText()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");

            service.Should().Contain("using AcadMText = Autodesk.AutoCAD.DatabaseServices.MText")
                .And.Contain("new AcadMText")
                .And.Contain(@"\fArial|b0|i0|c0|p34;")
                .And.NotContain("using AcadDbText",
                    "a transient DBText inherits the office SHX default and renders Hebrew as question marks");
            service.Should().NotContain("new TextStyleTableRecord",
                "preview remains read-only and embeds its font instead of creating a drawing style");
        }

        [Fact]
        public void Preview_StatesInsideEveryRouteThatItIsNotTheFinalAnnotatedDeliverable()
        {
            var service = Read("CivilDelivery", "Sections", "Services", "SectionPreviewService.cs");
            var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
            var xaml = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml");
            var command = Read("CivilDelivery", "Commands", "MhdSectionsCommand.cs");
            var tools = Read("Tools", "CivilDelivery", "SectionsTools.cs");

            service.Should().Contain("GeometryOnlyNotice")
                .And.Contain("אינה תצוגת התוצר הסופי")
                .And.Contain("רצועות, שיפועים ובלוקים משרדיים")
                .And.Contain("FinalAppearanceRendered => false")
                .And.Contain("frameTop + 3.7",
                    "the limitation must be visible in the transient itself, including screenshots");
            ui.Should().Contain("preview.ScopeNotice")
                .And.Contain("לא תוצר סופי");
            xaml.Should().Contain("אינה תצוגת התוצר הסופי");
            command.Should().Contain("preview.ScopeNotice");
            tools.Should().Contain("final_appearance_rendered")
                .And.Contain("preview.ScopeNotice")
                .And.Contain("geometry-only transient section check");
        }
    }
}
