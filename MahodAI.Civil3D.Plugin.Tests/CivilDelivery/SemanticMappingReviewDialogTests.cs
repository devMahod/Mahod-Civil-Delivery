using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Unshown WPF interactions with a fake assistant; no network or Civil.</summary>
public sealed class SemanticMappingReviewDialogTests
{
    private static readonly CatalogSnapshot Catalog = new()
    {
        SnapshotId = "SEMANTIC-UI-SIMULATION", FileHash = new string('c', 64),
        Items = { ["TEST.M"] = new() { Code = "TEST.M", Description = "אבני שפה לדוגמה בלבד", UnitRaw = "m" },
            ["TEST.A"] = new() { Code = "TEST.A", Description = "שטח לדוגמה בלבד", UnitRaw = "m2" } },
    };
    private static Dialog.Group Group(string key) => new(key, "Q742", "LWPOLYLINE", "length", "m", 12, 1,
        null, null, null, "Simulation", Array.Empty<MappingProposal>());
    private static SemanticMappingAssistResult Answer(string key = "g1", string code = "TEST.M") => new(
        new[] { new MappingProposal { RuleKey = key, Layer = "Q742", MeasurementKind = "length", MeasuredUnit = "m",
            ProposedCode = code, EvidenceKind = "ai-semantic-v1", Reasons = { "השערה לבדיקה בלבד" } } }, "הצעה לא מאושרת", false)
        { CatalogId = Catalog.SnapshotId, CatalogHash = Catalog.FileHash };

