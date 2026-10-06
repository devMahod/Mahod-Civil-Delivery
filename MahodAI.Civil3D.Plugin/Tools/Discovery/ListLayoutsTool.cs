using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.SheetQA;

namespace MahodAI.Civil3D.Plugin.Tools.Discovery
{
    /// <summary>
    /// Lists the drawing's printable sheets (paper-space layout tabs).
    ///
    /// The visual scan works one sheet at a time, so the agent needs to know which sheets
    /// exist before it can loop over them. Model space is deliberately absent: it is not a
    /// sheet, carries no legend, and is not what gets plotted.
    /// </summary>
    public class ListLayoutsTool : DrawingToolBase
    {
        public override string Name => "list_layouts";

        public override string Description =>
            "Lists the drawing's paper-space layout tabs (printable sheets) in tab order, " +
            "with the model viewport and legend status of each. Model space is excluded.";

        public override string Category => ToolCategories.Discovery;

        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var db = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager
                .MdiActiveDocument?.Database;
            if (db == null)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, "No active document."));
            }

            var includeLegend = GetBoolParam(parameters, "include_legend_status", true);

            var styles = new SheetStyleResolver(tr, db);
            var extractor = new SheetSnapshotExtractor(tr, db, styles);
            var legendReader = new LegendBlockReader(tr, styles);

            string? currentLayout = null;
            try { currentLayout = LayoutManager.Current.CurrentLayout; } catch { /* no UI context */ }

            var layouts = new List<LayoutInfo>();
            foreach (var layout in extractor.GetLayouts())
            {
                ct.ThrowIfCancellationRequested();

                var info = new LayoutInfo
                {
                    Name = layout.LayoutName,
                    TabOrder = layout.TabOrder,
                    IsCurrent = string.Equals(layout.LayoutName, currentLayout, StringComparison.OrdinalIgnoreCase)
                };

                try
                {
                    info.HasModelViewport = CountModelViewports(tr, layout, out var viewportCount) > 0;
                    info.ViewportCount = viewportCount;
                }
                catch (Exception ex)
                {
                    info.Note = $"Viewports unreadable: {ex.Message}";
                }

                if (includeLegend)
                {
                    try
                    {
                        var legend = legendReader.Read(layout);
                        info.LegendBlock = legend.BlockName;
                        info.LegendRowCount = legend.Rows.Count;
                    }
                    catch (Exception ex)
                    {
                        info.Note = string.IsNullOrEmpty(info.Note)
                            ? $"Legend unreadable: {ex.Message}"
                            : info.Note + $"; legend unreadable: {ex.Message}";
                    }
                }

                layouts.Add(info);
            }

            return Task.FromResult(ToolResult.Ok(new ListLayoutsResult
            {
                Layouts = layouts,
                TotalCount = layouts.Count,
                CurrentLayout = currentLayout
            }));
        }

        /// <summary>
        /// Counts viewports that actually show model content. Viewport number 1 is paper
        /// space itself and never counts.
        /// </summary>
        private static int CountModelViewports(Transaction tr, Layout layout, out int total)
        {
            total = 0;
            var model = 0;

            if (tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) is not BlockTableRecord btr)
            {
                return 0;
            }

            foreach (ObjectId id in btr)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
                    total++;
                    if (vp.Number > 1 && vp.On && vp.ViewHeight > 0) model++;
                }
                catch { /* skip unreadable viewport */ }
            }

            return model;
        }
    }

    #region Result Models

    public class ListLayoutsResult
    {
        public List<LayoutInfo> Layouts { get; set; } = new();
        public int TotalCount { get; set; }
        public string? CurrentLayout { get; set; }
    }

    public class LayoutInfo
    {
        public string Name { get; set; } = string.Empty;
        public int TabOrder { get; set; }
        public bool IsCurrent { get; set; }
        public bool HasModelViewport { get; set; }
        public int ViewportCount { get; set; }
        public string? LegendBlock { get; set; }
        public int LegendRowCount { get; set; }
        public string? Note { get; set; }
    }

    #endregion
}
