using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The image of the family assistant (vision path): chosen with an explicit button, normalised in memory, shown before the
/// consent, bound to the group and description, sent once as exactly the shown bytes, and never mixed up with a replaced
/// image. The assistant is a scripted delegate; records, images and answers are SYNTHETIC. No clipboard, provider or
/// Civil is touched: the clipboard reader is injected.
/// </summary>
public sealed class FamilyDecisionsDialogVisionTests
{
    private const string Gm = "6422-GM-MODEL-NATAZ";
    private const string CanaryQuery = "/tEXt/{str=Comment}";
    private static readonly string[] AllowedChunks = { "IHDR", "IDAT", "IEND", "pHYs", "sRGB", "gAMA", "cHRM" };
    private static int _handle;

    /// <summary>Abstains on every group, so every whole group can be asked about.</summary>
    private sealed class AbstainingClassifier : IFamilyClassifier
    {
        public string Identity => "vision-tests-abstain/1";

        public IReadOnlyList<RecognitionProposal> Classify(RecognitionGroupInput group, EngineerBoqLibrary library, CatalogSnapshot? catalog) =>
            new[]
            {
                new RecognitionProposal(group.GroupId, group.Records.Select(r => r.RecordId).ToList(), RecognitionStatus.Abstained, null,
                    Array.Empty<string>(), Array.Empty<RecognitionEvidenceRef>(), Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<RecognitionAlternative>(), new[] { "אין ראיה מכריעה" }, RecognitionProposal.OriginLocal),
            };
    }

    /// <summary>Records every question. <see cref="Next"/> makes the next answer wait; cancellation is deliberately ignored.</summary>
    private sealed class ScriptedAssistant
    {
        public readonly List<(RecognitionGroupInput Group, string? Context, VisionPayload? Vision, CancellationToken Token)> Calls = new();
        public TaskCompletionSource<IReadOnlyList<RecognitionProposal>>? Next;
        public Func<RecognitionGroupInput, VisionPayload?, IReadOnlyList<RecognitionProposal>> Answer = (_, _) => Array.Empty<RecognitionProposal>();

        public Task<IReadOnlyList<RecognitionProposal>> Ask(RecognitionGroupInput group, IReadOnlyList<RecognitionProposal> local,
            string? context, VisionPayload? vision, CancellationToken token)
        {
            Calls.Add((group, context, vision, token));
            var pending = Next;
            Next = null;
            return pending?.Task ?? Task.FromResult(Answer(group, vision));
        }
    }

