using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Review-only presentation. The workflow service computes the closed eligibility
    /// set and revalidates it after this window closes; this dialog supplies only the
    /// named, explicit human approval.
    /// </summary>
    public partial class ProvenMappingBatchDecisionDialog : Window
    {
        public string ApprovedBy => ApproverBox.Text.Trim();

        public ProvenMappingBatchDecisionDialog(
            IReadOnlyList<EstimateWorkflowService.ProvenBatchMappingCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                throw new ArgumentException(
                    "At least one proven mapping candidate is required.", nameof(candidates));
            if (candidates.Any(candidate =>
                    string.IsNullOrWhiteSpace(candidate.Approval.RuleKey) ||
                    string.IsNullOrWhiteSpace(candidate.Approval.CatalogCode) ||
                    candidate.Price < 0))
                throw new InvalidOperationException(
                    "The batch review received an incomplete mapping candidate.");

            InitializeComponent();
            UiGuard.Attach(this, "אישור מיפויים חד־משמעיים");
            CandidatesGrid.ItemsSource = candidates;
            SummaryText.Text =
                $"{candidates.Count} קבוצות · {candidates.Sum(candidate => candidate.ObjectCount):N0} עצמים · " +
                $"{candidates.Select(candidate => candidate.Approval.CatalogCode).Distinct(StringComparer.OrdinalIgnoreCase).Count()} סעיפי מחירון";
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
                ? "יש לסמן שנבדקה כל הטבלה"
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
