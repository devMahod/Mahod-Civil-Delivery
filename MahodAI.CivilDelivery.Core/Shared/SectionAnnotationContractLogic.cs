using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free parser for the APPLY evidence that VERIFY treats as a contract.
    /// Keeping this strict and independent of AutoCAD makes malformed/legacy
    /// artifacts testable without loading acdbmgd.dll.
    /// </summary>
    public static class SectionAnnotationContractLogic
    {
        public const int CurrentVersion = 6;
        public const string OfficeBlockPrefix = "office-block";
        public const string SchematicFallbackPrefix = "schematic-fallback";
        public const string SchematicPrefix = "schematic";
        public const string TrafficDirectionArrowPrefix = "traffic-direction-arrow";
        public const string DesignSlopePrefix = "design-surface-grade";
        public const string DimensionOffsetLabelPrefix = "dimension-offset-label";

        public sealed record DatumEvidence(double Elevation, string Handle);

        public sealed record VehicleEvidence(
            string Presentation,
            string ViewOrFamily,
            double Offset,
            string Detail,
            string? DirectionFlow = null,
            string? DirectionSource = null,
            string? DirectionDigest = null,
            string? Handle = null)
        {
            public bool IsOfficeBlock =>
                string.Equals(Presentation, OfficeBlockPrefix, StringComparison.Ordinal);

            public bool IsSchematic => !IsOfficeBlock;

            public bool HasStrictDirection =>
                SectionVehicleDirectionPlanner.TryParseFlowToken(DirectionFlow, out _) &&
                SectionVehicleDirectionPlanner.IsSupportedDirectionSource(DirectionSource) &&
                SectionVehicleDirectionPlanner.IsSha256(DirectionDigest);

            public bool HasExactOfficeHandle =>
                !IsOfficeBlock ||
                (!string.IsNullOrWhiteSpace(Handle) && Handle.All(Uri.IsHexDigit));
        }

        public sealed record SlopeAnnotationEvidence(
            double FromOffset,
            double ToOffset,
            double FromElevation,
            double ToElevation,
            double Percent,
            string Handle)
        {
            public string Label => SectionFurnitureLogic.FormatSlopePercent(Percent);
        }

        /// <summary>
        /// Verifies serialized APPLY evidence against the fresh PLAN span and live
        /// design chain. Use the same one-sided strip-interior sampling as APPLY.
        /// Sampling at the F6 serialized offsets can land across a vertical curb,
        /// so only the exact, independently matched PLAN endpoints drive sampling.
        /// The evidence may differ from those endpoints by serialization rounding,
        /// never by the wider span-association tolerance used by the host.
        /// </summary>
        public static bool TryVerifySlopeEvidence(
            IReadOnlyList<(double Offset, double Elevation)>? design,
            double plannedFromOffset,
            double plannedToOffset,
            SlopeAnnotationEvidence? recorded,
            out SectionFurnitureLogic.SlopeEvidence? live)
        {
            live = null;
            const double offsetSerializationTolerance = 0.000000500001;
            const double elevationTolerance = 0.000005;
            const double percentTolerance = 0.000005;
            if (design == null || recorded == null ||
                !Finite(recorded.FromOffset) || !Finite(recorded.ToOffset) ||
                !Finite(recorded.FromElevation) || !Finite(recorded.ToElevation) ||
                !Finite(recorded.Percent) ||
                !Finite(plannedFromOffset) || !Finite(plannedToOffset) ||
                Math.Abs(recorded.FromOffset - plannedFromOffset) > offsetSerializationTolerance ||
                Math.Abs(recorded.ToOffset - plannedToOffset) > offsetSerializationTolerance ||
                !SectionFurnitureLogic.TrySlopeEvidence(design, plannedFromOffset,
                    plannedToOffset, out live) || live == null)
                return false;

            return Math.Abs(live.FromElevation - recorded.FromElevation) <= elevationTolerance &&
                   Math.Abs(live.ToElevation - recorded.ToElevation) <= elevationTolerance &&
                   Math.Abs(live.Percent - recorded.Percent) <= percentTolerance;
        }

        public sealed record TrafficDirectionArrowEvidence(
            double Offset,
            string DirectionFlow,
            string DirectionSource,
            string DirectionDigest,
            string Style,
            string AssetSha256,
            string GeometrySha256,
            string Handle);

        public sealed record DimensionOffsetLabelEvidence(
            double AnchorOffset,
            double PlacedOffset,
            string Text,
            string Handle);

        public static string FormatDimensionOffsetLabelReference(
            double anchorOffset,
            double placedOffset,
            string handle)
        {
            if (!Finite(anchorOffset) || !Finite(placedOffset) ||
                string.IsNullOrWhiteSpace(handle) || !handle.All(Uri.IsHexDigit))
                throw new ArgumentException("Dimension-offset label evidence is incomplete.");
            return FormattableString.Invariant(
                $"{DimensionOffsetLabelPrefix}|offset={anchorOffset:F6}|placed={placedOffset:F6}|text={anchorOffset:0.00;-0.00}|handle={handle.ToUpperInvariant()}");
        }

        public static bool TryParseDimensionOffsetLabelReference(
            string? value,
            out DimensionOffsetLabelEvidence? evidence,
            out string error)
        {
            evidence = null;
            error = string.Empty;
            var fields = value?.Split('|') ?? Array.Empty<string>();
            if (fields.Length != 5 || fields[0] != DimensionOffsetLabelPrefix ||
                !TryOffset(fields[1], out var anchor) ||
                !TryTaggedDouble(fields[2], "placed=", out var placed) ||
                !fields[3].StartsWith("text=", StringComparison.Ordinal) ||
                !fields[4].StartsWith("handle=", StringComparison.Ordinal))
            {
                error = "dimension-offset label evidence has an unsupported shape";
                return false;
            }
            var text = fields[3].Substring("text=".Length);
            var expected = anchor.ToString("0.00;-0.00", CultureInfo.InvariantCulture);
            var handle = fields[4].Substring("handle=".Length).ToUpperInvariant();
            if (!string.Equals(text, expected, StringComparison.Ordinal) ||
                handle.Length == 0 || !handle.All(Uri.IsHexDigit))
            {
                error = "dimension-offset label text/handle contradicts its anchor";
                return false;
            }
            evidence = new DimensionOffsetLabelEvidence(anchor, placed, text, handle);
            return true;
        }

        /// <summary>
        /// Whether a datum label is the one decoration wrote for this evidence. Decoration labels the
        /// existing-ground datum with the FULL elevation to 2 decimals and records the evidence to 3
        /// (<c>elevation={x:F3}</c>), so re-rounding the stored value double-rounds: 102.21489 is
        /// labelled "102.21", stored as 102.215, and 102.215:F2 is "102.22" — VERIFY failed a correct
        /// label (MahodAI live test, 2026-09-30; 824/825 on a correct batch). The label matches when it
        /// is the 2-decimal rounding of any value the 3-decimal evidence stands for.
        /// </summary>
        public static bool DatumTextMatches(string? text, double evidenceElevation)
        {
            if (text == null || double.IsNaN(evidenceElevation) || double.IsInfinity(evidenceElevation))
                return false;
            foreach (var candidate in new[] { evidenceElevation, evidenceElevation - 0.0005, evidenceElevation + 0.0005 })
            {
                if (string.Equals(text, SectionDrawingTextLogic.DatumText(candidate), StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static bool TryParseDatumReference(
            string? value, out DatumEvidence? evidence, out string error)
        {
            evidence = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "datum evidence is missing";
                return false;
            }

            var fields = value.Split('|');
            if (fields.Length != 3 ||
                !string.Equals(fields[0], "existing-ground-at-axis", StringComparison.Ordinal) ||
                !fields[1].StartsWith("elevation=", StringComparison.Ordinal) ||
                !fields[2].StartsWith("handle=", StringComparison.Ordinal))
            {
                error = "datum evidence has an unsupported shape";
                return false;
            }

            var elevationText = fields[1].Substring("elevation=".Length);
            var handle = fields[2].Substring("handle=".Length);
            if (!double.TryParse(elevationText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var elevation) || !Finite(elevation))
            {
                error = "datum elevation is not finite invariant numeric evidence";
                return false;
            }
            if (handle.Length == 0 || !handle.All(Uri.IsHexDigit))
            {
                error = "datum handle is not hexadecimal";
                return false;
            }

            evidence = new DatumEvidence(elevation, handle.ToUpperInvariant());
            return true;
        }

        public static bool TryParseVehicleEvidence(
            string? value, out VehicleEvidence? evidence, out string error)
        {
            evidence = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "vehicle evidence is blank";
                return false;
            }

            var fields = value.Split('|');
            var presentation = fields[0];
            if (string.Equals(presentation, OfficeBlockPrefix, StringComparison.Ordinal) ||
                string.Equals(presentation, SchematicFallbackPrefix, StringComparison.Ordinal))
            {
                var office = string.Equals(presentation, OfficeBlockPrefix,
                    StringComparison.Ordinal);
                if (fields.Length != (office ? 8 : 7) ||
                    (fields[1] != "front" && fields[1] != "rear") ||
                    !TryOffset(fields[2], out var offset) ||
                    !fields[3].StartsWith("direction=", StringComparison.Ordinal) ||
                    !SectionVehicleDirectionPlanner.TryParseFlowToken(
                        fields[3].Substring("direction=".Length), out var flow) ||
                    !fields[4].StartsWith("direction-source=", StringComparison.Ordinal) ||
                    !SectionVehicleDirectionPlanner.IsSupportedDirectionSource(
                        fields[4].Substring("direction-source=".Length)) ||
                    !fields[5].StartsWith("direction-digest=", StringComparison.Ordinal) ||
                    !SectionVehicleDirectionPlanner.IsSha256(
                        fields[5].Substring("direction-digest=".Length)) ||
                    !fields[6].StartsWith("detail=", StringComparison.Ordinal) ||
                    fields[6].Length == "detail=".Length ||
                    (office && (!fields[7].StartsWith("handle=", StringComparison.Ordinal) ||
                                fields[7].Length == "handle=".Length ||
                                !fields[7].Substring("handle=".Length).All(Uri.IsHexDigit))))
                {
                    error = "office/fallback vehicle evidence lacks strict direction provenance";
                    return false;
                }

                if (!SectionFurnitureLogic.TryOfficeCarViewForFlow(flow, out var expectedView) ||
                    !string.Equals(expectedView.ToString(), fields[1],
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "office vehicle view contradicts the Civil traffic-flow convention";
                    return false;
                }

                var detail = fields[6].Substring("detail=".Length);
                if (presentation == OfficeBlockPrefix &&
                    (detail.Length != 16 || !detail.All(Uri.IsHexDigit)))
                {
                    error = "office block evidence does not carry its 16-hex asset digest";
                    return false;
                }

                evidence = new VehicleEvidence(
                    presentation,
                    fields[1],
                    offset,
                    detail,
                    SectionVehicleDirectionPlanner.FlowToken(flow),
                    fields[4].Substring("direction-source=".Length),
                    fields[5].Substring("direction-digest=".Length).ToLowerInvariant(),
                    office
                        ? fields[7].Substring("handle=".Length).ToUpperInvariant()
                        : null);
                return true;
            }

            if (string.Equals(presentation, SchematicPrefix, StringComparison.Ordinal))
            {
                if (fields.Length != 7 ||
                    (fields[1] != "bus" && fields[1] != "bike") ||
                    !TryOffset(fields[2], out var offset) ||
                    !fields[3].StartsWith("direction=", StringComparison.Ordinal) ||
                    !SectionVehicleDirectionPlanner.TryParseFlowToken(
                        fields[3].Substring("direction=".Length), out var flow) ||
                    !fields[4].StartsWith("direction-source=", StringComparison.Ordinal) ||
                    !SectionVehicleDirectionPlanner.IsSupportedDirectionSource(
                        fields[4].Substring("direction-source=".Length)) ||
                    !fields[5].StartsWith("direction-digest=", StringComparison.Ordinal) ||
                    !SectionVehicleDirectionPlanner.IsSha256(
                        fields[5].Substring("direction-digest=".Length)) ||
                    fields[6] != "approved-office-block-not-supplied")
                {
                    error = "schematic vehicle evidence lacks strict direction provenance";
                    return false;
                }

                evidence = new VehicleEvidence(
                    presentation,
                    fields[1],
                    offset,
                    fields[6],
                    SectionVehicleDirectionPlanner.FlowToken(flow),
                    fields[4].Substring("direction-source=".Length),
                    fields[5].Substring("direction-digest=".Length).ToLowerInvariant());
                return true;
            }

            error = "vehicle presentation is unsupported";
            return false;
        }

        public static string FormatSchematicVehicleReference(
            string family,
            double offset,
            SectionVehicleDirectionPlanner.DirectionPlan direction)
        {
            if ((family != "bus" && family != "bike") ||
                !Finite(offset) || direction == null || !direction.IsResolved ||
                direction.DirectionSource == null || direction.DirectionDigest == null ||
                (family == "bike") !=
                (direction.EvidenceMode ==
                 SectionVehicleDirectionPlanner.ArrowEvidenceMode.Bicycle))
                throw new ArgumentException(
                    "Schematic vehicle evidence requires a resolved family-specific direction plan.");

            return string.Join("|",
                SchematicPrefix,
                family,
                "offset=" + offset.ToString("F3", CultureInfo.InvariantCulture),
                "direction=" + SectionVehicleDirectionPlanner.FlowToken(direction.Flow),
                "direction-source=" + direction.DirectionSource,
                "direction-digest=" + direction.DirectionDigest.ToLowerInvariant(),
                "approved-office-block-not-supplied");
        }

        /// <summary>
        /// Serializes the one registered reference to Nataly's approved office arrow
        /// block above a directional strip. The same direction source/digest is
        /// carried by the vehicle evidence, so VERIFY can require a one-to-one pair.
        /// </summary>
        public static string FormatTrafficDirectionArrowReference(
            double offset,
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            string directionSource,
            string directionDigest,
            string style,
            string assetSha256,
            string geometrySha256,
            string handle)
        {
            if (!Finite(offset) ||
                !SectionVehicleDirectionPlanner.IsSupportedDirectionSource(directionSource) ||
                !SectionVehicleDirectionPlanner.IsSha256(directionDigest) ||
                !SectionTrafficDirectionAnnotationLogic.IsStyleToken(style) ||
                !string.Equals(
                    assetSha256,
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(
                    geometrySha256))
                throw new ArgumentException("Direction-arrow evidence is incomplete.");

            var normalizedHandle = handle?.Trim().ToUpperInvariant() ?? string.Empty;
            if (normalizedHandle.Length == 0 || !normalizedHandle.All(Uri.IsHexDigit))
                throw new ArgumentException(
                    "Direction-arrow evidence requires one hexadecimal block-reference handle.");

            return string.Join("|",
                TrafficDirectionArrowPrefix,
                "offset=" + offset.ToString("F3", CultureInfo.InvariantCulture),
                "direction=" + SectionVehicleDirectionPlanner.FlowToken(flow),
                "direction-source=" + directionSource,
                "direction-digest=" + directionDigest.ToLowerInvariant(),
                "style=" + style,
                "asset-sha256=" + assetSha256.ToLowerInvariant(),
                "geometry-sha256=" + geometrySha256,
                "handle=" + normalizedHandle);
        }

        public static bool TryParseTrafficDirectionArrowReference(
            string? value,
            out TrafficDirectionArrowEvidence? evidence,
            out string error)
        {
            evidence = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "direction-arrow evidence is blank";
                return false;
            }

            var fields = value.Split('|');
            if (fields.Length != 9 ||
                fields[0] != TrafficDirectionArrowPrefix ||
                !TryOffset(fields[1], out var offset) ||
                !fields[2].StartsWith("direction=", StringComparison.Ordinal) ||
                !SectionVehicleDirectionPlanner.TryParseFlowToken(
                    fields[2].Substring("direction=".Length), out var flow) ||
                !fields[3].StartsWith("direction-source=", StringComparison.Ordinal) ||
                !SectionVehicleDirectionPlanner.IsSupportedDirectionSource(
                    fields[3].Substring("direction-source=".Length)) ||
                !fields[4].StartsWith("direction-digest=", StringComparison.Ordinal) ||
                !SectionVehicleDirectionPlanner.IsSha256(
                    fields[4].Substring("direction-digest=".Length)) ||
                !fields[5].StartsWith("style=", StringComparison.Ordinal) ||
                !SectionTrafficDirectionAnnotationLogic.IsStyleToken(
                    fields[5].Substring("style=".Length)) ||
                !fields[6].StartsWith("asset-sha256=", StringComparison.Ordinal) ||
                !string.Equals(
                    fields[6].Substring("asset-sha256=".Length),
                    SectionTrafficArrowAssetEvidenceLogic.SourceSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !fields[7].StartsWith("geometry-sha256=", StringComparison.Ordinal) ||
                !SectionOfficeVehicleAssetEvidenceLogic.IsLowerHexSha256(
                    fields[7].Substring("geometry-sha256=".Length)) ||
                !fields[8].StartsWith("handle=", StringComparison.Ordinal))
            {
                error = "direction-arrow evidence has an unsupported shape";
                return false;
            }

            var handle = fields[8].Substring("handle=".Length).Trim().ToUpperInvariant();
            if (handle.Length == 0 || !handle.All(Uri.IsHexDigit))
            {
                error = "direction-arrow block-reference handle is malformed";
                return false;
            }

            evidence = new TrafficDirectionArrowEvidence(
                offset,
                SectionVehicleDirectionPlanner.FlowToken(flow),
                fields[3].Substring("direction-source=".Length),
                fields[4].Substring("direction-digest=".Length).ToLowerInvariant(),
                fields[5].Substring("style=".Length),
                fields[6].Substring("asset-sha256=".Length).ToLowerInvariant(),
                fields[7].Substring("geometry-sha256=".Length),
                handle);
            return true;
        }

        /// <summary>
        /// Serializes the exact sampled design endpoints, derived grade and registered
        /// DBText handle. VERIFY parses and recomputes the grade instead of trusting a
        /// free-form percentage string from APPLY.
        /// </summary>
        public static string FormatSlopeReference(
            SectionFurnitureLogic.SlopeEvidence evidence, string handle) =>
            FormattableString.Invariant(
                $"{DesignSlopePrefix}|from={evidence.FromOffset:F6}|to={evidence.ToOffset:F6}|z_from={evidence.FromElevation:F6}|z_to={evidence.ToElevation:F6}|percent={evidence.Percent:F6}|handle={handle}");

        public static bool TryParseSlopeReference(
            string? value, out SlopeAnnotationEvidence? evidence, out string error)
        {
            evidence = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "slope evidence is missing";
                return false;
            }

            var fields = value.Split('|');
            if (fields.Length != 7 ||
                !string.Equals(fields[0], DesignSlopePrefix, StringComparison.Ordinal) ||
                !TryTaggedDouble(fields[1], "from=", out var from) ||
                !TryTaggedDouble(fields[2], "to=", out var to) ||
                !TryTaggedDouble(fields[3], "z_from=", out var zFrom) ||
                !TryTaggedDouble(fields[4], "z_to=", out var zTo) ||
                !TryTaggedDouble(fields[5], "percent=", out var percent) ||
                !fields[6].StartsWith("handle=", StringComparison.Ordinal))
            {
                error = "slope evidence has an unsupported shape or non-finite value";
                return false;
            }

            var handle = fields[6].Substring("handle=".Length);
            if (handle.Length == 0 || !handle.All(Uri.IsHexDigit))
            {
                error = "slope label handle is not hexadecimal";
                return false;
            }
            // Each F6 endpoint can move by half a micrometre. This is only
            // serialization validation; TryVerifySlopeEvidence still enforces
            // the true 0.5 m minimum on the independently matched PLAN span.
            if (to - from < 0.5 - 0.000001000001)
            {
                error = "slope evidence span is shorter than the section mark contract";
                return false;
            }

            var recomputed = (zTo - zFrom) * 100.0 / (to - from);
            if (!Finite(recomputed) || Math.Abs(recomputed - percent) > 0.001)
            {
                error = "slope percentage does not match its sampled endpoint evidence";
                return false;
            }

            evidence = new SlopeAnnotationEvidence(
                from, to, zFrom, zTo, percent, handle.ToUpperInvariant());
            return true;
        }

        private static bool TryOffset(string field, out double offset)
        {
            offset = double.NaN;
            return field.StartsWith("offset=", StringComparison.Ordinal) &&
                   double.TryParse(field.Substring("offset=".Length), NumberStyles.Float,
                   CultureInfo.InvariantCulture, out offset) && Finite(offset);
        }

        private static bool TryTaggedDouble(string field, string tag, out double value)
        {
            value = double.NaN;
            return field.StartsWith(tag, StringComparison.Ordinal) &&
                   double.TryParse(field.Substring(tag.Length), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out value) && Finite(value);
        }

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
