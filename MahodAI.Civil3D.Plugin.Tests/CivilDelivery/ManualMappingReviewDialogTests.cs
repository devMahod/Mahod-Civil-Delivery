using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Xunit.Abstractions;
using Button = System.Windows.Controls.Button;
using Dialog = MahodAI.Civil3D.Plugin.CivilDelivery.UI.ManualMappingReviewDialog;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Compiled, unshown WPF interaction; no modal, profile write, DWG or Autodesk execution.</summary>
public sealed class ManualMappingReviewDialogTests
{
    private readonly ITestOutputHelper _output;
    public ManualMappingReviewDialogTests(ITestOutputHelper output) => _output = output;
    private const string LengthA = "TEST.LENGTH.A";
    private const string LengthB = "TEST.LENGTH.B";
    private const string AreaCode = "TEST.AREA";

    private static CatalogSnapshot Catalog() => new()
    {
        SnapshotId = "MANUAL-DIALOG-SIMULATION-ONLY", FileHash = new string('a', 64),
        Items =
        {
            [LengthA] = new() { Code = LengthA, Description = "אבן שפה רגילה לבדיקה", UnitRaw = "m" },
            [LengthB] = new() { Code = LengthB, Description = "אבן שפה מונמכת לבדיקה", UnitRaw = "m" },
            [AreaCode] = new() { Code = AreaCode, Description = "ריצוף שטח לבדיקה", UnitRaw = "m2" },
        },
        Prices =
        {
            [LengthA] = new() { Code = LengthA, Price = 12.5m, PriceBookId = "MANUAL-DIALOG-SIMULATION-ONLY", SourceHash = new string('a', 64) },
        },
    };

    private static Dialog.Group Group(string key = "g1", string unit = "m", string? alternative = null,
        string? readOnly = null, params string[] proposals) => new(
        key, "FIXTURE-LAYER-" + key, "LWPOLYLINE", unit == "m2" ? "area" : "length",
        unit, 37.25, 2, null, alternative, alternative == null ? null : "חלופה " + alternative,
        "SIMULATION ONLY: geometry findings remain unresolved",
        proposals.Select((code, index) => new MappingProposal
        {
            RuleKey = key, MeasurementKind = unit == "m2" ? "area" : "length", MeasuredUnit = unit,
            ProposedCode = code, Score = 90 - index,
            Reasons = { "Synthetic competing proposal " + code },
        }).ToArray(), readOnly);

    private static Dialog.GroupRow Row(Dialog dialog, string key) =>
        dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Single(row => row.Subject.RuleKey == key);

    private static void SelectGroup(Dialog dialog, string key) => dialog.GroupsGrid.SelectedItem = Row(dialog, key);

    private static Dialog.CatalogOption SelectCode(Dialog dialog, string code)
    {
        var option = dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Single(item => item.Code == code);
        dialog.CatalogGrid.SelectedItem = option;
        return option;
    }

    private static void Stage(Dialog dialog, string key, string code)
    {
        SelectGroup(dialog, key);
        dialog.CatalogSearch.Text = code;
        SelectCode(dialog, code);
        dialog.StageSelection().Should().BeTrue();
    }

    private static void WithDialog(IReadOnlyList<Dialog.Group> groups, Action<Dialog, CatalogSnapshot> action,
        Action<IReadOnlyList<Dialog.Choice>, string>? saveReviewed = null, string? initialRuleKey = null)
    {
        RunSta(() =>
        {
            var catalog = Catalog();
            var groupsBefore = JsonSerializer.Serialize(groups);
            var catalogBefore = JsonSerializer.Serialize(catalog);
            var dialog = new Dialog(groups, catalog, saveReviewed, initialRuleKey: initialRuleKey);
            try
            {
                dialog.IsVisible.Should().BeFalse(); dialog.IsLoaded.Should().BeFalse();
                action(dialog, catalog);
                JsonSerializer.Serialize(groups).Should().Be(groupsBefore, "review uses separate draft state, never mutates proposal inputs");
                JsonSerializer.Serialize(catalog).Should().Be(catalogBefore, "staging neither assigns catalog prices nor rewrites items");
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void InitialRuleKeyOpensExactNonFirstGroupWithoutSelectingCodeOrApproving()
    {
        var saves = 0;
        WithDialog(new[] { Group("S_WALL_BT", proposals: new[] { LengthB }),
            Group("HW-CURB", proposals: new[] { LengthA, LengthB }) }, (dialog, _) =>
        {
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "HW-CURB"));
            dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Select(option => option.Code).Should().Equal(LengthA, LengthB);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Should().OnlyContain(row => row.Draft == "—");
            dialog.StageSelection().Should().BeFalse(); dialog.TryConfirm().Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse(); dialog.RemoveButton.IsEnabled.Should().BeFalse();
            dialog.Approver.Text.Should().BeEmpty(); dialog.Confirm.IsChecked.Should().NotBe(true);
            dialog.ApprovedChoices.Should().BeNull();
            // Exercise the actual loaded-event handler without showing a window.
            dialog.GroupsGrid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "HW-CURB"));
            dialog.IsVisible.Should().BeFalse();
        }, (_, _) => saves++, initialRuleKey: "HW-CURB");
        saves.Should().Be(0, "opening, locating the initial row and closing are not approval");
    }

