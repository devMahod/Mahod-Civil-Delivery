using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Recovery = MahodAI.CivilDelivery.Shared.StrictHatchCurveAreaRecovery;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Interprets a complete diagnostic already captured from the current native DB.
/// Does not open drawings, retry Area, approximate curves, or alter source geometry.
/// The transform bound constrains numerical uncertainty only; the existing caller
/// still applies the actual nested quantity transform exactly once.
/// </summary>
internal static class StrictHatchCurveAreaRecoveryAdapter
{
    internal const string Method = "hatch-line-arc-boundary-area";
    internal const string FindingCode = "EST-HATCH-CURVE-AREA-RECOVERED";
    private static readonly Regex Header = new(
        @"\Astyle=(Normal|Outer|Ignore); normal=([^,;]+),([^,;]+),([^,;]+); elevation=([^;]+)\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex Arc = new(
        @"\Aarc: start=([^;]+); end=([^;]+); centre=([^;]+); radius=([^;]+); angles=([^,;]+),([^;]+); clockwise=(True|False)(?:; reference_vector=([^;]+))?\z",
        RegexOptions.CultureInvariant);

    internal static bool TryRecover(HatchAreaFailureDiagnostic.Snapshot snapshot,
        DrawingUnitPolicy.Scale units, double maximumLinearScale, string originalError,
        double[]? bounds, out QuantityMeasurement? measurement, out string refusal)
    {
        measurement = null;
        refusal = "";
        if (!units.IsSupported || !double.IsFinite(units.LinearToMetres) || units.LinearToMetres <= 0 ||
            !double.IsFinite(maximumLinearScale) || maximumLinearScale <= 0)
            return Refuse("source units or source-to-host transform bound is unproven", out refusal);
        if (string.IsNullOrWhiteSpace(originalError))
            return Refuse("original Area API failure is missing", out refusal);
        if (!TryParseOriginalInputs(snapshot, units, maximumLinearScale, out var inputs, out var boundaryIdentity, out refusal))
            return false;
        // Every captured loop participates. The multi-loop contract accepts only
        // fully separated loops whose entire analytic bounds are disjoint; holes,
        // contacts and overlapping envelopes are not guessed or silently dropped.
        Recovery.Proof? proof;
        var recovered = inputs!.Count == 1
            ? Recovery.TryRecover(inputs[0], out proof, out refusal)
            : Recovery.TryRecoverDisjointLoops(inputs, out proof, out refusal);
        if (!recovered) return false;
        var area = units.Area(proof!.AreaSourceUnitsSquared);
        // Enclose the source interval AND the rounding of conversion into SI;
        // scaling the error term alone would omit rounding of the midpoint.
        var lower = Math.BitDecrement(proof.AreaSourceUnitsSquared - proof.AbsoluteErrorBoundSourceUnitsSquared);
        var upper = Math.BitIncrement(proof.AreaSourceUnitsSquared + proof.AbsoluteErrorBoundSourceUnitsSquared);
        lower = Math.BitDecrement(Math.BitDecrement(lower * units.LinearToMetres) * units.LinearToMetres);
        upper = Math.BitIncrement(Math.BitIncrement(upper * units.LinearToMetres) * units.LinearToMetres);
        var error = Math.BitIncrement(Math.Max(area - lower, upper - area));
        // Numerical reader precision, not an engineering closure tolerance. Bound
        // the eventual host-area error before emitting any neutral quantity.
        var hostErrorBound = Math.BitIncrement(Math.BitIncrement(error * maximumLinearScale) * maximumLinearScale);
        if (!double.IsFinite(area) || area <= 0 || !double.IsFinite(error) || error < 0 ||
            !double.IsFinite(hostErrorBound) || hostErrorBound > 1e-6 || error > area * 1e-8)
            return Refuse("area enclosure exceeds reader precision (1e-6 host m2 / 1e-8 relative)", out refusal);
        measurement = new QuantityMeasurement
        {
            Kind = "area", Method = Method, RawValue = area, Unit = units.AreaUnit,
            GeometryEvidence = bounds,
            Parameters =
            {
                ["original_area_api_failure"] = originalError,
                ["boundary_sha256"] = boundaryIdentity,
                ["boundary_source_area"] = Number(proof.AreaSourceUnitsSquared),
                ["boundary_source_area_error_bound"] = Number(proof.AbsoluteErrorBoundSourceUnitsSquared),
                ["boundary_si_area_error_bound_before_transform"] = Number(error),
                ["boundary_host_area_error_upper_bound"] = Number(hostErrorBound),
                ["boundary_transform_maximum_linear_scale"] = Number(maximumLinearScale),
                ["boundary_edge_count"] = proof.EdgeCount.ToString(CultureInfo.InvariantCulture),
                ["boundary_arc_count"] = proof.ArcCount.ToString(CultureInfo.InvariantCulture),
                ["boundary_explicit_native_frame_arc_count"] = proof.ExplicitFrameArcCount.ToString(CultureInfo.InvariantCulture),
                ["boundary_loop_count"] = proof.LoopCount.ToString(CultureInfo.InvariantCulture),
                ["boundary_recognized_ulp_join_count"] = proof.RecognizedJoinCount.ToString(CultureInfo.InvariantCulture),
                ["boundary_contract"] = proof.BoundaryContract,
                ["source_insunits"] = units.SourceName,
            },
        };
        return true;
    }

