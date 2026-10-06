using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Findings of the first live APPLY rounds (06–07/09): a rolled-back apply must not
    /// leave "Applied" records or a dead section-view handle behind (the UI then offered
    /// "הצג חתך קיים" and answered "החתך לא נמצא"); centered labels need AdjustAlignment
    /// once database-resident; every UI-level failure is recorded with its stack; the
    /// apply confirmation speaks plain Hebrew.
    /// </summary>
    public class LiveApplyRollbackContractTests
    {
        private static string PluginSourceDir =>
            typeof(LiveApplyRollbackContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(
            new[] { PluginSourceDir, "CivilDelivery" }.Concat(parts).ToArray()));

        [Fact]
        public void RolledBackApply_MarksEveryRecordFailed_AndTheUiOffersNoGhostView()
        {
            var apply = Read("Sections", "Services", "SectionApplyService.cs");
            Regex.Matches(apply, Regex.Escape("foreach (var record in result.Records) record.Status = DeliveryStatus.Failed;"))
                .Count.Should().Be(2, "both the batch and the selected rollback paths");
            var guidance = Read("UI", "CivilDeliveryControl.SectionGuidance.cs");
            guidance.Should().Contain("_apply is { Committed: true } apply")
                .And.Contain("?? row.Record.ManualSectionReuse?.SectionViewHandle;")
                .And.NotContain("_apply?.Records.FirstOrDefault(result => result.RecordId == row.Record.RecordId)?.Handles.SectionView\n");
        }

        [Fact]
        public void CenteredLabels_AreAlignedOnceDatabaseResident()
        {
            var decoration = Read("Sections", "Services", "SectionDecorationService.cs");
            var append = decoration.IndexOf("btr.AppendEntity(ent);", StringComparison.Ordinal);
            var adjust = decoration.IndexOf("justified.AdjustAlignment(db);", StringComparison.Ordinal);
            append.Should().BeGreaterThan(0);
            adjust.Should().BeGreaterThan(append, "alignment is recomputed after the text joins the database");
            decoration.Should().Contain("justified.HorizontalMode != TextHorizontalMode.TextLeft ||");
        }

        [Fact]
        public void UiFailures_AreRecordedWithTheirStack_AndTheApplyConfirmationIsPlain()
        {
            var guard = Read("UI", "UiGuard.cs");
            guard.Should().Contain("public static void Record(string context, Exception ex)")
                .And.Contain("Record(context, ex);");
            var control = Read("UI", "CivilDeliveryControl.xaml.cs");
            control.Should().Contain("UiGuard.Record(title, ex);")
                .And.Contain("ליצור עכשיו את חתך {section}?")
                .And.NotContain("טרנזקציונית");
        }
    }
}
