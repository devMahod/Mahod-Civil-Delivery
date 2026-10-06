using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Pure geometry for the section furniture that needs GROUND context: schematic
    /// vehicles standing on the design line per strip (the engineer's drafted
    /// sections show them — call, 31/08), and normalization of Civil section points
    /// into (offset, elevation) chains regardless of the coordinate convention the
    /// host returns.
    /// </summary>
    public static class SectionFurnitureLogic
    {
        // ------------------------------------------------------------- vehicles

        /// <summary>A schematic vehicle: true metric size, drawn front-view.</summary>
        public sealed record VehicleSpec(string Key, string Label, double WidthM, double HeightM);

        public static readonly VehicleSpec Car = new("car", "רכב פרטי", 1.80, 1.45);
        public static readonly VehicleSpec Bus = new("bus", "אוטובוס", 2.55, 3.10);
        // The bike glyph is a SIDE view (two wheels), so its drawn width is the
        // wheelbase, not the handlebar width — 1.40 m keeps the box honest.
        public static readonly VehicleSpec Bike = new("bike", "אופניים", 1.40, 1.10);

        /// <summary>The two approved office car elevations supplied by Mahod.</summary>
        public enum OfficeCarView
        {
            Front,
            Rear,
        }

        /// <summary>
        /// Audited Civil section-view convention used by the Mahod office blocks.
        /// The section is viewed looking along the sample line: plan traffic flowing
        /// along the alignment is therefore shown from the rear, while traffic
        /// flowing against the alignment is shown from the front.  Left/right (the
        /// sign of a section offset) has no role in this mapping.
        /// </summary>
        public const string OfficeCarViewingConvention =
            "civil-section-view-v1;along-alignment=rear;against-alignment=front";

        /// <summary>
        /// Maps only a resolved, directed traffic-flow fact to an approved office
        /// elevation. Unknown direction deliberately has no car view.
        /// </summary>
        public static bool TryOfficeCarViewForFlow(
            TrafficDirectionEvidenceLogic.RelativeFlow flow,
            out OfficeCarView view)
        {
            switch (flow)
            {
                case TrafficDirectionEvidenceLogic.RelativeFlow.AlongAlignment:
                    view = OfficeCarView.Rear;
                    return true;
                case TrafficDirectionEvidenceLogic.RelativeFlow.AgainstAlignment:
                    view = OfficeCarView.Front;
                    return true;
                default:
                    view = default;
                    return false;
            }
        }

        /// <summary>Which vehicle stands on a strip with this Hebrew name, if any.</summary>
        public static VehicleSpec? VehicleForStrip(string stripLabel) => stripLabel switch
        {
            "נתיב נסיעה" => Car,
            // A wide curb-to-curb envelope may be source-proven as a composite
            // carriageway even when the plan supplies no internal lane boundaries.
            // New PLANs retain separate source-arrow tracks within this envelope;
            // never fabricate dimension marks merely to obtain several cars.
            "נתיבי נסיעה" => Car,
            "נת\"צ" => Bus,
            "נתיב אופניים" => Bike,
            "שביל אופניים" => Bike,
            _ => null,
        };

        /// <summary>A vehicle needs its width plus working clearance inside the strip.</summary>
        /// <summary>Working clearance a vehicle needs beside its body inside one strip.</summary>
        public const double VehicleClearanceM = 0.40;

        /// <summary>
        /// One named vehicle strip wider than this is several lanes drawn without their
        /// internal boundaries; the tool refuses to collapse them into one vehicle.
        /// </summary>
        public const double MaxSingleVehicleStripWidthM = 6.5;

        /// <summary>Narrowest strip that can credibly carry the vehicle.</summary>
        public static double MinimumStripWidthM(VehicleSpec spec) => spec.WidthM + VehicleClearanceM;

        public static bool FitsStrip(VehicleSpec spec, double stripWidthM) =>
            stripWidthM >= MinimumStripWidthM(spec);

        /// <summary>
        /// Fits an approved office elevation to the real car width without changing
        /// the block's aspect ratio.  The supplied DWGs are drawings, not a declared
        /// 1.80 x 1.45 m engineering envelope; forcing both dimensions independently
        /// stretches the artwork.  Refuse implausible source geometry rather than
        /// silently producing a flattened or elongated vehicle.
        /// </summary>
        public static bool TryOfficeBlockPhysicalHeight(
            double sourceWidth,
            double sourceHeight,
            double targetWidth,
            out double targetHeight)
        {
            targetHeight = double.NaN;
            if (!Finite(sourceWidth) || !Finite(sourceHeight) || !Finite(targetWidth) ||
                sourceWidth <= 1e-9 || sourceHeight <= 1e-9 || targetWidth <= 1e-9)
                return false;

            var height = targetWidth * sourceHeight / sourceWidth;
            // Both Natalie-approved car elevations resolve inside this generous
            // passenger-vehicle range (rear 1.896 m, front 1.580 m at 1.80 m wide).
            // A different result means the embedded definition was not the audited
            // 2D car elevation and must fail closed.
            if (!Finite(height) || height < 1.20 || height > 2.20)
                return false;

            targetHeight = height;
            return true;
        }

        /// <summary>
        /// The vehicle silhouette as closed polylines plus circles (wheels), in metres,
        /// origin at ground level mid-vehicle. The section view maps these through its
        /// own offset/elevation transform, so vertical exaggeration — if the view has
        /// any — applies to the vehicle exactly as it applies to the road surface.
        /// </summary>
        public static (IReadOnlyList<IReadOnlyList<(double X, double Y)>> Loops,
                       IReadOnlyList<(double X, double Y, double R)> Circles)
            VehicleGeometry(VehicleSpec spec)
        {
            switch (spec.Key)
            {
                case "car":
                    return (new[]
                    {
                        (IReadOnlyList<(double, double)>)new (double, double)[]
                        {
                            (-0.90, 0.25), (0.90, 0.25), (0.90, 0.80), (0.55, 0.80),
                            (0.45, 1.40), (-0.45, 1.40), (-0.55, 0.80), (-0.90, 0.80),
                        },
                    },
                    new[] { (-0.55, 0.16, 0.16), (0.55, 0.16, 0.16) });

                case "bus":
                    return (new[]
                    {
                        (IReadOnlyList<(double, double)>)new (double, double)[]
                        {
                            (-1.275, 0.35), (1.275, 0.35), (1.275, 2.90), (1.10, 3.05),
                            (-1.10, 3.05), (-1.275, 2.90),
                        },
                    },
                    new[] { (-0.85, 0.22, 0.22), (0.85, 0.22, 0.22) });

                default: // bike
                    return (new[]
                    {
                        (IReadOnlyList<(double, double)>)new (double, double)[]
                        { (-0.35, 0.30), (0.00, 0.75), (0.35, 0.30) },              // frame
                        new (double, double)[] { (0.00, 0.75), (-0.12, 1.00) },      // seat post
                        new (double, double)[] { (0.35, 0.30), (0.28, 1.05) },       // handlebar
                    },
                    new[] { (-0.35, 0.30, 0.30), (0.35, 0.30, 0.30) });
            }
        }

        // ------------------------------------------------------- label ladder

        /// <summary>
        /// One shared ladder for every rotated text hanging under the section grid
        /// (utility labels AND offset digits): anchors closer than <paramref
        /// name="minSepM"/> get pushed right until nothing overlaps. The marker
        /// line/tick stays at the TRUE position — only the text slides (live
        /// screenshots, 31/08: a lighting label printed over an offset digit).
        /// Returns the adjusted X per input index.
        /// </summary>
        public static double[] LabelLadder(IReadOnlyList<double> anchors, double minSepM = 1.4)
        {
            var result = new double[anchors.Count];
            var byX = Enumerable.Range(0, anchors.Count).OrderBy(i => anchors[i]).ToList();
            var prev = double.NegativeInfinity;
            foreach (var i in byX)
            {
                var x = Math.Max(anchors[i], prev + minSepM);
                result[i] = x;
                prev = x;
            }
            return result;
        }

        /// <summary>
        /// Places label centres inside a finite SectionView offset interval while
        /// preserving order and the requested occupied half-width/gap. Crowding that
        /// cannot fit returns false; a label is never silently pushed out of view.
        /// </summary>
        public static bool TryBoundedLabelLadder(
            IReadOnlyList<double> anchors,
            IReadOnlyList<double> occupiedHalfWidths,
            double minOffset,
            double maxOffset,
            double minimumGap,
            out double[] positions,
            out string error)
        {
            positions = new double[anchors.Count];
            error = string.Empty;
            if (anchors.Count != occupiedHalfWidths.Count ||
                !double.IsFinite(minOffset) || !double.IsFinite(maxOffset) ||
                maxOffset <= minOffset || !double.IsFinite(minimumGap) || minimumGap < 0 ||
                anchors.Any(a => !double.IsFinite(a)) ||
                occupiedHalfWidths.Any(w => !double.IsFinite(w) || w <= 0))
            {
                error = "bounded label-ladder inputs are invalid";
                return false;
            }
            if (anchors.Count == 0) return true;

            var order = Enumerable.Range(0, anchors.Count)
                .OrderBy(i => anchors[i])
                .ThenBy(i => i)
                .ToList();
            var first = order[0];
            positions[first] = Math.Max(anchors[first], minOffset + occupiedHalfWidths[first]);
            for (var rank = 1; rank < order.Count; rank++)
            {
                var previous = order[rank - 1];
                var current = order[rank];
                var separation = occupiedHalfWidths[previous] +
                                 occupiedHalfWidths[current] + minimumGap;
                positions[current] = Math.Max(
                    anchors[current], positions[previous] + separation);
            }

            var last = order[^1];
            positions[last] = Math.Min(
                positions[last], maxOffset - occupiedHalfWidths[last]);
            for (var rank = order.Count - 2; rank >= 0; rank--)
            {
                var current = order[rank];
                var next = order[rank + 1];
                var separation = occupiedHalfWidths[current] +
                                 occupiedHalfWidths[next] + minimumGap;
                positions[current] = Math.Min(
                    positions[current], positions[next] - separation);
            }

            if (positions[first] - occupiedHalfWidths[first] < minOffset - 1e-9 ||
                positions[last] + occupiedHalfWidths[last] > maxOffset + 1e-9)
            {
                positions = Array.Empty<double>();
                error = "labels cannot fit inside the SectionView offset bounds";
                return false;
            }
            return true;
        }

        /// <summary>Hebrew descenders (ק ן ף ץ) reach this fraction of the text height below the baseline.</summary>
        public const double RotatedLabelDescenderFactor = 0.30;

        /// <summary>Ink-to-ink clearance between neighbouring bottom texts; the measured layout demands the same.</summary>
        public const double RotatedLabelClearanceFactor = 0.30;

        /// <summary>
        /// Spaces the rotated (-90°) texts under the section grid by the column their
        /// ink really occupies. A text placed at offset p is inserted <paramref
        /// name="rightShift"/> drawing units to the right of p, its glyphs grow toward
        /// +X for one text height and descenders reach back 0.30 h, so its ink is
        /// [p + shift - 0.30h, p + shift + h]. Neighbouring columns keep 0.30 h of clear
        /// paper. The previous ladder reserved 2(h + 0.50) + 0.20 drawing units per
        /// text — 2.44 m for a 0.62 digit — so the 25 curb/lane faces of a 50.6 m urban
        /// section (6422 STA-12145, 29.09.2026) could not be applied at all. Returns the
        /// placed offset per input; every placed offset and every ink column stays
        /// inside [offMin, offMax]. Crowding that cannot fit still returns false.
        /// </summary>
        public static bool TryRotatedBottomLabelLadder(
            IReadOnlyList<double> offsets,
            IReadOnlyList<double> heights,
            double xUnitsPerOffset,
            double rightShift,
            double offMin,
            double offMax,
            out double[] placed,
            out string error)
        {
            placed = Array.Empty<double>();
            if (offsets.Count != heights.Count ||
                !double.IsFinite(xUnitsPerOffset) || xUnitsPerOffset <= 1e-9 ||
                !double.IsFinite(rightShift) || rightShift < 0 ||
                heights.Any(h => !double.IsFinite(h) || h <= 0))
            {
                error = "rotated bottom-label inputs are invalid";
                return false;
            }
            if (offsets.Count == 0)
            {
                error = string.Empty;
                return true;
            }

            // Everything below is in offset metres, converted through the live view scale.
            var inkCentre = heights.Select(h =>
                (rightShift + h * (1.0 - RotatedLabelDescenderFactor) / 2.0) / xUnitsPerOffset).ToList();
            var half = heights.Select(h =>
                h * (1.0 + RotatedLabelDescenderFactor) / 2.0 / xUnitsPerOffset).ToList();
            var gap = heights.Max() * RotatedLabelClearanceFactor / xUnitsPerOffset;
            // The ink may sit right of its placed offset; the placed offset itself must
            // also stay inside the view (VERIFY re-reads it), so the left bound grows by
            // whatever part of the insertion shift the ink column does not already cover.
            var leftPad = Enumerable.Range(0, heights.Count)
                .Select(i => Math.Max(0.0, inkCentre[i] - half[i])).Max();
            if (!TryCentredLabelLadder(
                    offsets.Select((offset, i) => offset + inkCentre[i]).ToList(),
                    half, offMin + leftPad, offMax, gap, out var centres, out error))
                return false;
            placed = centres.Select((centre, i) => centre - inkCentre[i]).ToArray();
            return true;
        }

        /// <summary>
        /// Order-preserving label placement with the least total squared displacement:
        /// a crowded group spreads to BOTH sides of its anchors instead of being pushed
        /// only rightwards (a forward-only ladder printed STA-12145's 0.94 at 4.78).
        /// With y = anchor − cumulative separation, any non-decreasing y keeps every
        /// neighbour at least its separation apart; the isotonic (pool-adjacent-
        /// violators) fit of y is the closest such arrangement, and clamping it into
        /// the view keeps it optimal. Crowding that cannot fit returns false.
        /// </summary>
        public static bool TryCentredLabelLadder(
            IReadOnlyList<double> anchors,
            IReadOnlyList<double> occupiedHalfWidths,
            double minOffset,
            double maxOffset,
            double minimumGap,
            out double[] positions,
            out string error)
        {
            positions = new double[anchors.Count];
            error = string.Empty;
            if (anchors.Count != occupiedHalfWidths.Count ||
                !double.IsFinite(minOffset) || !double.IsFinite(maxOffset) ||
                maxOffset <= minOffset || !double.IsFinite(minimumGap) || minimumGap < 0 ||
                anchors.Any(a => !double.IsFinite(a)) ||
                occupiedHalfWidths.Any(w => !double.IsFinite(w) || w <= 0))
            {
                error = "bounded label-ladder inputs are invalid";
                return false;
            }
            if (anchors.Count == 0) return true;

            var order = Enumerable.Range(0, anchors.Count)
                .OrderBy(i => anchors[i])
                .ThenBy(i => i)
                .ToList();
            var cumulative = new double[order.Count];
            for (var rank = 1; rank < order.Count; rank++)
                cumulative[rank] = cumulative[rank - 1] +
                    occupiedHalfWidths[order[rank - 1]] + occupiedHalfWidths[order[rank]] + minimumGap;

            var lower = minOffset + occupiedHalfWidths[order[0]];
            var upper = maxOffset - occupiedHalfWidths[order[^1]] - cumulative[^1];
            if (lower > upper + 1e-9)
            {
                positions = Array.Empty<double>();
                error = "labels cannot fit inside the SectionView offset bounds";
                return false;
            }

            // Pool adjacent violators over y = anchor - cumulative separation.
            var blockSum = new List<double>();
            var blockCount = new List<int>();
            for (var rank = 0; rank < order.Count; rank++)
            {
                blockSum.Add(anchors[order[rank]] - cumulative[rank]);
                blockCount.Add(1);
                while (blockSum.Count > 1 &&
                       blockSum[^2] / blockCount[^2] > blockSum[^1] / blockCount[^1])
                {
                    blockSum[^2] += blockSum[^1];
                    blockCount[^2] += blockCount[^1];
                    blockSum.RemoveAt(blockSum.Count - 1);
                    blockCount.RemoveAt(blockCount.Count - 1);
                }
            }
            var next = 0;
            for (var block = 0; block < blockSum.Count; block++)
            {
                var y = Math.Min(Math.Max(blockSum[block] / blockCount[block], lower), upper);
                for (var k = 0; k < blockCount[block]; k++, next++)
                    positions[order[next]] = y + cumulative[next];
            }
            return true;
        }

        // --------------------------------------------- design surface selection

        /// <summary>
        /// The DESIGN surface for an alignment, by name — never "the first surface
        /// that is not existing ground". That guess grabbed bottom/other-axis
        /// surfaces on wide legacy sample lines and produced 10-million-m³ earthworks
        /// (live, 31/08). Preference: this alignment's *DESIGN*FINAL*, then its
        /// *DESIGN*; null when neither is uniquely proven. A different alignment's
        /// design surface is never a fallback.
        /// </summary>
        public static string? PickDesignSurface(IEnumerable<string> names, string? alignment)
        {
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var choice = SectionSourceSelectionLogic.SelectDesign(list, alignment);
            return choice.IsSelected ? choice.Name : null;
        }

        /// <summary>
        /// A road cross-section that claims to move more than this much material per
        /// metre of road is a sampling artefact, not earthworks. ±31 m swath × ~24 m
        /// of depth is already generous.
        /// </summary>
        public const double MaxPlausibleCutFillAreaM2 = 1500.0;

        // ------------------------------------------- section point normalization

        /// <summary>
        /// Civil's SectionPoint.Location is expected in section coordinates
        /// (X=offset, Y=elevation), but the tool refuses to bet a drawing on an API
        /// convention: points are accepted only when their X span sits inside the
        /// view's real offset window and Y inside a plausible elevation band.
        /// Anything else returns empty and the caller draws nothing rather than
        /// drawing wrong.
        /// </summary>
        public static List<(double Offset, double Elevation)> NormalizeSectionPoints(
            IReadOnlyList<(double X, double Y, double Z)> raw,
            double offMin, double offMax, double elevMin, double elevMax)
        {
            if (raw.Count < 2) return new List<(double, double)>();
            var margin = Math.Max(5.0, (offMax - offMin) * 0.25);
            var inWindow = raw.Count(p =>
                p.X >= offMin - margin && p.X <= offMax + margin &&
                p.Y >= elevMin - 20 && p.Y <= elevMax + 20);
            if (inWindow < raw.Count * 0.8) return new List<(double, double)>();

            return raw
                .Where(p => p.X >= offMin - margin && p.X <= offMax + margin)
                .Select(p => (p.X, p.Y))
                .OrderBy(p => p.X)
                .ToList();
        }

        // ---------------------------------------------------------- cross slopes

        /// <summary>
        /// Proven design-surface grade across one labelled strip. Endpoint elevations
        /// are interpolated only inside the sampled section chain; extrapolation is
        /// deliberately refused. At a vertical curb face, the endpoint on the inside
        /// of the strip is used (right-hand value at <see cref="FromOffset"/>, left-hand
        /// value at <see cref="ToOffset"/>).
        /// </summary>
        public sealed record SlopeEvidence(
            double FromOffset,
            double ToOffset,
            double FromElevation,
            double ToElevation,
            double Percent);

        /// <summary>
        /// Computes a signed, real-metre cross slope from sampled section geometry.
        /// Returns false for malformed chains, short/reversed spans, non-finite values,
        /// ambiguous interpolation or any request outside the sampled domain.
        /// </summary>
        public static bool TrySlopeEvidence(
            IReadOnlyList<(double Offset, double Elevation)> chain,
            double fromOffset,
            double toOffset,
            out SlopeEvidence? evidence,
            double minSpanM = 0.5)
        {
            evidence = null;
            if (chain == null || chain.Count < 2 ||
                !Finite(fromOffset) || !Finite(toOffset) || !Finite(minSpanM) ||
                minSpanM <= 0 || toOffset - fromOffset < minSpanM)
                return false;

            if (chain.Any(p => !Finite(p.Offset) || !Finite(p.Elevation)))
                return false;

            // OrderBy is stable: Civil's original order through a vertical face is
            // retained among equal offsets, which lets the one-sided endpoint rule
            // select the strip interior rather than an arbitrary curb elevation.
            var ordered = chain.OrderBy(p => p.Offset).ToList();
            if (!TryElevationInside(ordered, fromOffset, towardRight: true, out var zFrom) ||
                !TryElevationInside(ordered, toOffset, towardRight: false, out var zTo))
                return false;

            var percent = (zTo - zFrom) * 100.0 / (toOffset - fromOffset);
            if (!Finite(percent)) return false;

            evidence = new SlopeEvidence(fromOffset, toOffset, zFrom, zTo, percent);
            return true;
        }

        /// <summary>Invariant DBText for a signed design cross slope.</summary>
        public static string FormatSlopePercent(double percent)
        {
            if (!Finite(percent))
                throw new ArgumentOutOfRangeException(nameof(percent), "Slope percent must be finite.");
            // Suppress a visually misleading "-0.00%" caused by floating-point noise.
            var display = Math.Abs(percent) < 0.005 ? 0.0 : percent;
            return display.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";
        }

        private static bool TryElevationInside(
            IReadOnlyList<(double Offset, double Elevation)> chain,
            double offset,
            bool towardRight,
            out double elevation)
        {
            elevation = double.NaN;
            const double tol = 1e-9;
            if (offset < chain[0].Offset - tol || offset > chain[^1].Offset + tol)
                return false;

            var exact = Enumerable.Range(0, chain.Count)
                .Where(i => Math.Abs(chain[i].Offset - offset) <= tol)
                .ToList();
            if (exact.Count > 0)
            {
                var index = towardRight ? exact[^1] : exact[0];
                elevation = chain[index].Elevation;
                return Finite(elevation);
            }

            for (var i = 1; i < chain.Count; i++)
            {
                var (x0, y0) = chain[i - 1];
                var (x1, y1) = chain[i];
                if (x0 >= offset || x1 <= offset) continue;
                var span = x1 - x0;
                if (span <= tol) return false;
                elevation = y0 + (y1 - y0) * (offset - x0) / span;
                return Finite(elevation);
            }

            return false;
        }

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
