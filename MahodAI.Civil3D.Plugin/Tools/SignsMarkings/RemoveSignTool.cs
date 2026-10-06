using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.SignsMarkings
{
    /// <summary>
    /// Removes a road sign block reference from the drawing.
    /// </summary>
    public class RemoveSignTool : DrawingToolBase
    {
        public override string Name => "remove_sign";
        public override string Description => "Removes a road sign from the drawing. Finds and erases the sign block reference by sign code and optional alignment/station filter.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var signCode = GetRequiredStringParam(parameters, "sign_code");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var station = GetDoubleParam(parameters, "station");
            var stationTolerance = GetDoubleParam(parameters, "station_tolerance") ?? 5.0;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var db = HostApplicationServices.WorkingDatabase;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            // Get alignments for station calculation
            var alignments = new List<CivilDb.Alignment>();
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment a)
                    alignments.Add(a);
            }

            int removedCount = 0;
            var removedSigns = new List<object>();

            foreach (ObjectId id in ms)
            {
                ct.ThrowIfCancellationRequested();

                if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference blkRef))
                    continue;

                // Check if this is the target sign
                string blockName = blkRef.Name ?? "";
                if (!blockName.Contains(signCode, StringComparison.OrdinalIgnoreCase) &&
                    !IsSignWithCode(tr, blkRef, signCode))
                    continue;

                // Check alignment/station filter
                if (alignmentName != null && station.HasValue)
                {
                    bool matchesFilter = false;
                    foreach (var alignment in alignments)
                    {
                        if (!alignment.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            double sta = 0, off = 0;
                            alignment.StationOffset(blkRef.Position.X, blkRef.Position.Y, ref sta, ref off);
                            if (Math.Abs(sta - station.Value) <= stationTolerance && Math.Abs(off) < 50)
                            {
                                matchesFilter = true;
                            }
                        }
                        catch { }
                    }

                    if (!matchesFilter)
                        continue;
                }

                // Erase the sign
                var blkRefWrite = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                if (blkRefWrite != null)
                {
                    removedSigns.Add(new
                    {
                        block_name = blockName,
                        x = Math.Round(blkRef.Position.X, 3),
                        y = Math.Round(blkRef.Position.Y, 3),
                        layer = blkRef.Layer
                    });
                    blkRefWrite.Erase();
                    removedCount++;
                }
            }

            if (removedCount == 0)
                return ToolResult.Fail(ToolErrorCodes.ObjectNotFound,
                    $"No sign with code '{signCode}' found matching the specified criteria");

            cache.RemoveByPattern("list_signs:");
            cache.RemoveByPattern("validate_signs:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                sign_code = signCode,
                removed_count = removedCount,
                removed_signs = removedSigns
            }));
        }

        private bool IsSignWithCode(Transaction tr, BlockReference blkRef, string signCode)
        {
            try
            {
                if (blkRef.AttributeCollection != null)
                {
                    foreach (ObjectId attrId in blkRef.AttributeCollection)
                    {
                        if (tr.GetObject(attrId, OpenMode.ForRead) is AttributeReference attr)
                        {
                            string tag = attr.Tag?.ToUpperInvariant() ?? "";
                            if ((tag.Contains("CODE") || tag.Contains("קוד") || tag.Contains("NUM")) &&
                                attr.TextString == signCode)
                                return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
