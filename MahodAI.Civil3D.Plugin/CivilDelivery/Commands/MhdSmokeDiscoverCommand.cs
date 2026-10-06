using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdSmokeDiscoverCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_SMOKE_DISCOVER (directive §33): read-only object-level inventory of the
    /// ACTIVE drawing for Gate-0 discovery evidence — alignments, surfaces,
    /// corridors, networks, sample-line infrastructure, per-layer entity counts,
    /// CL-candidate statistics, XREF states and label samples. Emits JSON to
    /// %LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\discover\<drawing>.json.
    /// </summary>
    public class MhdSmokeDiscoverCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.SmokeDiscover, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            var report = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["command"] = CivilDeliveryCommandNames.SmokeDiscover,
                ["generated_utc"] = DateTime.UtcNow.ToString("O"),
                ["drawing"] = db.Filename,
                ["drawing_hash"] = SafeHash(db.Filename),
                ["civil_version"] = AcadApp.Version.ToString(),
                ["insunits"] = db.Insunits.ToString(),
            };

            // Opened before ANY Civil API call: a hang leaves its last stage flushed.
            using var log = new StageLog("smoke_discover");
            report["stage_log"] = log.Path_;
            log.Info($"drawing={db.Filename}");

            try
            {
                log.Begin("discover.start_transaction");
                using var tr = db.TransactionManager.StartTransaction();
                log.End("discover.start_transaction");

                // ------------------------------------------------------- civil objects
                CivilDocument? civil = null;
                log.Begin("discover.get_civil_document");
                try { civil = CivilDocument.GetCivilDocument(db); } catch (System.Exception ex) { log.Fail("discover.get_civil_document", ex); }
                log.End("discover.get_civil_document", civil == null ? "unavailable" : "ok");

                if (civil != null)
                {
                    log.Begin("discover.alignments");
                    report["alignments"] = ListAlignments(tr, civil);
                    log.End("discover.alignments");

                    log.Begin("discover.surfaces");
                    report["surfaces"] = ListNamed(tr, civil.GetSurfaceIds());
                    log.End("discover.surfaces");

                    log.Begin("discover.pipe_networks");
                    report["pipe_networks"] = ListNamed(tr, civil.GetPipeNetworkIds());
                    log.End("discover.pipe_networks");

                    log.Begin("discover.corridors");
                    report["corridors"] = ListCorridors(tr, civil);
                    log.End("discover.corridors");

                    log.Begin("discover.sample_line_groups");
                    report["sample_line_groups"] = ListSampleLineGroups(tr, civil);
                    log.End("discover.sample_line_groups");
                }
                else
                {
                    report["civil_document"] = "unavailable (plain AutoCAD?)";
                }

                // ---------------------------------------------------------- entities
                var layerStats = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                var textSamples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var xrefs = new List<object>();
                int sectionViews = 0;

                log.Begin("discover.scan_modelspace");
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                int scanned = 0;
                foreach (ObjectId id in ms)
                {
                    DBObject obj;
                    try { obj = tr.GetObject(id, OpenMode.ForRead); }
                    catch { continue; }

                    if (++scanned % 20000 == 0) log.Info($"discover.scan progress: {scanned}");

                    if (obj is CivilDb.SectionView) sectionViews++;

                    if (obj is BlockReference br)
                    {
                        try
                        {
                            if (tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord btr &&
                                (btr.IsFromExternalReference || btr.IsFromOverlayReference))
                            {
                                xrefs.Add(new
                                {
                                    name = btr.Name,
                                    path = btr.PathName,
                                    status = btr.XrefStatus.ToString(),
                                    unloaded = btr.IsUnloaded,
                                });
                                continue;
                            }
                        }
                        catch { }
                    }

                    if (obj is not Entity ent) continue;

                    var kind = ent switch
                    {
                        Line => "LINE",
                        Polyline => "LWPOLYLINE",
                        Polyline2d => "POLYLINE2D",
                        DBText => "TEXT",
                        MText => "MTEXT",
                        Hatch => "HATCH",
                        BlockReference => "INSERT",
                        Arc => "ARC",
                        Circle => "CIRCLE",
                        _ => ent.GetType().Name.ToUpperInvariant(),
                    };

                    if (!layerStats.TryGetValue(ent.Layer, out var counts))
                        layerStats[ent.Layer] = counts = new Dictionary<string, int>();
                    counts.TryGetValue(kind, out var n);
                    counts[kind] = n + 1;

                    var text = ent switch
                    {
                        DBText t => t.TextString,
                        MText mt => mt.Text,
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (!textSamples.TryGetValue(ent.Layer, out var samples))
                            textSamples[ent.Layer] = samples = new List<string>();
                        if (samples.Count < 8) samples.Add(text.Trim());
                    }
                }

                log.End("discover.scan_modelspace", $"entities={scanned} layers={layerStats.Count}");
                report["scanned_entities"] = scanned;
                report["section_views_in_modelspace"] = sectionViews;
                report["xrefs"] = xrefs;
                report["layers"] = layerStats
                    .OrderByDescending(kv => kv.Value.Values.Sum())
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                report["text_samples_by_layer"] = textSamples;

                // CL candidates: layers whose LINE/LWPOLYLINE population could encode
                // section locations (evidence for profile layer_patterns — no guessing).
                report["cl_candidate_layers"] = layerStats
                    .Where(kv => kv.Value.ContainsKey("LINE") || kv.Value.ContainsKey("LWPOLYLINE"))
                    .OrderByDescending(kv => kv.Value.GetValueOrDefault("LINE") + kv.Value.GetValueOrDefault("LWPOLYLINE"))
                    .Take(25)
                    .Select(kv => new
                    {
                        layer = kv.Key,
                        lines = kv.Value.GetValueOrDefault("LINE"),
                        polylines = kv.Value.GetValueOrDefault("LWPOLYLINE"),
                    })
                    .ToList();

                // Discovery is read-only; it never publishes lazy host changes (the
                // same boundary PLAN/preview/VERIFY keep since 1.2.28).
                tr.Abort();
                report["result"] = "pass";
            }
            catch (System.Exception ex)
            {
                log.Fail("smoke_discover", ex);
                report["result"] = "fail";
                report["error"] = ex.ToString();
            }

            var outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery", "discover");
            Directory.CreateDirectory(outDir);
            var name = string.IsNullOrEmpty(db.Filename) ? "unsaved" : Path.GetFileNameWithoutExtension(db.Filename);
            var outPath = Path.Combine(outDir, name + ".json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, SectionsWorkflowService.Json));
            ed.WriteMessage($"\n{CivilDeliveryCommandNames.SmokeDiscover} -> {outPath}\nיומן שלבים: {log.Path_}\n");
        }

        private static List<object> ListAlignments(Transaction tr, CivilDocument civil)
        {
            var list = new List<object>();
            foreach (ObjectId id in civil.GetAlignmentIds())
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment a)
                        list.Add(new
                        {
                            name = a.Name,
                            start_station = Math.Round(a.StartingStation, 2),
                            end_station = Math.Round(a.EndingStation, 2),
                            length = Math.Round(a.Length, 2),
                        });
                }
                catch { }
            }
            return list;
        }

        private static List<object> ListNamed(Transaction tr, ObjectIdCollection ids)
        {
            var list = new List<object>();
            foreach (ObjectId id in ids)
            {
                try
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var name = obj switch
                    {
                        CivilDb.Surface s => s.Name,
                        CivilDb.Network n => n.Name,
                        CivilDb.Entity e => e.Name,
                        _ => obj.GetType().Name,
                    };
                    list.Add(new { name, type = obj.GetType().Name });
                }
                catch { }
            }
            return list;
        }

        private static List<object> ListCorridors(Transaction tr, CivilDocument civil)
        {
            var list = new List<object>();
            try
            {
                foreach (ObjectId id in civil.CorridorCollection)
                {
                    try
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Corridor c)
                            list.Add(new { name = c.Name });
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static List<object> ListSampleLineGroups(Transaction tr, CivilDocument civil)
        {
            var list = new List<object>();
            foreach (ObjectId alignmentId in civil.GetAlignmentIds())
            {
                try
                {
                    if (tr.GetObject(alignmentId, OpenMode.ForRead) is not CivilDb.Alignment a) continue;
                    foreach (ObjectId slgId in a.GetSampleLineGroupIds())
                    {
                        if (tr.GetObject(slgId, OpenMode.ForRead) is CivilDb.SampleLineGroup slg)
                            list.Add(new
                            {
                                name = slg.Name,
                                alignment = a.Name,
                                sample_lines = slg.GetSampleLineIds().Count,
                                tool_owned = SectionOwnershipService.Read(tr, slg) != null,
                            });
                    }
                }
                catch { }
            }
            return list;
        }

        private static string SafeHash(string? path)
        {
            try
            {
                return string.IsNullOrEmpty(path) || !File.Exists(path)
                    ? "" : ArtifactHash.Sha256OfFile(path);
            }
            catch { return ""; }
        }
    }
}
