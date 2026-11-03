using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The typical sections as the office's sheet (2026-11-02, MHD-HW-000-IC_00000-DD-5001): an A0 layout, the panels
    /// stacked in 1:100 viewports one under the other, and on the left the legend ("מקרא") of the pavement hatches and a
    /// title box. A sheet takes as many panels as fit; the rest go on the next sheet.
    /// </summary>
    internal static class OfficeTypicalSheetLayout
    {
        private const double SheetW = 1189, SheetH = 841, Margin = 10, Column = 150, Gap = 8;
        private const double PaperPerMetre = 10.0;    // 1:100 in millimetres per metre

        /// <summary>Creates the sheets for the given typical-section references; returns the layout names.</summary>
        public static IReadOnlyList<string> Create(Database db, IReadOnlyList<ObjectId> panels, string title, string baseName = "MHD-TYP")
        {
            var names = new List<string>();
            var frames = new List<(ObjectId Id, Extents3d Ext)>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in panels)
                    try { if (OfficeTypicalRenderer.FrameExtents(tr, id) is { } ext) frames.Add((id, ext)); } catch { }
                tr.Commit();
            }
            var areaH = SheetH - 2 * Margin;
            var sheets = new List<List<(ObjectId Id, Extents3d Ext)>>();
            var current = new List<(ObjectId, Extents3d)>();
            double used = 0;
            foreach (var f in frames)
            {
                var h = (f.Ext.MaxPoint.Y - f.Ext.MinPoint.Y) * PaperPerMetre;
                if (current.Count > 0 && used + Gap + h > areaH) { sheets.Add(current); current = new(); used = 0; }
                current.Add(f); used += (current.Count > 1 ? Gap : 0) + h;
            }
            if (current.Count > 0) sheets.Add(current);

            var manager = LayoutManager.Current;
            var returnTo = manager.CurrentLayout;
            int first;
            using (var check = db.TransactionManager.StartOpenCloseTransaction())
            {
                var existing = new List<string>();
                // DBDictionary's typed enumerator (its non-generic one yields DictionaryEntry: Cast<> threw, live)
                foreach (DBDictionaryEntry entry in (DBDictionary)check.GetObject(db.LayoutDictionaryId, OpenMode.ForRead))
                    existing.Add(entry.Key);
                first = OfficeTypicalSheet.FirstFreeSheetNumber(existing, baseName);
            }
            for (var s = 0; s < sheets.Count; s++)
            {
                var name = $"{baseName}-{first + s:00}";
                using (var check = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var dict = (DBDictionary)check.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    for (var i = 2; dict.Contains(name); i++) name = $"{baseName}-{first + s:00}_{i}";
                }
                var layoutId = manager.CreateLayout(name);
                // A viewport is switched on only in the current layout (eNotInPaperspace otherwise).
                manager.CurrentLayout = name;
                using var tr = db.TransactionManager.StartTransaction();
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                try { layout.Initialize(); } catch { /* an older host */ }
                SetA0(layout);
                var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                // the layout's default viewport goes; ours show the panels
                foreach (ObjectId id in paper)
                    if (tr.GetObject(id, OpenMode.ForWrite) is Viewport old) old.Erase();
                var frameLayer = TypicalSectionPicker.EnsureLayer(tr, db, OfficeTypicalRenderer.FrameLayer, 4);
                var keep = new HashSet<string>(OfficeTypicalRenderer.Layers.Select(x => x.Name), StringComparer.OrdinalIgnoreCase)
                    { DraftedTypicalSectionService.SectionLayer };
                var freeze = new List<ObjectId>();
                foreach (ObjectId lid in (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead))
                    if (!keep.Contains(((LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead)).Name)) freeze.Add(lid);
                var textLayer = TypicalSectionPicker.EnsureLayer(tr, db, "HW-TYP-TEXT", 2);
                var hebrew = StyleOr(tr, db, "MHD-TYP-MIRYL");
                var shx = hebrew != db.Textstyle && IsShx(tr, hebrew);
                // the sheet's text black (7), its frames and viewport borders grey, as the panels (OfficeTypicalRenderer.Palette)
                T Add<T>(T e, ObjectId layer) where T : Entity
                {
                    e.LayerId = layer;
                    e.ColorIndex = layer == textLayer ? (short)7 : (short)8;
                    paper.AppendEntity(e); tr.AddNewlyCreatedDBObject(e, true); return e;
                }
                void Rect(double x0, double y0, double x1, double y1, ObjectId layer)
                {
                    var pl = new Polyline { Closed = true };
                    pl.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0); pl.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0); pl.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
                    Add(pl, layer);
                }
                void Text(string value, double x, double y, double h)
                {
                    var t = Add(new DBText { TextString = shx ? OfficeTypicalSheet.ShxHebrew(value) : value, Height = h, TextStyleId = hebrew, Position = new Point3d(x, y, 0) }, textLayer);
                    // right-aligned at x, as the office's column reads
                    try { var e = t.GeometricExtents; t.Position = new Point3d(x - (e.MaxPoint.X - x), y, 0); } catch { }
                }

                Rect(Margin, Margin, SheetW - Margin, SheetH - Margin, frameLayer);
                Rect(Margin, Margin, Margin + Column, SheetH - Margin, frameLayer);
                // legend
                var lx = Margin + Column - 8;
                Text("מקרא:", lx, SheetH - 40, 6);
                var legend = new[] { ("אספלט", "SOLID", 1.0, (short)252), ("אגו\"מ", "AR-SAND", 0.05, (short)33), ("מצע סוג א'", "AR-CONC", 0.03, (short)33), ("מצע סוג ב'", "ANSI31", 0.6, (short)8) };
                for (var i = 0; i < legend.Length; i++)
                {
                    var (label, pattern, scale, color) = legend[i];
                    var y = SheetH - 60 - i * 12;
                    var box = new Polyline { Closed = true };
                    box.AddVertexAt(0, new Point2d(Margin + 10, y), 0, 0, 0); box.AddVertexAt(1, new Point2d(Margin + 40, y), 0, 0, 0);
                    box.AddVertexAt(2, new Point2d(Margin + 40, y + 6), 0, 0, 0); box.AddVertexAt(3, new Point2d(Margin + 10, y + 6), 0, 0, 0);
                    Add(box, frameLayer);
                    try
                    {
                        var hatch = Add(new Hatch(), frameLayer);
                        hatch.ColorIndex = color;
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                        hatch.PatternScale = scale;
                        hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { box.ObjectId });
                        hatch.EvaluateHatch(true);
                    }
                    catch { }
                    Text(label, lx, y + 1, 3.5);
                }
                // title box
                Rect(Margin, Margin, Margin + Column, Margin + 70, frameLayer);
                Text("חתכים טיפוסיים", lx, Margin + 52, 6);
                Text(title, lx, Margin + 38, 5);
                Text("קנ\"מ 1:100", lx, Margin + 24, 4);
                Text(FormattableString.Invariant($"גיליון {s + 1} מתוך {sheets.Count}"), lx, Margin + 12, 4);

                // the panels, top down
                var top = SheetH - Margin;
                var areaX0 = Margin + Column + Gap; var areaX1 = SheetW - Margin - Gap;
                foreach (var (_, ext) in sheets[s])
                {
                    var w = (ext.MaxPoint.X - ext.MinPoint.X) * PaperPerMetre;
                    var h = (ext.MaxPoint.Y - ext.MinPoint.Y) * PaperPerMetre;
                    var cx = (areaX0 + areaX1) / 2; var cy = top - h / 2;
                    var vp = Add(new Viewport { CenterPoint = new Point3d(cx, cy, 0), Width = Math.Min(w, areaX1 - areaX0), Height = h }, frameLayer);
                    vp.ViewDirection = Vector3d.ZAxis;
                    vp.ViewTarget = Point3d.Origin;
                    vp.TwistAngle = 0;
                    vp.ViewCenter = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
                    vp.CustomScale = PaperPerMetre;
                    vp.On = true;
                    // Only the typical section shows: the plan, the firm's sections and everything else under it are frozen
                    // in this viewport (the office keeps its sections in a drawing of their own).
                    if (freeze.Count > 0) vp.FreezeLayersInViewport(freeze.GetEnumerator());
                    vp.Locked = true;
                    top -= h + Gap;
                }
                tr.Commit();
                names.Add(name);
            }
            // The engineer asked for the sheet: it opens on the first one (2026-11-03; before, the model came back and the
            // new layouts waited unseen in the tabs).
            try { manager.CurrentLayout = names.Count > 0 ? names[0] : returnTo; } catch { /* stays where it is */ }
            return names;
        }

        /// <summary>Sheets (drawing, layout) whose viewports have not been redrawn yet.</summary>
        private static readonly HashSet<(IntPtr Database, string Layout)> Unshown = new();
        private static bool _switchHooked;

        /// <summary>
        /// Viewports made and switched on inside a command stay blank on screen (live 2026-11-03, Civil 3D 2027: an empty
        /// frame on MHD-TYP-01 through a regen and a layout switch, the section there at once after off/on from outside the
        /// command). So each new sheet is redrawn once AutoCAD is idle while it is the current layout: the first one right
        /// after the command, the others when the engineer opens them.
        /// </summary>
        internal static void RedrawWhenShown(Document doc, IEnumerable<string> sheets)
        {
            lock (Unshown)
                foreach (var sheet in sheets) Unshown.Add((doc.Database.UnmanagedObject, sheet));
            if (!_switchHooked)
            {
                _switchHooked = true;
                LayoutManager.Current.LayoutSwitched += (_, _) =>
                {
                    if (AcadApp.DocumentManager.MdiActiveDocument is { } active) RedrawWhenIdle(active);
                };
            }
            RedrawWhenIdle(doc);
        }

        private static void RedrawWhenIdle(Document doc, int idlesLeft = 30)
        {
            EventHandler? once = null;
            once = (_, _) =>
            {
                AcadApp.Idle -= once;
                try
                {
                    if (doc.IsDisposed) return;
                    var current = LayoutManager.Current.CurrentLayout;
                    bool pending;
                    lock (Unshown) pending = Unshown.Contains((doc.Database.UnmanagedObject, current));
                    // still inside a command, another drawing in front, or the sheet not current yet: a later idle
                    if (!ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, doc) ||
                        !string.IsNullOrEmpty(doc.CommandInProgress) || !pending)
                    {
                        if (idlesLeft > 0) RedrawWhenIdle(doc, idlesLeft - 1);
                        return;
                    }
                    // a moment after the command, in application context under the document's lock (as the code runner's
                    // off/on that showed it, live): switched inside the sheet's command, or in a command context of its
                    // own, the frame stayed blank
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        try
                        {
                            if (!ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, doc) || doc.IsDisposed ||
                                !string.Equals(LayoutManager.Current.CurrentLayout, current, StringComparison.OrdinalIgnoreCase))
                            {
                                MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Info($"typical sheet redraw skipped: now {LayoutManager.Current.CurrentLayout}, was {current}");
                                return;
                            }
                            if (!string.IsNullOrEmpty(doc.CommandInProgress)) { RedrawWhenIdle(doc); return; }
                            lock (Unshown)
                                if (!Unshown.Remove((doc.Database.UnmanagedObject, current))) return;
                            // the whole A0 first, then the frames: zoomed after the off/on, or regenerated, they went blank again
                            ZoomToSheet(doc);
                            int shown;
                            using (doc.LockDocument())
                                shown = ShowViewports(doc.Database, LayoutManager.Current.GetLayoutId(current));
                            MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Info($"typical sheet {current}: {shown} viewports redrawn");
                        }
                        catch (Exception ex) { MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("typical sheet redraw", ex); }
                    };
                    timer.Start();
                }
                catch (Exception ex) { MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("typical sheet redraw", ex); }
            };
            AcadApp.Idle += once;
        }

        /// <summary>The whole A0 in view (a new layout opens on a corner of it, the title box).</summary>
        private static void ZoomToSheet(Document doc)
        {
            try
            {
                using var view = doc.Editor.GetCurrentView();
                var aspect = view.Width / view.Height;
                var height = Math.Max(SheetH, SheetW / aspect) * 1.04;
                view.CenterPoint = new Point2d(SheetW / 2, SheetH / 2);
                view.Height = height;
                view.Width = height * aspect;
                doc.Editor.SetCurrentView(view);
            }
            catch { /* the sheet is there; ZOOM E shows all of it */ }
        }

        /// <summary>Our viewports of the current layout, switched off, on and redrawn (see <see cref="RedrawWhenShown"/>).</summary>
        private static int ShowViewports(Database db, ObjectId layoutId)
        {
            var shown = 0;
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in paper)
                {
                    // ours only, by their frame layer: never the paper's own viewport
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp ||
                        !string.Equals(vp.Layer, OfficeTypicalRenderer.FrameLayer, StringComparison.OrdinalIgnoreCase)) continue;
                    vp.UpgradeOpen();
                    vp.On = false;
                    vp.On = true;
                    vp.UpdateDisplay();
                    shown++;
                }
                tr.Commit();
            }
            catch { /* the sheet is there; a blank frame shows the section after a VIEWPORT off/on */ }
            return shown;
        }

        /// <summary>A0 landscape on DWG To PDF, 1:1 in millimetres, as the office plots its DD sheets; the host's default
        /// paper when that device is missing.</summary>
        private static void SetA0(Layout layout)
        {
            try
            {
                var validator = PlotSettingsValidator.Current;
                using var settings = new PlotSettings(layout.ModelType);
                settings.CopyFrom(layout);
                validator.SetPlotConfigurationName(settings, "DWG To PDF.pc3", null);
                validator.RefreshLists(settings);
                var media = validator.GetCanonicalMediaNameList(settings).Cast<string>()
                    .FirstOrDefault(m => m.StartsWith("ISO_full_bleed_A0_(1189", StringComparison.OrdinalIgnoreCase));
                if (media == null) return;
                validator.SetCanonicalMediaName(settings, media);
                validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
                // the rest is a nicety: one the device refuses must not cost the paper size
                try { validator.SetUseStandardScale(settings, true); validator.SetStdScaleType(settings, StdScaleType.StdScale1To1); } catch { } try { validator.SetPlotRotation(settings, PlotRotation.Degrees000); } catch { } try { validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout); } catch { }
                layout.CopyFrom(settings);
            }
            catch { /* the default paper; the frame is still A0 */ }
        }

        private static ObjectId StyleOr(Transaction tr, Database db, string name)
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            return table.Has(name) ? table[name] : db.Textstyle;
        }

        private static bool IsShx(Transaction tr, ObjectId style) =>
            ((TextStyleTableRecord)tr.GetObject(style, OpenMode.ForRead)).FileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
    }
}
