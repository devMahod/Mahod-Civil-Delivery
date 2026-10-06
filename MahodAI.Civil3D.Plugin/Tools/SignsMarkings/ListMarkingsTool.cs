using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Extractors;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Tools.SignsMarkings
{
    /// <summary>
    /// Lists all road markings in the drawing, optionally filtered by alignment.
    /// Read-only: shares the same extraction mechanism as <see cref="Validation.ValidateMarkingsTool"/>.
    /// </summary>
    public class ListMarkingsTool : DrawingToolBase
    {
        public override string Name => "list_markings";
        public override string Description => "Lists all road markings in the drawing with types, patterns, positions, and alignment stations.";
        public override string Category => "SignsMarkings";
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var alignmentName = GetStringParam(parameters, "alignment_name");

            var extractor = new SignMarkingExtractor();
            var result = extractor.ExtractAll(tr, civilDoc,
                HostApplicationServices.WorkingDatabase) as SignMarkingExtractionResult;

            if (result == null)
                return ToolResult.Fail("EXTRACTION_FAILED", "Failed to extract marking data");

            var markings = result.Markings;
            if (!string.IsNullOrEmpty(alignmentName))
                markings = markings.Where(m => m.AlignmentName.Equals(alignmentName, StringComparison.OrdinalIgnoreCase)).ToList();

            return await Task.FromResult(ToolResult.Ok(new
            {
                total_count = markings.Count,
                by_type = markings.GroupBy(m => m.MarkingType).ToDictionary(g => g.Key, g => g.Count()),
                markings = markings.Select(m => new
                {
                    name = m.MarkingType,
                    layer = m.LayerName,
                    pattern = m.Pattern,
                    color = m.Color,
                    alignment = m.AlignmentName,
                    station = m.StartStation,
                    start_station = m.StartStation,
                    end_station = m.EndStation,
                    length = Math.Max(0, m.EndStation - m.StartStation),
                    side = m.Side,
                    width_cm = m.Width,
                    arrow_type = m.ArrowType,
                    text = m.TextContent,
                }).ToList(),
            }));
        }
    }
}
