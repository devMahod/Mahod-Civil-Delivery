using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The image the engineer may add to one question about the selected group (the existing vision path). An image is only
/// ever chosen with an explicit button — the group's schematic shape, or a legend crop pasted from the clipboard (read on
/// that click only) — and is shown exactly as it would be sent, with what it is and a short hash, BEFORE the consent box
/// can be checked. The consent binds that image, this group and the description (<see cref="ReviewedFamilyImage"/>) and
/// covers one send; the question never re-binds it. Replacing or removing the image withdraws the consent, cancels a
/// pending question that carries the old image and withdraws the answer that rested on it; an answer that still arrives
/// for an older image, group or description is dropped before it reaches the group. Moving to another group drops the
/// image and consent only. Without an image the text-only question and the manual choice work as before.
/// </summary>
public sealed partial class FamilyDecisionsDialog
{
    private const string LegendConsentText =
        "לצרף את תמונת המקרא המוצגת (צירוף משתמש; מקור ושיוך לא אומתו). אני מאשר לשלוח את התמונה המוצגת בדיוק, פעם אחת, עם השאלה הבאה.";

    internal readonly Button BtnShowSchematic = VisionButton("הצג צורה סכמטית"), BtnPasteLegend = VisionButton("הדבק תמונת מקרא"),
        BtnRemoveImage = VisionButton("הסר תמונה");
    internal readonly TextBlock VisionDisclosure = Text(""), VisionStatus = Text("");

    /// <summary>Reads the clipboard image. Called from the "paste legend" click only; tests replace it to prove exactly that.</summary>
    internal Func<BitmapSource?> ClipboardImageReader { get; set; } = ReadSystemClipboardImage;

    /// <summary>The image on screen for the selected group: exactly the bytes a consented question would send.</summary>
    internal VisionImage? ShownVisionImage => _visionShown?.Image;

    // The request context of the last image answer each group shows, so replacing that image withdraws only that answer.
    private readonly Dictionary<FamilyDecisionRow, string> _visionAnswers = new();
    private VisionSelection? _visionShown;
    private ReviewedFamilyImage? _visionReviewed;
    private VisionTicket? _visionPending;
    private int _visionVersion;
    private bool _visionEnabled;
    private string? _visionConsentText;

    /// <summary>One explicit choice: the group it was made for, the validated PNG and its decoded preview.</summary>
    private sealed record VisionSelection(FamilyDecisionRow Row, VisionImage Image, FamilyImageOrigin Origin, BitmapSource Preview);

    /// <summary>The image question in flight (one at a time): what was sent, for which choice and selection version.</summary>
    private sealed record VisionTicket(int Version, VisionSelection Selection, ReviewedFamilyImage Reviewed, FamilyDecisionRow Row,
        VisionPayload Payload);

    private UIElement BuildVisionPanel(bool visionEnabled)
    {
        _visionEnabled = visionEnabled;
        _visionConsentText = (SendPreview.Content as TextBlock)?.Text;
        BtnShowSchematic.ToolTip = "מציג את צורת הקבוצה מדגימות הגאומטריה (בלי קואורדינטות, טקסט או שמות). שום דבר לא נשלח בלחיצה הזו.";
        BtnPasteLegend.ToolTip = "קורא את התמונה שבלוח ההעתקה רק בלחיצה הזו, מקטין ומנקה אותה בזיכרון ומציג אותה לבדיקה. שום דבר לא נשלח או נשמר.";
        var buttons = new WrapPanel();
        buttons.Children.Add(BtnShowSchematic);
        buttons.Children.Add(BtnPasteLegend);
        buttons.Children.Add(BtnRemoveImage);
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Left);
        line.Children.Add(buttons);
        VisionDisclosure.Margin = new Thickness(8, 4, 0, 0);
        VisionDisclosure.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(VisionDisclosure);
        VisionStatus.Foreground = WarningBrush;
        VisionStatus.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Visibility = visionEnabled ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(line);
        panel.Children.Add(VisionStatus);

