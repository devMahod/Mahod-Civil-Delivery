using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Finds intersection points between alignments on demand.
    /// Returns intersection type, coordinates, angles, and stations.
    /// </summary>
    public class FindIntersectionsTool : DrawingToolBase
    {
        public override string Name => "find_intersections";
        public override string Description => "Finds intersection points between alignments. Returns intersection type, coordinates, angles, and stations.";
        public override string Category => ToolCategories.Alignment;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentNameFilter = GetStringParam(parameters, "alignment_name");
            var includeAngles = GetBoolParam(parameters, "include_angles", true);

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            // Use DrawingSummaryExtractor to detect intersections
            var extractor = new DrawingSummaryExtractor();
            var summary = extractor.ExtractSummary();

            var intersections = summary.Intersections;

            // Filter by alignment name if specified
            if (!string.IsNullOrEmpty(alignmentNameFilter))
            {
                intersections = intersections
                    .Where(ix => ix.AlignmentNames.Any(
                        n => n.Equals(alignmentNameFilter, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            // Optionally strip angle data
            if (!includeAngles)
            {
                foreach (var ix in intersections)
                {
                    ix.IntersectionAngle = null;
                    foreach (var ap in ix.Approaches)
                        ap.BearingDegrees = 0;
                }
            }

            var result = new FindIntersectionsResult
            {
                IntersectionCount = intersections.Count,
                Intersections = intersections,
                FilteredByAlignment = alignmentNameFilter
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }
    }

    #region Result Models

    public class FindIntersectionsResult
    {
        public int IntersectionCount { get; set; }
        public List<IntersectionSummary> Intersections { get; set; } = new();
        public string? FilteredByAlignment { get; set; }
    }

    #endregion
}
