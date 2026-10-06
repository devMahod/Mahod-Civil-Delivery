using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Read-only review UI. Eligibility, exact-reason validation and atomic persistence
    /// remain authoritative in EstimateWorkflowService.
    /// </summary>
    public partial class QuantityNoiseBatchDecisionDialog : Window
    {
        public string ApprovedBy => ApproverBox.Text.Trim();

        public QuantityNoiseBatchDecisionDialog(
            IReadOnlyList<QuantityRowViewModel> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                throw new ArgumentException("At least one reviewed noise candidate is required.",
                    nameof(candidates));
            if (candidates.Any(candidate => !candidate.IsBulkNoiseCandidate))
                throw new InvalidOperationException(
                    "The review dialog received a group outside the closed drawing-noise policy.");

            InitializeComponent();
            UiGuard.Attach(this, "אישור מרוכז — סימוני תחנות ועזר");
            CandidatesGrid.ItemsSource = candidates;
            var stations = candidates.Count(candidate =>
                candidate.Verdict?.Kind == QuantitySignificance.Kind.StationGeometry);
            var auxiliary = candidates.Count - stations;
            SummaryText.Text =
                $"{candidates.Count} קבוצות לבדיקה · {stations} שכבות תחנה · " +
                $"{auxiliary} שכבות עזר · {candidates.Sum(candidate => candidate.ObjectCount):N0} עצמים נמדדים";
            ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
            RefreshState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => RefreshState();

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var confirmed = ConfirmBox.IsChecked == true;
            var approverOk = !string.IsNullOrWhiteSpace(ApproverBox.Text);
            BtnSave.IsEnabled = confirmed && approverOk;
            ValidationText.Text = !confirmed
                ? "יש לסמן שנבדקה הרשימה"
                : !approverOk
                    ? "שם המאשר הוא שדה חובה"
                    : string.Empty;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (BtnSave.IsEnabled) DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
