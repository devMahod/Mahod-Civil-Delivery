using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices.Core; // AutoCAD Core Application
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcDb = Autodesk.AutoCAD.DatabaseServices;

[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.MahodCommands))]

namespace MahodAI.Civil3D.Plugin
{
    public class MahodCommands
    {
        [CommandMethod("MAHOD_AI")]
        public void ShowMahodAI()
        {
            try
            {
                MahodChatWindow.Show();
            }
            catch (System.Exception ex)
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                    .DocumentManager
                    .MdiActiveDocument;

                var ed = doc?.Editor;
                ed?.WriteMessage($"\n❌ שגיאה בפתיחת חלון: {ex.Message}");
            }
        }

        [CommandMethod("MAHOD_CHECK")]
        public void CheckDrawing()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager
                .MdiActiveDocument;

            if (doc == null)
                return;

            var ed = doc.Editor;
            var db = doc.Database;

            ed.WriteMessage("\n🔍 בודק שרטוט...\n");

            try
            {
                CivilDocument civilDoc = CivilApplication.ActiveDocument;
                if (civilDoc == null)
                {
                    ed.WriteMessage("\n❌ אין CivilDocument פעיל.");
                    return;
                }

                int issueCount = 0;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId alignId in civilDoc.GetAlignmentIds())
                    {
                        var align = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                        if (align != null)
                        {
                            ed.WriteMessage($"\n📐 {align.Name}:");
                            ed.WriteMessage($"\n   אורך: {align.Length:F2} מ'");

                            if (align.Length < 50)
                            {
                                ed.WriteMessage("\n   ⚠️ תוואי קצר מדי!");
                                issueCount++;
                            }
                            else
                            {
                                ed.WriteMessage("\n   ✅ תקין");
                            }
                        }
                    }
                    tr.Commit();
                }

                ed.WriteMessage($"\n\n📊 סיכום: {issueCount} בעיות נמצאו\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ שגיאה: {ex.Message}\n");
            }
        }

        [CommandMethod("MAHOD_INFO")]
        public void ShowInfo()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager
                .MdiActiveDocument;

            var ed = doc?.Editor;
            if (ed == null)
                return;

            var platformVersion = typeof(MahodCommands).Assembly.GetName().Version?.ToString()
                                  ?? "unknown";
            string hostVersion;
            try
            {
                hostVersion = Convert.ToString(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("ACADVER"))
                    ?? "unknown";
            }
            catch
            {
                hostVersion = "unknown";
            }

            ed.WriteMessage("\nMahod AI for Civil 3D");
            ed.WriteMessage($"\n  Platform version: {platformVersion}");
            ed.WriteMessage($"\n  AutoCAD host: {hostVersion}");
            ed.WriteMessage($"\n  Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            ed.WriteMessage("\n  Developer: Mahod Engineering\n");
        }

        /// <summary>
        /// One-shot diagnostic: dumps every style / font / linetype name in the active
        /// drawing to %LOCALAPPDATA%\MahodAI_Civil3D\style_dump.txt so the firm's template
        /// style names can be wired into the Stage-3 profile/section tools verbatim
        /// (mirroring MahodCivilNet, which selects template styles by name rather than
        /// fabricating them). Reports:
        ///   • every Civil 3D style collection under CivilDocument.Styles (reflection-walked,
        ///     so it is version-tolerant — Profile View / Band Set / Profile / Section / etc.)
        ///   • Profile-style line display color + linetype (to spot the green-dashed EG and red FG)
        ///   • Text styles with their fonts (.shx = no Hebrew → ?????, TrueType = OK)
        ///   • Layers → linetype, and the linetype table (for the dashed existing-ground line)
        ///   • LTSCALE / MSLTSCALE / CELTSCALE (cross-section dash scaling)
        /// </summary>
        [CommandMethod("MAHOD_DUMPSTYLES")]
        public void DumpStyles()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            var sb = new StringBuilder();
            void W(string s) { sb.Append(s); sb.Append('\n'); }

            W("=== MahodAI style dump ===");
            try { W("Drawing : " + doc.Name); } catch { }
            W("Created : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            CivilDocument? civilDoc = null;
            try { civilDoc = CivilApplication.ActiveDocument; } catch { }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    // ── Drawing scale variables (drive cross-section dash visibility) ──
                    W("");
                    W("--- Drawing variables ---");
                    try { W($"LTSCALE   = {db.Ltscale}"); } catch { }
                    try { W($"CELTSCALE = {db.Celtscale}"); } catch { }
                    try { W($"MSLTSCALE = {Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("MSLTSCALE")}"); } catch { }
                    try { W($"DIMSCALE  = {Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DIMSCALE")}"); } catch { }
                    try
                    {
                        if (tr.GetObject(db.Textstyle, OpenMode.ForRead) is TextStyleTableRecord cur)
                            W($"current TEXTSTYLE = '{cur.Name}'  file='{cur.FileName}'");
                    }
                    catch { }

                    // ── Text styles + fonts (the ????? Hebrew lever) ──
                    W("");
                    W("--- Text styles (.shx font = Hebrew shows as ?????; TrueType = renders) ---");
                    if (tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) is TextStyleTable tst)
                    {
                        foreach (ObjectId id in tst)
                        {
                            try
                            {
                                if (tr.GetObject(id, OpenMode.ForRead) is not TextStyleTableRecord ts) continue;
                                string typeface = "";
                                try { typeface = ts.Font.TypeFace ?? ""; } catch { }
                                W($"  '{ts.Name}'  file='{ts.FileName}'  bigfont='{ts.BigFontFileName}'  typeface='{typeface}'");
                            }
                            catch { }
                        }
                    }

                    // ── Linetypes (for the dashed existing-ground line) ──
                    W("");
                    W("--- Linetypes ---");
                    if (tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) is LinetypeTable ltt)
                    {
                        foreach (ObjectId id in ltt)
                        {
                            try
                            {
                                if (tr.GetObject(id, OpenMode.ForRead) is LinetypeTableRecord lt)
                                    W($"  '{lt.Name}'");
                            }
                            catch { }
                        }
                    }

                    // ── Layers → linetype (CivilNet draws existing ground on a dashed layer) ──
                    W("");
                    W("--- Layers (name -> linetype, color) ---");
                    if (tr.GetObject(db.LayerTableId, OpenMode.ForRead) is LayerTable lat)
                    {
                        foreach (ObjectId id in lat)
                        {
                            try
                            {
                                if (tr.GetObject(id, OpenMode.ForRead) is not LayerTableRecord la) continue;
                                string ltn = "";
                                try
                                {
                                    if (tr.GetObject(la.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord llt)
                                        ltn = llt.Name;
                                }
                                catch { }
                                W($"  '{la.Name}' -> '{ltn}'  color={la.Color}");
                            }
                            catch { }
                        }
                    }

                    // ── Every Civil 3D style collection (reflection-walked) ──
                    W("");
                    W("--- Civil 3D styles (CivilDocument.Styles) ---");
                    if (civilDoc != null)
                    {
                        DumpCivilStyles(tr, sb, civilDoc.Styles);

                        // Profile-style line display — pinpoint the green-dashed EG + red FG.
                        W("");
                        W("--- Profile style line display (spot the green-dashed EG and the red FG) ---");
                        try
                        {
                            foreach (ObjectId id in civilDoc.Styles.ProfileStyles)
                            {
                                try
                                {
                                    if (tr.GetObject(id, OpenMode.ForRead) is not Autodesk.Civil.DatabaseServices.Styles.ProfileStyle ps) continue;
                                    string color = "?", lt = "?", lw = "?", vis = "?";
                                    try
                                    {
                                        var disp = ps.GetDisplayStyleProfile(
                                            Autodesk.Civil.DatabaseServices.Styles.ProfileDisplayStyleProfileType.Line);
                                        try { color = disp.Color.ToString(); } catch { }
                                        try { lt = disp.Linetype; } catch { }
                                        try { lw = disp.Lineweight.ToString(); } catch { }
                                        try { vis = disp.Visible.ToString(); } catch { }
                                    }
                                    catch { }
                                    W($"  '{ps.Name}'  color={color}  linetype='{lt}'  lw={lw}  visible={vis}");
                                }
                                catch { }
                            }
                        }
                        catch (System.Exception ex) { W("  (profile-style detail failed: " + ex.Message + ")"); }
                    }
                    else
                    {
                        W("  (no active CivilDocument — Civil styles skipped)");
                    }
                }
                catch (System.Exception ex)
                {
                    W("ERROR: " + ex.Message);
                }
                tr.Commit();
            }

            // ── Write the report to a file the engineer can share ──
            string path;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "style_dump.txt");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[MahodAI] Could not write style dump file: {ex.Message}\n");
                ed.WriteMessage(sb.ToString());
                return;
            }

            ed.WriteMessage(sb.ToString());
            ed.WriteMessage($"\n[MahodAI] ✅ Style dump written to:\n  {path}\n" +
                "Share that file so the firm's exact style names can be wired into Stage-3.\n");
        }

        /// <summary>
        /// Reflection-walks a Civil 3D StylesRoot (CivilDocument.Styles), printing every
        /// style collection it finds as "[CollectionName] (n): name1 | name2 | …". Walking
        /// by reflection keeps the dump version-tolerant: collections that don't exist on a
        /// given Civil 3D release are simply absent, never a compile/run break.
        /// </summary>
        private static void DumpCivilStyles(Transaction tr, StringBuilder sb, object stylesRoot)
        {
            DumpStylesNode(tr, sb, stylesRoot, 0, new HashSet<string>());
        }

        private static void DumpStylesNode(
            Transaction tr, StringBuilder sb, object node, int depth, HashSet<string> visited)
        {
            if (node == null || depth > 3) return;

            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? val;
                try { val = p.GetValue(node); } catch { continue; }
                if (val == null) continue;

                // A collection of style ObjectIds → list their names.
                if (val is System.Collections.IEnumerable en && val is not string)
                {
                    var names = new List<string>();
                    try
                    {
                        foreach (var item in en)
                        {
                            if (item is ObjectId oid && !oid.IsNull)
                            {
                                try
                                {
                                    if (tr.GetObject(oid, OpenMode.ForRead)
                                        is Autodesk.Civil.DatabaseServices.Styles.StyleBase stl)
                                        names.Add(stl.Name);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { continue; }

                    if (names.Count > 0)
                        sb.Append($"  [{p.Name}] ({names.Count}): {string.Join(" | ", names)}\n");
                    continue;
                }

                // A nested style-group object (e.g. LabelSetStyles, BandStyles) → recurse once.
                var t = val.GetType();
                if (t.Namespace != null && t.Namespace.StartsWith("Autodesk.Civil", StringComparison.Ordinal))
                {
                    string key = t.FullName + "#" + p.Name;
                    if (visited.Add(key))
                        DumpStylesNode(tr, sb, val, depth + 1, visited);
                }
            }
        }

        /// <summary>
        /// One-shot diagnostic: inspects every Profile View actually in the drawing and dumps,
        /// to %LOCALAPPDATA%\MahodAI_Civil3D\pv_dump.txt, the data needed to fix the remaining
        /// profile-view artifacts (????? band labels, empty data table, missing grid):
        ///   • the PV's APPLIED ProfileViewStyle name (is "IL Left & Bottom Axis" really on it?)
        ///     + whether the style's grid components are visible.
        ///   • each band (top/bottom): band type, band-style name, and the EG/FG profiles bound
        ///     to it (empty bound profiles ⇒ empty data table).
        ///   • every text style each band style references, with its font (.shx ⇒ ????? Hebrew).
        /// </summary>
        [CommandMethod("MAHOD_DUMPPV")]
        public void DumpProfileViews()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            var sb = new StringBuilder();
            void W(string s) { sb.Append(s); sb.Append('\n'); }

            W("=== MahodAI profile-view dump ===");
            try { W("Drawing : " + doc.Name); } catch { }
            W("Created : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms = bt != null
                        ? tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord
                        : null;
                    int pvCount = 0;
                    if (ms != null)
                    {
                        foreach (ObjectId id in ms)
                        {
                            ProfileView? pv = null;
                            try { pv = tr.GetObject(id, OpenMode.ForRead) as ProfileView; } catch { }
                            if (pv == null) continue;
                            pvCount++;

                            W("");
                            W($"ProfileView '{pv.Name}'  handle={pv.Handle}");

                            string pvStyleName = "?";
                            try
                            {
                                if (tr.GetObject(pv.StyleId, OpenMode.ForRead)
                                    is Autodesk.Civil.DatabaseServices.Styles.StyleBase s)
                                    pvStyleName = s.Name;
                            }
                            catch { }
                            W($"  applied PV style = '{pvStyleName}'");

                            // Grid component visibility (reflection — best effort).
                            try
                            {
                                var pvStyleObj = tr.GetObject(pv.StyleId, OpenMode.ForRead);
                                DumpGridVisibility(sb, pvStyleObj);
                            }
                            catch { }

                            // Bands + bound profiles + each band style's text styles/fonts.
                            DumpPvBands(tr, sb, pv);
                        }
                    }
                    if (pvCount == 0) W("(no ProfileView found in model space)");
                }
                catch (System.Exception ex) { W("ERROR: " + ex.Message); }
                tr.Commit();
            }

            string path;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "pv_dump.txt");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[MahodAI] Could not write PV dump file: {ex.Message}\n");
                ed.WriteMessage(sb.ToString());
                return;
            }

            ed.WriteMessage(sb.ToString());
            ed.WriteMessage($"\n[MahodAI] ✅ Profile-view dump written to:\n  {path}\n");
        }

        /// <summary>Reflection-dumps any "*Grid*Visible*" / grid-component visibility on a ProfileViewStyle.</summary>
        private static void DumpGridVisibility(StringBuilder sb, object pvStyle)
        {
            if (pvStyle == null) return;
            try
            {
                foreach (var m in pvStyle.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!m.Name.StartsWith("GetDisplayStyle", StringComparison.Ordinal)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                    foreach (var ev in Enum.GetValues(ps[0].ParameterType))
                    {
                        string name = ev.ToString() ?? "";
                        if (name.IndexOf("Grid", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            var disp = m.Invoke(pvStyle, new object[] { ev });
                            var visProp = disp?.GetType().GetProperty("Visible");
                            object? vis = visProp?.GetValue(disp);
                            sb.Append($"  grid component {name}: visible={vis}\n");
                        }
                        catch { }
                    }
                    break; // one matching getter is enough
                }
            }
            catch { }
        }

        /// <summary>Dumps top + bottom band items: type, style, bound profiles, and each band style's text styles + fonts.</summary>
        private static void DumpPvBands(Transaction tr, StringBuilder sb, ProfileView pv)
        {
            try
            {
                var bands = pv.Bands;
                var dumped = new HashSet<string>();
                DumpPvBandList(tr, sb, "bottom", bands.GetBottomBandItems(), dumped);
                DumpPvBandList(tr, sb, "top", bands.GetTopBandItems(), dumped);
            }
            catch (System.Exception ex) { sb.Append($"  (bands read failed: {ex.Message})\n"); }
        }

        private static void DumpPvBandList(
            Transaction tr, StringBuilder sb, string where, ProfileViewBandItemCollection items, HashSet<string> dumped)
        {
            sb.Append($"  {where} bands ({items.Count}):\n");
            foreach (ProfileViewBandItem b in items)
            {
                string bstyle = "?", p1 = "(none)", p2 = "(none)", btype = "?";
                try { btype = b.BandType.ToString(); } catch { }
                try
                {
                    if (tr.GetObject(b.BandStyleId, OpenMode.ForRead)
                        is Autodesk.Civil.DatabaseServices.Styles.StyleBase s) bstyle = s.Name;
                }
                catch { }
                try { if (!b.Profile1Id.IsNull && tr.GetObject(b.Profile1Id, OpenMode.ForRead) is Profile pf) p1 = pf.Name; } catch { }
                try { if (!b.Profile2Id.IsNull && tr.GetObject(b.Profile2Id, OpenMode.ForRead) is Profile pf) p2 = pf.Name; } catch { }
                sb.Append($"    [{btype}] style='{bstyle}'  prof1='{p1}'  prof2='{p2}'\n");
                try { DumpBandStyleTextStyles(tr, sb, b.BandStyleId); } catch { }
                // Full property structure of each unique band style — reveals where the label/title
                // text style lives (the ????? font cause) when the targeted scan finds nothing.
                try
                {
                    if (!b.BandStyleId.IsNull && dumped.Add(b.BandStyleId.Handle.ToString()))
                        DumpBandStyleFull(tr, sb, b.BandStyleId);
                }
                catch { }
            }
        }

        /// <summary>Dumps every public property of a band style (ObjectId → resolved type+name, strings, enums/primitives)
        /// so the band's label/title text style and font can be located.</summary>
        private static void DumpBandStyleFull(Transaction tr, StringBuilder sb, ObjectId bandStyleId)
        {
            object? bs;
            try { bs = tr.GetObject(bandStyleId, OpenMode.ForRead); } catch { return; }
            if (bs == null) return;
            sb.Append("        --- full band-style props ---\n");
            foreach (var p in bs.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? val;
                try { val = p.GetValue(bs); } catch { continue; }
                if (val == null) continue;

                string desc;
                if (val is ObjectId oid)
                {
                    if (oid.IsNull) continue;
                    string rn = "", rt = "";
                    try
                    {
                        var o = tr.GetObject(oid, OpenMode.ForRead);
                        rt = o.GetType().Name;
                        try { rn = o.GetType().GetProperty("Name")?.GetValue(o)?.ToString() ?? ""; } catch { }
                        if (o is TextStyleTableRecord ts)
                        {
                            string tf = ""; try { tf = ts.Font.TypeFace ?? ""; } catch { }
                            rt = $"TextStyle file='{ts.FileName}' typeface='{tf}'";
                        }
                    }
                    catch { }
                    desc = $"ObjectId -> {rt} '{rn}'";
                }
                else if (val is string s) { desc = $"\"{s}\""; }
                else if (p.PropertyType.IsEnum || p.PropertyType.IsPrimitive) { desc = val.ToString() ?? ""; }
                else { desc = "(" + p.PropertyType.Name + ")"; }

                string rw = p.CanWrite ? "RW" : "RO";
                sb.Append($"          [{rw}] {p.Name} = {desc}\n");
            }
        }

        /// <summary>Reflects a band style (and one level of sub-objects) for every ObjectId property
        /// whose name contains "Text", resolving the text style + font — so the band label/title
        /// font (the ????? cause) is surfaced even when it is nested in a component sub-object.</summary>
        private static void DumpBandStyleTextStyles(Transaction tr, StringBuilder sb, ObjectId bandStyleId)
        {
            if (bandStyleId.IsNull) return;
            object? bs;
            try { bs = tr.GetObject(bandStyleId, OpenMode.ForRead); } catch { return; }
            if (bs == null) return;

            var seen = new HashSet<string>();
            ScanForTextStyles(tr, sb, bs, "", seen, 0);
            if (seen.Count == 0)
                sb.Append("        (no *Text* ObjectId properties found — band label text style is set elsewhere)\n");
        }

        private static void ScanForTextStyles(
            Transaction tr, StringBuilder sb, object node, string prefix, HashSet<string> seen, int depth)
        {
            if (node == null || depth > 2) return;
            foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? val;
                try { val = p.GetValue(node); } catch { continue; }
                if (val == null) continue;

                string path = string.IsNullOrEmpty(prefix) ? p.Name : prefix + "." + p.Name;

                if (val is ObjectId tsId)
                {
                    if (p.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0 || tsId.IsNull) continue;
                    try
                    {
                        if (tr.GetObject(tsId, OpenMode.ForRead) is TextStyleTableRecord ts)
                        {
                            string tf = ""; try { tf = ts.Font.TypeFace ?? ""; } catch { }
                            string key = path + ":" + ts.Name;
                            if (seen.Add(key))
                                sb.Append($"        {path} -> textstyle '{ts.Name}' file='{ts.FileName}' typeface='{tf}'\n");
                        }
                    }
                    catch { }
                    continue;
                }

                // Recurse one more level into Civil component sub-objects (Title / band components).
                var t = val.GetType();
                if (depth < 2 && t.Namespace != null
                    && t.Namespace.StartsWith("Autodesk.Civil", StringComparison.Ordinal)
                    && (p.Name.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.Name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.Name.IndexOf("Band", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    ScanForTextStyles(tr, sb, val, path, seen, depth + 1);
                }
            }
        }

        /// <summary>
        /// Visual Hebrew-font probe. Writes the word "שלום עברית" at the centre of the current
        /// view using several text styles (Standard / arial / MHEB / David if present, plus two
        /// freshly-created TrueType styles with the Hebrew charset), each prefixed by its ASCII
        /// style name. Whichever lines render real Hebrew (vs ?????) tells us which text style to
        /// point the profile-view band titles at — settling whether "arial"/"MHEB" actually render
        /// Hebrew on this machine, or the band title is using a different font entirely.
        /// </summary>
        [CommandMethod("MAHOD_HEBTEST")]
        public void HebTest()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            const string heb = "שלום עברית"; // "Hello Hebrew"

            // Placement: centre of the current view, sized to the view height.
            Point3d basePt = new Point3d(0, 0, 0);
            double txtH = 2.0;
            try
            {
                using var view = ed.GetCurrentView();
                basePt = new Point3d(view.CenterPoint.X, view.CenterPoint.Y, 0);
                txtH = view.Height / 25.0;
            }
            catch { }

            int drawn = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    // Two fresh TrueType styles with the Hebrew charset (177).
                    EnsureProbeTtfStyle(tr, db, "MahodHeb_Arial", "Arial");
                    EnsureProbeTtfStyle(tr, db, "MahodHeb_David", "David");

                    var candidates = new[]
                    { "Standard", "arial", "MHEB", "David", "MahodHeb_Arial", "MahodHeb_David" };

                    var tst = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
                    var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms = bt != null
                        ? tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord
                        : null;
                    if (tst != null && ms != null)
                    {
                        foreach (var sn in candidates)
                        {
                            if (!tst.Has(sn)) continue;
                            var txt = new DBText();
                            txt.SetDatabaseDefaults();
                            txt.TextStyleId = tst[sn];
                            txt.Height = txtH;
                            txt.Position = new Point3d(basePt.X, basePt.Y + drawn * txtH * 2.0, 0);
                            txt.TextString = sn + ": " + heb;
                            txt.Layer = "0";
                            ms.AppendEntity(txt);
                            tr.AddNewlyCreatedDBObject(txt, true);
                            drawn++;
                        }

                        // MTEXT samples — the profile-view band TITLES are MTEXT with an inline
                        // \fArial|...|c177 run + \U+05xx escapes. DBText above renders Hebrew; if
                        // these MTEXT replicas do NOT, the issue is MTEXT/charset-specific on this
                        // machine (not the band data). "esc" = \U+ escapes (exact band format),
                        // "lit" = literal Hebrew.
                        string hebEsc = "\\U+05E9\\U+05DC\\U+05D5\\U+05DD"; // שלום via MTEXT escapes
                        var mtSamples = new (string style, string label, string content)[]
                        {
                            ("arial",    "MT arial c177 esc",  "{\\fArial|b0|i0|c177|p34;" + hebEsc + "}"),
                            ("Standard", "MT std c177 esc",    "{\\fArial|b0|i0|c177|p34;" + hebEsc + "}"),
                            ("arial",    "MT arial nofont esc", hebEsc),
                            ("arial",    "MT arial lit",        heb),
                        };
                        int mi = 0;
                        foreach (var (style, label, content) in mtSamples)
                        {
                            if (!tst.Has(style)) continue;
                            var mt = new MText();
                            mt.SetDatabaseDefaults();
                            mt.TextStyleId = tst[style];
                            mt.TextHeight = txtH;
                            mt.Location = new Point3d(basePt.X + 55 * txtH, basePt.Y + mi * txtH * 2.0, 0);
                            mt.Contents = label + ": " + content;
                            mt.Layer = "0";
                            ms.AppendEntity(mt);
                            tr.AddNewlyCreatedDBObject(mt, true);
                            mi++; drawn++;
                        }
                    }
                }
                catch (System.Exception ex)
                { ed.WriteMessage($"\n[MahodAI] HEBTEST error: {ex.Message}\n"); }
                tr.Commit();
            }

            ed.WriteMessage(
                $"\n[MahodAI] Drew {drawn} Hebrew test lines near ({basePt.X:F0},{basePt.Y:F0}) " +
                "at the centre of the view. Which lines show real Hebrew vs ?????\n");
        }

        /// <summary>Creates a TrueType text style with the Hebrew charset (177) if absent.</summary>
        private static void EnsureProbeTtfStyle(Transaction tr, Database db, string name, string typeface)
        {
            try
            {
                var tst = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
                if (tst == null || tst.Has(name)) return;
                var rec = new TextStyleTableRecord
                {
                    Name = name,
                    Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(typeface, false, false, 177, 34),
                };
                tst.UpgradeOpen();
                tst.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
            catch (System.Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] EnsureProbeTtfStyle({name}) failed: {ex.Message}"); }
        }

        /// <summary>
        /// Deferred command: creates corridor surface in a NEW transaction.
        /// Called via SendStringToExecute AFTER create_corridor transaction commits.
        /// This ensures the corridor has geometry before we add a surface to it.
        /// </summary>
        [CommandMethod("MAHOD_BUILDCORRIDORSURFACE")]
        public void BuildCorridorSurface()
        {
            string corridorName = Tools.Creation.CreateCorridorTool._pendingCorridorName;
            if (string.IsNullOrEmpty(corridorName))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[MahodAI] MAHOD_BUILDCORRIDORSURFACE: no pending corridor name");
                return;
            }

            Tools.Creation.CreateCorridorTool._pendingCorridorName = null;

            var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\n[MahodAI] Building corridor surface for '{corridorName}'...\n");

            var db = HostApplicationServices.WorkingDatabase;
            var civilDoc = CivilDocument.GetCivilDocument(db);
            string surfName = $"{corridorName}_Top";

            // ═══ Transaction 1: Add surface definition + link code + boundary ═══
            using (var tr1 = db.TransactionManager.StartTransaction())
            {
                try
                {
                    Corridor corridor = null;
                    foreach (ObjectId id in civilDoc.CorridorCollection)
                    {
                        var c = tr1.GetObject(id, OpenMode.ForWrite) as Corridor;
                        if (c != null && c.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                        {
                            corridor = c;
                            break;
                        }
                    }

                    if (corridor == null)
                    {
                        ed?.WriteMessage($"\n[MahodAI] Corridor '{corridorName}' not found\n");
                        tr1.Abort();
                        return;
                    }

                    if (corridor.CorridorSurfaces.Count > 0)
                    {
                        ed?.WriteMessage($"\n[MahodAI] Corridor already has {corridor.CorridorSurfaces.Count} surface(s)\n");
                        tr1.Commit();
                        return;
                    }

                    // Rebuild corridor FIRST — now targets are committed from create_corridor,
                    // so BasicSideSlopeCutDitch will extend slopes to EG surface
                    try
                    {
                        corridor.Rebuild();
                        ed?.WriteMessage($"\n[MahodAI] Corridor rebuilt with targets OK\n");
                    }
                    catch (System.Exception rebEx)
                    {
                        ed?.WriteMessage($"\n[MahodAI] Corridor rebuild: {rebEx.Message}\n");
                    }

                    var cs = corridor.CorridorSurfaces.Add(surfName);
                    cs.AddLinkCode("Top", false);
                    cs.Boundaries.AddCorridorExtentsBoundary("Outer");

                    tr1.Commit();
                    ed?.WriteMessage($"\n[MahodAI] Surface definition '{surfName}' added, committing...\n");
                }
                catch (System.Exception ex)
                {
                    ed?.WriteMessage($"\n[MahodAI] Surface definition error: {ex.Message}\n");
                    try { tr1.Abort(); } catch { }
                    return;
                }
            }

            // ═══ Transaction 2: Find the TinSurface and Rebuild (Igor's pattern) ═══
            // After commit, the corridor surface appears as a TinSurface in GetSurfaceIds()
            using (var tr2 = db.TransactionManager.StartTransaction())
            {
                try
                {
                    bool found = false;
                    foreach (ObjectId sfId in civilDoc.GetSurfaceIds())
                    {
                        var sf = tr2.GetObject(sfId, OpenMode.ForWrite) as TinSurface;
                        if (sf != null && sf.Name == surfName)
                        {
                            sf.Rebuild();
                            // Hide the corridor surface's blue triangles WITHOUT hiding the
                            // existing-ground (EG) surface. The previous version turned off
                            // sf.Layer — but a corridor surface inherits the *default* surface
                            // layer, which the EG surface frequently shares, so that hid the
                            // engineer's EG too (reported as "it removed the surface"). Move the
                            // corridor surface onto its OWN dedicated layer and turn off ONLY
                            // that layer, so no shared/EG layer is ever switched off.
                            bool hidden = false;
                            try
                            {
                                const string corrLayer = "MAHOD-CORRIDOR-SURF";
                                var lt = tr2.GetObject(db.LayerTableId, OpenMode.ForWrite) as LayerTable;
                                if (lt != null)
                                {
                                    ObjectId layerId;
                                    if (lt.Has(corrLayer))
                                    {
                                        layerId = lt[corrLayer];
                                    }
                                    else
                                    {
                                        var ltr = new LayerTableRecord { Name = corrLayer };
                                        layerId = lt.Add(ltr);
                                        tr2.AddNewlyCreatedDBObject(ltr, true);
                                    }
                                    // Re-parent the corridor surface onto its dedicated layer —
                                    // never the EG's layer — before hiding it.
                                    sf.Layer = corrLayer;
                                    var layer = tr2.GetObject(layerId, OpenMode.ForWrite) as LayerTableRecord;
                                    if (layer != null)
                                    {
                                        layer.IsOff = true;
                                        hidden = true;
                                        ed?.WriteMessage($"\n[MahodAI] Corridor surface '{surfName}' moved to layer '{corrLayer}' and that layer hidden (EG surface untouched)\n");
                                    }
                                }
                            }
                            catch (System.Exception hideEx)
                            {
                                ed?.WriteMessage($"\n[MahodAI] Corridor surface hide failed (non-critical): {hideEx.Message}\n");
                            }
                            if (!hidden)
                                ed?.WriteMessage($"\n[MahodAI] Note: hide '{surfName}' in Toolspace to remove artifact\n");
                            found = true;
                            ed?.WriteMessage($"\n[MahodAI] TinSurface '{surfName}' rebuilt OK\n");
                            break;
                        }
                    }

                    if (!found)
                    {
                        // TinSurface not found — try corridor.Rebuild() as fallback
                        ed?.WriteMessage($"\n[MahodAI] TinSurface '{surfName}' not in GetSurfaceIds, trying corridor.Rebuild...\n");
                        foreach (ObjectId id in civilDoc.CorridorCollection)
                        {
                            var corr = tr2.GetObject(id, OpenMode.ForWrite) as Corridor;
                            if (corr != null && corr.Name.Equals(corridorName, StringComparison.OrdinalIgnoreCase))
                            {
                                corr.Rebuild();
                                ed?.WriteMessage($"\n[MahodAI] corridor.Rebuild() done\n");
                                break;
                            }
                        }
                    }

                    tr2.Commit();
                    ed?.WriteMessage($"\n[MahodAI] Corridor surface '{surfName}' created successfully\n");
                }
                catch (System.Exception ex)
                {
                    ed?.WriteMessage($"\n[MahodAI] Surface rebuild error: {ex.Message}\n");
                    try { tr2.Abort(); } catch { }
                }
            }
        }
    }
}
