using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Read-only interpretation of an already captured failed Area call. No endpoint
/// snapping, repair, missing-edge closure, curve approximation or island inference.
/// Returns source-plane area through the SAME unit conversion as Hatch.Area;
/// the caller still owns the existing, independently guarded XREF transformation.
/// </summary>
internal static class StrictHatchLinearAreaRecovery
{
    internal const string Method = "hatch-linear-boundary-area";
    internal const string FindingCode = "EST-HATCH-LINEAR-AREA-RECOVERED";
    private static readonly Regex Header = new(
        @"\Astyle=(Normal|Outer|Ignore); normal=([^,;]+),([^,;]+),([^,;]+); elevation=([^;]+)\z",
        RegexOptions.CultureInvariant);

    internal static bool TryRecover(HatchAreaFailureDiagnostic.Snapshot snapshot,
        DrawingUnitPolicy.Scale units, string originalError, double[]? bounds,
        out QuantityMeasurement? measurement, out string refusal)
    {
        measurement = null;
        refusal = "";
        if (!units.IsSupported || !double.IsFinite(units.LinearToMetres) || units.LinearToMetres <= 0)
            return Refuse("drawing-unit conversion is not proven", out refusal);
        if (string.IsNullOrWhiteSpace(originalError))
            return Refuse("original Area API failure is missing", out refusal);
        if (snapshot == null || snapshot.Error != null || snapshot.Truncated ||
            snapshot.DeclaredLoops != 1 || snapshot.Loops.Count != 1 ||
            snapshot.CoordinateSystem != "raw hatch OCS / source drawing units; not host WCS or square metres")
            return Refuse("capture is not one complete source-OCS loop", out refusal);
        var header = Header.Match(snapshot.Header ?? "");
        if (!header.Success || !Finite(header.Groups[2].Value, out var nx) ||
            !Finite(header.Groups[3].Value, out var ny) || !Finite(header.Groups[4].Value, out var nz) ||
            !Finite(header.Groups[5].Value, out _) || nx != 0 || ny != 0 || Math.Abs(nz) != 1)
            return Refuse("style/elevation or exactly horizontal unit normal is unproven", out refusal);
        var loop = snapshot.Loops[0];
        if (loop.Index != 0 || loop.Error != null || loop.Evidence is not { } evidence ||
            evidence.Truncated || evidence.Kind != "edge-list" || evidence.Items.Count < 3 ||
            evidence.Items.Count > HatchAreaFailureDiagnostic.MaximumItems)
            return Refuse("loop is incomplete, curved, or outside the bounded reader contract", out refusal);
        var flags = evidence.Flags.Split(',', StringSplitOptions.TrimEntries);
        if (flags.Length == 0 || flags.Any(flag => flag is not ("Default" or "External" or "Derived" or "Outermost")))
            return Refuse("loop flags do not prove a supported closed boundary", out refusal);

        var starts = new Coordinate[evidence.Items.Count];
        var ends = new Coordinate[evidence.Items.Count];
        for (var index = 0; index < evidence.Items.Count; index++)
        {
            var item = evidence.Items[index];
            if (!item.StartsWith("line=", StringComparison.Ordinal))
                return Refuse("only explicitly captured straight edges are supported", out refusal);
            var pair = item[5..].Split(" -> ", StringSplitOptions.None);
            if (pair.Length != 2 || !Point(pair[0], out starts[index]) || !Point(pair[1], out ends[index]) ||
                starts[index].Equals2D(ends[index]))
                return Refuse("edge is malformed, non-finite or zero length", out refusal);
        }
        for (var index = 0; index < starts.Length; index++)
            if (!ends[index].Equals2D(starts[(index + 1) % starts.Length]))
                return Refuse("directed endpoints are not exactly closed; no gap may be repaired", out refusal);

        try
        {
            // The last captured endpoint already equals the first. Appending that
            // exact captured endpoint is an NTS ring representation, not closure.
            var coordinates = starts.Concat(new[] { ends[^1] }).ToArray();
            var polygon = new GeometryFactory().CreatePolygon(coordinates);
            var validity = new IsValidOp(polygon);
            if (!validity.IsValid || polygon.IsEmpty || !polygon.ExteriorRing.IsSimple)
                return Refuse("boundary is not a valid simple polygon: " + validity.ValidationError, out refusal);

            // Translated compensated shoelace retains precision at survey origins.
            // OCS translation/elevation and an exact +/-Z normal preserve area.
            var anchor = starts[0];
            double twiceArea = 0, compensation = 0;
            for (var index = 0; index < starts.Length; index++)
            {
                var a = starts[index]; var b = ends[index];
                var cross = (a.X - anchor.X) * (b.Y - anchor.Y) - (b.X - anchor.X) * (a.Y - anchor.Y);
                var corrected = cross - compensation;
                var next = twiceArea + corrected;
                compensation = (next - twiceArea) - corrected;
                twiceArea = next;
            }
            var sourceArea = Math.Abs(twiceArea) / 2;
            var converted = units.Area(sourceArea);
            if (!double.IsFinite(sourceArea) || sourceArea <= 0 || !double.IsFinite(converted) || converted <= 0)
                return Refuse("closed polygon area is not finite and positive", out refusal);
            measurement = new QuantityMeasurement
            {
                Kind = "area", Method = Method, RawValue = converted, Unit = units.AreaUnit,
                // MeasureNatural has already converted these bounds once.
                GeometryEvidence = bounds,
                Parameters =
                {
                    ["original_area_api_failure"] = originalError,
                    ["boundary_sha256"] = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(snapshot)),
                    ["boundary_source_area"] = sourceArea.ToString("R", CultureInfo.InvariantCulture),
                    ["boundary_edge_count"] = starts.Length.ToString(CultureInfo.InvariantCulture),
                    ["boundary_contract"] = "single-exact-closed-linear-simple-ocs-v1",
                    ["source_insunits"] = units.SourceName,
                },
            };
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or TopologyException)
        {
            return Refuse("polygon proof failed: " + error.Message, out refusal);
        }
    }

    private static bool Refuse(string reason, out string refusal) { refusal = reason; return false; }
    private static bool Finite(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    private static bool Point(string text, out Coordinate point)
    {
        point = null!;
        var xy = text.Split(',');
        if (xy.Length != 2 || !Finite(xy[0], out var x) || !Finite(xy[1], out var y)) return false;
        point = new Coordinate(x, y);
        return true;
    }
}