    /// <summary>Parses a complete original diagnostic without interpreting area,
    /// topology or quantity. Same parser used by the existing recovery path.</summary>
    internal static bool TryParseOriginalInputs(HatchAreaFailureDiagnostic.Snapshot snapshot,
        DrawingUnitPolicy.Scale units, double maximumLinearScale,
        out IReadOnlyList<Recovery.Input>? inputs, out string boundaryIdentity, out string refusal)
    {
        inputs = null; boundaryIdentity = ""; refusal = "";
        if (units == null)
            return Refuse("source units or source-to-host transform bound is unproven", out refusal);
        if (!units.IsSupported || !double.IsFinite(units.LinearToMetres) || units.LinearToMetres <= 0 ||
            !double.IsFinite(maximumLinearScale) || maximumLinearScale <= 0)
            return Refuse("source units or source-to-host transform bound is unproven", out refusal);
        if (snapshot == null || snapshot.Error != null || snapshot.Truncated ||
            snapshot.DeclaredLoops is not >= 1 or > 32 || snapshot.Loops == null || snapshot.Loops.Count != snapshot.DeclaredLoops ||
            snapshot.CoordinateSystem != "raw hatch OCS / source drawing units; not host WCS or square metres")
            return Refuse("capture does not contain every original source-OCS loop within the reader limit", out refusal);
        var header = Header.Match(snapshot.Header ?? "");
        if (!header.Success || !Finite(header.Groups[2].Value, out var nx) ||
            !Finite(header.Groups[3].Value, out var ny) || !Finite(header.Groups[4].Value, out var nz) ||
            !Finite(header.Groups[5].Value, out var elevation))
            return Refuse("original hatch plane/style is incomplete", out refusal);
        boundaryIdentity = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(snapshot));
        var parsedInputs = new List<Recovery.Input>(snapshot.Loops.Count);
        var edgeCount = 0;
        for (var loopIndex = 0; loopIndex < snapshot.Loops.Count; loopIndex++)
        {
            var loop = snapshot.Loops[loopIndex];
            if (loop == null || loop.Index != loopIndex || loop.Error != null || loop.Evidence is not { } evidence ||
                evidence.Truncated || evidence.Kind != "edge-list" ||
                evidence.Items == null || evidence.Items.Count < 2 || evidence.Items.Count > Recovery.MaximumEdges ||
                (edgeCount += evidence.Items.Count) > Recovery.MaximumEdges)
                return Refuse("original edge-list is incomplete or outside the bounded reader contract", out refusal);
            var edges = new List<Recovery.Edge>(evidence.Items.Count);
            foreach (var item in evidence.Items)
            {
                if (item == null)
                    return Refuse("original edge-list contains a missing item", out refusal);
                if (item.StartsWith("line=", StringComparison.Ordinal))
                {
                    var pair = item[5..].Split(" -> ", StringSplitOptions.None);
                    if (pair.Length != 2 || !Point(pair[0], out var a) || !Point(pair[1], out var b))
                        return Refuse("malformed original line parameters", out refusal);
                    edges.Add(new Recovery.LineEdge(a, b));
                }
                else
                {
                    var arc = Arc.Match(item);
                    if (!arc.Success || !Point(arc.Groups[1].Value, out var start) ||
                        !Point(arc.Groups[2].Value, out var end) || !Point(arc.Groups[3].Value, out var center) ||
                        !Finite(arc.Groups[4].Value, out var radius) ||
                        !Finite(arc.Groups[5].Value, out var angle1) || !Finite(arc.Groups[6].Value, out var angle2))
                        return Refuse("unsupported curve or malformed original circular-arc parameters", out refusal);
                    Recovery.OriginalArcFrame? frame = null;
                    if (arc.Groups[8].Success)
                    {
                        if (!Point(arc.Groups[8].Value, out var vector))
                            return Refuse("malformed native circular-arc reference vector", out refusal);
                        frame = new Recovery.OriginalArcFrame(vector, Recovery.FrameOrigin.NativeReferenceVector,
                            boundaryIdentity + "/loop/" + loopIndex.ToString(CultureInfo.InvariantCulture) +
                            "/edge/" + edges.Count.ToString(CultureInfo.InvariantCulture));
                    }
                    // Absence is supported only for historical captures. New
                    // captures include either the actual getter or an explicit
                    // failure marker, which the strict parser above refuses.
                    edges.Add(new Recovery.ArcEdge(start, end, center, radius, angle1, angle2,
                        arc.Groups[7].Value == "True") { OriginalFrame = frame });
                }
            }
            parsedInputs.Add(new Recovery.Input(Array.AsReadOnly(edges.ToArray()), snapshot.DeclaredLoops.Value, true,
                evidence.Flags, header.Groups[1].Value, nx, ny, nz, elevation,
                units.LinearToMetres, maximumLinearScale));
        }
        inputs = parsedInputs.AsReadOnly();
        return true;
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Refuse(string value, out string refusal) { refusal = value; return false; }
    private static bool Finite(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    private static bool Point(string text, out Recovery.Point point)
    {
        point = default;
        var xy = text.Split(',');
        if (xy.Length != 2 || !Finite(xy[0], out var x) || !Finite(xy[1], out var y)) return false;
        point = new Recovery.Point(x, y);
        return true;
    }
}
