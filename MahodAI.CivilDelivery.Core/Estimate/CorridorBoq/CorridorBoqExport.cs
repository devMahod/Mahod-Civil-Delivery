using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq
{
    /// <summary>
    /// The corridor bill (Natali 30.09.2026) from the tool's own measurement: per corridor — cut / fill from the kernel
    /// (CorridorBotSurfaceLogic, before stripping), the plan widths of cut and fill and the 3D factor of the ground (the
    /// stripping of MahodCivilNet CalcSectionVolumes3: plan area 3D × depth), and per corridor code the volume (average end
    /// area of Civil's shape areas) and the plan area (average end of the shape width). Only intervals between two
    /// consecutive, successfully read stations of one declared region are integrated; everything else is an issue on the
    /// corridor, which is then not complete. The workbook writes every quantity as a formula over the measurement sheet.
    /// </summary>
    public static class CorridorBoqExport
    {
        public sealed record CorridorMeasure(
            string CorridorId, bool Complete, IReadOnlyList<string> Issues, int Stations, double LengthM,
            double VolCut, double VolFill, double PlanCut, double PlanFill, double Plan3DCut, double Plan3DFill,
            IReadOnlyDictionary<string, double> CodeVolume, IReadOnlyDictionary<string, double> CodePlanArea)
        {
            /// <summary>Plan area of the union of all asphalt layers (CorridorMaterialLogic) — the prime-coat area.</summary>
            public double AsphaltPlanArea { get; init; }
            /// <summary>consistent / failed / unknown / not_applicable — vertical order and contact of the asphalt interfaces.</summary>
            public string InterfaceStatus { get; init; } = "unknown";
            /// <summary>Cut / fill / stripping of this corridor are totalled only when its earthworks are complete.</summary>
            public bool EarthworksComplete { get; init; }
            /// <summary>Base courses and asphalt of this corridor are totalled only when its materials are complete.</summary>
            public bool MaterialsComplete { get; init; }
            /// <summary>Cut after stripping (decision WEST-08): against the existing ground lowered per ground segment by the
            /// stripping depth (CorridorPostStripLogic, method <see cref="StripMethodId"/>); null when not available.</summary>
            public double? PostCut { get; init; }
            /// <summary>Fill after stripping, same method; null when not available.</summary>
            public double? PostFill { get; init; }
            /// <summary>Stripped volume of the same method inside the Bot envelope; null when not available.</summary>
            public double? StripDebit { get; init; }
            /// <summary>The stripping depth (m) the tool computed with — another depth needs a new measurement.</summary>
            public double StripDepthM { get; init; }
            /// <summary>The named estimating method of the stripping (an assumption, not a surveyed surface).</summary>
            public string? StripMethodId { get; init; }
        }

        public sealed record Export(
            CorridorBoqRuleset Rules, IReadOnlyList<CorridorMeasure> Measures, IReadOnlyList<(string Corridor, string Reason)> Skipped,
            IReadOnlyList<CorridorVolumeTable> DrawingTables, IReadOnlyList<string> Evidence, IReadOnlyList<string> Notes)
        {
            public bool Complete => Measures.Count > 0 && Measures.All(m => m.Complete) && Skipped.Count == 0;
            public int Corridors => Measures.Count;
        }

        public sealed record Context(DateTime Now, string DrawingPath, string DrawingSha256, string RawReceiptPath, string RawReceiptSha256);

        public static Export Build(CorridorBoqRuleset rules, IReadOnlyList<CorridorShapeStation> shapes,
            IReadOnlyList<CorridorBotSurfaceLogic.Schedule> schedules, IReadOnlyList<CorridorBotSurfaceLogic.StationInput> inputs,
            CorridorBotSurfaceLogic.Result earthworks, IReadOnlyList<(string Corridor, string Reason)> skipped,
            IReadOnlyList<CorridorVolumeTable> tables, IReadOnlyList<string> evidence,
            IReadOnlyList<(string CorridorId, string Issue)>? regionIssues = null,
            IReadOnlyList<(string CorridorId, string Issue)>? corridorBlocks = null)
        {
            var notes = new List<string>();
            regionIssues ??= new List<(string, string)>();
            corridorBlocks ??= new List<(string, string)>();
            var measures = new List<CorridorMeasure>();
            var inputByKey = inputs.GroupBy(i => (i.Key, i.StationM)).ToDictionary(g => g.Key, g => g.First());
            var stationByKey = earthworks.Stations.GroupBy(s => (s.Key, s.StationM)).ToDictionary(g => g.Key, g => g.First());
            var materials = CorridorMaterialLogic.Measure(schedules, shapes, rules, 50.0);
            static bool Advisory(string code) => code is "asphalt-order-unknown" or "unmapped-material-code" ||
                code.StartsWith("asphalt-interface-", StringComparison.Ordinal);
            var globalEarthworks = earthworks.Issues.Where(i => i.Key is null)
                .Select(i => $"{i.Code}: {i.Detail}").ToList();
            var globalMaterialFailures = materials.Issues.Where(i => i.Key is null && !Advisory(i.Code))
                .Select(i => $"material {i.Code}: {i.Detail}").ToList();
            var orderUnknown = materials.Issues.Any(i => i.Key is null && i.Code == "asphalt-order-unknown");
            // Earthworks after stripping (decision WEST-08; Codex CorridorPostStripLogic 48F3D1A3): an explicit, named
            // estimating method over the same inputs. Its own findings make the corridor's earthworks incomplete; the findings
            // it repeats from the measurement before stripping are not listed twice.
            var post = CorridorPostStripLogic.MeasureMetres(schedules, inputs, rules.HisufDepthM,
                CorridorPostStripLogic.StrippingMethod.PerGroundSegmentSlopeEquivalentDebit, 50.0);
            var grossIssueKeys = earthworks.Issues.Select(i => (i.Code, i.Key, i.StationM)).ToHashSet();
            var postGlobal = post.Issues.Where(i => i.Key is null)
                .Concat(post.PostStripObserved?.Issues.Where(i => i.Key is null && !grossIssueKeys.Contains((i.Code, i.Key, i.StationM)))
                    ?? Enumerable.Empty<CorridorBotSurfaceLogic.Issue>())
                .Select(i => $"post-strip {i.Code}: {i.Detail}").ToList();
            foreach (var corridor in schedules.Select(s => s.Key.CorridorId).Concat(regionIssues.Select(r => r.CorridorId)).Distinct(StringComparer.Ordinal))
            {
                var issues = regionIssues.Where(r => r.CorridorId == corridor).Select(r => r.Issue).Concat(earthworks.Issues.Where(i => i.Key?.CorridorId == corridor)
                    .Select(i => $"{i.Code}: {i.Key?.RegionId} {(i.StationM is { } st ? st.ToString("0.00", CultureInfo.InvariantCulture) : "")} {i.Detail}".Trim()))
                    .ToList();
                var pairs = earthworks.Pairs.Where(p => p.Key.CorridorId == corridor).ToList();
                double volCut = pairs.Sum(p => p.CutM3), volFill = pairs.Sum(p => p.FillM3), length = pairs.Sum(p => p.ToM - p.FromM);
                double planCut = 0, planFill = 0, plan3Cut = 0, plan3Fill = 0;
                foreach (var p in pairs)
                {
                    var a = Widths(inputByKey, stationByKey, p.Key, p.FromM);
                    var b = Widths(inputByKey, stationByKey, p.Key, p.ToM);
                    var ds = p.ToM - p.FromM;
                    planCut += (a.Cut + b.Cut) / 2 * ds;
                    planFill += (a.Fill + b.Fill) / 2 * ds;
                    plan3Cut += (a.Cut3D + b.Cut3D) / 2 * ds;
                    plan3Fill += (a.Fill3D + b.Fill3D) / 2 * ds;
                }
                var blocks = corridorBlocks.Where(b => b.CorridorId == corridor).Select(b => b.Issue).ToList();
                issues.AddRange(blocks);
                issues.AddRange(globalEarthworks);
                // A kernel 'not complete' that no keyed finding of another corridor explains is never upgraded (Codex 16:46).
                if (!earthworks.Complete && globalEarthworks.Count == 0 &&
                    !earthworks.Issues.Any(i => i.Key is not null) && !issues.Any())
                    issues.Add("earthworks-incomplete: the cut/fill measurement reported not complete without a keyed finding");
                var droppedRegions = regionIssues.Count(r => r.CorridorId == corridor);
                var corridorSchedules = schedules.Where(s => s.Key.CorridorId == corridor).ToList();
                var everyIntervalIntegrated = corridorSchedules.Count > 0 && corridorSchedules.All(s => s.StationsM.Count >= 2 &&
                    Enumerable.Range(1, s.StationsM.Count - 1).All(i => pairs.Any(p => p.Key == s.Key &&
                        p.FromM == s.StationsM[i - 1] && p.ToM == s.StationsM[i])));
                if (!everyIntervalIntegrated && !issues.Any())
                    issues.Add("earthworks-coverage: not every station interval of the corridor was integrated");
                var postPairs = post.PostStripObserved?.Pairs.Where(p => p.Key.CorridorId == corridor).ToList()
                    ?? new List<CorridorBotSurfaceLogic.IntegratedPair>();
                var postDebits = post.PairDebits.Where(p => p.Key.CorridorId == corridor).ToList();
                var postIssues = post.Issues.Where(i => i.Key?.CorridorId == corridor)
                    .Concat(post.PostStripObserved?.Issues.Where(i => i.Key?.CorridorId == corridor && !grossIssueKeys.Contains((i.Code, i.Key, i.StationM)))
                        ?? Enumerable.Empty<CorridorBotSurfaceLogic.Issue>())
                    .Select(i => $"post-strip {i.Code}: {i.Key?.RegionId} {(i.StationM is { } ps ? ps.ToString("0.00", CultureInfo.InvariantCulture) : "")} {i.Detail}".Trim())
                    .Concat(postGlobal).ToList();
                var postIntervals = postPairs.Select(p => (p.Key, p.FromM, p.ToM)).ToHashSet();
                var debitIntervals = postDebits.Select(d => (d.Key, d.FromM, d.ToM)).ToHashSet();
                var sameIntervals = postIntervals.Count == pairs.Count && debitIntervals.Count == pairs.Count &&
                    pairs.All(p => postIntervals.Contains((p.Key, p.FromM, p.ToM)) && debitIntervals.Contains((p.Key, p.FromM, p.ToM)));
                if (!sameIntervals && postIssues.Count == 0)
                    postIssues.Add("post-strip-coverage: the earthworks after stripping do not cover exactly the intervals measured before stripping");
                issues.AddRange(postIssues);
                var earthworksComplete = issues.Count == 0 && everyIntervalIntegrated;
                // materials: Codex's CorridorMaterialLogic over the same declared schedules (union plan areas, native-area
                // volumes, asphalt union, vertical order/contact); its issues make the corridor incomplete.
                var codeVol = new Dictionary<string, double>(StringComparer.Ordinal);
                var codeArea = new Dictionary<string, double>(StringComparer.Ordinal);
                var asphalt = 0.0;
                var regionsOfCorridor = materials.Regions.Where(r => r.Key.CorridorId == corridor).ToList();
                foreach (var region in regionsOfCorridor)
                {
                    foreach (var (code, v) in region.CodeVolumes) codeVol[code] = codeVol.GetValueOrDefault(code) + v;
                    foreach (var (code, a) in region.CodePlanAreas) codeArea[code] = codeArea.GetValueOrDefault(code) + a;
                    asphalt += region.AsphaltPlanArea ?? 0;
                }
                var materialNotes = materials.Issues.Where(i => i.Key?.CorridorId == corridor)
                    .Select(i => (Advisory(i.Code) ? "note " : "") + $"material {i.Code}: {i.Key?.RegionId} {(i.StationM is { } s ? s.ToString("0.00", CultureInfo.InvariantCulture) : "")} {i.Detail}".Trim())
                    .ToList();
                materialNotes.AddRange(globalMaterialFailures);
                var materialsQuantityComplete = blocks.Count == 0 && droppedRegions == 0 && globalMaterialFailures.Count == 0 &&
                    corridorSchedules.Count > 0 && regionsOfCorridor.Count == corridorSchedules.Count && regionsOfCorridor.All(r => r.Complete);
                if (!materialsQuantityComplete && !materialNotes.Any(n => !n.StartsWith("note ", StringComparison.Ordinal)) && blocks.Count == 0 && droppedRegions == 0)
                    materialNotes.Add("material-coverage: not every scheduled region has complete material quantities");
                var statuses = regionsOfCorridor.Select(r => r.InterfaceStatus).Distinct().ToList();
                var interfaceStatus = orderUnknown || statuses.Contains("unknown") || statuses.Count == 0 ? "unknown"
                    : statuses.Contains("failed") ? "failed" : statuses.Contains("consistent") ? "consistent" : "not_applicable";
                var materialsComplete = materialsQuantityComplete;
                var allIssues = issues.Concat(materialNotes).ToList();
                measures.Add(new CorridorMeasure(corridor, earthworksComplete && materialsComplete, allIssues,
                    earthworks.Stations.Count(s => s.Key.CorridorId == corridor), length, volCut, volFill, planCut, planFill,
                    plan3Cut, plan3Fill, codeVol, codeArea)
                {
                    AsphaltPlanArea = asphalt, InterfaceStatus = interfaceStatus,
                    EarthworksComplete = earthworksComplete, MaterialsComplete = materialsComplete,
                    PostCut = postPairs.Count > 0 ? postPairs.Sum(p => p.CutM3) : null,
                    PostFill = postPairs.Count > 0 ? postPairs.Sum(p => p.FillM3) : null,
                    StripDebit = postDebits.Count > 0 ? postDebits.Sum(d => d.VolumeM3) : null,
                    StripDepthM = rules.HisufDepthM, StripMethodId = post.MethodId,
                });
            }
            foreach (var (c, reason) in skipped) notes.Add($"קורידור לא נמדד — {c}: {reason}");
            return new Export(rules, measures, skipped, tables, evidence, notes);
        }

        /// <summary>Plan widths of cut and fill at one station (the getArea widths of CalcSectionVolumes3) and the same widths
        /// times the 3D factor of the ground piece they lie on (len3D / len, calcSectionPart).</summary>
        public static (double Cut, double Fill, double Cut3D, double Fill3D) Widths(
            IReadOnlyDictionary<(CorridorBotSurfaceLogic.RegionKey, double), CorridorBotSurfaceLogic.StationInput> inputs,
            IReadOnlyDictionary<(CorridorBotSurfaceLogic.RegionKey, double), CorridorBotSurfaceLogic.StationResult> stations,
            CorridorBotSurfaceLogic.RegionKey key, double station)
        {
            if (!inputs.TryGetValue((key, station), out var input) || !stations.TryGetValue((key, station), out var result))
                return (0, 0, 0, 0);
            double cut = 0, fill = 0, cut3 = 0, fill3 = 0;
            foreach (var part in input.GroundParts)
            {
                var ground = part.Points.Select(p => (p.OffsetM, p.ElevationM)).ToList();
                var ln = 0.0; var l3 = 0.0;
                for (var i = 1; i < ground.Count; i++)
                {
                    var dx = ground[i].OffsetM - ground[i - 1].OffsetM; var dz = ground[i].ElevationM - ground[i - 1].ElevationM;
                    ln += Math.Abs(dx); l3 += Math.Sqrt(dx * dx + dz * dz);
                }
                var k3 = ln > 0 ? l3 / ln : 1.0;
                foreach (var seg in result.Envelope)
                {
                    var (c, f) = SegmentWidths(ground, seg.A.OffsetM, seg.A.ElevationM, seg.B.OffsetM, seg.B.ElevationM);
                    cut += c; fill += f; cut3 += c * k3; fill3 += f * k3;
                }
            }
            return (cut, fill, cut3, fill3);
        }

        /// <summary>Horizontal widths where a design segment is below (cut) or above (fill) a ground polyline.</summary>
        public static (double Cut, double Fill) SegmentWidths(IReadOnlyList<(double X, double Z)> ground, double x1, double z1, double x2, double z2)
        {
            if (x2 < x1) (x1, z1, x2, z2) = (x2, z2, x1, z1);
            if (ground.Count < 2 || x2 - x1 <= 1e-12) return (0, 0);
            double lo = Math.Max(x1, ground[0].X), hi = Math.Min(x2, ground[^1].X);
            if (hi <= lo) return (0, 0);
            var xs = new SortedSet<double> { lo, hi };
            foreach (var g in ground) if (g.X > lo && g.X < hi) xs.Add(g.X);
            double Des(double x) => z1 + (z2 - z1) * (x - x1) / (x2 - x1);
            double Gr(double x)
            {
                for (var i = 1; i < ground.Count; i++)
                    if (x <= ground[i].X + 1e-12)
                        return ground[i].X == ground[i - 1].X ? ground[i].Z
                            : ground[i - 1].Z + (ground[i].Z - ground[i - 1].Z) * (x - ground[i - 1].X) / (ground[i].X - ground[i - 1].X);
                return ground[^1].Z;
            }
            double cut = 0, fill = 0;
            var list = xs.ToList();
            for (var i = 1; i < list.Count; i++)
            {
                double a = list[i - 1], b = list[i], d1 = Des(a) - Gr(a), d2 = Des(b) - Gr(b), dx = b - a;
                if (d1 >= 0 && d2 >= 0) { if (d1 != 0 || d2 != 0) fill += dx; }
                else if (d1 <= 0 && d2 <= 0) cut += dx;
                else
                {
                    var t = Math.Abs(d1) * dx / (Math.Abs(d1) + Math.Abs(d2));
                    if (d1 > 0) { fill += t; cut += dx - t; } else { cut += t; fill += dx - t; }
                }
            }
            return (cut, fill);
        }

        /// <summary>Writes the NTI workbook; returns its SHA-256.</summary>
        public static string WriteWorkbook(Export export, string path, Context ctx)
        {
            var wb = CorridorBoqWorkbook.Create(export, ctx);
            MiniXlsx.Write(wb, path);
            using var s = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
        }
    }
}
