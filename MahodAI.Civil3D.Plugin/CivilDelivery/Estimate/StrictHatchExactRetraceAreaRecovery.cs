using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Quantity-only interpretation of an unchanged failed Area capture. Cancels only
/// originally adjacent exact reversed straight edges; never snaps, joins, reorders,
/// repairs a DWG, supplies a section boundary, or interprets multiple loops.
/// This first candidate is deliberately restricted to proven direct-host inputs.
/// </summary>
internal static class StrictHatchExactRetraceAreaRecovery
{
    internal const string Method = "hatch-exact-retrace-linear-area";
    internal const string Contract = "single-exact-closed-linear-adjacent-retrace-area-v1";

    // Authenticity of these receipt fields is the native caller's responsibility.
    // Offline callers must bind them to verified source artifacts, not guessed SI.
    internal sealed record DirectHostContext(string RunId, string DrawingPath, string DrawingSha256,
        int DbMod, string ScanReceiptSha256, PhysicalDrawingUnitPolicy.Resolution Units,
        double MaximumLinearScale, string TransformContract);
    internal sealed record CancelledPair(int FirstOriginalIndex, int SecondOriginalIndex,
        string FirstOriginalEdge, string SecondOriginalEdge);
    internal sealed record Proof(string OriginalSnapshotJson, string OriginalSnapshotSha256,
        string SourceIdentityJson, string ContextJson, string ResidualSnapshotSha256,
        IReadOnlyList<CancelledPair> CancelledPairs, IReadOnlyList<int> RetainedOriginalIndices);
    private readonly record struct Point(double X, double Y);
    private readonly record struct Edge(int Index, Point Start, Point End, string Original);

    internal static bool TryRecover(HatchAreaFailureDiagnostic.Snapshot snapshot, ProvenanceRef source,
        DirectHostContext context, string originalAreaError, double[]? originalBounds,
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
            evidence.Items.Count < 5 || evidence.Items.Count > HatchAreaFailureDiagnostic.MaximumItems)
            return Refuse("complete bounded straight-edge loop with a removable pair is required", out refusal);
        var originalItems = evidence.Items.ToArray();
        var edges = new Edge[originalItems.Length];
        for (var i = 0; i < edges.Length; i++)
        {
            var item = originalItems[i];
            if (item == null || !item.StartsWith("line=", StringComparison.Ordinal))
                return Refuse("curves and non-line items are not supported", out refusal);
            var endpoints = item[5..].Split(" -> ", StringSplitOptions.None);
            if (endpoints.Length != 2 || !Parse(endpoints[0], out var start) ||
                !Parse(endpoints[1], out var end) || start == end)
                return Refuse("malformed, non-finite or zero-length original line", out refusal);
            edges[i] = new(i, start, end, item);
        }
        for (var i = 0; i < edges.Length; i++)
            if (edges[i].End != edges[(i + 1) % edges.Length].Start)
                return Refuse("original directed ring is not exactly closed", out refusal);

        var removed = new bool[edges.Length];
        var pairs = new List<CancelledPair>();
        // Original neighbours only, scan from the end for deterministic attribution.
        // No cyclic first/last cancellation or newly-adjacent/nested reduction.
        for (var i = edges.Length - 1; i > 0; i--)
        {
            var first = edges[i - 1]; var second = edges[i];
            if (first.Start != second.End || first.End != second.Start) continue;
            removed[i - 1] = removed[i] = true;
            pairs.Add(new(i - 1, i, first.Original, second.Original));
            i--;
        }
        if (pairs.Count == 0) return Refuse("no originally adjacent exact reverse pair", out refusal);
        var retained = edges.Where(edge => !removed[edge.Index]).ToArray();
        if (retained.Length < 3) return Refuse("residual has fewer than three original edges", out refusal);

        // Freeze the full original capture before deriving a separate diagnostic copy.
        var original = snapshot with { Loops = Array.AsReadOnly(new[] {
            originalLoop with { Evidence = evidence with { Items = Array.AsReadOnly(originalItems) } } }) };
        var residual = original with { Loops = Array.AsReadOnly(new[] {
            originalLoop with { Evidence = evidence with {
                Items = Array.AsReadOnly(retained.Select(edge => edge.Original).ToArray()) } } }) };
        var scale = new DrawingUnitPolicy.Scale(true, unit.LinearToMetres,
            "INSUNITS=" + unit.RawUnitCode.ToString(CultureInfo.InvariantCulture), "מטר", "מ\"ר", "מ\"ק");
        // Keep the existing normal/style/flags/exact closure/NTS/positive-area/unit guards.
        // No new geometry or area formula is introduced here.
        if (!StrictHatchLinearAreaRecovery.TryRecover(residual, scale, originalAreaError,
                originalBounds, out var recovered, out var residualRefusal))
            return Refuse("residual linear proof refused: " + residualRefusal, out refusal);
        var originalJson = JsonSerializer.Serialize(original);
        var originalSha = ArtifactHash.Sha256OfText(originalJson);
        var residualSha = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(residual));
        var contextJson = JsonSerializer.Serialize(context);
        var sourceJson = JsonSerializer.Serialize(source);
        pairs.Sort((a,b) => a.FirstOriginalIndex.CompareTo(b.FirstOriginalIndex));
        proof = new(originalJson, originalSha, sourceJson, contextJson, residualSha,
            pairs.AsReadOnly(), Array.AsReadOnly(retained.Select(edge => edge.Index).ToArray()));
        measurement = new QuantityMeasurement
        {
            Kind = recovered!.Kind, Method = Method, RawValue = recovered.RawValue,
            Unit = recovered.Unit, GeometryEvidence = recovered.GeometryEvidence,
        };
        foreach (var parameter in recovered.Parameters) measurement.Parameters.Add(parameter.Key, parameter.Value);
        measurement.Parameters["boundary_sha256"] = originalSha;
        measurement.Parameters["boundary_contract"] = Contract;
        measurement.Parameters["boundary_original_edge_count"] = edges.Length.ToString(CultureInfo.InvariantCulture);
        measurement.Parameters["boundary_residual_sha256"] = residualSha;
        measurement.Parameters["boundary_exact_retrace_proof"] = JsonSerializer.Serialize(proof);
        measurement.Parameters["source_unit_evidence"] = unit.Evidence;
        measurement.Parameters["source_scan_receipt_sha256"] = context.ScanReceiptSha256;
        measurement.Parameters["source_transform_contract"] = context.TransformContract;
        return true;
    }
    private static bool Sha(string? value) => Regex.IsMatch(value ?? "", "\\A[0-9A-Fa-f]{64}\\z");
    private static bool Refuse(string message, out string refusal) { refusal = message; return false; }
    private static bool Parse(string text, out Point point)
    {
        point = default;
        var xy = text.Split(',');
        if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) || !double.IsFinite(y)) return false;
        point = new(x,y); return true;
    }
}
