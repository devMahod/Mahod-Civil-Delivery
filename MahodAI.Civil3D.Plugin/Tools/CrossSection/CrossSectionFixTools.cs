using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.CrossSection
{
    /// <summary>Shared plumbing for the three write tools.</summary>
    internal static class CrossSectionFixSupport
    {
        internal static Database? ActiveDb() =>
            Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument?.Database;

        /// <summary>Ensures the working layer exists and returns its name.</summary>
        internal static string EnsurePreviewLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(CrossSectionLayers.Preview))
            {
                lt.UpgradeOpen();
                var rec = new LayerTableRecord
                {
                    Name = CrossSectionLayers.Preview,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 6)   // magenta — reads as "not final"
                };
                lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
            return CrossSectionLayers.Preview;
        }

        /// <summary>Erases everything previously drawn on the working layer.</summary>
        internal static int ClearPreview(Transaction tr, Database db)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int n = 0;
            foreach (ObjectId id in ms)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                if (e.Layer != CrossSectionLayers.Preview) continue;
                tr.GetObject(id, OpenMode.ForWrite).Erase();
                n++;
            }
            return n;
        }

        /// <summary>Copies the drafting style of an existing annotation onto a new one.</summary>
        internal static DBText CloneStyle(DBText source, string layer, string value, Point3d position)
        {
            var t = new DBText();
            t.SetDatabaseDefaults();
            t.TextStyleId = source.TextStyleId;
            t.Height = source.Height;
            t.WidthFactor = source.WidthFactor;
            t.Rotation = source.Rotation;
            t.Oblique = source.Oblique;
            t.Layer = layer;
            t.ColorIndex = 256;
            t.TextString = value;
            t.Position = position;
            return t;
        }

        /// <summary>Builds a dimension chain polyline: baseline with an up-tick at each ordinate.</summary>
        internal static Polyline BuildChain(IEnumerable<double> tickX, double baseY, double tickHeight, string layer)
        {
            var pl = new Polyline();
            int i = 0;
            foreach (var x in tickX)
            {
                pl.AddVertexAt(i++, new Point2d(x, baseY), 0, 0, 0);
                pl.AddVertexAt(i++, new Point2d(x, baseY + tickHeight), 0, 0, 0);
                pl.AddVertexAt(i++, new Point2d(x, baseY), 0, 0, 0);
            }
            pl.Layer = layer;
            pl.ColorIndex = 256;
            return pl;
        }

        /// <summary>Sections named by the caller, or all of them.</summary>
        internal static List<SectionModel> Select(Transaction tr, Database db, JsonElement parameters)
        {
            var all = CrossSectionReader.ReadAll(tr, db);
            if (parameters.TryGetProperty("sections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var wanted = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (wanted.Count > 0) return all.Where(s => wanted.Contains(s.Number)).ToList();
            }
            if (parameters.TryGetProperty("section", out var one) && one.ValueKind == JsonValueKind.String)
            {
                var n = one.GetString()!.Trim();
                return all.Where(s => s.Number == n).ToList();
            }
            return all;
        }

        internal static readonly JsonElement SectionsSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "section":  { "type": "string", "description": "A single section number, e.g. \"224\"." },
            "sections": { "type": "array", "items": { "type": "string" },
                          "description": "Several section numbers. Omit both to take every section." }
          }
        }
        """).RootElement;
    }

    /// <summary>
    /// Draws the corrected annotation on the MAHOD_FIX working layer, on top of
    /// the existing one. Native geometry is not touched — the engineer compares
    /// the two by eye and then accepts or discards.
    ///
    /// This is the engineer's own review habit made into a tool: she overlays the
    /// corrected line before committing to it.
    /// </summary>
    public sealed class PreviewCrossSectionFixTool : DrawingToolBase
    {
        public override string Name => "preview_cross_section_fix";

        public override string Description =>
            "Draw the proposed cross-section correction on the MAHOD_FIX working layer, over the " +
            "existing annotation, WITHOUT changing anything native. Shows the dimension chain moved " +
            "to a whole level, the kerb pair collapsed, and the recalculated distances and heights. " +
            "Follow with accept_cross_section_fix or discard_cross_section_fix.";

        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(90);
        public override JsonElement? ParameterSchema => CrossSectionFixSupport.SectionsSchema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var db = CrossSectionFixSupport.ActiveDb();
            if (db == null) return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active drawing."));

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            string layer = CrossSectionFixSupport.EnsurePreviewLayer(tr, db);
            int cleared = CrossSectionFixSupport.ClearPreview(tr, db);

            var sections = CrossSectionFixSupport.Select(tr, db, parameters);
            var report = new List<object>();
            int drawn = 0;

            foreach (var s in sections)
            {
                ct.ThrowIfCancellationRequested();
                if (!s.IsResolved || s.Frame == null) continue;

                var plan = CrossSectionFixPlanner.Plan(s);
                if (plan.IsEmpty)
                {
                    report.Add(new { section = s.Number, action = "nothing to change" });
                    continue;
                }

                double dy = plan.MovesLevel ? plan.LevelDy : 0.0;

                // chain, minus the dropped kerb tick, at the corrected level
                var ticks = s.Ticks.Where(x => !plan.HasKerbPair || Math.Abs(x - plan.KerbSecondX) > 0.005).ToList();
                var chain = CrossSectionFixSupport.BuildChain(ticks, s.ChainBaseY + dy, s.TickHeight, layer);
                ms.AppendEntity(chain);
                tr.AddNewlyCreatedDBObject(chain, true);

                // distance texts: drop the kerb one, update the merged span
                foreach (var d in s.Distances)
                {
                    if (plan.HasKerbPair && d.Id == plan.KerbDistanceTextId) continue;
                    if (tr.GetObject(d.Id, OpenMode.ForRead) is not DBText src) continue;
                    string value = (plan.HasKerbPair && d.Id == plan.MergedSpanTextId) ? plan.MergedSpanValue : d.Raw;
                    var t = CrossSectionFixSupport.CloneStyle(src, layer, value, new Point3d(d.X, d.Y + dy, 0));
                    ms.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                }

                // height labels: drop the lower of the kerb pair, shift the survivor left
                foreach (var h in s.Heights)
                {
                    if (plan.HasKerbPair && h.Id == plan.LowerHeightId) continue;
                    if (tr.GetObject(h.Id, OpenMode.ForRead) is not DBText src) continue;
                    double x = h.X + (plan.HasKerbPair && h.Id == plan.SurvivingHeightId ? plan.HeightMoveDx : 0.0);
                    var t = CrossSectionFixSupport.CloneStyle(src, layer, h.Raw, new Point3d(x, h.Y + dy, 0));
                    ms.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                }

                drawn++;
                report.Add(new
                {
                    section = s.Number,
                    levelMove = plan.MovesLevel ? $"{Math.Round(plan.LevelFrom, 3)} → {Math.Round(plan.LevelTo, 0)}" : null,
                    kerbCollapsed = plan.HasKerbPair,
                    spanUpdated = plan.HasKerbPair ? $"{plan.MergedSpanWas} → {plan.MergedSpanValue}" : null,
                    notes = plan.Notes
                });
            }

            return Task.FromResult(ToolResult.Ok(new
            {
                previewLayer = layer,
                sectionsDrawn = drawn,
                previousPreviewErased = cleared,
                nativeGeometryTouched = false,
                next = "accept_cross_section_fix or discard_cross_section_fix",
                detail = report
            }));
        }
    }

    /// <summary>
    /// Applies the plan to the native annotation and clears the working layer.
    /// Re-plans from the current drawing rather than trusting the preview, so a
    /// stale preview can never be applied to a section that has since changed.
    /// </summary>
    public sealed class AcceptCrossSectionFixTool : DrawingToolBase
    {
        public override string Name => "accept_cross_section_fix";

        public override string Description =>
            "Apply the previewed cross-section correction to the real annotation and remove the " +
            "MAHOD_FIX preview. Moves the dimension chain to a whole level, collapses the kerb pair, " +
            "recalculates the affected distance, and removes orphaned leader stubs.";

        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(90);
        public override JsonElement? ParameterSchema => CrossSectionFixSupport.SectionsSchema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var db = CrossSectionFixSupport.ActiveDb();
            if (db == null) return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active drawing."));

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var sections = CrossSectionFixSupport.Select(tr, db, parameters);
            var report = new List<object>();
            int changed = 0;

            foreach (var s in sections)
            {
                ct.ThrowIfCancellationRequested();
                if (!s.IsResolved || s.Frame == null) continue;

                var plan = CrossSectionFixPlanner.Plan(s);
                if (plan.IsEmpty)
                {
                    report.Add(new { section = s.Number, action = "nothing to change" });
                    continue;
                }

                var orphans = CrossSectionFixPlanner.ResolveOrphanStubs(tr, s, plan);

                // 1 · delete the lower gova and the kerb distance text
                if (plan.HasKerbPair)
                {
                    if (!plan.LowerHeightId.IsNull)
                        tr.GetObject(plan.LowerHeightId, OpenMode.ForWrite).Erase();
                    if (!plan.KerbDistanceTextId.IsNull)
                        tr.GetObject(plan.KerbDistanceTextId, OpenMode.ForWrite).Erase();

                    // 2 · move the surviving gova onto the first tick.
                    //     TransformBy, never Position — assigning Position to a DBText
                    //     whose justification is not left/baseline is silently ignored.
                    if (!plan.SurvivingHeightId.IsNull)
                    {
                        var e = (Entity)tr.GetObject(plan.SurvivingHeightId, OpenMode.ForWrite);
                        e.TransformBy(Matrix3d.Displacement(new Vector3d(plan.HeightMoveDx, 0, 0)));
                    }

                    // 3 · the span that widens
                    if (!plan.MergedSpanTextId.IsNull &&
                        tr.GetObject(plan.MergedSpanTextId, OpenMode.ForWrite) is DBText merged)
                    {
                        merged.TextString = plan.MergedSpanValue;
                    }

                    // 4 · rebuild the chain without the dropped tick
                    var ticks = s.Ticks.Where(x => Math.Abs(x - plan.KerbSecondX) > 0.005).ToList();
                    tr.GetObject(s.ChainId, OpenMode.ForWrite).Erase();
                    var chain = CrossSectionFixSupport.BuildChain(
                        ticks, s.ChainBaseY, s.TickHeight, CrossSectionLayers.DesignChain);
                    ms.AppendEntity(chain);
                    tr.AddNewlyCreatedDBObject(chain, true);
                    s.ChainId = chain.ObjectId;
                }

                // 5 · orphaned and zero-length leader stubs
                foreach (var id in orphans)
                    tr.GetObject(id, OpenMode.ForWrite).Erase();

                // 6 · the level move, applied last so it carries everything above with it
                if (plan.MovesLevel)
                {
                    var disp = Matrix3d.Displacement(new Vector3d(0, plan.LevelDy, 0));
                    var block = new List<ObjectId> { s.ChainId };
                    block.AddRange(s.Stubs.Where(id => !orphans.Contains(id)));
                    block.AddRange(s.Distances.Where(d => d.Id != plan.KerbDistanceTextId).Select(d => d.Id));
                    block.AddRange(s.Heights.Where(h => h.Id != plan.LowerHeightId).Select(h => h.Id));
                    foreach (var id in block)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).TransformBy(disp);
                    }
                }

                changed++;
                report.Add(new
                {
                    section = s.Number,
                    levelMove = plan.MovesLevel ? $"{Math.Round(plan.LevelFrom, 3)} → {Math.Round(plan.LevelTo, 0)}" : null,
                    kerbCollapsed = plan.HasKerbPair,
                    spanUpdated = plan.HasKerbPair ? $"{plan.MergedSpanWas} → {plan.MergedSpanValue}" : null,
                    stubsRemoved = orphans.Count
                });
            }

            int cleared = CrossSectionFixSupport.ClearPreview(tr, db);

            return Task.FromResult(ToolResult.Ok(new
            {
                sectionsChanged = changed,
                previewErased = cleared,
                detail = report
            }));
        }
    }

    /// <summary>Throws the preview away. Native geometry was never touched, so this is a clean abort.</summary>
    public sealed class DiscardCrossSectionFixTool : DrawingToolBase
    {
        public override string Name => "discard_cross_section_fix";

        public override string Description =>
            "Remove the MAHOD_FIX cross-section preview without applying it. " +
            "Native geometry was never modified, so nothing else changes.";

        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc, JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var db = CrossSectionFixSupport.ActiveDb();
            if (db == null) return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active drawing."));

            int cleared = CrossSectionFixSupport.ClearPreview(tr, db);
            return Task.FromResult(ToolResult.Ok(new
            {
                previewErased = cleared,
                nativeGeometryTouched = false
            }));
        }
    }
}
