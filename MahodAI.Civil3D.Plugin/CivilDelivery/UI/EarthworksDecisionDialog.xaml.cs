using System;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Thin input-only UI. Validation and persistence remain in
    /// EstimateWorkflowService so commands and future tool routes share the same gate.
    /// </summary>
    public partial class EarthworksDecisionDialog : Window
    {
        public bool IncludeEarthworks => IncludeRadio.IsChecked == true;
        public string? EngineeringReason =>
            IncludeEarthworks ? null : ReasonBox.Text.Trim();
        public string ApprovedBy => ApproverBox.Text.Trim();

        public EarthworksDecisionDialog(
            EstimateWorkflowService.EarthworksDecisionSummary current)
        {
            InitializeComponent();
            UiGuard.Attach(this, "החלטת היקף עבודות עפר");
            CurrentStateText.Text = current.DisplayText;
            // b24: a new decision starts from the session approver (or empty) — never the previous decider or Windows.
            ApproverBox.Text = ApproverContext.Session.Name ?? "";

            if (current.Status == EstimateWorkflowService.EarthworksDecisionStatus.Included)
                IncludeRadio.IsChecked = true;
            else if (current.Status == EstimateWorkflowService.EarthworksDecisionStatus.Excluded)
            {
                ExcludeRadio.IsChecked = true;
                ReasonBox.Text = current.Reason ?? string.Empty;
            }

            RefreshState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => RefreshState();

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var hasChoice = IncludeRadio.IsChecked == true || ExcludeRadio.IsChecked == true;
            var excluded = ExcludeRadio.IsChecked == true;
            ReasonBox.IsEnabled = excluded;
            var approverOk = !string.IsNullOrWhiteSpace(ApproverBox.Text);
            var reasonOk = !excluded || !string.IsNullOrWhiteSpace(ReasonBox.Text);
            BtnSave.IsEnabled = hasChoice && approverOk && reasonOk;

            ValidationText.Text = BtnSave.IsEnabled
                ? string.Empty
                : !hasChoice
                    ? "יש לבחור אם עבודות העפר נכללות"
                    : !approverOk
                        ? "שם המאשר הוא שדה חובה"
                        : "סיבה הנדסית היא שדה חובה בהחרגה";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (!BtnSave.IsEnabled) return;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
