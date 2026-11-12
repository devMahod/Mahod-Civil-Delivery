using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

#if !MAHOD_CD_STANDALONE // the separate plugin registers these through its guarded facade (MCD_*)
[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.CivilDelivery.Commands.MhdSmokeTypicalSectionsCommand))]
#endif

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// MHD_SMOKE_TYPICAL (2026-10): the typical-sections chain end to end on the OPEN drawing, without a single click,
    /// so it can run from a script against a fixture copy. It asks for an alignment, the stations and a half-width,
    /// then "clicks" a few metres off the axis at each station through the same picker the live preview uses, proves
    /// the two refusals (a click far from the road, a second click at a marked station), creates the firm's sections
    /// through <see cref="TypicalSectionsService"/>, reads every created block back (title, chain, names written) and
    /// writes smoke_typical_latest.json. Like the sections smoke it runs only on a copy in the fixture folder.
    /// </summary>
    public class MhdSmokeTypicalSectionsCommand
    {
        [CommandMethod(CivilDeliveryCommandNames.SmokeTypical, CommandFlags.Modal)]
        public void Run()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var report = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["command"] = CivilDeliveryCommandNames.SmokeTypical,
                ["started_utc"] = DateTime.UtcNow.ToString("O"),
                ["drawing"] = doc.Database.Filename,
            };
            var checks = new List<Dictionary<string, object?>>();
            report["checks"] = checks;
            void Check(string name, bool pass, string detail)
            {
                checks.Add(new() { ["check"] = name, ["pass"] = pass, ["detail"] = detail });
                ed.WriteMessage($"\n  [{(pass ? "PASS" : "FAIL")}] {name}: {detail}");
            }

            using var log = new StageLog("smoke_typical");
            var refusal = SmokeApplyGuard.Refusal(
                doc.Database.Filename,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MahodCivilDelivery_Fixtures"),
                Environment.GetEnvironmentVariable(SmokeApplyGuard.OverrideVariable));
            if (refusal != null)
            {
                Check("guard", false, refusal);
                Finish(ed, report, checks);
                return;
            }

            try
            {
                var alignmentAnswer = ed.GetString(new PromptStringOptions("\nתוואי (Enter = הראשון עם קורידור, AUTO = בדיקת הסריקה האוטומטית): ") { AllowSpaces = true });
                if (alignmentAnswer.Status is not (PromptStatus.OK or PromptStatus.None)) return;
                if (string.Equals(alignmentAnswer.StringResult?.Trim(), "AUTO", StringComparison.OrdinalIgnoreCase))
                {
                    var active = new ActiveProjectProfileService().LoadForDocument(doc, new SectionsWorkflowService());
                    Check("profile", active.IsUsable && active.Profile != null, active.ProfileSource ?? "(none)");
                    if (active.IsUsable && active.Profile is { } profile)
                    {
                        var scanRequest = new TypicalSectionPickRequest("smoke", doc.Database.UnmanagedObject,
                            TypicalSectionPickLogic.DefaultHalfWidth(profile.Sections.Typical?.HalfWidthM),
                            profile.Sections.Alignments.AllowedNames, true) { PreferredCorridor = profile.Sections.Typical?.CorridorName };
                        var scan = new TypicalSectionPickResult(scanRequest.Token) { Automatic = true };
                        MhdTypicalSectionsCommand.Scan(doc, scanRequest, scan);
                        report["scan"] = scan;
                        Check("automatic stations", scan.Picks.Count > 0 && scan.Picks.All(p => p.Station >= p.ScanFrom && p.Station <= p.ScanTo),
                            scan.Error ?? string.Join(" | ", scan.Notes));
                        if (scan.Picks.Count > 0)
                        {
                            var produced = new TypicalSectionsService().Create(doc, new TypicalSectionsService.Request(
                                scan.Picks, profile, active.ProfileHash, scan.Corridors), log);
                            report["result"] = produced;
                            Check("one section per automatic station", produced.CreatedCount == scan.Picks.Count, string.Join(" | ", produced.Blockers));
                            using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
                            foreach (var section in produced.Sections.Where(s => s.Created))
                            {
                                var exists = doc.Database.TryGetObjectId(new Handle(Convert.ToInt64(section.BlockHandle, 16)), out var id) &&
                                    !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is BlockReference block &&
                                    ((BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead)).Cast<ObjectId>().Any();
                                Check("read back " + section.BlockHandle, exists, section.AlignmentName + " " + Format(section.Station));
                            }
                        }
                    }
                    Finish(ed, report, checks);
                    return;
                }
                var stationsAnswer = ed.GetString(new PromptStringOptions("\nתחנות, מופרדות בפסיק: ") { AllowSpaces = true });
                if (stationsAnswer.Status != PromptStatus.OK) return;
                var halfWidth = MhdTypicalSectionsCommand.PromptHalfWidth(ed, TypicalSectionContracts.DefaultHalfWidthM);
                if (halfWidth == null) return;

                var stations = stationsAnswer.StringResult
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture))
                    .ToList();
                report["stations"] = stations;
                report["half_width_m"] = halfWidth;

                var loaded = new ActiveProjectProfileService().LoadForDocument(doc, new SectionsWorkflowService());
                Check("profile", loaded.IsUsable && loaded.Profile != null, loaded.ProfileSource ?? "(none)");
                if (!loaded.IsUsable || loaded.Profile == null) { Finish(ed, report, checks); return; }

                var picks = new List<TypicalSectionPick>();
                using (doc.LockDocument())
                using (var picker = TypicalSectionPicker.Open(doc.Database, CivilApplication.ActiveDocument,
                           loaded.Profile.Sections.Alignments.AllowedNames))
                {
                    var alignment = string.IsNullOrWhiteSpace(alignmentAnswer.StringResult)
                        ? picker.AlignmentNames.FirstOrDefault()
                        : alignmentAnswer.StringResult.Trim();
                    report["alignment"] = alignment;
                    report["corridor_alignments"] = picker.CorridorOnly ? picker.AlignmentNames : Array.Empty<string>();
                    Check("alignment", alignment != null && picker.AlignmentNames.Contains(alignment, StringComparer.OrdinalIgnoreCase),
                        $"{alignment} among [{string.Join(", ", picker.AlignmentNames)}]");
                    if (alignment == null) { Finish(ed, report, checks); return; }

                    var taken = picker.ExistingMarkerMidpoints();
                    var clickOffset = Math.Min(3.0, halfWidth.Value / 2);
                    report["corridor_stations_near"] = stations.ToDictionary(
                        station => Format(station), station => picker.CorridorStationsNear(alignment, station, 30));
                    foreach (var station in stations)
                    {
                        // A real click lands beside the axis, not on it: resolve a point a few metres to the left.
                        var clicked = picker.PointAt(alignment, station, clickOffset);
                        if (clicked == null) { Check("pick " + Format(station), false, "no plan point"); continue; }
                        var resolution = picker.Resolve(clicked.Value, halfWidth.Value, taken);
                        // The pick lands on the corridor station nearest the click, the one the section is cut at
                        // (midway between two, either is nearest).
                        var expected = picker.SnapStation(alignment, station);
                        var snapped = Math.Abs(Math.Abs(resolution.Station - station) - Math.Abs(expected - station)) < 0.005;
                        Check("pick " + Format(station), resolution.IsReady && snapped,
                            $"{resolution.State} station={Format(resolution.Station)} (corridor {Format(expected)}) offset={resolution.OffsetFromAxis:0.00}");
                        if (!resolution.IsReady) continue;
                        var added = picker.Commit(resolution, halfWidth.Value);
                        picks.Add(added.Pick);
                        taken.Add(new P2(added.Pick.AxisX, added.Pick.AxisY));
                    }

                    if (stations.Count > 0)
                    {
                        var first = stations[0];
                        var again = picker.PointAt(alignment, first, -clickOffset);
                        var duplicate = again == null ? null : picker.Resolve(again.Value, halfWidth.Value, taken);
                        Check("refuses a second click at a marked station",
                            duplicate?.State == TypicalSectionPickLogic.PickState.Duplicate, duplicate?.State.ToString() ?? "no point");
                        var far = picker.PointAt(alignment, first + 7, halfWidth.Value * 2.5);
                        var tooFar = far == null ? null : picker.Resolve(far.Value, halfWidth.Value, taken);
                        Check("refuses a click far from the road",
                            tooFar?.State is TypicalSectionPickLogic.PickState.TooFar or TypicalSectionPickLogic.PickState.NoAlignment,
                            tooFar?.State.ToString() ?? "no point");
                    }
                }
                report["picks"] = picks;

                var service = new TypicalSectionsService();
                var result = service.Create(doc, new TypicalSectionsService.Request(picks, loaded.Profile, loaded.ProfileHash), log);
                report["run_id"] = result.RunId;
                report["artifact"] = result.ArtifactPath;
                report["blockers"] = result.Blockers;
                report["warnings"] = result.Warnings;
                report["corridor_choice"] = result.CorridorChoiceReasons;
                Check("no blockers", result.Blockers.Count == 0, string.Join(" | ", result.Blockers));
                Check("one firm section per pick", result.CreatedCount == picks.Count,
                    $"created={result.CreatedCount} picks={picks.Count}");
                if (result.CreatedCount == 0 && result.Blockers.Count == 0 && result.Sections.Count > 0)
                    report["firm_probe"] = FirmProbe(doc, result);

                var sections = new List<Dictionary<string, object?>>();
                using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (var section in result.Sections)
                    {
                        var row = new Dictionary<string, object?>
                        {
                            ["station"] = section.Station, ["picked_station"] = section.PickedStation,
                            ["block"] = section.BlockName, ["handle"] = section.BlockHandle,
                            ["title"] = section.Title, ["chain_segments"] = section.ChainSegments,
                            ["named"] = section.NamedStrips, ["unmatched"] = section.UnmatchedStrips,
                            ["notes"] = section.Notes, ["error"] = section.Error, ["firm_messages"] = section.FirmMessages,
                        };
                        if (section.BlockHandle != null &&
                            doc.Database.TryGetObjectId(new Handle(Convert.ToInt64(section.BlockHandle, 16)), out var id) &&
                            tr.GetObject(id, OpenMode.ForRead) is BlockReference reference)
                        {
                            var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                            var namesWritten = definition.Cast<ObjectId>()
                                .Select(child => tr.GetObject(child, OpenMode.ForRead))
                                .OfType<DBText>()
                                .Count(text => string.Equals(text.Layer, TypicalSectionContracts.NamesLayer, StringComparison.OrdinalIgnoreCase));
                            row["names_written_in_block"] = namesWritten;
                            Check($"section {Format(section.Station)} block read back",
                                definition.Name.StartsWith("MSV_", StringComparison.OrdinalIgnoreCase) &&
                                (section.Title?.Contains(Format(section.Station), StringComparison.Ordinal) ?? false) &&
                                namesWritten == section.NamedStrips,
                                $"{definition.Name} · title='{section.Title}' · chain={section.ChainSegments} · names={namesWritten}/{section.NamedStrips}");
                        }
                        sections.Add(row);
                    }
                }
                report["sections"] = sections;
            }
            catch (System.Exception ex)
            {
                Check("exception", false, ex.GetType().Name + ": " + ex.Message);
                report["exception"] = ex.ToString();
            }
            Finish(ed, report, checks);
        }

        private static string Format(double station) => station.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>
        /// When the firm's code drew nothing: the first station again under a few settings, each in a transaction that is
        /// aborted, with the block count and what the firm printed — one run tells which setting it refuses.
        /// </summary>
        private static List<Dictionary<string, object?>> FirmProbe(
            Autodesk.AutoCAD.ApplicationServices.Document doc, TypicalSectionsService.Result result)
        {
            var rows = new List<Dictionary<string, object?>>();
            var section = result.Sections[0];
            var prerequisites = result.ByAlignment[section.AlignmentName];
            var adapter = new FirmSectionAdapter();
            if (!adapter.TryBind(out var bindError))
            {
                rows.Add(new() { ["error"] = bindError });
                return rows;
            }
            var station = section.Station;
            var interval = FirmSectionSettings.IntervalFor(station, double.MaxValue);
            // The last settings the firm's program drew this corridor with are on the corridor itself (MSV_SETTING):
            // every 20 m along the whole alignment, old sections removed, split by pages.
            var variants = new (string Name, double Interval, double Start, double End, string RemoveOld, string Pages)[]
            {
                ("as sent: one station", interval, station, station, "0", "0"),
                ("one station · pages on", interval, station, station, "0", "1"),
                ("one station · remove old on", interval, station, station, "1", "0"),
                ("one station · both on", interval, station, station, "1", "1"),
                ("20 m · 0 .. 120 · both on", 20, 0, 120, "1", "1"),
                ("20 m · 0 .. 120 · as sent", 20, 0, 120, "0", "0"),
            };
            using (doc.LockDocument())
            {
                foreach (var variant in variants)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["variant"] = variant.Name, ["interval"] = variant.Interval, ["start"] = variant.Start, ["end"] = variant.End,
                        ["remove_old"] = variant.RemoveOld, ["pages"] = variant.Pages,
                    };
                    try
                    {
                        var overrides = FirmSectionSettings.ForStation(
                                prerequisites.CorridorName, section.AlignmentName, prerequisites.ExistingSurface,
                                prerequisites.TopSurface, prerequisites.BottomSurface, variant.Start,
                                section.HalfWidthM, section.HalfWidthM, variant.Interval)
                            .Concat(new[]
                            {
                                ("txtEndStation", FirmSectionSettings.FormatNumber(variant.End)),
                                ("cbRemoveOld", variant.RemoveOld),
                                ("cbSplitByPages", variant.Pages),
                            })
                            .ToList();
                        adapter.Configure(FirmSectionSettings.FirmDefaults, overrides);
                        using var tr = doc.Database.TransactionManager.StartTransaction();
                        row["blocks"] = adapter.ProcessAt(tr, doc.Database, prerequisites.SectionLayer, 0, -100000).Count;
                        tr.Abort();
                    }
                    catch (System.Exception ex)
                    {
                        row["exception"] = ex.Message;
                    }
                    row["firm_boxes"] = adapter.LastFirmBoxes;
                    row["firm_exceptions"] = adapter.LastFirmExceptions;
                    row["firm_messages"] = adapter.LastFirmMessages;
                    rows.Add(row);
                    doc.Editor.WriteMessage($"\n  probe · {variant.Name}: blocks={row.GetValueOrDefault("blocks")}");
                }
            }
            return rows;
        }

        private static void Finish(Editor ed, Dictionary<string, object?> report, List<Dictionary<string, object?>> checks)
        {
            var ok = checks.Count > 0 && checks.All(check => check["pass"] is true);
            report["ok"] = ok;
            report["completed_utc"] = DateTime.UtcNow.ToString("O");
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery", "smoke_typical_latest.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
            catch (System.Exception ex) { ed.WriteMessage($"\nהדוח לא נכתב: {ex.Message}"); }
            ed.WriteMessage($"\nSMOKE_TYPICAL {(ok ? "OK" : "FAILED")} · {checks.Count(c => c["pass"] is true)}/{checks.Count} · {path}\n");
        }
    }
}
