using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Atomic engineer approval for every unresolved width span shown from one PLAN.
    /// All state lives in <see cref="SpanLabelDecisionModel"/> (unit-tested without
    /// WPF); this window only binds it and forwards clicks. The grid is read-only with
    /// cell templates and scoped guards; it does not open DataGrid edit transactions.
    /// </summary>
    public partial class SectionSpanLabelDecisionDialog : Window
    {
        private readonly SpanLabelDecisionModel _model;

        public string ApprovedBy => ApproverBox.Text.Trim();
        public DateTime ApprovedAtUtc { get; private set; }

        public IReadOnlyList<SectionDecisionProfileService.SpanLabelBatchApproval> Approvals =>
            _model.GetValidatedApprovals();

        public SectionSpanLabelDecisionDialog(SectionPlanRecord record)
            : this(new[] { record })
        {
        }

        public SectionSpanLabelDecisionDialog(
            IReadOnlyList<SectionPlanRecord> records, bool initiallyApproveStrongSuggestions = true,
            IReadOnlyList<ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision>? previousDecisions = null,
            bool includeResolvedSpans = false)
        {
            // The model validates its input before any window exists.
            _model = new SpanLabelDecisionModel(records, initiallyApproveStrongSuggestions,
                previousDecisions, includeResolvedSpans);
            InitializeComponent();
            UiGuard.Attach(this, "שמות רצועות");
            // Opened behind Civil once (live r9): come to the front on first render; the taskbar button keeps it findable.
            DialogActivation.BringForwardOnFirstRender(this);

            SubjectTitle.Text = "שמות רצועות · אצוות התכנון הנוכחית";
            SubjectDetail.Text =
                $"{_model.SectionCount} חתך מסומן · " +
                $"{_model.Rows.Count} רצועות לבדיקה · " +
                "שמות קיימים ניתנים לעריכה; רק שורות שסומנו יישמרו";
            _model.Changed += () => UiGuard.Run("עדכון שורה", RefreshState);
            SpansGrid.ItemsSource = _model.Rows;
            ReviewGroupBox.ItemsSource = _model.ReviewGroups;
            ReviewGroupBox.SelectedIndex = 0;
            BatchLabelBox.ItemsSource = SpanLabelDecisionModel.LabelChoices;
            ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
            RefreshState();
        }

        private void OnApproverChanged(object sender, TextChangedEventArgs e) =>
            UiGuard.Run("מאשר/ת", RefreshState);

        private void OnReviewGroupChanged(object sender, SelectionChangedEventArgs e) =>
            UiGuard.Run("סקירת קבוצת רצועות", () =>
            {
                if (ReviewGroupBox.SelectedItem is not SpanLabelDecisionModel.ReviewGroup group) return;
                SpansGrid.ItemsSource = _model.RowsForReview(group);
                ReviewGroupDetail.Text = group.Detail;
                RefreshState();
            });

        private void OnSelectReviewGroup(object sender, RoutedEventArgs e) =>
            UiGuard.Run("בחירת שורות לסקירה", () =>
            {
                if (ReviewGroupBox.SelectedItem is not SpanLabelDecisionModel.ReviewGroup group) return;
                var rows = _model.RowsForReview(group);
                SpansGrid.SelectedItems.Clear();
                foreach (var row in rows) SpansGrid.SelectedItems.Add(row);
                ValidationText.Text = $"נבחרו {rows.Count} שורות בטבלה; לא אושר שם. בחר שם משותף רק אם הוא מתאים לכל השורות שבחרת.";
            });

        private void OnApplyLabelToSelected(object sender, RoutedEventArgs e) =>
            UiGuard.Run("החלת שם על השורות המסומנות", () =>
            {
                var selected = SpansGrid.SelectedItems.Cast<SpanLabelDecisionModel.SpanRow>().ToList();
                var error = _model.ApplyLabelToSelected(selected, BatchLabelBox.Text);
                if (error != null) ValidationText.Text = error;
                else RefreshState();
            });

        private void OnMarkStrongSuggestions(object sender, RoutedEventArgs e) =>
            UiGuard.Run("סימון הצעות בביטחון גבוה", () =>
            {
                var error = _model.MarkStrongSuggestions(SpansGrid.SelectedItems.Cast<SpanLabelDecisionModel.SpanRow>().ToArray());
                if (error != null) ValidationText.Text = error;
                else RefreshState();
            });

        private void OnClearApprovals(object sender, RoutedEventArgs e) =>
            UiGuard.Run("ניקוי סימון", () =>
            {
                var error = _model.ClearApprovals(SpansGrid.SelectedItems.Cast<SpanLabelDecisionModel.SpanRow>().ToArray());
                if (error != null) ValidationText.Text = error;
                else RefreshState();
            });

        private void OnReassignPreviousName(object sender, RoutedEventArgs e) =>
            UiGuard.Run("שיוך מפורש של שם קודם לגבולות הנוכחיים", () =>
            {
                if (sender is not FrameworkElement { DataContext: SpanLabelDecisionModel.SpanRow row }) return;
                var error = _model.ReassignPreviousName(row, row.SelectedPreviousName);
                if (error != null) ValidationText.Text = error;
                else RefreshState();
            });

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var approver = ApproverBox?.Text;
            BtnSave.IsEnabled = _model.CanSave(approver);
            ValidationText.Text = _model.Validation(approver);
        }

        private void OnSave(object sender, RoutedEventArgs e) =>
            UiGuard.Run("שמירת שמות רצועות", () =>
            {
                RefreshState();
                if (!BtnSave.IsEnabled) return;
                _ = _model.GetValidatedApprovals();
                ApprovedAtUtc = DateTime.UtcNow;
                DialogResult = true;
            });

        private void OnCancel(object sender, RoutedEventArgs e) =>
            UiGuard.Run("ביטול", () => DialogResult = false);
    }
}
