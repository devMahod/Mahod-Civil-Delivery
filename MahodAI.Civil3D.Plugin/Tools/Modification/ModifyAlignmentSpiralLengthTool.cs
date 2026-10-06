using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Modifies the spiral transition length in an alignment SCS element.
    /// </summary>
    public class ModifyAlignmentSpiralLengthTool : DrawingToolBase
    {
        public override string Name => "modify_alignment_spiral_length";
        public override string Description => "Changes the spiral transition length on an alignment SCS (Spiral-Curve-Spiral) element. Can modify the entry or exit spiral.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var elementIndex = GetIntParam(parameters, "element_index");
            var newLength = GetDoubleParam(parameters, "new_length");

            if (elementIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'element_index' is missing");
            if (newLength == null || newLength.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_length' must be a positive number");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            int idx = elementIndex.Value;
            if (idx < 0 || idx >= alignment.Entities.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, $"Element index {idx} out of range (0-{alignment.Entities.Count - 1})");

            var entity = alignment.Entities[idx];

            if (entity.EntityType != CivilDb.AlignmentEntityType.SpiralCurveSpiral)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element at index {idx} is {entity.EntityType}, not a SpiralCurveSpiral");

            var scs = entity as CivilDb.AlignmentSCS;
            if (scs == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentSCS");

            // AlignmentSpiral.Length is read-only in the Civil 3D 2026 API —
            // the spiral is derived from the SCS anchor points and radius.
            // Surface a specific error rather than the generic "internal tool
            // error" that resulted from the old silent assignment attempt.
            await Task.CompletedTask;
            return ToolResult.Fail(
                ToolErrorCodes.ExecutionFailed,
                "Spiral length is not directly writable in the Civil 3D API. " +
                "Fix this violation by editing the SCS parameters (entry/exit points and " +
                "radius) in the alignment layout editor.");
        }
    }
}