        BtnShowSchematic.Click += (_, _) => OnVisionShowSchematic();
        BtnPasteLegend.Click += (_, _) => OnVisionPasteLegend();
        BtnRemoveImage.Click += (_, _) => OnVisionRemoveImage();
        // Subscribed before the dialog's own handlers, so a consent is bound (or refused) before the preview refreshes.
        SendPreview.Checked += (_, _) => BindVisionConsent();
        SendPreview.Unchecked += (_, _) => _visionReviewed = null;
        // A consent covers the description it was given with; the image stays on screen for a fresh review.
        AssistContext.TextChanged += (_, _) =>
        {
            if (SendPreview.IsChecked != true) return;
            SendPreview.IsChecked = false;
            SetVisionStatus("התיאור השתנה, ולכן ההסכמה לתמונה בוטלה. התמונה נשארה מוצגת: אפשר לבדוק אותה ולסמן הסכמה מחדש.");
        };
        // An image belongs to the group it was chosen for. Saved decisions, CAD proposals and answers stay.
        RowsGrid.SelectionChanged += (_, _) =>
        {
            if (_visionShown != null && !ReferenceEquals(_visionShown.Row, SelectedRow))
            {
                ResetVisionSelection();
                SetVisionStatus(string.Empty);
            }
            RefreshVisionControls(SelectedRow);
        };
        RefreshVisionControls(null);
        return panel;
    }

    private void RefreshVisionControls(FamilyDecisionRow? row)
    {
        var canChoose = _visionEnabled && _assistant != null && row is { CanAskAi: true };
        BtnShowSchematic.IsEnabled = canChoose;
        BtnPasteLegend.IsEnabled = canChoose;
        var shown = VisionShownFor(row);
        BtnRemoveImage.IsEnabled = shown != null;
        // Nothing to consent to until the image that would be sent is on screen.
        SendPreview.IsEnabled = shown != null;
        if (SendPreview.Content is TextBlock label && _visionConsentText != null)
            label.Text = shown?.Origin == FamilyImageOrigin.UserSuppliedLegend ? LegendConsentText : _visionConsentText;
        VisionDisclosure.Text = shown != null
            ? $"{ReviewedFamilyImage.DisclosureOf(shown.Origin)} · {shown.Row.Layer} · SHA-256 {shown.Image.Sha256[..12]}"
            : canChoose ? "לא נבחרה תמונה. אפשר לשאול בלי תמונה, או להציג כאן תמונה לבדיקה לפני ההסכמה." : string.Empty;
    }

    /// <summary>
    /// Hooked into RefreshAssist after the status line. Shows the image chosen explicitly for this group, decoded from
    /// exactly the bytes that would be sent. Always true: nothing is rendered because the consent box was checked, so the
    /// render-on-consent lines after the hook never run and <c>_preview</c> stays empty (its permit in AskAssistantAsync is
    /// never built). The hook keeps those lines untouched for the parallel edit of the dialog.
    /// </summary>
    private bool ShowVisionSelectionOnly(FamilyDecisionRow row)
    {
        RefreshVisionControls(row);
        var shown = VisionShownFor(row);
        if (shown == null)
        {
            PreviewImage.ToolTip = null;
            return true;
        }
        // Small beside the question (the window stays usable when narrow), full size on hover.
        PreviewImage.Source = shown.Preview;
        PreviewImage.Width = Math.Min(shown.Image.Width, shown.Origin == FamilyImageOrigin.UserSuppliedLegend ? 240 : 80);
        PreviewImage.Height = Math.Min(shown.Image.Height, 80);
        PreviewImage.ToolTip = new System.Windows.Controls.Image
        {
            Source = shown.Preview, Width = shown.Image.Width, Height = shown.Image.Height,
        };
        PreviewImage.Visibility = Visibility.Visible;
        return true;
    }

    private void OnVisionShowSchematic()
    {
        var row = SelectedRow;
        if (!_visionEnabled || _assistant == null || row is not { CanAskAi: true }) return;
        var png = GroupPreviewRenderer.Render(GroupPreviewRenderer.Samples(row.Group.Records));
        if (png == null || !VisionImagePolicy.TryCreate(png, VisionImagePolicy.GroupPreview, out var image, out _) || image == null)
        {
            SetVisionStatus("אין לקבוצה דגימת צורה קריאה, ולכן אין צורה סכמטית להציג או לשלוח. אפשר לשאול בלי תמונה.");
            return;
        }
        ShowVisionSelection(new VisionSelection(row, image, FamilyImageOrigin.SchematicGroup, VisionBitmap(image)));
    }

    private void OnVisionPasteLegend()
    {
        var row = SelectedRow;
        if (!_visionEnabled || _assistant == null || row is not { CanAskAi: true }) return;
        // The only clipboard read of this dialog. The meaning comes from the engineer's request to attach the crop as a
        // legend; nothing checks that it shows one, and a failure keeps the image already shown.
        BitmapSource? pasted;
        try { pasted = ClipboardImageReader(); }
        catch (Exception) { pasted = null; }
        var kept = VisionShownFor(row) != null ? " התמונה המוצגת לא הוחלפה." : string.Empty;
        if (pasted == null)
        {
            SetVisionStatus("בלוח ההעתקה אין תמונה. יש להעתיק את קטע המקרא (למשל בכלי החיתוך של Windows) וללחוץ שוב. לא נשלח ולא נשמר דבר." + kept);
            return;
        }
        var image = VisionImageNormalizer.TryNormalize(pasted, VisionImagePolicy.LegendCrop, out var error);
        if (image == null)
        {
            SetVisionStatus((error ?? "לא ניתן להכין את התמונה. לא נשלח ולא נשמר דבר.") + kept);
            return;
        }
        ShowVisionSelection(new VisionSelection(row, image, FamilyImageOrigin.UserSuppliedLegend, VisionBitmap(image)));
    }

    private void OnVisionRemoveImage()
    {
        var shown = _visionShown;
        if (shown == null) return;
        ResetVisionSelection();
        WithdrawVisionAnswer(shown.Row);
        SetVisionStatus("התמונה הוסרה ולא תישלח. אפשר לשאול בלי תמונה או לבחור משפחה ידנית.");
        RefreshVision();
    }

    /// <summary>A new choice replaces the previous one: its review, consent, pending question and the answer that rested on it.</summary>
    private void ShowVisionSelection(VisionSelection selection)
    {
        ResetVisionSelection();
        WithdrawVisionAnswer(selection.Row);
        _visionShown = selection;
        SetVisionStatus(string.Empty);
        RefreshVision();
    }

    private void ResetVisionSelection()
    {
        _visionVersion++;
        // A pending question that carries the previous image is cancelled; an answer that still arrives is dropped.
        if (_visionPending != null) _assistCancellation?.Cancel();
        _visionShown = null;
        _visionReviewed = null;
        SendPreview.IsChecked = false;
    }

    /// <summary>The consent box was checked: bind it to the image, group and description on screen, or refuse visibly.</summary>
    private void BindVisionConsent()
    {
        _visionReviewed = null;
        var row = SelectedRow;
        var shown = VisionShownFor(row);
        if (row == null || shown == null || !row.CanAskAi)
        {
            SetVisionStatus("אין תמונה מוצגת לקבוצה הזו. יש להציג קודם את התמונה שתישלח (צורה סכמטית או תמונת מקרא), לבדוק אותה, ורק אז לסמן הסכמה.");
            SendPreview.IsChecked = false;
            return;
        }
        var reviewed = ReviewedFamilyImage.Bind(row.Group, _library, VisionContext(), shown.Image, shown.Origin, out var refusal);
        if (reviewed == null)
        {
            SetVisionStatus("לא ניתן לאשר שליחה של התמונה לקבוצה הזו: " +
                            (string.IsNullOrWhiteSpace(refusal) || refusal == ReviewedFamilyImage.ImageRefusal
                                ? "התמונה אינה PNG תקין מהסוג שהוצג."
                                : refusal));
            SendPreview.IsChecked = false;
            return;
        }
        _visionReviewed = reviewed;
        SetVisionStatus(string.Empty);
    }

    /// <summary>
    /// Hooked into AskAssistantAsync before anything is sent. Without consent: a text-only question. With consent: the
    /// reviewed payload of the image, group and description on screen now, once. A consent that no longer matches sends
    /// nothing (false) and is reset, never re-bound here.
    /// </summary>
    private bool TakeVisionPayload(FamilyDecisionRow row, string? context, out VisionPayload? vision)
    {
        vision = null;
        _visionPending = null;
        if (SendPreview.IsChecked != true) return true;
        var shown = VisionShownFor(row);
        var reviewed = _visionReviewed;
        if (shown != null && reviewed != null &&
            reviewed.TryTakePayload(row.Group, _library, context, shown.Image, explicitConsent: true, ApprovedBy, DateTimeOffset.UtcNow,
                out var payload) && payload != null)
        {
            _visionPending = new VisionTicket(_visionVersion, shown, reviewed, row, payload);
            vision = payload;
            return true;
        }
        SendPreview.IsChecked = false;
        SetVisionStatus("התמונה לא נשלחה: ההסכמה אינה תואמת עוד לתמונה, לקבוצה או לתיאור המוצגים, או ששם המאשר חסר. לא נשלח דבר. " +
                        "אפשר לבדוק את התמונה ולסמן הסכמה מחדש, או לשאול שוב בלי תמונה.");
        return false;
    }

    /// <summary>
    /// Hooked into AskAssistantAsync before the answer reaches the group. A text-only answer passes. An image answer passes
    /// only while its choice, selection version, image and request context are still the current ones and nothing
    /// cancelled it; otherwise it is dropped (a provider may ignore cancellation).
    /// </summary>
    private bool AcceptVisionAnswer(FamilyDecisionRow row, VisionPayload? vision, IReadOnlyList<RecognitionProposal>? results)
    {
        var ticket = _visionPending;
        _visionPending = null;
        if (vision == null)
        {
            // A text-only answer for this group replaces what an image answer showed. One without this group leaves the
            // group's suggestion as it was (ApplyAssistant keeps it), so the link to the image it rested on stays.
            if (results != null && results.Any(result => result != null && result.GroupId == row.Group.GroupId))
                _visionAnswers.Remove(row);
            return true;
        }
        if (ticket == null || !ReferenceEquals(ticket.Payload, vision) || !ReferenceEquals(ticket.Row, row) ||
            ticket.Version != _visionVersion || !ReferenceEquals(ticket.Selection, _visionShown) ||
            _assistCancellation is { IsCancellationRequested: true } ||
            !ticket.Reviewed.IsCurrent(row.Group, _library, VisionContext(), _visionShown?.Image))
        {
            SetVisionStatus("התקבלה תשובה לתמונה שהוחלפה או הוסרה, או לקבוצה או לתיאור שהשתנו בינתיים, ולכן היא לא הוצגה ולא נשמרה. אפשר לשאול שוב.");
            return false;
        }
        // The link names the request whose answer the group now shows. An image answer without this group leaves the
        // group's suggestion as it was (ApplyAssistant keeps it), so the earlier link must stay: overwriting it would
        // make a later removal withdraw nothing while the suggestion from the earlier image stayed on screen.
        if (results != null && results.Any(result => result != null && result.GroupId == row.Group.GroupId))
            _visionAnswers[row] = ticket.Reviewed.ContextId;
        return true;
    }

    private void WithdrawVisionAnswer(FamilyDecisionRow row)
    {
        if (_visionAnswers.Remove(row, out var contextId)) row.WithdrawAssistantAnswer(contextId);
    }

    private VisionSelection? VisionShownFor(FamilyDecisionRow? row) =>
        _visionShown != null && row != null && ReferenceEquals(_visionShown.Row, row) ? _visionShown : null;

    private string? VisionContext() => string.IsNullOrWhiteSpace(AssistContext.Text) ? null : AssistContext.Text.Trim();

    private void SetVisionStatus(string text)
    {
        VisionStatus.Text = text;
        VisionStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshVision()
    {
        RefreshAssist();
        RefreshVisionControls(SelectedRow);
    }

    private static Button VisionButton(string value)
    {
        var button = ActionButton(value);
        button.Padding = new Thickness(10, 4, 10, 4);
        return button;
    }

    private static BitmapSource VisionBitmap(VisionImage image)
    {
        using var stream = new MemoryStream(image.ToArray());
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? ReadSystemClipboardImage()
    {
        // A clipboard held by another program is "no image", never a crash.
        try { return System.Windows.Clipboard.ContainsImage() ? System.Windows.Clipboard.GetImage() : null; }
        catch (Exception) { return null; }
    }
}
