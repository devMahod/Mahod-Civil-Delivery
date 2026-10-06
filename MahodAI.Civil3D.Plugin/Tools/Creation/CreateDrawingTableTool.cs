using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Draws an AutoCAD <see cref="Table"/> in model space from a title/headers/rows payload —
    /// the generic table primitive used by engineering reports (volumes, sight-distance,
    /// validation summaries). Supports Hebrew text, an optional subtitle, per-column widths,
    /// and red highlighting of cells whose text matches a keyword. Idempotent by <c>name</c>:
    /// a re-run with the same name erases the previous table first (XData-tagged on layer
    /// MAHOD_TABLES). When both insert points are 0 it auto-places to the right of the drawing.
    /// </summary>
    public class CreateDrawingTableTool : DrawingToolBase
    {
        public override string Name => "create_drawing_table";

        public override string Description =>
            "Creates an AutoCAD Table in the drawing from title + headers + rows (Hebrew OK). " +
            "Used for engineering report tables (earthwork volumes, sight distance, validation " +
            "summaries). Placed at insert_x/insert_y, or auto-placed right of the drawing when both 0.";

        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private const string TableLayer = "MAHOD_TABLES";
        private const string XAppName = "MAHOD_TABLE";

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            string title = GetStringParam(parameters, "title") ?? "";
            string? subtitle = GetStringParam(parameters, "subtitle");
            string[] headers = ParseStringArray(parameters, "headers");
            List<string[]> rows = ParseRows(parameters);
            string? name = GetStringParam(parameters, "name");
            double insertX = GetDoubleParam(parameters, "insert_x") ?? 0.0;
            double insertY = GetDoubleParam(parameters, "insert_y") ?? 0.0;
            double rowHeight = GetDoubleParam(parameters, "row_height") ?? 8.0;
            double textHeight = GetDoubleParam(parameters, "text_height") ?? 3.5;
            double[] columnWidths = ParseDoubleArray(parameters, "column_widths");
            int highlightCol = GetIntParam(parameters, "highlight_column") ?? -1;
            string highlightKeyword = GetStringParam(parameters, "highlight_keyword") ?? "FAIL";

            if (headers.Length == 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "headers is required (non-empty)");
            if (rowHeight <= 0) rowHeight = 8.0;
            if (textHeight <= 0) textHeight = 3.5;

            var db = HostApplicationServices.WorkingDatabase;
            EnsureLayer(tr, db, TableLayer, 7);

            // Idempotency: erase a prior table with the same name (best-effort).
            int replaced = 0;
            if (!string.IsNullOrWhiteSpace(name))
            {
                try { EnsureRegApp(tr, db, XAppName); replaced = EraseByName(tr, db, name!); }
                catch { /* tolerate — just draw a new one */ }
            }

            int nCols = headers.Length;
            int hasSub = string.IsNullOrWhiteSpace(subtitle) ? 0 : 1;
            int titleRow = 0;
            int subRow = hasSub == 1 ? 1 : -1;
            int headerRow = 1 + hasSub;
            int dataStart = headerRow + 1;
            int totalRows = dataStart + rows.Count;

            // Column widths are needed up front: the auto-placement measures the table's real
            // footprint so it can be centred on the drawing and stacked clear of other tables.
            var colWidths = new double[nCols];
            for (int c = 0; c < nCols; c++)
                colWidths[c] = (c < columnWidths.Length && columnWidths[c] > 0)
                    ? columnWidths[c]
                    : EstimateColumnWidth(c, headers, rows, textHeight);
            double tableWidth = colWidths.Sum();
            double tableHeight = totalRows * rowHeight;

            // Auto-place when no point was given: a clear column just right of the drawing
            // content, vertically centred on it — level with the plan, profile view, section
            // views and the other report tables. The old rule (db.Extmax corner) put the table
            // kilometres off the road whenever a single far-away entity stretched the drawing
            // extents, which is what the engineer saw (2026-07-27).
            var placementNotes = new List<string>();
            string placementMethod = "explicit";
            if (insertX == 0.0 && insertY == 0.0)
            {
                placementMethod = AutoPlace(
                    tr, civilDoc, db, tableWidth, tableHeight,
                    out insertX, out insertY, placementNotes);
            }

            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = bt == null
                ? null
                : tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
            if (ms == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Model space unavailable");

            var table = new Table();
            table.SetDatabaseDefaults();
            try { table.TableStyle = db.Tablestyle; } catch { /* default style */ }
            table.Layer = TableLayer;
            table.Position = new Point3d(insertX, insertY, 0);
            table.SetSize(totalRows, nCols);

            // Title (merged across all columns).
            SetCell(table, titleRow, 0, title, textHeight * 1.25);
            TryMerge(table, titleRow, 0, titleRow, nCols - 1);
            if (subRow >= 0)
            {
                SetCell(table, subRow, 0, subtitle!, textHeight * 0.9);
                TryMerge(table, subRow, 0, subRow, nCols - 1);
            }

            // Header row.
            for (int c = 0; c < nCols; c++)
                SetCell(table, headerRow, c, headers[c], textHeight);

            // Data rows.
            for (int r = 0; r < rows.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows[r];
                for (int c = 0; c < nCols; c++)
                {
                    string text = c < row.Length ? row[c] : "";
                    SetCell(table, dataStart + r, c, text, textHeight);
                    if (highlightCol >= 0 && highlightCol < nCols && c == highlightCol &&
                        !string.IsNullOrEmpty(highlightKeyword) &&
                        text.IndexOf(highlightKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { table.Cells[dataStart + r, c].ContentColor = AcColor.FromColorIndex(AcColorMethod.ByAci, 1); }
                        catch { /* tolerate */ }
                    }
                }
            }

            // Column widths + row heights (best-effort).
            for (int c = 0; c < nCols; c++)
            {
                try { table.Columns[c].Width = colWidths[c]; } catch { }
            }
            for (int r = 0; r < totalRows; r++)
            {
                try { table.Rows[r].Height = rowHeight; } catch { }
            }

            try { table.GenerateLayout(); } catch { }

            ms.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);

            if (!string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    table.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, XAppName),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, name!));
                }
                catch { /* name tag is optional */ }
            }

            cache.RemoveByPattern("get_drawing_summary:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                success = true,
                table_name = name,
                rows = rows.Count,
                columns = nCols,
                replaced_existing = replaced,
                insert_point = new { x = Math.Round(insertX, 3), y = Math.Round(insertY, 3) },
                placement = new { method = placementMethod, notes = placementNotes },
                layer = TableLayer,
                message = $"Drew table '{title}' ({rows.Count} rows × {nCols} cols) on layer {TableLayer}" +
                          (replaced > 0 ? $" (replaced {replaced} prior)" : "") + ".",
            }));
        }

        /// <summary>
        /// Resolves the table's top-left insertion point from the real drawing content: a clear
        /// column just right of everything, vertically centred on it, stacked below any table
        /// already parked there. Report tables (layer <see cref="TableLayer"/>) are measured
        /// separately so they never push the column further right on every run, and entities far
        /// outside the road's region are discarded so a stray origin block cannot fling the table
        /// off the drawing. Returns the placement method; falls back to the drawing extents and
        /// finally leaves (0,0) untouched only when nothing at all is measurable.
        /// </summary>
        private static string AutoPlace(
            Transaction tr,
            CivilDocument civilDoc,
            Database db,
            double tableWidth,
            double tableHeight,
            out double x,
            out double y,
            List<string> notes)
        {
            x = 0.0;
            y = 0.0;

            var content = new List<DrawingTablePlacement.Rect>();
            var tables = new List<DrawingTablePlacement.Rect>();
            // The stage-3 deliverable row the table has to line up with.
            var band = new List<DrawingTablePlacement.Rect>();
            try
            {
                // The road(s) define the region worth measuring.
                DrawingTablePlacement.Rect? keep = null;
                var roads = new List<DrawingTablePlacement.Rect>();
                try
                {
                    foreach (ObjectId alId in civilDoc.GetAlignmentIds())
                    {
                        if (tr.GetObject(alId, OpenMode.ForRead)
                            is not Autodesk.Civil.DatabaseServices.Alignment al) continue;
                        try
                        {
                            var ae = al.GeometricExtents;
                            roads.Add(new DrawingTablePlacement.Rect(
                                ae.MinPoint.X, ae.MinPoint.Y, ae.MaxPoint.X, ae.MaxPoint.Y));
                        }
                        catch { /* alignment without usable extents */ }
                    }
                }
                catch { /* no Civil document / no alignments */ }

                if (roads.Count > 0)
                {
                    double rMinX = double.PositiveInfinity, rMinY = double.PositiveInfinity;
                    double rMaxX = double.NegativeInfinity, rMaxY = double.NegativeInfinity;
                    foreach (var r in roads)
                    {
                        rMinX = Math.Min(rMinX, r.MinX); rMinY = Math.Min(rMinY, r.MinY);
                        rMaxX = Math.Max(rMaxX, r.MaxX); rMaxY = Math.Max(rMaxY, r.MaxY);
                    }
                    keep = DrawingTablePlacement.KeepRegion(
                        new DrawingTablePlacement.Rect(rMinX, rMinY, rMaxX, rMaxY));
                }

                int outliers = 0;
                if (tr.GetObject(db.BlockTableId, OpenMode.ForRead) is BlockTable bt &&
                    tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) is BlockTableRecord ms)
                {
                    foreach (ObjectId entId in ms)
                    {
                        try
                        {
                            if (tr.GetObject(entId, OpenMode.ForRead) is not Entity ent) continue;
                            var ext = ent.GeometricExtents;
                            var rect = new DrawingTablePlacement.Rect(
                                ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y);
                            if (keep.HasValue && !rect.Intersects(keep.Value)) { outliers++; continue; }

                            if (ent is Table ||
                                string.Equals(ent.Layer, TableLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                tables.Add(rect);
                                continue;
                            }

                            content.Add(rect);
                            if (ent is Autodesk.Civil.DatabaseServices.ProfileView ||
                                ent is Autodesk.Civil.DatabaseServices.SectionView)
                                band.Add(rect);
                        }
                        catch { /* entities without valid extents (empty blocks) are skipped */ }
                    }
                }
                if (outliers > 0)
                    notes.Add($"Ignored {outliers} entity(ies) far outside the road region when measuring extents.");
            }
            catch (Exception ex)
            {
                notes.Add($"Extents scan failed: {ex.Message}");
            }

            if (band.Count > 0)
                notes.Add($"Levelled with {band.Count} profile/section view(s).");
            var placement = DrawingTablePlacement.Choose(content, tables, tableWidth, tableHeight, band);
            if (placement.Resolved)
            {
                x = placement.X;
                y = placement.Y;
                notes.AddRange(placement.Notes);
                return placement.Method;
            }

            // Nothing measurable around the road — fall back to the raw drawing extents.
            try
            {
                Point3d emin = db.Extmin, emax = db.Extmax;
                if (emax.X > emin.X && emax.X < 1e18)
                {
                    x = emax.X + Math.Max(20.0, (emax.X - emin.X) * 0.05);
                    y = (emin.Y + emax.Y) / 2.0 + tableHeight / 2.0;
                    notes.Add("No measurable content near the road; used the drawing extents.");
                    return "drawing_extents";
                }
            }
            catch { /* keep 0,0 */ }

            notes.Add("Drawing has no measurable extents; table left at the origin.");
            return "origin_fallback";
        }

        private static void SetCell(Table table, int r, int c, string text, double height)
        {
            try { table.Cells[r, c].TextString = text ?? ""; } catch { }
            try { table.Cells[r, c].TextHeight = height; } catch { }
            try { table.Cells[r, c].Alignment = CellAlignment.MiddleCenter; } catch { }
        }

        private static void TryMerge(Table table, int r1, int c1, int r2, int c2)
        {
            if (c2 <= c1) return;
            try { table.MergeCells(CellRange.Create(table, r1, c1, r2, c2)); } catch { }
        }

        private static double EstimateColumnWidth(
            int col, string[] headers, List<string[]> rows, double textHeight)
        {
            int maxChars = col < headers.Length ? headers[col].Length : 1;
            foreach (var row in rows)
                if (col < row.Length) maxChars = Math.Max(maxChars, (row[col] ?? "").Length);
            // ~0.8 drawing units per char at the given text height, with sane bounds.
            return Math.Max(12.0, Math.Min(120.0, maxChars * textHeight * 0.8 + textHeight));
        }

        private static void EnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            if (tr.GetObject(db.LayerTableId, OpenMode.ForRead) is not LayerTable lt) return;
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name };
            ltr.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, colorIndex);
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        private static void EnsureRegApp(Transaction tr, Database db, string app)
        {
            if (tr.GetObject(db.RegAppTableId, OpenMode.ForRead) is not RegAppTable rat) return;
            if (rat.Has(app)) return;
            rat.UpgradeOpen();
            var ratr = new RegAppTableRecord { Name = app };
            rat.Add(ratr);
            tr.AddNewlyCreatedDBObject(ratr, true);
        }

        private static int EraseByName(Transaction tr, Database db, string name)
        {
            if (tr.GetObject(db.BlockTableId, OpenMode.ForRead) is not BlockTable bt) return 0;
            if (tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) is not BlockTableRecord ms)
                return 0;
            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Table t) continue;
                var xd = t.GetXDataForApplication(XAppName);
                if (xd == null) continue;
                foreach (TypedValue tv in xd)
                {
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        string.Equals(tv.Value?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        toErase.Add(id);
                        break;
                    }
                }
            }
            foreach (ObjectId id in toErase)
                if (tr.GetObject(id, OpenMode.ForWrite) is Entity e) e.Erase();
            return toErase.Count;
        }

        private static string[] ParseStringArray(JsonElement p, string name)
        {
            if (!p.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return arr.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
                .ToArray();
        }

        private static double[] ParseDoubleArray(JsonElement p, string name)
        {
            if (!p.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<double>();
            var list = new List<double>();
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d))
                    list.Add(d);
            return list.ToArray();
        }

        private static List<string[]> ParseRows(JsonElement p)
        {
            var rows = new List<string[]>();
            if (!p.TryGetProperty("rows", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array) continue;
                var cells = row.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
                    .ToArray();
                rows.Add(cells);
            }
            return rows;
        }
    }
}
