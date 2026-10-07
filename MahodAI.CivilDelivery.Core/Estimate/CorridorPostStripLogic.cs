using static MahodAI.CivilDelivery.Estimate.CorridorBotSurfaceLogic;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Isolated engineering-estimate candidate, NOT an approved method or a native measurement.
/// Metres / absolute elevations only. The caller proves the original source, units and scope.
/// A slope-equivalent debit is applied separately to each original ground segment:
/// deltaZ = depth * sqrt(1 + slope^2). This is NOT a joined normal-offset terrain,
/// nor the legacy whole-ground-part average factor. No joins, snapping or gap repair.
/// </summary>
public static class CorridorPostStripLogic
{
    public enum StrippingMethod { Unspecified = 0, PerGroundSegmentSlopeEquivalentDebit = 1 }
    public const string SlopeEquivalentMethodId = "post-strip/per-ground-segment-slope-equivalent-debit/avg-end-area/v1-candidate";
    public const string Assumption = "Engineering estimate requiring explicit method/depth approval; not surveyed terrain or a joined normal-offset surface; not legacy-identical.";

    public sealed record StationDebit(RegionKey Key, double StationM, double AreaM2, string RawReceiptRef);
    public sealed record PairDebit(RegionKey Key, double FromM, double ToM, double VolumeM3,
        double SignedBalanceResidualM3, string FromReceiptRef, string ToReceiptRef);
    // Do not expose the reused kernel's misleading 'complete-before-stripping' status for lowered geometry.
    public sealed record PostStripObservation(bool Complete, double? ObservedCutM3, double? ObservedFillM3,
        IReadOnlyList<StationResult> Stations, IReadOnlyList<IntegratedPair> Pairs, IReadOnlyList<Issue> Issues);
    public sealed record PostStripResult(Result GrossBefore, PostStripObservation? PostStripObserved,
        StrippingMethod SelectedMethod, string? MethodId, double? DepthM,
        IReadOnlyList<StationDebit> StationDebits, IReadOnlyList<PairDebit> PairDebits, IReadOnlyList<Issue> Issues)
    {
        // Refusal evidence is JSON-safe: never replace a non-finite requested depth with numeric zero.
        public string? InvalidRequestedDepth { get; init; }
        public bool Complete => GrossBefore.Complete && PostStripObserved?.Complete == true && Issues.Count == 0;
        public bool EngineeringApproved => false;
        public string Status => Complete ? "complete-observed-post-strip-assumption-unapproved" : "partial-or-unavailable-post-strip-unapproved";
        public double? ObservedStrippingM3 => PairDebits.Count == 0 ? null : FiniteTotal(PairDebits.Sum(p => p.VolumeM3));
        private static double? FiniteTotal(double value) => double.IsFinite(value) ? value : null;
    }

    public static PostStripResult MeasureMetres(IReadOnlyList<Schedule> schedules, IReadOnlyList<StationInput> inputs,
        double depthM, StrippingMethod method, double maxStationGapM)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(inputs);
        var gross = CorridorBotSurfaceLogic.MeasureMetres(schedules, inputs, maxStationGapM);
        var issues = new List<Issue>();
        var stationDebits = new List<StationDebit>();
        var pairDebits = new List<PairDebit>();
        string? methodId = method == StrippingMethod.PerGroundSegmentSlopeEquivalentDebit ? SlopeEquivalentMethodId : null;
        if (methodId is null) issues.Add(new("explicit-stripping-method-required", null, null, "No default interpretation is selected."));
        if (!double.IsFinite(depthM) || depthM < 0) issues.Add(new("invalid-stripping-depth", null, null, "A finite nonnegative depth in metres is required."));
        if (issues.Count > 0)
            return new(gross, null, method, methodId, double.IsFinite(depthM) ? depthM : null, stationDebits, pairDebits, issues)
            {
                InvalidRequestedDepth = double.IsFinite(depthM) ? null : depthM.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            };

