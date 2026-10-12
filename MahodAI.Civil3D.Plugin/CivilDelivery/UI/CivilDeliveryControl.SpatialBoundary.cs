using System;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private async void OnSelectQuantityBoundary(object sender, RoutedEventArgs e)
    {
        var doc = Doc();
        if (doc == null) return;
        try
        {
            await AcadApp.DocumentManager.ExecuteInCommandContextAsync(async _ =>
            {
                if (!ReferenceEquals(doc, Doc())) return;
                var options = new PromptEntityOptions("\nבחר פוליליין סגור לתחום כתב הכמויות: ");
                options.SetRejectMessage("\nיש לבחור פוליליין סגור עם מקטעים ישרים");
                options.AddAllowedClass(typeof(Polyline), true);
                var chosen = doc.Editor.GetEntity(options);
                if (chosen.Status != PromptStatus.OK) return;
                using var tr = doc.Database.TransactionManager.StartTransaction();
                var selection = new SpatialBoundarySelection(doc.Database.FingerprintGuid, chosen.ObjectId.Handle.ToString());
                EstimateSpatialBoundary.Resolve(doc.Database, tr, selection);
                _estimate.SpatialBoundary = selection;
                InvalidateEstimateEvidence("תחום המדידה השתנה — יש להריץ סריקה מחדש");
                QuantityBoundaryStatus.Text = "תחום פעיל: " + selection.Handle + " · מוגבל לתחום · יש להריץ סריקה";
                SetStatus("התחום נבחר. הפעל סריקה; הבחירה תקפה בחלון זה עד ניקוי התחום או סגירת Civil");
                RefreshGates();
                await System.Threading.Tasks.Task.CompletedTask;
            }, null);
        }
        catch (Exception ex) { ShowError("בחירת תחום", ex); }
    }

    private void OnClearQuantityBoundary(object sender, RoutedEventArgs e)
    {
        if (_estimate.SpatialBoundary == null) return;
        _estimate.SpatialBoundary = null;
        InvalidateEstimateEvidence("תחום המדידה נוקה — יש להריץ סריקה מחדש");
        QuantityBoundaryStatus.Text = "תחום המדידה: כל השרטוט · יש להריץ סריקה";
        SetStatus("התחום נוקה; הסריקה הבאה תמדוד את כל המקורות שנבחרו");
        RefreshGates();
    }
}
