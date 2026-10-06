using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Stages a single explicit first-profile approval. Closing never saves or starts a scan.</summary>
internal sealed class EstimateProjectStartDialog : Window
{
    internal readonly TextBox ProjectNameInput = Input(), ApproverInput = Input();
    internal readonly CheckBox ConfirmSources = new() { IsChecked = false, Foreground = Brushes.White,
        Margin = new Thickness(0, 10, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
    internal readonly Button Start = Action("אשר, שמור והתחל מדידה"), Cancel = Action("ביטול — ללא שמירה");
    // The engineer declares the discipline; nothing is preselected and nothing is guessed from layer names.
    internal readonly RadioButton Roads = Choice("כבישים ותנועה"), Landscape = Choice("פיתוח נופי וגינון");
    internal readonly TextBlock Validation = Text("");
    internal readonly ScrollViewer BodyScroll;
    internal EstimateProjectStartService.Decision? ApprovedDecision { get; private set; }

    internal EstimateProjectStartDialog(string profileId, string? projectName, string drawingPath,
        string writeTarget, string inventory, string? currentApprover = null, bool completingExistingProfile = false)
    {
        Title = completingExistingProfile ? "השלמת תחום העבודה לאומדן" : "התחלת אומדן — פרויקט חדש";
        Width = 780; Height = 720; MinWidth = 540; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(20, 24, 33)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(14) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        ConfirmSources.Content = Text(completingExistingProfile
            ? "בדקתי את זהות הפרויקט והמקורות. אני מאשר/ת את שם הפרויקט והתחום שבחרתי; בחירת המקורות וההחרגות הקיימות נשמרות. אין כאן אישור כמויות או מחירים."
            : "בדקתי את המקורות ומאשר/ת סריקה של כל הגאומטריה הנתמכת במארח וב-XREFs מאומתים. זה אינו אישור כמויות, מחירים או עבודות עפר.");
        footer.Children.Add(ConfirmSources); footer.Children.Add(Validation);
        var buttons = new WrapPanel(); buttons.Children.Add(Start); buttons.Children.Add(Cancel);
        footer.Children.Add(buttons); root.Children.Add(footer);
        var body = new StackPanel();
        body.Children.Add(Text("אומדן ללא הכנת חתכים", 20));
        body.Children.Add(Text(completingExistingProfile
            ? "הפרופיל כבר נשמר, אך עדיין לא נבחר תחום עבודה. בחר תחום במפורש לפני המדידה. המחירונים, בחירת המקורות וההחלטות הקיימות לא יוחלפו."
            : EstimateProjectStartService.Guidance));
        body.Children.Add(Text("מזהה פרויקט: " + Bidi.Ltr(profileId)));
        // Paths in their own left-to-right box: a Hebrew file name inside a path otherwise reads reversed (WPF, 02/10).
        body.Children.Add(Text("שרטוט נוכחי ויעד הפרופיל המקומי:"));
        body.Children.Add(PathBox(drawingPath + "\n" + writeTarget));
        body.Children.Add(Text("שם פרויקט לתצוגה")); body.Children.Add(ProjectNameInput);
        body.Children.Add(Text("שם המאשר/ת — לפעולה זו בלבד")); body.Children.Add(ApproverInput);
        // b25 (Codex 15:12): the name approves this start only; the session approver is set in the status strip.
        body.Children.Add(Text("השם מאשר את התחלת האומדן הזו בלבד. מאשר הסשן להכרעות הבאות נקבע בשורת המצב (\"שנה…\")."));
        body.Children.Add(Text("תחום העבודה — קובע את ספריית השיוך של הטיוטה (חובה לבחור)"));
        var disciplines = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        disciplines.Children.Add(Roads); disciplines.Children.Add(Landscape);
        body.Children.Add(disciplines);
        body.Children.Add(Text("בפרויקט נוף הפריטים המוצעים הם מועמדים מכתב כמויות לדוגמה, ואינם נכללים בסכום עד החלטה הנדסית. החלפת תחום בהמשך דורשת סריקה ובדיקה מחודשות."));
        body.Children.Add(Text("מקורות שדווחו בשרטוט — אימות SHA וקריאת הגאומטריה יבוצעו בסריקה"));
        body.Children.Add(ReadOnly(inventory));
        body.Children.Add(Text("מקור חסר או כשל קריאה לא נחשב לאפס ולא מוחרג. הכמויות שנמדדות יוצגו לעיון; אומדן מלא ידרוש השלמת ממצאים והחלטות."));
        BodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(BodyScroll);
        // b24 (b23 E4 live: "ArthurF" was prefilled): the approver is the confirmed session approver or empty — never the
        // Windows account.
        ProjectNameInput.Text = projectName ?? ""; ApproverInput.Text = currentApprover ?? "";
        void Refresh()
        {
            var problem = Failure();
            Start.IsEnabled = problem == null;
            Validation.Text = problem ?? "האישור ישמור פרופיל אחד וימשיך למדידה באותו שרטוט.";
        }
        ProjectNameInput.TextChanged += (_, _) => Refresh(); ApproverInput.TextChanged += (_, _) => Refresh();
        ConfirmSources.Checked += (_, _) => Refresh(); ConfirmSources.Unchecked += (_, _) => Refresh();
        Roads.Checked += (_, _) => Refresh(); Landscape.Checked += (_, _) => Refresh();
        Start.IsDefault = false; Cancel.IsCancel = true;
        Start.Click += (_, _) =>
        {
            var decision = CurrentDecision();
            if (Failure() != null) { Refresh(); return; }
            ApprovedDecision = decision; Close();
        };
        Cancel.Click += (_, _) => Close();
        System.Windows.Automation.AutomationProperties.SetName(ProjectNameInput, "שם פרויקט לאומדן");
        System.Windows.Automation.AutomationProperties.SetName(ApproverInput, "שם מאשר מקורות האומדן");
        System.Windows.Automation.AutomationProperties.SetName(Roads, "תחום: כבישים ותנועה");
        System.Windows.Automation.AutomationProperties.SetName(Landscape, "תחום: פיתוח נופי וגינון");
        Content = root; Refresh(); UiGuard.Attach(this, "תחילת אומדן בפרויקט חדש");
    }

    private EstimateProjectStartService.Decision CurrentDecision() =>
        new(ProjectNameInput.Text.Trim(), ApproverInput.Text.Trim(), ConfirmSources.IsChecked == true, CurrentDiscipline());

    private string? CurrentDiscipline() =>
        Roads.IsChecked == true ? "roads" : Landscape.IsChecked == true ? "landscape" : null;

    private string? Failure() => EstimateProjectStartService.DecisionFailure(CurrentDecision()) ??
                                 (CurrentDiscipline() == null ? EstimateProjectStartService.DisciplineMissing : null);

    private static RadioButton Choice(string text) => new()
    {
        Content = new TextBlock { Text = text, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
        GroupName = "estimate-discipline", Foreground = Brushes.White, Margin = new Thickness(0, 4, 18, 4),
        VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 28,
    };

    private static TextBlock Text(string text, double size = 13) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = size, Margin = new Thickness(0, 5, 0, 5) };

    private static TextBox Input() => new() { MinHeight = 32, Padding = new Thickness(8, 5, 8, 5),
        Background = new SolidColorBrush(Color.FromRgb(29, 35, 47)), Foreground = Brushes.White,
        CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(95, 112, 136)),
        Margin = new Thickness(0, 0, 0, 6) };

    private static TextBox PathBox(string text)
    {
        var box = ReadOnly(text);
        box.FlowDirection = System.Windows.FlowDirection.LeftToRight;
        box.TextAlignment = TextAlignment.Left;
        return box;
    }

    private static TextBox ReadOnly(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true, Padding = new Thickness(8), BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.FromRgb(29, 35, 47)), Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(95, 112, 136)), Margin = new Thickness(0, 4, 0, 6) };

    private static Button Action(string text) => new() { Content = text, MinHeight = 38,
        Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6),
        Background = new SolidColorBrush(Color.FromRgb(232, 239, 247)),
        Foreground = new SolidColorBrush(Color.FromRgb(20, 24, 33)) };
}
