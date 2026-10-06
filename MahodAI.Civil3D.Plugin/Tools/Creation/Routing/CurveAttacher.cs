using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Attaches `AddFixedLine` segments and curves (optionally Spiral-Curve-Spiral) to a Civil
    /// 3D alignment. Each interior PI gets a curve attempted at the requested <c>radius</c>; on
    /// infeasibility the radius is halved and retried, down to <c>rFloor</c>. When an SCS
    /// delegate is supplied, SCS is tried first at each radius step, and a plain arc is the
    /// fallback (matches FixAlignmentGeometryTool's behaviour). Every relaxation or fallback
    /// is recorded as a warning. Decoupled from Civil 3D via delegates so the loop is unit-testable.
    /// </summary>
    public static class CurveAttacher
    {
        public sealed class Warning
        {
            public int PiIndex { get; init; }
            public double RequestedM { get; init; }
            public double AchievedM { get; init; }
            public string Reason { get; init; } = "";
            /// <summary>"SCS" when a Spiral-Curve-Spiral was placed, "Arc" for a plain curve, "none" if nothing fit.</summary>
            public string PlacedAs { get; init; } = "Arc";
        }

        /// <summary>
        /// P0-03: separates the design policy from the attachment algorithm. In the default
        /// production policy (<see cref="Strict"/>) NONE of the relaxations are allowed — a PI
        /// that cannot take a curve at the requested radius/spiral is a hard failure, not a
        /// silent green result. <see cref="Relaxed"/> permits the historical behaviour but
        /// every deviation is recorded and flagged as requiring engineer approval.
        /// </summary>
        public sealed record HorizontalDesignPolicy(
            bool AllowRadiusRelaxation,
            bool AllowSpiralShortening,
            bool AllowArcFallback,
            bool AllowSharpPi)
        {
            /// <summary>Production: exact geometry or reject. No relaxation, no arc fallback, no sharp PI.</summary>
            public static HorizontalDesignPolicy Strict { get; } = new(false, false, false, false);

            /// <summary>Explicit opt-in: historical behaviour; every deviation is reported and needs approval.</summary>
            public static HorizontalDesignPolicy Relaxed { get; } = new(true, true, true, true);
        }

        public sealed class AttachResult
        {
            public bool Success { get; init; }
            public List<Warning> Warnings { get; init; } = new();
            public string? FailureReason { get; init; }
            public int ScsCount { get; init; }
            public int ArcCount { get; init; }

            /// <summary>
            /// True when at least one PI deviated from the requested geometry (radius relaxed,
            /// spiral shortened, SCS→arc fallback, or a sharp PI). In <see cref="HorizontalDesignPolicy.Strict"/>
            /// this can only be true together with <see cref="Success"/>==false.
            /// </summary>
            public bool HasDeviations { get; init; }

            /// <summary>True when a produced result is a relaxed candidate that a human engineer must approve.</summary>
            public bool RequiresEngineerApproval { get; init; }
        }

        /// <summary>Adds a fixed-line entity to the alignment and returns its uint entity id.</summary>
        public delegate int AddLineFn(Pt2 start, Pt2 end);

        /// <summary>Tries to add a free curve between two line entities at the given radius. Returns true on success.</summary>
        public delegate bool TryAddCurveFn(int prevLineId, int nextLineId, double radius);

        /// <summary>Tries to add a free Spiral-Curve-Spiral between two line entities at the given radius and per-side spiral length. Returns true on success.</summary>
        public delegate bool TryAddScsFn(int prevLineId, int nextLineId, double radius, double spiralLengthM);

        public static AttachResult Attach(
            Pt2[] pis,
            double radius,
            double rFloor,
            AddLineFn addLine,
            TryAddCurveFn tryAddCurve,
            TryAddScsFn? tryAddScs = null,
            double spiralLengthM = 0.0,
            HorizontalDesignPolicy? policy = null)
        {
            // Default is Relaxed for source back-compat; PRODUCTION callers pass Strict.
            policy ??= HorizontalDesignPolicy.Relaxed;

            if (pis == null || pis.Length < 2)
                return new AttachResult { Success = false, FailureReason = "Need at least 2 PIs" };
            if (radius <= 0)
                return new AttachResult { Success = false, FailureReason = "radius must be > 0" };

            // P0-03: in strict mode the floor IS the required minimum radius — the loop may
            // not go below it, so no silent radius relaxation is possible.
            if (!policy.AllowRadiusRelaxation)
                rFloor = radius;
            else if (rFloor <= 0 || rFloor > radius)
                rFloor = Math.Max(1.0, Math.Min(radius, rFloor <= 0 ? 30.0 : rFloor));

            var warnings = new List<Warning>();

            // Fixed lines for every segment
            var lineIds = new int[pis.Length - 1];
            for (int i = 0; i < pis.Length - 1; i++)
            {
                try
                {
                    lineIds[i] = addLine(pis[i], pis[i + 1]);
                }
                catch (Exception ex)
                {
                    return new AttachResult
                    {
                        Success = false,
                        FailureReason = $"AddFixedLine failed for segment {i}: {ex.Message}",
                        Warnings = warnings,
                    };
                }
            }

            // Free curves at each interior PI. When tryAddScs is supplied:
            //   1. Try SCS at the current radius with the requested spiral length.
            //   2. If that fails, halve the spiral length (down to ~25% of requested) and retry —
            //      Israeli interurban convention is 50 m, but lower-class roads accept 25 m or less,
            //      and a tight tangent that can't host (R=R, L=50) often hosts (R=R, L=25).
            //   3. Only if all spiral-length attempts fail, fall back to a plain Arc at the same radius.
            //   4. If Arc also fails, halve the radius and start over from step 1.
            // This dramatically reduces "Arc fallback" warnings on densified paths where
            // intermediate PIs sit between corridor curves with limited tangent length.
            int scsCount = 0;
            int arcCount = 0;
            int unplacedCount = 0;
            // P0-03: spiral shortening is gated. Strict → floor equals the requested length,
            // so the inner loop tries only the requested spiral and never shortens it.
            double minSpiralM = spiralLengthM > 0
                ? (policy.AllowSpiralShortening ? Math.Max(spiralLengthM * 0.25, 5.0) : spiralLengthM)
                : 0.0;
            bool scsRequired = tryAddScs != null && spiralLengthM > 0;
            bool mayUseArc = !scsRequired || policy.AllowArcFallback;
            for (int i = 0; i < lineIds.Length - 1; i++)
            {
                int piIndex = i + 1;   // 1-based index of the interior PI
                double r = radius;
                bool placed = false;
                while (r >= rFloor && !placed)
                {
                    if (tryAddScs != null && spiralLengthM > 0)
                    {
                        for (double L = spiralLengthM; L >= minSpiralM - 1e-9; L *= 0.5)
                        {
                            bool scsOk;
                            try { scsOk = tryAddScs(lineIds[i], lineIds[i + 1], r, L); }
                            catch { scsOk = false; }

                            if (scsOk)
                            {
                                placed = true;
                                scsCount++;
                                if (r < radius || L < spiralLengthM)
                                {
                                    string reason = (L < spiralLengthM && r < radius)
                                        ? $"SCS placed — radius relaxed to {r:F0}m and spiral shortened to {L:F0}m (requested {spiralLengthM:F0}m)"
                                        : (L < spiralLengthM)
                                            ? $"SCS placed at radius {r:F0}m with shortened spiral {L:F0}m (requested {spiralLengthM:F0}m)"
                                            : "SCS placed — radius relaxed for tangent length";
                                    warnings.Add(new Warning
                                    {
                                        PiIndex = piIndex,
                                        RequestedM = radius,
                                        AchievedM = r,
                                        PlacedAs = "SCS",
                                        Reason = reason,
                                    });
                                }
                                break;
                            }
                            // Stop the inner loop on the last permissible spiral length;
                            // halving further would go below minSpiralM.
                            if (L * 0.5 < minSpiralM - 1e-9) break;
                        }
                        if (placed) break;
                    }

                    // P0-03: when SCS is required, a plain-arc fallback is only attempted if
                    // the policy allows the SCS→arc downgrade. Strict production forbids it.
                    bool arcOk = false;
                    if (mayUseArc)
                    {
                        try { arcOk = tryAddCurve(lineIds[i], lineIds[i + 1], r); }
                        catch { arcOk = false; }
                    }

                    if (arcOk)
                    {
                        placed = true;
                        arcCount++;
                        if (tryAddScs != null && spiralLengthM > 0)
                        {
                            warnings.Add(new Warning
                            {
                                PiIndex = piIndex,
                                RequestedM = radius,
                                AchievedM = r,
                                PlacedAs = "Arc",
                                Reason = $"SCS rejected even at minimum spiral_length={minSpiralM:F0}m at radius {r:F0}m — fell back to plain arc",
                            });
                        }
                        else if (r < radius)
                        {
                            warnings.Add(new Warning
                            {
                                PiIndex = piIndex,
                                RequestedM = radius,
                                AchievedM = r,
                                PlacedAs = "Arc",
                                Reason = "tangent length insufficient — radius relaxed",
                            });
                        }
                    }
                    else
                    {
                        r *= 0.5;
                    }
                }
                if (!placed)
                {
                    unplacedCount++;
                    warnings.Add(new Warning
                    {
                        PiIndex = piIndex,
                        RequestedM = radius,
                        AchievedM = 0,
                        PlacedAs = "none",
                        Reason = policy.AllowSharpPi
                            ? $"no curve achievable above floor {rFloor:F0} m — PI left as sharp vertex"
                            : $"no valid curve at required radius {radius:F0} m (spiral {spiralLengthM:F0} m) — obligatory PI",
                    });
                }
            }

            // P0-03: a required PI with no valid curve is a HARD failure in any policy that
            // forbids sharp PIs (the production default). This is the case the review calls
            // out: "AttachResult.Success must be false when an obligatory PI has no valid
            // curve." Radius relaxation / spiral shortening / arc fallback that DID place a
            // curve are deviations — allowed only under an explicit relaxed policy, and always
            // flagged as requiring engineer approval so they can never hide in a green result.
            bool hasDeviations = warnings.Count > 0;
            if (!policy.AllowSharpPi && unplacedCount > 0)
            {
                return new AttachResult
                {
                    Success = false,
                    Warnings = warnings,
                    ScsCount = scsCount,
                    ArcCount = arcCount,
                    HasDeviations = true,
                    FailureReason =
                        $"{unplacedCount} obligatory PI(s) could not take a curve at the required " +
                        $"radius {radius:F0} m" + (spiralLengthM > 0 ? $"/spiral {spiralLengthM:F0} m" : "") +
                        " under the strict horizontal policy.",
                };
            }

            return new AttachResult
            {
                Success = true,
                Warnings = warnings,
                ScsCount = scsCount,
                ArcCount = arcCount,
                HasDeviations = hasDeviations,
                // Any deviation that survives into a successful result (only possible under a
                // relaxed policy) must be approved by an engineer.
                RequiresEngineerApproval = hasDeviations,
            };
        }
    }
}
