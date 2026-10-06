using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Inserts a horizontal curve at a PI (break point) between two adjacent
    /// tangent entities on an alignment. Paired with the analyze step's
    /// "PI without curve" (param="curve_missing") violation type.
    /// </summary>
    public class InsertAlignmentCurveTool : DrawingToolBase
    {
        private const double StationTolerance = 0.5; // meters — same tolerance as analyze-side PI detection

        public override string Name => "insert_alignment_curve";
        public override string Description => "Inserts a horizontal curve at a PI (break point) between two adjacent tangents of an alignment.";
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
            var station = GetDoubleParam(parameters, "station");
            var radius = GetDoubleParam(parameters, "radius");

            if (station == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'station' is missing");
            if (radius == null || radius.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'radius' must be a positive number");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForWrite) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open alignment for write");

            // Locate the two adjacent Line entities whose shared PI sits at ``station``
            // within ±StationTolerance meters. The first line's EndStation must
            // equal the second line's StartStation (continuous PI — no curve yet).
            CivilDb.AlignmentLine? beforeLine = null;
            CivilDb.AlignmentLine? afterLine = null;
            int beforeIndex = -1;
            int afterIndex = -1;

            var entities = alignment.Entities;
            for (int i = 0; i < entities.Count - 1; i++)
            {
                var curr = entities[i];
                var next = entities[i + 1];
                if (curr.EntityType != CivilDb.AlignmentEntityType.Line) continue;
                if (next.EntityType != CivilDb.AlignmentEntityType.Line) continue;

                var currLine = curr as CivilDb.AlignmentLine;
                var nextLine = next as CivilDb.AlignmentLine;
                if (currLine == null || nextLine == null) continue;

                double currEnd = currLine.EndStation;
                double nextStart = nextLine.StartStation;
                if (Math.Abs(currEnd - nextStart) > StationTolerance) continue; // something between them
                if (Math.Abs(currEnd - station.Value) > StationTolerance) continue;

                beforeLine = currLine;
                afterLine = nextLine;
                beforeIndex = i;
                afterIndex = i + 1;
                break;
            }

            if (beforeLine == null || afterLine == null)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"No pair of adjacent tangents found around station {station.Value:F3} " +
                    $"on alignment '{alignmentName}' (tolerance {StationTolerance} m). " +
                    "The PI may already have a curve, or the station does not match a break point.");
            }

            int countBefore = entities.Count;

            // The `AlignmentEntityCollection` exposes several `AddFreeCurve`
            // overloads that vary between Civil 3D releases. Resolve the right
            // one at runtime via reflection — that keeps the tool insulated
            // from API drift and surfaces a clear error message if the SDK
            // doesn't provide a compatible overload instead of failing to
            // compile against a future version.
            object? addedCurve;
            try
            {
                addedCurve = InvokeAddFreeCurve(entities, beforeLine.EntityId, afterLine.EntityId, radius.Value);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Civil 3D rejected curve insertion at station {station.Value:F3} with radius {radius.Value}: " +
                    $"{tie.InnerException.Message}. The radius may be too large for the tangent deflection angle.");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed,
                    $"Curve insertion failed on alignment '{alignmentName}': {ex.Message}.");
            }

            if (addedCurve == null)
            {
                return ToolResult.Fail(
                    ToolErrorCodes.NotSupported,
                    "The Civil 3D managed API on this machine does not expose a compatible " +
                    "AddFreeCurve overload on AlignmentEntityCollection. Insert the curve " +
                    "manually in Civil 3D (Alignment Editor → Add Fixed/Free Curve).");
            }

            int countAfter = entities.Count;
            double newLength = 0;
            try { newLength = Convert.ToDouble(addedCurve.GetType().GetProperty("Length")?.GetValue(addedCurve) ?? 0); } catch { }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("validate_alignment:");

            return await Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                station = Math.Round(station.Value, 3),
                radius = Math.Round(radius.Value, 3),
                tangent_before_index = beforeIndex,
                tangent_after_index = afterIndex,
                entity_count_before = countBefore,
                entity_count_after = countAfter,
                new_curve_length = Math.Round(newLength, 3)
            }));
        }

        /// <summary>
        /// Resolve and invoke <c>AddFreeCurve</c> on the alignment-entities
        /// collection. Different Civil 3D releases ship different overloads
        /// (both ObjectId- and int-keyed); resolve at runtime to stay
        /// insulated from API drift. Returns null when no compatible overload
        /// is found.
        /// </summary>
        private static object? InvokeAddFreeCurve(
            CivilDb.AlignmentEntityCollection entities,
            int beforeEntityId,
            int afterEntityId,
            double radius)
        {
            int idArgsBound = 0;

            var type = entities.GetType();
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(method.Name, "AddFreeCurve", StringComparison.Ordinal)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length < 3 || parameters.Length > 6) continue;

                // Build args matching this overload's parameter list. The 3-arg
                // core is (int/ObjectId, int/ObjectId, double). Optional bool /
                // enum parameters get sensible defaults (greaterThan180 = false,
                // direction enums default to their zero value).
                var args = new object?[parameters.Length];
                bool ok = true;
                idArgsBound = 0;
                for (int p = 0; p < parameters.Length; p++)
                {
                    var pt = parameters[p].ParameterType;
                    if (pt == typeof(int))
                    {
                        args[p] = (idArgsBound++ == 0) ? beforeEntityId : afterEntityId;
                    }
                    else if (pt == typeof(ObjectId))
                    {
                        // Some overloads take ObjectId; we can't convert
                        // reliably without a DB lookup — skip those.
                        ok = false;
                        break;
                    }
                    else if (pt == typeof(double))
                    {
                        args[p] = radius;
                    }
                    else if (pt == typeof(bool))
                    {
                        args[p] = false;
                    }
                    else if (pt.IsEnum)
                    {
                        args[p] = Activator.CreateInstance(pt);
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;
                if (idArgsBound < 2) continue;
                return method.Invoke(entities, args);
            }
            return null;
        }
    }
}
