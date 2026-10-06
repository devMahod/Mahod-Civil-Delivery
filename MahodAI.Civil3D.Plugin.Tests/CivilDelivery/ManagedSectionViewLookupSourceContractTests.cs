using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public sealed class ManagedSectionViewLookupSourceContractTests
    {
        [Fact]
        public void ExplicitShowResolvesUnchangedViews_WhileRecurringGuidanceNeverScansNativeInventory()
        {
            var root = typeof(ManagedSectionViewLookupSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            var ui = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            var start = ui.IndexOf("private void OnShow(", System.StringComparison.Ordinal);
            var end = ui.IndexOf("private bool ZoomToClLines(", start, System.StringComparison.Ordinal);
            var show = ui[start..end];
            show.Should().Contain("row.Record.Action == PlanAction.Unchanged")
                .And.Contain("ManagedSectionViewLookupService.Resolve(doc, _plan, row.Record)")
                .And.Contain("if (!lookup.Found)")
                .And.Contain("SetStatus(lookup.Reason)")
                .And.Contain("handle = lookup.SectionViewHandle;");
            var failed = show.IndexOf("if (!lookup.Found)", System.StringComparison.Ordinal);
            var resolved = show.IndexOf("handle = lookup.SectionViewHandle;", System.StringComparison.Ordinal);
            show[failed..resolved].Should().Contain("return;")
                .And.NotContain("ZoomToClLines(");
            var guidance = File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", "CivilDeliveryControl.SectionGuidance.cs"));
            guidance.Should().Contain("ManagedViewLookupAvailable = canLocate")
                .And.Contain("אתר חתך קיים")
                .And.NotContain("ManagedSectionViewLookupService.Resolve(");
        }

        [Fact]
        public void LookupIsBoundedToCurrentNativeDrawingAndNeverMutatesOrUsesAFirstMatch()
        {
            var root = typeof(ManagedSectionViewLookupSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services",
                "ManagedSectionViewLookupService.cs"));
            source.Should().Contain("record.Action != PlanAction.Unchanged")
                .And.Contain("MdiActiveDocument")
                .And.Contain("DrawingScopeIdentity.For(doc)")
                .And.Contain("plan.SourceDatabaseRevision")
                .And.Contain("DrawingRevisionTracker.Capture(doc.Database)")
                .And.Contain("MaxNativeObjects")
                .And.Contain("!space.IsLayout || space.IsFromExternalReference || space.IsFromOverlayReference")
                .And.Contain("SectionOwnershipService.Read(tr, obj)")
                .And.Contain("view.SampleLineId")
                .And.Contain("id.ObjectClass.IsDerivedFrom(viewClass)")
                .And.Contain("id.ObjectClass.IsDerivedFrom(lineClass)")
                .And.Contain("tr.Abort()")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain("tr.Commit()")
                .And.NotContain("FirstOrDefault")
                .And.NotContain("ReadDwgFile")
                .And.NotContain("AttachXref");
            source.IndexOf("id.ObjectClass.IsDerivedFrom(viewClass)", System.StringComparison.Ordinal)
                .Should().BeLessThan(source.IndexOf("var obj = tr.GetObject(id, OpenMode.ForRead)",
                    System.StringComparison.Ordinal));
            source.Should().Contain("RXObject.GetClass(typeof(CivilDb.SampleLine))");
        }
    }
}