    private static NeutralQuantityRecord VisionRec(string layer)
    {
        var handle = Interlocked.Increment(ref _handle).ToString("X", CultureInfo.InvariantCulture);
        return new NeutralQuantityRecord
        {
            RecordId = $"fv-{handle}", ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource { Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = handle, EntityType = "X",
                Layer = $"{Gm}|{layer}", Xref = Gm },
            Measurement = new QuantityMeasurement
            {
                Kind = "length", Method = "polyline-length+xref-transform", RawValue = 20, Unit = "מטר",
                Parameters = new Dictionary<string, string>
                {
                    [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
                    [EvidenceKeys.NearbyText] = "[{\"text\":\"אבן גן\",\"distance_m\":0.4,\"handle\":\"1A\",\"source\":\"host\"}]",
                    [EvidenceKeys.NearbyText + EvidenceKeys.StatusSuffix] = "read",
                    [EvidenceKeys.GeometrySample] = "{\"space\":\"host\",\"points\":[[184000,662000],[184020,662000],[184020,662003]]}",
                    [EvidenceKeys.GeometrySample + EvidenceKeys.StatusSuffix] = "read",
                },
            },
            Classification = new QuantityClassification(),
        };
    }

    private static FamilyDecisionReviewModel Model(params string[] layers)
    {
        var catalog = new CatalogSnapshot { SnapshotId = "SYNTHETIC", FileHash = new string('b', 64) };
        var draft = EngineerBoqDraftBuilder.Build(layers.Select(VisionRec).ToList(), Array.Empty<DeliveryFinding>(), catalog,
            new Dictionary<string, string>(), EngineerBoqLibrary.RoadsV1,
            new EngineerDraftContext("SYNTHETIC", "SYNTHETIC", "SYNTHETIC-RUN", "synthetic.dwg", "SYNTHETIC", Array.Empty<string>(),
                Classifier: new AbstainingClassifier()));
        return FamilyDecisionReviewModel.Create(draft);
    }

    private static FamilyDecisionsDialog Dialog(FamilyDecisionReviewModel model, ScriptedAssistant assistant)
    {
        var dialog = new FamilyDecisionsDialog(model, EngineerBoqLibrary.RoadsV1, assistant.Ask, visionEnabled: true);
        dialog.ApproverBox.Text = "synthetic-engineer";
        return dialog;
    }

    /// <summary>An assistant answer for the whole group, carrying the request context the way FamilyRecognitionAssist does.</summary>
    private static RecognitionProposal Suggest(RecognitionGroupInput group, string family, string contextId) =>
        new(group.GroupId, group.Records.Select(r => r.RecordId).ToList(), RecognitionStatus.Proposed, family, Array.Empty<string>(),
            new[] { new RecognitionEvidenceRef(EvidenceKeys.NearbyText, group.Records.Select(r => r.RecordId).ToList()) },
            new[] { "ev_nearby_text: \"אבן גן\"" }, new[] { "השערת עוזר בלבד", "ai_context=" + contextId },
            Array.Empty<RecognitionAlternative>(), Array.Empty<string>(), RecognitionProposal.OriginAi);

    /// <summary>A 600×300 solid-colour frame that carries a PNG text metadata canary (as a pasted bitmap may).</summary>
    private static BitmapFrame LegendFrame(out string canary)
    {
        const int width = 600, height = 300;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 17; pixels[i + 1] = 83; pixels[i + 2] = 191; pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        canary = "SYNTHETIC_PRIVATE_METADATA_NOT_PIXELS";
        var metadata = new BitmapMetadata("png");
        metadata.SetQuery(CanaryQuery, canary);
        return BitmapFrame.Create(source, null, metadata, null);
    }

    private static List<string> Chunks(byte[] png)
    {
        var chunks = new List<string>();
        for (var offset = 8; offset + 8 <= png.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            chunks.Add(Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += length + 12;
        }
        return chunks;
    }

    private static void ClickOn(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>Runs whatever the dispatcher queued (an awaited answer may continue there).</summary>
    private static void Pump() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

    [Fact]
    public void TheNormalizerMakesABoundedMetadataFreePngInMemoryWithTheSamePixels() => Sta(() =>
    {
        var frame = LegendFrame(out var canary);
        ((BitmapMetadata)frame.Metadata).GetQuery(CanaryQuery).Should().Be(canary, "the input really carries metadata");

        var image = VisionImageNormalizer.TryNormalize(frame, VisionImagePolicy.LegendCrop, out var error);

        error.Should().BeNull();
        image.Should().NotBeNull();
        image!.Kind.Should().Be(VisionImagePolicy.LegendCrop);
        var png = image.ToArray();
        var check = VisionImagePolicy.Validate(png);
        check.IsValid.Should().BeTrue(check.Reason);
        check.Width.Should().Be(512);
        check.Height.Should().Be(256);
        png.Length.Should().BeLessThanOrEqualTo(VisionImagePolicy.MaxBytes);
        image.Sha256.Should().Be(VisionImagePolicy.Sha256(png));
        Encoding.UTF8.GetString(png).Should().NotContain(canary);
        var chunks = Chunks(png);
        chunks.Should().NotBeEmpty();
        chunks.Should().BeSubsetOf(AllowedChunks, "no text, EXIF, time, profile or private chunk survives");

        using var stream = new MemoryStream(png, writable: false);
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        decoded.Frames.Should().ContainSingle();
        var decodedFrame = decoded.Frames[0];
        decodedFrame.Thumbnail.Should().BeNull();
        if (decodedFrame.Metadata is BitmapMetadata decodedMetadata) decodedMetadata.GetQuery(CanaryQuery).Should().BeNull();
        var output = new FormatConvertedBitmap(decodedFrame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var actual = new byte[512 * 256 * 4];
        output.CopyPixels(actual, 512 * 4, 0);
        var wrong = 0;
        for (var i = 0; i < actual.Length; i += 4)
            if (actual[i] != 17 || actual[i + 1] != 83 || actual[i + 2] != 191 || actual[i + 3] != 255) wrong++;
        wrong.Should().Be(0, "only the size changes, not what the image shows");

        frame.PixelWidth.Should().Be(600, "the caller's bitmap is not modified");
        ((BitmapMetadata)frame.Metadata).GetQuery(CanaryQuery).Should().Be(canary);
        VisionImageNormalizer.TryNormalize(null, VisionImagePolicy.LegendCrop, out var none).Should().BeNull();
        none.Should().NotBeNullOrWhiteSpace();
    });

    [Fact]
    public void APastedLegendIsReadOnlyOnTheClickShownCleanBeforeConsentAndExactlyItsBytesAreSent() => Sta(() =>
    {
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3");
        var dialog = Dialog(model, assistant);
        var reads = 0;
        BitmapSource? clipboard = null;
        dialog.ClipboardImageReader = () => { reads++; return clipboard; };
        var row = model.Rows.Single();
        row.CanAskAi.Should().BeTrue();
        dialog.RowsGrid.SelectedItem = row;

        dialog.SendPreview.IsEnabled.Should().BeFalse("nothing is shown, so there is nothing to consent to");
        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeFalse("a consent without a shown image is refused");
        dialog.VisionStatus.Text.Should().Contain("יש להציג קודם");

        ClickOn(dialog.BtnShowSchematic);
        dialog.ShownVisionImage!.Kind.Should().Be(VisionImagePolicy.GroupPreview);
        dialog.VisionDisclosure.Text.Should().Contain("לא צילום ולא מדידה");
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().ContainSingle().Which.Vision.Should().BeNull("a shown image without consent is not sent");
        ClickOn(dialog.BtnRemoveImage);
        dialog.ShownVisionImage.Should().BeNull();
        reads.Should().Be(0, "selection, the schematic, consent, questions and removal never read the clipboard");

        ClickOn(dialog.BtnPasteLegend);
        reads.Should().Be(1);
        dialog.ShownVisionImage.Should().BeNull("an empty clipboard shows nothing");
        dialog.VisionStatus.Text.Should().Contain("אין תמונה");

        clipboard = LegendFrame(out var canary);
        ClickOn(dialog.BtnPasteLegend);
        reads.Should().Be(2);
        var shown = dialog.ShownVisionImage!;
        shown.Kind.Should().Be(VisionImagePolicy.LegendCrop);
        shown.Width.Should().BeLessThanOrEqualTo(VisionImagePolicy.MaxDimension);
        shown.Height.Should().BeLessThanOrEqualTo(VisionImagePolicy.MaxDimension);
        var bytes = shown.ToArray();
        VisionImagePolicy.Validate(bytes).IsValid.Should().BeTrue();
        Encoding.UTF8.GetString(bytes).Should().NotContain(canary);
        dialog.PreviewImage.Visibility.Should().Be(Visibility.Visible);
        ((BitmapSource)dialog.PreviewImage.Source).PixelWidth.Should().Be(shown.Width, "the preview is decoded from the bytes to be sent");
        dialog.SendPreview.IsChecked.Should().BeFalse("shown first; the consent is a separate, explicit step");
        dialog.SendPreview.IsEnabled.Should().BeTrue();
        dialog.VisionDisclosure.Text.Should().Contain("המשתמש צירף; מקור ושיוך לא אומתו").And.Contain(shown.Sha256[..12]);

        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().HaveCount(2);
        var vision = assistant.Calls[1].Vision!;
        vision.Images.Should().ContainSingle();
        vision.Images[0].ToArray().Should().Equal(bytes, "the request carries exactly the normalised bytes on screen");
        var prepared = FamilyRecognitionAssist.Prepare(row.Group, EngineerBoqLibrary.RoadsV1, null, new[] { shown });
        vision.Permit!.ContextId.Should().Be(prepared.Request!.ContextId);
        vision.Permit.ImageSha256.Should().Equal(shown.Sha256);
        VisionImagePolicy.IsPermitted(prepared.Request, vision).Should().BeTrue();
        dialog.SendPreview.IsChecked.Should().BeFalse("one consent covers one send");
        reads.Should().Be(2);
        dialog.Close();
    });

    [Fact]
    public void AReplacedImagesLateAnswerIsDroppedWhileAFreshConsentAndTheTextOnlyQuestionStillWork() => Sta(() =>
    {
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3");
        var dialog = Dialog(model, assistant);
        var reads = 0;
        var legend = LegendFrame(out _);
        dialog.ClipboardImageReader = () => { reads++; return legend; };
        var row = model.Rows.Single();
        dialog.RowsGrid.SelectedItem = row;

        // A: the schematic, shown, consented and sent; the provider has not answered yet.
        ClickOn(dialog.BtnShowSchematic);
        var a = dialog.ShownVisionImage!;
        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();
        var answerA = new TaskCompletionSource<IReadOnlyList<RecognitionProposal>>();
        assistant.Next = answerA;
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().ContainSingle();
        var sentA = assistant.Calls[0].Vision!;
        sentA.Images.Should().ContainSingle().Which.Sha256.Should().Be(a.Sha256);
        dialog.BtnAskAi.IsEnabled.Should().BeFalse("one question at a time");

        // Replace A by B while A is pending: review and consent reset, and A's question is cancelled.
        ClickOn(dialog.BtnPasteLegend);
        reads.Should().Be(1);
        var b = dialog.ShownVisionImage!;
        b.Kind.Should().Be(VisionImagePolicy.LegendCrop);
        b.Sha256.Should().NotBe(a.Sha256);
        dialog.SendPreview.IsChecked.Should().BeFalse("a consent covers the image it was given for");
        assistant.Calls[0].Token.IsCancellationRequested.Should().BeTrue();

        // The provider ignores the cancellation and answers for A anyway: dropped before it reaches the group.
        answerA.SetResult(new[] { Suggest(row.Group, "curb-garden", sentA.Permit!.ContextId) });
        Pump();
        row.AiProposal.Should().BeNull("an answer for a replaced image is never applied");
        dialog.VisionStatus.Text.Should().Contain("לא הוצגה");
        dialog.ShownVisionImage.Should().BeSameAs(b);
        dialog.BtnAskAi.IsEnabled.Should().BeTrue();

        // B with a fresh consent: exactly B's bytes are sent, and its answer is shown.
        assistant.Answer = (group, vision) => new[] { Suggest(group, "curb-garden", vision?.Permit?.ContextId ?? "text-only") };
        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().HaveCount(2);
        var sentB = assistant.Calls[1].Vision!;
        sentB.Images.Should().ContainSingle();
        sentB.Images[0].ToArray().Should().Equal(b.ToArray());
        row.AiProposal!.FamilyId.Should().Be("curb-garden");
        row.AiProposal!.Inferred.Should().Contain("ai_context=" + sentB.Permit!.ContextId);

        // A text-only answer without this group keeps the image-based suggestion (ApplyAssistant leaves it), still tied to B.
        var suggest = assistant.Answer;
        assistant.Answer = (_, _) => Array.Empty<RecognitionProposal>();
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().HaveCount(3);
        assistant.Calls[2].Vision.Should().BeNull();
        row.AiProposal!.Inferred.Should().Contain("ai_context=" + sentB.Permit!.ContextId);
        assistant.Answer = suggest;

        // The engineer picks the suggested family. Removing the image withdraws the suggestion that rested on it, and the
        // pick is not silently kept as an approvable manual choice.
        row.ChosenFamily = row.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
        row.IsSelected = true;
        row.ChosenFromAi.Should().BeTrue();
        ClickOn(dialog.BtnRemoveImage);
        dialog.ShownVisionImage.Should().BeNull();
        dialog.PreviewImage.Visibility.Should().Be(Visibility.Collapsed);
        row.AiProposal.Should().BeNull();
        row.AiStatus.Should().Contain("הוסרה");
        row.IsSelected.Should().BeFalse("the group must be checked again after the suggestion behind its family is gone");
        row.ChosenFamily!.FamilyId.Should().Be("curb-garden", "the engineer's pick itself is not deleted");

        // Without an image the text-only question works as before.
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().HaveCount(4);
        assistant.Calls[3].Vision.Should().BeNull();
        row.AiProposal!.FamilyId.Should().Be("curb-garden");
        reads.Should().Be(1, "the clipboard is read on the paste click only");
        dialog.Close();
    });

    [Fact]
    public void AnEmptyImageAnswerInANewContextKeepsTheLinkSoRemovingTheImageWithdrawsTheEarlierSuggestion() => Sta(() =>
    {
        // Codex int-b reproduction: image A -> description/consent in context B -> empty image answer -> remove left the
        // suggestion from A applied, still checked and attested.
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3");
        var dialog = Dialog(model, assistant);
        var row = model.Rows.Single();
        dialog.RowsGrid.SelectedItem = row;

        ClickOn(dialog.BtnShowSchematic);
        dialog.SendPreview.IsChecked = true;
        assistant.Answer = (group, vision) => new[] { Suggest(group, "curb-garden", vision!.Permit!.ContextId) };
        ClickOn(dialog.BtnAskAi);
        var sentA = assistant.Calls.Single().Vision!;
        row.AiProposal!.Inferred.Should().Contain("ai_context=" + sentA.Permit!.ContextId);

        // Same image, a new description: consent is given again for context B, and B's image answer is empty.
        dialog.AssistContext.Text = "תיאור אחר";
        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();
        assistant.Answer = (_, _) => Array.Empty<RecognitionProposal>();
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().HaveCount(2);
        assistant.Calls[1].Vision.Should().NotBeNull();
        row.AiProposal!.Inferred.Should().Contain("ai_context=" + sentA.Permit!.ContextId, "an empty answer keeps A's suggestion");

        row.ChosenFamily = row.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
        row.IsSelected = true;
        row.ChosenFromAi.Should().BeTrue();
        ClickOn(dialog.BtnRemoveImage);
        row.AiProposal.Should().BeNull("the suggestion that rested on the removed image goes with it");
        row.IsSelected.Should().BeFalse();
        dialog.ConfirmBox.IsChecked.Should().NotBe(true);
        row.ChosenFamily!.FamilyId.Should().Be("curb-garden", "the engineer's pick itself is not deleted");
        dialog.Close();
    });

    [Fact]
    public void AConsentCoversTheShownImageGroupAndDescriptionAndIsNeverReboundByTheQuestion() => Sta(() =>
    {
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3", "QQ7");
        var dialog = Dialog(model, assistant);
        dialog.ClipboardImageReader = () => throw new InvalidOperationException("the clipboard must not be read here");
        var first = model.Rows.Single(r => r.Layer == "QQ3");
        var second = model.Rows.Single(r => r.Layer == "QQ7");
        dialog.RowsGrid.SelectedItem = first;
        ClickOn(dialog.BtnShowSchematic);
        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();

        dialog.AssistContext.Text = "אבני גן סביב ערוגות";
        dialog.SendPreview.IsChecked.Should().BeFalse("the consent covered the description it was given with");
        dialog.ShownVisionImage.Should().NotBeNull("the image stays on screen for a fresh review");
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().ContainSingle();
        assistant.Calls[0].Vision.Should().BeNull("the question never re-binds a consent by itself");
        assistant.Calls[0].Context.Should().Be("אבני גן סביב ערוגות");

        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();
        dialog.ApproverBox.Text = " ";
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().ContainSingle("a consent that no longer yields a valid permit sends nothing");
        dialog.SendPreview.IsChecked.Should().BeFalse();
        dialog.VisionStatus.Text.Should().Contain("לא נשלחה");

        dialog.ApproverBox.Text = "synthetic-engineer";
        dialog.SendPreview.IsChecked = true;
        dialog.RowsGrid.SelectedItem = second;
        dialog.SendPreview.IsChecked.Should().BeFalse();
        dialog.ShownVisionImage.Should().BeNull("an image belongs to the group it was chosen for");
        dialog.PreviewImage.Visibility.Should().Be(Visibility.Collapsed);
        dialog.SendPreview.IsEnabled.Should().BeFalse();
        dialog.RowsGrid.SelectedItem = first;
        dialog.ShownVisionImage.Should().BeNull("going back does not bring back an image without a new explicit choice");
        dialog.Close();
    });

    [Fact]
    public void TheImageControlsAndTheDialogButtonsStayReachableWithTheAssistantOpenInANarrowWindow() => Sta(() =>
    {
        var model = Model("QQ3");
        var dialog = Dialog(model, new ScriptedAssistant());
        dialog.ClipboardImageReader = () => LegendFrame(out _);
        dialog.AssistExpander.IsExpanded = true;
        dialog.RowsGrid.SelectedItem = model.Rows.Single();
        ClickOn(dialog.BtnPasteLegend);
        dialog.ShownVisionImage.Should().NotBeNull();
        foreach (var (width, height) in new[] { (1240, 760), (940, 580) })
            AssertReachable(dialog, width, height, dialog.BtnShowSchematic, dialog.BtnPasteLegend, dialog.BtnRemoveImage,
                dialog.VisionDisclosure, dialog.PreviewImage, dialog.SendPreview, dialog.BtnAskAi, dialog.BtnSave, dialog.BtnCancel);
        dialog.Close();
    });

    /// <summary>Lays the dialog content out offscreen (as the other dialog tests do) and checks each control is inside.</summary>
    private static void AssertReachable(FamilyDecisionsDialog dialog, int width, int height, params FrameworkElement[] controls)
    {
        var root = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        root.FlowDirection = dialog.FlowDirection;
        System.Windows.Documents.TextElement.SetFontFamily(root, dialog.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(root, dialog.FontSize);
        System.Windows.Documents.TextElement.SetForeground(root, dialog.Foreground);
        var frame = new System.Windows.Controls.Border
            { Background = dialog.Background, Child = root, FlowDirection = System.Windows.FlowDirection.LeftToRight };
        frame.Measure(new System.Windows.Size(width, height));
        frame.Arrange(new Rect(0, 0, width, height));
        frame.UpdateLayout();
        foreach (var control in controls)
        {
            var bounds = control.TransformToAncestor(frame).TransformBounds(new Rect(control.RenderSize));
            var name = control.GetType().Name + " " + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
            bounds.Width.Should().BeGreaterThan(0, name);
            bounds.Height.Should().BeGreaterThan(0, name);
            bounds.Left.Should().BeGreaterThanOrEqualTo(0, name);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0, name);
            bounds.Right.Should().BeLessThanOrEqualTo(width, name);
            bounds.Bottom.Should().BeLessThanOrEqualTo(height, name);
        }
        frame.Child = null;
        dialog.Content = root;
    }

    [Fact]
    public void AReplacedImagesLateAnswerLeavesTheConsentGivenForTheNewImage() => Sta(() =>
    {
        // Review finding (int-b, 28.09): the late answer for A unchecked the consent the engineer had given for B meanwhile.
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3");
        var dialog = Dialog(model, assistant);
        dialog.ClipboardImageReader = () => LegendFrame(out _);
        var row = model.Rows.Single();
        dialog.RowsGrid.SelectedItem = row;

        ClickOn(dialog.BtnShowSchematic);
        dialog.SendPreview.IsChecked = true;
        var answerA = new TaskCompletionSource<IReadOnlyList<RecognitionProposal>>();
        assistant.Next = answerA;
        ClickOn(dialog.BtnAskAi);
        var sentA = assistant.Calls.Single().Vision!;

        // B is shown and consented to while A's question is still in flight.
        ClickOn(dialog.BtnPasteLegend);
        var b = dialog.ShownVisionImage!;
        dialog.SendPreview.IsChecked = true;
        dialog.SendPreview.IsChecked.Should().BeTrue();

        answerA.SetResult(new[] { Suggest(row.Group, "curb-garden", sentA.Permit!.ContextId) });
        Pump();
        row.AiProposal.Should().BeNull("an answer for a replaced image is never applied");
        dialog.SendPreview.IsChecked.Should().BeTrue("the consent was given for B, not for the image A's question carried");

        assistant.Answer = (group, vision) => new[] { Suggest(group, "curb-garden", vision!.Permit!.ContextId) };
        ClickOn(dialog.BtnAskAi);
        assistant.Calls.Should().HaveCount(2);
        assistant.Calls[1].Vision!.Images.Single().ToArray().Should().Equal(b.ToArray());
        row.AiProposal!.Inferred.Should().Contain("ai_context=" + assistant.Calls[1].Vision!.Permit!.ContextId);
        dialog.SendPreview.IsChecked.Should().BeFalse("one consent covers one send");
        dialog.Close();
    });

    // CODEX ISOLATED REGRESSION ONLY. Added to the copied int-b test fixture; product source is unchanged.
    // Distinct from Claude's empty TEXT-only regression: the second request here carries the SAME image
    // under a NEW description/context and receives no matching proposal.
    [Fact]
    public void Codex_EmptyImageAnswerForANewContextKeepsTheOldImageProvenanceUntilRemoval() => Sta(() =>
    {
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3");
        var dialog = Dialog(model, assistant);
        try
        {
            var row = model.Rows.Single();
            dialog.RowsGrid.SelectedItem = row;
            ClickOn(dialog.BtnShowSchematic);
            var image = dialog.ShownVisionImage!;
            assistant.Answer = (group, vision) => new[] { Suggest(group, "curb-garden", vision!.Permit!.ContextId) };
            dialog.SendPreview.IsChecked = true;
            ClickOn(dialog.BtnAskAi);
            var first = assistant.Calls.Single().Vision!;
            row.AiProposal!.Inferred.Should().Contain("ai_context=" + first.Permit!.ContextId);
            row.ChosenFamily = row.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
            row.IsSelected = true;
            dialog.ReasonBox.Text = "SYNTHETIC isolated regression based on the first image answer";
            dialog.ConfirmBox.IsChecked = true;

            dialog.AssistContext.Text = "אבני גן סביב ערוגות";
            assistant.Answer = (_, _) => Array.Empty<RecognitionProposal>();
            dialog.SendPreview.IsChecked = true;
            dialog.SendPreview.IsChecked.Should().BeTrue();
            ClickOn(dialog.BtnAskAi);
            assistant.Calls.Should().HaveCount(2);
            var second = assistant.Calls[1].Vision!;
            second.Should().NotBeNull("this regression must use an IMAGE request, not the already-fixed text-only path");
            second.Images.Single().Sha256.Should().Be(image.Sha256);
            second.Permit!.ContextId.Should().NotBe(first.Permit!.ContextId);
            row.AiProposal!.Inferred.Should().Contain("ai_context=" + first.Permit.ContextId,
                "an empty answer deliberately preserves the previous proposal");

            ClickOn(dialog.BtnRemoveImage);
            using (new FluentAssertions.Execution.AssertionScope())
            {
                dialog.ShownVisionImage.Should().BeNull();
                row.AiProposal.Should().BeNull("removing the image must withdraw the old proposal still tied to it");
                row.IsSelected.Should().BeFalse("the selected AI-backed family requires renewed review after withdrawal");
                dialog.ConfirmBox.IsChecked.Should().BeFalse("the prior attestation must not survive withdrawal");
                row.ChosenFamily!.FamilyId.Should().Be("curb-garden", "the engineer's actual choice is preserved");
            }
        }
        finally { dialog.Close(); }
    });

    // CODEX ISOLATED POSITIVE CONTROL. A real answer for the second image request must adopt that new context;
    // removing that image withdraws its answer, preserves the engineer's choice and leaves the other group alone.
    [Fact]
    public void Codex_ANewImageContextAnswerReplacesProvenanceAndRemovalPreservesUnrelatedManualChoice() => Sta(() =>
    {
        var assistant = new ScriptedAssistant();
        var model = Model("QQ3", "QQ7");
        var dialog = Dialog(model, assistant);
        try
        {
            var row = model.Rows.Single(r => r.Layer == "QQ3");
            var other = model.Rows.Single(r => r.Layer == "QQ7");
            other.ChosenFamily = other.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
            other.IsSelected = true;
            var otherChoice = other.ChosenFamily;
            var otherGroup = other.Group;
            dialog.RowsGrid.SelectedItem = row;
            ClickOn(dialog.BtnShowSchematic);
            assistant.Answer = (group, vision) => new[] { Suggest(group, "curb-garden", vision!.Permit!.ContextId) };
            dialog.SendPreview.IsChecked = true;
            ClickOn(dialog.BtnAskAi);
            var first = assistant.Calls.Single().Vision!;

            dialog.AssistContext.Text = "אבני גן סביב ערוגות";
            dialog.SendPreview.IsChecked = true;
            ClickOn(dialog.BtnAskAi);
            assistant.Calls.Should().HaveCount(2);
            var second = assistant.Calls[1].Vision!;
            second.Permit!.ContextId.Should().NotBe(first.Permit!.ContextId);
            row.AiProposal!.Inferred.Should().Contain("ai_context=" + second.Permit.ContextId);
            row.ChosenFamily = row.FamilyOptions.Single(o => o.FamilyId == "curb-garden");
            row.IsSelected = true;
            dialog.ConfirmBox.IsChecked = true;
            ClickOn(dialog.BtnRemoveImage);

            using (new FluentAssertions.Execution.AssertionScope())
            {
                row.AiProposal.Should().BeNull();
                row.IsSelected.Should().BeFalse();
                dialog.ConfirmBox.IsChecked.Should().BeFalse();
                row.ChosenFamily!.FamilyId.Should().Be("curb-garden");
                other.IsSelected.Should().BeTrue();
                other.ChosenFamily.Should().BeSameAs(otherChoice);
                other.Group.Should().BeSameAs(otherGroup);
                other.AiProposal.Should().BeNull();
            }
        }
        finally { dialog.Close(); }
    });

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
