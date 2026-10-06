using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Shared;
using MessageBox = System.Windows.MessageBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _corridorBoqExporting;

    /// <summary>The button appears in a drawing that has corridors (for example 6422-CIVIL-WEST); reading them changes nothing.</summary>
    private void RefreshCorridorBoqExport(bool profileUsable)
    {
        var hasCorridors = false;
        try { hasCorridors = Doc() != null && CivilApplication.ActiveDocument?.CorridorCollection.Count > 0; }
        catch { hasCorridors = false; }
        BtnExportCorridorBoq.Visibility = hasCorridors ? Visibility.Visible : Visibility.Collapsed;
        var supportedProject = Estimate.CorridorBoqExportGuard.SupportsEmbeddedRules(_profile?.ProfileId);
        BtnExportCorridorBoq.IsEnabled = hasCorridors && profileUsable && _profile != null && supportedProject && !_corridorBoqExporting;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(BtnExportCorridorBoq, true);
        BtnExportCorridorBoq.ToolTip = supportedProject
            ? "מדידת קורידורים לפי כללי פרויקט 6422 ומחירון נת״י הכלול בהם; טיוטה לאישור הנדסי."
            : "המסלול כולל כעת כללי פרויקט 6422 בלבד. אם זהו פרויקט 6422, בחר את הפרופיל שלו; לפרויקט אחר יש להגדיר כללים ומחירון מתאימים — אין לשנות את מזהה הפרויקט כדי לעקוף בדיקה זו.";
    }

    private void OnExportCorridorBoq(object sender, RoutedEventArgs e)
    {
        var doc = Doc();
        var profile = _profile;
        if (doc == null || profile == null || _corridorBoqExporting) return;
        _corridorBoqExporting = true;
        BtnExportCorridorBoq.IsEnabled = false;
        string? createdPath = null;
        try
        {
            Estimate.EstimateWorkflowService.CorridorBoqExportResult? result = null;
            RunBusy("מודד חפירה, מילוי, מצעים ואספלט מהקורידורים של השרטוט…",
                () => result = _estimate.ExportCorridorBoq(doc, profile));
            var written = result ?? throw new InvalidOperationException("הייצוא לא החזיר תוצאה.");
            createdPath = written.XlsxPath;
            SetExportNotice(
                $"נוצר כתב כמויות מקורידורים (טיוטה, לא אומדן מאושר): {written.Corridors} קורידורים, {written.Stations} חתכים" +
                (written.FailedStations > 0 ? $", {written.FailedStations} חתכים שלא נקראו" : "") +
                (written.Complete ? "" : " — חלק מהמדידה חלקית ואינו בסה\"כ (גיליון 'כיסוי')") + ".\n" + SupportPackage.DisplayPath(written.XlsxPath));
            MeasurementDraftNotice.Visibility = Visibility.Visible;
            Log(MeasurementDraftNotice.Text.Replace("\n", " "));
            foreach (var note in written.Notes.Take(20)) Log("כמויות מקורידורים: " + note);
            SetStatus("כתב הכמויות מקורידורים נוצר — לבדיקה ולאישור הנדסי");
        }
        catch (Exception ex)
        {
            SetExportNotice("כתב הכמויות מקורידורים לא נוצר: " + ex.Message);
            ShowError("כמויות מקורידורים", ex);
        }
        finally
        {
            _corridorBoqExporting = false;
            RefreshGates();
        }
        if (createdPath == null) return;
        try
        {
            // Yes stays the default (Enter), as the explicit default of the message box this replaces (b23).
            if (RtlMessageBox.ShowPath("נוצר כתב כמויות מקורידורים:", createdPath, "לפתוח ב-Excel?",
                    "כמויות מקורידורים", MessageBoxButton.YesNo, MessageBoxImage.Information,
                    System.IO.Path.GetFileName(createdPath)) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(createdPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("הקובץ נוצר, אבל Excel לא נפתח: " + ex.Message);
        }
    }
}
