using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Immutable review evidence; this window never reads/writes CAD or a profile.</summary>
internal sealed record DrawingUnitsReviewContext(
    string DrawingPath, string DrawingFingerprint, int RawInsunitsCode,
    string DrawingHash, string CurrentEvidence, bool HasCurrentDeclaration = false, string? Suspicion = null,
    string? CurrentApprover = null, PhysicalDrawingUnitPolicy.CivilUnitEvidence? CivilUnits = null);

/// <summary>An explicit host-only physical-unit decision, never an INSUNITS edit: metres for a unitless host, or —
/// for an explicit INSUNITS (b24) — confirming the recorded unit or recording a different supported unit.</summary>
internal sealed class DrawingUnitsReviewDialog : Window
{
    private readonly DrawingUnitsReviewContext _context;
    internal readonly CheckBox Metres = new()
    {
        IsChecked = false,
        Content = Text("שרטוט זה מצויר במטרים — יחידת שרטוט אחת מייצגת מטר אחד."),
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 8, 0, 8), Foreground = Brushes.White,
    };
    /// <summary>b25 (Codex 15:12): a unitless host whose Civil drawing units read another unit than metres — declaring
    /// metres contradicts that setting, so it takes this explicit acknowledgement besides the reason and the source.</summary>
    internal readonly CheckBox CivilConflict = new()
    {
        IsChecked = false,
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 8), Foreground = Brushes.White, Visibility = Visibility.Collapsed,
    };
    internal readonly RadioButton KeepRecorded = Choice(), OtherUnit = Choice();
    internal readonly ComboBox OtherUnitChoice = new()
    {
        MinHeight = 30, Margin = new Thickness(24, 0, 0, 8), IsEnabled = false,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left, MinWidth = 260,
    };
    internal readonly TextBox Reason = Input(multiline: true), Source = Input(multiline: true),
        Approver = Input();
    internal readonly Button Save = Action("שמור הצהרת מטרים"), Cancel = Action("ביטול — ללא שינוי"),
        Revoke = Action("בטל הצהרה קיימת");
    internal readonly TextBlock Validation = Text("");
    internal readonly ScrollViewer BodyScroll;
    internal ProjectProfile.DrawingUnitDeclaration? ApprovedDeclaration { get; private set; }
    internal bool RevocationRequested { get; private set; }

    internal DrawingUnitsReviewDialog(DrawingUnitsReviewContext context)
    {
        _context = context;
        Title = "יחידות פיזיות — השרטוט הנוכחי בלבד";
        Width = 700; Height = 710; MinWidth = 460; MinHeight = 420;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 33)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(14) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        Validation.MinHeight = 36; footer.Children.Add(Validation);
        var actions = new WrapPanel(); actions.Children.Add(Save); actions.Children.Add(Revoke); actions.Children.Add(Cancel);
        Revoke.Visibility = context.HasCurrentDeclaration ? Visibility.Visible : Visibility.Collapsed;
        var explicitUnit = context.RawInsunitsCode != PhysicalDrawingUnitPolicy.Unitless;
        if (explicitUnit) Save.Content = "שמור הכרעת יחידות";
        footer.Children.Add(actions); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(Text("באיזו יחידה צויר המודל?", 21));
        body.Children.Add(Text(explicitUnit
            ? "לשרטוט יש יחידה רשומה (INSUNITS). אם המודל צויר ביחידה אחרת, מכריעים כאן לפי ראיה: מאשרים את היחידה הרשומה, או רושמים יחידה פיזית שונה. אין ברירת מחדל ואין הנחת מטרים."
            : "הגדרת ההכנסה INSUNITS אינה תמיד היחידה הפיזית: לעיתים שרטוט במטרים מוגדר Unitless. רק מי שמכיר את מקור השרטוט יכול להצהיר על כך."));
        if (!string.IsNullOrWhiteSpace(context.Suspicion))
        {
            var suspicion = Text("נדרשת בדיקה: " + context.Suspicion);
            suspicion.Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 120));
            body.Children.Add(suspicion);
        }
        body.Children.Add(Text("השרטוט הנוכחי", 15));
        body.Children.Add(IdentityText(context.DrawingPath));
        body.Children.Add(IdentityText($"INSUNITS = {context.RawInsunitsCode} · GUID = {context.DrawingFingerprint}"));
        // b25 (Codex 15:12): the Civil evidence and its read status are shown for a unitless host too — it informs the
        // decision without making it; the decision is bound to it either way.
        var civilName = DrawingUnitDeclarationReview.CivilName(context.CivilUnits);
        body.Children.Add(Text("יחידות השרטוט ב-Civil: " + civilName + ". ההכרעה תיקשר לערך הזה; שינוי שלו יחייב בירור חדש."));
        if (!explicitUnit && HasCivilConflict(context))
        {
            // b26 (Codex 17:57): an unmapped value is "cannot confirm", not "another unit" — the declaration acknowledges
            // that uncertainty instead of a contradiction.
            var unmapped = context.CivilUnits!.Status == PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther;
            var conflict = Text(unmapped
                ? $"יחידות Civil של השרטוט: {civilName}. אי אפשר לאשר מהן שהשרטוט במטרים, וגם לא לשלול זאת: " +
                  "הצהר רק אם יש ראיה מתועדת שהשרטוט מצויר במטרים, ופרט אותה בנימוק ובמקור."
                : $"הגדרת Civil של השרטוט היא {civilName}, לא מטר. היא אינה מכריעה לבדה (לעיתים זו ברירת מחדל של התבנית), " +
                  "אבל הצהרת מטרים סותרת אותה: הצהר רק אם יש ראיה מתועדת שהשרטוט מצויר במטרים, ופרט אותה בנימוק ובמקור.");
            conflict.Foreground = new SolidColorBrush(Color.FromRgb(255, 214, 120));
            body.Children.Add(conflict);
            CivilConflict.Content = Text(unmapped
                ? "ראיתי שיחידות Civil אינן מאשרות מטרים, ולפי הראיה שבנימוק ובמקור השרטוט מצויר במטרים."
                : $"ראיתי ש-Civil מוגדר {civilName}, ולפי הראיה שבנימוק ובמקור השרטוט בכל זאת מצויר במטרים.");
            CivilConflict.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrWhiteSpace(context.DrawingHash))
            body.Children.Add(IdentityText("SHA-256 (בעת פתיחת ההכרעה): " + context.DrawingHash));
        body.Children.Add(Text(context.CurrentEvidence));
        body.Children.Add(Text("ההצהרה נשמרת בפרופיל; היא אינה משנה DWG, גאומטריה או INSUNITS. היא חלה על זהות השרטוט והנתיב המוצגים בלבד — לא על יחידות XREF ולא כאישור תכנון."));
        var editor = new StackPanel();
        if (explicitUnit)
        {
            var recorded = DrawingUnitDeclarationReview.UnitName(context.RawInsunitsCode);
            var factor = PhysicalDrawingUnitPolicy.StandardMetresPerUnit(context.RawInsunitsCode);
            KeepRecorded.Content = Text(factor is { } f
                ? $"אישור היחידה הרשומה: {recorded} — ×{f:G9} למטר."
                : $"אישור היחידה הרשומה אינו אפשרי: קוד {context.RawInsunitsCode} אינו יחידה נתמכת.");
            OtherUnit.Content = Text("יחידה פיזית שונה לפי ראיה (מהרשימה, במקדם התקני שלה):");
            foreach (var code in PhysicalDrawingUnitPolicy.DeclarableUnits)
                if (code != context.RawInsunitsCode)
                    OtherUnitChoice.Items.Add(new ComboBoxItem
                    {
                        Tag = code, FlowDirection = System.Windows.FlowDirection.RightToLeft,
                        Content = $"{DrawingUnitDeclarationReview.UnitName(code)} — ×{PhysicalDrawingUnitPolicy.StandardMetresPerUnit(code)!.Value:G9} למטר",
                    });
            editor.Children.Add(KeepRecorded); editor.Children.Add(OtherUnit); editor.Children.Add(OtherUnitChoice);
        }
        else { editor.Children.Add(Metres); editor.Children.Add(CivilConflict); }
        editor.Children.Add(Text(context.HasCurrentDeclaration
            ? "נימוק מקצועי להצהרה או לביטול שלה (חובה)"
            : explicitUnit ? "על סמך מה הוכרעה היחידה? (חובה)" : "על סמך מה נקבע שמדובר במטרים? (חובה)")); editor.Children.Add(Reason);
        editor.Children.Add(Text("מקור / הפניה לבדיקה או למסמך (חובה)")); editor.Children.Add(Source);
        // b24 (b23 E4 live): the approver field is the confirmed session approver or empty — the Windows account is not
        // necessarily the person who approves, so the name on a declaration is always one somebody typed.
        editor.Children.Add(Text("שם המאשר (חובה)")); editor.Children.Add(Approver);
        Approver.Text = context.CurrentApprover ?? "";
        editor.Children.Add(Text("אחרי שמירה: תכנון חתכים וסריקת כמויות חדשים. תוצאות קודמות לא ישמשו לאישור או לייצוא."));
        body.Children.Add(editor);
        BodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(BodyScroll); Content = root;
        Metres.Checked += (_, _) => Refresh(); Metres.Unchecked += (_, _) => { CivilConflict.IsChecked = false; Refresh(); };
        CivilConflict.Checked += (_, _) => Refresh(); CivilConflict.Unchecked += (_, _) => Refresh();
        KeepRecorded.Checked += (_, _) => Refresh(); OtherUnit.Checked += (_, _) => Refresh();
        OtherUnit.Unchecked += (_, _) => Refresh(); OtherUnitChoice.SelectionChanged += (_, _) => Refresh();
        Reason.TextChanged += (_, _) => Refresh(); Source.TextChanged += (_, _) => Refresh();
        Approver.TextChanged += (_, _) => Refresh();
        Save.Click += (_, _) =>
        {
            Refresh(); if (!Save.IsEnabled) return;
            ApprovedDeclaration = CreateDeclaration();
            Close();
        };
        Revoke.Click += (_, _) =>
        {
            Refresh(); if (!Revoke.IsEnabled) return;
            RevocationRequested = true; Close();
        };
        Cancel.IsCancel = true;
        Cancel.Click += (_, _) => { ApprovedDeclaration = null; RevocationRequested = false; Close(); };
        Refresh(); UiGuard.Attach(this, "יחידות פיזיות");
    }

    // The physical unit the current choice records; null while nothing (or no listed unit) is chosen.
    private int? ChosenPhysicalUnit() =>
        _context.RawInsunitsCode == PhysicalDrawingUnitPolicy.Unitless
            ? (Metres.IsChecked == true ? PhysicalDrawingUnitPolicy.Metres : null)
            : KeepRecorded.IsChecked == true ? _context.RawInsunitsCode
            : OtherUnit.IsChecked == true ? (OtherUnitChoice.SelectedItem as ComboBoxItem)?.Tag as int? : null;

    // b25 (Codex 15:12): Civil drawing units observed in a unit other than metres, on a unitless host.
    // b26 (Codex 17:57): the same rule as the policy — a contradicting or an unmapped reading needs the acknowledgement.
    private static bool HasCivilConflict(DrawingUnitsReviewContext context) =>
        context.CivilUnits is { } civil && PhysicalDrawingUnitPolicy.NeedsCivilReview(civil, 1.0);

    // b25: a unitless host is declared through the reviewed unitless-metres kind, bound to the Civil evidence shown here
    // and closed by its review record; the kind-less form is only read from existing profiles.
    private ProjectProfile.DrawingUnitDeclaration CreateDeclaration() =>
        _context.RawInsunitsCode == PhysicalDrawingUnitPolicy.Unitless
            ? PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(
                _context.DrawingFingerprint, _context.DrawingPath, _context.RawInsunitsCode,
                PhysicalDrawingUnitPolicy.UnitlessMetres, PhysicalDrawingUnitPolicy.Metres,
                DrawingUnitDeclarationReview.AuditText(Approver.Text), DateTime.UtcNow, Reason.Text, Source.Text,
                _context.CivilUnits ?? PhysicalDrawingUnitPolicy.CivilUnitEvidence.None)
            : PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(
                _context.DrawingFingerprint, _context.DrawingPath, _context.RawInsunitsCode,
                KeepRecorded.IsChecked == true ? PhysicalDrawingUnitPolicy.ConfirmRecordedUnit : PhysicalDrawingUnitPolicy.DifferentPhysicalUnit,
                ChosenPhysicalUnit() ?? -1,
                DrawingUnitDeclarationReview.AuditText(Approver.Text), DateTime.UtcNow, Reason.Text, Source.Text,
                _context.CivilUnits ?? PhysicalDrawingUnitPolicy.CivilUnitEvidence.None);

    private void Refresh()
    {
        var savedEvidence = !string.IsNullOrWhiteSpace(_context.DrawingHash);
        var unitless = _context.RawInsunitsCode == PhysicalDrawingUnitPolicy.Unitless;
        Metres.IsEnabled = unitless && savedEvidence;
        CivilConflict.IsEnabled = unitless && savedEvidence && Metres.IsChecked == true;
        KeepRecorded.IsEnabled = !unitless && savedEvidence &&
            PhysicalDrawingUnitPolicy.StandardMetresPerUnit(_context.RawInsunitsCode) != null;
        OtherUnit.IsEnabled = !unitless && savedEvidence;
        OtherUnitChoice.IsEnabled = OtherUnit.IsEnabled && OtherUnit.IsChecked == true;
        Reason.IsEnabled = Source.IsEnabled = Approver.IsEnabled = savedEvidence;
        Revoke.IsEnabled = _context.HasCurrentDeclaration && savedEvidence &&
            !string.IsNullOrWhiteSpace(Reason.Text) && !string.IsNullOrWhiteSpace(Source.Text) &&
            !string.IsNullOrWhiteSpace(Approver.Text);
        var chosen = ChosenPhysicalUnit();
        string? error = !savedEvidence ? "אין ראיית קובץ שמור. שמור את השרטוט ופתח את ההכרעה מחדש."
            : unitless && Metres.IsChecked != true ? "בדוק את מקור היחידות, וסמן רק אם השרטוט אכן מצויר במטרים."
            : unitless && HasCivilConflict(_context) && CivilConflict.IsChecked != true
                // Codex 18:18: an unmapped reading is uncertainty, not a proven contradiction.
                ? (_context.CivilUnits!.Status == PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther
                    ? "יחידות Civil אינן מאשרות מטרים — אשר במפורש שיש ראיה לכך, או בטל."
                    : "יחידות Civil סותרות את המטרים — אשר במפורש שיש ראיה לכך, או בטל.")
            : !unitless && KeepRecorded.IsChecked != true && OtherUnit.IsChecked != true
                ? "בחר: אישור היחידה הרשומה, או יחידה פיזית שונה לפי ראיה."
            : !unitless && chosen == null ? "בחר את היחידה הפיזית מהרשימה."
            : string.IsNullOrWhiteSpace(Reason.Text) ? "כתוב את הסיבה המקצועית להצהרה."
            : string.IsNullOrWhiteSpace(Source.Text) ? "ציין מקור או הפניה שמבססים את יחידת השרטוט."
            : string.IsNullOrWhiteSpace(Approver.Text) ? "יש למלא את שם המאשר."
            : null;
        if (error == null)
        {
            try { _ = CreateDeclaration(); }
            catch (ArgumentException ex) { error = ex.Message; }
        }
        Save.IsEnabled = error == null;
        Save.Opacity = Save.IsEnabled ? 1.0 : 0.58;
        Revoke.Opacity = Revoke.IsEnabled ? 1.0 : 0.58;
        // Before approval: raw, effective unit, factor and the host-only scope — the same numbers the scan will use.
        Validation.Text = Revoke.IsEnabled && !Save.IsEnabled
            ? "אפשר לבטל את ההצהרה הקיימת עם הסיבה והמקור שמולאו. יחידות ה-DWG לא ישתנו."
            : error ?? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "לשמירה: הגדרת השרטוט {0} ({1}); יחידה לחישוב: {2}; המרה ×{3:G9} למטר — לשרטוט המארח הזה בלבד, לא ל-XREF. נדרשות מדידה ותכנון מחדש.",
                DrawingUnitDeclarationReview.UnitName(_context.RawInsunitsCode), _context.RawInsunitsCode,
                DrawingUnitDeclarationReview.UnitName(chosen!.Value), PhysicalDrawingUnitPolicy.StandardMetresPerUnit(chosen.Value));
    }

    private static TextBlock Text(string text, double size = 13) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White,
      FontSize = size, Margin = new Thickness(0, 4, 0, 4) };
    private static TextBlock IdentityText(string value)
    {
        var text = Text(value); text.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        text.TextAlignment = TextAlignment.Left; return text;
    }
    private static RadioButton Choice() => new()
    {
        GroupName = "physical-unit-decision", IsChecked = false, Foreground = Brushes.White,
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 4),
    };
    private static TextBox Input(bool multiline = false) => new()
    {
        MinHeight = multiline ? 54 : 32, TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = multiline, MaxLength = 2000,
        Padding = new Thickness(7), Margin = new Thickness(0, 1, 0, 5),
        Foreground = Brushes.Black, Background = Brushes.White,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private static Button Action(string label) => new()
    {
        Content = label, MinHeight = 38, Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(0, 6, 8, 0), Foreground = new SolidColorBrush(Color.FromRgb(20, 24, 33)),
        Background = new SolidColorBrush(Color.FromRgb(232, 239, 247)),
    };
}
