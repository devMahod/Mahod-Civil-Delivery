using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Utilities;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Annotates an alignment with firm-style station markings: at each interval station a
    /// perpendicular tick + an elevation label (from the FG profile) + a chainage/station
    /// label, on AE_&lt;name&gt;_CR / _EL / _ST layers (cleared per run for idempotent re-labelling).
    /// Reimplements the MahodCivilNet <c>Al_SetElevations</c> mechanism inside the AI plugin.
    /// </summary>
    public class SetStationLabelsTool : DrawingToolBase
    {
        public override string Name => "set_station_labels";

        public override string Description =>
            "Draws station (chainage 0+000) + elevation labels with perpendicular tick marks along " +
            "an alignment, every `interval` metres, on AE_<name>_CR/_EL/_ST layers (cleared per run). " +
            "Elevations come from the alignment's FG profile (or a named profile). Mirrors the firm's " +
            "Al_SetElevations annotation.";

        public override string Category => ToolCategories.Alignment;
        public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

        private const short ColorTick = 7;   // white
        private const short ColorElev = 3;   // green
        private const short ColorSta = 4;    // cyan

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            string alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            string? profileName = GetStringParam(parameters, "profile_name");
            double interval = GetDoubleParam(parameters, "interval") ?? 20.0;
            double textSize = GetDoubleParam(parameters, "text_size") ?? 2.0;
            double offset = GetDoubleParam(parameters, "offset") ?? 0.0;
            string mode = (GetStringParam(parameters, "mode") ?? "station").Trim().ToLowerInvariant();
            if (interval <= 0) interval = 20.0;
            if (textSize <= 0) textSize = 2.0;

            var alignment = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, alignmentName);
            if (alignment == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            CivilDb.Profile? profile = ResolveProfile(tr, alignment, profileName);

            var db = HostApplicationServices.WorkingDatabase;
            string baseLayer = SanitizeLayer($"AE_{alignment.Name}");
            string crLayer = baseLayer + "_CR";
            string elLayer = baseLayer + "_EL";
            string stLayer = baseLayer + "_ST";
            EnsureLayer(tr, db, crLayer, ColorTick);
            EnsureLayer(tr, db, elLayer, ColorElev);
            EnsureLayer(tr, db, stLayer, ColorSta);
            int cleared = ClearLayers(tr, db, new[] { crLayer, elLayer, stLayer });

            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = bt == null
                ? null
                : tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
            if (ms == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Model space unavailable");

            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            double tickLen = textSize * 1.5;

            int labelCount = 0;
            int missingElev = 0;

            foreach (double sta in StationLabelGeometry.BuildStationList(start, end, interval))
            {
                ct.ThrowIfCancellationRequested();

                double x = 0, y = 0;
                try { alignment.PointLocation(sta, offset, ref x, ref y); }
                catch { continue; }

                double bearing = TangentBearing(alignment, sta, start, end);
                double nx = -Math.Sin(bearing);   // unit normal (perpendicular to tangent)
                double ny = Math.Cos(bearing);
                double rot = StationLabelGeometry.UprightRotation(bearing);

                // Perpendicular tick centred on the centreline point.
                AddLine(tr, ms, crLayer,
                    new Point3d(x - nx * tickLen * 0.5, y - ny * tickLen * 0.5, 0),
                    new Point3d(x + nx * tickLen * 0.5, y + ny * tickLen * 0.5, 0));

                // Elevation label on the +normal side (from the profile).
                if (profile != null && TryElevationAt(profile, sta, out double elev))
                {
                    AddText(tr, ms, elLayer, $"{elev:F2}",
                        new Point3d(x + nx * tickLen * 0.6, y + ny * tickLen * 0.6, 0), textSize, rot);
                }
                else if (profile != null)
                {
                    missingElev++;
                }

                // Station / section label on the -normal side.
                string staText = mode == "section"
                    ? StationLabelGeometry.SectionIndex(sta, start, interval).ToString()
                    : StationLabelGeometry.FormatChainage(sta);
                AddText(tr, ms, stLayer, staText,
                    new Point3d(x - nx * tickLen * 0.9, y - ny * tickLen * 0.9, 0), textSize, rot);

                labelCount++;
            }

            cache.RemoveByPattern("get_drawing_summary:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                success = true,
                alignment_name = alignment.Name,
                profile_name = profile?.Name,
                interval,
                mode = mode == "section" ? "section" : "station",
                label_count = labelCount,
                stations_outside_profile = missingElev,
                cleared_existing = cleared,
                layers = new { tick = crLayer, elevation = elLayer, station = stLayer },
                message =
                    $"Drew {labelCount} station labels on {baseLayer}_* (every {interval:F0} m" +
                    (profile != null ? $", elevations from profile '{profile.Name}'" : ", no profile — station only") +
                    ").",
            }));
        }

        /// <summary>Named profile if given; otherwise the FG profile; otherwise the first profile.</summary>
        private static CivilDb.Profile? ResolveProfile(Transaction tr, CivilDb.Alignment alignment, string? name)
        {
            CivilDb.Profile? first = null, fg = null, named = null;
            ObjectIdCollection ids;
            try { ids = alignment.GetProfileIds(); }
            catch { return null; }

            foreach (ObjectId pid in ids)
            {
                if (tr.GetObject(pid, OpenMode.ForRead) is not CivilDb.Profile p) continue;
                first ??= p;
                if (!string.IsNullOrWhiteSpace(name) &&
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    named = p;
                try { if (p.ProfileType == CivilDb.ProfileType.FG) fg = p; }
                catch { /* older API / unknown type — ignore */ }
            }
            return named ?? fg ?? first;
        }

        /// <summary>Tangent bearing (rad) at a station, from two nearby PointLocation samples.</summary>
        private static double TangentBearing(CivilDb.Alignment al, double sta, double start, double end)
        {
            const double d = 0.5;
            double s0 = sta, s1 = sta + d;
            if (s1 > end) { s1 = sta; s0 = Math.Max(start, sta - d); }
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            try
            {
                al.PointLocation(s0, 0, ref x0, ref y0);
                al.PointLocation(s1, 0, ref x1, ref y1);
            }
            catch { return 0.0; }
            if (Math.Abs(x1 - x0) < 1e-9 && Math.Abs(y1 - y0) < 1e-9) return 0.0;
            return Math.Atan2(y1 - y0, x1 - x0);
        }

        private static bool TryElevationAt(CivilDb.Profile p, double station, out double elev)
        {
            elev = 0;
            try
            {
                elev = p.ElevationAt(station);
                return !double.IsNaN(elev) && !double.IsInfinity(elev);
            }
            catch { return false; }
        }

        private static void AddLine(Transaction tr, BlockTableRecord ms, string layer, Point3d a, Point3d b)
        {
            var ln = new Line(a, b);
            ln.SetDatabaseDefaults();
            ln.Layer = layer;
            ms.AppendEntity(ln);
            tr.AddNewlyCreatedDBObject(ln, true);
        }

        private static void AddText(Transaction tr, BlockTableRecord ms, string layer,
            string text, Point3d pos, double height, double rotation)
        {
            var t = new DBText();
            t.SetDatabaseDefaults();
            t.Layer = layer;
            t.TextString = text;
            t.Height = height;
            t.Position = pos;
            t.Rotation = rotation;
            ms.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
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

        /// <summary>Erase existing entities on the AE_* layers so a re-run replaces, not stacks.</summary>
        private static int ClearLayers(Transaction tr, Database db, string[] layers)
        {
            var set = new HashSet<string>(layers, StringComparer.OrdinalIgnoreCase);
            if (tr.GetObject(db.BlockTableId, OpenMode.ForRead) is not BlockTable bt) return 0;
            if (tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) is not BlockTableRecord ms) return 0;

            var toErase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e && set.Contains(e.Layer))
                    toErase.Add(id);
            }
            foreach (ObjectId id in toErase)
            {
                if (tr.GetObject(id, OpenMode.ForWrite) is Entity e) e.Erase();
            }
            return toErase.Count;
        }

        /// <summary>Layer-safe, ASCII-only name (Hebrew/odd chars break Civil 3D geometry names).</summary>
        private static string SanitizeLayer(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(((c < 128 && char.IsLetterOrDigit(c)) || c == '_' || c == '-') ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "AE_Road";
        }
    }
}
