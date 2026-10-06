using System;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Binds a complete, unchanged native failure capture to the workflow's saved-host
/// identity BEFORE calculating an area. The receipt is input-only, not output proof.
/// Native traversal supplies DirectHostModelSpaceIdentity; no geometry is inferred
/// from bounds, a file name, or a successful output. Existing end-of-scan source and
/// database-revision guards still control publication.
/// </summary>
internal static class StrictHatchMixedLineRetraceInputFactory
{
    internal const string FindingCode = "EST-HATCH-MIXED-LINE-RETRACE-AREA-RECOVERED";
    internal const string ReceiptContract = "hatch-mixed-line-retrace-input-only-v1";
    private sealed record InputReceipt(string Contract, StrictHatchExactRetraceInputFactory.HostInput Host, ProvenanceRef Source,
        HatchAreaFailureDiagnostic.Snapshot Boundary, string OriginalAreaError);

    internal static bool TryRecover(HatchAreaFailureDiagnostic.Snapshot? snapshot,
        ProvenanceRef? source, StrictHatchExactRetraceInputFactory.HostInput? host, string originalAreaError, double[]? originalBounds,
        out QuantityMeasurement? measurement, out string refusal)
    {
        measurement = null;
        refusal = "";
        if (snapshot == null || source == null || host == null ||
            !host.DirectHostModelSpaceIdentity || host.Failure != null || host.DbMod != 0 ||
            string.IsNullOrWhiteSpace(host.DatabaseRevision))
            return Refuse("saved direct-host ModelSpace identity is absent, dirty, failed or transformed; this recovery is not enabled for XREFs or nested blocks", out refusal);
        if (snapshot.Truncated || snapshot.Error != null || snapshot.Loops == null ||
            snapshot.DeclaredLoops != snapshot.Loops.Count || snapshot.Loops.Count == 0 ||
            snapshot.Loops.Select((loop, i) => loop == null || loop.Index != i ||
                loop.Error != null || loop.Evidence == null || loop.Evidence.Truncated ||
                loop.Evidence.Items == null || loop.Evidence.Items.Any(item => item == null)).Any(bad => bad))
            return Refuse("complete original boundary inventory is required; no omitted, truncated or unreadable loops/items", out refusal);
        try
        {
            // Freeze all inputs once. The full source boundary survives even when the
            // interpretation later refuses. No measured area/proof appears in this hash.
            var receiptJson = JsonSerializer.Serialize(new InputReceipt(
                ReceiptContract, host, source, snapshot, originalAreaError));
            var receiptSha = ArtifactHash.Sha256OfText(receiptJson);
            var frozen = JsonSerializer.Deserialize<InputReceipt>(receiptJson)!;
            var context = new StrictHatchExactRetraceAreaRecovery.DirectHostContext(
                frozen.Host.RunId, frozen.Host.DrawingPath, frozen.Host.DrawingSha256,
                frozen.Host.DbMod!.Value, receiptSha, frozen.Host.Units, 1,
                "direct-host-modelspace-identity");
            if (!StrictHatchMixedLineRetraceAreaRecovery.TryRecover(frozen.Boundary, frozen.Source,
                    context, frozen.OriginalAreaError, originalBounds?.ToArray(),
                    out measurement, out _, out refusal))
                return false;
            measurement!.Parameters["boundary_input_receipt_contract"] = ReceiptContract;
            measurement.Parameters["boundary_input_receipt_json"] = receiptJson;
            measurement.Parameters["boundary_input_receipt_sha256"] = receiptSha;
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            measurement = null;
            return Refuse("input receipt cannot be frozen: " + error.Message, out refusal);
        }
    }

    private static bool Refuse(string reason, out string refusal) { refusal = reason; return false; }
}

