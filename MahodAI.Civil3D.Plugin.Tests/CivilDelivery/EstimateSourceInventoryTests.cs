using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class EstimateSourceInventoryTests
    {
        private const string Host = @"C:\Projects\6422-CIVIL-WEST.dwg";

        [Fact]
        public void CompleteEmptyInventory_ExplicitlyStatesHostOnlyWithoutClaimingMeasurement()
        {
            var snapshot = new EstimateSourceInventorySnapshot(
                Host, Array.Empty<EstimateXrefInventoryEntry>());

            snapshot.IsComplete.Should().BeTrue();
            snapshot.Text.Should().Contain(Host)
                .And.Contain("המארח בלבד ברשימה זו")
                .And.Contain("אינה סריקה רקורסיבית")
                .And.Contain("אינה הוכחה שכל המקורות נמדדו או אומתו");
        }

        [Fact]
        public void EmptyFailedRead_NeverMasqueradesAsAHostOnlyDrawing()
        {
            var snapshot = new EstimateSourceInventorySnapshot(
                Host, Array.Empty<EstimateXrefInventoryEntry>(), "eNotOpenForRead");

            snapshot.IsComplete.Should().BeFalse();
            snapshot.Text.Should().Contain("eNotOpenForRead")
                .And.Contain("אינה מלאה עקב כשל קריאה")
                .And.Contain("זה אינו אישור שהשרטוט הוא מארח בלבד")
                .And.NotContain("המארח בלבד ברשימה זו");
        }

        [Fact]
        public void MixedStates_KeepExactPathsAndReportedStatus_WithSeparateCounts()
        {
            var snapshot = new EstimateSourceInventorySnapshot(Host, new[]
            {
                new EstimateXrefInventoryEntry("Road", @".\Road.dwg", "Resolved", false, true),
                new EstimateXrefInventoryEntry("Survey", @"Z:\Survey.dwg", "Unloaded", true, false),
                new EstimateXrefInventoryEntry("Water", @"..\Water.dwg", "FileNotFound", false, false),
            });

            snapshot.IsComplete.Should().BeTrue("missing sources are inventory, not an inventory read failure");
            snapshot.Text.Should().Contain("הגדרות XREF שנקראו: 3")
                .And.Contain("טעונות/פתורות: 1; לא טעונות: 1; לא פתורות: 1");
            foreach (var reference in snapshot.References)
                snapshot.Text.Should().Contain(reference.Name)
                    .And.Contain(reference.Path).And.Contain(reference.Status);
            snapshot.Text.Should().Contain("נתיב מוגדר:")
                .And.Contain("לא טעון: כן; פתור: לא")
                .And.NotContain(@"C:\Projects\Road.dwg", "configured relative paths must not be presented as resolved disk identities");
        }

        [Fact]
        public void MoreThanTwelveReferences_AreAllVisibleWithoutAnApprovalGateOrTruncation()
        {
            var references = Enumerable.Range(1, 24)
                .Select(index => new EstimateXrefInventoryEntry(
                    $"Source-{index:D2}", $@"..\Refs\Source-{index:D2}.dwg", "Resolved", false, true))
                .ToArray();
            var snapshot = new EstimateSourceInventorySnapshot(Host, references);

            snapshot.IsComplete.Should().BeTrue();
            snapshot.Text.Should().Contain("הגדרות XREF שנקראו: 24");
            foreach (var reference in references)
                snapshot.Text.Should().Contain(reference.Name).And.Contain(reference.Path);
            snapshot.Text.Should().Contain("24. ").And.Contain("Source-24.dwg");
        }

        [Fact]
        public void PartialRead_PreservesKnownReferencesAndFailureDetails()
        {
            var snapshot = new EstimateSourceInventorySnapshot(Host, new[]
            {
                new EstimateXrefInventoryEntry("Known", "Known.dwg", "Resolved", false, true),
            }, "Definition 2 could not be read");

            snapshot.IsComplete.Should().BeFalse();
            snapshot.Text.Should().Contain("Known.dwg")
                .And.Contain("Definition 2 could not be read")
                .And.Contain("אין להסיק שאין הפניות נוספות");
        }

        [Fact]
        public void Capture_IsReadOnlyBlockTableInventory_NotLoadingOrMeasuringSources()
        {
            var sourceDirectory = typeof(EstimateSourceInventoryTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            var source = File.ReadAllText(Path.Combine(sourceDirectory, "CivilDelivery", "Estimate",
                "EstimateSourceInventoryService.cs"));

            source.Should().Contain("document.LockDocument()")
                .And.Contain("database.BlockTableId, OpenMode.ForRead")
                .And.Contain("definition.IsFromExternalReference")
                .And.Contain("definition.IsFromOverlayReference")
                .And.Contain("definition.PathName")
                .And.Contain("definition.XrefStatus.ToString()")
                .And.Contain("definition.IsUnloaded")
                .And.Contain("transaction.Abort()")
                .And.NotContain("OpenMode.ForWrite")
                .And.NotContain(".Commit()")
                .And.NotContain("ReadDwgFile(")
                .And.NotContain("ResolveXrefs(")
                .And.NotContain("ReloadXrefs(")
                .And.NotContain("AttachXref(")
                .And.NotContain("BindXrefs(")
                .And.NotContain("GetXrefDatabase(")
                .And.NotContain("ExtractDiscovery(")
                .And.NotContain("File.Write");
        }
    }
}
