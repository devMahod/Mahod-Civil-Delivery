using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Utilities;
using MahodAI.Civil3D.Plugin.Utilities.CurveFitting;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilDb = Autodesk.Civil.DatabaseServices;
using CivilSpiralType = Autodesk.Civil.SpiralType;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Reconstructs raw / imported centerline geometry as clean Line + Arc entities on a
    /// Civil 3D Alignment. Accepts four input modes, in this resolution order:
    ///
    ///  1. <c>target_name</c> matches an existing Alignment → rebuilt in place (sub-entities
    ///     removed and replaced; profiles and corridors that reference it are preserved).
    ///  2. <c>target_name</c> matches a polyline handle → a new Alignment is fitted from it.
    ///  3. <c>layer_name</c> provided → all <c>Line</c> entities on that layer are chained
    ///     end-to-end and a new Alignment is fitted from them.
    ///  4. Neither provided → the tool reads the active document's PickFirst selection
    ///     (whatever the engineer had highlighted when they invoked the chat). This is the
    ///     natural workflow: select 100+ short Lines that approximate a centerline, ask the
    ///     agent to fix them.
    /// </summary>
    public class FixAlignmentGeometryTool : DrawingToolBase
    {
        public override string Name => "fix_alignment_geometry";

        public override string Description =>
            "Rebuilds raw / imported centerline geometry as a clean Spiral-Curve-Spiral Alignment. " +
            "Resolves the source in this strict order: (1) the engineer's active selection in the " +
            "drawing — ALWAYS WINS when present, do not pass any params if the user just said 'fix this'; " +
            "(2) layer_name — all Line / Arc entities on that layer are chained; " +
            "(3) target_name — only as a last resort, treats it as an Alignment name OR polyline handle. " +
            "DO NOT guess an existing-Alignment name from the drawing summary — that is almost never " +
            "what the user wants. " +
            "When the source contains Arc entities (typical of an exploded Civil 3D alignment), their " +
            "explicit radii are read from the drawing and used for the rebuilt SCS curves so the result " +
            "matches the engineer's original design. When no Arc entities are present (pure-Line surveys, " +
            "GIS imports, foreign CAD exports), curve radii are fitted from the densified point cloud.";

        public override string Category => ToolCategories.Modification;

        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""target_name"":                  { ""type"": ""string"", ""description"": ""Optional. Alignment name OR polyline handle (hex from Properties)."" },
                ""layer_name"":                   { ""type"": ""string"", ""description"": ""Optional. Layer whose Line / Arc entities form a chained centerline (e.g. '0-bou'). All Line+Arc entities on the layer are gathered and chained end-to-end."" },
                ""sampling_step_m"":              { ""type"": ""number"", ""description"": ""Densification step for source points, in metres. Default 1.0."" },
                ""min_arc_radius_m"":             { ""type"": ""number"", ""description"": ""Curves below this radius are still fit but flagged as poor. Default 30."" },
                ""tangent_curvature_threshold"":  { ""type"": ""number"", ""description"": ""Curvature (1/m) below which a sample is classified as tangent. Default 2e-4 (≈ R > 5000 m)."" },
                ""new_alignment_name"":           { ""type"": ""string"", ""description"": ""Name for the created Alignment when the source is a polyline / lines / selection. Default 'FixedAlignment_<n>'."" },
                ""erase_source"":                 { ""type"": ""boolean"", ""description"": ""When the source is a set of Line / Arc entities or a polyline, erase those originals after the new Alignment is built. Default true."" },
                ""use_spirals"":                  { ""type"": ""boolean"", ""description"": ""Insert clothoid Spiral-Curve-Spiral (SCS) compound entities at each PI instead of plain Arc entities — matches engineer-built alignments. Default true."" },
                ""spiral_length_m"":              { ""type"": ""number"", ""description"": ""Length (m) of each transition spiral when use_spirals=true. Default 50.0 (Israeli interurban convention)."" }
            },
            ""required"": []
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            string? targetName = GetStringParam(parameters, "target_name");
            string? layerName = GetStringParam(parameters, "layer_name");
            double samplingStep = GetDoubleParam(parameters, "sampling_step_m") ?? 1.0;
            double minRadius = GetDoubleParam(parameters, "min_arc_radius_m") ?? 30.0;
            double tangentThreshold = GetDoubleParam(parameters, "tangent_curvature_threshold") ?? 2e-4;
            string? newAlignmentName = GetStringParam(parameters, "new_alignment_name");
            bool eraseSource = GetBoolParam(parameters, "erase_source", defaultValue: true);
            bool useSpirals = GetBoolParam(parameters, "use_spirals", defaultValue: true);
            double spiralLength = GetDoubleParam(parameters, "spiral_length_m") ?? 50.0;

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // ── Resolve the source: active selection → layer → name ─────────────────
            // Selection wins: if the engineer pre-selected entities in the drawing,
            // that's always what they want fixed — never an existing alignment looked
            // up by name from the drawing summary.
            AlignmentSource.Result? source = TryResolveFromActiveSelection(tr, samplingStep);
            if (source == null && !string.IsNullOrWhiteSpace(layerName))
                source = AlignmentSource.SampleFromLayer(tr, layerName, samplingStep);
            if (source == null && !string.IsNullOrWhiteSpace(targetName))
                source = AlignmentSource.Sample(tr, civilDoc, targetName, samplingStep);

            if (source == null || source.Points.Length < 3)
            {
                string detail = !string.IsNullOrWhiteSpace(targetName) ? $"target_name='{targetName}'"
                              : !string.IsNullOrWhiteSpace(layerName)  ? $"layer_name='{layerName}'"
                              : "no target_name / layer_name provided and no active selection";
                return ToolResult.NotFound("Alignment / polyline / line chain", detail);
            }

            // ── Run the fit pipeline ────────────────────────────────────────────────
            var plan = AlignmentRebuilder.Build(source.Points, new AlignmentRebuilder.Options
            {
                SamplingStepM = samplingStep,
                TangentCurvatureThreshold = tangentThreshold,
                MinArcRadiusM = minRadius,
            });
            if (plan.FailureReason != null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, plan.FailureReason);
            if (plan.Pis.Length < 2)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Fit produced fewer than 2 PIs.");

            ct.ThrowIfCancellationRequested();

            // ── Override fitted radii with source Arc.Radius where available ────────
            // CircleFit on densified curve-region points is unreliable when the source's
            // curve regions include exploded spiral approximations whose curvature varies
            // smoothly (an exploded SCS curve looks like a soft S, not a circle). When the
            // source carries real Arc entities — typical of an exploded Civil 3D alignment
            // where the central arc of each SCS survives as a single Arc — read each PI's
            // radius directly from the matching source arc instead of from the fitted
            // circle. Match each interior PI to its nearest unused source Arc by center
            // distance; arcs and PIs are nearly 1-to-1 for the exploded-alignment case.
            double[] interiorRadii = (double[])plan.InteriorRadii.Clone();
            var radiusOrigins = new string[interiorRadii.Length];
            for (int i = 0; i < radiusOrigins.Length; i++) radiusOrigins[i] = "fitted";
            int sourceArcOverrides = 0;
            if (source.ArcCount > 0 && interiorRadii.Length > 0)
            {
                var arcSegments = source.Segments
                    .Where(s => s.Kind == AlignmentSource.SegmentKind.Arc && s.ArcRadius > 0)
                    .ToList();
                var consumed = new bool[arcSegments.Count];
                for (int i = 0; i < interiorRadii.Length; i++)
                {
                    var pi = plan.Pis[i + 1];
                    int bestIdx = -1;
                    double bestDist = double.PositiveInfinity;
                    for (int j = 0; j < arcSegments.Count; j++)
                    {
                        if (consumed[j]) continue;
                        var c = arcSegments[j].ArcCenter;
                        double dx = c.X - pi.X;
                        double dy = c.Y - pi.Y;
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (d < bestDist) { bestDist = d; bestIdx = j; }
                    }
                    if (bestIdx >= 0)
                    {
                        consumed[bestIdx] = true;
                        interiorRadii[i] = arcSegments[bestIdx].ArcRadius;
                        radiusOrigins[i] = "source_arc";
                        sourceArcOverrides++;
                    }
                }
            }

            // ── Resolve / create the destination alignment ──────────────────────────
            var resolveErr = ResolveOrCreateDestinationAlignment(
                tr, civilDoc, source, newAlignmentName,
                out CivilDb.Alignment alignment, out string finalAlignmentName);
            if (resolveErr != null) return resolveErr;

            // ── Build geometry: fixed-line tangents, then free-curves at each interior PI ──
            var lineEntityIds = new List<int>();
            for (int i = 0; i < plan.Pis.Length - 1; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = plan.Pis[i];
                var e = plan.Pis[i + 1];
                try
                {
                    var ln = alignment.Entities.AddFixedLine(
                        new Point3d(s.X, s.Y, 0),
                        new Point3d(e.X, e.Y, 0));
                    lineEntityIds.Add((int)ln.EntityId);
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"AddFixedLine failed for tangent {i}: {ex.Message}", ex.ToString());
                }
            }

            // Per interior PI: try Spiral-Curve-Spiral first (matches engineer-built alignments),
            // fall back to plain Arc if Civil 3D rejects (typical reason: tangent too short to
            // host spiral-in + arc + spiral-out at the requested radius). The radius-relax loop
            // halves the radius down to minRadius/2 if all attempts fail at the requested size.
            var curveWarnings = new List<object>();
            int placedCurves = 0;
            int placedScs = 0;
            for (int piIndex = 0; piIndex < interiorRadii.Length; piIndex++)
            {
                ct.ThrowIfCancellationRequested();
                double r = interiorRadii[piIndex];
                if (r <= 0) continue;

                int prevLineId = lineEntityIds[piIndex];
                int nextLineId = lineEntityIds[piIndex + 1];
                double tryR = r;
                bool placed = false;
                string? lastScsError = null;

                while (tryR >= Math.Max(1, minRadius / 2) && !placed)
                {
                    // 1. Try AddFreeSCS — single call that builds spiral-in + arc + spiral-out
                    //    as one AlignmentSCS compound entity (matches `Spiral-Curve-Spiral` rows
                    //    in the Alignment Entities panel).
                    if (useSpirals && spiralLength > 0)
                    {
                        try
                        {
                            alignment.Entities.AddFreeSCS(
                                prevLineId, nextLineId,
                                spiralLength,                            // spiral1Param (entry length)
                                spiralLength,                            // spiral2Param (exit length)
                                CivilDb.SpiralParamType.Length,          // spType
                                tryR,                                    // central arc radius
                                isGreaterThan180: false,
                                CivilSpiralType.Clothoid);
                            placed = true;
                            placedCurves++;
                            placedScs++;
                            if (Math.Abs(tryR - r) > 0.01)
                            {
                                curveWarnings.Add(new
                                {
                                    pi_index = piIndex + 1,
                                    placed_as = "SCS",
                                    requested_r_m = Math.Round(r, 1),
                                    achieved_r_m = Math.Round(tryR, 1),
                                    reason = "SCS placed — radius relaxed for tangent length",
                                });
                            }
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastScsError = ex.Message;
                        }
                    }

                    // 2. Plain Arc fallback (no spirals).
                    try
                    {
                        alignment.Entities.AddFreeCurve(
                            prevLineId, nextLineId, tryR,
                            CivilDb.CurveParamType.Radius,
                            isGreaterThan180: false,
                            CivilDb.CurveType.Compound);
                        placed = true;
                        placedCurves++;
                        if (useSpirals)
                        {
                            curveWarnings.Add(new
                            {
                                pi_index = piIndex + 1,
                                placed_as = "Arc",
                                requested_r_m = Math.Round(r, 1),
                                achieved_r_m = Math.Round(tryR, 1),
                                reason = $"SCS rejected — tangent too short for spiral_length={spiralLength:F0}m at radius {tryR:F0}m. Civil 3D: {lastScsError ?? "no detail"}",
                            });
                        }
                        else if (Math.Abs(tryR - r) > 0.01)
                        {
                            curveWarnings.Add(new
                            {
                                pi_index = piIndex + 1,
                                placed_as = "Arc",
                                requested_r_m = Math.Round(r, 1),
                                achieved_r_m = Math.Round(tryR, 1),
                                reason = "tangent length insufficient — radius relaxed",
                            });
                        }
                    }
                    catch
                    {
                        tryR *= 0.5;
                    }
                }
                if (!placed)
                {
                    curveWarnings.Add(new
                    {
                        pi_index = piIndex + 1,
                        placed_as = "none",
                        requested_r_m = Math.Round(r, 1),
                        achieved_r_m = 0.0,
                        reason = $"No curve placed at radii ≥ {minRadius / 2:F0} m — PI left as sharp vertex.",
                    });
                }
            }

            // ── Erase source entities (lines / polyline) if requested ───────────────
            int erasedCount = 0;
            if (eraseSource && (source.SourceKind == "lines" || source.SourceKind == "polyline"))
            {
                erasedCount = EraseSourceEntities(tr, source);
            }

            // ── Cache invalidation ────────────────────────────────────────────────
            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("list_alignments:");
            cache.RemoveByPattern("get_drawing_summary:");
            cache.RemoveByPattern("validate_alignment:");
            cache.RemoveByPattern("list_objects:");

            // ── Build result payload ──────────────────────────────────────────────
            var diagPayload = plan.Diagnostics.Select(d => new
            {
                kind = d.Kind.ToString().ToLowerInvariant(),
                start_arc_m = Math.Round(d.StartArcLengthM, 2),
                end_arc_m = Math.Round(d.EndArcLengthM, 2),
                length_m = Math.Round(d.EndArcLengthM - d.StartArcLengthM, 2),
                radius_m = d.RadiusM.HasValue ? (object)Math.Round(d.RadiusM.Value, 2) : null!,
                rms_residual_m = Math.Round(d.RmsResidualM, 4),
            }).ToList();

            return await Task.FromResult(ToolResult.Ok(new
            {
                success = true,
                alignment_name = finalAlignmentName,
                source_kind = source.SourceKind,
                source_name = source.ResolvedName,
                source_line_count = source.LineIds.Count,
                source_arc_count = source.ArcCount,
                source_arc_overrides = sourceArcOverrides,
                erased_source_count = erasedCount,
                pi_count = plan.Pis.Length,
                tangent_count = plan.Pis.Length - 1,
                curve_count = placedCurves,
                scs_count = placedScs,
                arc_count = placedCurves - placedScs,
                spiral_length_m = useSpirals ? spiralLength : 0,
                length_m = Math.Round(alignment.Length, 2),
                worst_rms_residual_m = Math.Round(plan.WorstRmsResidualM, 4),
                overall_quality = plan.OverallQuality,
                radius_origins = radiusOrigins,
                segments = diagPayload,
                curve_warnings = curveWarnings,
                message = BuildHebrewMessage(finalAlignmentName, alignment.Length, plan.Pis.Length - 1, placedCurves, plan.OverallQuality, source.SourceKind, source.LineIds.Count, erasedCount),
            }));
        }

        // ─── Source resolution helpers ─────────────────────────────────────────────

        /// <summary>
        /// Reads the active document's PickFirst selection (whatever the engineer had
        /// highlighted in Civil 3D when they invoked the chat). Returns null if there's
        /// no document or nothing was pre-selected.
        /// </summary>
        private static AlignmentSource.Result? TryResolveFromActiveSelection(Transaction tr, double samplingStep)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return null;

            PromptSelectionResult selRes;
            try { selRes = doc.Editor.SelectImplied(); }
            catch { return null; }
            if (selRes.Status != PromptStatus.OK || selRes.Value == null) return null;

            var ids = selRes.Value.GetObjectIds();
            if (ids == null || ids.Length == 0) return null;

            return AlignmentSource.SampleFromEntityIds(tr, ids, samplingStep);
        }

        /// <summary>
        /// Picks alignment style + label-set style for a newly-created alignment. Strategy:
        /// (1) reuse the styles of an existing alignment in the drawing — guarantees the
        /// engineer's preferred styling. (2) fall back to a style whose name contains
        /// "Standard", "Layout", "Centerline", or "Alignment" (in that order). (3) only as a
        /// last resort, take the first style in the collection — which on some drawings is a
        /// "Parcel Style" inherited from a template, and renders alignments incorrectly.
        /// </summary>
        private static void ResolveAlignmentStyles(
            Transaction tr, CivilDocument civilDoc, ref ObjectId styleId, ref ObjectId labelSetId)
        {
            // (1) Borrow from an existing alignment.
            try
            {
                foreach (var id in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
                {
                    var existing = tr.GetObject(id, OpenMode.ForRead) as CivilDb.Alignment;
                    if (existing == null) continue;
                    if (styleId.IsNull) styleId = existing.StyleId;
                    try
                    {
                        var labelProp = existing.GetType().GetProperty("LabelSetStyleId")
                                       ?? existing.GetType().GetProperty("AlignmentLabelSetStyleId");
                        if (labelProp != null && labelSetId.IsNull)
                        {
                            var v = labelProp.GetValue(existing);
                            if (v is ObjectId oid && !oid.IsNull) labelSetId = oid;
                        }
                    }
                    catch { }
                    if (!styleId.IsNull) break;
                }
            }
            catch { }

            // (2) Fall back to preferred names.
            if (styleId.IsNull)
            {
                styleId = FindStyleByPreferredName(
                    tr, civilDoc.Styles.AlignmentStyles,
                    new[] { "Standard", "Layout", "Centerline", "Alignment" });
            }
            if (labelSetId.IsNull)
            {
                try
                {
                    labelSetId = FindStyleByPreferredName(
                        tr, civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles,
                        new[] { "Standard", "Layout", "Major", "Stations" });
                }
                catch { }
            }

            // (3) Last resort: first available.
            if (styleId.IsNull)
            {
                try { foreach (ObjectId id in civilDoc.Styles.AlignmentStyles) { styleId = id; break; } }
                catch { }
            }
            if (labelSetId.IsNull)
            {
                try { foreach (ObjectId id in civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles) { labelSetId = id; break; } }
                catch { }
            }
        }

        private static ObjectId FindStyleByPreferredName(
            Transaction tr, System.Collections.IEnumerable styleCollection, string[] preferredNames)
        {
            // Build name → id map once.
            var byName = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ObjectId id in styleCollection)
                {
                    var s = tr.GetObject(id, OpenMode.ForRead);
                    var name = s?.GetType().GetProperty("Name")?.GetValue(s)?.ToString();
                    if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name)) byName[name] = id;
                }
            }
            catch { }

            // Exact match first.
            foreach (var pref in preferredNames)
                if (byName.TryGetValue(pref, out var idExact)) return idExact;

            // Then partial / contains match.
            foreach (var pref in preferredNames)
                foreach (var kvp in byName)
                    if (kvp.Key.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kvp.Value;

            return ObjectId.Null;
        }

        private static string GenerateDefaultAlignmentName(Transaction tr, CivilDocument civilDoc, AlignmentSource.Result source)
        {
            string baseName = source.SourceKind switch
            {
                "polyline" => $"Pline_{source.ResolvedName}_fixed",
                "lines"    => "FixedAlignment",
                _          => "FixedAlignment",
            };
            string candidate = baseName;
            int n = 1;
            while (CivilObjectFinder.FindAlignmentByName(tr, civilDoc, candidate) != null)
            {
                candidate = $"{baseName}_{n}";
                n++;
                if (n > 999) break;
            }
            return candidate;
        }

        private static int EraseSourceEntities(Transaction tr, AlignmentSource.Result source)
        {
            int erased = 0;
            var ids = new List<ObjectId>();
            ids.AddRange(source.LineIds);
            if (!source.PolylineId.IsNull) ids.Add(source.PolylineId);

            foreach (var id in ids)
            {
                try
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as AcadEntity;
                    if (ent != null && !ent.IsErased) { ent.Erase(); erased++; }
                }
                catch { /* keep going — not critical if a single source entity fails to erase */ }
            }
            return erased;
        }

        /// <summary>
        /// Removes every sub-entity from an alignment so it can be rebuilt. We collect
        /// entity references first because modifying the collection while iterating it
        /// invalidates the enumerator.
        /// </summary>
        private static void ClearAlignmentEntities(CivilDb.Alignment alignment)
        {
            var snapshot = new List<CivilDb.AlignmentEntity>();
            foreach (CivilDb.AlignmentEntity entity in alignment.Entities)
                snapshot.Add(entity);

            foreach (var entity in snapshot)
            {
                try { alignment.Entities.Remove(entity); }
                catch { /* already removed by a chained dependency — keep going */ }
            }
        }

        private static string BuildHebrewMessage(string name, double length, int tangents, int curves, string quality, string sourceKind, int lineCount, int erased)
        {
            string qualHeb = quality switch
            {
                "good" => "איכות התאמה טובה",
                "acceptable" => "איכות התאמה סבירה",
                _ => "איכות התאמה נמוכה — מומלץ לבדוק"
            };
            string sourceDesc = sourceKind switch
            {
                "alignment" => "מתוואי קיים",
                "polyline"  => "מפוליליין",
                "lines"     => $"מ-{lineCount} קווים",
                _           => "ממקור",
            };
            string erasedDesc = erased > 0 ? $" {erased} ישויות מקור נמחקו." : "";
            return $"ציר '{name}' שוקם {sourceDesc}: {tangents} קטעים ישרים, {curves} עקומות, אורך {length:F0} מ'.{erasedDesc} {qualHeb}.";
        }

        /// <summary>
        /// Resolves the destination alignment: either an existing alignment cleared in
        /// place (for the alignment-by-name rebuild flow) or a freshly created alignment
        /// with appropriate styles. Returns a non-null <see cref="ToolResult"/> only on
        /// failure.
        /// </summary>
        private static ToolResult? ResolveOrCreateDestinationAlignment(
            Transaction tr,
            CivilDocument civilDoc,
            AlignmentSource.Result source,
            string? newAlignmentName,
            out CivilDb.Alignment alignment,
            out string finalAlignmentName)
        {
            ObjectId styleId = ObjectId.Null;
            ObjectId labelSetId = ObjectId.Null;
            ObjectId layerId = HostApplicationServices.WorkingDatabase.Clayer;
            ResolveAlignmentStyles(tr, civilDoc, ref styleId, ref labelSetId);

            if (source.SourceKind == "alignment")
            {
                alignment = (CivilDb.Alignment)tr.GetObject(source.AlignmentId, OpenMode.ForWrite);
                finalAlignmentName = alignment.Name;
                ClearAlignmentEntities(alignment);
                return null;
            }

            string proposed = !string.IsNullOrWhiteSpace(newAlignmentName)
                ? newAlignmentName!
                : GenerateDefaultAlignmentName(tr, civilDoc, source);

            ObjectId newId;
            try
            {
                newId = CivilDb.Alignment.Create(
                    civilDoc, proposed, ObjectId.Null, layerId, styleId, labelSetId,
                    CivilDb.AlignmentType.Centerline);
            }
            catch (Exception ex)
            {
                alignment = null!;
                finalAlignmentName = proposed;
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Alignment.Create failed: {ex.Message}", ex.ToString());
            }
            alignment = (CivilDb.Alignment)tr.GetObject(newId, OpenMode.ForWrite);
            finalAlignmentName = alignment.Name;
            return null;
        }

    }
}
