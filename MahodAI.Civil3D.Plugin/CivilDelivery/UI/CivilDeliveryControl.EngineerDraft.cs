using System;
using System.Diagnostics;
using System.Windows;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;
using MessageBox = System.Windows.MessageBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _engineerDraftExporting;
    private object? _engineerDraftNoticeScan;

    private void RefreshEngineerDraftExport(bool profileUsable, bool scanFresh)
    {
        BtnExportEngineerDraft.IsEnabled = !_engineerDraftExporting && profileUsable && _profile != null &&
            scanFresh && _scan is { Records.Count: > 0 };
        // 30.09 (Arthur): where the project rules build the bill, this older draft must not look like the primary
        // action next to "כתב כמויות לפי כללים…" — it keeps the plain button style there.
        var engineerDraftStyle = ProjectHasBoqRules() ? null : (Style)FindResource("PrimaryButton");
        if (!ReferenceEquals(BtnExportEngineerDraft.Style, engineerDraftStyle))
            BtnExportEngineerDraft.Style = engineerDraftStyle;
        // A notice describes one scan; once the scan is replaced it must not look current.
        if (EngineerDraftNotice.Visibility == Visibility.Visible && !ReferenceEquals(_engineerDraftNoticeScan, _scan))
        {
            EngineerDraftNotice.Visibility = Visibility.Collapsed;
            _engineerDraftNoticeScan = null;
        }
    }

    /// <summary>
    /// One click after a scan: an editable, priced BoQ draft for the engineer, built
    /// from the design models and the mappings already approved in the profile.
    /// No per-layer mapping, no approval, no profile write.
    /// </summary>
    private void OnExportEngineerDraft(object sender, RoutedEventArgs e)
    {
        var capturedScan = _scan;
        var profile = _profile;
        var doc = Doc();
        if (doc == null || capturedScan == null || profile == null || _engineerDraftExporting) return;
        _engineerDraftExporting = true;
        BtnExportEngineerDraft.IsEnabled = false;
        string? createdPath = null;
        try
        {
            if (!ReferenceEquals(capturedScan, _scan))
                throw new InvalidOperationException("הסריקה התחלפה — יש לייצא מהסריקה העדכנית.");
            EngineerBoqDraftExcelWriter.WriteResult? result = null;
            // The export hashes the drawing and the published scan twice and writes the workbook:
            // show the busy overlay and the progress window instead of a silent, frozen palette.
            RunBusy("בונה טיוטת כתב כמויות להנדסה מתוך הסריקה…",
                () => result = _estimate.ExportEngineerDraft(doc, capturedScan, profile));
            var written = result ?? throw new InvalidOperationException("הייצוא לא החזיר תוצאה.");
            createdPath = written.XlsxPath;
            EngineerDraftNotice.Text =
                $"נוצרה טיוטת כתב כמויות להנדסה — לא אומדן. {written.BoqRowCount} שורות מוצעות, ל-{written.PricedLineCount} מהן יש מחיר. " +
                $"סכום ביניים חלקי של השורות עם מחיר בלבד: {written.PricedTotalAtDefaults:N0} ₪ — לפני בדיקת ההנחות, ולא כולל רכיבים ללא פריט, " +
                "מערכות אחרות (ניקוז, מים, תאורה ועוד), היקפים שאינם משורטטים, חפירה ומילוי ומע\"מ. הרשימה המלאה בגיליון 'סיכום'; " +
                "להתחיל בגיליון 'כתב כמויות פשוט'; פירוט החישובים והחסרים בגיליונות הנלווים.\n" + SupportPackage.DisplayPath(written.XlsxPath);
            EngineerDraftNotice.Visibility = Visibility.Visible;
            _engineerDraftNoticeScan = capturedScan;
            Log(EngineerDraftNotice.Text.Replace(Environment.NewLine, " ").Replace("\n", " "));
            SetStatus("טיוטת כתב הכמויות להנדסה נוצרה — לבדיקה ולאישור הנדסי");
        }
        catch (Exception ex)
        {
            EngineerDraftNotice.Text = "טיוטת כתב הכמויות לא פורסמה: " + ex.Message;
            EngineerDraftNotice.Visibility = Visibility.Visible;
            _engineerDraftNoticeScan = capturedScan;
            ShowError("טיוטת כתב כמויות להנדסה", ex);
        }
        finally
        {
            _engineerDraftExporting = false;
            RefreshGates();
        }

        if (createdPath == null) return;
        // Opening Excel is a convenience; its failure never reads as a failed export.
        try
        {
            if (RtlMessageBox.ShowPath("נוצרה טיוטת כתב כמויות להנדסה:", createdPath, "לפתוח ב-Excel?",
                    "טיוטת כתב כמויות", MessageBoxButton.YesNo, MessageBoxImage.Information,
                    System.IO.Path.GetFileName(createdPath)) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(createdPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("הטיוטה נוצרה, אבל Excel לא נפתח: " + ex.Message);
            Log("פתיחת הטיוטה ב-Excel נכשלה: " + ex.Message);
        }
    }
}
