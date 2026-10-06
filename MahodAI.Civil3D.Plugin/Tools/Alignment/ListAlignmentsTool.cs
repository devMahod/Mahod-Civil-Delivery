using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Lists all alignments with summary information.
    /// </summary>
    public class ListAlignmentsTool : DrawingToolBase
    {
        public override string Name => "list_alignments";
        public override string Description => "Lists all alignments in the drawing with their names, lengths, station ranges, and associated profiles.";
        public override string Category => ToolCategories.Alignment;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            var filter = GetStringParam(parameters, "filter");
            var limit = GetIntParam(parameters, "limit") ?? 100;
            var includeProfiles = GetBoolParam(parameters, "include_profiles", true);

            var alignments = new List<AlignmentSummary>();

            foreach (ObjectId id in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
            {
                ct.ThrowIfCancellationRequested();
                if (alignments.Count >= limit) break;

                var alignment = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                if (alignment == null) continue;

                // Apply filter if specified
                if (!string.IsNullOrEmpty(filter) &&
                    !alignment.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var summary = new AlignmentSummary
                {
                    Name = alignment.Name,
                    Description = alignment.Description,
                    Length = alignment.Length,
                    StartStation = alignment.StartingStation,
                    EndStation = alignment.EndingStation,
                    Style = GetStyleName(tr, alignment.StyleId),
                    // Project station interval (פיקטים spacing) read from the drawing so the
                    // agent's report tables can show classic N+offset stations without
                    // hardcoding 20/25/100. 0 when the property is unavailable.
                    StationIndexIncrement = GetStationIncrement(alignment)
                };

                // Get associated profiles
                if (includeProfiles)
                {
                    summary.Profiles = new List<string>();
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        var profile = tr.GetObject(profileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                        if (profile != null)
                        {
                            summary.Profiles.Add(profile.Name);
                        }
                    }
                }

                alignments.Add(summary);
            }

            var result = new ListAlignmentsResult
            {
                Alignments = alignments,
                TotalCount = alignments.Count
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// Reads the alignment's station interval (the spacing of station marks /
        /// פיקטים — 20 m roads, 25 m railways, 100 m other). Uses reflection so the
        /// build is tolerant of the exact property name across Civil 3D versions;
        /// returns 0 when no usable value is found (the agent then falls back).
        /// </summary>
        private static double GetStationIncrement(Autodesk.Civil.DatabaseServices.Alignment alignment)
        {
            foreach (var prop in new[] { "StationIndexIncrement", "StationIncrement" })
            {
                try
                {
                    var p = alignment.GetType().GetProperty(prop);
                    if (p?.GetValue(alignment) is double d && d > 0)
                        return d;
                }
                catch { /* property absent on this version — try the next */ }
            }
            return 0.0;
        }

        private string GetStyleName(Transaction tr, ObjectId styleId)
        {
            try
            {
                if (styleId.IsNull) return "Default";
                var style = tr.GetObject(styleId, OpenMode.ForRead);
                // Try Name, then DisplayName as fallback
                var nameProperty = style.GetType().GetProperty("Name");
                var name = nameProperty?.GetValue(style)?.ToString();
                if (!string.IsNullOrEmpty(name)) return name;

                var displayProperty = style.GetType().GetProperty("DisplayName");
                name = displayProperty?.GetValue(style)?.ToString();
                if (!string.IsNullOrEmpty(name)) return name;

                return "Default";
            }
            catch
            {
                return "Default";
            }
        }
    }

    #region Result Models

    public class ListAlignmentsResult
    {
        public List<AlignmentSummary> Alignments { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class AlignmentSummary
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public double Length { get; set; }
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public string Style { get; set; } = string.Empty;
        /// <summary>Station interval in metres read from the drawing (0 = unknown).</summary>
        public double StationIndexIncrement { get; set; }
        public List<string>? Profiles { get; set; }
    }

    #endregion
}
