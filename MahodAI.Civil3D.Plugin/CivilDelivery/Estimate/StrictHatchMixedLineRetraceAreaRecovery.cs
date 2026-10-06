using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Quantity-only interpretation of an unchanged failed Area capture. Cancels only
/// originally adjacent exact reversed LINE pairs in a mixed loop; never snaps, joins, reorders,
/// repairs a DWG, supplies a section boundary, or interprets multiple loops.
/// This first candidate is deliberately restricted to proven direct-host inputs.
/// </summary>
internal static class StrictHatchMixedLineRetraceAreaRecovery
{
    internal const string Method = "hatch-exact-retrace-mixed-line-area";
    internal const string Contract = "single-mixed-original-line-neighbours-exact-retrace-area-v1";

    internal sealed record CancelledPair(int FirstOriginalIndex, int SecondOriginalIndex,
        string FirstOriginalEdge, string SecondOriginalEdge);
    internal sealed record Proof(string OriginalSnapshotJson, string OriginalSnapshotSha256,
        string SourceIdentityJson, string SourceIdentitySha256, string ContextJson,
        string InputReceiptSha256, string ResidualSnapshotSha256,
        StrictHatchCurveAreaRecovery.OriginalValidation OriginalValidation,
        IReadOnlyList<CancelledPair> CancelledPairs, IReadOnlyList<int> RetainedOriginalIndices);

