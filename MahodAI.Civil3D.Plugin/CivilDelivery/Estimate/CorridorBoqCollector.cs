using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using RXObject = Autodesk.AutoCAD.Runtime.RXObject;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// READ-ONLY native reader of a corridor drawing for the corridor bill (Natali 30.09.2026): per applied assembly of every
    /// corridor / baseline / region — the station, the links coded as the design bottom (ruleset link_code, 'Bot'), the
    /// existing-ground surface (ruleset existing_ground_surface, 'MK') sampled on the section line between the bottom's
    /// outer offsets and split at surface holes (FindElevationAtXY at the midpoint of every sample pair, like
    /// CalcSectionVolumes3.calcSection), and every coded shape with its area and links. Elevations are the calculated
    /// points' absolute XYZ.Z; the baseline-relative SOE.Z plus the profile elevation is kept as a cross-check. All values
    /// are converted to metres with the proven drawing unit. A native failure becomes a failed station (never a zero), an
    /// out-of-date or unreadable corridor is skipped with its reason. Nothing is written to the drawing; the raw inputs are
    /// written to a JSON receipt in the run folder, whose SHA-256 every station references.
    /// </summary>
    internal static class CorridorBoqCollector
    {
        internal sealed record Collected(
            string SurfaceName, string LinkCode, double LinearToMetres,
            List<CorridorBotSurfaceLogic.Schedule> Schedules, List<CorridorBotSurfaceLogic.StationInput> Inputs,
            List<CorridorShapeStation> Shapes, List<CorridorRegionSample> Regions, List<(string Corridor, string Reason)> Skipped,
            List<CorridorVolumeTable> DrawingTables, List<string> Evidence, string RawReceiptPath, string RawReceiptSha256)
        {
            /// <summary>Regions that cannot be integrated because an applied assembly's station could not be read (Codex 16:33).</summary>
            public List<(string CorridorId, string Issue)> RegionIssues { get; init; } = new();
            /// <summary>Corridors measured but never totalled (out of date): both earthworks and materials stay out of the sum.</summary>
            public List<(string CorridorId, string Issue)> CorridorBlocks { get; init; } = new();
        }

        private const string TableLayer = "CalcVolumes2_Table";

        /// <summary>Why a switched-off corridor or region (IsProcessed=false) is not measured — decision WEST-11.</summary>
        internal const string DisabledReason = "מכובה בשרטוט (IsProcessed=false) — מחוץ לתחום האומדן לפי מדיניות 'תכנון פעיל בלבד'; לא נמדד ואינו כמות 0";

        internal static Collected Collect(Transaction tr, Database db, CivilDocument civilDoc, CorridorBoqRuleset rules,
            double linearToMetres, string runFolder, string drawingPath, string drawingHash)
        {
            if (!double.IsFinite(linearToMetres) || linearToMetres <= 0)
                throw new InvalidDataException("The drawing unit is not proven; the corridor bill cannot convert to metres.");
            var L = linearToMetres;
            var surface = FindSurface(tr, civilDoc, rules.ExistingGroundSurface);
            var schedules = new List<CorridorBotSurfaceLogic.Schedule>();
            var inputs = new List<CorridorBotSurfaceLogic.StationInput>();
            var shapes = new List<CorridorShapeStation>();
            var regions = new List<CorridorRegionSample>();
            var skipped = new List<(string, string)>();
            var evidence = new List<string>();
            var regionIssues = new List<(string, string)>();
            var corridorBlocks = new List<(string, string)>();
            var snapped = 0;
            var raw = new List<object>();
            var receiptName = "corridor_boq_raw_inputs.json";
            double maxZCheck = 0;
            var zChecks = 0;
            var ends = new CorridorGroundLine.Counters();
            var rawRegions = new List<object>();

            foreach (ObjectId corridorId in civilDoc.CorridorCollection)
            {
                CivilDb.Corridor corridor;
                try { corridor = (CivilDb.Corridor)tr.GetObject(corridorId, OpenMode.ForRead); }
                catch (Exception ex) { skipped.Add((corridorId.ToString(), "הקורידור לא נפתח לקריאה: " + ex.Message)); continue; }
                var corridorName = corridor.Name ?? corridor.Handle.ToString();
                var corridorKey = $"{corridorName} [{corridor.Handle}]";
                if (corridor.IsOutOfDate)
                    // Measured and shown from the last build, never totalled: rebuild in Civil and measure again.
                    corridorBlocks.Add((corridorKey, "corridor-out-of-date: הקורידור אינו מעודכן (Out of date) — הכמויות מהבנייה האחרונה ואינן בסה\"כ; יש לבנות אותו מחדש ב-Civil ולמדוד שוב"));
                var anyAssembly = false;
                var anyProcessed = false;
                try
                {
                    foreach (CivilDb.Baseline b0 in corridor.Baselines)
                        foreach (CivilDb.BaselineRegion r0 in b0.BaselineRegions)
                        {
                            if (r0.AppliedAssemblies.Count > 0) anyAssembly = true;
                            if (b0.IsProcessed && r0.IsProcessed) anyProcessed = true;
                        }
                }
                catch (Exception ex) { skipped.Add((corridorName, "ה-regions של הקורידור לא נקראו: " + ex.Message)); continue; }
                if (!anyProcessed)
                {
                    // Estimate scope "active design only" (decision WEST-11, 30.09): a corridor switched off in the drawing is
                    // listed, never measured, never a zero quantity and never called replaced.
                    skipped.Add((corridorName, DisabledReason));
                    continue;
                }
                if (!anyAssembly)
                {
                    skipped.Add((corridorName, "אין בקורידור חתכים מחושבים (applied assemblies) אף שהוא פעיל — יש לבנות אותו מחדש ב-Civil ולמדוד שוב"));
                    continue;
                }
                var bi = 0;
                foreach (CivilDb.Baseline baseline in corridor.Baselines)
                {
                    bi++;
                    var baselineKey = $"baseline-{bi:D3}";
                    CivilDb.Alignment alignment;
                    CivilDb.Profile profile;
                    try
                    {
                        alignment = (CivilDb.Alignment)tr.GetObject(baseline.AlignmentId, OpenMode.ForRead);
                        profile = (CivilDb.Profile)tr.GetObject(baseline.ProfileId, OpenMode.ForRead);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add((corridorName, $"{baselineKey}: התוואי או הפרופיל של ה-baseline לא נקראו — {ex.Message}"));
                        continue;
                    }
                    var ri = 0;
                    foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                    {
                        ri++;
                        var key = new CorridorBotSurfaceLogic.RegionKey(corridorKey, baselineKey, $"region-{ri:D3}");
                        if (!baseline.IsProcessed || !region.IsProcessed)
                        {
                            skipped.Add((corridorName, $"{baselineKey}/{key.RegionId}: {DisabledReason}"));
                            continue;
                        }
                        var stationsM = new List<double>();
                        var assemblies = 0;
                        var unreadable = 0;
                        foreach (CivilDb.AppliedAssembly aa in region.AppliedAssemblies)
                        {
                            assemblies++;
                            double stDu;
                            try { stDu = aa.Points[0].StationOffsetElevationToBaseline.X; }
                            catch (Exception ex)
                            {
                                unreadable++;
                                evidence.Add($"{key.CorridorId}/{key.RegionId}: assembly {assemblies} without a readable station — {ex.Message}");
                                continue;
                            }
                            var stM = stDu * L;
                            stationsM.Add(stM);
                            var receiptRef = $"{receiptName}#{raw.Count}";
                            var bot = new List<CorridorBotSurfaceLogic.Segment>();
                            var ground = new List<CorridorBotSurfaceLogic.Part>();
                            string? failure = null;
                            double offFrom = double.NaN, offTo = double.NaN;
                            // A section Civil did not build (live WEST r8, 3000 at 60+080: 0 links, 0 shapes, every point at offset 0)
                            // fails both the Bot and the shapes explicitly — never a zero, never 'no applicable geometry', never
                            // bridged (Codex 17:26).
                            string? emptySection = null;
                            try
                            {
                                if (aa.Links.Count == 0)
                                    emptySection = $"empty-section: the applied assembly has no links ({aa.Points.Count} points) — Civil did not build the corridor section at this station";
                            }
                            catch (Exception ex) { emptySection = "the applied assembly's links could not be counted — " + ex.Message; }
                            try
                            {
                                if (emptySection != null) throw new InvalidDataException(emptySection);
                                double pe = double.NaN;
                                try { pe = profile.ElevationAt(stDu); } catch { /* cross-check only */ }
                                foreach (CivilDb.CalculatedLink link in aa.GetLinksByCode(rules.LinkCode))
                                {
                                    if (link.CalculatedPoints.Count != 2)
                                        throw new InvalidDataException($"a Bot link with {link.CalculatedPoints.Count} points (a straight link has exactly two)");
                                    var a = link.CalculatedPoints[0];
                                    var b = link.CalculatedPoints[1];
                                    var pa = new CorridorBotSurfaceLogic.Point(a.StationOffsetElevationToBaseline.Y * L, a.XYZ.Z * L);
                                    var pb = new CorridorBotSurfaceLogic.Point(b.StationOffsetElevationToBaseline.Y * L, b.XYZ.Z * L);
                                    if (double.IsFinite(pe))
                                    {
                                        foreach (var p in new[] { a, b })
                                        {
                                            maxZCheck = Math.Max(maxZCheck, Math.Abs((p.StationOffsetElevationToBaseline.Z + pe) - p.XYZ.Z) * L);
                                            zChecks++;
                                        }
                                    }
                                    bot.Add(pa.OffsetM <= pb.OffsetM ? new(pa, pb) : new(pb, pa));
                                }
                                if (bot.Count == 0) throw new InvalidDataException($"no links coded '{rules.LinkCode}'");
                                offFrom = bot.Min(s => s.A.OffsetM);
                                offTo = bot.Max(s => s.B.OffsetM);
                                ground = SampleGround(alignment, surface, stDu, offFrom, offTo, L, ends);
                                if (ground.Count == 0) throw new InvalidDataException($"surface '{rules.ExistingGroundSurface}' has fewer than two points on the section line");
                            }
                            catch (Exception ex) { failure = ex.Message; }

                            var shapeSamples = new List<CorridorShapeSample>();
                            string? shapeFailure = null;
                            try
                            {
                                if (emptySection != null) throw new InvalidDataException(emptySection);
                                foreach (CivilDb.CalculatedShape shape in aa.Shapes)
                                {
                                    var codes = shape.CorridorCodes.Cast<string>().Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
                                    if (codes.Count != 1)
                                        throw new InvalidDataException($"a calculated shape with {codes.Count} corridor codes ({string.Join(", ", codes)})");
                                    var links = new List<CorridorLink>();
                                    foreach (CivilDb.CalculatedLink link in shape.CalculatedLinks)
                                    {
                                        if (link.CalculatedPoints.Count != 2)
                                            throw new InvalidDataException($"a shape link of '{codes[0]}' with {link.CalculatedPoints.Count} points");
                                        var a = link.CalculatedPoints[0];
                                        var b = link.CalculatedPoints[1];
                                        links.Add(new CorridorLink(a.StationOffsetElevationToBaseline.Y * L, a.XYZ.Z * L,
                                            b.StationOffsetElevationToBaseline.Y * L, b.XYZ.Z * L));
                                    }
                                    var area = shape.Area * L * L;
                                    foreach (var code in codes)
                                        shapeSamples.Add(new CorridorShapeSample(code, area, links));
                                }
                            }
                            catch (Exception ex) { shapeFailure = ex.Message; }

                            // The complete Bot inventory of this assembly was read (a link without two points fails the station), so its
                            // parts are measured as they are: a gap between Bot parts (e.g. under a traffic island) is not measured and
                            // never bridged — CalcSectionVolumes3 :360-389 (Codex 16:48, opt-in).
                            inputs.Add(new CorridorBotSurfaceLogic.StationInput(key, stM, offFrom, offTo, bot, ground,
                                failure == null, receiptRef, failure) { AllowDisconnectedBotDomains = failure == null });
                            shapes.Add(new CorridorShapeStation(key.CorridorId, key.BaselineId, key.RegionId, stM,
                                shapeFailure == null, shapeFailure, shapeFailure == null ? shapeSamples : new List<CorridorShapeSample>()));
                            raw.Add(new
                            {
                                key.CorridorId, key.BaselineId, key.RegionId, StationM = stM,
                                OffsetFromM = double.IsFinite(offFrom) ? offFrom : (double?)null,
                                OffsetToM = double.IsFinite(offTo) ? offTo : (double?)null,
                                Bot = bot.Select(s => new[] { s.A.OffsetM, s.A.ElevationM, s.B.OffsetM, s.B.ElevationM }),
                                Ground = ground.Select(g => g.Points.Select(p => new[] { p.OffsetM, p.ElevationM })),
                                Shapes = shapeSamples.Select(s => new { s.Code, s.Area, Links = s.Links.Select(l => new[] { l.X1, l.Z1, l.X2, l.Z2 }) }),
                                Failure = failure, ShapeFailure = shapeFailure, BotDomainMode = failure == null ? "union-of-reported-bot-domains" : null,
                            });
                        }
                        regions.Add(new CorridorRegionSample(key.CorridorId, bi, ri, region.StartStation * L, region.EndStation * L, assemblies));
                        if (unreadable > 0)
                        {
                            // The missing station is unknown, so nothing in this region may be integrated or bridged.
                            regionIssues.Add((key.CorridorId, $"region-station-unreadable: {key.BaselineId}/{key.RegionId} — {unreadable} of {assemblies} applied assemblies without a readable station; the region is not measured"));
                            continue;
                        }
                        var ordered = stationsM.OrderBy(s => s).ToList();
                        double startM = region.StartStation * L, endM = region.EndStation * L;
                        if (ordered.Count > 0 && ordered[0] != startM && Math.Abs(ordered[0] - startM) <= 1e-6) { startM = ordered[0]; snapped++; }
                        if (ordered.Count > 0 && ordered[^1] != endM && Math.Abs(ordered[^1] - endM) <= 1e-6) { endM = ordered[^1]; snapped++; }
                        schedules.Add(new CorridorBotSurfaceLogic.Schedule(key, startM, endM, ordered));
                        rawRegions.Add(new
                        {
                            key.CorridorId, key.BaselineId, key.RegionId, NativeStartM = region.StartStation * L, NativeEndM = region.EndStation * L,
                            ScheduleStartM = startM, ScheduleEndM = endM, AppliedAssemblies = assemblies, StationsM = ordered,
                        });
                    }
                }
            }
            if (snapped > 0) evidence.Add($"{snapped} גבולות region הותאמו לתחנת החתך הקיצונית שלהם (הפרש של עד מיקרון)");
            evidence.Add($"קצוות קו הקרקע: {ends.Measured} קצוות בנקודה המבוקשת המדויקת, בגובה שנמדד שם על '{rules.ExistingGroundSurface}' (FindElevationAtXY); " +
                $"{ends.Replaced} דגימות קצה היו אותה נקודה ברעש נומרי (הזזה מרבית {ends.MaxEndShiftM:0.0E+0} מ'); " +
                $"{ends.Added} קצוות לא הוחזרו בדגימה (הדגימה הקרובה עד {ends.MaxAddedGapM * 1000:0.000} מ\"מ מהם, נשמרה כנקודה אמיתית); {ends.OffSurface} קצוות מחוץ למשטח — לא הוארכו" +
                (ends.Outside > 0 ? $"; {ends.Outside} דגימות מחוץ לקו החתך לא נכללו" : ""));
            evidence.Add($"גבהים: XYZ.Z המוחלט של נקודות Civil; בדיקה צולבת מול SOE.Z + גובה הפרופיל ב-{zChecks} נקודות — הפרש מרבי {maxZCheck:0.0000} מ'");
            var tables = ReadDrawingTables(tr, db);
            Directory.CreateDirectory(runFolder);
            var receiptPath = Path.Combine(runFolder, receiptName);
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
            {
                schema = "mahod-corridor-boq-raw/1", drawing = drawingPath, drawing_sha256 = drawingHash,
                surface = rules.ExistingGroundSurface, link_code = rules.LinkCode, linear_to_metres = L,
                ground_ends = new
                {
                    measured = ends.Measured, replaced_noise = ends.Replaced, max_end_shift_m = ends.MaxEndShiftM,
                    added = ends.Added, max_added_gap_m = ends.MaxAddedGapM, off_surface = ends.OffSurface, outside = ends.Outside,
                },
                regions = rawRegions, stations = raw,
            }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            var sha = ArtifactHash.Sha256OfFile(receiptPath);
            return new Collected(rules.ExistingGroundSurface, rules.LinkCode, L, schedules, inputs, shapes, regions, skipped,
                tables, evidence, receiptPath, sha) { RegionIssues = regionIssues, CorridorBlocks = corridorBlocks };
        }

        private static CivilDb.TinSurface FindSurface(Transaction tr, CivilDocument civilDoc, string name)
        {
            var found = new List<CivilDb.TinSurface>();
            var names = new List<string>();
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Surface s) continue;
                    names.Add(s.Name);
                    if (s is CivilDb.TinSurface tin && string.Equals(s.Name, name, StringComparison.Ordinal))
                        found.Add(tin);
                }
                catch { /* an unreadable surface is not the named one */ }
            }
            return found.Count switch
            {
                1 => found[0],
                0 => throw new InvalidDataException($"משטח הקרקע הקיימת '{name}' (TIN, לפי כללי הפרויקט) לא נמצא בשרטוט. משטחים בשרטוט: " +
                    (names.Count == 0 ? "אין" : string.Join(", ", names.Select(n => "'" + n + "'")))),
                _ => throw new InvalidDataException($"בשרטוט {found.Count} משטחים בשם '{name}' — לא ברור איזה הוא הקרקע הקיימת."),
            };
        }

        /// <summary>The surface on the section line, as continuous pieces (offset, elevation) in metres; a hole ends a piece.
        /// The line and its ends are CorridorGroundLine's (Core): the ends are the Bot's outer offsets exactly, with the elevation
        /// FindElevationAtXY measures at that exact point (an end off the surface gets no point, never an extension); interior
        /// samples by projection; a sample is an end only within numeric noise (live WEST r8, Codex 17:23).</summary>
        private static List<CorridorBotSurfaceLogic.Part> SampleGround(CivilDb.Alignment alignment, CivilDb.TinSurface surface,
            double stationDu, double fromM, double toM, double L, CorridorGroundLine.Counters ends)
        {
            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            alignment.PointLocation(stationDu, fromM / L, ref x1, ref y1);
            alignment.PointLocation(stationDu, toM / L, ref x2, ref y2);
            double? Measure(double x, double y) =>
                CorridorGroundElevation.Read<Autodesk.Civil.PointNotOnEntityException>(surface.FindElevationAtXY, x, y);
            var samples = new List<CorridorGroundLine.Sample>();
            foreach (Point3d p in surface.SampleElevations(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)))
                samples.Add(new CorridorGroundLine.Sample(p.X, p.Y, p.Z));
            var line = CorridorGroundLine.Order(x1, y1, x2, y2, fromM, toM, L, samples, Measure(x1, y1), Measure(x2, y2), ends);
            var parts = new List<CorridorBotSurfaceLogic.Part>();
            var current = new List<CorridorBotSurfaceLogic.Point>();
            CorridorGroundLine.GroundPoint? previous = null;
            foreach (var g in line)
            {
                // A hole in the surface between two samples ends a piece (CalcSectionVolumes3.calcSection :278-321).
                if (previous is { } q && Measure((q.X + g.X) / 2, (q.Y + g.Y) / 2) is null)
                {
                    if (current.Count > 1) parts.Add(new CorridorBotSurfaceLogic.Part(current));
                    current = new List<CorridorBotSurfaceLogic.Point>();
                }
                current.Add(new CorridorBotSurfaceLogic.Point(g.OffsetM, g.Z * L));
                previous = g;
            }
            if (current.Count > 1) parts.Add(new CorridorBotSurfaceLogic.Part(current));
            return parts;
        }

        /// <summary>'Road Volumes' tables of the MahodCivilNet CalcVolumes tool in the drawing, for comparison only.</summary>
        private static List<CorridorVolumeTable> ReadDrawingTables(Transaction tr, Database db)
        {
            var tables = new List<CorridorVolumeTable>();
            try
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(Table)))) continue;
                    if (tr.GetObject(id, OpenMode.ForRead) is not Table t || !string.Equals(t.Layer, TableLayer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (t.Rows.Count < 3) continue;
                    var title = t.Cells[0, 0].TextString ?? "";
                    var header = Enumerable.Range(0, t.Columns.Count).Select(c => (t.Cells[1, c].TextString ?? "").Trim()).ToList();
                    for (var r = 2; r < t.Rows.Count; r++)
                    {
                        var road = (t.Cells[r, 0].TextString ?? "").Trim();
                        if (road.Length == 0 || road.Equals("Total", StringComparison.OrdinalIgnoreCase)) continue;
                        var values = new Dictionary<string, double>(StringComparer.Ordinal);
                        for (var c = 1; c < header.Count; c++)
                            if (double.TryParse((t.Cells[r, c].TextString ?? "").Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                                values[header[c]] = v;
                        tables.Add(new CorridorVolumeTable(t.Handle.ToString(), title.Trim(), road, values));
                    }
                }
            }
            catch { /* comparison tables are optional evidence; their absence never blocks the bill */ }
            return tables;
        }
    }
}
