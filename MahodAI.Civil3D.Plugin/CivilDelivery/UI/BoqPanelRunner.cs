using System;
using System.IO;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The work behind <see cref="BoqPanel"/>, in AutoCAD's command context (the drawing is read and the polygon picked there):
/// read the whole drawing or what can lie in a marked area, cut it at the area's edge, price it, show the estimate. The
/// last result stays (its read, its area highlighted on the plan) for "export" and "full details" until the next one.
/// </summary>
internal static class BoqPanelRunner
{
    private sealed class Session : IDisposable
    {
        internal required string Drawing { get; init; }
        internal required InfrastructureDrawingService Source { get; init; }
        internal required InfrastructureDrawingService View { get; init; }
        internal required CatalogSnapshot Catalog { get; init; }
        internal BoqAreaPicker.Outline? Outline { get; init; }
        internal bool IsArea => View.Area != null;
        public void Dispose()
        {
            Outline?.Dispose();
            if (!ReferenceEquals(View, Source)) View.Dispose();
            Source.Dispose();
        }
    }

    private static Session? _session;
    private static CatalogSnapshot? _catalog;

    /// <summary>From a panel button (the palette is modeless): run <paramref name="work"/> as a command.</summary>
    internal static async void Start(BoqPanel panel, Action<BoqPanel> work)
    {
        if (panel.Busy) return;
        if (AcadApp.DocumentManager.MdiActiveDocument == null) { panel.ShowMessage("אין שרטוט פעיל.", error: true); return; }
        try
        {
            await AcadApp.DocumentManager.ExecuteInCommandContextAsync(_ => { work(panel); return Task.CompletedTask; }, null);
        }
        catch (System.Exception ex) { panel.ShowMessage(ex.Message, error: true); }
        CivilDeliveryPalette.FocusPanel();
    }

    /// <summary>MHD_BOQ / MHD_BOQ_AREA (already a command): the Civil Delivery window on its BOQ tab, and the same work.</summary>
    internal static void FromCommand(bool area)
    {
        var panel = CivilDeliveryPalette.ShowBoq();
        if (panel == null || panel.Busy) return;
        Bill(panel, area);
    }

    internal static void Bill(BoqPanel panel, bool area)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { panel.ShowMessage("אין שרטוט פעיל.", error: true); return; }
        try
        {
            if (string.IsNullOrWhiteSpace(doc.Name) || !File.Exists(doc.Name)) throw new InvalidOperationException("שמרו את השרטוט לפני הסריקה.");
            var factor = MahodAI.CivilDelivery.Shared.PhysicalDrawingUnitPolicy.StandardMetresPerUnit((int)doc.Database.Insunits)
                ?? throw new InvalidOperationException("יחידות השרטוט אינן מוגדרות. יש לבדוק ולהגדיר INSUNITS לפני מדידה במטרים.");
            BoqAreaPicker.Picked? picked = null;
            if (area)
            {
                panel.ShowPicking();
                picked = BoqAreaPicker.Pick(doc, factor);
                if (picked == null) { panel.ShowLast(); return; }   // Esc: nothing changes
            }
            Replace(null);   // the previous area's highlight goes; its read is freed
            panel.Forget();
            panel.BeginBusy(area ? "קורא את מה שבתוך האזור" : "קורא את כל השרטוט");
            BoqAreaPicker.Outline? outline = null;
            InfrastructureDrawingService? source = null, view = null;
            try
            {
                if (picked != null) { outline = new BoqAreaPicker.Outline(); outline.Show(picked.Area); doc.Editor.UpdateScreen(); }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                source = new InfrastructureDrawingService(doc.Database, doc.Name, factor, picked?.Area, n => panel.Tick(n, "עצמים נקראו"));
                doc.Editor.WriteMessage($"\nBOQ: read {source.Measurements.Count:N0} measurements in {clock.Elapsed.TotalSeconds:N1} s.\n");
                if (picked != null)
                {
                    panel.Step("חותך לפי גבול האזור");
                    var of = source.Measurements.Count;
                    view = source.ClipTo(picked.Area, picked.PickedPolyline, n => panel.Tick(n, $"מתוך {of:N0} מדידות"));
                }
                else view = source;
                panel.Step("מתמחר לפי מחירון נתיבי ישראל");
                var catalog = _catalog ??= InfrastructureBoqWindow.LoadCatalog();
                var estimate = new InfrastructureBoqWindow(view, catalog).Estimate(panel.Wait);
                Replace(new Session { Drawing = doc.Name, Source = source, View = view, Catalog = catalog, Outline = outline });
                panel.EndBusy();
                panel.ShowResult(estimate, area);
            }
            catch
            {
                outline?.Dispose();
                if (view != null && !ReferenceEquals(view, source)) view.Dispose();
                source?.Dispose();
                doc.Editor.UpdateScreen();
                throw;
            }
        }
        catch (OperationCanceledException) { panel.EndBusy(); panel.ShowMessage("בוטל. לא נשמר דבר.", error: false); }
        catch (System.Exception ex) { panel.EndBusy(); panel.ShowMessage(ex.Message, error: true); }
    }

    internal static void Export(BoqPanel panel)
    {
        if (Current(panel) is not { } s) return;
        var folder = new InfrastructureBoqWindow(s.View, s.Catalog).ExportBoq(null);
        if (folder != null) panel.ShowExported(folder);
    }

    /// <summary>The full window for the same read: every layer, its item, the report, concrete. What it changes shows here after.</summary>
    internal static void Details(BoqPanel panel, InfrastructureBoqWindow.Sheet table)
    {
        if (Current(panel) is not { } s) return;
        var window = new InfrastructureBoqWindow(s.View, s.Catalog);
        window.Open(table);
        CivilModalHost.ShowFromPalette(window);
        if (window.NextStep == InfrastructureBoqWindow.Step.MarkArea) { Bill(panel, area: true); return; }
        if (window.NextStep == InfrastructureBoqWindow.Step.WholeDrawing) { Bill(panel, area: false); return; }
        try
        {
            panel.BeginBusy("מעדכן את האומדן");
            var estimate = new InfrastructureBoqWindow(s.View, s.Catalog).Estimate(panel.Wait);
            panel.EndBusy();
            panel.ShowResult(estimate, s.IsArea);
        }
        catch (OperationCanceledException) { panel.EndBusy(); panel.ShowLast(); }
    }

    private static Session? Current(BoqPanel panel)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (_session != null && doc != null && string.Equals(doc.Name, _session.Drawing, StringComparison.OrdinalIgnoreCase)) return _session;
        panel.ShowMessage("האומדן הוא של שרטוט אחר — סמנו אזור או בחרו כל השרטוט מחדש.", error: true);
        return null;
    }

    private static void Replace(Session? next)
    {
        var old = _session;
        _session = next;
        if (old == null) return;
        try { old.Dispose(); } catch (System.Exception) { }
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.UpdateScreen();
    }
}
