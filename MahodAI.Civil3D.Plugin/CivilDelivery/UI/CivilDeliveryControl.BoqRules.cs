using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MessageBox = System.Windows.MessageBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _boqRulesExporting;

    /// <summary>Only a scanned drawing with one declared source role uses the embedded rules route.</summary>
    private bool ProjectHasBoqRules()
    {
        try
        {
            return _profile != null && _scan != null && BoqRulesRoutePolicy.AppliesTo(
                BoqRuleset.LoadEmbedded6422(), _profile.ProfileId, _scan.SourceDrawing);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RefreshBoqRulesExport(bool profileUsable, bool scanFresh)
    {
        var applies = ProjectHasBoqRules();
        BtnExportBoqRules.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
        BtnExportBoqRules.IsEnabled = !_boqRulesExporting && profileUsable &&
            applies && scanFresh && _scan is { Records.Count: > 0 };
    }

    /// <summary>
    /// "כתב כמויות לפי כללים": the NTI-format bill of quantities from the rules (Natali's method and guidelines) over the
    /// latest scans of the project's drawings. Written into its own run folder; a draft for review, never an approval.
    /// </summary>
    private void OnExportBoqRules(object sender, RoutedEventArgs e)
    {
        var capturedScan = _scan;
        var doc = Doc();
        if (doc == null || capturedScan == null || _boqRulesExporting) return;
        if (!ProjectHasBoqRules())
        {
            SetStatus("הקובץ אינו מקור של כללי הפרויקט. המשך בשיוך קבוצות יחד ובתמחור מהמחירון הפעיל.");
            RefreshGates();
            return;
        }
        // Several drawings measured in this profile: the engineer chooses — the latest measurement is not the estimate's
        // source by being latest (review 01/10, an experiment copy replaced it). Cancel = no export.
        string? selectedCorridor = null;
        try
        {
            var corridorOptions = _estimate.CorridorSourceOptions(capturedScan);
            if (corridorOptions.Count > 1)
            {
                var picker = new CorridorSourcePickerDialog(corridorOptions);
                if (CivilModalHost.ShowFromPalette(picker) != true || string.IsNullOrWhiteSpace(picker.SelectedDrawingPath))
                {
                    SetStatus("הייצוא בוטל — לא נבחרה מדידת קורידורים");
                    return;
                }
                selectedCorridor = picker.SelectedDrawingPath;
            }
        }
        catch (Exception ex)
        {
            ShowError("כתב כמויות לפי כללים", ex);
            return;
        }
        _boqRulesExporting = true;
        BtnExportBoqRules.IsEnabled = false;
        string? createdPath = null;
        string? corridorSummary = null;
        try
        {
            if (!ReferenceEquals(capturedScan, _scan))
                throw new InvalidOperationException("הסריקה התחלפה — יש לייצא מהסריקה העדכנית.");
            EstimateWorkflowService.BoqRulesExportResult? result = null;
            RunBusy("בונה כתב כמויות לפי הכללים מהסריקות האחרונות של קובצי הפרויקט…",
                () => result = _estimate.ExportBoqRules(doc, capturedScan, selectedCorridor));
            var written = result ?? throw new InvalidOperationException("הייצוא לא החזיר תוצאה.");
            createdPath = written.XlsxPath;
            corridorSummary = written.Corridor;
            var missingRoles = written.MissingRoles.Count > 0
                ? $" חסרות סריקות לקבצים: {string.Join(", ", written.MissingRoles)} — השורות שלהם חסרות בקובץ."
                : "";
            var missingObjects = written.MissingObjects > 0
                ? $" {written.MissingObjects:N0} עצמים לא נמדדו (גיליון 'חסרים')."
                : "";
            SetExportNotice(
                $"נוצר כתב כמויות לפי כללים (טיוטה, לא אומדן מאושר): {written.LineCount} סעיפים, {written.MappedLineCount} עם סעיף מחירון. " +
                $"קבצים: {string.Join(", ", written.UsedRoles)}.{missingRoles}{missingObjects}\n{written.Corridor}\n" + SupportPackage.DisplayPath(written.XlsxPath));
            Log(MeasurementDraftNotice.Text.Replace(Environment.NewLine, " ").Replace("\n", " "));
            foreach (var note in written.Notes.Take(20)) Log("כתב כמויות לפי כללים: " + note);
            SetStatus("כתב הכמויות לפי כללים נוצר — לבדיקה ולאישור הנדסי");
        }
        catch (Exception ex)
        {
            SetExportNotice("כתב הכמויות לפי כללים לא נוצר: " + ex.Message);
            ShowError("כתב כמויות לפי כללים", ex);
        }
        finally
        {
            _boqRulesExporting = false;
            RefreshGates();
        }

        if (createdPath == null) return;
        // Opening Excel is a convenience; its failure never reads as a failed export. Right-to-left, so the question
        // "לפתוח ב-Excel?" does not wrap as mixed-direction text (guide p.12, audit Z26).
        try
        {
            // The corridor chapters (51.01–51.04) are named in the question itself: in the bill, or why not.
            // The file name is shown on its own line for recognition; the full path is in its own left-to-right field
            // (b23 — a full path wrapped at '\' in the narrow message box, b14 live 17:31). Yes stays the default (Enter).
            if (RtlMessageBox.ShowPath("נוצר כתב כמויות לפי כללים:", createdPath, $"{corridorSummary}\n\nלפתוח ב-Excel?",
                    "כתב כמויות לפי כללים", MessageBoxButton.YesNo, MessageBoxImage.Information,
                    System.IO.Path.GetFileName(createdPath)) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(createdPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("הקובץ נוצר, אבל Excel לא נפתח: " + ex.Message);
            Log("פתיחת כתב הכמויות ב-Excel נכשלה: " + ex.Message);
        }
    }
}