        var eligible = gross.Stations.Where(s => s.Complete).ToDictionary(s => (s.Key, s.StationM));
        var shifted = new List<StationInput>(inputs.Count);
        foreach (var input in inputs)
        {
            // Validation is reused BEFORE modification: malformed parts cannot be repaired by splitting.
            // Missing/duplicate observations and failed native reads remain precisely as supplied.
            if (!eligible.TryGetValue((input.Key, input.StationM), out var valid)) { shifted.Add(input); continue; }
            if (depthM == 0)
            {
                // Exact identity, including extreme but already valid geometry; no needless slope arithmetic.
                stationDebits.Add(new(input.Key, input.StationM, 0, input.RawReceiptRef));
                shifted.Add(input);
                continue;
            }
            var parts = new List<Part>();
            double area = 0;
            bool failed = false;
            foreach (var part in input.GroundParts)
            {
                for (var i = 1; i < part.Points.Count; i++)
                {
                    var a = part.Points[i - 1]; var b = part.Points[i];
                    var dx = b.OffsetM - a.OffsetM;
                    var dz = b.ElevationM - a.ElevationM;
                    var scale = Math.Max(Math.Abs(dx), Math.Abs(dz));
                    var length = scale * Math.Sqrt((dx / scale) * (dx / scale) + (dz / scale) * (dz / scale));
                    var lowering = depthM * (length / dx);
                    var za = a.ElevationM - lowering; var zb = b.ElevationM - lowering;
                    if (!double.IsFinite(dx) || !double.IsFinite(dz) || !double.IsFinite(lowering) ||
                        !double.IsFinite(za) || !double.IsFinite(zb)) { failed = true; break; }
                    parts.Add(new(new[] { new CorridorBotSurfaceLogic.Point(a.OffsetM, za), new CorridorBotSurfaceLogic.Point(b.OffsetM, zb) }));
                    // Envelope cells are disjoint and cover only the proven Bot domain union.
                    foreach (var cell in valid.Envelope)
                    {
                        var width = Math.Max(0, Math.Min(b.OffsetM, cell.B.OffsetM) - Math.Max(a.OffsetM, cell.A.OffsetM));
                        area += width * lowering;
                    }
                    if (!double.IsFinite(area)) { failed = true; break; }
                }
                if (failed) break;
            }
            if (failed)
            {
                var detail = "Non-finite segment debit; original geometry retained, post-strip station unavailable.";
                issues.Add(new("stripping-overflow", input.Key, input.StationM, detail));
                shifted.Add(input with { ReadSucceeded = false, ReadFailure = detail });
                continue;
            }
            stationDebits.Add(new(input.Key, input.StationM, area, input.RawReceiptRef));
            shifted.Add(input with { GroundParts = parts });
        }
        var post = CorridorBotSurfaceLogic.MeasureMetres(schedules, shifted, maxStationGapM);
        var beforePairs = gross.Pairs.ToDictionary(p => (p.Key, p.FromM, p.ToM));
        var debits = stationDebits.ToDictionary(s => (s.Key, s.StationM));
        foreach (var pair in post.Pairs)
        {
            if (!beforePairs.TryGetValue((pair.Key, pair.FromM, pair.ToM), out var before) ||
                !debits.TryGetValue((pair.Key, pair.FromM), out var a) || !debits.TryGetValue((pair.Key, pair.ToM), out var b))
            {
                issues.Add(new("post-strip-coverage-upgrade", pair.Key, pair.ToM, "A post-strip interval lacks the same original proven interval; result is unusable."));
                continue;
            }
            var volume = (a.AreaM2 / 2 + b.AreaM2 / 2) * (pair.ToM - pair.FromM);
            var expected = before.CutM3 - before.FillM3 - volume;
            var residual = pair.CutM3 - pair.FillM3 - expected;
            var tolerance = 1e-8 * Math.Max(1, Math.Max(volume, Math.Max(before.CutM3 + before.FillM3, pair.CutM3 + pair.FillM3)));
            if (!double.IsFinite(volume) || !double.IsFinite(residual))
            {
                issues.Add(new("stripping-volume-overflow", pair.Key, pair.ToM, "Non-finite strip debit or signed-balance residual."));
                continue;
            }
            pairDebits.Add(new(pair.Key, pair.FromM, pair.ToM, volume, residual, pair.FromReceiptRef, pair.ToReceiptRef));
            if (Math.Abs(residual) > tolerance)
                issues.Add(new("stripping-balance-mismatch", pair.Key, pair.ToM, "Local signed balance does not match the independently integrated segment debit."));
        }
        if (pairDebits.Count > 0 && !double.IsFinite(pairDebits.Sum(p => p.VolumeM3)))
            issues.Add(new("stripping-total-overflow", null, null, "Strip debit total overflow; interval receipts remain available."));
        var observation = new PostStripObservation(post.Complete && issues.Count == 0, post.ObservedCutM3,
            post.ObservedFillM3, post.Stations, post.Pairs, post.Issues);
        return new(gross, observation, method, methodId, depthM, stationDebits, pairDebits, issues);
    }
}
