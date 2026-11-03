using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Typical sections (2026-10): from the engineer's marked picks to the firm's section blocks, one station per
    /// call of MahodCivilNet, then the Hebrew strip names from the plan written above each width. Everything the firm
    /// program would answer with a modal box is checked first; a missing corridor, surface or program stops the run
    /// before anything is drawn. A run folder records what was asked, found, drawn and named.
    /// </summary>
    internal sealed class TypicalSectionsService
    {
        /// <param name="CorridorChoices">The engineer's choice in this session, alignment → corridor, for alignments
        /// where more than one corridor covers the picked stations.</param>
        /// <param name="ExistingSurfaceChoice">The engineer's choice of existing ground in this session, asked when
        /// several TINs could be it.</param>
        internal sealed record Request(
            IReadOnlyList<TypicalSectionPick> Picks, ProjectProfile Profile, string? ProfileHash,
            IReadOnlyDictionary<string, string>? CorridorChoices = null, string? ExistingSurfaceChoice = null);

        internal sealed record Prerequisites(
            string AlignmentName, string CorridorName, string ExistingSurface, string TopSurface,
            string BottomSurface, string SectionLayer);

        /// <summary>A switched-off baseline of <paramref name="Corridor"/> along <paramref name="Alignment"/>, to switch on at these stations.</summary>
        internal sealed record SwitchOnRequest(string Corridor, string Alignment, IReadOnlyList<double> Stations);

        internal sealed class SectionOutcome
        {
            public required string LineHandle { get; init; }
            public required string AlignmentName { get; init; }
            /// <summary>Where the section is cut: the corridor station nearest the pick.</summary>
            public required double Station { get; init; }
            /// <summary>Where the engineer marked it.</summary>
            public required double PickedStation { get; init; }
            public required double HalfWidthM { get; init; }
            /// <summary>What the firm's code printed on the command line while drawing this section.</summary>
            public List<string> FirmMessages { get; } = new();
            public string? BlockHandle { get; set; }
            /// <summary>The plan layer (lanes, stages, objects) drawn above the firm block, when the plan had road lines.</summary>
            public string? OverlayHandle { get; set; }
            public string? BlockName { get; set; }
            public string? Title { get; set; }
            public int ChainSegments { get; set; }
            public int NamedStrips { get; set; }
            public int UnmatchedStrips { get; set; }
            public List<string> Notes { get; } = new();
            /// <summary>The block reader's finding codes, kept for the run folder; <see cref="Notes"/> has them in Hebrew.</summary>
            public List<string> FrameFindings { get; } = new();
            public string? Error { get; set; }
            public bool Created => BlockHandle != null && Error == null;
        }

        internal sealed class Result
        {
            public required string RunId { get; init; }
            public List<string> Blockers { get; } = new();
            public List<string> Warnings { get; } = new();
            public Dictionary<string, Prerequisites> ByAlignment { get; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Alignments where several corridors cover the picked stations: the engineer chooses, then the run repeats.</summary>
            public Dictionary<string, IReadOnlyList<string>> CorridorChoicesNeeded { get; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Several TINs could be the existing ground: the engineer chooses one in the window, then the run repeats.</summary>
            public List<string> ExistingSurfaceChoicesNeeded { get; } = new();
            /// <summary>Chosen corridors Civil has not rebuilt: the tool can rebuild them when the engineer says so.</summary>
            public List<string> CorridorsToRebuild { get; } = new();
            /// <summary>
            /// Baselines (and the regions at the marked stations) the drawing has switched off — "build" unticked in the
            /// corridor's Parameters, so Civil 3D never computes them (6403's R-14 along RAMP-14, 2026-11-03). The tool
            /// switches them on before the rebuild.
            /// </summary>
            public List<SwitchOnRequest> CorridorsToSwitchOn { get; } = new();
            /// <summary>Chosen corridors with no corridor surface at all: the tool can add "&lt;corridor&gt; - Top" from the Top links.</summary>
            public List<string> CorridorsWithoutSurface { get; } = new();
            public bool CanFixCorridors => CorridorsToRebuild.Count > 0 || CorridorsWithoutSurface.Count > 0;
            /// <summary>
            /// The lines of <see cref="Blockers"/> the tool mends itself (rebuild, add the top surface). When every
            /// blocker is one of them the run is one step short, not failed: the window says so in green (2026-11-03,
            /// a coworker's R-14 was only unbuilt and read as two errors).
            /// </summary>
            public List<string> FixSteps { get; } = new();
            public bool OnlyFixSteps => Blockers.Count > 0 && CanFixCorridors && Blockers.All(FixSteps.Contains);
            /// <summary>Why each corridor was chosen (session, profile, covers the stations…), for the run folder.</summary>
            public Dictionary<string, string> CorridorChoiceReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<SectionOutcome> Sections { get; } = new();
            public string? ArtifactPath { get; set; }
            public (double MinX, double MinY, double MaxX, double MaxY)? CreatedExtents { get; set; }
            public int CreatedCount => Sections.Count(section => section.Created);
            public int NamedCount => Sections.Sum(section => section.NamedStrips);
            public int UnmatchedCount => Sections.Sum(section => section.UnmatchedStrips);
            public bool Stopped => Blockers.Count > 0 || Sections.Any(section => section.Error != null);
        }

        public Result Create(Document doc, Request request, StageLog? log)
        {
            var started = DateTime.UtcNow;
            var result = new Result { RunId = RunManifest.NewRunId("typical-sections", "create") };
            var db = doc.Database;
            var picks = request.Picks
                .OrderBy(pick => pick.AlignmentName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pick => pick.Station)
                .ToList();
            if (picks.Count == 0)
            {
                result.Blockers.Add("לא סומן אף חתך.");
                return Finish(result, request, started);
            }
            // A drafted drawing has no corridor for the firm's program: its sections are read off the plan.
            if (picks.All(pick => pick.FromPlan))
                return CreateDrafted(doc, request, picks, result, log, started);

            // ----------------------------------------------------------------- guards
            var workingDatabase = HostApplicationServices.WorkingDatabase?.UnmanagedObject ?? IntPtr.Zero;
            var activeDatabase = AcadApp.DocumentManager.MdiActiveDocument?.Database?.UnmanagedObject ?? IntPtr.Zero;
            if (!FirmSectionAdapter.TargetIsActive(db.UnmanagedObject, workingDatabase, activeDatabase))
            {
                result.Blockers.Add(FirmSectionAdapter.NotActiveMessageHe);
                return Finish(result, request, started);
            }
            var adapter = new FirmSectionAdapter();
            if (!adapter.TryBind(out var bindError))
            {
                result.Blockers.Add(bindError ?? FirmSectionAdapter.NotFoundMessageHe);
                return Finish(result, request, started);
            }

            // ---------------------------------------------------------- prerequisites
            log?.Begin("typical.precheck", $"picks={picks.Count}");
            var civilDoc = CivilApplication.ActiveDocument;
            var corridorStations = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            var stationing = new Dictionary<string, (List<(double, double)> Equations, double EndingStation)>(StringComparer.OrdinalIgnoreCase);
            // One line per reason: the same missing surface or unbuilt corridor reached from two alignments is one fix.
            void Block(string reason)
            {
                if (!result.Blockers.Contains(reason, StringComparer.Ordinal)) result.Blockers.Add(reason);
            }
            void Step(string reason)
            {
                Block(reason);
                if (!result.FixSteps.Contains(reason, StringComparer.Ordinal)) result.FixSteps.Add(reason);
            }
            var cuttable = new List<(string Alignment, string Corridor, string Top, string Bottom)>();
            var everyCorridor = new List<TypicalCorridorChoiceLogic.Candidate>();
            string? projectSettings;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var tins = TinNames(tr, civilDoc);
                // The office's own section settings, saved in the drawing by the firm's dialog: drawn with them, a typical
                // section is the office's section (6403, 2026-11-02 — the 794 defaults dropped every lane edge and
                // the neighbouring corridors the office draws).
                projectSettings = FirmSectionSettings.FromStored(ReadStoredFirmSettings(tr, db));
                if (projectSettings != null)
                    result.Warnings.Add("הגדרות תוכנת החתכים נלקחו מהשרטוט (ההגדרות שהמשרד שמר בהרצה האחרונה).");
                foreach (var alignmentName in picks.Select(pick => pick.AlignmentName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var corridors = CorridorsOn(tr, civilDoc, alignmentName);
                    everyCorridor.AddRange(corridors.Select(c => c.Candidate));
                    var stations = picks
                        .Where(p => string.Equals(p.AlignmentName, alignmentName, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Station).ToList();
                    string? sessionChoice = null;
                    request.CorridorChoices?.TryGetValue(alignmentName, out sessionChoice);
                    var choice = TypicalCorridorChoiceLogic.Choose(
                        corridors.Select(c => c.Candidate).ToList(), stations,
                        request.Profile.Sections.Typical?.CorridorName, sessionChoice);
                    if (choice.Chosen == null)
                    {
                        if (choice.AskAmong.Count > 0)
                        {
                            result.CorridorChoicesNeeded[alignmentName] = choice.AskAmong;
                            Block($"לתוואי {alignmentName} כמה קורידורים מכסים את התחנות שסומנו ({string.Join(", ", choice.AskAmong)}) — יש לבחור אחד.");
                        }
                        else if (choice.Reason == TypicalCorridorChoiceLogic.Reason.NoCorridor)
                            Block($"לתוואי {alignmentName} אין קורידור — תוכנת החתכים המשרדית מציירת לאורך קורידור מעובד.");
                        else
                            Block(FormattableString.Invariant(
                                $"אף קורידור של התוואי {alignmentName} אינו מכסה את כל התחנות שסומנו ({string.Join(", ", stations.Select(s => s.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}) — יש לסמן חתכים בתוך אזורי הקורידור."));
                        continue;
                    }
                    var chosen = corridors.First(c => string.Equals(c.Name, choice.Chosen, StringComparison.OrdinalIgnoreCase));
                    result.CorridorChoiceReasons[alignmentName] = chosen.Name + " · " + choice.Reason;
                    if (corridors.Count > 1)
                        result.Warnings.Add($"לתוואי {alignmentName} יש {corridors.Count} קורידורים; נבחר {chosen.Name} ({ChoiceReasonHe(choice.Reason)}).");
                    foreach (var station in stations.Where(s => !TypicalCorridorChoiceLogic.Covers(chosen.Candidate, s)))
                        Block(FormattableString.Invariant(
                            $"תחנה {station:0.00} בתוואי {alignmentName} נמצאת מחוץ לאזורי הקורידור {chosen.Name}."));
                    if (!FirstBaselineHasProfile(tr, chosen.Id))
                        Block($"לקורידור {chosen.Name} אין פרופיל תכנון (הפרופיל שהוא נשען עליו נמחק או לא נטען) — תוכנת החתכים לא יכולה לחתוך אותו. יש לחבר לו פרופיל במאפייני הקורידור (Baselines) ולבנות אותו מחדש.");
                    var computed = AssemblyStations(tr, chosen.Id, alignmentName);
                    // An unbuilt corridor has no computed sections either: one fix, one line — a step the tool takes, when
                    // this computer can build it: the office's corridors need the office's subassemblies, and a rebuild
                    // without them computes nothing and wipes what was built (CorridorRebuildLogic, 2026-11-03).
                    var switchedOff = IsSwitchedOff(tr, chosen.Id, alignmentName, stations);
                    var unbuilt = chosen.Candidate.OutOfDate || computed.Count == 0 || switchedOff;
                    var missing = unbuilt ? MissingSubassemblyFiles(tr, chosen.Id) : Array.Empty<string>();
                    var year = CorridorRebuildLogic.YearOf(SubassemblyVersionFolder);
                    var civil = year.Length > 0 ? "Civil 3D " + year : "Civil 3D הזה";
                    if (unbuilt && missing.Count > 0 && computed.Count > 0 && !switchedOff)
                    {
                        // Out of date but built: its last build is cut as it is, rather than lost to a rebuild.
                        result.Warnings.Add($"הקורידור {chosen.Name} אינו מעובד (Out of date), ואי אפשר לבנות אותו מחדש במחשב הזה (חסר {string.Join(", ", missing)} ל-{civil}) — החתכים נחתכו מהבנייה האחרונה שלו.");
                        unbuilt = false;
                    }
                    if (unbuilt && missing.Count > 0)
                    {
                        Block($"את הקורידור {chosen.Name} אי אפשר לבנות במחשב הזה: הוא בנוי מה-Subassemblies של המשרד ({string.Join(", ", missing)}), והם לא מותקנים כאן ל-{civil}. יש להתקין את חבילת MAHOD_3D של המשרד, או לפתוח את השרטוט במחשב שהיא מותקנת בו, וליצור שוב.");
                    }
                    else if (unbuilt)
                    {
                        Step(switchedOff
                            ? $"להפעיל את הקורידור {chosen.Name} לאורך {alignmentName} ולבנות אותו — הוא כבוי במאפייני הקורידור (Parameters), ולכן Civil 3D לא בונה אותו."
                            : chosen.Candidate.OutOfDate
                                ? $"לבנות מחדש את הקורידור {chosen.Name} — הוא אינו מעובד (Out of date)."
                                : $"לבנות מחדש את הקורידור {chosen.Name} — אין בו עדיין חתכים מחושבים לאורך {alignmentName}.");
                        if (!result.CorridorsToRebuild.Contains(chosen.Name, StringComparer.OrdinalIgnoreCase))
                            result.CorridorsToRebuild.Add(chosen.Name);
                        if (switchedOff)
                            result.CorridorsToSwitchOn.Add(new SwitchOnRequest(chosen.Name, alignmentName, stations));
                    }
                    // The firm's program draws the road from the corridor's coded links (by links): with no code at all it
                    // draws ground only, and no Top surface can be built either (6403 ROAD-16-ZMANI, 2026-11-02). An
                    // unbuilt corridor lists no codes until it is built (a coworker's R-14, 2026-11-03): judged after the
                    // rebuild, on the retry.
                    else if (!CorridorHasLinkCodes(tr, chosen.Id))
                        Block($"בקורידור {chosen.Name} אין קודי קישור (Top / Bot) — ה-Assembly שלו אינו מקודד, ולכן תוכנת החתכים לא תשרטט בו את הכביש. יש לתקן את ה-Assembly ולבנות את הקורידור מחדש.");
                    corridorStations[alignmentName] = computed;
                    stationing[alignmentName] = Stationing(tr, civilDoc, alignmentName);

                    var (top, bottom) = TypicalCorridorChoiceLogic.Surfaces(
                        chosen.Candidate.SurfaceNames, tins,
                        request.Profile.Sections.Typical?.TopSurfaceName, request.Profile.Sections.Typical?.BottomSurfaceName);
                    // The office cut this corridor against these surfaces last time (a temporary corridor against the main
                    // road's finished surfaces): the same surfaces give the same section, and need no new Top surface.
                    var stored = FirmSectionSettings.StoredSurfacesFor(projectSettings, chosen.Name);
                    if (request.Profile.Sections.Typical?.TopSurfaceName == null && stored.Top != null &&
                        tins.Contains(stored.Top, StringComparer.OrdinalIgnoreCase))
                    {
                        top = stored.Top;
                        bottom = stored.Bottom != null && tins.Contains(stored.Bottom, StringComparer.OrdinalIgnoreCase) ? stored.Bottom : top;
                    }
                    if (top == null)
                    {
                        // No corridor surface at all is mendable: "<corridor> - Top" from the Top links, as the firm names it.
                        // Surfaces that exist but none reads as the top are the engineer's to name.
                        // The surface is added and built by a rebuild: only where this computer can build the corridor.
                        var cannotBuild = missing.Count > 0 ? missing : MissingSubassemblyFiles(tr, chosen.Id);
                        if (chosen.Candidate.SurfaceNames.Count == 0 && cannotBuild.Count > 0)
                        {
                            Block($"לקורידור {chosen.Name} אין משטח קורידור (Top), ובמחשב הזה אי אפשר לבנות אותו כדי להוסיף משטח: חסרים ה-Subassemblies של המשרד ({string.Join(", ", cannotBuild)}) ל-{civil}. יש להתקין את חבילת MAHOD_3D של המשרד, או להוסיף את המשטח במחשב שהיא מותקנת בו.");
                        }
                        else if (chosen.Candidate.SurfaceNames.Count == 0)
                        {
                            Step($"להוסיף לקורידור {chosen.Name} משטח עליון (\"{chosen.Name} - Top\") ולבנות אותו מחדש.");
                            if (!result.CorridorsWithoutSurface.Contains(chosen.Name, StringComparer.OrdinalIgnoreCase))
                                result.CorridorsWithoutSurface.Add(chosen.Name);
                        }
                        else
                        {
                            Block($"לקורידור {chosen.Name} יש משטחי קורידור ({string.Join(", ", chosen.Candidate.SurfaceNames)}) אבל אף אחד מהם אינו העליון (Top) — יש לתת לעליון שם שמכיל Top.");
                        }
                    }
                    else
                        cuttable.Add((alignmentName, chosen.Name, top, bottom ?? top));
                }

                // The existing ground belongs to the drawing, not to an alignment: decided once.
                IReadOnlyList<string> patterns = request.Profile.Sections.Projection.ExistingSurfacePatterns.Count > 0
                    ? request.Profile.Sections.Projection.ExistingSurfacePatterns
                    : SectionSourceSelectionLogic.DefaultExistingSurfacePatterns;
                var ground = TypicalCorridorChoiceLogic.ExistingGround(
                    tins,
                    everyCorridor.SelectMany(c => c.SurfaceNames).ToList(),
                    everyCorridor.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    patterns, ClInstructionReader.WildcardMatch,
                    request.Profile.Sections.Typical?.ExistingSurfaceName,
                    request.ExistingSurfaceChoice ?? StoredGround(projectSettings, tins));
                if (ground.AskAmong.Count > 0)
                {
                    result.ExistingSurfaceChoicesNeeded.AddRange(ground.AskAmong);
                    Block($"כמה משטחים יכולים להיות הקרקע הקיימת ({string.Join(", ", ground.AskAmong)}) — יש לבחור אחד.");
                }
                else if (ground.Problem != null)
                {
                    Block(ground.Problem);
                }
                if (ground.Chosen != null)
                    foreach (var (alignment, corridor, top, bottom) in cuttable)
                        result.ByAlignment[alignment] = new Prerequisites(
                            alignment, corridor, ground.Chosen, top, bottom, alignment + "_SectionView");
                tr.Abort();
            }
            log?.End("typical.precheck", $"blockers={result.Blockers.Count}");
            if (result.Blockers.Count > 0) return Finish(result, request, started);

            // ------------------------------------------------------ firm sections
            log?.Begin("typical.firm_sections", $"count={picks.Count}");
            TypicalSectionLayoutLogic.LayoutState layout;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var (originX, originY) = SectionPlanService.ResolveLayoutOriginFor(db, tr);
                layout = TypicalSectionLayoutLogic.LayoutState.Start(originX, originY);
                tr.Abort();
            }
            (double MinX, double MinY, double MaxX, double MaxY)? previousExtents = null;
            var cut = new Dictionary<string, TypicalSectionPick>(StringComparer.OrdinalIgnoreCase);
            using (doc.LockDocument())
            {
                foreach (var picked in picks)
                {
                    // The firm's code draws the road only where the corridor applied its assembly: cut at the nearest.
                    var station = TypicalSectionPickLogic.SnapToCorridorStation(picked.Station, corridorStations[picked.AlignmentName])
                                  ?? picked.Station;
                    var outcome = new SectionOutcome
                    {
                        LineHandle = picked.LineHandle, AlignmentName = picked.AlignmentName,
                        Station = station, PickedStation = picked.Station, HalfWidthM = picked.HalfWidthM,
                    };
                    result.Sections.Add(outcome);
                    var prerequisites = result.ByAlignment[picked.AlignmentName];
                    var (equations, endingStation) = stationing[picked.AlignmentName];
                    try
                    {
                        var pick = picked;
                        if (Math.Abs(station - picked.Station) >= TypicalSectionContracts.SnapNoteThresholdM)
                        {
                            pick = TypicalSectionPicker.MoveTo(db, civilDoc, picked, station);
                            outcome.Notes.Add(FormattableString.Invariant(
                                $"החתך עבר מתחנה {picked.Station:0.00} לתחנה {station:0.00}, תחנת הקורידור הקרובה."));
                        }
                        cut[pick.LineHandle] = pick;

                        var interval = FirmSectionSettings.IntervalFor(
                            FirmSectionSettings.DisplayStation(station, equations),
                            FirmSectionSettings.DisplayStation(endingStation, equations));
                        var overrides = FirmSectionSettings.ForStation(
                            prerequisites.CorridorName, pick.AlignmentName, prerequisites.ExistingSurface,
                            prerequisites.TopSurface, prerequisites.BottomSurface, station,
                            pick.HalfWidthM, pick.HalfWidthM, interval);
                        adapter.Configure(projectSettings ?? FirmSectionSettings.FirmDefaults, overrides);
                        var slot = TypicalSectionLayoutLogic.NextSlot(layout, previousExtents);
                        layout = slot.State;

                        IReadOnlyList<Handle> added;
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            added = adapter.ProcessAt(tr, db, prerequisites.SectionLayer, slot.X, slot.Y);
                            if (added.Count > 1)
                            {
                                EraseSections(tr, db, added.Skip(1));
                                outcome.Notes.Add($"תוכנת החתכים שרטטה {added.Count} בלוקים בקריאה אחת; נשאר הראשון.");
                            }
                            tr.Commit();
                        }
                        outcome.FirmMessages.AddRange(adapter.LastFirmBoxes.Select(box => "[box] " + box));
                        outcome.FirmMessages.AddRange(adapter.LastFirmExceptions.Select(thrown => "[thrown] " + thrown));
                        outcome.FirmMessages.AddRange(adapter.LastFirmMessages);
                        if (added.Count == 0)
                        {
                            var reason = adapter.LastFirmBoxes.LastOrDefault() ?? adapter.LastFirmMessages.LastOrDefault(line =>
                                line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0);
                            outcome.Error = FormattableString.Invariant(
                                $"תוכנת החתכים של מהוד לא שרטטה חתך בתחנה {station:0.00}") +
                                (reason != null ? " — " + reason : " — ראה את שורת הפקודה של Civil 3D.");
                            previousExtents = null;
                            break;
                        }
                        outcome.BlockHandle = added[0].ToString();
                        previousExtents = ReadBlockExtents(db, added[0], outcome, result);
                        previousExtents = AddPlanOverlay(db, pick, added[0], prerequisites.CorridorName, prerequisites.ExistingSurface,
                            outcome, result, previousExtents);
                    }
                    catch (Exception ex)
                    {
                        outcome.Error = ex.Message;
                        break;
                    }
                }
            }
            log?.End("typical.firm_sections", $"created={result.CreatedCount}");

            // The office's sheet (DD-5001: A0, 1:100, the panels stacked) is its own command: building a layout right after
            // the firm's section program took Civil 3D down (2026-11-02).
            if (result.Sections.Any(s => s.OverlayHandle != null))
                result.Warnings.Add($"לגיליון החתכים הטיפוסיים (A0, קנ\"מ 1:100): הפקודה {Commands.CivilDeliveryCommandNames.TypicalSheet}.");

            // -------------------------------------------------------- strip names
            // A section with the plan layer above it is named there already: the naming pass collects every plan mark of
            // the host and its XREFs (6403: 36 s collect + 24 s matching for nothing new, then a 20 s regen), so it runs
            // only for the sections the plan layer could not cover.
            var created = result.Sections.Where(section => section.Created && section.OverlayHandle == null).ToList();
            if (created.Count > 0)
            {
                log?.Begin("typical.strip_names", $"blocks={created.Count}");
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var resolver = TypicalSectionStripResolver.Collect(tr, db, request.Profile, log);
                    foreach (var finding in resolver.CollectionFindings.Where(f => f.Severity >= FindingSeverity.Warning))
                        result.Warnings.Add(finding.Title);
                    if (resolver.PlanMarkTotal == 0)
                        result.Warnings.Add(request.Profile.Sections.Projection.PlanMarkRules.Count == 0
                            ? "לפרויקט עוד לא הוגדרו שכבות התכנית (אבני שפה, נתיבים, מדרכות) — שמות הרצועות לא נכתבו. הגדרת הפרויקט מוסיפה אותן."
                            : "לא נמצאו בשרטוט קווי תכנית בשכבות שהוגדרו לפרויקט — שמות הרצועות לא נכתבו.");
                    foreach (var outcome in created)
                    {
                        var pick = cut[outcome.LineHandle];
                        try
                        {
                            NameStrips(tr, db, resolver, pick, outcome);
                        }
                        catch (Exception ex)
                        {
                            outcome.Notes.Add("שמות הרצועות לא נכתבו: " + ex.Message);
                        }
                    }
                    tr.Commit();
                }
                // WriteNames marks each changed reference for redraw; a full regen is only a fallback for written names.
                if (created.Any(section => section.NamedStrips > 0))
                    try { doc.Editor.Regen(); } catch { /* display only */ }
                log?.End("typical.strip_names", $"named={result.NamedCount} unmatched={result.UnmatchedCount}");
            }

            return Finish(result, request, started);
        }

        /// <summary>
        /// Typical sections on a drafted drawing (no Civil alignment, the axes are plan polylines): each pick's chord cuts
        /// the plan — host and every XREF — and <see cref="DraftedTypicalSectionService"/> draws the section from what it
        /// crossed. Laid out on the same grid as the firm's blocks. The window reports widths, names and what was found.
        /// </summary>
        private Result CreateDrafted(Document doc, Request request, List<TypicalSectionPick> picks, Result result,
            StageLog? log, DateTime started)
        {
            var db = doc.Database;
            log?.Begin("typical.drafted", $"picks={picks.Count}");
            result.Warnings.Add("בשרטוט אין קורידור — החתכים נקראו מקווי התנוחה (כולל XREF) ושורטטו סכמטית, ללא גבהים.");
            TypicalSectionLayoutLogic.LayoutState layout;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var (originX, originY) = SectionPlanService.ResolveLayoutOriginFor(db, tr);
                layout = TypicalSectionLayoutLogic.LayoutState.Start(originX, originY);
                tr.Abort();
            }
            (double MinX, double MinY, double MaxX, double MaxY)? previousExtents = null;
            using (doc.LockDocument())
            {
                foreach (var pick in picks)
                {
                    var outcome = new SectionOutcome
                    {
                        LineHandle = pick.LineHandle, AlignmentName = pick.AlignmentName,
                        Station = pick.Station, PickedStation = pick.Station, HalfWidthM = pick.HalfWidthM,
                    };
                    result.Sections.Add(outcome);
                    try
                    {
                        using var tr = db.TransactionManager.StartTransaction();
                        var (crossings, objects) = DraftedTypicalSectionService.Cut(tr, db, pick);
                        var panels = DraftedTypicalSectionLogic.BuildPanels(crossings, objects, pick.HalfWidthM);
                        var title = FormattableString.Invariant($"חתך טיפוסי — {pick.AlignmentName}, תחנה {pick.Station:0.00}");
                        // Rows grow upward; the block hangs below its insertion point by its chain, names and title.
                        var slot = TypicalSectionLayoutLogic.NextSlot(layout, previousExtents);
                        layout = slot.State;
                        var referenceId = DraftedTypicalSectionService.Draw(tr, db, panels, pick, slot.X, slot.Y);
                        tr.Commit();
                        RedrawCommitted(db, referenceId);

                        outcome.BlockHandle = referenceId.Handle.ToString();
                        outcome.Title = title;
                        outcome.ChainSegments = panels.Sum(p => p.Section.Strips.Count);
                        outcome.NamedStrips = panels.Sum(p => p.Section.NamedCount);
                        outcome.UnmatchedStrips = outcome.ChainSegments - outcome.NamedStrips;
                        foreach (var panel in panels) outcome.Notes.AddRange(panel.Section.Findings.Select(f => panel.Title + ": " + f));
                        foreach (var panel in panels) outcome.Notes.Add(panel.Title + " — " + DraftedSummaryHe(panel.Section));
                        previousExtents = ReadBlockExtents(db, referenceId.Handle, outcome, result);
                    }
                    catch (Exception ex)
                    {
                        outcome.Error = "החתך לא שורטט: " + ex.Message;
                        MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("[TypicalSections] drafted section failed", ex);
                    }
                }
            }
            // Each drawn block marks itself for redraw (DraftedTypicalSectionService.Draw): no full regen of a large drawing.
            log?.End("typical.drafted", $"created={result.CreatedCount} named={result.NamedCount}");
            return Finish(result, request, started);
        }

        /// <summary>
        /// Draws the plan's lanes, stages and objects above a firm section (<see cref="DraftedTypicalSectionService.DrawOverPlanSection"/>)
        /// and returns the extents of both together, so the next slot of the grid clears the overlay too. A failure only
        /// leaves a note: the firm section stands on its own.
        /// </summary>
        private static (double MinX, double MinY, double MaxX, double MaxY)? AddPlanOverlay(Database db, TypicalSectionPick pick,
            Handle firmBlock, string corridorName, string groundSurface, SectionOutcome outcome, Result result,
            (double MinX, double MinY, double MaxX, double MaxY)? blockExtents)
        {
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var overlayId = DraftedTypicalSectionService.DrawOverPlanSection(tr, db, pick, firmBlock, corridorName, groundSurface,
                    out var panels, out var reason);
                if (overlayId is not { } id)
                {
                    tr.Abort();
                    if (reason != null) outcome.Notes.Add(reason);
                    return blockExtents;
                }
                // The naming pass is skipped for this section (the plan layer names it): its title and counts come from here.
                try { outcome.Title = FirmSectionBlockReader.Read(tr, db, firmBlock).Title; }
                catch { /* the window falls back to the block name */ }
                tr.Commit();
                RedrawCommitted(db, id);
                outcome.OverlayHandle = id.Handle.ToString();
                outcome.ChainSegments = panels.Sum(p => p.Section.Strips.Count);
                outcome.NamedStrips = panels.Sum(p => p.Section.NamedCount);
                outcome.UnmatchedStrips = outcome.ChainSegments - outcome.NamedStrips;
                foreach (var panel in panels) outcome.Notes.AddRange(panel.Section.Findings.Select(f => panel.Title + ": " + f));
                foreach (var panel in panels) outcome.Notes.Add(panel.Title + " — " + DraftedSummaryHe(panel.Section));
                using var read = db.TransactionManager.StartOpenCloseTransaction();
                var e = OfficeTypicalRenderer.FrameExtents(read, id) ?? ((Entity)read.GetObject(id, OpenMode.ForRead)).GeometricExtents;
                var overlay = (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
                result.CreatedExtents = result.CreatedExtents is { } all
                    ? (Math.Min(all.MinX, overlay.Item1), Math.Min(all.MinY, overlay.Item2), Math.Max(all.MaxX, overlay.Item3), Math.Max(all.MaxY, overlay.Item4))
                    : overlay;
                return blockExtents is { } b
                    ? (Math.Min(b.MinX, overlay.Item1), Math.Min(b.MinY, overlay.Item2), Math.Max(b.MaxX, overlay.Item3), Math.Max(b.MaxY, overlay.Item4))
                    : overlay;
            }
            catch (Exception ex)
            {
                outcome.Notes.Add("שכבת הרצועות מהתנוחה לא שורטטה: " + ex.Message);
                MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Error("[TypicalSections] plan overlay failed", ex);
                return blockExtents;
            }
        }

        /// <summary>One line on what the cut found, so the engineer sees the section was read and from what.</summary>
        internal static string DraftedSummaryHe(DraftedTypicalSectionLogic.DraftedSection section)
        {
            int Count(string kind) => section.Objects.Count(o => o.Kind == kind);
            var parts = new List<string> { $"{section.Strips.Count} רוחבים", $"{section.NamedCount} עם שם" };
            if (section.Zones.Count > 0) parts.Add($"{section.Zones.Count} תחומי עבודה");
            if (section.Fences.Count > 0) parts.Add($"{section.Fences.Count} גדרות");
            if (section.Barriers.Count > 0) parts.Add($"{section.Barriers.Count} מעקות");
            if (Count(DraftedTypicalSectionLogic.ObjectKind.Tree) > 0) parts.Add($"{Count(DraftedTypicalSectionLogic.ObjectKind.Tree)} עצים");
            if (Count(DraftedTypicalSectionLogic.ObjectKind.Light) > 0) parts.Add($"{Count(DraftedTypicalSectionLogic.ObjectKind.Light)} עמודי תאורה");
            if (Count(DraftedTypicalSectionLogic.ObjectKind.Sign) > 0) parts.Add($"{Count(DraftedTypicalSectionLogic.ObjectKind.Sign)} תמרורים");
            return "נקרא מהתנוחה: " + string.Join(", ", parts) + ".";
        }

        /// <summary>
        /// Mends what the engineer agreed to in the blocked window: adds "&lt;corridor&gt; - Top" from the Top links (as
        /// break lines) to a corridor with no corridor surface, then rebuilds every corridor named. One Hebrew line per
        /// corridor; a failure is reported, never thrown, so the others are still mended.
        /// </summary>
        public static List<string> FixCorridors(
            Document doc, IEnumerable<string> rebuild, IEnumerable<string> addTop, StageLog? log,
            IReadOnlyList<SwitchOnRequest>? switchOn = null)
        {
            var lines = new List<string>();
            var withTop = new HashSet<string>(addTop, StringComparer.OrdinalIgnoreCase);
            var names = rebuild.Concat(withTop).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var civilDoc = CivilApplication.ActiveDocument;
            using (doc.LockDocument())
            {
                foreach (var name in names)
                {
                    log?.Begin("typical.fix_corridor", name);
                    try
                    {
                        using var tr = doc.Database.TransactionManager.StartTransaction();
                        CivilDb.Corridor? corridor = null;
                        foreach (ObjectId id in civilDoc.CorridorCollection)
                        {
                            if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Corridor candidate &&
                                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                            { corridor = candidate; break; }
                        }
                        if (corridor == null)
                        {
                            lines.Add($"הקורידור {name} לא נמצא בשרטוט.");
                            continue;
                        }
                        corridor.UpgradeOpen();
                        var switched = new List<string>();
                        foreach (var request in (switchOn ?? Array.Empty<SwitchOnRequest>())
                                     .Where(r => string.Equals(r.Corridor, name, StringComparison.OrdinalIgnoreCase)))
                            if (SwitchOn(tr, corridor, request)) switched.Add(request.Alignment);
                        var added = false;
                        if (withTop.Contains(name) && corridor.CorridorSurfaces.Count == 0)
                        {
                            var surface = corridor.CorridorSurfaces.Add(corridor.Name + " - Top");
                            surface.AddLinkCode("Top", true);
                            added = true;
                        }
                        corridor.Rebuild();
                        tr.Commit();
                        lines.Add(added
                            ? $"לקורידור {name} נוסף המשטח \"{name} - Top\" והוא נבנה מחדש."
                            : switched.Count > 0
                                ? $"הקורידור {name} הופעל לאורך {string.Join(", ", switched)} ונבנה מחדש."
                                : $"הקורידור {name} נבנה מחדש.");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"הקורידור {name} לא תוקן: {ex.Message}");
                    }
                    finally
                    {
                        log?.End("typical.fix_corridor", name);
                    }
                }
            }
            return lines;
        }

        internal const string NoPlanMarksNote = "לא נמצאו סימוני תכנית (אבני שפה, נתיבים, מדרכות) שחוצים את קו הסימון — שמות הרצועות לא נכתבו.";

        private static void NameStrips(
            Transaction tr, Database db, TypicalSectionStripResolver resolver, TypicalSectionPick pick, SectionOutcome outcome)
        {
            var block = FirmSectionBlockReader.Read(tr, db, new Handle(Convert.ToInt64(outcome.BlockHandle!, 16)));
            outcome.Title = block.Title;
            var frame = FirmSectionFrameLogic.Decode(block.Input);
            outcome.FrameFindings.AddRange(frame.Findings);
            outcome.Notes.AddRange(frame.Findings.Select(FirmSectionFrameLogic.Finding.DescribeHe));
            if (!frame.HasHorizontalFrame || frame.Segments.Count == 0)
            {
                outcome.Notes.Add("שרשרת הרוחבים בבלוק לא נקראה — לא נכתבו שמות רצועות.");
                return;
            }
            outcome.ChainSegments = frame.Segments.Count;

            var strips = resolver.Resolve(pick);
            if (strips.Failure != null)
            {
                outcome.Notes.Add(strips.Failure);
                return;
            }
            // Nothing in the plan to match is not a width without a match: one note, no count (live 2026-10-28, a
            // drawing with no curb or lane lines showed "60 ללא התאמה" and 66 notes).
            if (strips.PlanMarkCount == 0)
            {
                outcome.Notes.Add(NoPlanMarksNote);
                return;
            }

            var match = TypicalSectionStripMatchLogic.Match(frame.Segments, strips.Named);
            var names = match.Matches.Where(m => m.IsMatched).Select(m => (m.SegmentIndex, m.Label!)).ToList();
            FirmSectionBlockReader.WriteNames(tr, db, block, frame, names, TypicalSectionContracts.NamesLayer);
            outcome.NamedStrips = match.MatchedCount;
            outcome.UnmatchedStrips = match.UnmatchedCount;
            foreach (var miss in match.Matches.Where(m => !m.IsMatched))
            {
                var segment = frame.Segments[miss.SegmentIndex];
                outcome.Notes.Add(FormattableString.Invariant(
                    $"רוחב {segment.WidthM:0.00} מ' ({segment.FromOffsetM:0.00}..{segment.ToOffsetM:0.00}) ללא שם — {TypicalSectionStripMatchLogic.Reason.DescribeHe(miss.Reason)}."));
            }
        }

        private static (double, double, double, double)? ReadBlockExtents(Database db, Handle handle, SectionOutcome outcome, Result result)
        {
            using var tr = db.TransactionManager.StartTransaction();
            if (!db.TryGetObjectId(handle, out var id) || id.IsNull) return null;
            if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference reference) return null;
            outcome.BlockName = ((BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead)).Name;
            try
            {
                var e = reference.GeometricExtents;
                var extents = (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
                result.CreatedExtents = result.CreatedExtents is { } all
                    ? (Math.Min(all.MinX, extents.Item1), Math.Min(all.MinY, extents.Item2),
                       Math.Max(all.MaxX, extents.Item3), Math.Max(all.MaxY, extents.Item4))
                    : extents;
                return extents;
            }
            catch { return null; }
        }

        private sealed record CorridorOnAlignment(string Name, ObjectId Id, TypicalCorridorChoiceLogic.Candidate Candidate);

        /// <summary>
        /// The stations where the corridor applied its assembly along the alignment, read the way the firm's code reads
        /// them (MahodCivilNet <c>getAppliedAssemblies</c>: each applied assembly's first calculated point, its station
        /// to the baseline). Only at these does the firm's section show the road.
        /// </summary>
        private static List<double> AssemblyStations(Transaction tr, ObjectId corridorId, string alignmentName)
        {
            var stations = new List<double>();
            if (tr.GetObject(corridorId, OpenMode.ForRead) is not CivilDb.Corridor corridor) return stations;
            for (var i = 0; i < corridor.Baselines.Count; i++)
            {
                var baseline = corridor.Baselines[i];
                if (tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) is not CivilDb.Alignment alignment ||
                    !string.Equals(alignment.Name, alignmentName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                {
                    try
                    {
                        foreach (CivilDb.AppliedAssembly assembly in region.AppliedAssemblies)
                        {
                            try
                            {
                                if (assembly.Points.Count > 0)
                                    stations.Add(assembly.Points[0].StationOffsetElevationToBaseline.X);
                            }
                            catch { /* an assembly Civil did not build has no station */ }
                        }
                    }
                    catch { /* a region Civil has not built has no assemblies */ }
                }
            }
            stations.Sort();
            return stations;
        }

        /// <summary>
        /// The firm's code opens the profile of the corridor's FIRST baseline before it draws anything (MahodCivilNet
        /// <c>getSpecialCodes</c>); with none it stops with eNullObjectId and draws nothing. Live 2026-10-28, TR_GISHA:
        /// Road34's design profile had been deleted while the corridor still listed its stations.
        /// </summary>
        private static bool FirstBaselineHasProfile(Transaction tr, ObjectId corridorId) =>
            tr.GetObject(corridorId, OpenMode.ForRead) is CivilDb.Corridor corridor &&
            corridor.Baselines.Count > 0 &&
            !corridor.Baselines[0].ProfileId.IsNull;

        /// <summary>The alignment's station equations, in its order, and its ending station: what the firm's code lists stations by.</summary>
        private static (List<(double, double)> Equations, double EndingStation) Stationing(
            Transaction tr, CivilDocument civilDoc, string alignmentName)
        {
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Alignment alignment ||
                    !string.Equals(alignment.Name, alignmentName, StringComparison.OrdinalIgnoreCase)) continue;
                var equations = new List<(double, double)>();
                try
                {
                    foreach (CivilDb.StationEquation equation in alignment.StationEquations)
                        equations.Add((equation.RawStationBack, equation.StationAhead));
                }
                catch { /* no equations to follow */ }
                return (equations, alignment.EndingStation);
            }
            return (new List<(double, double)>(), double.MaxValue);
        }

        /// <summary>Erases section blocks the firm's code drew beyond the one asked for, with their definitions when unused.</summary>
        private static void EraseSections(Transaction tr, Database db, IEnumerable<Handle> handles)
        {
            foreach (var handle in handles)
            {
                if (!db.TryGetObjectId(handle, out var id) || id.IsErased) continue;
                if (tr.GetObject(id, OpenMode.ForWrite) is not BlockReference reference) continue;
                var definitionId = reference.BlockTableRecord;
                reference.Erase();
                try
                {
                    var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
                    if (definition.GetBlockReferenceIds(true, false).Count == 0)
                    {
                        definition.UpgradeOpen();
                        definition.Erase();
                    }
                }
                catch { /* an unused definition left behind is harmless; PURGE removes it */ }
            }
        }

        /// <summary>
        /// Every corridor with a baseline on the alignment, as evidence for the choice: its regions on that baseline,
        /// whether it is out of date, and the TIN names of its own corridor surfaces.
        /// </summary>
        private static List<CorridorOnAlignment> CorridorsOn(Transaction tr, CivilDocument civilDoc, string alignmentName)
        {
            var found = new List<CorridorOnAlignment>();
            foreach (ObjectId corridorId in civilDoc.CorridorCollection)
            {
                if (tr.GetObject(corridorId, OpenMode.ForRead) is not CivilDb.Corridor corridor) continue;
                for (var i = 0; i < corridor.Baselines.Count; i++)
                {
                    var baseline = corridor.Baselines[i];
                    if (tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) is not CivilDb.Alignment alignment) continue;
                    if (!string.Equals(alignment.Name, alignmentName, StringComparison.OrdinalIgnoreCase)) continue;
                    var regions = new List<(double Start, double End)>();
                    foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                        regions.Add((region.StartStation, region.EndStation));
                    var surfaces = new List<string>();
                    foreach (CivilDb.CorridorSurface corridorSurface in corridor.CorridorSurfaces)
                    {
                        try
                        {
                            if (!corridorSurface.SurfaceId.IsNull &&
                                tr.GetObject(corridorSurface.SurfaceId, OpenMode.ForRead) is CivilDb.Surface surface)
                                surfaces.Add(surface.Name);
                            else
                                surfaces.Add(corridorSurface.Name);
                        }
                        catch { surfaces.Add(corridorSurface.Name); }
                    }
                    bool outOfDate;
                    try { outOfDate = corridor.IsOutOfDate; } catch { outOfDate = false; }
                    found.Add(new CorridorOnAlignment(corridor.Name, corridorId,
                        new TypicalCorridorChoiceLogic.Candidate(corridor.Name, regions, outOfDate, surfaces)));
                    break;
                }
            }
            return found;
        }

        private static string ChoiceReasonHe(string reason) => reason switch
        {
            TypicalCorridorChoiceLogic.Reason.SessionChoice => "לפי הבחירה שלך",
            TypicalCorridorChoiceLogic.Reason.ProfileChoice => "לפי פרופיל הפרויקט",
            TypicalCorridorChoiceLogic.Reason.CoversStations => "רק הוא מכסה את התחנות שסומנו",
            TypicalCorridorChoiceLogic.Reason.UpToDate => "רק הוא מעודכן מבין המכסים",
            _ => reason,
        };

        /// <summary>
        /// Marks a block drawn and committed in an earlier transaction for redraw (2026-11-02, 6403): a typical section
        /// placed above a firm block stayed invisible, through a regen too, until its reference was marked again once its
        /// own transaction had committed. Only that block is redrawn - no regen of a large drawing.
        /// </summary>
        private static void RedrawCommitted(Database db, ObjectId id)
        {
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                ((Entity)tr.GetObject(id, OpenMode.ForWrite)).RecordGraphicsModified(true);
                tr.Commit();
            }
            catch { /* display only */ }
        }

        /// <summary>True when the corridor's assemblies carry at least one link code; a corridor Civil cannot read is given the benefit.</summary>
        /// <summary>
        /// Whether the corridor's baseline along the alignment, or a region at one of the stations, is switched off ("build"
        /// unticked in the corridor's Parameters): Civil 3D computes nothing there however often it is rebuilt.
        /// </summary>
        private static bool IsSwitchedOff(Transaction tr, ObjectId corridorId, string alignmentName, IReadOnlyList<double> stations)
        {
            try
            {
                if (tr.GetObject(corridorId, OpenMode.ForRead) is not CivilDb.Corridor corridor) return false;
                foreach (var baseline in corridor.Baselines)
                {
                    if (tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) is not CivilDb.Alignment alignment ||
                        !string.Equals(alignment.Name, alignmentName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!baseline.NeedsProcessing) return true;
                    foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                        if (!region.NeedsProcessing && stations.Any(station => Within(region, station))) return true;
                }
            }
            catch { /* an older host without the switch: judged by its computed sections alone */ }
            return false;
        }

        /// <summary>Switches on the baseline of the request and its regions at the request's stations; true when it changed anything.</summary>
        private static bool SwitchOn(Transaction tr, CivilDb.Corridor corridor, SwitchOnRequest request)
        {
            var changed = false;
            foreach (var baseline in corridor.Baselines)
            {
                if (tr.GetObject(baseline.AlignmentId, OpenMode.ForRead) is not CivilDb.Alignment alignment ||
                    !string.Equals(alignment.Name, request.Alignment, StringComparison.OrdinalIgnoreCase)) continue;
                if (!baseline.NeedsProcessing) { baseline.NeedsProcessing = true; changed = true; }
                foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                    if (!region.NeedsProcessing && request.Stations.Any(station => Within(region, station)))
                    {
                        region.NeedsProcessing = true;
                        changed = true;
                    }
            }
            return changed;
        }

        private static bool Within(CivilDb.BaselineRegion region, double station) =>
            station >= region.StartStation - TypicalCorridorChoiceLogic.RegionToleranceM &&
            station <= region.EndStation + TypicalCorridorChoiceLogic.RegionToleranceM;

        /// <summary>The running Civil 3D's subassembly folder (<c>%ProgramData%\Autodesk\C3D &lt;year&gt;\enu</c>), or null.</summary>
        private static string? SubassemblyVersionFolder => CorridorRebuildLogic.VersionFolder(
            typeof(CivilApplication).Assembly.Location, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        /// <summary>The .NET subassembly DLLs the corridor's assemblies use that this computer cannot find; none when unsure.</summary>
        private static IReadOnlyList<string> MissingSubassemblyFiles(Transaction tr, ObjectId corridorId)
        {
            try
            {
                if (tr.GetObject(corridorId, OpenMode.ForRead) is not CivilDb.Corridor corridor) return Array.Empty<string>();
                var generators = new List<CorridorRebuildLogic.Generator>();
                var assemblies = new HashSet<ObjectId>();
                foreach (var baseline in corridor.Baselines)
                foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                {
                    try
                    {
                        if (region.AssemblyId.IsNull || !assemblies.Add(region.AssemblyId) ||
                            tr.GetObject(region.AssemblyId, OpenMode.ForRead) is not CivilDb.Assembly assembly) continue;
                        foreach (CivilDb.AssemblyGroup group in assembly.Groups)
                        foreach (ObjectId id in group.GetSubassemblyIds())
                        {
                            try
                            {
                                if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Subassembly subassembly)
                                    generators.Add(new CorridorRebuildLogic.Generator(
                                        subassembly.GeometryGenerator.GeometryGenerateMode.ToString(),
                                        subassembly.GeometryGenerator.ProjectOrAssemblyName));
                            }
                            catch { /* a subassembly Civil cannot open is not judged */ }
                        }
                    }
                    catch { /* a region without an assembly is not judged */ }
                }
                return CorridorRebuildLogic.MissingFiles(generators, SubassemblyVersionFolder, System.IO.File.Exists);
            }
            catch { return Array.Empty<string>(); }
        }

        private static bool CorridorHasLinkCodes(Transaction tr, ObjectId corridorId)
        {
            try
            {
                return tr.GetObject(corridorId, OpenMode.ForRead) is not CivilDb.Corridor corridor ||
                       corridor.GetLinkCodes().Any(code => !string.IsNullOrWhiteSpace(code));
            }
            catch { return true; }
        }

        /// <summary>The firm dialog's saved settings (named objects dictionary, <see cref="FirmSectionSettings.StoredSettingsKey"/>), or null.</summary>
        private static string? ReadStoredFirmSettings(Transaction tr, Database db)
        {
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(FirmSectionSettings.StoredSettingsKey)) return null;
                if (tr.GetObject(nod.GetAt(FirmSectionSettings.StoredSettingsKey), OpenMode.ForRead) is not Xrecord record || record.Data == null)
                    return null;
                var text = new System.Text.StringBuilder();
                foreach (TypedValue value in record.Data) text.Append(value.Value?.ToString());
                return text.ToString();
            }
            catch { return null; }
        }

        /// <summary>The ground the office's last run used, when it is still a TIN in the drawing.</summary>
        private static string? StoredGround(string? settings, IReadOnlyList<string> tins)
        {
            var ground = FirmSectionSettings.Field(settings, "cmbSurfBase");
            return !string.IsNullOrWhiteSpace(ground) && tins.Contains(ground, StringComparer.OrdinalIgnoreCase) ? ground : null;
        }

        private static List<string> TinNames(Transaction tr, CivilDocument civilDoc)
        {
            var names = new List<string>();
            foreach (ObjectId id in civilDoc.GetSurfaceIds())
                try { if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.TinSurface surface) names.Add(surface.Name); }
                catch { }
            return names;
        }

        private static Result Finish(Result result, Request request, DateTime started)
        {
            try
            {
                var payload = new
                {
                    run_id = result.RunId,
                    profile_id = request.Profile.ProfileId,
                    profile_hash = request.ProfileHash,
                    picks = request.Picks.Select(pick => new
                    {
                        pick.LineHandle, pick.TextHandle, pick.AlignmentName, pick.Station, pick.HalfWidthM, from_plan = pick.FromPlan,
                        axis = new[] { pick.AxisX, pick.AxisY }, tangent_deg = pick.TangentDeg,
                    }),
                    prerequisites = result.ByAlignment.Values,
                    corridor_choice = result.CorridorChoiceReasons,
                    corridor_choices_needed = result.CorridorChoicesNeeded,
                    existing_surface_choice = request.ExistingSurfaceChoice,
                    existing_surface_choices_needed = result.ExistingSurfaceChoicesNeeded,
                    blockers = result.Blockers,
                    warnings = result.Warnings,
                    sections = result.Sections.Select(section => new
                    {
                        section.LineHandle, section.AlignmentName, section.Station, section.PickedStation, section.HalfWidthM,
                        section.BlockHandle, section.OverlayHandle, section.BlockName, section.Title, section.ChainSegments,
                        section.NamedStrips, section.UnmatchedStrips, section.Notes, section.FrameFindings, section.Error,
                        section.FirmMessages,
                    }),
                    created_extents = result.CreatedExtents,
                };
                result.ArtifactPath = SectionsWorkflowService.WriteArtifact(result.RunId, "typical_sections.json", payload);
                var manifest = new RunManifest
                {
                    RunId = result.RunId,
                    Feature = "typical-sections",
                    Operation = "create",
                    StartedAtUtc = started,
                    CompletedAtUtc = DateTime.UtcNow,
                    ProjectProfileId = request.Profile.ProfileId,
                    ProjectProfileHash = request.ProfileHash,
                    ResultStatus = result.Blockers.Count > 0 ? DeliveryStatus.Blocked
                        : result.Sections.Any(section => section.Error != null) ? DeliveryStatus.Failed
                        : DeliveryStatus.Applied,
                };
                manifest.RecordCounts["picks"] = request.Picks.Count;
                manifest.RecordCounts["created"] = result.CreatedCount;
                manifest.RecordCounts["named_strips"] = result.NamedCount;
                manifest.RecordCounts["unmatched_strips"] = result.UnmatchedCount;
                manifest.Artifacts.Add("typical_sections.json");
                manifest.ArtifactHashes["typical_sections.json"] = ArtifactHash.Sha256OfFile(result.ArtifactPath);
                RunManifestWriter.Write(manifest);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("תיקיית הריצה לא נכתבה: " + ex.Message);
            }
            return result;
        }
    }
}
