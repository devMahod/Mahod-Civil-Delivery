using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using GroupBox = System.Windows.Controls.GroupBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// "קישור ספרייה למהדורה": for each library recipe code the active price list does not list, the engineer picks the
/// item of this edition that is the same item — or leaves it unlinked. Nothing is pre-selected; one explicit button
/// marks only the unique exact-text matches. A truncated description or a differing parameter is shown as such. The
/// dialog returns requests; the service validates and saves them with the approver and reason.
/// </summary>
public sealed class EditionLinksDialog : Window
{
    private readonly IReadOnlyList<EditionLinkProposal> _proposals;
    private readonly Dictionary<string, List<(RadioButton Button, EditionCandidate? Candidate)>> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _approver;
    private readonly TextBox _reason;
    private readonly TextBlock _status;

    public IReadOnlyList<EditionLinkRequest> Requests { get; private set; } = Array.Empty<EditionLinkRequest>();
    public string ApprovedBy => _approver.Text.Trim();
    public string Reason => _reason.Text.Trim();

    /// <summary>
    /// An identifier embedded left-to-right (LRE … PDF) in a right-to-left sentence. A Run's FlowDirection is not applied
    /// inside a TextBlock, so without the embedding "nti-unified-2026-07 (1797086f4850…)" shows its parentheses reordered.
    /// </summary>
    internal static string Ltr(string text) => "‪" + text + "‬";

    public static string KindLabel(EditionMatchKind kind) => kind switch
    {
        EditionMatchKind.ExactText => "טקסט זהה",
        EditionMatchKind.TruncatedPrefix => "תיאור קטוע — עמימות",
        EditionMatchKind.ConsistentParameters => "ניסוח שונה, פרמטרים זהים — לבדיקה",
        _ => "פרמטר שונה",
    };

    /// <summary>The library codes to review: not listed in the active list and having a reference text.</summary>
    public static IReadOnlyList<EditionLinkProposal> Reviewable(IReadOnlyList<EditionLinkProposal> proposals) =>
        proposals.Where(p => !p.PresentInCatalog && p.Reference != null).ToList();

    /// <summary>Only the codes whose candidates hold exactly one exact-text item, and that item.</summary>
    public static IReadOnlyList<EditionLinkRequest> UniqueExactMatches(IReadOnlyList<EditionLinkProposal> proposals) =>
        Reviewable(proposals)
            .Select(p => (p, exact: p.Candidates.Where(c => c.Kind == EditionMatchKind.ExactText).ToList()))
            .Where(x => x.exact.Count == 1 && !string.Equals(x.p.LinkedCode, x.exact[0].Code, StringComparison.OrdinalIgnoreCase))
            .Select(x => new EditionLinkRequest(x.p.LibraryCode, x.exact[0].Code, EditionMatchKind.ExactText))
            .ToList();

