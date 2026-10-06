using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>Explicit source-provenance approval for one ROW source instance.</summary>
    public partial class SectionRowAuthorityDecisionDialog : Window
    {
        public sealed class SourceRow
        {
            public required string Key { get; init; }
            public required string FullHash { get; init; }
            public string Hash => FullHash.Length > 16 ? FullHash[..16] + "…" : FullHash;
            public required string Path { get; init; }
            public required string Xref { get; init; }
        }

        public string? SelectedSourceKey => (SourcesGrid.SelectedItem as SourceRow)?.Key;
        public string ApprovedBy => ApproverBox.Text.Trim();
        public DateTime ApprovedAtUtc { get; private set; }

        public SectionRowAuthorityDecisionDialog(SectionPlanRecord record)
        {
            InitializeComponent();
            UiGuard.Attach(this, "אישור מקור זכות דרך");
            SubjectTitle.Text = $"חתך {record.SectionId ?? record.Cl.SourceHandle} · מקור זכות דרך";
            SubjectDetail.Text =
                $"מצב נוכחי: {record.PresentationCoverage.RowAuthorityState ?? "לא ידוע"} · " +
                $"{record.PresentationCoverage.RowCandidateSourceKeys.Count} מקורות מועמדים";
            var rows = new List<SourceRow>();
            foreach (var key in record.PresentationCoverage.RowCandidateSourceKeys)
            {
                if (!SectionDecisionProfileService.TryParseRowSourceKey(
                        key, out var hash, out var path, out var xref)) continue;
                rows.Add(new SourceRow
                {
                    Key = key,
                    FullHash = hash,
                    Path = path.Length == 0 ? "(שרטוט מארח)" : Bidi.Ltr(Path.GetFileName(path)),
                    Xref = xref.Length == 0 ? "(ללא XREF)" : Bidi.Ltr(xref),
                });
            }
            SourcesGrid.ItemsSource = rows;
            if (rows.Count == 1) SourcesGrid.SelectedIndex = 0;
            ApproverBox.Text = ApproverContext.Session.Name ?? ""; // b24: the session approver or empty, never Windows
            RefreshState();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => RefreshState();

        private void RefreshState()
        {
            if (BtnSave == null) return;
            var selected = SourcesGrid?.SelectedItem as SourceRow;
            var hashBacked = selected?.FullHash is { Length: 64 } hash && hash.All(Uri.IsHexDigit);
            BtnSave.IsEnabled = hashBacked && !string.IsNullOrWhiteSpace(ApproverBox?.Text);
            ValidationText.Text = BtnSave.IsEnabled
                ? string.Empty
                : "יש לבחור מקור עם SHA-256 ולציין מאשר/ת";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            RefreshState();
            if (!BtnSave.IsEnabled) return;
            ApprovedAtUtc = DateTime.UtcNow;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
