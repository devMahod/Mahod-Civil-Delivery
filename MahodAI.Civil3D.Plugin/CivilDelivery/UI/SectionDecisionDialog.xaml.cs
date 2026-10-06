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
    /// <summary>Compact, explicit review UI for one unresolved CL row.</summary>
    public partial class SectionDecisionDialog : Window
    {
        public enum DecisionKind { Crossing, Exclusion }

        public sealed class CandidateRow
        {
            public required AlignmentCrossing Crossing { get; init; }
            public string Alignment => Bidi.Ltr(Crossing.AlignmentName);
            public string Station => Crossing.Station.ToString("F3");
            public string Point => $"{Crossing.Point[0]:F3} / {Crossing.Point[1]:F3}";
            public string Gap => Crossing.GapDistance.ToString("F3");
        }

        public sealed class FindingChoice
        {
            public required string Code { get; init; }
            public int Count { get; init; }
            public string Label => $"{Code} · {Count} רשומות";
        }

        public DecisionKind SelectedKind { get; private set; }
        public AlignmentCrossing? SelectedCrossing { get; private set; }
        public string ApprovedBy => ApproverBox.Text.Trim();
        public string Reason => ReasonBox.Text.Trim();
        public string? FindingCode => (FindingCodeBox.SelectedItem as FindingChoice)?.Code;
        public bool BatchSameFindingCode => BatchCheck.IsChecked == true;

        public SectionDecisionDialog(
            SectionPlanRecord record,
            SectionPlan plan,
            DecisionKind initialKind)
        {
            InitializeComponent();
            UiGuard.Attach(this, "הכרעת רשומת חתך");
            SubjectTitle.Text = $"חתך {record.SectionId ?? record.Cl.SourceHandle}";
            SubjectDetail.Text =
                $"CL handle {Bidi.Ltr(record.Cl.SourceHandle)} · {Bidi.Ltr(record.Cl.SourceLayer)} · " +
                $"{record.CandidateCrossings.Count} חציות מועמדות";

            CandidatesGrid.ItemsSource = record.CandidateCrossings
                .OrderBy(c => c.AlignmentName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Station)
                .Select(c => new CandidateRow { Crossing = c })
                .ToList();

            var codes = record.Findings
                .Where(SectionPlanLogic.IsExcludableFinding)
                .Select(f => f.Code)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .Select(code => new FindingChoice
                {
                    Code = code,
                    Count = plan.Records.Count(r =>
                        r.Action != PlanAction.Excluded &&
                        r.Findings.Any(f => string.Equals(f.Code, code, StringComparison.Ordinal))),
                })
                .OrderBy(c => c.Code, StringComparer.Ordinal)
                .ToList();
            FindingCodeBox.ItemsSource = codes;
            FindingCodeBox.SelectedIndex = codes.Count > 0 ? 0 : -1;

            ResolveTab.IsEnabled = record.CandidateCrossings.Count > 0;
            ModeTabs.SelectedIndex = initialKind == DecisionKind.Crossing && ResolveTab.IsEnabled ? 0 : 1;
            ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
            if (record.CandidateCrossings.Count == 1) CandidatesGrid.SelectedIndex = 0;
            RefreshState();
        }

        private void OnModeChanged(object sender, SelectionChangedEventArgs e) => RefreshState();
        private void OnCandidateChanged(object sender, SelectionChangedEventArgs e) => RefreshState();
        private void OnFindingChanged(object sender, SelectionChangedEventArgs e) => RefreshState();
        private void OnInputChanged(object sender, RoutedEventArgs e) => RefreshState();

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var crossingMode = ModeTabs.SelectedIndex == 0 && ResolveTab.IsEnabled;
            BtnSave.Content = crossingMode ? "אשר חצייה" : "אשר החרגה";

            var approverOk = !string.IsNullOrWhiteSpace(ApproverBox?.Text);
            if (crossingMode)
            {
                BtnSave.IsEnabled = approverOk && CandidatesGrid.SelectedItem is CandidateRow;
                ValidationText.Text = BtnSave.IsEnabled ? "" : "יש לבחור חצייה ולציין מאשר";
                return;
            }

            var choice = FindingCodeBox?.SelectedItem as FindingChoice;
            if (BatchHint != null)
                BatchHint.Text = choice == null
                    ? "אין קוד ממצא להחרגה"
                    : BatchCheck.IsChecked == true
                        ? $"האישור יחול על {choice.Count} רשומות עם {choice.Code}"
                        : $"האישור יחול על הרשומה הנבחרת בלבד ({choice.Code})";
            BtnSave.IsEnabled = approverOk && choice != null &&
                                !string.IsNullOrWhiteSpace(ReasonBox?.Text);
            ValidationText.Text = BtnSave.IsEnabled ? "" : "קוד ממצא, סיבה ומאשר הם שדות חובה";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (!BtnSave.IsEnabled) return;
            if (ModeTabs.SelectedIndex == 0 && ResolveTab.IsEnabled)
            {
                SelectedKind = DecisionKind.Crossing;
                SelectedCrossing = ((CandidateRow)CandidatesGrid.SelectedItem).Crossing;
            }
            else
            {
                SelectedKind = DecisionKind.Exclusion;
            }
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
