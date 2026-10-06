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
    /// A WPF handler exception inside AutoCAD is fatal for the host (live on 6422,
    /// 2026-09-03: the strip-label dialog refreshed its DataGrid during an edit
    /// transaction and Civil aborted). These contracts pin the hardening: rows notify
    /// their own changes, the grid owns no edit transaction, every handler is guarded,
    /// every dialog holds a dispatcher guard, and long work shows an animated progress
    /// window on its own thread (1.2.33).
    /// </summary>
    public class UiHardeningContractTests
    {
        private static string PluginSourceDir =>
            typeof(UiHardeningContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Ui(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "UI", name));

        [Fact]
        public void SpanLabelDialog_NeverRefreshesTheGrid_AndBindsRowsThatNotify()
        {
            var cs = Ui("SectionSpanLabelDecisionDialog.xaml.cs");
            cs.Should().NotContain("Items.Refresh(")
                .And.Contain("UiGuard.Attach(this, \"שמות רצועות\");");
            Regex.IsMatch(cs, @"_model\s*=\s*new SpanLabelDecisionModel\(\s*records,\s*initiallyApproveStrongSuggestions,\s*previousDecisions,\s*includeResolvedSpans\s*\);")
                .Should().BeTrue("the dialog must pass the displayed current targets and review-only history to its model");
            // All state lives in the XAML-free model (unit-tested); choosing a name is the approval gesture.
            var model = Ui("SpanLabelDecisionModel.cs");
            model.Should().Contain("public sealed class SpanRow : INotifyPropertyChanged")
                .And.Contain("if (!string.IsNullOrWhiteSpace(next) && !_isApproved)")
                .And.NotContain("System.Windows");
            // Every click handler is guarded.
            foreach (var handler in new[] { "OnApplyLabelToSelected", "OnMarkStrongSuggestions", "OnClearApprovals", "OnReassignPreviousName", "OnSave", "OnCancel", "OnApproverChanged" })
            {
                var at = cs.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
                at.Should().BeGreaterThan(0, handler);
                cs.Substring(at, Math.Min(260, cs.Length - at)).Should().Contain("UiGuard.Run(", handler + " must be guarded");
            }

            var xaml = Ui("SectionSpanLabelDecisionDialog.xaml");
            xaml.Should().Contain("IsReadOnly=\"True\"")
                .And.NotContain("DataGridCheckBoxColumn")
                .And.Contain("<CheckBox IsChecked=\"{Binding IsApproved, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"")
                .And.Contain("Text=\"{Binding Label, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"")
                .And.Contain("ColumnHeaderStyle=\"{StaticResource HeaderStyle}\"")
                .And.Contain("TargetType=\"ComboBoxItem\"")
                .And.Contain("<Trigger Property=\"IsEnabled\" Value=\"False\">")
                .And.Contain("שמור שמות מסומנים")
                .And.Contain("Binding Suggestion")
                .And.Contain("Binding Evidence")
                .And.Contain("x:Name=\"ApproverBox\"");
        }

        [Fact]
        public void EveryDialog_HoldsADispatcherGuard()
        {
            var dir = Path.Combine(PluginSourceDir, "CivilDelivery", "UI");
            var dialogs = Directory.GetFiles(dir, "*Dialog.xaml.cs");
            dialogs.Should().NotBeEmpty();
            foreach (var path in dialogs)
                File.ReadAllText(path).Should().Contain("UiGuard.Attach(this, ", Path.GetFileName(path));

            var guard = Ui("UiGuard.cs");
            guard.Should().Contain("e.Handled = true;")
                .And.Contain("if (e.Handled || !ShouldHandleDispatcherException(e.Exception)) return;")
                .And.Contain("dispatcher.UnhandledException += handler;")
                .And.Contain("window.Loaded += (_, _) => Register();")
                .And.Contain("window.Unloaded += (_, _) => Unregister();")
                .And.Contain("window.Closed += (_, _) => Unregister();")
                .And.NotContain("השרטוט ו-Civil לא נפגעו")
                .And.Contain("ui_errors.log");
        }

        [Fact]
        public void LongWork_ShowsAProgressWindowOnItsOwnThread_FedByStageLog()
        {
            var palette = Ui("CivilDeliveryControl.xaml.cs");
            palette.Should().Contain("_busyProgress = BusyProgressWindow.Show(")
                .And.Contain("StageLog.StageObserver += OnStage;")
                .And.Contain("StageLog.StageObserver -= OnStage;")
                .And.Contain("_busyProgress?.Dispose();");

            var window = Ui("BusyProgressWindow.cs");
            window.Should().Contain("thread.SetApartmentState(ApartmentState.STA);")
                .And.Contain("Dispatcher.Run();")
                .And.Contain("Topmost = true")
                .And.Contain("IsIndeterminate = true");

            var stageLog = File.ReadAllText(Path.Combine(PluginSourceDir, "..", "MahodAI.CivilDelivery.Core", "Shared", "StageLog.cs"));
            stageLog.Should().Contain("public static event Action<string, string?>? StageObserver;");
            // The observer is invoked outside the log lock and can never fail the operation.
            var begin = stageLog.Substring(stageLog.IndexOf("public void Begin(", StringComparison.Ordinal));
            begin = begin.Substring(0, begin.IndexOf("public void End(", StringComparison.Ordinal));
            begin.IndexOf("var observer = StageObserver;", StringComparison.Ordinal)
                .Should().BeGreaterThan(begin.IndexOf("lock (_lock)", StringComparison.Ordinal));
            Regex.IsMatch(begin, @"try \{ observer\.Invoke\(stage, detail\); \}\s*catch").Should().BeTrue();
        }

        /// <summary>
        /// Live 29/09: the PLAN progress window showed the raw id "project.collect" for a
        /// minute. Every stage id the tool begins must have a Hebrew label (explicit or by
        /// family), and the fallback must never echo the id.
        /// </summary>
        [Fact]
        public void EveryStageId_HasAHebrewProgressLabel()
        {
            var palette = Ui("CivilDeliveryControl.xaml.cs");
            var at = palette.IndexOf("internal static string StageLabel(", StringComparison.Ordinal);
            at.Should().BeGreaterThan(0);
            var method = palette.Substring(at, palette.IndexOf("private void FlushRender()", at, StringComparison.Ordinal) - at);
            method.Should().NotContain("_ => stage,")
                .And.NotContain("+ stage.Substring(")
                .And.Contain("_ => \"מעבד…\",");

            var families = Regex.Matches(method, @"stage\.StartsWith\(""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            families.Should().NotBeEmpty();
            var roots = new[]
            {
                PluginSourceDir,
                Path.Combine(PluginSourceDir, "..", "MahodAI.CivilDelivery.Core"),
            };
            var ids = roots
                .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\.Begin\(""([^""]+)""").Cast<Match>())
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ids.Should().Contain("project.collect");
            foreach (var id in ids)
            {
                var labelled = method.Contains("\"" + id + "\"", StringComparison.Ordinal)
                    || families.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));
                labelled.Should().BeTrue($"stage '{id}' needs a Hebrew progress label");
            }
        }
    }
}
