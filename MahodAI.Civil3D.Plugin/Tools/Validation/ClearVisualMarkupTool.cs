using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.SheetQA;

namespace MahodAI.Civil3D.Plugin.Tools.Validation
{
    /// <summary>
    /// Removes every review circle and id the visual scan drew.
    ///
    /// Scan markup is disposable by design — it all lives on one layer and describes nothing
    /// the drawing needs — so clearing it is a safe, complete undo of a scan's visible
    /// effect. It never touches project geometry: only entities on
    /// <see cref="VisualMarkupWriter.MarkupLayerName"/> are erased.
    /// </summary>
    public class ClearVisualMarkupTool : DrawingToolBase
    {
        public override string Name => "clear_visual_markup";

        public override string Description =>
            "Erases all visual-scan review markup (circles and finding ids) from the drawing. " +
            "Only entities on the MAHOD_VISUAL_SCAN layer are removed; project geometry is untouched.";

        public override string Category => ToolCategories.Validation;

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
                    VisualScanErrorCodes.ScanFailed, "No active document."));
            }

            try
            {
                var writer = new VisualMarkupWriter(tr, db);
                var erased = writer.EraseAllMarkup();

                return Task.FromResult(ToolResult.Ok(new ClearVisualMarkupResult
                {
                    Erased = erased,
                    MarkupLayer = VisualMarkupWriter.MarkupLayerName,
                    Message = erased > 0
                        ? $"Removed {erased} markup entities."
                        : "There was no visual-scan markup to remove."
                }));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    VisualScanErrorCodes.ScanFailed,
                    $"Could not clear visual markup: {ex.Message}",
                    ex.ToString()));
            }
        }
    }

    public class ClearVisualMarkupResult
    {
        public int Erased { get; set; }
        public string MarkupLayer { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
