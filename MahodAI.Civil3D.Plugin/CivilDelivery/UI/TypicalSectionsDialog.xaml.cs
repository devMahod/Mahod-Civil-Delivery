using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// The typical-sections popup (2026-10): review the marks before creating (remove one, add more, create), the
    /// result after (numbers that count up, every section with "show"), or what is missing when the run stopped.
    /// Presentation only: the panel builds the rows and acts on <see cref="Choice"/>, the removed marks and the
    /// show requests.
    /// </summary>
    public partial class TypicalSectionsDialog : Window
    {
        public enum Outcome { None, Create, AddMore, Fix, Sheet }

        /// <summary>One line of a stopped run: an error, or a step the tool takes itself (<see cref="Fixable"/>).</summary>
        public sealed class IssueRow
        {
            public required string Text { get; init; }
            public bool Fixable { get; init; }
        }

        public sealed class PickRow
        {
            public required int Number { get; init; }
            public required string Station { get; init; }
            public required string Alignment { get; init; }
            public required string LineHandle { get; init; }
        }

        public sealed class SectionRow
        {
            public required string Title { get; init; }
            public required string Detail { get; init; }
            public required bool Good { get; init; }
            public string? BlockHandle { get; init; }
            public bool CanShow => BlockHandle != null;
        }

        private readonly ObservableCollection<PickRow> _picks = new();
        private readonly List<(TextBlock Target, double Value, string Suffix)> _countUps = new();

        public Outcome Choice { get; private set; }

        /// <summary>Marker lines the engineer removed in the review; the panel erases them from the drawing.</summary>
        public List<string> RemovedLineHandles { get; } = new();

        /// <summary>The block handle of a created section the engineer asked to see.</summary>
        public event Action<string>? ShowSectionRequested;

        /// <summary>The run folder of a finished run, for "תיקיית הריצה".</summary>
        public string? RunFolder { get; set; }

        private TypicalSectionsDialog()
        {
            InitializeComponent();
            FontFamily = CivilDeliveryTheme.Font;
            UiGuard.Attach(this, "חתכים טיפוסיים");
            Loaded += (_, _) => StartCountUps();
        }

        /// <summary>Before creating: every mark, with remove; create and add-more.</summary>
        public static TypicalSectionsDialog ForReview(IReadOnlyList<PickRow> picks, string subtitle, double halfWidthM)
        {
            var dialog = new TypicalSectionsDialog();
            foreach (var pick in picks) dialog._picks.Add(pick);
            dialog.PickList.ItemsSource = dialog._picks;
            dialog.HeadSub.Text = subtitle;
            dialog.StatLabel1.Text = "חתכים";
            dialog.StatLabel2.Text = "תוואים";
            dialog.StatLabel3.Text = "רוחב חתך";
            dialog.CountUp(dialog.StatValue3, Math.Round(halfWidthM * 2, 1), " מ'");
            dialog.RefreshReview();
            dialog.CountUp(dialog.StatValue1, picks.Count, "");
            dialog.CountUp(dialog.StatValue2, picks.Select(p => p.Alignment).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "");
            return dialog;
        }

        /// <summary>After a run that created sections (all or some): numbers, the sections with show, notes.</summary>
        public static TypicalSectionsDialog ForResult(
            int created, int named, int unmatched, IReadOnlyList<SectionRow> sections, string subtitle,
            IReadOnlyList<string> notes, bool stoppedEarly, bool offerSheet = false)
        {
            var dialog = new TypicalSectionsDialog();
            dialog.Title = "חתכים טיפוסיים · תוצאה";
            dialog.HeadTitle.Text = created == 1 ? "נוצר חתך אחד" : $"נוצרו {created} חתכים";
            dialog.HeadSub.Text = subtitle;
            dialog.SetHeadChip(stoppedEarly ? "IconLayers" : "IconCheck", good: !stoppedEarly);
            dialog.PickList.Visibility = Visibility.Collapsed;
            dialog.SectionList.Visibility = Visibility.Visible;
            dialog.SectionList.ItemsSource = sections;
            dialog.StatLabel1.Text = "חתכים";
            dialog.StatLabel2.Text = "רצועות עם שם";
            dialog.StatLabel3.Text = "ללא התאמה";
            dialog.SetIcon(dialog.StatIcon2, "IconLayers");
            dialog.SetIcon(dialog.StatIcon3, "IconEyeOff");
            dialog.CountUp(dialog.StatValue1, created, "");
            dialog.CountUp(dialog.StatValue2, named, "");
            dialog.CountUp(dialog.StatValue3, unmatched, "");
            dialog.BtnCreate.Visibility = Visibility.Collapsed;
            dialog.BtnAddMore.Content = "חתכים נוספים";
            dialog.BtnFolder.Visibility = Visibility.Visible;
            dialog.BtnClose.Content = "סגור";
            // The sections on the office's A0 sheet, one click (2026-11-03: it was a typed command nobody found).
            if (offerSheet)
            {
                dialog.BtnSheet.Visibility = Visibility.Visible;
                dialog.BtnSheet.IsDefault = true;
            }
            else
                dialog.BtnClose.Style = (Style)dialog.FindResource("PrimaryButton");
            dialog.ShowNotes(notes);
            return dialog;
        }

        /// <summary>
        /// When the run is only a step short (every line is one the tool mends: rebuild a corridor, add its top surface):
        /// the steps in green and one green button that takes them and creates the sections (2026-11-03, a coworker's
        /// unbuilt R-14 read as two errors). Closes with <see cref="Outcome.Fix"/>.
        /// </summary>
        public static TypicalSectionsDialog ForSteps(IReadOnlyList<string> steps, string subtitle, string goLabel)
        {
            var dialog = new TypicalSectionsDialog();
            dialog.Title = "חתכים טיפוסיים · עוד שלב";
            dialog.HeadTitle.Text = steps.Count == 1 ? "עוד שלב אחד לפני היצירה" : $"עוד {steps.Count} שלבים לפני היצירה";
            dialog.HeadSub.Text = subtitle;
            dialog.SetHeadChip("IconRefresh", good: true);
            dialog.Stats.Visibility = Visibility.Collapsed;
            dialog.PickList.Visibility = Visibility.Collapsed;
            dialog.BlockerList.Visibility = Visibility.Visible;
            dialog.BlockerList.ItemsSource = steps.Select(step => new IssueRow { Text = step, Fixable = true }).ToList();
            dialog.BtnAddMore.Visibility = Visibility.Collapsed;
            dialog._fixOffered = true;
            dialog.BtnCreate.Content = goLabel;
            dialog.BtnCreate.Style = (Style)dialog.FindResource("GoButton");
            dialog.BtnCreate.Tag = "refresh";
            dialog.BtnCreate.IsEnabled = true;
            dialog.BtnClose.Content = "לא עכשיו";
            dialog.ShowNotes(new[]
            {
                "הכלי בונה את הקורידור בשרטוט הפתוח ואז יוצר את החתכים. אפשר לבטל את הבנייה עם U.",
            });
            return dialog;
        }

        /// <summary>
        /// When the run stopped before drawing: what is missing, one line each. With <paramref name="fixLabel"/> the
        /// tool can mend some of it itself (rebuild a corridor, add its top surface): the primary button says so and
        /// closes with <see cref="Outcome.Fix"/>.
        /// </summary>
        public static TypicalSectionsDialog ForBlocked(
            IReadOnlyList<string> blockers, string subtitle, string? fixLabel = null, IReadOnlyCollection<string>? fixable = null)
        {
            var dialog = new TypicalSectionsDialog();
            dialog.Title = "חתכים טיפוסיים · לא נוצרו";
            dialog.HeadTitle.Text = "החתכים לא נוצרו";
            dialog.HeadSub.Text = subtitle;
            dialog.SetHeadChip("IconX", good: false, blocked: true);
            dialog.Stats.Visibility = Visibility.Collapsed;
            dialog.PickList.Visibility = Visibility.Collapsed;
            dialog.BlockerList.Visibility = Visibility.Visible;
            dialog.BlockerList.ItemsSource = blockers
                .Select(line => new IssueRow { Text = line, Fixable = fixLabel != null && fixable?.Contains(line) == true })
                .ToList();
            dialog.BtnAddMore.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(fixLabel))
            {
                dialog.BtnCreate.Visibility = Visibility.Collapsed;
                dialog.BtnClose.Content = "סגור";
                dialog.BtnClose.Style = (Style)dialog.FindResource("PrimaryButton");
            }
            else
            {
                dialog._fixOffered = true;
                dialog.BtnCreate.Content = fixLabel;
                dialog.BtnCreate.IsEnabled = true;
                dialog.BtnClose.Content = "סגור";
            }
            return dialog;
        }

        private bool _fixOffered;

        public IReadOnlyList<PickRow> RemainingPicks => _picks.ToList();

        private void RefreshReview()
        {
            var count = _picks.Count;
            HeadTitle.Text = count switch
            {
                0 => "אין חתכים מסומנים",
                1 => "חתך אחד מוכן ליצירה",
                _ => $"{count} חתכים מוכנים ליצירה",
            };
            BtnCreate.Content = count == 1 ? "צור חתך" : $"צור {count} חתכים";
            BtnCreate.IsEnabled = count > 0;
            StatValue1.Text = count.ToString(CultureInfo.InvariantCulture);
            StatValue2.Text = _picks.Select(p => p.Alignment).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                .ToString(CultureInfo.InvariantCulture);
        }

        private void OnRemovePick(object sender, RoutedEventArgs e) => UiGuard.Run("הסרת סימון", () =>
        {
            if ((sender as FrameworkElement)?.DataContext is not PickRow row) return;
            _picks.Remove(row);
            RemovedLineHandles.Add(row.LineHandle);
            RefreshReview();
        });

        private void OnCreate(object sender, RoutedEventArgs e) => UiGuard.Run("יצירת חתכים", () =>
        {
            if (_fixOffered)
            {
                Choice = Outcome.Fix;
                CloseWith(true);
                return;
            }
            if (_picks.Count == 0) return;
            Choice = Outcome.Create;
            CloseWith(true);
        });

        private void OnAddMore(object sender, RoutedEventArgs e) => UiGuard.Run("סימון חתכים נוספים", () =>
        {
            Choice = Outcome.AddMore;
            CloseWith(true);
        });

        private void OnSheet(object sender, RoutedEventArgs e) => UiGuard.Run("גיליון חתכים", () =>
        {
            Choice = Outcome.Sheet;
            CloseWith(true);
        });

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Choice = Outcome.None;
            CloseWith(false);
        }

        private void OnShowSection(object sender, RoutedEventArgs e) => UiGuard.Run("הצגת חתך", () =>
        {
            if ((sender as FrameworkElement)?.DataContext is SectionRow { BlockHandle: { } handle })
                ShowSectionRequested?.Invoke(handle);
        });

        private void OnOpenFolder(object sender, RoutedEventArgs e) => UiGuard.Run("תיקיית הריצה", () =>
        {
            if (!string.IsNullOrWhiteSpace(RunFolder) && System.IO.Directory.Exists(RunFolder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + RunFolder + "\"")
                { UseShellExecute = true });
        });

        /// <summary>A modal review returns through DialogResult; the modeless result window just closes.</summary>
        private void CloseWith(bool result)
        {
            try { DialogResult = result; }
            catch (InvalidOperationException) { Close(); }
        }

        private void ShowNotes(IReadOnlyList<string> notes)
        {
            if (notes.Count == 0) return;
            NotesExpander.Visibility = Visibility.Visible;
            NotesExpander.Header = notes.Count == 1 ? "הערה אחת" : $"{notes.Count} הערות";
            NotesText.Text = string.Join("\n", notes);
        }

        private void SetHeadChip(string iconKey, bool good, bool blocked = false)
        {
            SetIcon(HeadIcon, iconKey);
            if (good)
            {
                HeadChip.Background = (Brush)FindResource("AccentTint");
                HeadIcon.Stroke = (Brush)FindResource("AccentInk");
            }
            else if (blocked)
            {
                HeadChip.Background = (Brush)FindResource("BlockedTint");
                HeadIcon.Stroke = (Brush)FindResource("Blocked");
            }
        }

        private void SetIcon(System.Windows.Shapes.Path path, string iconKey) =>
            path.Data = (Geometry)FindResource(iconKey);

        /// <summary>Numbers count up from zero when the window opens: a small motion that draws the eye to them.</summary>
        private void CountUp(TextBlock target, double value, string suffix)
        {
            target.Text = Format(value) + suffix;
            _countUps.RemoveAll(item => ReferenceEquals(item.Target, target));
            _countUps.Add((target, value, suffix));
        }

        private void StartCountUps()
        {
            if (_countUps.Count == 0 || SystemParameters.ClientAreaAnimation == false) return;
            var started = DateTime.UtcNow;
            const double durationMs = 420;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / durationMs);
                var eased = 1 - Math.Pow(1 - t, 3);
                foreach (var (target, value, suffix) in _countUps)
                {
                    var shown = value * eased;
                    target.Text = (value % 1 == 0 ? Math.Round(shown) : Math.Round(shown, 1)).ToString(CultureInfo.InvariantCulture) + suffix;
                }
                if (t >= 1)
                {
                    timer.Stop();
                    foreach (var (target, value, suffix) in _countUps) target.Text = Format(value) + suffix;
                }
            };
            timer.Start();
        }

        private static string Format(double value) =>
            value % 1 == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
