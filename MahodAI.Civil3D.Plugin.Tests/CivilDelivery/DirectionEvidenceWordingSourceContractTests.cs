using System.IO;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Live 30.09.2026: the directions dialog showed "resolved · automatic-traffic-scope=cut-not-…" to the
    /// engineer. The dialog and the section detail word the evidence in Hebrew; the codes stay reachable
    /// for support in the evidence tooltip.
    /// </summary>
    public class DirectionEvidenceWordingSourceContractTests
    {
        private static string PluginFile(params string[] path) => Path.Combine(
            EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin", Path.Combine(path));

        [Fact]
        public void DirectionsDialog_ShowsHebrewEvidence_WithCodesInTooltip()
        {
            var code = File.ReadAllText(PluginFile("CivilDelivery", "UI", "TrafficDirectionDecisionDialog.xaml.cs"));
            code.Should().Contain("public string Evidence => TrafficDirectionReasonText.Describe(Direction.State, Direction.Reason);")
                .And.Contain("public string EvidenceCode => $\"{Direction.State} · {Direction.Reason}\";");
            var xaml = File.ReadAllText(PluginFile("CivilDelivery", "UI", "TrafficDirectionDecisionDialog.xaml"));
            xaml.Should().Contain("<Setter Property=\"ToolTip\" Value=\"{Binding EvidenceCode}\"/>");
        }

        /// <summary>
        /// Audit Z11 (30.09.2026): «החל כיוון על השורות המסומנות» acts on the highlighted DataGrid rows, while the guide and
        /// the word "מסומנות" pointed at the «אישור» ticks — ticking three rows and applying changed only the highlighted one.
        /// The batch controls now say "מודגשות" and explain highlighting; the behaviour itself is unchanged.
        /// </summary>
        [Fact]
        public void BatchApply_NamesTheHighlightedRows_NotTheApprovalTicks()
        {
            var xaml = File.ReadAllText(PluginFile("CivilDelivery", "UI", "TrafficDirectionDecisionDialog.xaml"));
            xaml.Should().Contain("Content=\"החל כיוון על השורות המודגשות\"")
                .And.Contain("Text=\"כיוון לשורות המודגשות:\"")
                .And.Contain("Ctrl או Shift")
                .And.Contain("תיבת «אישור» קובעת רק אילו שורות יישמרו")
                .And.NotContain("השורות המסומנות")
                .And.NotContain("לאצווה מסומנת");
            var code = File.ReadAllText(PluginFile("CivilDelivery", "UI", "TrafficDirectionDecisionDialog.xaml.cs"));
            code.Should().Contain("var selected = DirectionsGrid.SelectedItems.Cast<DirectionRow>().ToList();")
                .And.Contain("יש להדגיש לפחות שורה אחת בטבלה")
                .And.Contain("יש לבחור כיוון לפני החלה על השורות המודגשות")
                .And.NotContain("יש לסמן לפחות שורה אחת");
        }

        [Fact]
        public void SectionDetail_WordsDirections_InHebrew()
        {
            var control = File.ReadAllText(PluginFile("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            control.Should().Contain("TrafficDirectionReasonText.Flow(direction.Flow)")
                .And.Contain("TrafficDirectionReasonText.Describe(direction.State, direction.Reason)")
                .And.NotContain("$\"{direction.State} — {direction.Reason}\"");
        }
    }
}
