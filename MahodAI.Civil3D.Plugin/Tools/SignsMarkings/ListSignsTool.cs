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
    /// Lists all road signs in the drawing, optionally filtered by alignment.
    /// </summary>
    public class ListSignsTool : DrawingToolBase
    {
        public override string Name => "list_signs";
        public override string Description => "Lists all road signs in the drawing with codes, types, positions, and alignment stations.";
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
                return ToolResult.Fail("EXTRACTION_FAILED", "Failed to extract sign data");

            var signs = result.Signs;
            if (!string.IsNullOrEmpty(alignmentName))
                signs = signs.Where(s => s.AlignmentName.Equals(alignmentName, StringComparison.OrdinalIgnoreCase)).ToList();

            return await Task.FromResult(ToolResult.Ok(new
            {
                total_count = signs.Count,
                by_type = signs.GroupBy(s => s.SignType).ToDictionary(g => g.Key, g => g.Count()),
                signs = signs.Select(s => new
                {
                    sign_code = s.SignCode,
                    sign_type = s.SignType,
                    description = s.Description,
                    alignment = s.AlignmentName,
                    station = s.Station,
                    side = s.Side,
                    block_name = s.BlockName,
                    layer = s.LayerName,
                }).ToList(),
            }));
        }
    }
}