    public EditionLinksDialog(IReadOnlyList<EditionLinkProposal> proposals, CatalogSnapshot catalog, IReadOnlyList<string> staleLinks,
        string defaultApprover)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(catalog);
        _proposals = proposals;
        Title = "קישור ספרייה למהדורת המחירון";
        FlowDirection = System.Windows.FlowDirection.RightToLeft;
        Width = 1000;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        var reviewable = Reviewable(proposals);
        var root = new DockPanel { Margin = new Thickness(12) };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = $"המחירון הפעיל: {Ltr($"{catalog.SnapshotId} ({catalog.FileHash[..Math.Min(12, catalog.FileHash.Length)]}…)")}. " +
                   $"הספרייה נכתבה במחירון {Ltr(LibraryItemReferences.SourcePriceList)}; במהדורה אחרת המספור והניסוח שונים, " +
                   "ואותו מספר או תיאור דומה אינם אותו סעיף. בחרו לכל סעיף ספרייה את הסעיף המקביל רק אם זה אותו סעיף — " +
                   "'טקסט זהה' היא הראיה החזקה; 'תיאור קטוע' ו'פרמטר שונה' דורשים בדיקה הנדסית. שום דבר לא נבחר אוטומטית; " +
                   "הקישור חל רק על מחירון זה ונשמר בפרופיל עם שם המאשר.",
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);
        var summary = new TextBlock
        {
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
            Text = $"{proposals.Count(p => p.PresentInCatalog)} סעיפי ספרייה נמצאים במחירון כפי שהם · " +
                   $"{reviewable.Count(p => p.LinkedCode != null)} מקושרים · {reviewable.Count(p => p.LinkedCode == null)} ללא קישור",
        };
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);
        if (staleLinks.Count > 0)
        {
            var stale = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed, Margin = new Thickness(0, 0, 0, 6),
                Text = "קישורים שלא הוחלו: " + string.Join(" · ", staleLinks),
            };
            DockPanel.SetDock(stale, Dock.Top);
            root.Children.Add(stale);
        }

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var markExact = new Button { Content = "סמן את כל ההתאמות בטקסט זהה", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right };
        markExact.Click += (_, _) => MarkUniqueExact();
        footer.Children.Add(markExact);
        var who = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        who.Children.Add(new TextBlock { Text = "מאשר:", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        _approver = new TextBox { Text = defaultApprover, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        who.Children.Add(_approver);
        footer.Children.Add(who);
        var why = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        why.Children.Add(new TextBlock { Text = "סיבה:", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        _reason = new TextBox { TextWrapping = TextWrapping.Wrap, MinHeight = 44, AcceptsReturn = false };
        why.Children.Add(_reason);
        footer.Children.Add(why);
        _status = new TextBlock { Foreground = Brushes.DarkRed, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        var approve = new Button { Content = "אשר קישורים שנבחרו", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        approve.Click += (_, _) => Approve();
        var cancel = new Button { Content = "ביטול", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(approve);
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        root.Children.Add(footer);

        var list = new StackPanel();
        foreach (var proposal in reviewable) list.Children.Add(Section(proposal));
        if (reviewable.Count == 0)
            list.Children.Add(new TextBlock { Text = "כל סעיפי הספרייה נמצאים במחירון הפעיל; אין מה לקשר.", Margin = new Thickness(4) });
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private UIElement Section(EditionLinkProposal proposal)
    {
        var box = new GroupBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        // The heading is the first line of the content, not GroupBox.Header: a header is measured without a width limit,
        // so a long library description would run off the window instead of wrapping.
        var header = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        header.Inlines.Add(new System.Windows.Documents.Run(proposal.LibraryCode) { FlowDirection = System.Windows.FlowDirection.LeftToRight });
        header.Inlines.Add(new System.Windows.Documents.Run($" — {proposal.Reference!.Description} [{proposal.Reference.Unit}]"));
        if (proposal.LinkedCode != null)
            header.Inlines.Add(new System.Windows.Documents.Run($" · מקושר כעת ל־{proposal.LinkedCode}") { Foreground = Brushes.DarkGreen });
        var panel = new StackPanel();
        panel.Children.Add(header);
        var options = new List<(RadioButton, EditionCandidate?)>();
        var keep = new RadioButton
        {
            GroupName = proposal.LibraryCode, IsChecked = true, Margin = new Thickness(0, 2, 0, 2),
            Content = proposal.LinkedCode == null ? "ללא קישור (להשאיר)" : "להשאיר את הקישור הקיים",
        };
        panel.Children.Add(keep);
        options.Add((keep, null));
        foreach (var candidate in proposal.Candidates)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new System.Windows.Documents.Run(KindLabel(candidate.Kind) + " · ") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run(candidate.Code) { FlowDirection = System.Windows.FlowDirection.LeftToRight });
            text.Inlines.Add(new System.Windows.Documents.Run($" · {candidate.Description} · {candidate.Unit.Trim()} · " +
                (candidate.Price is { } price ? price.ToString("N2", CultureInfo.InvariantCulture) + " ₪" : "ללא מחיר")));
            if (candidate.Differences.Count > 0)
            {
                text.Inlines.Add(new System.Windows.Documents.LineBreak());
                text.Inlines.Add(new System.Windows.Documents.Run(string.Join(" · ", candidate.Differences)) { Foreground = Brushes.DarkRed });
            }
            var button = new RadioButton { GroupName = proposal.LibraryCode, Content = text, Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(button);
            options.Add((button, candidate));
        }
        if (proposal.Candidates.Count == 0)
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                Text = "אין במחירון זה מועמד באותה יחידה עם ראיה מספקת — זו בחירה הנדסית; אפשר לבחור סעיף בחלון השיוך.",
            });
        _choices[proposal.LibraryCode] = options;
        box.Content = panel;
        return box;
    }

    private void MarkUniqueExact()
    {
        var marked = 0;
        foreach (var request in UniqueExactMatches(_proposals))
        {
            var option = _choices[request.LibraryCode].FirstOrDefault(o => o.Candidate?.Code == request.CatalogCode);
            if (option.Button == null) continue;
            option.Button.IsChecked = true;
            marked++;
        }
        _status.Foreground = Brushes.DarkGreen;
        _status.Text = $"סומנו {marked} התאמות בטקסט זהה. כל שאר הבחירות נשארו כפי שהיו.";
    }

    private void Approve()
    {
        var requests = _choices.Select(pair => (pair.Key, Chosen: pair.Value.FirstOrDefault(o => o.Button.IsChecked == true).Candidate))
            .Where(x => x.Chosen != null)
            .Select(x => new EditionLinkRequest(x.Key, x.Chosen!.Code, x.Chosen.Kind))
            .ToList();
        _status.Foreground = Brushes.DarkRed;
        if (requests.Count == 0) { _status.Text = "לא נבחר אף קישור חדש."; return; }
        if (ApprovedBy.Length == 0) { _status.Text = "שם המאשר חובה."; return; }
        if (Reason.Length == 0) { _status.Text = "סיבת האישור חובה."; return; }
        Requests = requests;
        DialogResult = true;
    }
}