    [Theory]
    [InlineData("not-in-this-scan")]
    [InlineData("hw-curb")]
    [InlineData("")]
    public void MissingInitialRuleKeyDoesNotSubstituteAnUnrelatedGroup(string initialRuleKey)
    {
        WithDialog(new[] { Group("S_WALL_BT"), Group("HW-CURB", proposals: new[] { LengthA }) }, (dialog, _) =>
        {
            dialog.GroupsGrid.SelectedItem.Should().BeNull();
            dialog.CatalogGrid.Items.Count.Should().Be(0); dialog.CatalogGrid.SelectedItem.Should().BeNull();
            dialog.StageSelection().Should().BeFalse(); dialog.TryConfirm().Should().BeFalse();
            dialog.ApprovedChoices.Should().BeNull();
            SelectGroup(dialog, "HW-CURB"); // Recovery remains an explicit user selection.
            dialog.CatalogGrid.Items.Count.Should().Be(1);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
        }, initialRuleKey: initialRuleKey);
    }

    [Fact]
    public void InitialReadOnlyGroupRemainsSelectedAndCannotBeStaged()
    {
        WithDialog(new[] { Group("editable"), Group("held", readOnly: "מקור חסום — לבדיקה בלבד",
            proposals: new[] { LengthA }) }, (dialog, _) =>
        {
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "held"));
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            SelectCode(dialog, LengthA);
            dialog.StageButton.IsEnabled.Should().BeFalse(); dialog.StageSelection().Should().BeFalse();
            Row(dialog, "held").Draft.Should().Be("—");
            dialog.TryConfirm().Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull();
        }, initialRuleKey: "held");
    }

    [Fact]
    public void OmittedInitialRuleKeyPreservesFirstGroupAndLoadedNeverRevertsUserSelection()
    {
        WithDialog(new[] { Group("first"), Group("second", proposals: new[] { LengthA }) }, (dialog, _) =>
        {
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "first"));
            SelectGroup(dialog, "second");
            dialog.GroupsGrid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            dialog.GroupsGrid.SelectedItem.Should().BeSameAs(Row(dialog, "second"));
            dialog.CatalogGrid.SelectedItem.Should().BeNull(); dialog.ApprovedChoices.Should().BeNull();
        });
    }

    [Fact]
    public void InitialCompetingProposalsDoNotSelectCodeStageOrApproveAnything()
    {
        WithDialog(new[] { Group(proposals: new[] { LengthA, LengthB }) }, (dialog, _) =>
        {
            dialog.GroupsGrid.Items.Count.Should().Be(1);
            dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Select(option => option.Code).Should().Equal(LengthA, LengthB);
            dialog.CatalogGrid.SelectedItem.Should().BeNull();
            Row(dialog, "g1").Draft.Should().Be("—");
            dialog.StageButton.IsEnabled.Should().BeFalse(); dialog.RemoveButton.IsEnabled.Should().BeFalse();
            dialog.SaveButton.IsEnabled.Should().BeFalse(); dialog.Approver.Text.Should().BeEmpty();
            dialog.Confirm.IsChecked.Should().NotBe(true);
            dialog.StageSelection().Should().BeFalse(); dialog.TryConfirm().Should().BeFalse();
            dialog.ApprovedChoices.Should().BeNull();
        });
    }

    [Fact]
    public void ChoosingCompetingProposalIsNotApprovalAndOnlyExplicitStagingAddsIt()
    {
        WithDialog(new[] { Group(proposals: new[] { LengthA, LengthB, LengthA }) }, (dialog, _) =>
        {
            dialog.CatalogGrid.Items.Count.Should().Be(2, "duplicate suggestions must not become duplicate choices");
            SelectCode(dialog, LengthB);
            dialog.StageButton.IsEnabled.Should().BeTrue(); Row(dialog, "g1").Draft.Should().Be("—");
            dialog.ApprovedChoices.Should().BeNull(); dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.StageSelection().Should().BeTrue();
            Row(dialog, "g1").Draft.Should().Be(LengthB);
            dialog.ApprovedChoices.Should().BeNull(); dialog.TryConfirm().Should().BeFalse();
        });
    }

    [Fact]
    public void ManualSearchWithoutProposalsStagesMissingPriceWithoutInventingPrice()
    {
        WithDialog(new[] { Group() }, (dialog, catalog) =>
        {
            dialog.CatalogGrid.Items.Count.Should().Be(0);
            dialog.CatalogSearch.Text = "מונמכת"; // actual TextChanged handler, not a model-only search
            var option = dialog.CatalogGrid.Items.Cast<Dialog.CatalogOption>().Should().ContainSingle().Which;
            option.Code.Should().Be(LengthB); option.Compatible.Should().BeTrue(); option.Price.Should().BeNull();
            dialog.CatalogGrid.SelectedItem = option;
            dialog.StageSelection().Should().BeTrue(); Row(dialog, "g1").Draft.Should().Be(LengthB);
            catalog.Prices.Should().NotContainKey(LengthB); dialog.ApprovedChoices.Should().BeNull();
        });
    }

    [Fact]
    public void DifferentUnitCannotBeStagedEvenWhenSelectedFromCatalogSearch()
    {
        WithDialog(new[] { Group() }, (dialog, _) =>
        {
            dialog.CatalogSearch.Text = AreaCode;
            SelectCode(dialog, AreaCode).Compatible.Should().BeFalse();
            dialog.StageButton.IsEnabled.Should().BeFalse(); dialog.StageSelection().Should().BeFalse();
            Row(dialog, "g1").Draft.Should().Be("—"); dialog.SaveButton.IsEnabled.Should().BeFalse();
        });
    }

    [Fact]
    public void TwoGroupBasketSurvivesChosenOnlyAndSearchFiltersAndLeavesThirdUnselected()
    {
        WithDialog(new[] { Group("g1"), Group("g2"), Group("g3") }, (dialog, _) =>
        {
            Stage(dialog, "g1", LengthA); Stage(dialog, "g2", LengthB);
            dialog.ChosenOnly.IsChecked = true;
            dialog.GroupsGrid.Items.Cast<Dialog.GroupRow>().Select(row => row.Subject.RuleKey).Should().Equal("g1", "g2");
            dialog.GroupSearch.Text = "g2"; dialog.GroupsGrid.Items.Count.Should().Be(1);
            dialog.GroupSearch.Text = "NO-MATCH"; dialog.GroupsGrid.Items.Count.Should().Be(0);
            dialog.GroupSearch.Text = ""; dialog.ChosenOnly.IsChecked = false;
            dialog.GroupsGrid.Items.Count.Should().Be(3);
            Row(dialog, "g1").Draft.Should().Be(LengthA); Row(dialog, "g2").Draft.Should().Be(LengthB);
            Row(dialog, "g3").Draft.Should().Be("—");
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue();
            dialog.ApprovedChoices!.Select(choice => choice.RuleKey).Should().Equal("g1", "g2");
            dialog.ApprovedChoices!.Select(choice => choice.CatalogCode).Should().Equal(LengthA, LengthB);
        });
    }

    [Fact]
    public void RemoveButtonDiscardsOnlyDraftAndClearsFinalConfirmation()
    {
        WithDialog(new[] { Group() }, (dialog, _) =>
        {
            Stage(dialog, "g1", LengthA);
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue();
            SelectGroup(dialog, "g1");
            dialog.RemoveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Row(dialog, "g1").Draft.Should().Be("—"); dialog.Confirm.IsChecked.Should().Be(false);
            dialog.ApprovedChoices.Should().BeNull(); dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.TryConfirm().Should().BeFalse();
        });
    }

    [Fact]
    public void ClosingWithStagedChoicesMakesNoApprovalAndLeavesInputsUnchanged()
    {
        RunSta(() =>
        {
            var catalog = Catalog(); var groups = new[] { Group() };
            var before = JsonSerializer.Serialize(new { groups, catalog });
            var dialog = new Dialog(groups, catalog);
            try
            {
                Stage(dialog, "g1", LengthB);
                dialog.ApprovedChoices.Should().BeNull();
            }
            finally { dialog.Close(); }
            dialog.ApprovedChoices.Should().BeNull();
            JsonSerializer.Serialize(new { groups, catalog }).Should().Be(before);
        });
    }

    [Fact]
    public void ClosedAlternativeRequiresSeparateCheckboxAndCannotStageBothSiblings()
    {
        WithDialog(new[]
        {
            Group("area", "m2", "perimeter", proposals: new[] { AreaCode }),
            Group("perimeter", "m", "area", proposals: new[] { LengthA }),
        }, (dialog, _) =>
        {
            SelectCode(dialog, AreaCode);
            dialog.ConfirmAlternative.Visibility.Should().Be(Visibility.Visible);
            dialog.StageSelection().Should().BeFalse();
            dialog.ConfirmAlternative.IsChecked = true;
            dialog.StageSelection().Should().BeTrue();
            SelectGroup(dialog, "perimeter"); SelectCode(dialog, LengthA);
            dialog.ConfirmAlternative.IsChecked = true;
            dialog.StageButton.IsEnabled.Should().BeFalse(); dialog.StageSelection().Should().BeFalse();
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue();
            var choice = dialog.ApprovedChoices.Should().ContainSingle().Which;
            choice.RuleKey.Should().Be("area"); choice.ExcludedAlternativeRuleKey.Should().Be("perimeter");
        });
    }

    [Fact]
    public void RevokingClosedAlternativeCheckboxRemovesItsDraftAndRequiresNewConfirmation()
    {
        WithDialog(new[] { Group("area", "m2", "perimeter", proposals: new[] { AreaCode }) }, (dialog, _) =>
        {
            SelectCode(dialog, AreaCode); dialog.ConfirmAlternative.IsChecked = true;
            dialog.StageSelection().Should().BeTrue();
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue();
            dialog.ConfirmAlternative.IsChecked = false;
            Row(dialog, "area").Draft.Should().Be("—"); dialog.Confirm.IsChecked.Should().Be(false);
            dialog.ApprovedChoices.Should().BeNull(); dialog.TryConfirm().Should().BeFalse();
        });
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("SYNTHETIC REVIEWER", false)]
    [InlineData("FIRST\nSECOND", true)]
    public void FinalSaveRequiresNamedSingleLineApproverAndExplicitConfirmation(string name, bool confirmed)
    {
        WithDialog(new[] { Group() }, (dialog, _) =>
        {
            Stage(dialog, "g1", LengthA);
            dialog.Approver.Text = name; dialog.Confirm.IsChecked = confirmed;
            dialog.SaveButton.IsEnabled.Should().BeFalse(); dialog.TryConfirm().Should().BeFalse();
            dialog.ApprovedChoices.Should().BeNull();
        });
    }

    [Fact]
    public void RestagingDifferentCodeRevokesAnEarlierConfirmation()
    {
        WithDialog(new[] { Group() }, (dialog, _) =>
        {
            Stage(dialog, "g1", LengthA);
            dialog.Approver.Text = " SYNTHETIC REVIEWER "; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue(); dialog.ApprovedBy.Should().Be("SYNTHETIC REVIEWER");
            Stage(dialog, "g1", LengthB);
            Row(dialog, "g1").Draft.Should().Be(LengthB);
            dialog.Confirm.IsChecked.Should().Be(false); dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.ApprovedChoices.Should().BeNull(); dialog.TryConfirm().Should().BeFalse();
            dialog.Confirm.IsChecked = true; dialog.TryConfirm().Should().BeTrue();
            dialog.ApprovedChoices.Should().ContainSingle().Which.CatalogCode.Should().Be(LengthB);
        });
    }

    [Fact]
    public void SelectionStagingMissingConfirmationAndCancellationNeverInvokeSaveCallback()
    {
        var calls = 0;
        WithDialog(new[] { Group(proposals: new[] { LengthA, LengthB }) }, (dialog, _) =>
        {
            SelectCode(dialog, LengthB); calls.Should().Be(0);
            dialog.StageSelection().Should().BeTrue(); calls.Should().Be(0);
            dialog.Approver.Text = "SYNTHETIC REVIEWER";
            dialog.TryConfirm().Should().BeFalse(); calls.Should().Be(0);
            dialog.ApprovedChoices.Should().BeNull();
        }, (_, _) => calls++);
        calls.Should().Be(0, "closing an unconfirmed dialog must not call the profile-saving callback");
    }

    [Fact]
    public void FailedSaveRetainsDraftAndConfirmationThenCorrectedRetryCanSucceed()
    {
        var attempts = new List<(string Code, string ApprovedBy)>();
        WithDialog(new[] { Group() }, (dialog, _) =>
        {
            Stage(dialog, "g1", LengthA);
            dialog.Approver.Text = " SYNTHETIC REVIEWER "; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeFalse();
            attempts.Should().Equal((LengthA, "SYNTHETIC REVIEWER"));
            Row(dialog, "g1").Draft.Should().Be(LengthA);
            dialog.Confirm.IsChecked.Should().Be(true); dialog.SaveButton.IsEnabled.Should().BeTrue();
            dialog.ApprovedChoices.Should().BeNull(); dialog.IsVisible.Should().BeFalse();

            Stage(dialog, "g1", LengthB);
            dialog.Confirm.IsChecked.Should().Be(false);
            dialog.TryConfirm().Should().BeFalse(); attempts.Should().HaveCount(1);
            dialog.Confirm.IsChecked = true; dialog.TryConfirm().Should().BeTrue();
            attempts.Should().Equal((LengthA, "SYNTHETIC REVIEWER"), (LengthB, "SYNTHETIC REVIEWER"));
            dialog.ApprovedChoices.Should().ContainSingle().Which.CatalogCode.Should().Be(LengthB);
        }, (choices, name) =>
        {
            attempts.Add((choices.Single().CatalogCode, name));
            if (attempts.Count == 1) throw new InvalidOperationException("SYNTHETIC refusal: choose the corrected code.");
        });
    }

    [Fact]
    public void RevokingFinalConfirmationInvalidatesApprovedChoicesAndCannotSaveAgain()
    {
        var calls = 0;
        WithDialog(new[] { Group() }, (dialog, _) =>
        {
            Stage(dialog, "g1", LengthA);
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeTrue(); calls.Should().Be(1);
            dialog.Confirm.IsChecked = false;
            dialog.ApprovedChoices.Should().BeNull(); dialog.SaveButton.IsEnabled.Should().BeFalse();
            dialog.TryConfirm().Should().BeFalse(); calls.Should().Be(1);
            Row(dialog, "g1").Draft.Should().Be(LengthA);
        }, (_, _) => calls++);
    }

    [Fact]
    public void ReadOnlyGroupCannotBeStagedEvenWithCompatibleCode()
    {
        WithDialog(new[] { Group(readOnly: "Synthetic coverage guard", proposals: new[] { LengthA }) }, (dialog, _) =>
        {
            SelectCode(dialog, LengthA).Compatible.Should().BeTrue();
            dialog.StageSelection().Should().BeFalse(); Row(dialog, "g1").Draft.Should().Be("—");
            dialog.Approver.Text = "SYNTHETIC REVIEWER"; dialog.Confirm.IsChecked = true;
            dialog.TryConfirm().Should().BeFalse();
        });
    }

    [Theory]
    [InlineData(1180, 780)]
    [InlineData(900, 620)]
    public void OffscreenActualContentKeepsTablesAndActionsInsideBothRequestedSizes(int width, int height)
    {
        var longSource = Group("long-source") with
        {
            Layer = "FIXTURE-ONLY / XREF / מקור-מקונן-עם-שם-ארוך-לבדיקת-גלישה / שכבת-תשתיות-ארוכה",
            FindingsSummary = string.Join("\n", Enumerable.Range(1, 7).Select(index =>
                $"{index}. ממצא מקור לבדיקה בלבד — המדידה והמחיר אינם מאושרים; פירוט ארוך נשמר לצורך סקירה.")),
        };
        var area = Group("area", "m2", "perimeter", proposals: new[] { AreaCode }) with
        {
            Layer = "FIXTURE-HW-CS-TABL / חלופת שטח של אותם פוליליינים סגורים",
            Quantity = 240, ObjectCount = 1,
            AlternativeQuantityDisplay = "128.00 מטר היקף — אותם עצמים; אין להחריג שורות פתוחות אחרות",
            FindingsSummary = longSource.FindingsSummary,
        };
        WithDialog(new[]
        {
            Group("staged"), area,
            Group("perimeter", "m", "area", proposals: new[] { LengthA }) with { Quantity = 128, ObjectCount = 1 },
            longSource, Group("still-unselected"),
        }, (dialog, _) =>
        {
            Stage(dialog, "staged", LengthB);
            SelectGroup(dialog, "area"); SelectCode(dialog, AreaCode);
            dialog.ConfirmAlternative.IsChecked = true;
            dialog.Approver.Text = "SYNTHETIC VISUAL REVIEWER";
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new System.Windows.Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();

            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(content)
                .TransformBounds(new Rect(new System.Windows.Point(0, 0), element.RenderSize));
            var bounds = new Dictionary<string, Rect>
            {
                ["GroupsGrid"] = Bounds(dialog.GroupsGrid), ["CatalogGrid"] = Bounds(dialog.CatalogGrid),
                ["GroupSearch"] = Bounds(dialog.GroupSearch), ["CatalogSearch"] = Bounds(dialog.CatalogSearch),
                ["StageButton"] = Bounds(dialog.StageButton), ["SaveButton"] = Bounds(dialog.SaveButton),
                ["ConfirmAlternative"] = Bounds(dialog.ConfirmAlternative), ["Confirm"] = Bounds(dialog.Confirm),
            };
            var evidence = new
            {
                evidence_kind = "actual compiled WPF content; unshown; synthetic input; not native CAD acceptance",
                width, height, content_width = content.ActualWidth, content_height = content.ActualHeight,
                bounds = bounds.ToDictionary(pair => pair.Key, pair => new
                {
                    x = pair.Value.X, y = pair.Value.Y, width = pair.Value.Width, height = pair.Value.Height,
                    right = pair.Value.Right, bottom = pair.Value.Bottom,
                }),
            };
            var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
            _output.WriteLine(json);
            var renderDirectory = Environment.GetEnvironmentVariable("MHD_MAPPING_REVIEW_RENDER_DIR");
            if (!string.IsNullOrWhiteSpace(renderDirectory))
            {
                var directory = Path.GetFullPath(renderDirectory);
                Directory.CreateDirectory(directory);
                var stem = $"manual-mapping-review-{width}x{height}-{Guid.NewGuid():N}";
                var pngPath = Path.Combine(directory, stem + ".png");
                var boundsPath = Path.Combine(directory, stem + ".bounds.json");
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen())
                    drawing.DrawRectangle(dialog.Background, null, new Rect(0, 0, width, height));
                image.Render(background); image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = new FileStream(pngPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
                File.WriteAllText(boundsPath, json);
                _output.WriteLine("Render: " + pngPath); _output.WriteLine("Bounds: " + boundsPath);
            }

            // Persist diagnostic evidence before asserting, so a clipped layout remains inspectable.
            dialog.CatalogGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(90);
            dialog.GroupsGrid.ActualHeight.Should().BeGreaterThanOrEqualTo(150);
            foreach (var name in new[] { "StageButton", "SaveButton" })
            {
                bounds[name].Height.Should().BeGreaterThan(0);
                bounds[name].Top.Should().BeGreaterThanOrEqualTo(0);
                bounds[name].Bottom.Should().BeLessThanOrEqualTo(content.ActualHeight + 0.5,
                    name + " must remain inside the measured content at the minimum size");
            }
            dialog.IsVisible.Should().BeFalse(); dialog.ApprovedChoices.Should().BeNull();
            Row(dialog, "staged").Draft.Should().Be(LengthB);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue("unshown dialog interaction must not start a modal loop");
        if (failure != null) throw new InvalidOperationException("Headless manual-mapping dialog test failed.", failure);
    }
}
