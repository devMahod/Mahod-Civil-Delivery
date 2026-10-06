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
    /// Modifies the length of a tangent segment in an alignment.
    /// </summary>
    public class ModifyAlignmentTangentLengthTool : DrawingToolBase
    {
        public override string Name => "modify_alignment_tangent_length";
        public override string Description => "Modifies the length of a tangent (straight line) segment in an alignment by adjusting its end point.";
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

            if (entity.EntityType != CivilDb.AlignmentEntityType.Line)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"Element at index {idx} is {entity.EntityType}, not a Line (tangent)");

            var line = entity as CivilDb.AlignmentLine;
            if (line == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to cast entity to AlignmentLine");

            // AlignmentLine.Length and AlignmentLine.StartPoint/EndPoint are
            // read-only in the Civil 3D 2026 API — the geometry is derived
            // from the alignment's PI (point of intersection) array. Setting
            // them via reflection used to fail silently with a generic
            // "Exception has been thrown by the target of an invocation",
            // which surfaced in the chat as the meaningless "שגיאה פנימית
            // בכלי" cell. Return a specific, actionable error so the
            // engineer knows to edit the alignment's PIs manually.
            await Task.CompletedTask;
            return ToolResult.Fail(
                ToolErrorCodes.ExecutionFailed,
                "Tangent length is not directly writable in the Civil 3D API. " +
                "Fix this violation by moving the adjacent PI points in the alignment, " +
                "or by editing the alignment geometry manually in Civil 3D.");
        }
    }
}
