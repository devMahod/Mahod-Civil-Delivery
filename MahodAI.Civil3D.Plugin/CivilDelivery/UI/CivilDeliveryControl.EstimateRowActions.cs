using System;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void OnEstimateSourcesExpanded(object sender, RoutedEventArgs e)
    {
        if (EstimateQuantityDetails != null) EstimateQuantityDetails.IsExpanded = false;
    }

    private void OnEstimateDetailsExpanded(object sender, RoutedEventArgs e)
    {
        if (EstimateAdvancedActions != null) EstimateAdvancedActions.IsExpanded = false;
    }

    // Presentation uses the freshness already checked by RefreshGates. Opening an
    // editor still rechecks its original document/profile/catalog at the write boundary.
    private void RefreshEstimateRowActions(bool profileUsable, bool scanFresh, bool savePending)
    {
        var row = QuantitiesGrid.SelectedItem as QuantityRowViewModel;
        BtnProjectPrice.IsEnabled = false;
        BtnQuantityAdjustment.IsEnabled = false;
        BtnProjectPrice.Content = "מחיר פרויקט…";
        BtnApprove.Content = row?.CatalogCode is { Length: > 0 } ? "שנה סעיף…" : "בחר סעיף…";
        EstimateRowTitle.Text = row == null
            ? "בחר שורה לשיוך סעיף או לעריכת מחיר"
            : $"{Bidi.Ltr(row.Layer)} · {row.QuantityDisplay}";
        var available = profileUsable && Doc() != null && !savePending && scanFresh &&
                        row != null && !row.IsHistorical && _quantityRows.Contains(row);
        BtnReviewMappings.IsEnabled = profileUsable && Doc() != null && !savePending && scanFresh &&
            _catalog != null && _quantityRows.Any(candidate => candidate.CanApproveCatalogMapping);
        BtnApprove.IsEnabled = available && _catalog != null && row!.CanApproveCatalogMapping;
        BtnShowQuantity.IsEnabled = available;
        BtnRelevance.IsEnabled = available;
        BtnQuantityAdjustment.IsEnabled = available && row is { IsIgnored: false, CanApproveCatalogMapping: true };
        BtnQuantityAdjustment.ToolTip = "מקדם מפורש לקבוצה הזאת בלבד, עם בסיס מדידה, מקור ואישור. אינו משנה מדידה, יחידה, מחיר או חסמים.";

        if (!profileUsable || Doc() == null)
            EstimateRowActionHint.Text = "פתח שרטוט עם פרופיל תקין כדי לערוך. נתונים קודמים, אם מוצגים, הם לעיון בלבד.";
        else if (savePending)
            EstimateRowActionHint.Text = "ממתין לשמירת השרטוט; לא ניתן לשנות שיוך או מחיר על סמך הסריקה הקודמת.";
        else if (_scan == null)
            EstimateRowActionHint.Text = "סרוק כמויות כדי לראות את השכבות והכמויות. לאחר מכן בחר שורה לשיוך סעיף או לעריכת מחיר.";
        else if (!scanFresh || row?.IsHistorical == true)
            EstimateRowActionHint.Text = "הסריקה אינה עדכנית. הנתונים לעיון בלבד — שמור וסרוק כמויות כדי להמשיך בעריכה.";
        else if (row == null)
            EstimateRowActionHint.Text = "בחר שורת כמות. ניתן לחפש סעיף לפי תיאור או מספר, גם אם אין הצעה אוטומטית.";
        else if (row.IsIgnored)
            EstimateRowActionHint.Text = "הקבוצה סומנה כלא רלוונטית. לחץ החזר לרשימה אם ברצונך לשייך או לתמחר אותה.";
        else if (!row.CanApproveCatalogMapping)
            EstimateRowActionHint.Text = "חלופת המדידה של אותם עצמים כבר נבחרה; לא מתמחרים שטח והיקף של אותה גאומטריה יחד. הפרטים למטה.";
        else if (_catalog == null)
            EstimateRowActionHint.Text = "טען מחירון כדי לבחור סעיף. הכמות שנמדדה אינה תלויה במחירון.";
        else if (string.IsNullOrWhiteSpace(row.CatalogCode) && row.ProjectRuleNote is { Length: > 0 } ruleNote)
            EstimateRowActionHint.Text = ruleNote + (row.ProjectRuleGoverned
                ? " שיוך סעיף לכמות הגולמית כאן הוא החלטה הנדסית מפורשת בלבד ('בחר סעיף')."
                : "");
        else if (string.IsNullOrWhiteSpace(row.CatalogCode))
            EstimateRowActionHint.Text = row.ProposedCode is { Length: > 0 }
                ? $"הצעה בלבד: {Bidi.Ltr(row.ProposedCode)}. אפשר ללחוץ 'בחר סעיף' כדי לבדוק אותה או לחפש סעיף אחר; דבר לא אושר אוטומטית."
                : "טרם שויך סעיף. יש ללחוץ 'בחר סעיף' ולחפש במחירון לפי תיאור או מספר; יחידת הסעיף חייבת להתאים לכמות.";
        else
        {
            try
            {
                var records = _scan!.Records.Where(record => record.Classification.RuleKey == row.RuleKey).ToArray();
                if (records.Any(record => !EstimateWorkflowService.HasApprovedCatalogMapping(_profile, record)))
                    throw new InvalidOperationException("השיוך אינו מאושר בפרופיל הנוכחי — בחר ואשר את הסעיף מחדש.");
                var context = ProjectPriceApprovalPolicy.CaptureForRecords(_profile!, _catalog, row.CatalogCode, records);
                var previous = _profile!.Estimate.ProjectOverrides.Any(value => string.Equals(
                    value.ItemCode?.Trim(), context.ItemCode.Trim(), StringComparison.OrdinalIgnoreCase));
                BtnProjectPrice.IsEnabled = available;
                BtnProjectPrice.Content = previous ? "ערוך מחיר פרויקט…" : "מחיר פרויקט…";
                EstimateRowActionHint.Text = $"סעיף מאושר: {Bidi.Ltr(context.ItemCode)}. " +
                    (previous ? "קיימת החלטת מחיר פרויקט שניתן לבדוק ולערוך. " : "ניתן להשלים או לשנות מחיר פרויקט כבר עכשיו. ") +
                    "שינוי מחיר חל על כל הכמויות המשויכות לסעיף בפרויקט; תמחור מלא נבדק בבניית האומדן.";
            }
            catch (InvalidOperationException ex)
            {
                EstimateRowActionHint.Text = ex.Message;
            }
        }
        BtnProjectPrice.ToolTip = BtnProjectPrice.IsEnabled
            ? "אישור מחיר עם מקור ונימוק, לכל הכמויות המשויכות לסעיף בפרויקט. אין צורך לבנות אומדן קודם."
            : EstimateRowActionHint.Text;
    }

    private void OnEditSelectedProjectPrice(object sender, RoutedEventArgs e)
    {
        RefreshGates();
        if (BtnProjectPrice.IsEnabled && QuantitiesGrid.SelectedItem is QuantityRowViewModel row)
            OnApproveProjectPrice(row);
    }
}