    [Fact]
    public void NoAssistantLeavesManualSearchAndStagingAvailable() => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("g1") }, Catalog);
        try
        {
            dialog.AiSuggest.IsEnabled.Should().BeFalse();
            dialog.CatalogSearch.Text = "TEST.M";
            dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue();
            dialog.ApprovedChoices.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void VerifiedObjectBridge_ReachesEditorGuardWithoutTypedDescriptionAndDoesNotApprove() => Sta(() =>
    {
        var bridge = new CatalogEvidenceBridge.Evidence(new[]
        {
            new CatalogEvidenceBridge.Subject(EvidenceKeys.BlockAttributes, "value", "DESC", "CURB", 1),
        }, Array.Empty<string>(), null, Array.Empty<string>(), Array.Empty<string>());
        var group = Group("g1") with { RecognitionEvidence = CatalogEvidenceBridge.Snapshot(bridge) };
        var dialog = new Dialog(new[] { group }, Catalog, suggestWithAi: (seen, context, _) =>
        {
            seen.RecognitionEvidence.Should().BeSameAs(group.RecognitionEvidence);
            context.Should().BeEmpty();
            return Task.FromResult(Answer());
        });
        try
        {
            Pump(dialog.RequestSemanticSuggestionsAsync());
            dialog.CatalogGrid.Items.Count.Should().Be(1);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            dialog.ApprovedChoices.Should().BeNull();
            dialog.StageButton.IsEnabled.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ProposalDoesNotChooseStageApproveOrChangeCatalog() => Sta(() =>
    {
        var calls = 0;
        var dialog = new Dialog(new[] { Group("g1") }, Catalog, suggestWithAi: (_, _, _) =>
        { calls++; return Task.FromResult(Answer()); });
        try
        {
            calls.Should().Be(0, "selection alone never sends data");
            dialog.AiContext.Text = "אבני שפה";
            Pump(dialog.RequestSemanticSuggestionsAsync());
            calls.Should().Be(1); dialog.CatalogGrid.Items.Count.Should().Be(1);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            dialog.StageButton.IsEnabled.Should().BeFalse(); dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.ApprovedChoices.Should().BeNull(); Catalog.Prices.Should().BeEmpty();
            dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue("engineer can explicitly use an unapproved suggestion");
            dialog.Confirm.IsChecked = true; dialog.Approver.Text = "SIMULATION REVIEWER";
            dialog.TryConfirm().Should().BeTrue();
            dialog.ApprovedChoices!.Single().CatalogCode.Should().Be("TEST.M");
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("HASHMAL", "חשמל לרמזורים", "הזנת חשמל לרמזורים", true)]
    [InlineData("HASHMAL", "", "הזנת חשמל לרמזורים", false)]
    [InlineData("HASHMAL-HV", "חשמל במתח נמוך", "כבלי חשמל במתח נמוך", false)]
    [InlineData("DSFSDFDSF", "", "כבלי חשמל במתח נמוך", false)]
    [InlineData("C:/private/HASHMAL", "חשמל לרמזורים", "הזנת חשמל לרמזורים", false)]
    public void ElectricalSuggestionsRespectWholeEvidenceWithoutApproving(
        string layer, string context, string description, bool expected) => Sta(() =>
    {
        var catalog = new CatalogSnapshot
        {
            SnapshotId = "ELECTRICAL-UI-SIMULATION", FileHash = new string('e', 64),
            Items = { ["TEST.E"] = new() { Code = "TEST.E", Description = description, UnitRaw = "m" } },
        };
        var group = Group("g1") with { Layer = layer };
        var answer = new SemanticMappingAssistResult(new[]
        {
            new MappingProposal { RuleKey = group.RuleKey, Layer = layer, MeasurementKind = "length",
                MeasuredUnit = "m", ProposedCode = "TEST.E", EvidenceKind = "ai-semantic-v1" },
        }, "הצעה לא מאושרת", false) { CatalogId = catalog.SnapshotId, CatalogHash = catalog.FileHash };
        var dialog = new Dialog(new[] { group }, catalog, suggestWithAi: (_, _, _) => Task.FromResult(answer));
        try
        {
            dialog.AiContext.Text = context;
            Pump(dialog.RequestSemanticSuggestionsAsync());
            dialog.CatalogGrid.Items.Count.Should().Be(expected ? 1 : 0);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            dialog.ApprovedChoices.Should().BeNull();
            dialog.StageButton.IsEnabled.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse();
            // A refused automatic hypothesis must not disable explicit catalog search.
            dialog.CatalogSearch.Text = "TEST.E";
            dialog.CatalogGrid.Items.Count.Should().Be(1);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("g2", "TEST.M")]
    [InlineData("g1", "FABRICATED")]
    [InlineData("g1", "TEST.A")]
    public void ForeignGroupInventedCodeOrWrongUnitNeverReachChoices(string key, string code) => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("g1") }, Catalog, suggestWithAi: (_, _, _) => Task.FromResult(Answer(key, code)));
        try
        {
            Pump(dialog.RequestSemanticSuggestionsAsync());
            dialog.CatalogGrid.Items.Count.Should().Be(0); dialog.ApprovedChoices.Should().BeNull();
            dialog.CatalogSearch.Text = "TEST.M"; dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue("manual recovery remains available");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void LateResponseAfterChangingGroupDoesNotReplaceCurrentResults() => Sta(() =>
    {
        var pending = new TaskCompletionSource<SemanticMappingAssistResult>();
        CancellationToken seenToken = default;
        var dialog = new Dialog(new[] { Group("g1"), Group("g2") }, Catalog, suggestWithAi: (_, _, ct) =>
        { seenToken = ct; return pending.Task; });
        try
        {
            var task = dialog.RequestSemanticSuggestionsAsync();
            dialog.GroupsGrid.SelectedIndex = 1; seenToken.IsCancellationRequested.Should().BeTrue();
            pending.SetResult(Answer()); Pump(task);
            dialog.CatalogGrid.Items.Count.Should().Be(0); dialog.SaveButton.IsEnabled.Should().BeFalse();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ProviderFailureDoesNotExposeExceptionOrLoseStagedChoices() => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("g1") }, Catalog, suggestWithAi: (_, _, _) =>
            Task.FromException<SemanticMappingAssistResult>(new InvalidOperationException("PRIVATE_PROVIDER_DETAIL")));
        try
        {
            dialog.CatalogSearch.Text = "TEST.M"; dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue(); Pump(dialog.RequestSemanticSuggestionsAsync());
            dialog.AiStatus.Text.Should().NotContain("PRIVATE_PROVIDER_DETAIL");
            dialog.Confirm.IsChecked = true; dialog.Approver.Text = "SIMULATION REVIEWER";
            dialog.TryConfirm().Should().BeTrue(); dialog.ApprovedChoices.Should().HaveCount(1);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditingContextOrSearchingManuallyCancelsOldAnswer(bool editContext) => Sta(() =>
    {
        var pending = new TaskCompletionSource<SemanticMappingAssistResult>();
        CancellationToken observed = default;
        var dialog = new Dialog(new[] { Group("g1") }, Catalog, suggestWithAi: (_, _, ct) =>
        { observed = ct; return pending.Task; });
        try
        {
            var task = dialog.RequestSemanticSuggestionsAsync();
            if (editContext) dialog.AiContext.Text = "צינורות ולא אבני שפה";
            else { dialog.CatalogSearch.Text = "TEST.A"; dialog.CatalogGrid.SelectedIndex = 0; }
            observed.IsCancellationRequested.Should().BeTrue();
            pending.SetResult(Answer()); Pump(task);
            if (editContext) dialog.CatalogGrid.Items.Count.Should().Be(0);
            else
            {
                dialog.CatalogSearch.Text.Should().Be("TEST.A");
                ((Dialog.CatalogOption)dialog.CatalogGrid.SelectedItem).Code.Should().Be("TEST.A");
            }
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void RoleAndIndependentDraftsSurviveGroupRoundTripWithoutSendingOrLosingProposals() => Sta(() =>
    {
        var calls = 0; string? received = null;
        var dialog = new Dialog(new[] { Group("g1"), Group("g2") }, Catalog,
            suggestWithAi: (group, context, _) => { calls++; received = context; return Task.FromResult(Answer(group.RuleKey)); });
        try
        {
            dialog.SemanticRole.SelectedValue = "curb"; dialog.AiContext.Text = "מונמכת";
            calls.Should().Be(0);
            Pump(dialog.RequestSemanticSuggestionsAsync()); received.Should().Be("אבן שפה — מונמכת");
            dialog.CatalogGrid.SelectedIndex = 0; dialog.StageSelection().Should().BeTrue();
            dialog.GroupsGrid.SelectedIndex = 1;
            dialog.SemanticRole.SelectedValue = "water"; dialog.AiContext.Text = "קו מים";
            Pump(dialog.RequestSemanticSuggestionsAsync());
            dialog.GroupsGrid.SelectedIndex = 0;
            dialog.SemanticRole.SelectedValue.Should().Be("curb"); dialog.AiContext.Text.Should().Be("מונמכת");
            dialog.CatalogGrid.Items.Count.Should().Be(1);
            dialog.RemoveButton.IsEnabled.Should().BeTrue("the separately staged mapping is retained");
            dialog.GroupsGrid.SelectedIndex = 1;
            dialog.SemanticRole.SelectedValue.Should().Be("water"); dialog.AiContext.Text.Should().Be("קו מים");
            dialog.CatalogGrid.Items.Count.Should().Be(1); calls.Should().Be(2);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ExplicitHintSaveWorksWithoutCatalogChoiceAndKeepsStagedChoicesOnFurtherSave() => Sta(() =>
    {
        var saved = new List<SemanticHintPolicy.Draft>();
        var dialog = new Dialog(new[] { Group("g1") }, Catalog,
            saveSemanticHint: (_, input, author) => { author.Should().Be("FIXTURE"); saved.Add(input); });
        try
        {
            dialog.SemanticRole.SelectedValue = "curb"; dialog.Approver.Text = "FIXTURE";
            dialog.TrySaveSemanticHint().Should().BeTrue(); saved.Should().ContainSingle();
            dialog.ApprovedChoices.Should().BeNull(); dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.CatalogSearch.Text = "TEST.M"; dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue();
            dialog.AiContext.Text = "תיאור מתוקן";
            dialog.TrySaveSemanticHint().Should().BeTrue(); saved.Should().HaveCount(2);
            dialog.Confirm.IsChecked = true; dialog.TryConfirm().Should().BeTrue();
            dialog.ApprovedChoices!.Should().ContainSingle().Which.CatalogCode.Should().Be("TEST.M");
        }
        finally { dialog.Close(); }
        var reopened = new Dialog(new[] { Group("g1") with { SemanticHint = saved.Last() } }, Catalog);
        try { reopened.SemanticRole.SelectedValue.Should().Be("curb"); reopened.AiContext.Text.Should().Be("תיאור מתוקן"); }
        finally { reopened.Close(); }
    });

    [Fact]
    public void RoleEditCancelsPendingAnswerAndOversizedCombinedContextIsNotTruncatedOrSent() => Sta(() =>
    {
        var pending = new TaskCompletionSource<SemanticMappingAssistResult>();
        var calls = 0; CancellationToken observed = default;
        var dialog = new Dialog(new[] { Group("g1") }, Catalog,
            suggestWithAi: (_, _, ct) => { calls++; observed = ct; return pending.Task; });
        try
        {
            var request = dialog.RequestSemanticSuggestionsAsync();
            dialog.SemanticRole.SelectedValue = "curb"; observed.IsCancellationRequested.Should().BeTrue();
            pending.SetResult(Answer()); Pump(request); dialog.CatalogGrid.Items.Count.Should().Be(0);
            dialog.AiContext.Text = new string('א', 620);
            Pump(dialog.RequestSemanticSuggestionsAsync()); calls.Should().Be(1);
            dialog.AiContext.Text.Should().HaveLength(620); dialog.AiStatus.Text.Should().Contain("לא קוצר");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void HintPublicationErrorPreservesChoicesWithoutClaimingThatNothingWasSaved() => Sta(() =>
    {
        var dialog = new Dialog(new[] { Group("g1") }, Catalog,
            saveSemanticHint: (_, _, _) => throw new InvalidOperationException("publication needs recheck"));
        try
        {
            dialog.CatalogSearch.Text = "TEST.M"; dialog.CatalogGrid.SelectedIndex = 0;
            dialog.StageSelection().Should().BeTrue(); dialog.Approver.Text = "FIXTURE";
            dialog.SemanticRole.SelectedValue = "curb";
            dialog.TrySaveSemanticHint().Should().BeFalse();
            dialog.AiStatus.Text.Should().Contain("לבדוק מה נשמר").And.NotContain("המשמעות לא נשמרה");
            dialog.Confirm.IsChecked = true; dialog.TryConfirm().Should().BeTrue();
            dialog.ApprovedChoices.Should().ContainSingle();
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(900, 680, 870, 620, "semantic-mapping-expanded.png", false)]
    [InlineData(1180, 780, 1150, 720, "semantic-mapping-expanded-default.png", false)]
    [InlineData(900, 680, 870, 620, "semantic-mapping-long-expanded.png", true)]
    [InlineData(1180, 780, 1150, 720, "semantic-mapping-long-expanded-default.png", true)]
    public void ExpandedAssistanceAtMinimumSizeKeepsInputsAndActionsLaidOut(
        double width, double height, double clientWidth, double clientHeight, string captureName, bool longDetails) => Sta(() =>
    {
        var group = Group("g1");
        var answer = Answer();
        var assistantCalls = 0; var saveCalls = 0;
        if (longDetails)
        {
            // Keep the same long-label layout stress without a path separator:
            // path-like evidence is separately refused at the assistant boundary.
            group = group with { Layer = string.Join(" · ", Enumerable.Repeat("מקור-מקונן-ושכבת-ריצוף-ארוכה-לבדיקת-גלישה", 5)),
                MeasurementKind = "area", Unit = "m2", AlternativeRuleKey = "perimeter",
                AlternativeQuantityDisplay = string.Join("; ", Enumerable.Repeat("128.00 מטר היקף — אותם עצמים; אין להחריג שורות פתוחות אחרות", 5)) };
            answer = new SemanticMappingAssistResult(new[] { new MappingProposal { RuleKey = "g1", Layer = group.Layer,
                MeasurementKind = "area", MeasuredUnit = "m2", ProposedCode = "TEST.A", EvidenceKind = "ai-semantic-v1",
                Reasons = { string.Join("; ", Enumerable.Repeat("הסבר הצעה ארוך לבדיקה; המדידה והמחיר אינם מאושרים", 12)) } } },
                string.Join(" ", Enumerable.Repeat("הצעה לא מאושרת; יש לבדוק משמעות, מקור ויחידה לפני בחירה.", 8)), false)
                { CatalogId = Catalog.SnapshotId, CatalogHash = Catalog.FileHash };
        }
        var dialog = new Dialog(new[] { group, Group("g2") }, Catalog,
            saveReviewed: (_, _) => saveCalls++,
            suggestWithAi: (_, _, _) => { assistantCalls++; return Task.FromResult(answer); },
            saveSemanticHint: (_, _, _) => saveCalls++);
        try
        {
            dialog.Width = width; dialog.Height = height;
            var expander = Ancestors(dialog.AiSuggest).OfType<System.Windows.Controls.Expander>().First();
            expander.IsExpanded = true;
            dialog.AiContext.Text = "אבני שפה מונמכות — תיאור מהנדס לבדיקה";
            dialog.SemanticRole.SelectedValue = "curb";
            dialog.Approver.Text = "בודק ממשק — הדמיה בלבד";
            Pump(dialog.RequestSemanticSuggestionsAsync());
            if (longDetails)
            {
                dialog.CatalogGrid.SelectedIndex = 0;
                dialog.StageSelection().Should().BeFalse("the explicit alternative decision is still required");
                dialog.ConfirmAlternative.IsChecked = true;
                dialog.StageButton.IsEnabled.Should().BeTrue();
            }
            var root = (System.Windows.FrameworkElement)dialog.Content;
            // Preserve Window-inherited presentation before detaching. A neutral
            // parent includes the RTL child's transform exactly once in the capture.
            root.FlowDirection = dialog.FlowDirection;
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, dialog.FontFamily);
            root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, dialog.FontSize);
            root.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, dialog.FontWeight);
            root.SetValue(System.Windows.Documents.TextElement.FontStyleProperty, dialog.FontStyle);
            root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, dialog.Foreground);
            dialog.Content = null;
            var frame = new System.Windows.Controls.Border { Background = dialog.Background,
                FlowDirection = System.Windows.FlowDirection.LeftToRight, Child = root };
            // Conservative client area after window chrome at the 900x680 minimum.
            var client = new System.Windows.Size(clientWidth, clientHeight);
            frame.Measure(client);
            frame.Arrange(new System.Windows.Rect(new System.Windows.Point(), client)); frame.UpdateLayout();
            // Keep the failed-layout image as evidence without suppressing any assertion.
            using var layoutAssertions = new FluentAssertions.Execution.AssertionScope();
            dialog.AiSuggest.ActualWidth.Should().BeGreaterThan(50);
            dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90,
                "expanded assistance and verbose evidence must leave multiple usable catalog rows");
            dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
            dialog.AiContext.ActualWidth.Should().BeGreaterThan(150);
            foreach (var control in new System.Windows.FrameworkElement[] { dialog.AiSuggest, dialog.AiCancel,
                dialog.AiContext, dialog.SemanticRole, dialog.SaveSemanticHint, dialog.AiStatus, dialog.CatalogSearch, dialog.CatalogGrid,
                dialog.StageButton, dialog.RemoveButton, dialog.SaveButton, dialog.Approver, dialog.ReviewerLabel, dialog.Confirm })
            {
                var bounds = control.TransformToAncestor(frame).TransformBounds(new System.Windows.Rect(control.RenderSize));
                bounds.Left.Should().BeGreaterThanOrEqualTo(-0.1);
                bounds.Top.Should().BeGreaterThanOrEqualTo(-0.1);
                bounds.Right.Should().BeLessThanOrEqualTo(frame.ActualWidth + 0.1);
                bounds.Bottom.Should().BeLessThanOrEqualTo(frame.ActualHeight + 0.1);
            }
            var directory = Environment.GetEnvironmentVariable("MHD_SEMANTIC_UI_CAPTURE_DIR");
            void Capture(string name)
            {
                if (string.IsNullOrWhiteSpace(directory)) return;
                System.IO.Directory.CreateDirectory(directory);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)client.Width, (int)client.Height,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(frame);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name));
                encoder.Save(output);
            }
            Capture(captureName);
            if (longDetails)
            {
                var overviewColumn = dialog.GroupsGrid.Columns.OfType<System.Windows.Controls.DataGridTextColumn>()
                    .Single(column => (column.Binding as System.Windows.Data.Binding)?.Path.Path == nameof(Dialog.GroupRow.Overview));
                var firstRow = (System.Windows.Controls.DataGridRow)dialog.GroupsGrid.ItemContainerGenerator.ContainerFromIndex(0);
                var secondRow = (System.Windows.Controls.DataGridRow)dialog.GroupsGrid.ItemContainerGenerator.ContainerFromIndex(1);
                firstRow.ActualHeight.Should().BeLessThan(65, "a long layer name must not consume the group list");
                secondRow.Should().NotBeNull("the next group must remain visible without scrolling");
                var overview = (Dialog.GroupRow)firstRow.Item;
                var overviewText = (System.Windows.Controls.TextBlock)overviewColumn.GetCellContent(firstRow);
                overview.Overview.Split('\n').Should().HaveCount(3, "the compact preview has three explicit lines");
                overviewText.Text.Should().Be(overview.Overview);
                overviewText.TextWrapping.Should().Be(System.Windows.TextWrapping.NoWrap);
                overviewText.TextTrimming.Should().Be(System.Windows.TextTrimming.CharacterEllipsis);
                overviewText.MaxHeight.Should().Be(51, "the three-line preview must stay bounded");
                System.Windows.Data.BindingOperations.GetBindingExpression(overviewText,
                    System.Windows.FrameworkElement.ToolTipProperty)!.UpdateTarget();
                overviewText.ToolTip.Should().Be(overview.OverviewDetail, "the complete overview remains accessible");
                overviewText.ToolTip.ToString().Should().Contain(group.Layer, "the full long source name must not be lost to ellipsis");
                dialog.ConfirmAlternative.Visibility.Should().Be(System.Windows.Visibility.Visible);
                var approvalBounds = dialog.ConfirmAlternative.TransformToAncestor(frame)
                    .TransformBounds(new System.Windows.Rect(dialog.ConfirmAlternative.RenderSize));
                approvalBounds.Height.Should().BeGreaterThan(15);
                approvalBounds.Bottom.Should().BeLessThan(dialog.StageButton.TransformToAncestor(frame)
                    .Transform(new System.Windows.Point()).Y + 1);
                var details = (System.Windows.Controls.StackPanel)dialog.DecisionDetails.Content;
                details.Children.OfType<System.Windows.Controls.TextBlock>().First().Text.Should()
                    .Contain(group.AlternativeQuantityDisplay!);
                dialog.SubjectDetails.ToolTip.ToString().Should().Contain(group.Layer).And.Contain("עצמים");
                dialog.SubjectDetails.ActualHeight.Should().BeLessThanOrEqualTo(22);
                dialog.DecisionDetails.ScrollableHeight.Should().BeGreaterThan(0);
                dialog.DecisionDetails.ScrollToEnd(); frame.UpdateLayout();
                dialog.DecisionDetails.VerticalOffset.Should().Be(dialog.DecisionDetails.ScrollableHeight);
                Capture(System.IO.Path.GetFileNameWithoutExtension(captureName) + "-details-bottom.png");
                dialog.StageSelection().Should().BeTrue();
            }
            assistantCalls.Should().Be(1); saveCalls.Should().Be(0);
        }
        finally { dialog.Close(); }
    });

    private static IEnumerable<System.Windows.DependencyObject> Ancestors(System.Windows.DependencyObject item)
    {
        while ((item = System.Windows.Media.VisualTreeHelper.GetParent(item) ??
                    System.Windows.LogicalTreeHelper.GetParent(item)) != null!)
            yield return item;
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            var deadline = DateTime.UtcNow.AddSeconds(3);
            timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow > deadline) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        }
        task.IsCompleted.Should().BeTrue(); task.GetAwaiter().GetResult();
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("WPF fixture did not finish.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
