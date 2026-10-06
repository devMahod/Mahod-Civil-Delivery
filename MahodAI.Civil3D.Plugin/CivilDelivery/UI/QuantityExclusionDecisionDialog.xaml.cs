using System;
using System.Windows;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>Input-only UI; validation/persistence authority stays in EstimateWorkflowService.</summary>
    public partial class QuantityExclusionDecisionDialog : Window
    {
        public string EngineeringReason => ReasonBox.Text.Trim();
        public string ApprovedBy => ApproverBox.Text.Trim();

        public QuantityExclusionDecisionDialog(
            string ruleKey, string layer, string quantityDisplay, int objectCount,
            string? suggestedReason = null)
        {
            InitializeComponent();
            UiGuard.Attach(this, "אישור החרגת קבוצת כמות");
            QuantitySummaryText.Text =
                $"שכבה: {layer}\nכמות: {quantityDisplay} · {objectCount} עצמים\nחוק: {ruleKey}";
            ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
            if (!string.IsNullOrWhiteSpace(suggestedReason))
                ReasonBox.Text = suggestedReason.Trim();
            RefreshState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => RefreshState();

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var reasonOk = !string.IsNullOrWhiteSpace(ReasonBox.Text);
            var approverOk = !string.IsNullOrWhiteSpace(ApproverBox.Text);
            BtnSave.IsEnabled = reasonOk && approverOk;
            ValidationText.Text = BtnSave.IsEnabled
                ? string.Empty
                : !reasonOk
                    ? "סיבה הנדסית היא שדה חובה"
                    : "שם המאשר הוא שדה חובה";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (BtnSave.IsEnabled) DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
