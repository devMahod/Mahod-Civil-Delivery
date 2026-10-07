using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Reads the quantities the Civil model ALREADY KNOWS and turns them into
    /// neutral quantity records that flow through the same mapping/pricing pipeline
    /// as drawn geometry (engineer, live call 31/08: "לכל חתך יש את העובי שלו —
    /// אפשר להשתמש בזה"; "אפשר להוציא עוד דברים מהסיביל כמו החפירה מהחתכים"):
    ///
    ///   * corridor material volumes — CalculatedShape.Area per code (Pave1, Base,
    ///     SubBase…) at every corridor station, integrated by average end area;
    ///   * earthworks — cut/fill areas between the existing-ground section and the
    ///     design section at every sample line, integrated the same way.
    ///
    /// Layers are synthetic ("corridor:Base", "earthworks:cut") so profile rules
    /// can map them to catalog items exactly like drawn layers.
    /// </summary>
    internal static class CorridorQuantityService
    {
        internal sealed record Outcome(
            List<NeutralQuantityRecord> Records, List<DeliveryFinding> Findings,
            int Corridors, int StationsSampled, int SectionsCompared)
        {
            internal List<MaterialSectionAreaObservation> MaterialAreas { get; init; } = new();
        }

        internal static Outcome Collect(
            Transaction tr, Database db, CivilDocument civilDoc, ProjectProfile profile,
            string runId, string drawingPath, string drawingHash, StageLog? log)
        {
            var records = new List<NeutralQuantityRecord>();
            var findings = new List<DeliveryFinding>();
            var materialAreas = new List<MaterialSectionAreaObservation>();
            var drawingName = string.IsNullOrEmpty(drawingPath) ? "(unsaved)" : Path.GetFileName(drawingPath);
            var physicalUnits = HostDrawingUnitService.Resolve(db, profile);
            var drawingUnits = HostDrawingUnitService.Scale(physicalUnits);
            var unitFinding = HostDrawingUnitService.Finding(physicalUnits, profile.ProfileId);
            if (unitFinding != null) findings.Add(unitFinding);
            int seq = 0, stationsSampled = 0, sectionsCompared = 0, corridors = 0;
            var drawingOrigin = CorridorFailureProvenance.Source(drawingPath, drawingHash,
                null, null, null, runId, Sections.Services.SectionPlanService.ToolVersion);
            var origin = drawingOrigin;
            var materialPassOrigin = drawingOrigin;
            var earthworksPassOrigin = drawingOrigin;
            var materialReadFailures = new CorridorFailureProvenance.ReadFailures(() => origin);
            var earthworksReadFailures = new CorridorFailureProvenance.ReadFailures(() => origin);

            // A source identity is diagnostic evidence, not a completeness boundary.
            // In particular, omitted stations/shapes have no emitted record to scope to.
            DeliveryFinding Finding(FindingSeverity severity, string title, string message,
                ProjectProfile project, string code = EstimateFindingCodes.Unmapped) =>
                CorridorFailureProvenance.Attach(CorridorQuantityService.Finding(
                    severity, title, message, project, code), origin);

            // ------------------------------------------------ corridor materials
            try
            {
                foreach (ObjectId corridorId in civilDoc.CorridorCollection)
                {
                    origin = NativeOrigin(drawingOrigin, corridorId, "CORRIDOR");
                    CivilDb.Corridor corridor;
                    try { corridor = (CivilDb.Corridor)tr.GetObject(corridorId, OpenMode.ForRead); }
                    catch (Exception ex)
                    {
                        materialReadFailures.Add(new("corridor-open", corridorId.ToString(), ex.Message));
                        continue;
                    }
                    corridors++;
                    origin = NativeOrigin(drawingOrigin, corridorId, "CORRIDOR", corridor);
                    materialPassOrigin = origin;

                    if (corridor.IsOutOfDate)
                    {
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            $"הקורידור '{Bidi.Ltr(corridor.Name)}' אינו מעודכן (Out of date) — כמויותיו דולגו ולא יתומחרו",
                            "Rebuild the corridor in Civil, then rescan.", profile,
                            EstimatePreflightPolicy.CorridorOutOfDateCode));
                        // Never turn a stale cached corridor build into a Ready/priced
                        // quantity. The global preflight finding also blocks export.
                        materialPassOrigin = drawingOrigin;
                        continue;
                    }

                    var samples = new List<CorridorQuantityLogic.ShapeSample>();
                    var readFailuresBeforeCorridor = materialReadFailures.Count;
                    var expectedStationSeries = new List<CorridorQuantityLogic.StationSeries>();
                    var regionCoverageIssues = new List<string>();
                    var baselineOrdinal = 0;
                    foreach (CivilDb.Baseline baseline in corridor.Baselines)
                    {
                        baselineOrdinal++;
                        var baselineName = $"baseline-{baselineOrdinal:D3}";
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(baseline.Name))
                                baselineName += $":{baseline.Name}";
                        }
                        catch (Exception ex)
                        {
                            materialReadFailures.Add(new("baseline-name", baselineName, ex.Message));
                        }

                        double[] stations;
                        try { stations = baseline.SortedStations(); }
                        catch (Exception ex)
                        {
                            materialReadFailures.Add(new("baseline-stations", baselineName, ex.Message));
                            continue;
                        }
                        // Baseline.SortedStations merges disconnected regions. A short
                        // region gap is NOT a quantity interval, even below maxGapM.
                        // Read each region's own assembly collection so shared endpoints
                        // cannot pick the neighbouring region's (different) cross section.
                        var regions = new List<(CivilDb.BaselineRegion Native,
                            CorridorRegionQuantityPolicy.Region Contract)>();
                        try
                        {
                            var regionOrdinal = 0;
                            foreach (CivilDb.BaselineRegion region in baseline.BaselineRegions)
                            {
                                var seriesId = CorridorRegionQuantityPolicy.SeriesId(baselineName, ++regionOrdinal);
                                regions.Add((region, new CorridorRegionQuantityPolicy.Region(
                                    seriesId, region.StartStation, region.EndStation)));
                            }
                            CorridorRegionQuantityPolicy.RequireNonOverlappingRegions(
                                regions.Select(r => r.Contract).ToList());
                        }
                        catch (Exception ex)
                        {
                            materialReadFailures.Add(new("baseline-regions", baselineName, ex.Message));
                            continue;
                        }

                        foreach (var region in regions)
                        {
                            var seriesId = region.Contract.SeriesId;
                            var regionSamples = new List<CorridorQuantityLogic.ShapeSample>();
                            var observedStations = new List<double>();
                            var readFailuresBeforeRegion = materialReadFailures.Count;
                            try
                            {
                                foreach (CivilDb.AppliedAssembly assembly in region.Native.AppliedAssemblies)
                                {
                                    double st;
                                    try
                                    {
                                        if (assembly == null || assembly.Points.Count == 0)
                                            throw new InvalidOperationException("Civil returned an assembly without station points.");
                                        st = assembly.Points[0].StationOffsetElevationToBaseline.X;
                                        if (!double.IsFinite(st))
                                            throw new InvalidOperationException("Applied-assembly station is not finite.");
                                        observedStations.Add(st);
                                    }
                                    catch (Exception ex)
                                    {
                                        materialReadFailures.Add(new("region-assembly-station", seriesId, ex.Message));
                                        continue;
                                    }
                                    stationsSampled++;
                                    try
                                    {
                                        foreach (CivilDb.CalculatedShape shape in assembly.Shapes)
                                        {
                                            string code = "(shape)";
                                            try
                                            {
                                                var codes = shape.CorridorCodes.Cast<string>()
                                                    .Where(c => !string.IsNullOrWhiteSpace(c))
                                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                                if (codes.Count != 1)
                                                {
                                                    materialReadFailures.Add(new("shape-code-ambiguity", $"{seriesId}@{st:F4}",
                                                        codes.Count == 0 ? "Calculated shape has no corridor code."
                                                        : "Calculated shape has multiple codes: " + string.Join(", ", codes)));
                                                    continue;
                                                }
                                                code = codes[0];
                                            }
                                            catch (Exception ex)
                                            {
                                                materialReadFailures.Add(new("shape-codes", $"{seriesId}@{st:F4}", ex.Message));
                                                continue;
                                            }
                                            double area;
                                            try { area = shape.Area; }
                                            catch (Exception ex)
                                            {
                                                materialReadFailures.Add(new("shape-area", $"{seriesId}@{st:F4}:{code}", ex.Message));
                                                continue;
                                            }
                                            if (!double.IsFinite(area) || area < 0)
                                            {
                                                materialReadFailures.Add(new("shape-area-invalid", $"{seriesId}@{st:F4}:{code}",
                                                    $"Calculated shape area is invalid: {area}."));
                                                continue;
                                            }
                                            // Keep explicit zeros; never invent a taper or
                                            // bridge a region gap with a synthetic sample.
                                            regionSamples.Add(new CorridorQuantityLogic.ShapeSample(st, code, area, seriesId));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        materialReadFailures.Add(new("assembly-shapes", $"{seriesId}@{st:F4}", ex.Message));
                                    }
                                }
                                // A missing station after an unreadable getter/shape
                                // is still a QTO read failure, not proved missing coverage.
                                // Do not publish any samples from that invalid region.
                                if (materialReadFailures.Count != readFailuresBeforeRegion) continue;
                                var schedule = CorridorRegionQuantityPolicy.RequireSchedule(
                                    region.Contract, stations, observedStations);
                                expectedStationSeries.Add(schedule);
                                samples.AddRange(regionSamples);
                            }
                            catch (CorridorRegionQuantityPolicy.CoverageIncompleteException ex)
                            {
                                // Fully read and valid, but incomplete, collections do
                                // not justify a quantity. Retain the corridor-scoped
                                // completeness blocker; independent measured geometry
                                // may still appear in an explicitly partial draft.
                                regionCoverageIssues.Add(ex.Message);
                            }
                            catch (Exception ex)
                            {
                                // Invalid/duplicate/incomplete region assemblies are not
                                // published, including in a partial priced draft.
                                materialReadFailures.Add(new("region-assemblies", seriesId, ex.Message));
                            }
                        }
                    }

                    var materialMaxGap = 50.0 / drawingUnits.LinearToMetres;
                    var materialCoverageIssues = CorridorQuantityLogic.MaterialCoverageIssues(
                        samples, expectedStationSeries, materialMaxGap);
                    materialCoverageIssues.AddRange(regionCoverageIssues);
                    if (materialCoverageIssues.Count > 0)
                    {
                        findings.Add(Finding(
                            FindingSeverity.ReviewRequired,
                            $"כמויות חומרי הקורידור '{Bidi.Ltr(corridor.Name)}' אינן מכסות סדרות תחנות מוכחות",
                            string.Join(" | ", materialCoverageIssues), profile,
                            EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode));
                    }

                    foreach (var mv in CorridorQuantityLogic.AverageEndArea(
                                 samples, maxGapM: materialMaxGap))
                    {
                        records.Add(VolumeRecord(profile, runId, drawingName, drawingPath, drawingHash,
                            corridor.Handle.ToString(), "CORRIDOR",
                            layer: $"corridor:{mv.Code}",
                            identity: $"{corridor.Name}/{mv.SeriesId}",
                            method: "corridor-qto-avg-end-area",
                            mv, drawingUnits, ref seq, findings));
                    }
                    // Station areas are preserved separately, never added to BOQ
                    // records or summed along the road as a supposed plan area.
                    materialAreas.AddRange(MaterialSectionAreaEvidence.Capture(
                        samples, runId, drawingPath, drawingHash,
                        corridor.Handle.ToString(), corridor.Name,
                        drawingUnits.LinearToMetres, drawingUnits.IsSupported,
                        drawingUnits.SourceName,
                        materialCoverageIssues.Count == 0 &&
                        materialReadFailures.Count == readFailuresBeforeCorridor));
                    materialPassOrigin = drawingOrigin;
                }
            }
            catch (Exception ex)
            {
                materialReadFailures.Add(new("corridor-material-pass", drawingName, ex.Message), materialPassOrigin);
            }
            var materialBlocker = CorridorQuantityLogic.BlockingReadFinding(
                materialReadFailures,
                EstimatePreflightPolicy.CorridorMaterialReadFailedCode,
                profile.ProfileId,
                "קריאת חלק מכמויות חומרי הקורידור נכשלה — האומדן חלקי וחסום לייצוא");
            if (materialBlocker != null)
                findings.Add(CorridorFailureProvenance.Attach(materialBlocker, materialReadFailures.Sources));
            var materialAvailability = CorridorMaterialAvailabilityPolicy.Evaluate(
                corridors, materialReadFailures.Count > 0, profile.ProfileId, drawingPath, drawingHash);
            if (materialAvailability != null) findings.Add(materialAvailability);

            // ------------------------------------------------------- earthworks
            origin = drawingOrigin;
            var earthworksDecision = EstimateWorkflowService.GetEarthworksDecision(profile);
            if (earthworksDecision.Status == EstimateWorkflowService.EarthworksDecisionStatus.Excluded)
            {
                findings.Add(Finding(
                    FindingSeverity.Info,
                    "חפירה/מילוי הוחרגו במפורש מהאומדן",
                    $"החלטה: {profile.Estimate.Earthworks.Reason}; " +
                    $"מאשר: {profile.Estimate.Earthworks.DecidedBy}; " +
                    $"זמן: {profile.Estimate.Earthworks.DecidedAtUtc:O}",
                    profile, EstimateFindingCodes.NotAQuantity));
            }
            else if (earthworksDecision.Status != EstimateWorkflowService.EarthworksDecisionStatus.Included)
            {
                // This is an undecided project scope, not a failed Civil object.
                // Do not attach the handle-less pass origin as object evidence.
                findings.Add(CorridorQuantityService.Finding(
                    FindingSeverity.ReviewRequired,
                    "עבודות עפר טרם נבדקו — לא אפס ולא הוחרגו",
                    EstimateGuidedActionPolicy.EarthworksNotAssessed + " " + earthworksDecision.GateText,
                    profile, EstimatePreflightPolicy.EarthworksNotAssessedCode, runId));
            }
            else try
            {
                var patterns = profile.Sections.Projection.ExistingSurfacePatterns is { Count: > 0 } pp
                    ? pp.ToArray()
                    : new[] { "MK", "MK*", "*EG*", "*EXIST*", "*KAYAM*", "*קיים*" };
                var allowedAlignments = new HashSet<string>(
                    profile.Sections.Alignments.AllowedNames,
                    StringComparer.OrdinalIgnoreCase);

                var alignments = civilDoc.GetAlignmentIds();
                var targetAlignmentCount = 0;
                foreach (ObjectId alignId in alignments)
                {
                    origin = NativeOrigin(drawingOrigin, alignId, "ALIGNMENT");
                    CivilDb.Alignment alignment;
                    try { alignment = (CivilDb.Alignment)tr.GetObject(alignId, OpenMode.ForRead); }
                    catch (Exception ex)
                    {
                        earthworksReadFailures.Add(new("alignment-open", alignId.ToString(), ex.Message));
                        continue;
                    }
                    if (allowedAlignments.Count > 0 && !allowedAlignments.Contains(alignment.Name))
                        continue;
                    targetAlignmentCount++;
                    var alignmentOrigin = NativeOrigin(drawingOrigin, alignId, "ALIGNMENT", alignment);
                    origin = alignmentOrigin;
                    earthworksPassOrigin = alignmentOrigin;

                    var cutSamples = new List<CorridorQuantityLogic.ShapeSample>();
                    var rejected = 0;
                    var incompleteSurfaceChains = 0;
                    var untrustedGroups = new List<string>();
                    var mcdGroupCount = 0;
                    var trustedGroupCount = 0;
                    var validStations = 0;
                    SectionSourceSelectionLogic.Selection? rejectedSourceSelection = null;
                    foreach (ObjectId slgId in alignment.GetSampleLineGroupIds())
                    {
                        origin = NativeOrigin(drawingOrigin, slgId, "SAMPLELINEGROUP");
                        CivilDb.SampleLineGroup slg;
                        try { slg = (CivilDb.SampleLineGroup)tr.GetObject(slgId, OpenMode.ForRead); }
                        catch (Exception ex)
                        {
                            earthworksReadFailures.Add(new("sample-line-group-open", $"{alignment.Name}/{slgId}", ex.Message));
                            continue;
                        }
                        origin = NativeOrigin(drawingOrigin, slgId, "SAMPLELINEGROUP", slg);

                        // Only the tool's own sample line groups (MCD-*): their swath is
                        // capped at 31 m and their sampled surfaces are known. Legacy
                        // project groups carry hundreds of metres of swath and surface
                        // sets that made "cut" total 10 million m3 (live, 31/08).
                        if (!(slg.Name ?? "").StartsWith("MCD-", StringComparison.OrdinalIgnoreCase))
                            continue;
                        mcdGroupCount++;

                        var ownership = SectionOwnershipService.Read(tr, slg);
                        if (!CorridorQuantityLogic.IsTrustedSectionGroup(ownership, profile.ProfileId))
                        {
                            untrustedGroups.Add(slg.Name ?? slgId.ToString());
                            continue;
                        }
                        trustedGroupCount++;
                        var seriesId = slg.Name ?? slgId.ToString();

                        foreach (ObjectId slId in slg.GetSampleLineIds())
                        {
                            origin = NativeOrigin(drawingOrigin, slId, "SAMPLELINE");
                            CivilDb.SampleLine sl;
                            try { sl = (CivilDb.SampleLine)tr.GetObject(slId, OpenMode.ForRead); }
                            catch (Exception ex)
                            {
                                earthworksReadFailures.Add(new("sample-line-open", $"{alignment.Name}/{slg.Name}/{slId}", ex.Message));
                                continue;
                            }
                            origin = NativeOrigin(drawingOrigin, slId, "SAMPLELINE", sl);

                            var (eg, ds, sourceSelection) = SurfaceChains(
                                tr, sl, patterns, alignment.Name,
                                drawingUnits.IsSupported &&
                                profile.Sections.Projection.MaxHalfWidthM is { } maxHalfWidthM &&
                                double.IsFinite(maxHalfWidthM) && maxHalfWidthM > 0
                                    ? maxHalfWidthM / drawingUnits.LinearToMetres
                                    : double.NaN,
                                drawingUnits.IsSupported
                                    ? 0.01 / drawingUnits.LinearToMetres
                                    : double.NaN,
                                earthworksReadFailures, $"{alignment.Name}/{slg.Name}/{slId}", origin);
                            if (!sourceSelection.IsReady)
                            {
                                rejectedSourceSelection ??= sourceSelection;
                                continue;
                            }
                            if (eg.Count < 2 || ds.Count < 2)
                            {
                                incompleteSurfaceChains++;
                                continue;
                            }
                            var (cut, fill) = CorridorQuantityLogic.CutFill(eg, ds);
                            if (drawingUnits.Area(cut) > SectionFurnitureLogic.MaxPlausibleCutFillAreaM2 ||
                                drawingUnits.Area(fill) > SectionFurnitureLogic.MaxPlausibleCutFillAreaM2)
                            {
                                rejected++;                    // sampling artefact, never priced
                                continue;
                            }
                            double station;
                            try { station = sl.Station; }
                            catch (Exception ex)
                            {
                                earthworksReadFailures.Add(new("sample-line-station", $"{alignment.Name}/{slg.Name}/{slId}", ex.Message));
                                continue;
                            }
                            sectionsCompared++;
                            validStations++;
                            // Keep explicit zero endpoints. Omitting them silently
                            // drops the taper from positive cut/fill back to zero.
                            cutSamples.Add(new CorridorQuantityLogic.ShapeSample(station, "cut", cut, seriesId));
                            cutSamples.Add(new CorridorQuantityLogic.ShapeSample(station, "fill", fill, seriesId));
                        }
                    }

                    origin = alignmentOrigin;
                    if (rejected > 0)
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            "חפירה/מילוי: " + rejected + " חתכים בציר '" + Bidi.Ltr(alignment.Name) +
                            "' דולגו - שטח חתך לא סביר; הנפח המדווח אינו כולל אותם",
                            "Check the sampled design surface for those sample lines.", profile,
                            EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));

                    if (mcdGroupCount == 0)
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            $"חפירה/מילוי בציר '{Bidi.Ltr(alignment.Name)}' חסומים — לא נמצאה קבוצת חתכים MCD בבעלות הכלי",
                            "Missing tool-owned MCD sample-line coverage is unknown quantity, not zero.",
                            profile, EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));

                    if (trustedGroupCount > 1)
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            $"חפירה/מילוי בציר '{Bidi.Ltr(alignment.Name)}' חסומים — נמצאו {trustedGroupCount} מקורות MCD בבעלות הכלי",
                            "Multiple owned sample-line groups require an explicit range/deduplication decision.",
                            profile, EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));

                    if (untrustedGroups.Count > 0)
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            $"חפירה/מילוי בציר '{Bidi.Ltr(alignment.Name)}' דולגו — קבוצת חתכים בשם MCD אינה בבעלות הכלי/הפרופיל",
                            $"Rejected groups: {string.Join(", ", untrustedGroups.Select(Bidi.Ltr))}",
                            profile, EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));

                    if (rejectedSourceSelection != null || incompleteSurfaceChains > 0)
                    {
                        var selectionEvidence = rejectedSourceSelection is not { } rejectedChoice
                            ? ""
                            : $"EG={rejectedChoice.ExistingGround.State} [{string.Join(", ", rejectedChoice.ExistingGround.Candidates)}]; " +
                              $"Design={rejectedChoice.Design.State} [{string.Join(", ", rejectedChoice.Design.Candidates)}]. ";
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            $"חפירה/מילוי בציר '{Bidi.Ltr(alignment.Name)}' דולגו — מקור קרקע קיימת או תכנון חסר/עמום",
                            selectionEvidence +
                            $"חתכים עם פחות משתי נקודות תקינות באחד המשטחים: {incompleteSurfaceChains}.",
                            profile, EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));
                    }

                    var coverageIssues = EarthworksCoverageIssues(
                        cutSamples, validStations, 200.0 / drawingUnits.LinearToMetres);
                    if (coverageIssues.Count > 0)
                        findings.Add(Finding(FindingSeverity.ReviewRequired,
                            $"חפירה/מילוי בציר '{Bidi.Ltr(alignment.Name)}' אינם מכסים רצף חתכים מוכח",
                            string.Join(" | ", coverageIssues), profile,
                            EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));

                    foreach (var mv in CorridorQuantityLogic.AverageEndArea(
                                 cutSamples, maxGapM: 200.0 / drawingUnits.LinearToMetres))
                    {
                        records.Add(VolumeRecord(profile, runId, drawingName, drawingPath, drawingHash,
                            alignment.Handle.ToString(), "ALIGNMENT",
                            layer: $"earthworks:{mv.Code}",
                            identity: alignment.Name,
                            method: "sections-cutfill-avg-end-area",
                            mv, drawingUnits, ref seq, findings));
                    }
                    earthworksPassOrigin = drawingOrigin;
                }
                origin = drawingOrigin;
                if (targetAlignmentCount == 0)
                    findings.Add(Finding(FindingSeverity.ReviewRequired,
                        "חפירה/מילוי חסומים — אין צירים מאושרים שמהם ניתן להוכיח כיסוי חתכים",
                        "Earthworks were not explicitly marked as outside scope; missing approved alignment coverage is not zero.",
                        profile, EstimatePreflightPolicy.EarthworksSourceUnverifiedCode));
            }
            catch (Exception ex)
            {
                earthworksReadFailures.Add(new("earthworks-pass", drawingName, ex.Message), earthworksPassOrigin);
            }
            var earthworksBlocker = CorridorQuantityLogic.BlockingReadFinding(
                earthworksReadFailures,
                EstimatePreflightPolicy.EarthworksQtoFailedCode,
                profile.ProfileId,
                "קריאת חלק מנתוני החפירה/מילוי נכשלה — האומדן חלקי וחסום לייצוא");
            if (earthworksBlocker != null)
                findings.Add(CorridorFailureProvenance.Attach(earthworksBlocker, earthworksReadFailures.Sources));

            log?.Info($"corridor_qto corridors={corridors} stations={stationsSampled} " +
                      $"sections={sectionsCompared} records={records.Count}");
            return new Outcome(records, findings, corridors, stationsSampled, sectionsCompared)
            {
                MaterialAreas = materialAreas,
            };
        }

        /// <summary>EG + design (offset, elevation) chains of one sample line's surface sections.</summary>
        private static (
            List<(double, double)> Eg,
            List<(double, double)> Design,
            SectionSourceSelectionLogic.Selection Selection) SurfaceChains(
            Transaction tr, CivilDb.SampleLine sl, string[] existingPatterns, string? alignmentName,
            double maxHalfWidthDrawingUnits, double boundaryToleranceDrawingUnits,
            CorridorFailureProvenance.ReadFailures readFailures, string context, ProvenanceRef sampleLineOrigin)
        {
            var byName = new Dictionary<string, List<(double X, double Y, double Z)>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ObjectId secId in sl.GetSectionIds())
                {
                    var sectionOrigin = NativeOrigin(sampleLineOrigin, secId, "SECTION");
                    try
                    {
                        if (tr.GetObject(secId, OpenMode.ForRead) is not CivilDb.Section s)
                        {
                            readFailures.Add(new("section-open", $"{context}/{secId}", "Civil returned a non-section object."), sectionOrigin);
                            continue;
                        }
                        sectionOrigin = NativeOrigin(sampleLineOrigin, secId, "SECTION", s);
                        if (s.SourceType != CivilDb.SectionSourceType.TinSurface &&
                            s.SourceType != CivilDb.SectionSourceType.GridSurface) continue;
                        var name = s.SourceName ?? "";
                        if (byName.ContainsKey(name)) continue;
                        var pts = new List<(double, double, double)>();
                        foreach (CivilDb.SectionPoint sp in s.SectionPoints)
                            pts.Add((sp.Location.X, sp.Location.Y, sp.Location.Z));
                        byName[name] = pts;
                    }
                    catch (Exception ex)
                    {
                        readFailures.Add(new("section-read", $"{context}/{secId}", ex.Message), sectionOrigin);
                    }
                }
            }
            catch (Exception ex)
            {
                readFailures.Add(new("section-enumeration", context, ex.Message));
            }

            // Use the same deterministic, fail-closed source contract as Sections:
            // literal MK outranks MK*, and design must belong to this alignment.
            var selection = SectionSourceSelectionLogic.Select(
                byName.Keys, alignmentName, existingPatterns);
            if (!selection.IsReady)
                return (new List<(double, double)>(), new List<(double, double)>(), selection);

            var egName = selection.ExistingGround.Name!;
            var dsName = selection.Design.Name!;
            var eg = egName != null ? byName[egName] : new List<(double, double, double)>();
            var ds = dsName != null ? byName[dsName] : new List<(double, double, double)>();
            if (!CorridorQuantityLogic.TryNormalizeOwnedSectionPoints(
                    eg, maxHalfWidthDrawingUnits, boundaryToleranceDrawingUnits,
                    out var egChain, out var egFailure))
                readFailures.Add(new("section-point-bounds", $"{context}/{egName}", egFailure));
            if (!CorridorQuantityLogic.TryNormalizeOwnedSectionPoints(
                    ds, maxHalfWidthDrawingUnits, boundaryToleranceDrawingUnits,
                    out var dsChain, out var dsFailure))
                readFailures.Add(new("section-point-bounds", $"{context}/{dsName}", dsFailure));

            return (egChain, dsChain, selection);
        }

        private static ProvenanceRef NativeOrigin(
            ProvenanceRef drawing, ObjectId id, string entityType, Entity? entity = null)
        {
            string? handle = null, layer = null;
            // Evidence getters must not replace the original read failure. Unknown
            // identity stays unknown; ObjectId.ToString is never passed off as a handle.
            try { if (!id.IsNull) handle = id.Handle.ToString(); } catch { }
            try { layer = entity?.Layer; } catch { }
            return CorridorFailureProvenance.Source(drawing.SourcePathOrUri, drawing.DrawingChecksum,
                handle, entityType, layer, drawing.RunId, drawing.ToolVersion);
        }

        private static NeutralQuantityRecord VolumeRecord(
            ProjectProfile profile, string runId, string drawingName, string drawingPath, string drawingHash,
            string handle, string entityType, string layer, string identity, string method,
            CorridorQuantityLogic.MaterialVolume mv, DrawingUnitPolicy.Scale drawingUnits,
            ref int seq, List<DeliveryFinding>? findings = null)
        {
            seq++;
            var ruleKey = $"layer:{layer}|volume";
            var resolution = CivilQuantityExtractionService.ResolveApprovedRule(profile, ruleKey, layer, "volume");
            var approved = resolution.Rule;
            DeliveryFinding? ambiguousMapping = null;
            if (resolution.IsAmbiguous && findings != null)
            {
                ambiguousMapping = Finding(FindingSeverity.Error,
                    $"יותר מחוק כמות מאושר אחד מתאים ל-{Bidi.Ltr(ruleKey)}",
                    "Matching rules: " + string.Join(", ", resolution.Matches.Select(r => r.RuleKey ?? "(missing)")) +
                    ". No mapping was selected.", profile, EstimateFindingCodes.ConfigurationAmbiguous);
            }
            var record = new NeutralQuantityRecord
            {
                RecordId = $"cq-{seq:0000}",
                ProjectProfileId = profile.ProfileId,
                RunId = runId,
                Source = new QuantitySource
                {
                    Drawing = drawingName,
                    DrawingPath = drawingPath,
                    DrawingHash = drawingHash,
                    Handle = handle,
                    EntityType = entityType,
                    Layer = layer,
                    CivilIdentity = identity,
                    StationFrom = drawingUnits.Length(mv.StationFrom),
                    StationTo = drawingUnits.Length(mv.StationTo),
                },
                Measurement = new QuantityMeasurement
                {
                    Kind = "volume",
                    Method = method,
                    // Preserve the measured volume; the BOQ builder owns the single
                    // canonical four-decimal rounding step.
                    RawValue = drawingUnits.Volume(mv.VolumeM3),
                    Unit = drawingUnits.VolumeUnit,
                    Parameters =
                    {
                        ["stations"] = mv.StationCount.ToString(),
                        ["max_gap_m"] = drawingUnits.Length(mv.MaxGapM).ToString("F1"),
                        ["source_insunits"] = drawingUnits.SourceName,
                    },
                },
                Classification = new QuantityClassification
                {
                    SourceClass = entityType,
                    RuleKey = approved?.RuleKey ?? ruleKey,
                    CandidateCatalogCode = approved?.CandidateCatalogCode,
                    ApprovedCatalogId = approved?.ApprovedCatalogId,
                    ApprovedCatalogHash = approved?.ApprovedCatalogHash,
                    ApprovedCatalogItemFingerprint = approved?.ApprovedCatalogItemFingerprint,
                    MappingApprovedBy = approved?.ApprovedBy,
                    MappingApprovedAtUtc = approved?.ApprovedAtUtc,
                    Tags = { approved != null ? "rule-classified" : "discovered", "civil-model" },
                },
                Provenance = new ProvenanceRef
                {
                    SourceKind = "civil-model",
                    SourcePathOrUri = drawingPath,
                    DrawingChecksum = drawingHash,
                    SourceHandle = handle,
                    EntityType = entityType,
                    Layer = layer,
                    MeasurementMethod = method,
                    ToolVersion = Sections.Services.SectionPlanService.ToolVersion,
                    RunId = runId,
                },
                Status = approved != null ? DeliveryStatus.Ready : DeliveryStatus.ReviewRequired,
            };
            if (!string.IsNullOrWhiteSpace(mv.SeriesId))
                record.Measurement.Parameters["baseline_series"] = mv.SeriesId;
            if (ambiguousMapping != null)
            {
                // This ambiguity concerns this actually emitted record, unlike
                // incomplete source coverage for which no record may exist at all.
                ambiguousMapping.AffectedRecordIds.Add(record.RecordId);
                findings!.Add(CorridorFailureProvenance.Attach(ambiguousMapping, record.Provenance));
            }
            if (approved == null)
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.Unmapped,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"נמדדו {record.Measurement.RawValue:F2} מ\"ק ({Bidi.Ltr(layer)}, {Bidi.Ltr(identity)}) ללא שיוך לסעיף מחירון",
                    RecommendedAction = "יש לאשר שיוך דרך 'אשר מיפוי' — כמות שחושבה מהמודל, לא משכבה משורטטת.",
                    AffectedRecordIds = { record.RecordId },
                    SourceRefs = { record.Provenance },
                    ProjectProfileId = profile.ProfileId,
                });
            }
            return record;
        }

        internal static bool EarthworksExplicitlyNotRequested(ProjectProfile profile)
        {
            return EstimateWorkflowService.GetEarthworksDecision(profile).Status ==
                   EstimateWorkflowService.EarthworksDecisionStatus.Excluded;
        }

        /// <summary>
        /// Pure coverage audit before average-end-area integration. Singletons,
        /// duplicate station/code observations and gaps are all partial evidence;
        /// AverageEndArea intentionally does not bridge them, but omission alone is
        /// not enough — the export must also remain blocked and say why.
        /// </summary>
        internal static List<string> EarthworksCoverageIssues(
            IReadOnlyList<CorridorQuantityLogic.ShapeSample> samples,
            int validStations, double maxGapDrawingUnits)
        {
            var issues = new List<string>();
            if (validStations == 0)
            {
                issues.Add("no valid stations with an unambiguous existing/design surface pair");
                return issues;
            }

            foreach (var series in samples
                         .GroupBy(s => s.SeriesId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                var stations = series.Select(s => Math.Round(s.Station, 4))
                    .Distinct().OrderBy(x => x).ToList();
                if (stations.Count < 2)
                    issues.Add($"series '{series.Key}' has a singleton station");
                for (var i = 1; i < stations.Count; i++)
                {
                    var gap = stations[i] - stations[i - 1];
                    if (gap > maxGapDrawingUnits)
                        issues.Add($"series '{series.Key}' has an unintegrated gap ({gap:F3} drawing units)");
                }

                var duplicateCodes = series
                    .GroupBy(s => (Station: Math.Round(s.Station, 4), Code: s.Code.ToUpperInvariant()))
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key.Code}@{g.Key.Station:F4}")
                    .ToList();
                if (duplicateCodes.Count > 0)
                    issues.Add($"series '{series.Key}' has duplicate/multi-source station codes: " +
                               string.Join(", ", duplicateCodes.Take(8)));
            }
            return issues;
        }

        private static DeliveryFinding Finding(
            FindingSeverity severity, string title, string message, ProjectProfile profile,
            string code = EstimateFindingCodes.Unmapped, string? runId = null) =>
            new()
            {
                Code = code,
                Domain = "estimate",
                Severity = severity,
                Title = title,
                Message = message,
                ProjectProfileId = profile.ProfileId,
                RunId = runId,
            };
    }

    /// <summary>Pure evidence adapter; it never infers completeness or record scope.</summary>
    internal static class CorridorFailureProvenance
    {
        internal static ProvenanceRef Source(string? drawingPath, string? drawingHash,
            string? handle, string? entityType, string? layer, string? runId, string? toolVersion) => new()
        {
            SourceKind = "civil-model",
            SourcePathOrUri = drawingPath,
            DrawingChecksum = drawingHash,
            SourceHandle = handle,
            EntityType = entityType,
            Layer = layer,
            MeasurementMethod = "civil-model-quantity",
            RunId = runId,
            ToolVersion = toolVersion,
        };

        internal static DeliveryFinding Attach(DeliveryFinding finding, params ProvenanceRef[] sources) =>
            Attach(finding, (IEnumerable<ProvenanceRef>)sources);

        internal static DeliveryFinding Attach(DeliveryFinding finding, IEnumerable<ProvenanceRef> sources)
        {
            var keys = new HashSet<string>(finding.SourceRefs.Select(
                source => System.Text.Json.JsonSerializer.Serialize(source)), StringComparer.Ordinal);
            foreach (var source in sources)
                if (keys.Add(System.Text.Json.JsonSerializer.Serialize(source))) finding.SourceRefs.Add(source);
            // Intentionally leave Code/Severity/Message/AffectedRecordIds unchanged.
            return finding;
        }

        internal sealed class ReadFailures : List<CorridorQuantityLogic.QuantityReadFailure>
        {
            private readonly Func<ProvenanceRef> _currentSource;
            internal List<ProvenanceRef> Sources { get; } = new();
            internal ReadFailures(Func<ProvenanceRef> currentSource) => _currentSource = currentSource;

            public new void Add(CorridorQuantityLogic.QuantityReadFailure failure) => Add(failure, _currentSource());

            internal void Add(CorridorQuantityLogic.QuantityReadFailure failure, ProvenanceRef source)
            {
                base.Add(failure);
                Sources.Add(new ProvenanceRef
                {
                    SourceKind = source.SourceKind,
                    SourcePathOrUri = source.SourcePathOrUri,
                    DrawingChecksum = source.DrawingChecksum,
                    SourceHandle = source.SourceHandle,
                    EntityType = source.EntityType,
                    Layer = source.Layer,
                    SourceSubentityPath = failure.Context,
                    MeasurementMethod = "civil-model-quantity:" + failure.Stage,
                    RunId = source.RunId,
                    ToolVersion = source.ToolVersion,
                });
            }
        }
    }
}
