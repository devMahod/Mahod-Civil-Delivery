using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Atomic review of every unresolved lane direction displayed from one PLAN.
    /// There is no default flow and no use of offset sign as traffic evidence.
    /// </summary>
    public partial class TrafficDirectionDecisionDialog : Window
    {
        public sealed class FlowChoice
        {
            public required string Label { get; init; }
            public TrafficDirectionEvidenceLogic.RelativeFlow Flow { get; init; }
        }

        public sealed class DirectionRow : INotifyPropertyChanged
        {
            public required SectionPlanRecord Record { get; init; }
            public required SectionTrafficDirectionPlan Direction { get; init; }
            public required IReadOnlyList<FlowChoice> Choices { get; init; }
            private FlowChoice? _selectedChoice;
            private bool _isApproved;
            public FlowChoice? SelectedChoice
            {
                get => _selectedChoice;
                set
                {
                    if (value != null && !Choices.Contains(value))
                        throw new InvalidOperationException("הכיוון שנבחר אינו אחת האפשרויות שהוצגו.");
                    if (ReferenceEquals(value, _selectedChoice)) return;
                    _selectedChoice = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChoice)));
                    if (value != null) IsApproved = true;
                }
            }
            public bool IsApproved
            {
                get => _isApproved;
                set
                {
                    if (_isApproved == value) return;
                    _isApproved = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsApproved)));
                }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
            public string Section => Record.SectionId ?? Record.Cl.SourceHandle;
            public string Alignment => Bidi.Ltr(Record.SelectedAlignment ?? "—");
            public string Station => Record.Station?.ToString("F2", CultureInfo.CurrentCulture) ?? "—";
            public string Offset => Direction.LaneMidOffsetM.ToString("F2", CultureInfo.CurrentCulture) + " מ׳";
            public string Strip => Direction.StripLabel +
                (Direction.TrackEvidenceDigest == null ? "" : " · מסלול חץ במקור (לא גבול נתיב)");
            public string CurrentDirection => !Direction.IsResolved ? "טרם הוכרע" :
                (Direction.Flow == SectionVehicleDirectionPlanner.AlongFlowToken ? "עם כיוון הציר" : "נגד כיוון הציר") +
                (Direction.DirectionSource == "arrow" ? " · מחץ במקור" : " · אישור ידני");
            public string Evidence => TrafficDirectionReasonText.Describe(Direction.State, Direction.Reason);
            /// <summary>The stored codes, for support (tooltip of the evidence cell).</summary>
            public string EvidenceCode => $"{Direction.State} · {Direction.Reason}";
        }

        private static readonly IReadOnlyList<FlowChoice> FlowChoices = new[]
        {
            new FlowChoice
            {
                Label = "עם כיוון הציר — רכב אחורי",
                Flow = TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment,
            },
            new FlowChoice
            {
                Label = "נגד כיוון הציר — רכב קדמי",
                Flow = TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment,
            },
        };

        private readonly List<DirectionRow> _rows;
        private readonly IReadOnlyList<(SectionPlanRecord Record, string Snapshot)> _sourceSnapshots;
        private readonly bool _includeResolvedDirections;
        public string ApprovedBy => ApproverBox.Text.Trim();
        public DateTime ApprovedAtUtc { get; private set; }
        public IReadOnlyList<SectionDecisionProfileService.TrafficDirectionBatchApproval> Approvals
        {
            get
            {
                RequireUnchangedEvidence();
                return _rows.Where(row => !_includeResolvedDirections || row.IsApproved)
                .Select(row => new SectionDecisionProfileService.TrafficDirectionBatchApproval(
                row.Record,
                row.Direction,
                row.SelectedChoice?.Flow ?? TrafficDirectionEvidenceLogic.RelativeFlow.Unknown))
                .ToList();
            }
        }

        public TrafficDirectionDecisionDialog(IReadOnlyList<SectionPlanRecord> records, bool includeResolvedDirections = false)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            _includeResolvedDirections = includeResolvedDirections;
            _sourceSnapshots = records.Select(record => (record, JsonSerializer.Serialize(record))).ToArray();
            InitializeComponent();
            UiGuard.Attach(this, "כיווני נסיעה לאצוות חתכים");
            _rows = records
                .OrderBy(record => record.Station ?? double.MaxValue)
                .ThenBy(record => record.RecordId, StringComparer.Ordinal)
                .SelectMany(record => record.TrafficDirections
                    .Where(direction => includeResolvedDirections || !direction.IsResolved)
                    .OrderBy(direction => direction.LaneMidOffsetM)
                    .Select(direction => new DirectionRow
                    {
                        Record = record,
                        Direction = direction,
                        Choices = FlowChoices,
                        SelectedChoice = includeResolvedDirections && direction.IsResolved ?
                            FlowChoices.SingleOrDefault(choice => SectionVehicleDirectionPlanner.FlowToken(choice.Flow) == direction.Flow) : null,
                        IsApproved = false,
                    }))
                .ToList();
            if (_rows.Count == 0)
                throw new ArgumentException(
                    "At least one unresolved traffic direction is required.", nameof(records));
            foreach (var row in _rows)
                row.PropertyChanged += (_, _) => UiGuard.Run("עדכון כיוון", RefreshState);

            SubjectTitle.Text = "כיווני נסיעה · אצוות התכנון הנוכחית";
            SubjectDetail.Text =
                $"{_rows.Select(row => row.Record.RecordId).Distinct(StringComparer.Ordinal).Count()} חתכים · " +
                $"{_rows.Count} כיוונים · " + (includeResolvedDirections
                    ? "אפשר לערוך כיוון קיים; רק בחירות מסומנות יישמרו, ללא שינוי גבולות הרצועה"
                    : "כל רצועה דורשת הכרעה מפורשת");
            ApprovalColumn.Visibility = includeResolvedDirections ? Visibility.Visible : Visibility.Collapsed;
            BtnSave.Content = includeResolvedDirections ? "שמור כיוונים מסומנים" : "אשר את כל כיווני הנסיעה";
            DirectionsGrid.ItemsSource = _rows;
            BatchFlowBox.ItemsSource = FlowChoices;
            ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
            RefreshState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) =>
            UiGuard.Run("עדכון כיוון ומאשר", RefreshState);

        private void OnApplyFlowToSelected(object sender, RoutedEventArgs e)
            => UiGuard.Run("החלת כיוון על השורות שנבחרו", () =>
        {
            RequireUnchangedEvidence();
            if (BatchFlowBox.SelectedItem is not FlowChoice choice)
            {
                ValidationText.Text = "יש לבחור כיוון לפני החלה על השורות המודגשות";
                return;
            }
            // The batch acts on the highlighted rows (DataGrid selection), not on the «אישור» ticks; the wording says so
            // (guide p.8, audit Z11: ticking three rows and applying changed only the highlighted one).
            var selected = DirectionsGrid.SelectedItems.Cast<DirectionRow>().ToList();
            if (selected.Count == 0)
            {
                ValidationText.Text = "יש להדגיש לפחות שורה אחת בטבלה (לחיצה על השורה; Ctrl או Shift לכמה שורות)";
                return;
            }
            if (selected.Distinct().Count() != selected.Count || selected.Any(row => !_rows.Contains(row)))
                throw new InvalidOperationException("הבחירה אינה שייכת לתכנון שהוצג.");
            foreach (var row in selected) row.SelectedChoice = choice;
            foreach (var row in selected) row.IsApproved = true;
            RefreshState();
        });

        private void RequireUnchangedEvidence()
        {
            if (_sourceSnapshots.Any(item => !string.Equals(item.Snapshot,
                    JsonSerializer.Serialize(item.Record), StringComparison.Ordinal)))
                throw new InvalidOperationException("התכנון השתנה — פתח את בחירת הכיוונים מחדש. לא נשמרו החלטות.");
        }

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var targets = _rows?.Where(row => !_includeResolvedDirections || row.IsApproved).ToArray() ?? Array.Empty<DirectionRow>();
            var missing = targets.Count(row => row.SelectedChoice == null);
            BtnSave.IsEnabled = targets.Length > 0 && missing == 0 &&
                                !string.IsNullOrWhiteSpace(ApproverBox?.Text);
            ValidationText.Text = BtnSave.IsEnabled
                ? $"יישמרו {targets.Length} כיוונים; יתר הבחירות הקיימות יישארו ללא שינוי"
                : (_includeResolvedDirections ? "יש לבחור כיוון לפחות לרצועה אחת, לסמן אישור ולציין מאשר/ת" :
                   "יש לבחור כיוון לכל רצועה ולציין מאשר/ת") +
                  (missing > 0 ? $" · חסרות {missing}" : string.Empty);
        }

        private void OnSave(object sender, RoutedEventArgs e)
            => UiGuard.Run("שמירת כיווני נסיעה", () =>
        {
            RequireUnchangedEvidence();
            RefreshState();
            if (!BtnSave.IsEnabled) return;
            ApprovedAtUtc = DateTime.UtcNow;
            DialogResult = true;
        });

        private void OnCancel(object sender, RoutedEventArgs e) =>
            UiGuard.Run("ביטול כיווני נסיעה", () => DialogResult = false);
    }
}
