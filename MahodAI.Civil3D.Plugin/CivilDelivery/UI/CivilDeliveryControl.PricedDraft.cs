using System;
using System.IO;
using System.Windows;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private bool _pricedDraftExporting;

    private void RefreshPricedDraftExport(bool profileUsable, bool scanFresh)
    {
        var draft = _estimateResult == null ? null : EstimatePartialPricedDraftPolicy.Evaluate(_estimateResult);
        BtnExportPricedDraft.IsEnabled = !_pricedDraftExporting && profileUsable && scanFresh &&
            draft?.CanExport == true;
        PricedDraftSummary.Visibility = _estimateResult == null ? Visibility.Collapsed : Visibility.Visible;
        PricedDraftSummary.Text = draft == null ? string.Empty : !profileUsable || !scanFresh ?
            "תוצאה קודמת — אינה עדכנית לשימוש או לייצוא. יש לסרוק ולחשב מחדש." : !draft.CanExport ?
            "התמחור עדיין אינו כשיר לייצוא — יש לבדוק שיוכים, מחירים וממצאי המדידה. לא מוצג סכום מאושר." :
            $"סכום השורות התקינות: {draft.Subtotal:N2} ₪ · {draft.EligibleLineCount:N0} שורות מתומחרות. " +
            $"{draft.UnresolvedLineCount:N0} שורות לא נכללו בסכום." +
            (BtnExport.IsEnabled ? " האומדן מוכן לייצוא." : " טיוטה חלקית בלבד; חסרי הכיסוי וההחלטות נשארים בדוח.");
    }

    private void OnExportPricedDraft(object sender, RoutedEventArgs e)
    {
        var scan = _scan;
        var result = _estimateResult;
        var profile = _profile;
        var doc = Doc();
        if (doc == null || scan == null || result == null || profile == null || _pricedDraftExporting) return;
        if (!VerifyEstimateSourcesForAction(doc, "ייצוא טיוטה מתומחרת")) return;
        _pricedDraftExporting = true;
        BtnExportPricedDraft.IsEnabled = false;
        try
        {
            if (!ReferenceEquals(scan, _scan) || !ReferenceEquals(result, _estimateResult) ||
                !ReferenceEquals(profile, _profile))
                throw new InvalidOperationException("הנתונים התחלפו — יש לחשב מחדש לפני ייצוא.");
            SetStatus("מייצא טיוטה חלקית מתומחרת עם כל החסרים והממצאים…");
            var written = _estimate.ExportPartialPricedDraft(doc, scan, result, profile,
                drawingName: Path.GetFileName(doc.Name ?? string.Empty));
            PricedDraftSummary.Text = "יוצאה טיוטה חלקית מתומחרת — לא אומדן סופי.\n" + written.XlsxPath;
            SetExportNotice(PricedDraftSummary.Text);
            Log(PricedDraftSummary.Text);
            SetStatus("הטיוטה החלקית יוצאה; החסרים לא אושרו ולא הוחרגו");
        }
        catch (Exception ex)
        {
            SetExportNotice("הטיוטה המתומחרת לא יוצאה: " + ex.Message);
            ShowError("ייצוא טיוטה מתומחרת", ex);
        }
        finally
        {
            _pricedDraftExporting = false;
            RefreshGates();
        }
    }
}
