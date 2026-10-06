using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The strip-label dialog's state, driven through the exact gesture that killed
    /// Civil on 2026-09-03 (un-check a row, then change its name) and through the
    /// one-click contract (choosing a name approves the row). The dialog window only
    /// binds this model, so no WPF or AutoCAD is needed to prove the behaviour.
    /// </summary>
    public class SpanLabelDialogBehaviorTests
    {
        private static SectionPlanRecord Record(params (double from, double to, string left, string right)[] spans)
        {
            var record = new SectionPlanRecord
            {
                RecordId = "cl-TEST",
                Cl = new ClSourceRecord
                {
                    RecordId = "cl-TEST",
                    SourceDrawing = "CL.dwg",
                    SourceDrawingHash = new string('a', 64),
                    SourceHandle = "7C89",
                    SourceEntityType = "LINE",
                    SourceLayer = "GFC111",
                    SourceEndpoints = new[] { 0.0, 0.0, 10.0, 0.0 },
                    WcsEndpoints = new[] { 0.0, 0.0, 10.0, 0.0 },
                },
                SectionId = "STA-12145",
                SelectedAlignment = "600",
                Station = 12145.43,
            };
            foreach (var (from, to, left, right) in spans)
            {
                record.PresentationCoverage.UnresolvedSpans.Add(new SectionUnresolvedSpanPlan
                {
                    FromOffsetM = from,
                    ToOffsetM = to,
                    WidthM = to - from,
                    LeftKind = left,
                    RightKind = right,
                    Reason = "no-confident-strip-label",
                    HomologousSpanCount = 2,
                });
            }
            return record;
        }

        private static SpanLabelDecisionModel Model() => new(new[]
        {
            Record((-8.45, -4.04, "curb", "curb"), (-4.04, -2.03, "curb", "island")),
        });

        [Fact]
        public void ChoosingAName_ApprovesTheRow_AndTheCrashGesture_IsHarmless()
        {
            var model = Model();
            var changes = 0;
            model.Changed += () => changes++;
            var rows = model.Rows;

            rows.Should().HaveCount(2);
            rows.All(row => !row.IsApproved && row.Label == string.Empty).Should().BeTrue();
            model.CanSave("ArthurF").Should().BeFalse("nothing is approved yet");
            model.Validation("ArthurF").Should().Contain("בחר שם");
            rows[0].Bounds.Should().Be("אבן שפה ↔ אבן שפה");
            rows[1].Bounds.Should().Be("אבן שפה ↔ אי תנועה");

            // One click: choosing a name approves the row and enables Save.
            rows[0].Label = "נתיב נסיעה";
            rows[0].IsApproved.Should().BeTrue();
            model.CanSave("ArthurF").Should().BeTrue();
            model.Approvals.Should().ContainSingle().Which.Label.Should().Be("נתיב נסיעה");

            // The gesture that crashed Civil: un-check the row, then change its name.
            rows[0].IsApproved = false;
            model.CanSave("ArthurF").Should().BeFalse();
            rows[0].Label = "נת\"צ";
            rows[0].IsApproved.Should().BeTrue("a new name re-approves the row");
            model.Approvals.Should().ContainSingle().Which.Label.Should().Be("נת\"צ");

            // An approved row whose name was erased blocks Save with a reason.
            rows[0].Label = string.Empty;
            rows[0].IsApproved.Should().BeTrue("erasing the text does not silently drop the approval");
            model.CanSave("ArthurF").Should().BeFalse();
            model.Validation("ArthurF").Should().Contain("בלי שם");

            // No approver, no save.
            rows[0].Label = "מדרכה";
            model.CanSave("").Should().BeFalse();
            model.Validation("   ").Should().Contain("מאשר");

            changes.Should().BeGreaterThan(5, "every row change notifies the dialog");
        }

        [Fact]
        public void BatchActions_ApplyClearAndStrongSuggestions()
        {
            var model = Model();
            var rows = model.Rows;

            model.ApplyLabelToSelected(rows, "").Should().Contain("שם חוקי");
            model.ApplyLabelToSelected(rows, new string('x', 81)).Should().Contain("שם חוקי");
            model.ApplyLabelToSelected(Array.Empty<SpanLabelDecisionModel.SpanRow>(), "אי תנועה")
                .Should().Contain("לפחות שורה אחת");
            model.ApplyLabelToSelected(rows, " אי תנועה ").Should().BeNull();
            rows.All(row => row.Label == "אי תנועה" && row.IsApproved).Should().BeTrue();
            model.Approvals.Should().HaveCount(2);

            model.ClearApprovals();
            model.Approvals.Should().BeEmpty();
            rows.All(row => row.Label == "אי תנועה").Should().BeTrue("names survive a cleared approval");

            model.MarkStrongSuggestions();
            model.Approvals.Should().BeEmpty("no row carries a strong suggestion");
        }

        [Fact]
        public void StrongSuggestions_ArePreselected_ButOnlyWithAName()
        {
            var record = Record((0.0, 3.5, "curb", "curb"), (3.5, 5.0, "curb", "island"));
            record.PresentationCoverage.UnresolvedSpans[0].SuggestedLabel = "נתיב נסיעה";
            record.PresentationCoverage.UnresolvedSpans[0].SuggestionConfidence = "high";
            record.PresentationCoverage.UnresolvedSpans[0].StrongReviewCandidate = true;
            record.PresentationCoverage.UnresolvedSpans[1].StrongReviewCandidate = true; // no label

            var model = new SpanLabelDecisionModel(new[] { record });
            model.Rows[0].IsApproved.Should().BeTrue();
            model.Rows[0].Suggestion.Should().Contain("נתיב נסיעה").And.Contain("ביטחון גבוה");
            model.Rows[1].IsApproved.Should().BeFalse("a missing name is not an approvable suggestion");
            model.CanSave("ArthurF").Should().BeTrue("only the valid source suggestion is selected");
            model.ClearApprovals();
            model.MarkStrongSuggestions();
            model.Approvals.Should().ContainSingle().Which.Label.Should().Be("נתיב נסיעה");
        }

        [Fact]
        public void VehicleName_ThatDoesNotFitTheWidth_BlocksSaveWithAHebrewReason()
        {
            // Live 2026-09-03: נת"צ on the 2.01 m curb-to-island strip was refused only
            // after the dialog closed, in English. The model now refuses before Save.
            var model = Model();
            var rows = model.Rows;
            rows[0].Label = "נתיב נסיעה";      // 4.41 m: a car fits
            rows[1].Label = "נת\"צ";           // 2.01 m: a bus needs 2.95 m
            rows[1].IsApproved.Should().BeTrue();
            rows[1].LabelProblem.Should().Contain("לא מתאים לרוחב").And.Contain("2.95");
            model.CanSave("ArthurF").Should().BeFalse();
            model.Validation("ArthurF").Should().StartWith("שורה 2:").And.Contain("אוטובוס");

            rows[1].Label = "מדרכה";
            rows[1].LabelProblem.Should().BeNull();
            model.CanSave("ArthurF").Should().BeTrue();

            // An un-approved row with a bad name does not block; approving it does.
            rows[1].Label = "נת\"צ";
            rows[1].IsApproved = false;
            model.CanSave("ArthurF").Should().BeTrue();
            rows[1].IsApproved = true;
            model.CanSave("ArthurF").Should().BeFalse();

            // One name across several lanes is refused as well.
            var wide = new SpanLabelDecisionModel(new[] { Record((0.0, 8.2, "curb", "curb")) });
            wide.Rows[0].Label = "נתיב נסיעה";
            wide.Rows[0].LabelProblem.Should().Contain("יותר מנתיב אחד");
            wide.CanSave("ArthurF").Should().BeFalse();
        }

        [Fact]
        public void EmptyRecordList_IsRefusedBeforeAnyWindowExists()
        {
            var act = () => new SpanLabelDecisionModel(Array.Empty<SectionPlanRecord>());
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void UiGuard_ReportsInsteadOfThrowing()
        {
            var reports = new List<(string Context, Exception Error)>();
            UiGuard.ReportOverride = (context, error) => reports.Add((context, error));
            try
            {
                var act = () => UiGuard.Run("test", () => throw new InvalidOperationException("boom"));
                act.Should().NotThrow();
                reports.Should().ContainSingle().Which.Error.Message.Should().Be("boom");
            }
            finally
            {
                UiGuard.ReportOverride = null;
            }
        }

        [Theory]
        [InlineData("נתיב אופניים")]
        [InlineData("שביל אופניים")]
        public void BothOfferedBicycleNames_ValidateWidthBeforeDialogSave(string label)
        {
            SpanLabelDecisionModel.LabelChoices.Should().Contain(label);
            var narrow = new SpanLabelDecisionModel(new[] { Record((0.0, 1.5, "curb", "curb")) });
            narrow.Rows[0].Label = label;
            narrow.Rows[0].LabelProblem.Should().Contain("לא מתאים לרוחב");
            narrow.CanSave("ArthurF").Should().BeFalse();
            var fitting = new SpanLabelDecisionModel(new[] { Record((0.0, 2.0, "curb", "curb")) });
            fitting.Rows[0].Label = label;
            fitting.Rows[0].LabelProblem.Should().BeNull();
            fitting.CanSave("ArthurF").Should().BeTrue();
        }

        [Fact]
        public void UiGuard_DoesNotSwallowFatalRuntimeFailures()
        {
            var fatal = new OutOfMemoryException("synthetic fatal; no allocation attempted");
            var act = () => UiGuard.Run("test", () => throw fatal);
            act.Should().Throw<OutOfMemoryException>().Which.Should().BeSameAs(fatal);
            UiGuard.ShouldHandleDispatcherException(
                new AggregateException(new InvalidOperationException("ordinary"), fatal))
                .Should().BeFalse("wrapping a fatal failure does not make continuation safe");
        }

        [Fact]
        public void UiGuard_Attach_IsScopedToLoadedWindowAndActualProductUiFailures()
        {
            Exception? failure = null;
            var reports = new List<(string Context, Exception Error)>();
            UiGuard.ReportOverride = (context, error) => reports.Add((context, error));
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        // Never show this window: lifecycle events exercise the actual
                        // dispatcher subscription without opening a native test window.
                        var window = new Window();
                        var dispatcher = Dispatcher.CurrentDispatcher;
                        UiGuard.Attach(window, "owned dialog");
                        Action owned = () => _ = new SpanLabelDecisionModel(Array.Empty<SectionPlanRecord>());
                        Action foreign = () => throw new InvalidOperationException("unrelated add-in");

                        bool GuardHandled(Action action)
                        {
                            var frame = new DispatcherFrame();
                            bool? handled = null;
                            DispatcherUnhandledExceptionEventHandler observer = (_, args) =>
                            {
                                handled = args.Handled;
                                // A test-only last-resort handler prevents the intentionally
                                // foreign exception from escaping the test dispatcher.
                                args.Handled = true;
                                frame.Continue = false;
                            };
                            dispatcher.UnhandledException += observer;
                            try
                            {
                                dispatcher.BeginInvoke(action);
                                Dispatcher.PushFrame(frame);
                                return handled ?? throw new InvalidOperationException("Test exception not observed");
                            }
                            finally { dispatcher.UnhandledException -= observer; }
                        }

                        GuardHandled(owned).Should().BeFalse("an unshown/failed constructor has no global handler");
                        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        GuardHandled(owned).Should().BeTrue("recoverable failures from our UI are contained");
                        GuardHandled(foreign).Should().BeFalse("another add-in shares the dispatcher, not our guard");
                        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                        GuardHandled(owned).Should().BeFalse("unloading removes the global subscription");
                        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        GuardHandled(owned).Should().BeTrue("reload installs exactly one active guard");
                        window.Close();
                        GuardHandled(owned).Should().BeFalse("closing removes the subscription as well");
                        dispatcher.InvokeShutdown();
                    }
                    catch (Exception ex) { failure = ex; }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("dispatcher lifecycle test must finish");
                failure.Should().BeNull();
                reports.Should().HaveCount(2);
                reports.Should().OnlyContain(report => report.Context == "owned dialog" &&
                    report.Error is ArgumentException);
            }
            finally { UiGuard.ReportOverride = null; }
        }

        [Fact]
        public void BusyProgressWindow_ShowsUpdatesAndClosesOnItsOwnThread()
        {
            var act = () =>
            {
                using (var progress = BusyProgressWindow.Show("Mahod Civil Delivery", "מתכנן חתכים…"))
                {
                    for (var i = 0; i < 20; i++) progress.Update($"שלב {i}");
                    Thread.Sleep(300);
                }
                Thread.Sleep(500);
            };
            act.Should().NotThrow();
        }
    }
}
