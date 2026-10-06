using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Alignment
{
    /// <summary>
    /// Computes required superelevation rate based on Israeli road design standards
    /// (Table 5.5/5.6). Ported from MahodCivilNet NatiSE.
    /// </summary>
    public class ComputeSuperelevationRateTool : DrawingToolBase
    {
        public override string Name => "compute_superelevation_rate";
        public override string Description => "Computes required superelevation rate based on Israeli road design standards (Table 5.5/5.6) given design speed and curve radius. Returns standard-based e(max), minimum radius, and computed superelevation.";
        public override string Category => ToolCategories.Alignment;

        // Israeli standard Table 5.5 data: (Vd km/h, Emax, F, Gamma, Rmax)
        private static readonly (int Speed, double Emax, double F, double Gamma, double Rmax)[] Table55 =
        {
            (30, 0.06, 0.19, 0.0, 30),
            (40, 0.06, 0.17, 0.0, 55),
            (50, 0.06, 0.16, 0.0, 85),
            (60, 0.06, 0.15, 0.0, 130),
            (70, 0.07, 0.14, 0.0, 175),
            (80, 0.07, 0.13, 0.0, 250),
            (90, 0.07, 0.12, 0.0, 340),
            (100, 0.07, 0.11, 0.0, 460),
            (110, 0.07, 0.09, 0.0, 700),
            (120, 0.07, 0.08, 0.0, 950)
        };

        // Israeli standard Table 5.6 data: (Vd km/h, Delta, C)
        private static readonly (int Speed, double Delta, double C)[] Table56 =
        {
            (30, 0.56, 0.4), (40, 0.50, 0.4), (50, 0.44, 0.4),
            (60, 0.38, 0.4), (70, 0.32, 0.4), (80, 0.26, 0.4),
            (90, 0.22, 0.4), (100, 0.19, 0.4), (110, 0.17, 0.4),
            (120, 0.15, 0.4)
        };

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var designSpeed = GetIntParam(parameters, "design_speed_kph")
                ?? throw new ArgumentException("design_speed_kph is required");
            var radius = GetDoubleParam(parameters, "radius");

            // Find matching row in Table 5.5 (use next higher speed if not exact)
            var row = Table55.FirstOrDefault(r => designSpeed <= r.Speed);
            if (row == default)
                row = Table55[Table55.Length - 1]; // Use highest speed row

            var row56 = Table56.FirstOrDefault(r => designSpeed <= r.Speed);
            if (row56 == default)
                row56 = Table56[Table56.Length - 1];

            // Calculate minimum radius: R_min = V^2 / (127 * (emax + f))
            double minRadius = Math.Ceiling(
                (double)(row.Speed * row.Speed) /
                (127.0 * (row.Emax * (1.0 + row.Gamma) + row.F - row.Emax * row.Gamma)));

            var result = new Dictionary<string, object>
            {
                ["design_speed_kph"] = designSpeed,
                ["standard_row_speed"] = row.Speed,
                ["emax"] = row.Emax,
                ["f_coefficient"] = row.F,
                ["gamma"] = row.Gamma,
                ["min_radius_m"] = minRadius
            };

            if (radius.HasValue && radius.Value > 0)
            {
                double r = radius.Value;
                result["radius_m"] = Math.Round(r, 1);

                if (r < minRadius)
                {
                    result["status"] = "below_minimum";
                    result["required_superelevation"] = row.Emax;
                    result["note"] = $"Radius {r:F1}m is below minimum {minRadius}m for {row.Speed} km/h";
                }
                else
                {
                    // e = 1/(1+gamma) * (V^2/(127*R) - f + emax*gamma)
                    double e = (1.0 / (1.0 + row.Gamma)) *
                        ((double)(row.Speed * row.Speed) / (127.0 * r) - row.F + row.Emax * row.Gamma);

                    // Clamp to [normal crown slope, emax]
                    double normalSlope = -0.02; // Standard 2% normal crown
                    e = Math.Max(normalSlope, Math.Min(row.Emax, e));

                    result["status"] = e <= normalSlope + 0.001 ? "normal_crown" : "superelevated";
                    result["required_superelevation"] = Math.Round(e, 4);
                    result["superelevation_percent"] = Math.Round(e * 100, 2);
                }

                // Spiral length calculation: Ls = Delta * V^3 / (C * R)
                if (row56 != default)
                {
                    double ls = row56.Delta * Math.Pow(row.Speed, 3) / (row56.C * r);
                    result["recommended_spiral_length_m"] = Math.Round(Math.Max(ls, 30.0), 1); // Min 30m
                }
            }

            return ToolResult.Ok(result);
        }
    }
}
