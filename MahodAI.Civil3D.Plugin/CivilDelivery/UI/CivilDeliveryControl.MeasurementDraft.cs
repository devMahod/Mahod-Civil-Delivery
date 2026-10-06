using System;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _measurementDraftExporting;
    private readonly DrawingScopedNotice _exportNotice = new();

    /// <summary>Every export result or failure under the estimate buttons, owned by the active drawing
    /// (<see cref="DrawingScopedNotice"/>).</summary>
    private void SetExportNotice(string text)
    {
        var doc = Doc();
        _exportNotice.Set(text, doc == null ? null : EstimateWorkflowService.DrawingIdentity(doc));
        MeasurementDraftNotice.Text = _exportNotice.Text;
    }

    private void RefreshMeasurementDraftExport(bool profileUsable, bool scanFresh)
    {
        BtnExportMeasurementDraft.IsEnabled = !_measurementDraftExporting && profileUsable &&
            scanFresh && _scan is { Records.Count: > 0 };
    }

    private void OnExportMeasurementDraft(object sender, RoutedEventArgs e)
    {
        var capturedScan = _scan;
        var doc = Doc();
        if (doc == null || capturedScan == null || _measurementDraftExporting) return;
        _measurementDraftExporting = true;
        BtnExportMeasurementDraft.IsEnabled = false;
        try
        {
            if (!ReferenceEquals(capturedScan, _scan))
                throw new InvalidOperationException("הסריקה התחלפה — יש לייצא מהסריקה העדכנית.");
            SetStatus("מייצא את כל המדידות והממצאים לבדיקה, ללא תמחור…");
            var written = _estimate.ExportMeasurementDraft(doc, capturedScan);
            SetExportNotice($"יוצאו {written.RecordCount:N0} רשומות וכל הממצאים שנשמרו בסריקה. " +
                "טיוטה ללא תמחור, לא אומדן מאושר. מחיר ריק אינו אפס.\n" + written.XlsxPath);
            Log(MeasurementDraftNotice.Text);
            SetStatus("טיוטת המדידות יוצאה — לא אומדן מאושר");
        }
        catch (Exception ex)
        {
            var displayError = MeasurementDraftErrorText.Describe(ex);
            SetExportNotice("טיוטת המדידות לא פורסמה: " + displayError.Message);
            ShowError("ייצוא מדידות לבדיקה", displayError);
        }
        finally
        {
            _measurementDraftExporting = false;
            RefreshGates();
        }
    }
}