    internal static bool TryRecover(HatchAreaFailureDiagnostic.Snapshot snapshot, ProvenanceRef source,
        StrictHatchExactRetraceAreaRecovery.DirectHostContext context, string originalAreaError, double[]? originalBounds,
        out QuantityMeasurement? measurement, out Proof? proof, out string refusal)
    {
        measurement = null; proof = null; refusal = "";
        if (source == null || context == null || snapshot == null)
            return Refuse("source, context and full snapshot are required", out refusal);
        if (string.IsNullOrWhiteSpace(originalAreaError))
            return Refuse("original Area failure is required", out refusal);
        if (!Sha(context.ScanReceiptSha256) || !Sha(context.DrawingSha256) ||
            string.IsNullOrWhiteSpace(context.DrawingPath) || string.IsNullOrWhiteSpace(context.RunId) ||
            context.DbMod != 0 || source.SourceKind != "drawing" || source.EntityType != "Hatch" ||
            source.MeasurementMethod != "hatch-area" || source.RunId != context.RunId ||
            !string.Equals(source.SourcePathOrUri, context.DrawingPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.DrawingChecksum, context.DrawingSha256, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(source.SourceHandle ?? "", "\\A[0-9A-Fa-f]+\\z") ||
            !string.IsNullOrEmpty(source.SourceSubentityPath) || !string.IsNullOrEmpty(source.XrefPath) ||
            source.XrefTransform != null || context.TransformContract != "direct-host-modelspace-identity" ||
            context.MaximumLinearScale != 1)
            return Refuse("direct-host source/run/saved-identity/transform evidence is incomplete or mismatched", out refusal);
        var unit = context.Units;
        if (unit == null || !unit.IsSupported || unit.UsedDeclaration || unit.DeclarationDigest != null ||
            unit.FailureCode != null || unit.FailureReason != null)
            return Refuse("explicit source units are unproved", out refusal);
        var expectedUnits = PhysicalDrawingUnitPolicy.Resolve(unit.RawUnitCode, null, null,
            Array.Empty<ProjectProfile.DrawingUnitDeclaration>(), isHostDrawing: false);
        if (!expectedUnits.IsSupported || unit.EffectiveUnitCode != expectedUnits.EffectiveUnitCode ||
            unit.LinearToMetres != expectedUnits.LinearToMetres || unit.Evidence != expectedUnits.Evidence)
            return Refuse("unit evidence does not match the existing explicit-unit policy", out refusal);
        if (snapshot.Error != null || snapshot.Truncated || snapshot.DeclaredLoops != 1 ||
            snapshot.Loops == null || snapshot.Loops.Count != 1 ||
            snapshot.CoordinateSystem != "raw hatch OCS / source drawing units; not host WCS or square metres")
            return Refuse("one complete original source-OCS loop is required", out refusal);
        var originalLoop = snapshot.Loops[0];
        if (originalLoop == null) return Refuse("original loop receipt is missing", out refusal);
        var evidence = originalLoop.Evidence;
        if (originalLoop.Index != 0 || originalLoop.Error != null || evidence == null ||
            evidence.Truncated || evidence.Kind != "edge-list" || evidence.Items == null ||
            evidence.Items.Count < 4 || evidence.Items.Count > HatchAreaFailureDiagnostic.MaximumItems)
            return Refuse("complete bounded mixed edge loop with a removable pair is required", out refusal);
        // Freeze once BEFORE parsing/preflight/removal. Every subsequent stage uses
        // the same private original strings; a caller cannot replace list entries.
        var originalItems = evidence.Items.ToArray();
        if (originalItems.Any(item => item == null))
            return Refuse("original edge item is missing", out refusal);
        var original = snapshot with { Loops = Array.AsReadOnly(new[] {
            originalLoop with { Evidence = evidence with { Items = Array.AsReadOnly(originalItems) } } }) };
        var scale = new DrawingUnitPolicy.Scale(true, unit.LinearToMetres,
            "INSUNITS=" + unit.RawUnitCode.ToString(CultureInfo.InvariantCulture), "מטר", "מ\"ר", "מ\"ק");
        if (!StrictHatchCurveAreaRecoveryAdapter.TryParseOriginalInputs(original, scale, 1,
                out var inputs, out _, out var parseRefusal))
            return Refuse("original mixed input refused: " + parseRefusal, out refusal);
        if (inputs!.Count != 1)
            return Refuse("one parsed original mixed loop is required", out refusal);
        if (!StrictHatchCurveAreaRecovery.TryValidateOriginalGeometry(
                inputs[0], out var originalValidation, out var preflightRefusal))
            return Refuse("original mixed preflight refused: " + preflightRefusal, out refusal);
        var edges = inputs[0].Edges;
        var removed = new bool[edges.Count];
        var pairs = new List<CancelledPair>();
        // Original neighbours only, deterministic reverse scan. Never cancel a
        // curve, first/last cyclic pair, or a pair formed by an earlier removal.
        for (var i = edges.Count - 1; i > 0; i--)
        {
            if (edges[i - 1] is not StrictHatchCurveAreaRecovery.LineEdge first ||
                edges[i] is not StrictHatchCurveAreaRecovery.LineEdge second ||
                first.Start != second.End || first.End != second.Start) continue;
            // v1 only removes an excursion between exact ORIGINAL LINE joins.
            // No composition of two ULP joins into a new uncertain splice.
            var before = edges[(i - 2 + edges.Count) % edges.Count];
            var after = edges[(i + 1) % edges.Count];
            if (before is not StrictHatchCurveAreaRecovery.LineEdge ||
                after is not StrictHatchCurveAreaRecovery.LineEdge ||
                before.End != first.Start || second.End != after.Start)
                return Refuse("exact reverse pair has no exact original LINE splice neighbours", out refusal);
            removed[i - 1] = removed[i] = true;
            pairs.Add(new(i - 1, i, originalItems[i - 1], originalItems[i]));
            i--;
        }
        if (pairs.Count == 0) return Refuse("no originally adjacent exact reverse LINE pair", out refusal);
        var retained = Enumerable.Range(0, edges.Count).Where(index => !removed[index]).ToArray();
        if (retained.Length < 2) return Refuse("residual has fewer than two original edges", out refusal);
        var residual = original with { Loops = Array.AsReadOnly(new[] {
            original.Loops[0] with { Evidence = original.Loops[0].Evidence! with {
                Items = Array.AsReadOnly(retained.Select(index => originalItems[index]).ToArray()) } } }) };
        // Unchanged full reader validates every surviving arc/frame/join/topology
        // and the same area enclosure. This is not a section-boundary contract.
        if (!StrictHatchCurveAreaRecoveryAdapter.TryRecover(residual, scale, 1, originalAreaError,
                originalBounds, out var recovered, out var residualRefusal))
            return Refuse("residual mixed proof refused: " + residualRefusal, out refusal);
        var originalJson = JsonSerializer.Serialize(original);
        var originalSha = ArtifactHash.Sha256OfText(originalJson);
        var residualSha = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(residual));
        var contextJson = JsonSerializer.Serialize(context);
        var sourceJson = JsonSerializer.Serialize(source);
        pairs.Sort((a,b) => a.FirstOriginalIndex.CompareTo(b.FirstOriginalIndex));
        proof = new(originalJson, originalSha, sourceJson, ArtifactHash.Sha256OfText(sourceJson),
            contextJson, context.ScanReceiptSha256, residualSha, originalValidation!,
            pairs.AsReadOnly(), Array.AsReadOnly(retained));
        measurement = new QuantityMeasurement
        {
            Kind = recovered!.Kind, Method = Method, RawValue = recovered.RawValue,
            Unit = recovered.Unit, GeometryEvidence = recovered.GeometryEvidence,
        };
        foreach (var parameter in recovered.Parameters) measurement.Parameters.Add(parameter.Key, parameter.Value);
        measurement.Parameters["boundary_sha256"] = originalSha;
        measurement.Parameters["boundary_residual_contract"] = recovered.Parameters["boundary_contract"];
        measurement.Parameters["boundary_contract"] = Contract;
        measurement.Parameters["boundary_original_edge_count"] = edges.Count.ToString(CultureInfo.InvariantCulture);
        measurement.Parameters["boundary_residual_sha256"] = residualSha;
        measurement.Parameters["boundary_exact_retrace_proof"] = JsonSerializer.Serialize(proof);
        measurement.Parameters["source_unit_evidence"] = unit.Evidence;
        measurement.Parameters["source_scan_receipt_sha256"] = context.ScanReceiptSha256;
        measurement.Parameters["source_transform_contract"] = context.TransformContract;
        return true;
    }
    private static bool Sha(string? value) => Regex.IsMatch(value ?? "", "\\A[0-9A-Fa-f]{64}\\z");
    private static bool Refuse(string message, out string refusal) { refusal = message; return false; }
}
