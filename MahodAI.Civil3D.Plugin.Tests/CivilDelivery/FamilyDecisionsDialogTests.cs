using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The family review: nothing is chosen for the engineer, one decision per family covers every checked group,
/// and the window keeps its controls reachable in a narrow size. Records and proposals are SYNTHETIC.
/// </summary>
public sealed class FamilyDecisionsDialogTests
{
    private const string Gm = "6422-GM-MODEL-NATAZ";
    private static int _handle;

    private static NeutralQuantityRecord Rec(string layer, string kind, string method, double value, string unit, string? block = null,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>();
        if (block != null) parameters["cad_block_name_effective"] = block;
        if (extra != null) foreach (var pair in extra) parameters[pair.Key] = pair.Value;
        var handle = Interlocked.Increment(ref _handle).ToString("X", CultureInfo.InvariantCulture);
        return new NeutralQuantityRecord
        {
            RecordId = $"fd-{handle}", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource { Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = handle, EntityType = "X",
                Layer = $"{Gm}|{layer}", Xref = Gm },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = parameters },
            Classification = new QuantityClassification(),
        };
    }

    /// <summary>
    /// ZZ1/ZZ2 → curb-road (cited legend), QQ3 → abstained with alternatives, MIX4 → only its first record proposed
    /// (a partial group), BB5 (count) → abstained.
    /// </summary>
    private sealed class ScriptedClassifier : IFamilyClassifier
    {
        public string Identity => "scripted/1";

        public IReadOnlyList<RecognitionProposal> Classify(RecognitionGroupInput group, EngineerBoqLibrary library, CatalogSnapshot? catalog)
        {
            var ids = group.Records.Select(r => r.RecordId).ToList();
            RecognitionProposal Proposed(IReadOnlyList<string> recordIds) => new(group.GroupId, recordIds, RecognitionStatus.Proposed, "curb-road",
                new[] { "U51.06.1900" }, new[] { new RecognitionEvidenceRef("ev_legend_row", recordIds) }, new[] { "שורת מקרא: אבן שפה לכביש" },
                new[] { "התאמה לאבן שפה לכביש" }, Array.Empty<RecognitionAlternative>(), Array.Empty<string>(), RecognitionProposal.OriginLocal);
            RecognitionProposal Abstained(IReadOnlyList<string> recordIds, params string[] alternatives) => new(group.GroupId, recordIds,
                RecognitionStatus.Abstained, null, Array.Empty<string>(), Array.Empty<RecognitionEvidenceRef>(), new[] { "צבע: צהוב" },
                Array.Empty<string>(), alternatives.Select(a => new RecognitionAlternative(a, "חלופה")).ToList(), new[] { "אין ראיה קריאה מלבד צבע" },
                RecognitionProposal.OriginLocal);
            return group.LayerLeaf switch
            {
                "ZZ1" or "ZZ2" => new[] { Proposed(ids) },
                "QQ3" => new[] { Abstained(ids, "curb-island", "curb-garden") },
                "MIX4" => new[] { Proposed(ids.Take(1).ToList()), Abstained(ids.Skip(1).ToList()) },
                _ => new[] { Abstained(ids) },
            };
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NearbyAndShape = new Dictionary<string, string>
    {
        ["ev_nearby_text"] = "[{\"text\":\"אבן גן\",\"distance_m\":0.4,\"handle\":\"1A\",\"source\":\"host\"}]",
        ["ev_nearby_text_status"] = "read",
        ["ev_geometry_sample"] = "{\"space\":\"host\",\"points\":[[184000,662000],[184020,662000],[184020,662003]]}",
        ["ev_geometry_sample_status"] = "read",
    };

    private static NeutralQuantityRecord[] Records() => new[]
    {
        Rec("ZZ1", "length", "polyline-length+xref-transform", 100, "מטר"),
        Rec("ZZ2", "length", "polyline-length+xref-transform", 40, "מטר"),
        Rec("QQ3", "length", "polyline-length+xref-transform", 20, "מטר", extra: NearbyAndShape),
        Rec("MIX4", "length", "polyline-length+xref-transform", 5, "מטר"),
        Rec("MIX4", "length", "polyline-length+xref-transform", 6, "מטר"),
        Rec("BB5", "count", "block-count+xref-transform", 1, "יח'", "UNKNOWN-BLOCK"),
    };

    private static EngineerBoqDraft Draft() => DraftOf(Records());

    /// <summary>The draft of <paramref name="records"/>, resolved against the profile's family decisions when given.</summary>
    private static EngineerBoqDraft DraftOf(IReadOnlyList<NeutralQuantityRecord> records,
        IReadOnlyList<ProjectProfile.EstimateProfile.FamilyDecision>? decisions = null)
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('b', 64) };
        return EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(), catalog, new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC", Array.Empty<string>(),
                FamilyDecisions: decisions, Classifier: new ScriptedClassifier()));
    }

    /// <summary>A hatch record with a read hatch pattern, read nearby text and colour, and unusable block/PropertySet evidence.</summary>
    private static NeutralQuantityRecord HatchRec(string layer, double value, string pattern, IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>
        {
            [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
            [EvidenceKeys.Hatch] = "{\"pattern\":\"" + pattern + "\",\"scale\":1,\"angle\":0,\"solid\":false,\"space\":\"source\"}",
            [EvidenceKeys.Hatch + EvidenceKeys.StatusSuffix] = "read",
            [EvidenceKeys.NearbyText] = "[{\"text\":\"מיסעה\",\"distance_m\":0.4,\"handle\":\"2A\",\"source\":\"host\"}]",
            [EvidenceKeys.NearbyText + EvidenceKeys.StatusSuffix] = "read",
            [EvidenceKeys.ColorEffective] = "{\"aci\":8}",
            [EvidenceKeys.ColorEffective + EvidenceKeys.StatusSuffix] = "read",
            [EvidenceKeys.BlockAttributes + EvidenceKeys.StatusSuffix] = "absent",
            [EvidenceKeys.PsetComponent + EvidenceKeys.StatusSuffix] = "unavailable:not-collected",
        };
        if (extra != null) foreach (var pair in extra) parameters[pair.Key] = pair.Value;
        return Rec(layer, "area", "hatch-area+xref-transform", value, "מ\"ר", extra: parameters);
    }

    private static void ClickOn(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Fact]
    public void NothingIsChosenForTheEngineerAndOneBatchCoversEveryCheckedGroupOfAFamily()
    {
        var model = FamilyDecisionReviewModel.Create(Draft());

        model.Rows.Should().HaveCount(6);
        model.Rows.Should().OnlyContain(r => !r.IsSelected);
        model.Rows.Where(r => r.ProposedFamily == null).Should().OnlyContain(r => r.ChosenFamily == null);
        model.ValidationError("x", "y", true).Should().Be("לא סומנו קבוצות לאישור");
        model.Rows.Where(r => r.Layer == "MIX4").Should().OnlyContain(r => r.IsPartialGroup && !r.CanSelect);

        model.SelectAllProposed();
        model.Rows.Where(r => r.IsSelected).Select(r => r.Layer).Should().BeEquivalentTo(new[] { "ZZ1", "ZZ2" });
        var batch = model.SelectedBatches().Should().ContainSingle().Subject;
        batch.FamilyId.Should().Be("curb-road");
        batch.Groups.Select(g => g.LayerLeaf).Should().BeEquivalentTo(new[] { "ZZ1", "ZZ2" });
        batch.EvidenceKeys.Should().Equal("ev_legend_row");
        batch.OverridesProposal.Should().BeFalse();

        var abstained = model.Rows.Single(r => r.Layer == "QQ3");
        abstained.IsSelected = true;
        model.ValidationError("x", "y", true).Should().Be("לכל קבוצה מסומנת צריך לבחור משפחה");
        abstained.FamilyOptions.Take(2).Select(o => o.FamilyId).Should().Equal("curb-island", "curb-garden");
        abstained.ChosenFamily = abstained.FamilyOptions[0];
        var batches = model.SelectedBatches();
        batches.Should().HaveCount(2);
        batches.Single(b => b.FamilyId == "curb-island").Should().Match<FamilyApprovalBatch>(b => b.OverridesProposal && b.EvidenceKeys.Count == 0);

        model.ValidationError("x", " ", true).Should().Be("יש לכתוב על מה מבוסס האישור");
        model.ValidationError(" ", "y", true).Should().Be("שם המאשר הוא שדה חובה");
        model.ValidationError("x", "y", false).Should().Be("יש לסמן שנבדקו הראיות של כל הקבוצות המסומנות");
        model.ValidationError("x", "y", true).Should().BeNull();
    }

    private static RecognitionProposal AiProposal(FamilyDecisionRow row, string family, IReadOnlyList<string>? recordIds = null) =>
        new(row.Group.GroupId, recordIds ?? row.Group.Records.Select(r => r.RecordId).ToList(), RecognitionStatus.Proposed, family,
            Array.Empty<string>(), new[]
            {
                new RecognitionEvidenceRef("ev_nearby_text", row.Group.Records.Select(r => r.RecordId).ToList()),
                new RecognitionEvidenceRef("engineer_context", Array.Empty<string>()),
            },
            new[] { "ev_nearby_text: אבן גן" }, new[] { "השערת עוזר בלבד", "הטקסט הסמוך הוא אבן גן" }, Array.Empty<RecognitionAlternative>(),
            Array.Empty<string>(), RecognitionProposal.OriginAi);

    [Fact]
    public void TheAssistantsFamilyIsShownButNeverChosenAndOnlyItsCadCitationsBackTheDecision()
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        var row = model.Rows.Single(r => r.Layer == "QQ3");
        row.CanAskAi.Should().BeTrue();
        model.Rows.Single(r => r.Layer == "ZZ1").CanAskAi.Should().BeFalse("a group the drawing evidence already decided is not sent");
        model.Rows.Where(r => r.Layer == "MIX4").Should().OnlyContain(r => !r.CanAskAi);

        row.ApplyAssistant(new[] { AiProposal(row, "curb-garden") }, id => id);
        row.AiProposal!.FamilyId.Should().Be("curb-garden");
        row.AiStatus.Should().Contain("הצעת עוזר");
        row.ChosenFamily.Should().BeNull("the assistant never chooses for the engineer");
        row.IsSelected.Should().BeFalse();

        row.ChosenFamily = row.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
        row.IsSelected = true;
        var batch = model.SelectedBatches().Should().ContainSingle().Subject;
        batch.FromAiSuggestion.Should().BeTrue();
        batch.EvidenceKeys.Should().Equal(new[] { "ev_nearby_text" }, "only evidence the records carry can bind the decision");

        row.ApplyAssistant(new[] { AiProposal(row, "curb-garden", new[] { row.Group.Records[0].RecordId, "foreign" }) }, id => id);
        row.AiProposal.Should().BeNull("a proposal for other records is not this group's");
        row.ApplyAssistant(new[] { AiProposal(row, "bike-racks") }, id => id);
        row.AiProposal.Should().BeNull("a family that is not offered for this measurement is ignored");
        row.AiStatus.Should().StartWith("העוזר לא הציע");
        // Batch V3 (Codex 20:38, handoff item 3): a choice made from an assistant proposal is bound to that exact
        // proposal; a newer answer withdraws it instead of silently turning it into a manual decision.
        model.SelectedBatches().Should().BeEmpty();
    }

    [Fact]
    public void TheAssistantIsAskedOnlyOnAClickAndAnImageTravelsOnlyWithAPermitForExactlyTheShownImage() => Sta(() =>
    {
        var calls = new List<(RecognitionGroupInput Group, string? Context, VisionPayload? Vision)>();
        FamilyDecisionsDialog.FamilyAssistant assistant = (group, local, context, vision, token) =>
        {
            calls.Add((group, context, vision));
            return Task.FromResult<IReadOnlyList<RecognitionProposal>>(Array.Empty<RecognitionProposal>());
        };
        var model = FamilyDecisionReviewModel.Create(Draft());
        var dialog = new FamilyDecisionsDialog(model, EngineerBoqLibrary.RoadsV1, assistant, visionEnabled: true);
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        calls.Should().BeEmpty();
        dialog.BtnAskAi.IsEnabled.Should().BeFalse("nothing is selected");
        var row = model.Rows.Single(r => r.Layer == "QQ3");
        dialog.RowsGrid.SelectedItem = row;
        dialog.BtnAskAi.IsEnabled.Should().BeTrue();
        dialog.AssistContext.Text = "אבני גן סביב ערוגות";
        dialog.BtnAskAi.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        calls.Should().ContainSingle();
        calls[0].Context.Should().Be("אבני גן סביב ערוגות");
        calls[0].Vision.Should().BeNull("the image was not offered");
        row.AiStatus.Should().Be("העוזר לא נדרש לקבוצה הזו.");

        ClickOn(dialog.BtnShowSchematic); // Vision: the image is shown explicitly before the consent can bind it
        dialog.SendPreview.IsChecked = true;
        dialog.PreviewImage.Visibility.Should().Be(Visibility.Visible);
        dialog.BtnAskAi.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        calls.Should().HaveCount(2);
        var vision = calls[1].Vision!;
        vision.Images.Should().ContainSingle();
        var image = vision.Images[0];
        vision.Permit!.ImageSha256.Should().Equal(image.Sha256);
        var prepared = FamilyRecognitionAssist.Prepare(row.Group, EngineerBoqLibrary.RoadsV1, "אבני גן סביב ערוגות", new[] { image });
        vision.Permit.ContextId.Should().Be(prepared.Request!.ContextId);
        VisionImagePolicy.IsPermitted(prepared.Request, vision).Should().BeTrue();

        dialog.RowsGrid.SelectedItem = model.Rows.Single(r => r.Layer == "ZZ1");
        dialog.BtnAskAi.IsEnabled.Should().BeFalse();
        dialog.Close();

        var offline = new FamilyDecisionsDialog(FamilyDecisionReviewModel.Create(Draft()), EngineerBoqLibrary.RoadsV1, null, visionEnabled: true);
        offline.RowsGrid.SelectedItem = offline.RowsGrid.Items.OfType<FamilyDecisionRow>().Single(r => r.Layer == "QQ3");
        offline.BtnAskAi.IsEnabled.Should().BeFalse();
        offline.Close();
    });

    [Fact]
    public void OnlyFamiliesWhoseMeasurementBasisFitsTheGroupAreOffered()
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        var count = model.Rows.Single(r => r.Layer == "BB5");
        var library = EngineerBoqLibrary.RoadsV1;
        count.FamilyOptions.Should().NotBeEmpty()
            .And.OnlyContain(o => library.Rules.Single(r => r.Id == o.FamilyId).Basis == DraftQuantityBasis.Count);
        var line = model.Rows.First(r => r.Layer == "ZZ1");
        line.FamilyOptions.Should().NotContain(o => library.Rules.Single(r => r.Id == o.FamilyId).Basis == DraftQuantityBasis.Count ||
                                                   library.Rules.Single(r => r.Id == o.FamilyId).Basis == DraftQuantityBasis.HatchArea);
        line.ChosenFamily = new FamilyOption("bike-racks", "invented");
        line.ChosenFamily!.FamilyId.Should().Be("curb-road", "an option outside the offered list is ignored");
    }

    [Fact]
    public void TheWindowUsesTheLibraryItsReviewWasBuiltWith() => Sta(() =>
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        model.Library.Should().BeSameAs(EngineerBoqLibrary.RoadsV1);
        new FamilyDecisionsDialog(model).Should().NotBeNull();
        var other = () => new FamilyDecisionsDialog(model, EngineerBoqLibrary.LandscapeV1, null, false);
        other.Should().Throw<InvalidOperationException>("one library per window: the rows were proposed against the model's library");
    });

    [Fact]
    public void TheDialogStartsDisabledEnablesOnlyAfterAnExplicitReviewAndStaysReachableWhenNarrow() => Sta(() =>
    {
        var dialog = new FamilyDecisionsDialog(FamilyDecisionReviewModel.Create(Draft()));
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        dialog.BtnSave.IsEnabled.Should().BeFalse();
        dialog.BtnSelectProposed.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        dialog.BtnSave.IsEnabled.Should().BeFalse();
        dialog.ReasonBox.Text = "שורת מקרא 'אבן שפה לכביש' בשתי השכבות";
        dialog.BtnSave.IsEnabled.Should().BeFalse();
        dialog.ConfirmBox.IsChecked = true;
        dialog.BtnSave.IsEnabled.Should().BeTrue();
        dialog.Batches.Should().ContainSingle(b => b.FamilyId == "curb-road" && b.Groups.Count == 2);
        dialog.ConfirmBox.IsChecked = false;
        dialog.BtnSave.IsEnabled.Should().BeFalse();
        dialog.ConfirmBox.IsChecked = true;
        Render(dialog, 1240, 760, "family-decisions-1240x760.png");
        Render(dialog, 940, 580, "family-decisions-940x580.png");
        dialog.Close();
    });

    [Fact]
    public void LayoutContractMatchesTheOtherDecisionDialogs() => Sta(() =>
    {
        var dialog = new FamilyDecisionsDialog(FamilyDecisionReviewModel.Create(Draft()));
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        foreach (var column in dialog.RowsGrid.Columns.Concat(dialog.SavedGrid.Columns))
        {
            column.MinWidth.Should().BeGreaterThan(0);
            if (column.Width.IsAbsolute) column.MinWidth.Should().BeGreaterThanOrEqualTo(column.Width.Value);
        }
        System.Windows.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(dialog.SavedGrid)
            .Should().Be(System.Windows.Controls.ScrollBarVisibility.Auto);
        System.Windows.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(dialog.RowsGrid)
            .Should().Be(System.Windows.Controls.ScrollBarVisibility.Auto);
        dialog.ConfirmBox.IsChecked.Should().BeFalse();
        ((System.Windows.Controls.TextBlock)dialog.ConfirmBox.Content).Text.Should().Contain("אינו מאשר סעיפים או מחירים");
        dialog.RowsGrid.Items.Groups.Should().HaveCount(3,
            "one group per proposed family (here only curb-road), the abstentions, and the mixed groups that cannot be approved here");
        dialog.ViewSwitch.Visibility.Should().Be(Visibility.Collapsed, "without saved decisions there is nothing to revoke");
        dialog.BtnRevoke.Visibility.Should().Be(Visibility.Collapsed);
        dialog.Close();
    });

    // ------------------------------------------------------------------ finding 31

    [Fact]
    public void SelectAllProposedNeverOverwritesAFamilyTheEngineerChose()
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        var zz1 = model.Rows.Single(r => r.Layer == "ZZ1");
        var zz2 = model.Rows.Single(r => r.Layer == "ZZ2");
        var garden = zz1.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
        zz1.ChosenFamily = garden;
        zz1.IsSelected = true;

        model.SelectAllProposed();

        zz1.ChosenFamily.Should().Be(garden, "the engineer's own choice is never reverted to the proposal");
        zz1.IsSelected.Should().BeTrue();
        zz2.IsSelected.Should().BeTrue();
        var batches = model.SelectedBatches();
        batches.Single(b => b.FamilyId == "curb-garden").Should().Match<FamilyApprovalBatch>(b =>
            b.OverridesProposal && b.Groups.Count == 1 && b.Groups[0].LayerLeaf == "ZZ1");
        batches.Single(b => b.FamilyId == "curb-road").Groups.Select(g => g.LayerLeaf).Should().Equal("ZZ2");

        var other = FamilyDecisionReviewModel.Create(Draft());
        var changed = other.Rows.Single(r => r.Layer == "ZZ1");
        changed.ChosenFamily = changed.FamilyOptions.Single(o => o.FamilyId == "curb-island");
        other.SelectAllProposed();
        changed.ChosenFamily!.FamilyId.Should().Be("curb-island");
        changed.IsSelected.Should().BeFalse("the shortcut checks proposals, not families the engineer changed");
        other.Rows.Single(r => r.Layer == "ZZ2").IsSelected.Should().BeTrue();
    }

    // ------------------------------------------------------------------ finding 9

    [Fact]
    public void AManualChoiceCitesTheSubjectEvidenceReadOnEveryRecordSoAChangedMeaningMakesTheDecisionStale()
    {
        var legendOnFirst = new Dictionary<string, string>
        {
            [EvidenceKeys.LegendRow] = "{\"text\":\"מיסעה\"}",
            [EvidenceKeys.LegendRow + EvidenceKeys.StatusSuffix] = "read",
        };
        var records = new[] { HatchRec("HH6", 100, "ANSI31", legendOnFirst), HatchRec("HH6", 40, "ANSI31") };
        var model = FamilyDecisionReviewModel.Create(DraftOf(records));
        var row = model.Rows.Single(r => r.Layer == "HH6");
        row.ProposedFamily.Should().BeNull("the synthetic classifier abstains on this layer");
        row.ChosenFamily = row.FamilyOptions.Single(o => o.FamilyId == "road-pavement");
        row.IsSelected = true;

        var batch = model.SelectedBatches().Should().ContainSingle().Subject;
        batch.OverridesProposal.Should().BeTrue();
        batch.EvidenceKeys.Should().Equal(new[] { EvidenceKeys.Hatch },
            "only subject evidence read on every record is cited: not nearby text, not colour, not a legend row on one record only");
        model.CitationNotice().Should().BeNull("the choice is bound to read CAD evidence");

        var library = EngineerBoqLibrary.RoadsV1;
        var decision = FamilyDecisionPolicy.CreateApproval(batch.FamilyId, batch.Groups, batch.EvidenceKeys, library, "nataly",
            "בחירה ידנית של מיסעה", DateTime.UtcNow);
        FamilyDecisionPolicy.Resolve(new[] { decision }, batch.Groups, library).Single().State.Should().Be(FamilyDecisionState.Applied);

        // A label placed near the hatch is not what the decision relied on: it keeps applying.
        foreach (var record in records)
            record.Measurement.Parameters[EvidenceKeys.NearbyText] = "[{\"text\":\"משהו אחר\",\"distance_m\":0.2,\"handle\":\"3C\",\"source\":\"host\"}]";
        FamilyDecisionPolicy.Resolve(new[] { decision }, batch.Groups, library).Single().State.Should().Be(FamilyDecisionState.Applied);

        // The same layer now holds another hatch pattern: the approved meaning changed, so the decision is stale.
        foreach (var record in records)
            record.Measurement.Parameters[EvidenceKeys.Hatch] = "{\"pattern\":\"AR-CONC\",\"scale\":1,\"angle\":0,\"solid\":false,\"space\":\"source\"}";
        var stale = FamilyDecisionPolicy.Resolve(new[] { decision }, batch.Groups, library).Single();
        stale.State.Should().Be(FamilyDecisionState.Stale);
        stale.StaleReason.Should().Be(FamilyDecisionPolicy.StaleEvidence);
    }

    // ------------------------------------------------------------------ findings 35 and 36

    [Fact]
    public void EachSavedDecisionCoversOneFamilyOneProvenanceAndOnlyTheKeysItsGroupsWereApprovedOn()
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        foreach (var row in model.Rows.Where(r => r.Layer is "ZZ1" or "ZZ2")) row.IsSelected = true;
        var qq3 = model.Rows.Single(r => r.Layer == "QQ3");
        qq3.ApplyAssistant(new[] { AiProposal(qq3, "curb-road") }, id => id);
        qq3.ChosenFamily = qq3.FamilyOptions.Single(o => o.FamilyId == "curb-road");
        qq3.IsSelected = true;

        var batches = model.SelectedBatches();
        batches.Should().HaveCount(2).And.OnlyContain(b => b.FamilyId == "curb-road");
        var kept = batches.Single(b => !b.FromAiSuggestion);
        kept.Groups.Select(g => g.LayerLeaf).Should().BeEquivalentTo(new[] { "ZZ1", "ZZ2" });
        kept.EvidenceKeys.Should().Equal(new[] { "ev_legend_row" }, "nearby text played no part in the kept proposals");
        kept.OverridesProposal.Should().BeFalse();
        var assisted = batches.Single(b => b.FromAiSuggestion);
        assisted.Groups.Select(g => g.LayerLeaf).Should().Equal("QQ3");
        assisted.EvidenceKeys.Should().Equal("ev_nearby_text");

        // A manual choice of the same family is its own decision, and it is flagged when it cites no CAD evidence.
        var manualModel = FamilyDecisionReviewModel.Create(Draft());
        manualModel.Rows.Single(r => r.Layer == "ZZ1").IsSelected = true;
        var manual = manualModel.Rows.Single(r => r.Layer == "QQ3");
        manual.ChosenFamily = manual.FamilyOptions.Single(o => o.FamilyId == "curb-road");
        manual.IsSelected = true;
        var split = manualModel.SelectedBatches();
        split.Should().HaveCount(2);
        split.Single(b => b.OverridesProposal).Should().Match<FamilyApprovalBatch>(b =>
            b.Groups.Count == 1 && b.Groups[0].LayerLeaf == "QQ3" && b.EvidenceKeys.Count == 0 && !b.FromAiSuggestion);
        split.Single(b => !b.OverridesProposal).EvidenceKeys.Should().Equal("ev_legend_row");
        manualModel.CitationNotice().Should().Contain("QQ3");
    }

    // ------------------------------------------------------------------ findings 32 and 37

    [Fact]
    public void AttestationAndImageConsentAreResetWhenTheCheckedSetAFamilyOrTheShownGroupChanges() => Sta(() =>
    {
        var visions = new List<VisionPayload?>();
        FamilyDecisionsDialog.FamilyAssistant assistant = (group, local, context, vision, token) =>
        {
            visions.Add(vision);
            return Task.FromResult<IReadOnlyList<RecognitionProposal>>(Array.Empty<RecognitionProposal>());
        };
        var model = FamilyDecisionReviewModel.Create(Draft());
        var dialog = new FamilyDecisionsDialog(model, EngineerBoqLibrary.RoadsV1, assistant, visionEnabled: true);
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        var zz1 = model.Rows.Single(r => r.Layer == "ZZ1");
        var zz2 = model.Rows.Single(r => r.Layer == "ZZ2");
        zz1.IsSelected = true;
        dialog.ReasonBox.Text = "שורת מקרא 'אבן שפה לכביש'";
        dialog.ConfirmBox.IsChecked = true;
        dialog.BtnSave.IsEnabled.Should().BeTrue();

        ClickOn(dialog.BtnSelectProposed);
        zz2.IsSelected.Should().BeTrue();
        dialog.ConfirmBox.IsChecked.Should().BeFalse("the attestation covered only the groups checked when it was given");
        dialog.BtnSave.IsEnabled.Should().BeFalse();

        dialog.ConfirmBox.IsChecked = true;
        zz1.ChosenFamily = zz1.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
        dialog.ConfirmBox.IsChecked.Should().BeFalse("a changed family needs a fresh attestation");

        dialog.ConfirmBox.IsChecked = true;
        zz2.IsSelected = false;
        dialog.ConfirmBox.IsChecked.Should().BeFalse("a changed checked set needs a fresh attestation");
        dialog.BtnSave.IsEnabled.Should().BeFalse();

        var qq3 = model.Rows.Single(r => r.Layer == "QQ3");
        dialog.RowsGrid.SelectedItem = qq3;
        ClickOn(dialog.BtnShowSchematic); // Vision: the image is shown explicitly before the consent can bind it
        dialog.SendPreview.IsChecked = true;
        dialog.PreviewImage.Visibility.Should().Be(Visibility.Visible);
        dialog.ConfirmBox.IsChecked = true;
        dialog.RowsGrid.SelectedItem = model.Rows.Single(r => r.Layer == "BB5");
        dialog.SendPreview.IsChecked.Should().BeFalse("the consent was given for the image of another group");
        dialog.PreviewImage.Visibility.Should().Be(Visibility.Collapsed);
        dialog.ConfirmBox.IsChecked.Should().BeFalse("the shown group changed");

        dialog.RowsGrid.SelectedItem = qq3;
        dialog.AssistContext.Text = "אבני גן סביב ערוגות";
        ClickOn(dialog.BtnShowSchematic); // Vision: the image is shown explicitly before the consent can bind it
        dialog.SendPreview.IsChecked = true;
        ClickOn(dialog.BtnAskAi);
        visions.Should().ContainSingle().Which.Should().NotBeNull("the image was sent with the consent given for it");
        dialog.SendPreview.IsChecked.Should().BeFalse("one consent covers one send; the next image needs a fresh one");
        dialog.Close();
    });

    // ------------------------------------------------------------------ finding 34

    [Fact]
    public void AMixedGroupIsNeverApprovableSaysWhyAndShowsNoChosenFamily()
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        var mixed = model.Rows.Where(r => r.Layer == "MIX4").ToList();
        mixed.Should().HaveCount(2).And.OnlyContain(r => r.IsPartialGroup && !r.CanSelect && !r.CanAskAi && r.ChosenFamily == null);
        mixed.Should().OnlyContain(r => r.BatchLabel.Contains("קבוצות מעורבות") && r.Missing.Contains("1 מתוך 2") &&
                                        r.Missing.Contains("אי אפשר לאשר"));
        var proposedPart = mixed.Single(r => r.ProposedFamily != null);
        proposedPart.ChosenFamily = proposedPart.FamilyOptions[0];
        proposedPart.ChosenFamily.Should().BeNull("nothing can be chosen for part of a group");
        proposedPart.IsSelected = true;
        proposedPart.IsSelected.Should().BeFalse();
        model.SelectAllProposed();
        mixed.Should().NotContain(r => r.IsSelected);
        model.SelectedBatches().Should().NotContain(b => b.Groups.Any(g => g.LayerLeaf == "MIX4"));
        model.Summary.Should().Contain("קבוצות מעורבות").And.Contain("2 עם הצעת משפחה");
    }

    // ------------------------------------------------------------------ findings 13 and 33

    [Fact]
    public void SavedDecisionsAreListedWithWhatTheyCoverAndOneCanBeRevokedOnlyWithAReasonAndAConfirmation() => Sta(() =>
    {
        var records = Records();
        var library = EngineerBoqLibrary.RoadsV1;
        var first = DraftOf(records);
        var zz2 = first.RecognitionGroups.Single(g => g.LayerLeaf == "ZZ2");
        var qq3 = first.RecognitionGroups.Single(g => g.LayerLeaf == "QQ3");
        var at = DateTime.UtcNow;
        var old = FamilyDecisionPolicy.CreateApproval("curb-island", new[] { zz2 }, Array.Empty<string>(), library, "nataly", "ישן", at.AddMinutes(-10));
        var history = FamilyDecisionPolicy.Revoke(new[] { old }, old.DecisionId!, "זוהה בטעות");
        var byLayer = FamilyDecisionPolicy.CreateApproval("curb-road", new[] { zz2 }, Array.Empty<string>(), library, "nataly",
            "מקרא אבן שפה", at.AddMinutes(-5));
        var byText = FamilyDecisionPolicy.CreateApproval("curb-garden", new[] { qq3 }, new[] { EvidenceKeys.NearbyText }, library, "nataly",
            "טקסט 'אבן גן' ליד הקו", at);
        // After the approval, the text near QQ3 changed.
        records.Single(r => r.Source.Layer == $"{Gm}|QQ3").Measurement.Parameters[EvidenceKeys.NearbyText] =
            "[{\"text\":\"אבן שפה\",\"distance_m\":0.4,\"handle\":\"1B\",\"source\":\"host\"}]";
        var decisions = history.Concat(new[] { byLayer, byText }).ToList();

        var model = FamilyDecisionReviewModel.Create(DraftOf(records, decisions), decisions);
        model.Rows.Should().NotContain(r => r.Layer == "ZZ2", "an applied decision consumes its group");
        model.SavedDecisions.Should().HaveCount(2, "a revoked decision is history, not a decision to revoke");
        var applied = model.SavedDecisions.Single(s => s.DecisionId == byLayer.DecisionId);
        applied.Should().Match<SavedFamilyDecision>(s => s.AppliedGroups == 1 && s.StaleGroups == 0 && s.State == "בתוקף" &&
                                                         s.Layers == "ZZ2" && s.Reason == "מקרא אבן שפה");
        applied.Evidence.Should().Contain("שכבה בלבד");
        applied.Approval.Should().StartWith("nataly");
        var stale = model.SavedDecisions.Single(s => s.DecisionId == byText.DecisionId);
        stale.StaleGroups.Should().Be(1);
        stale.State.Should().Contain("הראיות השתנו");
        stale.Evidence.Should().Be(EvidenceKeys.NearbyText);
        model.SavedDecisions[0].Should().Be(stale, "decisions that no longer hold are listed first");

        model.RevokeValidationError(null, "nataly", "טעות", true).Should().Be("לא נבחרה החלטה שמורה לביטול");
        model.RevokeValidationError(applied, "nataly", " ", true).Should().Be("יש לכתוב את סיבת הביטול");
        model.RevokeValidationError(applied, " ", "טעות", true).Should().Be("שם המאשר הוא שדה חובה");
        model.RevokeValidationError(applied, "nataly", "טעות", false).Should().Be("יש לאשר במפורש את ביטול ההחלטה המסומנת");
        model.RevokeValidationError(applied, "nataly", "טעות", true).Should().BeNull();

        var dialog = new FamilyDecisionsDialog(model);
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        dialog.ShowingSaved.Should().BeFalse("groups to approve exist, so the approval view opens first");
        dialog.ViewSwitch.Visibility.Should().Be(Visibility.Visible);
        dialog.SavedGrid.Items.Count.Should().Be(2);
        ClickOn(dialog.BtnShowSaved);
        dialog.ShowingSaved.Should().BeTrue();
        dialog.BtnRevoke.Visibility.Should().Be(Visibility.Visible);
        dialog.BtnSave.Visibility.Should().Be(Visibility.Collapsed);
        dialog.BtnRevoke.IsEnabled.Should().BeFalse("nothing is selected");

        dialog.SavedGrid.SelectedItem = applied;
        dialog.RevokeReasonBox.Text = "הקבוצה היא אבן גן, לא אבן שפה לכביש";
        dialog.BtnRevoke.IsEnabled.Should().BeFalse("the engineer has not confirmed");
        dialog.RevokeConfirmBox.IsChecked = true;
        dialog.BtnRevoke.IsEnabled.Should().BeTrue();
        dialog.RevokeTarget.Should().Be(applied);
        dialog.RevokeReason.Should().Be("הקבוצה היא אבן גן, לא אבן שפה לכביש");

        var row = model.Rows.First(r => r.CanSelect);
        row.IsSelected = true;
        dialog.BtnRevoke.IsEnabled.Should().BeFalse("one action per close: approve or revoke");
        dialog.RevokeValidationText.Text.Should().Contain("פעולה אחת בכל פעם");
        row.IsSelected = false;
        dialog.BtnRevoke.IsEnabled.Should().BeTrue();

        dialog.SavedGrid.SelectedItem = stale;
        dialog.RevokeConfirmBox.IsChecked.Should().BeFalse("the confirmation was given for another decision");
        dialog.BtnRevoke.IsEnabled.Should().BeFalse();

        Render(dialog, 940, 580, "family-decisions-saved-940x580.png",
            dialog.SavedGrid, dialog.RevokeReasonBox, dialog.ApproverBox, dialog.RevokeConfirmBox, dialog.BtnRevoke, dialog.BtnCancel);
        ClickOn(dialog.BtnShowRows);
        dialog.ShowingSaved.Should().BeFalse();
        Render(dialog, 940, 580, "family-decisions-with-saved-940x580.png");
        dialog.Close();
    });

    [Fact]
    public void SameTimeDecisionsOfDifferentFamiliesAreBothShownAsConflictNotAsInForce()
    {
        var records = Records();
        var library = EngineerBoqLibrary.RoadsV1;
        var first = DraftOf(records);
        var zz2 = first.RecognitionGroups.Single(g => g.LayerLeaf == "ZZ2");
        var zz1 = first.RecognitionGroups.Single(g => g.LayerLeaf == "ZZ1");
        var at = DateTime.UtcNow;
        var road = FamilyDecisionPolicy.CreateApproval("curb-road", new[] { zz2 }, Array.Empty<string>(), library, "nataly", "א", at);
        var garden = FamilyDecisionPolicy.CreateApproval("curb-garden", new[] { zz2 }, Array.Empty<string>(), library, "igor", "ב", at);
        // Control: the scripted local recognition proposes curb-road on ZZ1, so this decision holds there.
        var unrelated = FamilyDecisionPolicy.CreateApproval("curb-road", new[] { zz1 }, Array.Empty<string>(), library, "nataly", "ג", at);
        var decisions = new[] { road, garden, unrelated };

        var draft = DraftOf(records, decisions);
        draft.FamilyResolutions.Should().Contain(r => r.GroupId == zz2.GroupId && r.State == FamilyDecisionState.Stale &&
                                                      r.StaleReason == FamilyDecisionPolicy.StaleConflict && r.DecisionId == null);
        var model = FamilyDecisionReviewModel.Create(draft, decisions);
        foreach (var id in new[] { road.DecisionId, garden.DecisionId })
            model.SavedDecisions.Single(s => s.DecisionId == id).Should().Match<SavedFamilyDecision>(s =>
                s.AppliedGroups == 0 && s.StaleGroups == 1 && s.State.Contains("שתי החלטות סותרות"),
                "the joint resolution is blocked: neither decision is in force on ZZ2");
        model.SavedDecisions.Single(s => s.DecisionId == unrelated.DecisionId).Should().Match<SavedFamilyDecision>(s =>
            s.AppliedGroups == 1 && s.StaleGroups == 0 && s.State == "בתוקף", "an unrelated decision is unaffected");
    }

    [Fact]
    public void AnOlderDecisionALaterOneTookOverIsNotShownAsInForce()
    {
        var records = Records();
        var library = EngineerBoqLibrary.RoadsV1;
        var zz2 = DraftOf(records).RecognitionGroups.Single(g => g.LayerLeaf == "ZZ2");
        var at = DateTime.UtcNow;
        // The later decision agrees with the scripted local recognition (curb-road on ZZ2), so it holds.
        var older = FamilyDecisionPolicy.CreateApproval("curb-garden", new[] { zz2 }, Array.Empty<string>(), library, "nataly", "א", at.AddMinutes(-5));
        var later = FamilyDecisionPolicy.CreateApproval("curb-road", new[] { zz2 }, Array.Empty<string>(), library, "nataly", "ב", at);
        var decisions = new[] { older, later };

        var model = FamilyDecisionReviewModel.Create(DraftOf(records, decisions), decisions);
        model.SavedDecisions.Single(s => s.DecisionId == later.DecisionId).Should().Match<SavedFamilyDecision>(s =>
            s.AppliedGroups == 1 && s.State == "בתוקף");
        model.SavedDecisions.Single(s => s.DecisionId == older.DecisionId).Should().Match<SavedFamilyDecision>(s =>
            s.AppliedGroups == 0 && s.StaleGroups == 1 && s.State.Contains("החלטה מאוחרת יותר"),
            "the older decision still addresses ZZ2 but no longer applies there");
    }

    [Fact]
    public void AReviewWithOnlySavedDecisionsOpensOnTheRevokeViewInsteadOfADeadEnd() => Sta(() =>
    {
        var records = new[] { Rec("ZZ2", "length", "polyline-length+xref-transform", 40, "מטר") };
        var library = EngineerBoqLibrary.RoadsV1;
        var group = DraftOf(records).RecognitionGroups.Single();
        var decision = FamilyDecisionPolicy.CreateApproval("curb-road", new[] { group }, Array.Empty<string>(), library, "nataly",
            "מקרא אבן שפה", DateTime.UtcNow);
        var decisions = new[] { decision };

        var model = FamilyDecisionReviewModel.Create(DraftOf(records, decisions), decisions);
        model.Rows.Should().BeEmpty();
        model.SavedDecisions.Should().ContainSingle().Which.AppliedGroups.Should().Be(1);

        var dialog = new FamilyDecisionsDialog(model);
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        dialog.ShowingSaved.Should().BeTrue();
        dialog.BtnShowRows.IsEnabled.Should().BeFalse();
        dialog.BtnSave.Visibility.Should().Be(Visibility.Collapsed);
        dialog.BtnRevoke.Visibility.Should().Be(Visibility.Visible);
        dialog.Close();
    });

    // ------------------------------------------------------------------ finding 34: semantic partition approval

    [Fact]
    public void AProvenPartitionOfAMixedGroupIsSelectableWithItsScopeShownAndTheRestStaysUnapprovableWithTheReason()
    {
        var records = PartitionApprovalFixture.Records();
        var model = PartitionApprovalFixture.Review(records, PartitionApprovalFixture.UnrelatedDecision(records));
        model.Rows.Should().HaveCount(2, "C1 is covered by the earlier decision; S1,S2,U1 and U2 are the two parts of one group");

        var partition = model.Rows.Single(r => r.IsPartition);
        partition.Group.Records.Select(r => r.RecordId).Should().BeEquivalentTo(PartitionApprovalFixture.Proven);
        partition.IsPartialGroup.Should().BeTrue();
        partition.WholeGroup.Records.Should().HaveCount(4);
        partition.CanSelect.Should().BeTrue("the local classifier proves exactly these records as one partition");
        partition.CanAskAi.Should().BeFalse();
        partition.IsSelected.Should().BeFalse("nothing is checked for the engineer");
        partition.ChosenFamily!.FamilyId.Should().Be(PartitionApprovalFixture.Family);
        partition.FamilyOptions.Should().ContainSingle("a proven partition is approved as the proven family only");
        partition.ChosenFamily = new FamilyOption("curb-road", "invented");
        partition.ChosenFamily!.FamilyId.Should().Be(PartitionApprovalFixture.Family);
        partition.BatchLabel.Should().Contain("תת־קבוצה מוכחת");
        partition.ScopeText.Should().Contain("3 מתוך 4").And.Contain(PartitionApprovalFixture.Layer)
            .And.Contain(PartitionApprovalFixture.Source).And.Contain(EvidenceKeys.BlockAttributes + "=BUS SHELTER")
            .And.Contain("עצמים חדשים").And.Contain("אינם מאושרים");

        var residual = model.Rows.Single(r => !r.IsPartition);
        residual.Group.Records.Select(r => r.RecordId).Should().Equal("TEST-U2");
        residual.IsPartialGroup.Should().BeTrue();
        residual.CanSelect.Should().BeFalse();
        residual.ScopeText.Should().BeEmpty();
        residual.Missing.Should().Contain("1 מתוך 4").And.Contain("אי אפשר לאשר");
        residual.IsSelected = true;
        residual.IsSelected.Should().BeFalse();

        model.SelectAllProposed();
        partition.IsSelected.Should().BeFalse("a partition rule is checked explicitly, never by the shortcut");
        model.SelectedBatches().Should().BeEmpty();
        model.PartitionNotice().Should().BeNull();
        model.Summary.Should().Contain("תת־קבוצות מוכחות").And.Contain("קבוצות מעורבות");

        partition.IsSelected = true;
        var batch = model.SelectedBatches().Should().ContainSingle().Subject;
        batch.FamilyId.Should().Be(PartitionApprovalFixture.Family);
        batch.OverridesProposal.Should().BeFalse();
        batch.FromAiSuggestion.Should().BeFalse();
        batch.EvidenceKeys.Should().Contain(EvidenceKeys.BlockAttributes)
            .And.BeEquivalentTo(partition.PartitionMatches!.Select(m => m.Key!).Distinct(), "a partition cites exactly the evidence that singles it out");
        batch.Partition!.WholeGroupId.Should().Be(partition.WholeGroup.GroupId);
        batch.Partition.RecordIds.Should().Equal(PartitionApprovalFixture.Proven);
        batch.Partition.WholeGroupRecords.Should().Be(4);
        model.CitationNotice().Should().BeNull("the partition cites the evidence that singles it out");
        model.PartitionNotice().Should().Contain("עצמים חדשים");
        model.ValidationError("x", "y", true).Should().BeNull();
    }

    [Fact]
    public void AnUnprovenPartOfAGroupStaysUnselectableEvenWhenTheDraftClassifierProposedAFamily()
    {
        // The scripted draft proposes curb-road for the first MIX4 record, but the local classifier proves no partition there.
        var model = FamilyDecisionReviewModel.Create(Draft());
        var proposedPart = model.Rows.Single(r => r.Layer == "MIX4" && r.ProposedFamily != null);
        proposedPart.IsPartition.Should().BeFalse();
        proposedPart.CanSelect.Should().BeFalse();
        proposedPart.PartitionMatches.Should().BeNull();
        proposedPart.Missing.Should().Contain("אין כאן תת־קבוצה מוכחת");
        model.Rows.Should().NotContain(r => r.IsPartition);
    }

    [Fact]
    public void TheSelectedRowsEvidenceMissingDetailsAlternativesAndAssistantAreShownBesideTheTable() => Sta(() =>
    {
        var model = FamilyDecisionReviewModel.Create(Draft());
        var dialog = new FamilyDecisionsDialog(model);
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        dialog.RowDetailTitle.Text.Should().Contain("יש לבחור שורה");
        dialog.RowDetailPanel.HorizontalScrollBarVisibility.Should().Be(System.Windows.Controls.ScrollBarVisibility.Disabled);

        var qq3 = model.Rows.Single(r => r.Layer == "QQ3");
        dialog.RowsGrid.SelectedItem = qq3;
        dialog.RowDetailTitle.Text.Should().Contain("QQ3");
        dialog.RowDetailAlternatives.Text.Should().Contain("חלופה");
        dialog.RowDetailMissing.Text.Should().Contain("אין ראיה קריאה מלבד צבע");
        dialog.RowDetailEvidence.Text.Should().Contain("צבע: צהוב");
        dialog.RowDetailAssistant.Text.Should().Be("—");
        qq3.ApplyAssistant(new[] { AiProposal(qq3, "curb-garden") }, id => id);
        dialog.RowDetailAssistant.Text.Should().Contain("הצעת עוזר", "the assistant's answer for the shown row appears at once");
        qq3.IsSelected.Should().BeFalse("showing a row never checks it");

        var mixed = model.Rows.First(r => r.Layer == "MIX4");
        dialog.RowsGrid.SelectedItem = mixed;
        dialog.RowDetailState.Text.Should().Contain("אי אפשר לאשר");
        dialog.RowDetailMissing.Text.Should().Contain("1 מתוך 2");
        dialog.RowDetailScope.Text.Should().BeEmpty();
        dialog.Close();
    });

    [Fact]
    public void ThePartitionRowsDetailsAndScopeAreReadableWithoutHorizontalScrollingAt940x580And1240x760() => Sta(() =>
    {
        var records = PartitionApprovalFixture.Records();
        // The unrelated earlier decision on C1 is listed too, so the saved view (and its switch) is part of the layout.
        var model = PartitionApprovalFixture.Review(records, PartitionApprovalFixture.UnrelatedDecision(records));
        model.SavedDecisions.Should().ContainSingle();
        var dialog = new FamilyDecisionsDialog(model);
        dialog.ApproverBox.Text = "SYNTHETIC TEST REVIEWER"; // b24: typed, never prefilled from Windows
        var partition = model.Rows.Single(r => r.IsPartition);
        dialog.RowsGrid.SelectedItem = partition;
        partition.IsSelected = true;
        dialog.ReasonBox.Text = "TEST ONLY: BUS SHELTER on three objects";
        dialog.ConfirmBox.IsChecked = true;
        dialog.BtnSave.IsEnabled.Should().BeTrue("several rows or one partition are approved in one action");
        dialog.RowDetailScope.Text.Should().Be(partition.ScopeText);
        dialog.RowDetailState.Text.Should().Contain("תת־קבוצה מוכחת");
        dialog.ValidationText.Text.Should().Contain("עצמים חדשים", "the rule's scope is repeated before the attestation");

        foreach (var (width, height) in new[] { (1240, 760), (940, 580) })
        {
            Render(dialog, width, height, $"family-decisions-partition-{width}x{height}.png",
                dialog.RowsGrid, dialog.RowDetailPanel, dialog.BtnSave, dialog.BtnCancel, dialog.ReasonBox, dialog.ApproverBox, dialog.ConfirmBox);
            AssertRowDetailReadable(dialog, width, height);
        }

        // The saved view gives its table the whole width again.
        ClickOn(dialog.BtnShowSaved);
        dialog.ShowingSaved.Should().BeTrue();
        Render(dialog, 940, 580, "family-decisions-partition-saved-940x580.png",
            dialog.SavedGrid, dialog.RevokeReasonBox, dialog.ApproverBox, dialog.BtnRevoke, dialog.BtnCancel);
        LaidOut(dialog, 940, 580, _ => dialog.SavedGrid.ActualWidth.Should().BeGreaterThan(880,
            "the row details are hidden with their column in the saved view"));
        dialog.Close();
    });

    /// <summary>Lays the dialog's content out offscreen at the size (as <see cref="Render"/> does), runs the check, and restores it.</summary>
    private static void LaidOut(FamilyDecisionsDialog dialog, int width, int height, Action<FrameworkElement> check)
    {
        var root = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        root.FlowDirection = dialog.FlowDirection;
        var frame = new System.Windows.Controls.Border { Background = dialog.Background, Child = root, FlowDirection = System.Windows.FlowDirection.LeftToRight };
        frame.Measure(new System.Windows.Size(width, height));
        frame.Arrange(new Rect(0, 0, width, height));
        frame.UpdateLayout();
        try { check(frame); }
        finally
        {
            frame.Child = null;
            dialog.Content = root;
        }
    }

    /// <summary>
    /// Lays the dialog out at the size and checks that the row details neither scroll nor overflow horizontally, have a usable
    /// width and height, and start (with a partition's scope) inside the visible part of the panel.
    /// </summary>
    private static void AssertRowDetailReadable(FamilyDecisionsDialog dialog, int width, int height) => LaidOut(dialog, width, height, frame =>
    {
        var panel = dialog.RowDetailPanel;
        var bounds = panel.TransformToAncestor(frame).TransformBounds(new Rect(panel.RenderSize));
        bounds.Left.Should().BeGreaterThanOrEqualTo(0, $"{width}x{height}");
        bounds.Right.Should().BeLessThanOrEqualTo(width, $"{width}x{height}");
        bounds.Bottom.Should().BeLessThanOrEqualTo(height, $"{width}x{height}");
        panel.ActualWidth.Should().BeGreaterThanOrEqualTo(270, $"the details keep a readable width at {width}x{height}");
        panel.ViewportHeight.Should().BeGreaterThanOrEqualTo(200, $"the details keep a readable height at {width}x{height}");
        panel.ScrollableWidth.Should().BeLessThanOrEqualTo(0.5, $"nothing in the details scrolls horizontally at {width}x{height}");
        foreach (var text in new[] { dialog.RowDetailTitle, dialog.RowDetailState, dialog.RowDetailScope, dialog.RowDetailMissing,
                     dialog.RowDetailEvidence, dialog.RowDetailAlternatives, dialog.RowDetailAssistant })
            text.ActualWidth.Should().BeLessThanOrEqualTo(panel.ViewportWidth + 0.5, $"{width}x{height}");
        var scopeTop = dialog.RowDetailScope.TransformToAncestor(panel).Transform(new System.Windows.Point(0, 0)).Y;
        scopeTop.Should().BeGreaterThanOrEqualTo(0);
        (scopeTop + 60).Should().BeLessThanOrEqualTo(panel.ViewportHeight, $"the partition's scope starts in view at {width}x{height}");
    });

    private static void Render(FamilyDecisionsDialog dialog, int width, int height, string name, params FrameworkElement[] controls)
    {
        if (controls.Length == 0)
            controls = new FrameworkElement[] { dialog.BtnSave, dialog.ReasonBox, dialog.ApproverBox, dialog.ConfirmBox, dialog.RowsGrid, dialog.BtnSelectProposed };
        var root = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        root.FlowDirection = dialog.FlowDirection;
        System.Windows.Documents.TextElement.SetFontFamily(root, dialog.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, dialog.FontSize);
        System.Windows.Documents.TextElement.SetForeground(root, dialog.Foreground);
        var frame = new System.Windows.Controls.Border { Background = dialog.Background, Child = root, FlowDirection = System.Windows.FlowDirection.LeftToRight };
        frame.Measure(new System.Windows.Size(width, height));
        frame.Arrange(new Rect(0, 0, width, height));
        frame.UpdateLayout();
        foreach (var control in controls)
        {
            var bounds = control.TransformToAncestor(frame).TransformBounds(new Rect(control.RenderSize));
            bounds.Width.Should().BeGreaterThan(0, control.Name);
            bounds.Left.Should().BeGreaterThanOrEqualTo(0, control.Name);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0, control.Name);
            bounds.Right.Should().BeLessThanOrEqualTo(width, control.Name);
            bounds.Bottom.Should().BeLessThanOrEqualTo(height, control.Name);
        }
        frame.Child = null;
        dialog.Content = root;
        var output = Environment.GetEnvironmentVariable("MHD_PALETTE_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var host = new System.Windows.Controls.Border { Background = dialog.Background, FlowDirection = System.Windows.FlowDirection.LeftToRight };
        dialog.Content = null;
        host.Child = root;
        host.Measure(new System.Windows.Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(Path.Combine(output, name), FileMode.Create)) encoder.Save(stream);
        host.Child = null;
        dialog.Content = root;
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
