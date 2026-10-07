using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using CivilStyles = Autodesk.Civil.DatabaseServices.Styles;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Turns a bare SectionView into a section an engineer can read (engineer
    /// feedback on 1.1.x, 2026-08-30: "two thin lines on an empty grid"):
    ///
    ///   * existing-ground vs design surfaces get visibly different styles;
    ///   * the axis (CL) is marked at offset 0;
    ///   * utilities that exist only as polylines — usually inside the UT XREF —
    ///     are PROJECTED to their true offset (and depth when they carry Z) with a
    ///     Hebrew label, which is what the old hand-drafted sections show;
    ///   * plan marks (curbs, lane lines, right-of-way) become ticks with widths
    ///     between them;
    ///   * a band set is attached when the drawing has one.
    ///
    /// Everything drawn is registered per section (NOD) so a re-apply replaces its
    /// own annotations instead of stacking copies, and the layer is the tool's own.
    /// </summary>
    internal static class SectionDecorationService
    {
        internal const string AnnoLayer =
            SectionAnnotationResourceContracts.AnnotationLayerName;
        internal const string ExistingStyleName =
            SectionSurfaceStyleContractLogic.ExistingName;
        internal const string DesignStyleName =
            SectionSurfaceStyleContractLogic.DesignName;
        internal const string EmptyBandSetStyleName = "MHD-NO-NATIVE-BANDS-V2";
        private const string EmptyBandSetStyleDescription =
            "Mahod-owned empty SectionView band set; required for the single-datum contract.";
        internal const string AnnoTextStyle =
            SectionAnnotationResourceContracts.AnnotationTextStyleName;
        internal const string AnnoTypeface = "Arial";

        /// <summary>Default patterns that mean "existing ground" when the profile is silent.</summary>
        private static readonly string[] DefaultExistingPatterns =
            { "MK", "MK*", "*EG*", "*EXIST*", "*KAYAM*", "*מצב קיים*", "*קיים*" };

        internal sealed record Outcome(int Decorated, int UtilitiesProjected, int MarksDrawn);

        internal static Outcome Decorate(
            Transaction tr,
            Database db,
            CivilDocument civilDoc,
            ProjectProfile profile,
            IReadOnlyList<SectionPlanRecord> targets,
            SectionApplyResult result,
            SectionGeometryCollector.CollectResult collected,
            StageLog? log)
        {
            if (!profile.Sections.Projection.Enabled) return new Outcome(0, 0, 0);

            // APPLY recollects the real source. The same spatial failure policy as
            // PLAN must run before erasure/resource changes: an unrelated invalid
            // hatch cannot block this cut, while a new affecting/unknown failure
            // must never be silently omitted by the decorator.
            foreach (var target in targets)
            {
                var affecting = collected.Findings.Where(f =>
                    f.Severity == FindingSeverity.Error &&
                    SectionProjectionFailureScope.AffectsCut(f, target.Cl.WcsEndpoints)).ToList();
                var unresolvedSpanCount = SectionPlanService.UnresolvedSpanCountForFailureScope(target);
                // b7: the same local cut rule as PLAN, re-proven on the re-read source.
                var locallyProven = SectionPlanService.LocallyProvenFailures(target, affecting, collected, profile);
                var failures = affecting.Where(f =>
                    SectionProjectionFailureScope.BlocksCreation(f, target.Cl.WcsEndpoints, unresolvedSpanCount) &&
                    !locallyProven.ContainsKey(f.FindingId)).ToList();
                // Same policy as PLAN: an unreadable HA area on a fully named cut is
                // recorded as a warning in the apply evidence, never silently dropped.
                result.Findings.AddRange(affecting.Where(f => !failures.Contains(f)).Select(f =>
                    locallyProven.TryGetValue(f.FindingId, out var proof) &&
                    !SectionProjectionFailureScope.IsNamedRegionOnly(f, unresolvedSpanCount)
                        ? SectionProjectionFailureScope.ForLocalCut(f, target.RecordId, proof)
                        : SectionProjectionFailureScope.ForNamedCut(f, target.RecordId)));
                if (failures.Count == 0) continue;
                result.Findings.AddRange(failures.Select(f =>
                    SectionProjectionFailureScope.ForCut(f, target.RecordId)));
                throw new InvalidOperationException(
                    "Source geometry remains unreadable for section " + target.RecordId + ": " +
                    string.Join(" | ", failures.Select(f => f.Code + ": " + f.Message)));
            }

            // Shared resources: a selected-record APPLY may create what is absent
            // (nothing references it yet) but never modify what other sections already
            // reference; a required change blocks (review, 02/09).
            var allowModify = !string.Equals(result.Scope, "selected-record", StringComparison.Ordinal);
            EnsureLayer(tr, db, AnnoLayer, allowModify, log);
            EnsureTextStyle(tr, db, allowModify);
            EnsureLinetypes(tr, db, allowModify);
            // Reused manual sections retain their native Civil styles/bands exactly.
            // Creating Mahod styles is unnecessary when the batch only enriches
            // manual views, and applying them would violate SEC-02 preservation.
            var hasManagedTargets = targets.Any(t => t.ManualSectionReuse == null);
            var existingStyleOk = !hasManagedTargets || EnsureSectionStyles(tr, civilDoc, result, allowModify);
            if (hasManagedTargets && !existingStyleOk)
                throw new InvalidOperationException(
                    "Managed existing/design Section styles could not be prepared; APPLY must roll back (SEC-STYLE-MISSING).");
            var vehicleBlocks = SectionVehicleBlockService.Load(tr, db, log, allowModify);
            foreach (var failure in vehicleBlocks.Failures)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.VehicleBlockUnavailable,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = $"בלוק הרכב המשרדי ({failure.View}) לא נטען — חתך שדורש אותו ייחסם",
                    Message = failure.Error,
                    RecommendedAction = "יש להתקין מחדש את חבילת Mahod Civil Delivery, להריץ תכנון מחדש ולאמת שהבלוק מופיע לפני מסירה.",
                });
            }
            var trafficArrowBlocks = SectionTrafficDirectionArrowService.Load(
                tr, db, log, allowModify);
            if (trafficArrowBlocks.Failure is { } arrowFailure)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.TrafficDirectionEvidenceMismatch,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "בלוק חץ התנועה המשרדי של נטלי לא נטען — החתכים חסומים",
                    Message = arrowFailure,
                    RecommendedAction = "יש להתקין מחדש את החבילה ולאמת את HW-ARRW-01 לפני מסירה.",
                });
            }

            int decorated = 0, utils = 0, marks = 0;
            foreach (var rec in result.Records)
            {
                if (string.IsNullOrEmpty(rec.Handles.SectionView) || rec.LogicalKey == null) continue;
                var record = targets.FirstOrDefault(t => t.RecordId == rec.RecordId);
                if (record?.SelectedCrossing == null) continue;

                try
                {
                    var (u, m) = DecorateOne(tr, db, profile, record, rec, collected,
                        existingStyleOk, vehicleBlocks, trafficArrowBlocks, result.RunId, log);
                    utils += u; marks += m; decorated++;
                }
                catch (Exception ex)
                {
                    // The annotations are part of the promised deliverable, not optional
                    // decoration. Keeping a bare Civil view and reporting it as Applied is
                    // a fail-open result: VERIFY used to pass while the engineer saw an
                    // empty section. Mark the record failed so ApplyCore can roll the
                    // transaction back atomically (including any old annotations erased
                    // earlier in this same transaction).
                    log?.Info($"decorate failed for {rec.RecordId}: {ex.Message}");
                    rec.Status = DeliveryStatus.Failed;
                    rec.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.LayoutUnresolved,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "סימון החתך (ציר/מערכות/מידות) לא הושלם לרשומה הזו",
                        Message = ex.Message,
                        AffectedRecordIds = { rec.RecordId },
                    });
                    throw new InvalidOperationException(
                        $"Section decoration failed for record '{rec.RecordId}'; " +
                        "the atomic APPLY batch must stop immediately.", ex);
                }
            }

            log?.Info($"decorate views={decorated} utilities={utils} marks={marks}");
            return new Outcome(decorated, utils, marks);
        }

        private static (int Utils, int Marks) DecorateOne(
            Transaction tr, Database db, ProjectProfile profile,
            SectionPlanRecord record, SectionApplyRecordResult rec,
            SectionGeometryCollector.CollectResult collected, bool surfaceStylesOk,
            SectionVehicleBlockService vehicleBlocks,
            SectionTrafficDirectionArrowService trafficArrowBlocks,
            string runId,
            StageLog? log)
        {
            var civilOpenMode = record.ManualSectionReuse == null
                ? OpenMode.ForWrite
                : OpenMode.ForRead;
            var view = OpenByHandle<CivilDb.SectionView>(
                tr, db, rec.Handles.SectionView!, civilOpenMode);
            var sampleLine = OpenByHandle<CivilDb.SampleLine>(
                tr, db, rec.Handles.SampleLine!, civilOpenMode);
            if (view == null || sampleLine == null)
                throw new InvalidOperationException("The created section view/sample line cannot be reopened for annotation.");

            // Replace this section's previous annotations (idempotent re-run).
            SectionAnnotationRegistry.EraseExisting(tr, db, rec.LogicalKey!);

            if (record.ManualSectionReuse == null)
                ApplySurfaceStyles(tr, profile, record, sampleLine, rec, surfaceStylesOk);

            var elevMin = view.ElevationMin;
            var elevMax = view.ElevationMax;
            var offMin = view.OffsetLeft;
            var offMax = view.OffsetRight;

            var frame = SectionCutGeometry.RequireFrame(record);
            var leftEnd = LeftEndpoint(sampleLine) ??
                throw new InvalidOperationException(
                    "SampleLine has no readable Civil left endpoint; annotation offset signs cannot be proven.");
            if (!frame.MatchesNativeLeftEndpoint(leftEnd))
                throw new InvalidOperationException(
                    "Civil SampleLine left endpoint differs from the planned straight-cut frame; annotation orientation is unproven.");
            if (!frame.IsContainedInDisplay(offMin, offMax))
                throw new InvalidOperationException(
                    "SectionView display range does not contain the complete planned CL cut; annotation coverage would be clipped.");

            var created = new List<Entity>();
            var databaseResident = new List<Entity>();
            var handles = new List<Handle>();
            var projectionEntities = new Dictionary<string, List<Entity>>(StringComparer.Ordinal);
            var renderedDirectionArrows =
                new List<SectionTrafficDirectionArrowService.RenderedArrow>();
            var renderedOfficeVehicles =
                new List<(BlockReference Reference, int EvidenceIndex, string Evidence)>();
            var corePresentation =
                new List<(Entity Entity, string Kind, string SemanticKey)>();

            void TrackProjection(string key, params Entity[] entities)
            {
                if (!projectionEntities.TryGetValue(key, out var list))
                    projectionEntities[key] = list = new List<Entity>();
                foreach (var entity in entities)
                {
                    created.Add(entity);
                    list.Add(entity);
                }
            }

            void TrackCore(Entity entity, string kind, string semanticKey)
            {
                created.Add(entity);
                corePresentation.Add((entity, kind, semanticKey));
            }

            try
            {
            // ------------------------------------------------------------ the axis
            var axis = SectionAnnotationPlacementContract.AxisLine(view, elevMin, elevMax);
            var axisLabel = SectionAnnotationPlacementContract.AxisLabelPosition(view, elevMax);
            if (axis is not { } axisPlacement || axisLabel is not { } axisTextPosition)
                throw new InvalidOperationException("SectionView could not map the axis into annotation coordinates.");
            TrackCore(NewLine(axisPlacement.Start, axisPlacement.End, colorIndex: 8,
                    linetype: SectionAnnotationResourceContracts.CenterLinetypeName),
                SectionCorePresentationContract.AxisLine,
                SectionCorePresentationContract.Key(SectionCorePresentationContract.AxisLine));
            TrackCore(NewText("ציר", axisTextPosition, 0.95, 7, rotation: 0, centered: true),
                SectionCorePresentationContract.AxisLabel,
                SectionCorePresentationContract.Key(SectionCorePresentationContract.AxisLabel, "ציר"));
            var titleAnchor = SectionAnnotationPlacementContract.TitlePosition(
                                  view, offMin, offMax, elevMax) ??
                throw new InvalidOperationException(
                    "SectionView could not map the title anchor into annotation coordinates.");
            // SEC-m3: the title names the alignment and chainage (PLAN values only).
            var titleText = SectionCorePresentationContract.TitleText(record);
            TrackCore(NewText(titleText, titleAnchor, 1.0, 7,
                    rotation: 0, centered: true),
                SectionCorePresentationContract.Title,
                SectionCorePresentationContract.Key(SectionCorePresentationContract.Title, titleText));

            // -------------------------------------------------------- utilities
            // Everything that hangs BELOW the grid as rotated text (utility labels,
            // offset digits) goes through ONE ladder so nothing prints over anything
            // (live screenshots, 31/08). Offset digits stay color 7. SEC-M3 (review of
            // 1.3.9): a utility label is drawn in its system's color, like Natali's
            // reference sheet; dark ACI colors are brightened exactly as the marker is,
            // so a dark-blue water label still cannot vanish on the black background.
            var bottomTexts = new List<(double Offset, string Text, double Height,
                string? ProjectionKey, double? DimensionAnchor, short ColorIndex)>();
            var dimensionOffsetLabels = new List<(DBText Text, double Anchor, double Placed)>();
            var utilityCrossings = SectionCutGeometry.CrossingsFor(collected.Utilities, frame);
            var actualProjectionKeys = utilityCrossings
                .Select(ProjectionEvidenceKey)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(k => k, _ => 1, StringComparer.Ordinal);
            var plannedComparison = CompareProjectionEvidence(
                record.ProjectedEntities.Select(p => p.ProjectionKey), actualProjectionKeys);
            if (!plannedComparison.IsExact)
                throw new InvalidOperationException(
                    "Projected utility geometry changed after PLAN. " +
                    $"Missing=[{string.Join(",", plannedComparison.Missing)}] " +
                    $"Unexpected=[{string.Join(",", plannedComparison.Unexpected)}]");

            var projectedLabels = new List<string>();
            foreach (var c in utilityCrossings)
            {
                var projectionKey = ProjectionEvidenceKey(c);
                var x0 = MapXY(view, c.Offset, elevMin);
                if (x0 is not { } bottom)
                    throw new InvalidOperationException(
                        $"Projection {projectionKey} could not map its bottom point into the SectionView.");

                if (c.Elevation is { } knownZ &&
                    (!double.IsFinite(knownZ) || knownZ <= elevMin || knownZ >= elevMax))
                {
                    var message = FormattableString.Invariant(
                        $"Projection {projectionKey} carries known elevation {knownZ:F3}, outside SectionView range {elevMin:F3}..{elevMax:F3}; it cannot be relabelled as unknown depth.");
                    rec.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.UtilityElevationOutOfRange,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "רום מערכת ידוע נמצא מחוץ לטווח החתך — ההחלה בוטלה",
                        Message = message,
                        RecommendedAction = "יש לתקן את רום המקור או את טווח החתך ולהריץ PLAN/APPLY מחדש; אין לסמן עומק ידוע כלא מוגדר.",
                        AffectedRecordIds = { rec.RecordId },
                    });
                    throw new InvalidOperationException(
                        $"{message} ({SectionFindingCodes.UtilityElevationOutOfRange})");
                }

                if (c.Elevation is { } z)
                {
                    var at = MapXY(view, c.Offset, z);
                    if (at is not { } zp)
                        throw new InvalidOperationException(
                            $"Projection {projectionKey} could not map elevation {z:F3} into the SectionView.");
                    var line = NewLine(bottom, zp, c.Rule.ColorIndex,
                        SectionAnnotationResourceContracts.DashedLinetypeName);
                    Brighten(line, c.Rule.ColorIndex);
                    // SEC-M3: a small fixed LOCATION marker centred at the source Z. The
                    // 1.2 m circle read as a 1.2 m pipe; the source carries no diameter.
                    var circle = new Circle(zp, Vector3d.ZAxis, SectionDrawingTextLogic.UtilityMarkerRadius)
                    { ColorIndex = c.Rule.ColorIndex, Layer = AnnoLayer };
                    Brighten(circle, c.Rule.ColorIndex);
                    TrackProjection(projectionKey, line, circle);
                    bottomTexts.Add((c.Offset, SectionDrawingTextLogic.UtilityLabel(c.Rule.Label, z), 0.7,
                        projectionKey, null, c.Rule.ColorIndex));
                }
                else // Source geometry is genuinely 2D/flat: depth is absent, not out of range.
                {
                    var top = MapXY(view, c.Offset, elevMax);
                    if (top is not { } tp)
                        throw new InvalidOperationException(
                            $"Projection {projectionKey} could not map its top point into the SectionView.");
                    var line = NewLine(bottom, tp, c.Rule.ColorIndex,
                        SectionAnnotationResourceContracts.DashedLinetypeName);
                    Brighten(line, c.Rule.ColorIndex);
                    TrackProjection(projectionKey, line);
                    bottomTexts.Add((c.Offset, SectionDrawingTextLogic.UtilityLabel(c.Rule.Label, null), 0.7,
                        projectionKey, null, c.Rule.ColorIndex));
                }
                projectedLabels.Add(c.Rule.Label);
            }

            // -------------------------------------------------------- plan marks
            // PLAN admitted ROW only from one exact engineer-approved source. APPLY
            // must repeat that same provenance gate before drawing or measuring it;
            // otherwise a suppressed photogrammetry/GM ROW could silently widen the
            // live section even though PLAN promised CL extents.
            var rowPolicy = SectionPlanService.SelectAuthoritativeRowMarks(
                collected.PlanMarks, profile.Sections.Projection.RowAuthorities);
            var rowState = rowPolicy.Selection.State.ToString().ToLowerInvariant();
            var plannedCoverage = record.PresentationCoverage;
            var candidateKeysExact = plannedCoverage.RowCandidateSourceKeys.SequenceEqual(
                rowPolicy.Selection.CandidateSourceKeys, StringComparer.Ordinal);
            if (!string.Equals(plannedCoverage.RowAuthorityState, rowState,
                    StringComparison.Ordinal) ||
                !string.Equals(plannedCoverage.AuthoritativeRowSourceKey,
                    rowPolicy.Selection.AuthoritativeSourceKey, StringComparison.Ordinal) ||
                !candidateKeysExact)
                throw new InvalidOperationException(
                    "ROW source authority changed after PLAN; APPLY refused to mix or invent right-of-way geometry " +
                    $"({SectionFindingCodes.RowAuthorityUnresolved}).");

            var markCrossings = SectionCutGeometry.DimensionCrossingsFor(rowPolicy.PlanMarks, frame);
            // Region geometry is reread and reinterpreted during APPLY. Copying its
            // planned override would conceal a moved hole or replaced source hatch.
            var approvedOverrides = plannedCoverage.ExplicitSpanOverrides
                .Where(item => !SectionHatchSpanLabelService.IsRegionEvidence(item.Source))
                .Select(item => new SpanLabelOverride(
                    item.OffsetM, item.Label, item.Source, item.Evidence))
                .ToList();
            var baseRegionAnalysis = AnalyzePresentationCoverage(
                markCrossings.Select(DimensionPresentationMark),
                requiredLeftOffset: frame.MinOffset, requiredRightOffset: frame.MaxOffset,
                approvedOverrides: approvedOverrides);
            approvedOverrides.AddRange(SectionHatchSpanLabelService.Resolve(
                collected.PlanRegions, markCrossings, baseRegionAnalysis));
            var presentation = AnalyzePresentationCoverage(
                markCrossings.Select(DimensionPresentationMark),
                requiredLeftOffset: frame.MinOffset,
                requiredRightOffset: frame.MaxOffset,
                approvedOverrides: approvedOverrides);
            var actualCoverage = presentation.Summary;
            var dimensionSemanticsExact =
                plannedCoverage.DimensionMarks.Count == presentation.DimensionMarks.Count &&
                plannedCoverage.DimensionMarks.Zip(presentation.DimensionMarks,
                    (planned, actual) =>
                        Math.Abs(planned.OffsetM - actual.Offset) <= 0.0005 &&
                        string.Equals(planned.Kind, actual.Kind, StringComparison.Ordinal) &&
                        string.Equals(planned.Label, actual.Label, StringComparison.Ordinal) &&
                        planned.GeometryKey == DimensionGeometryKey(actual.Offset) &&
                        DimensionSourcesCanonical(planned.SourceEvidence ?? new()) ==
                            DimensionSourcesCanonical(presentation.DimensionBoundaries.Single(boundary =>
                                boundary.Offset == actual.Offset).Sources) &&
                        planned.ColorIndex >= 1 && planned.ColorIndex <= 255).All(match => match);
            if (!actualCoverage.IsComplete || !plannedCoverage.Complete ||
                plannedCoverage.PlanMarkCount != actualCoverage.PlanMarkCount ||
                plannedCoverage.DimensionMarkCount != actualCoverage.DimensionMarkCount ||
                plannedCoverage.WidthSpanCount != actualCoverage.WidthSpanCount ||
                plannedCoverage.NamedStripCount != actualCoverage.NamedStripCount ||
                plannedCoverage.VehicleStripCount != actualCoverage.VehicleStripCount ||
                plannedCoverage.OfficeCarStripCount != actualCoverage.OfficeCarStripCount ||
                !string.Equals(plannedCoverage.EvidenceDigest, actualCoverage.EvidenceDigest,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(plannedCoverage.BoundarySource, actualCoverage.BoundarySource,
                    StringComparison.Ordinal) ||
                !NullableOffsetEquals(plannedCoverage.BoundaryFromM, actualCoverage.BoundaryFrom) ||
                !NullableOffsetEquals(plannedCoverage.BoundaryToM, actualCoverage.BoundaryTo) ||
                presentation.UnresolvedSpans.Count != 0 || !dimensionSemanticsExact)
                throw new InvalidOperationException(
                    "Plan-mark presentation geometry changed after PLAN or coverage is incomplete " +
                    $"({SectionFindingCodes.PresentationCoverageMissing}).");
            rec.PresentationCoverage = new SectionPresentationCoveragePlan
            {
                PlanMarkCount = actualCoverage.PlanMarkCount,
                DimensionMarkCount = actualCoverage.DimensionMarkCount,
                DimensionOffsets = presentation.DimensionMarks.Select(mark => mark.Offset).ToList(),
                WidthSpanCount = actualCoverage.WidthSpanCount,
                NamedStripCount = actualCoverage.NamedStripCount,
                VehicleStripCount = actualCoverage.VehicleStripCount,
                OfficeCarStripCount = actualCoverage.OfficeCarStripCount,
                EvidenceDigest = actualCoverage.EvidenceDigest,
                BoundarySource = actualCoverage.BoundarySource,
                BoundaryFromM = actualCoverage.BoundaryFrom,
                BoundaryToM = actualCoverage.BoundaryTo,
                RowAuthorityState = rowState,
                AuthoritativeRowSourceKey = rowPolicy.Selection.AuthoritativeSourceKey,
                Complete = actualCoverage.IsComplete,
            };
            rec.PresentationCoverage.DimensionMarks.AddRange(
                plannedCoverage.DimensionMarks.Select(mark => new SectionDimensionMarkPlan
                {
                    OffsetM = mark.OffsetM,
                    Kind = mark.Kind,
                    Label = mark.Label,
                    ColorIndex = mark.ColorIndex,
                    GeometryKey = mark.GeometryKey,
                    SourceEvidence = mark.SourceEvidence.ToList(),
                }));
            rec.PresentationCoverage.RowCandidateSourceKeys.AddRange(
                rowPolicy.Selection.CandidateSourceKeys);
            rec.PresentationCoverage.ExplicitSpanOverrides.AddRange(
                plannedCoverage.ExplicitSpanOverrides.Select(item =>
                    new SectionSpanLabelOverridePlan
                    {
                        OffsetM = item.OffsetM,
                        Label = item.Label,
                        Source = item.Source,
                        Evidence = item.Evidence,
                    }));
            rec.PresentationCoverage.ResolvedSpans.AddRange(
                plannedCoverage.ResolvedSpans.Select(item =>
                    new SectionResolvedSpanPlan
                    {
                        FromOffsetM = item.FromOffsetM,
                        ToOffsetM = item.ToOffsetM,
                        WidthM = item.WidthM,
                        LeftKind = item.LeftKind,
                        RightKind = item.RightKind,
                        Label = item.Label,
                        EvidenceSource = item.EvidenceSource,
                        EvidenceDigest = item.EvidenceDigest,
                    }));

            static bool NullableOffsetEquals(double? planned, double? actual) =>
                planned.HasValue == actual.HasValue &&
                (!planned.HasValue || Math.Abs(planned.Value - actual!.Value) <= 0.0005);

            // Draw from the normalized dimension contract, not raw source crossings.
            // This includes ROW anchors inserted by the pure coverage logic and avoids
            // duplicate coincident ticks. Every counted dimension therefore receives
            // a top tick, a bottom tick and one bounded offset label.
            foreach (var mark in presentation.DimensionMarks)
            {
                var plannedMark = plannedCoverage.DimensionMarks.Single(candidate =>
                    candidate.GeometryKey == DimensionGeometryKey(mark.Offset) &&
                    string.Equals(candidate.Kind, mark.Kind, StringComparison.Ordinal) &&
                    string.Equals(candidate.Label, mark.Label, StringComparison.Ordinal));
                var tickColor = plannedMark.ColorIndex;

                var topTick = SectionAnnotationPlacementContract.TopTick(
                    view, mark.Offset, elevMax) ?? throw new InvalidOperationException(
                    $"SectionView could not map top dimension tick at offset {mark.Offset:F3}.");
                TrackCore(NewLine(topTick.Start, topTick.End, tickColor, ""),
                    SectionCorePresentationContract.DimensionTopTick,
                    SectionCorePresentationContract.Key(
                        SectionCorePresentationContract.DimensionTopTick,
                        mark.Offset, mark.Kind, tickColor));

                var bottomTick = SectionAnnotationPlacementContract.BottomTick(
                    view, mark.Offset, elevMin) ?? throw new InvalidOperationException(
                    $"SectionView could not map bottom dimension tick at offset {mark.Offset:F3}.");
                TrackCore(NewLine(bottomTick.Start, bottomTick.End, 8, ""),
                    SectionCorePresentationContract.DimensionBottomTick,
                    SectionCorePresentationContract.Key(
                        SectionCorePresentationContract.DimensionBottomTick,
                        mark.Offset, mark.Kind, (short)8));
                bottomTexts.Add((mark.Offset, mark.Offset.ToString("0.00;-0.00", CultureInfo.InvariantCulture), 0.62,
                    null, mark.Offset, (short)7));

                if (!string.Equals(mark.Kind, "row", StringComparison.Ordinal))
                    continue;
                var rowLine = SectionAnnotationPlacementContract.RowLine(
                    view, mark.Offset, elevMin, elevMax) ?? throw new InvalidOperationException(
                    $"SectionView could not map ROW mark at offset {mark.Offset:F3}.");
                var row = NewLine(rowLine.Start, rowLine.End, colorIndex: 1, linetype: "");
                row.LineWeight = LineWeight.LineWeight050;
                TrackCore(row, SectionCorePresentationContract.RowLine,
                    SectionCorePresentationContract.Key(
                        SectionCorePresentationContract.RowLine, mark.Offset));
                var rowLabel = SectionAnnotationPlacementContract.RowLabelPosition(
                    view, mark.Offset, elevMax) ?? throw new InvalidOperationException(
                    $"SectionView could not map ROW label at offset {mark.Offset:F3}.");
                TrackCore(NewText("זכות דרך", rowLabel, 1.0, 1,
                        rotation: Math.PI / 2, centered: false),
                    SectionCorePresentationContract.RowLabel,
                    SectionCorePresentationContract.Key(
                        SectionCorePresentationContract.RowLabel, mark.Offset, "זכות דרך"));
            }

            // widths between adjacent curb/lane marks — the "3.00  6.35" row
            var widthSpans = presentation.WidthSpans;
            foreach (var (from, to, width) in widthSpans)
            {
                var mp = SectionAnnotationPlacementContract.WidthLabelPosition(
                    view, from, to, elevMax);
                if (mp is not { } widthPosition)
                    throw new InvalidOperationException(
                        $"SectionView could not map width span {from:F3}..{to:F3}.");
                var widthText = width.ToString("F2", CultureInfo.InvariantCulture);
                TrackCore(NewText(widthText, widthPosition, 0.75, 7,
                        rotation: 0, centered: true),
                    SectionCorePresentationContract.WidthLabel,
                    SectionCorePresentationContract.Key(
                        SectionCorePresentationContract.WidthLabel, from, to, widthText));
            }

            // Review of 1.3.9 (30/09): the closed dimension chain (SEC-B4: a width for
            // every gap piece between named strips, one chain line, one overall width)
            // and the drawn legend (SEC-m3 surfaces, SEC-M3 utility markers) come
            // straight from the PLAN semantic inventory, so APPLY and VERIFY cannot
            // disagree about which of them exist. None of them is an engineering value.
            foreach (var extra in SectionCorePresentationContract.ExpectedFor(
                         record, record.PresentationCoverage)
                         .Where(item => item.Kind is SectionCorePresentationContract.GapWidthLabel
                             or SectionCorePresentationContract.DimensionChainLine
                             or SectionCorePresentationContract.OverallWidthLabel
                             or SectionCorePresentationContract.LegendSurfaces
                             or SectionCorePresentationContract.LegendUtilities))
            {
                switch (extra.Kind)
                {
                    case SectionCorePresentationContract.GapWidthLabel:
                    {
                        var at = SectionAnnotationPlacementContract.WidthLabelPosition(
                                     view, extra.From!.Value, extra.To!.Value, elevMax) ??
                                 throw new InvalidOperationException(FormattableString.Invariant(
                                     $"SectionView could not map gap width {extra.From:F3}..{extra.To:F3}."));
                        TrackCore(NewText(extra.Text!, at,
                                SectionCorePresentationContract.GapWidthTextHeight, 7,
                                rotation: 0, centered: true),
                            extra.Kind, extra.SemanticKey);
                        break;
                    }
                    case SectionCorePresentationContract.DimensionChainLine:
                    {
                        var chainLine = SectionAnnotationPlacementContract.DimensionChainLine(
                                            view, extra.From!.Value, extra.To!.Value, elevMax) ??
                                        throw new InvalidOperationException(
                                            "SectionView could not map the dimension chain line.");
                        TrackCore(NewLine(chainLine.Start, chainLine.End, 8, ""),
                            extra.Kind, extra.SemanticKey);
                        break;
                    }
                    case SectionCorePresentationContract.OverallWidthLabel:
                    {
                        var at = SectionAnnotationPlacementContract.OverallWidthLabelPosition(
                                     view, extra.From!.Value, extra.To!.Value, elevMax) ??
                                 throw new InvalidOperationException(
                                     "SectionView could not map the overall width label.");
                        TrackCore(NewText(extra.Text!, at,
                                SectionCorePresentationContract.OverallWidthTextHeight, 7,
                                rotation: 0, centered: true),
                            extra.Kind, extra.SemanticKey);
                        break;
                    }
                    default:
                    {
                        var at = SectionAnnotationPlacementContract.LegendPosition(
                                     view, offMin, offMax, elevMax) ??
                                 throw new InvalidOperationException(
                                     "SectionView could not map the section legend.");
                        TrackCore(NewText(extra.Text!, at,
                                SectionCorePresentationContract.LegendTextHeight, 7,
                                rotation: 0, centered: true),
                            extra.Kind, extra.SemanticKey);
                        break;
                    }
                }
            }

            // One exact surface read powers every grounded annotation below.  APPLY
            // and VERIFY share this boundary; neither may reclassify a child by a
            // name heuristic after PLAN selected the EG/FG identities.
            var surfaceChains = SectionAnnotationPlacementContract.ReadSurfaceChains(
                tr, sampleLine, record, offMin, offMax, elevMin, elevMax);

            // Signed cross-slope labels sit on the REAL design section, never on the
            // graph frame and never on an EG fallback. Every adjacent width span is an
            // explicit presentation promise (SEC-04), so an absent/ambiguous design
            // chain or an endpoint outside that chain aborts the record atomically.
            var slopeLabels = new List<(DBText Text, SectionFurnitureLogic.SlopeEvidence Evidence)>();
            if (widthSpans.Count > 0)
            {
                var design = surfaceChains.Design;
                if (design.Count < 2)
                    throw SlopeFailure(
                        "No unique sampled design-surface chain is available for the marked width spans.");

                foreach (var (from, to, _) in widthSpans)
                {
                    if (!SectionFurnitureLogic.TrySlopeEvidence(
                            design, from, to, out var slope) || slope == null)
                        throw SlopeFailure(FormattableString.Invariant(
                            $"Design-surface endpoints are not proven for width span {from:F3}..{to:F3}."));

                    var at = SectionAnnotationPlacementContract.SlopePosition(
                        view, from, to, slope.FromElevation, slope.ToElevation);
                    if (at is not { } slopePoint)
                        throw SlopeFailure(FormattableString.Invariant(
                            $"SectionView could not map the grounded slope label for {from:F3}..{to:F3}."));

                    var canonicalSlope = slope with
                    {
                        Percent = Math.Round(slope.Percent, 6, MidpointRounding.AwayFromZero),
                    };
                    var text = NewText(
                        SectionFurnitureLogic.FormatSlopePercent(canonicalSlope.Percent),
                        slopePoint,
                        0.62, 7, rotation: 0, centered: true);
                    created.Add(text);
                    slopeLabels.Add((text, canonicalSlope));
                }
            }

            InvalidOperationException SlopeFailure(string message)
            {
                rec.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SlopeUnproven,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "שיפועי החתך לא הוכחו ממשטח התכנון — הרשומה לא תוחל",
                    Message = message,
                    AffectedRecordIds = { rec.RecordId },
                    RecommendedAction = "יש לוודא שמשטח התכנון היחיד של הציר נדגם ומכסה את כל תחומי הרוחב, ואז להריץ תכנון מחדש.",
                });
                return new InvalidOperationException(
                    $"{message} ({SectionFindingCodes.SlopeUnproven})");
            }

            // Strip names above the widths — נתיב נסיעה / מדרכה / שביל אופניים — derived
            // from the KINDS of the bounding marks (חתך 1039 style). Only confident pairs
            // are named: a curb-to-curb strip could be נת"צ or parking — the engineer says.
            var ground = surfaceChains.Design;
            var stripLabels = presentation.StripLabels;
            var trackContractError = SectionTrafficPlanContract.Validate(record);
            if (trackContractError != null)
                throw new InvalidOperationException("Source-track contract cannot be applied: " + trackContractError);
            var expectedVehicleInstances = SectionTrafficPlanContract.VehicleInstances(record);
            rec.ExpectedOfficeCarBlocks = SectionTrafficPlanContract.OfficeCarInstances(record);
            foreach (var (from, to, label) in stripLabels)
            {
                var sp = SectionAnnotationPlacementContract.StripLabelPosition(
                    view, from, to, elevMax);
                if (sp is not { } stripPosition)
                    throw new InvalidOperationException(
                        $"SectionView could not map named strip {from:F3}..{to:F3} ({label}).");
                TrackCore(NewText(label, stripPosition, 0.8, 7,
                        rotation: 0, centered: true),
                    SectionCorePresentationContract.StripLabel,
                    SectionCorePresentationContract.Key(
                        SectionCorePresentationContract.StripLabel, from, to, label));

                // The approved OFFICE vehicle block stands ON the sampled ground and
                // is normalized through the view transform. The legacy schematic is
                // retained only as a visible, reported technical fallback. No ground
                // sample means no vehicle; the tool never draws at a guessed height.
                var spec = SectionFurnitureLogic.VehicleForStrip(label);
                if (spec == null || !SectionFurnitureLogic.FitsStrip(spec, to - from)) continue;
                var trackEnvelope = record.CompositeTrafficEnvelopes.SingleOrDefault(envelope =>
                    envelope.FromOffsetM == from && envelope.ToOffsetM == to);
                var vehicleOffsets = trackEnvelope == null
                    ? new[] { (from + to) / 2.0 }
                    : trackEnvelope.SourceTracks.Select(track => track.OffsetM).ToArray();
                foreach (var midOff in vehicleOffsets)
                {
                var plannedDirection = ResolvePlannedDirection(
                    record, from, to, label, spec, midOff);
                var groundZ = ground.Count > 1
                    ? MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(ground, midOff)
                    : null;
                if (groundZ is not { } gz || gz <= elevMin || gz >= elevMax)
                {
                    var groundError = groundZ is null
                        ? FormattableString.Invariant(
                            $"No sampled ground chain spans car-strip offset {midOff:F3} (points={ground.Count}).")
                        : FormattableString.Invariant(
                            $"Sampled ground {groundZ.Value:F3} at car-strip offset {midOff:F3} is outside the section range {elevMin:F3}..{elevMax:F3}.");
                    rec.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.VehicleBlockUnavailable,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "ריהוט הרצועה לא הוצב — לא נמצא רום קרקע מוכח ברצועה",
                        Message = groundError,
                        AffectedRecordIds = { rec.RecordId },
                        RecommendedAction = "יש לבדוק שמשטח המצב הקיים נדגם וחוצה את נתיב הרכב, ואז להריץ תכנון והחלה מחדש.",
                    });
                    log?.Info($"decorate {rec.RecordId}: vehicle ground unavailable error={groundError}");
                    throw new InvalidOperationException(
                        "The planned vehicle furniture has no proven sampled-ground elevation; " +
                        $"empty vehicle evidence is not a deliverable ({SectionFindingCodes.VehicleBlockUnavailable}).");
                }

                var stripKind = spec.Key switch
                {
                    "bus" => SectionTrafficDirectionAnnotationLogic.StripKind.Bus,
                    "bike" => SectionTrafficDirectionAnnotationLogic.StripKind.Bike,
                    _ => SectionTrafficDirectionAnnotationLogic.StripKind.Road,
                };
                var requiredTop = gz +
                    SectionTrafficDirectionAnnotationLogic.RequiredHeadroomM(stripKind);
                if (requiredTop > elevMax + 1e-6)
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"Visible traffic arrow for {label} at {midOff:F3} needs elevation {requiredTop:F3}, but the SectionView ends at {elevMax:F3} ({SectionFindingCodes.TrafficDirectionEvidenceMismatch})."));

                if (!trafficArrowBlocks.TryCreate(
                        tr, view, midOff, gz, stripKind, plannedDirection, AnnoLayer,
                        out var renderedArrow, out var arrowError) || renderedArrow == null)
                    throw new InvalidOperationException(
                        $"Visible traffic arrow failed for strip {midOff:F3}: {arrowError} " +
                        $"({SectionFindingCodes.TrafficDirectionEvidenceMismatch}).");
                foreach (var entity in renderedArrow.Entities)
                    created.Add(entity);
                renderedDirectionArrows.Add(renderedArrow);

                if (spec.Key == SectionFurnitureLogic.Car.Key)
                {
                    if (vehicleBlocks.TryCreateCarReference(
                            tr, view, spec, midOff, gz, plannedDirection,
                            out var officeBlock, out var evidence, out var blockError) &&
                        officeBlock != null)
                    {
                        // Persisted and registered below with every other tool-owned
                        // annotation. No manual BlockReference is searched or changed.
                        created.Add(officeBlock);
                        var evidenceIndex = rec.VehicleBlocks.Count;
                        rec.VehicleBlocks.Add(evidence);
                        renderedOfficeVehicles.Add((officeBlock, evidenceIndex, evidence));
                        log?.Info($"decorate {rec.RecordId}: vehicle {evidence}");
                        continue;
                    }

                    rec.VehicleBlocks.Add(evidence);
                    rec.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.VehicleBlockUnavailable,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "בלוק הרכב המשרדי לא הוצב בנתיב — החתך לא יאושר בלי הבלוק",
                        Message = blockError ?? "Office block placement failed without a diagnostic.",
                        AffectedRecordIds = { rec.RecordId },
                        RecommendedAction = "יש לבדוק את משאב הבלוק/קנה המידה ולנסות שוב; אין למסור רכב סכמטי במקום הבלוק המשרדי.",
                    });
                    log?.Info($"decorate {rec.RecordId}: vehicle fallback {evidence} error={blockError}");
                    throw new InvalidOperationException(
                        "The approved office car block could not be placed; schematic fallback is not a deliverable " +
                        $"({SectionFindingCodes.VehicleBlockUnavailable}).");
                }

                foreach (var entity in BuildSchematicVehicle(view, spec, midOff, gz))
                    created.Add(entity);
                if (spec.Key != SectionFurnitureLogic.Car.Key)
                    rec.VehicleBlocks.Add(
                        SectionAnnotationContractLogic.FormatSchematicVehicleReference(
                            spec.Key, midOff, plannedDirection));
                }
            }
            if (rec.VehicleBlocks.Count != expectedVehicleInstances)
                throw new InvalidOperationException(
                    $"Vehicle evidence is incomplete: planned {expectedVehicleInstances}, " +
                    $"created {rec.VehicleBlocks.Count} ({SectionFindingCodes.PresentationCoverageMissing}).");

            // The shared bottom ladder: anchors sorted, pushed apart, drawn once.
            // Duplicate texts at the same spot (two mark layers on one curb line)
            // collapse first — "1.22  1.22" reached the engineer's screen (31/08).
            bottomTexts = bottomTexts
                .GroupBy(t => (Math.Round(t.Offset, 1), t.Text, t.ProjectionKey,
                    t.DimensionAnchor))
                .Select(g => g.First())
                .ToList();
            if (bottomTexts.Count > 0)
            {
                var scaleStart = MapXY(view, offMin, elevMin);
                var scaleEnd = MapXY(view,
                    Math.Min(offMax, offMin + 1.0), elevMin);
                var mappedOffsetSpan = Math.Min(1.0, offMax - offMin);
                var xUnitsPerOffset = scaleStart is { } ss && scaleEnd is { } se &&
                                      mappedOffsetSpan > 1e-9
                    ? Math.Abs(se.X - ss.X) / mappedOffsetSpan
                    : 0.0;
                if (!double.IsFinite(xUnitsPerOffset) || xUnitsPerOffset <= 1e-9)
                    throw new InvalidOperationException(
                        "SectionView horizontal scale is unreadable; bottom labels cannot be bounded.");

                // Rotated text: spaced by the ink column it really occupies (insertion
                // shift, one text height, descenders, clearance), converted through the
                // live SectionView horizontal scale. The measured layout below still
                // moves any label whose real ink touches another one down on a leader.
                if (!SectionFurnitureLogic.TryRotatedBottomLabelLadder(
                        bottomTexts.Select(t => t.Offset).ToList(),
                        bottomTexts.Select(t => t.Height).ToList(),
                        xUnitsPerOffset, SectionAnnotationPlacementLogic.DimensionRightShift,
                        offMin, offMax,
                        out var laddered, out var ladderError))
                {
                    rec.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.LayoutUnresolved,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "שורת תוויות התחתית צפופה מדי — החתך לא הוחל",
                        Message = ladderError,
                        RecommendedAction = "יש להרחיב את תחום החתך/קנה המידה או לצמצם מקורות חופפים, ואז להריץ PLAN/APPLY מחדש.",
                        AffectedRecordIds = { rec.RecordId },
                    });
                    throw new InvalidOperationException(
                        $"{ladderError} ({SectionFindingCodes.LayoutUnresolved})");
                }
                for (int i = 0; i < bottomTexts.Count; i++)
                {
                    var at = SectionAnnotationPlacementContract.DimensionLabelPosition(
                        view, laddered[i], elevMin);
                    if (at is not { } bp)
                        throw new InvalidOperationException(
                            $"Bottom label at true offset {bottomTexts[i].Offset:F3} could not map its bounded position {laddered[i]:F3}.");
                    var text = NewText(bottomTexts[i].Text,
                        bp, bottomTexts[i].Height, bottomTexts[i].ColorIndex,
                        rotation: -Math.PI / 2, centered: false);
                    if (bottomTexts[i].ProjectionKey is { } projectionKey)
                    {
                        Brighten(text, bottomTexts[i].ColorIndex);
                        TrackProjection(projectionKey, text);
                    }
                    else
                        created.Add(text);
                    if (bottomTexts[i].DimensionAnchor is { } anchor)
                        dimensionOffsetLabels.Add((text, anchor, laddered[i]));
                }
            }

            // ONE stated elevation: a proven existing-ground elevation at the axis,
            // instead of a pile of native per-surface band rows.  ElevationMin is a
            // presentation boundary, not survey evidence, so there is deliberately
            // no fallback to it.
            var egChain = surfaceChains.Existing;
            var egAtAxis = egChain.Count > 1
                ? MahodAI.CivilDelivery.Estimate.CorridorQuantityLogic.ElevationAt(egChain, 0.0)
                : null;
            if (egAtAxis is not { } datumElevation)
                throw new InvalidOperationException(
                    "Existing-ground elevation at the section axis could not be proven (SEC-DATUM-UNPROVEN).");
            var corner = SectionAnnotationPlacementContract.DatumPosition(view, offMin, elevMin)
                ?? throw new InvalidOperationException(
                    "SectionView could not map the datum annotation (SEC-DATUM-UNPROVEN).");
            // SEC-M2 (review of 1.3.9): the text now says WHICH point it is. The value
            // is unchanged (proven EG at the axis); whether the single reference should
            // be existing or design ground remains Natali's decision.
            var datumText = NewText(SectionDrawingTextLogic.DatumText(datumElevation),
                corner, 0.75, 8,
                rotation: 0, centered: false);
            created.Add(datumText);

            // ------------------------------------------------------------ persist
            var btr = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            foreach (var ent in created)
            {
                ent.Layer = AnnoLayer;
                btr.AppendEntity(ent);
                databaseResident.Add(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                // A justified DBText keeps a stale Position until AutoCAD recomputes it
                // from the alignment point; without this every centered label is drawn
                // from its left baseline (checked before the first live decoration, 07/09).
                if (ent is DBText justified &&
                    (justified.HorizontalMode != TextHorizontalMode.TextLeft ||
                     justified.VerticalMode != TextVerticalMode.TextBase))
                    justified.AdjustAlignment(db);
                handles.Add(ent.Handle);
            }
            var expectedCore = SectionCorePresentationContract.ExpectedFor(
                record, record.PresentationCoverage);
            var expectedCoreKeys = expectedCore.Select(item => item.SemanticKey)
                .OrderBy(value => value, StringComparer.Ordinal).ToList();
            var actualCoreKeys = corePresentation.Select(item => item.SemanticKey)
                .OrderBy(value => value, StringComparer.Ordinal).ToList();
            if (!expectedCoreKeys.SequenceEqual(actualCoreKeys, StringComparer.Ordinal) ||
                corePresentation.Select(item => item.Entity.Handle.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != corePresentation.Count)
                throw new InvalidOperationException(
                    "Core section-presentation entities do not match the exact PLAN semantic inventory.");
            rec.CorePresentationAnnotations.Clear();
            rec.CorePresentationAnnotations.AddRange(corePresentation.Select(item =>
                new SectionCoreAnnotationEvidence
                {
                    Kind = item.Kind,
                    SemanticKey = item.SemanticKey,
                    Handle = item.Entity.Handle.ToString(),
                }));
            foreach (var office in renderedOfficeVehicles)
                rec.VehicleBlocks[office.EvidenceIndex] =
                    SectionVehicleBlockService.BindLiveReference(
                        office.Evidence, office.Reference);
            if (handles.Count == 0)
                throw new InvalidOperationException("The section annotation pass produced no registered entities.");
            rec.DatumReference = FormattableString.Invariant(
                $"existing-ground-at-axis|elevation={datumElevation:F3}|handle={datumText.Handle}");
            foreach (var (text, evidence) in slopeLabels)
                rec.SlopeLabels.Add(SectionAnnotationContractLogic.FormatSlopeReference(
                    evidence, text.Handle.ToString()));
            if (rec.SlopeLabels.Count != actualCoverage.WidthSpanCount)
                throw new InvalidOperationException(
                    $"Slope evidence is incomplete: planned {actualCoverage.WidthSpanCount}, " +
                    $"created {rec.SlopeLabels.Count} ({SectionFindingCodes.PresentationCoverageMissing}).");

            rec.DimensionOffsetLabels.Clear();
            foreach (var (text, anchor, placed) in dimensionOffsetLabels)
                rec.DimensionOffsetLabels.Add(
                    SectionAnnotationContractLogic.FormatDimensionOffsetLabelReference(
                        anchor, placed, text.Handle.ToString()));
            if (rec.DimensionOffsetLabels.Count != actualCoverage.DimensionMarkCount)
                throw new InvalidOperationException(
                    $"Dimension-offset label evidence is incomplete: planned {actualCoverage.DimensionMarkCount}, " +
                    $"created {rec.DimensionOffsetLabels.Count} ({SectionFindingCodes.PresentationCoverageMissing}).");

            rec.TrafficDirectionArrows.Clear();
            foreach (var arrow in renderedDirectionArrows)
                rec.TrafficDirectionArrows.Add(
                    SectionTrafficDirectionArrowService.Evidence(arrow));
            if (rec.TrafficDirectionArrows.Count != record.TrafficDirections.Count ||
                rec.TrafficDirectionArrows.Count != expectedVehicleInstances)
                throw new InvalidOperationException(
                    $"Direction-arrow evidence is incomplete: planned {record.TrafficDirections.Count}, " +
                    $"created {rec.TrafficDirectionArrows.Count} " +
                    $"({SectionFindingCodes.TrafficDirectionEvidenceMismatch}).");

            // Native text extents are reliable only after append and justification.
            // Layout therefore runs here, before any ownership fingerprints are saved.
            // Text content, size, rotation and semantic X stay unchanged; no surface,
            // source geometry or vehicle moves. The one proven datum value is unchanged;
            // its label and the bottom offset labels may move downward with leaders.
            var measuredLayout = SectionAnnotationPlacementContract.ComputeLabelLayout(
                view, record, rec, surfaceChains, created);
            foreach (var label in created.OfType<DBText>())
            {
                if (!measuredLayout.ExpectedPositionsByHandle.TryGetValue(
                        label.Handle.ToString(), out var expected)) continue;
                var current = SectionAnnotationPlacementContract.TextAnchor(label);
                label.TransformBy(Matrix3d.Displacement(expected - current));
                if (label.HorizontalMode != TextHorizontalMode.TextLeft ||
                    label.VerticalMode != TextVerticalMode.TextBase)
                    label.AdjustAlignment(db);
            }
            SectionAnnotationPlacementContract.RequirePlacedLabelBounds(measuredLayout, created);
            foreach (var stem in measuredLayout.Leaders)
            {
                var leader = NewLine(stem.Start, stem.End, colorIndex: 8, linetype: "");
                leader.Layer = AnnoLayer;
                created.Add(leader);
                btr.AppendEntity(leader);
                databaseResident.Add(leader);
                tr.AddNewlyCreatedDBObject(leader, true);
                handles.Add(leader.Handle);
            }
            log?.Info(FormattableString.Invariant(
                $"decorate {rec.RecordId}: measured-layout labels={measuredLayout.ExpectedPositionsByHandle.Count} leaders={measuredLayout.Leaders.Count} clearance={measuredLayout.Clearance:F3} bounds={measuredLayout.OverallBounds.MinX:R},{measuredLayout.OverallBounds.MinY:R}..{measuredLayout.OverallBounds.MaxX:R},{measuredLayout.OverallBounds.MaxY:R}"));

            var projectionHandles = projectionEntities.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<Handle>)kv.Value.Select(e => e.Handle).ToList(),
                StringComparer.Ordinal);
            var projectionFingerprints = projectionEntities.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, string>)kv.Value.ToDictionary(
                    e => e.Handle.ToString(),
                    SectionProjectionAnnotationSemantics.Fingerprint,
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
            var unproven = record.ProjectedEntities
                .Where(p => !projectionHandles.TryGetValue(p.ProjectionKey, out var hs) || hs.Count == 0)
                .Select(p => p.ProjectionKey)
                .ToList();
            if (unproven.Count > 0)
                throw new InvalidOperationException(
                    "No annotation entities were produced for projections: " + string.Join(", ", unproven));

            SectionAnnotationRegistry.Record(
                tr, db, rec.LogicalKey!, handles,
                fingerprint => new OwnershipMetadata
                {
                    Feature = "sections",
                    Role = "annotation",
                    ProjectProfileId = profile.ProfileId,
                    RunId = runId,
                    SourceClDrawingHash = record.Cl.SourceDrawingHash,
                    SourceClHandle = record.Cl.SourceHandle,
                    LogicalKey = rec.LogicalKey!,
                    InputFingerprint = fingerprint,
                    CreatedByToolVersion = SectionPlanService.ToolVersion,
                },
                projectionHandles, projectionFingerprints);

            // This is written only after the complete annotation set and its registry
            // entry exist in the caller-owned transaction.  VERIFY can therefore
            // distinguish "no vehicle strips" from a legacy/incomplete apply record.
            rec.AnnotationContractVersion = SectionAnnotationContractLogic.CurrentVersion;
            // The dash length depends on drawing state that lives on no entity; record
            // what the guide lines were drawn against so VERIFY can prove nothing moved.
            rec.LinetypeDisplayEvidence = LinetypeDisplayContract(db);
            rec.VehicleEvidenceComplete = true;
            rec.SlopeEvidenceComplete = true;

            rec.ProjectedSystems.AddRange(projectedLabels.Distinct());
            rec.ProjectedEntities.Clear();
            foreach (var planEvidence in record.ProjectedEntities.OrderBy(p => p.ProjectionKey, StringComparer.Ordinal))
            {
                rec.ProjectedEntities.Add(new ProjectedEntityEvidence
                {
                    ProjectionKey = planEvidence.ProjectionKey,
                    SystemLabel = planEvidence.SystemLabel,
                    AnnotationHandles = projectionHandles[planEvidence.ProjectionKey]
                        .Select(h => h.ToString()).ToList(),
                    AnnotationFingerprints = projectionFingerprints[planEvidence.ProjectionKey]
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                });
            }
            rec.AnnotationCount = handles.Count;
            log?.Info($"decorate {rec.RecordId}: entities={handles.Count} " +
                      $"utilities={utilityCrossings.Count} marks={markCrossings.Count}");
            return (utilityCrossings.Count, markCrossings.Count);
            }
            finally
            {
                // Entities that reached AppendEntity are database-resident and belong
                // to the caller-owned transaction. Dispose only transient wrappers
                // created before a fail-closed branch; otherwise a failed record can
                // leak native AutoCAD objects until process exit.
                foreach (var entity in created)
                {
                    if (databaseResident.Any(saved => ReferenceEquals(saved, entity)))
                        continue;
                    try { entity.Dispose(); }
                    catch { /* best-effort cleanup must not hide the original failure */ }
                }
            }
        }

        private static SectionVehicleDirectionPlanner.DirectionPlan ResolvePlannedDirection(
            SectionPlanRecord record,
            double from,
            double to,
            string label,
            SectionFurnitureLogic.VehicleSpec spec,
            double midpoint)
        {
            var matches = record.TrafficDirections.Where(direction =>
                    Math.Abs(direction.FromOffsetM - from) <=
                        SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM &&
                    Math.Abs(direction.ToOffsetM - to) <=
                        SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM &&
                    Math.Abs(direction.LaneMidOffsetM - midpoint) <=
                        SectionVehicleDirectionPlanner.ManualLaneOffsetToleranceM &&
                    string.Equals(direction.StripLabel, label, StringComparison.Ordinal))
                .ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one PLAN direction for strip {from:F3}..{to:F3} ({label}); " +
                    $"found {matches.Count} ({SectionFindingCodes.TrafficDirectionEvidenceMismatch}).");

            var planned = matches[0];
            if (!planned.IsResolved ||
                !SectionVehicleDirectionPlanner.TryRestoreResolved(
                    planned.Flow,
                    planned.EvidenceMode,
                    planned.DirectionSource,
                    planned.DirectionDigest,
                    planned.Reason,
                    out var restored) || restored == null)
                throw new InvalidOperationException(
                    $"PLAN direction for strip {midpoint:F3} is unresolved or malformed " +
                    $"({SectionFindingCodes.TrafficDirectionUnresolved}).");

            var expectedKind = spec.Key switch
            {
                "bus" => "bus",
                "bike" => "bike",
                _ => "road",
            };
            var expectedMode = spec.Key == "bike" ? "bicycle" : "motor";
            var expectedView = restored.OfficeCarView?.ToString().ToLowerInvariant();
            if (!string.Equals(planned.StripKind, expectedKind, StringComparison.Ordinal) ||
                !string.Equals(planned.EvidenceMode, expectedMode, StringComparison.Ordinal) ||
                !string.Equals(planned.OfficeCarView, expectedView, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PLAN direction kind/view contradicts strip {midpoint:F3} " +
                    $"({SectionFindingCodes.TrafficDirectionEvidenceMismatch}).");
            return restored;
        }

        /// <summary>Silhouette loops + wheel circles, mapped through the view transform.</summary>
        internal static IReadOnlyList<Entity> BuildSchematicVehicle(
            CivilDb.SectionView view,
            SectionFurnitureLogic.VehicleSpec spec, double midOffset, double groundZ)
        {
            var created = new List<Entity>();
            var (loops, circles) = SectionFurnitureLogic.VehicleGeometry(spec);
            try
            {
                foreach (var loop in loops)
                {
                    var pl = new Polyline();
                    foreach (var (dx, dy) in loop)
                    {
                        var pt = MapXY(view, midOffset + dx, groundZ + dy);
                        if (pt is not { } wp)
                            throw new InvalidOperationException(
                                "SectionView could not map the complete schematic vehicle silhouette.");
                        pl.AddVertexAt(pl.NumberOfVertices, new Point2d(wp.X, wp.Y), 0, 0, 0);
                    }
                    if (pl.NumberOfVertices < 2)
                        throw new InvalidOperationException(
                            "Schematic vehicle silhouette contains fewer than two vertices.");
                    pl.Closed = loop.Count > 2;
                    pl.ColorIndex = 8;
                    pl.Layer = AnnoLayer;
                    created.Add(pl);
                }
                foreach (var (dx, dy, r) in circles)
                {
                    var c0 = MapXY(view, midOffset + dx, groundZ + dy);
                    var c1 = MapXY(view, midOffset + dx, groundZ + dy + r);
                    if (c0 is not { } cc || c1 is not { } cr)
                        throw new InvalidOperationException(
                            "SectionView could not map a complete schematic vehicle wheel.");
                    var radius = Math.Abs(cr.Y - cc.Y);
                    if (!double.IsFinite(radius) || radius < 1e-6)
                        throw new InvalidOperationException(
                            "Schematic vehicle wheel mapped to an invalid radius.");
                    created.Add(new Circle(cc, Vector3d.ZAxis, radius)
                        { ColorIndex = 8, Layer = AnnoLayer });
                }
                return created;
            }
            catch
            {
                foreach (var entity in created)
                {
                    try { entity.Dispose(); } catch { }
                }
                throw;
            }
        }

        // ------------------------------------------------------------- styles

        /// <summary>Existing ground dashed green, design red — readable at a glance.</summary>
        private static bool EnsureSectionStyles(Transaction tr, CivilDocument civilDoc, SectionApplyResult result, bool allowModify)
        {
            try
            {
                var styles = civilDoc.Styles.SectionStyles;
                EnsureOne(SectionSurfaceStyleContractLogic.Existing);
                EnsureOne(SectionSurfaceStyleContractLogic.Design);
                return true;

                void EnsureOne(SectionSurfaceStyleContractLogic.Spec spec)
                {
                    var existed = styles.Contains(spec.Name);
                    var id = existed ? styles[spec.Name] : styles.Add(spec.Name);
                    var style = (CivilStyles.SectionStyle)tr.GetObject(
                        id, existed ? OpenMode.ForRead : OpenMode.ForWrite);

                    // A reserved-looking name is not ownership. Never mutate a
                    // pre-existing office style unless it carries our exact marker.
                    if (existed && !SectionSurfaceStyleContractLogic.IsToolOwnedDescription(
                            spec, style.Description))
                        throw new InvalidOperationException(
                            $"Section style name collision: '{spec.Name}' is not Mahod-owned.");
                    if (existed)
                    {
                        var liveDisplay = style.GetDisplayStyleSection(
                            CivilStyles.SectionDisplayStyleSectionType.Segments);
                        var liveColor = liveDisplay.Color;
                        var compliant = SectionSurfaceStyleContractLogic.TryValidateLive(
                            spec, style.Name, style.Description, (short)liveColor.ColorIndex,
                            liveColor.ColorMethod.ToString(), liveDisplay.Visible, liveDisplay.Linetype,
                            out _) &&
                            double.IsFinite(liveDisplay.LinetypeScale) &&
                            Math.Abs(liveDisplay.LinetypeScale -
                                EffectiveAnnotationLinetypeScale(
                                    HostApplicationServices.WorkingDatabase ??
                                    throw new InvalidOperationException("No working database for Section style."))) <= 1e-9;
                        var action = SharedResourceLogic.Decide(true, compliant,
                            allowModify ? SharedResourceLogic.Mode.NormalizeAll
                                        : SharedResourceLogic.Mode.CreateOnlyNeverModify);
                        if (action == SharedResourceLogic.Action.NoOp) return;
                        if (action == SharedResourceLogic.Action.Block)
                            throw new InvalidOperationException(SharedResourceBlocked("סגנון חתך", spec.Name));
                        style.UpgradeOpen();
                    }
                    style.Description = spec.Description;

                    var display = style.GetDisplayStyleSection(
                        CivilStyles.SectionDisplayStyleSectionType.Segments);
                    display.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, spec.ColorIndex);
                    display.Visible = true;
                    // Design must be reset to solid even when a prior owned style was
                    // edited; retaining a dashed linetype makes EG/FG ambiguous.
                    display.Linetype = spec.Linetype;
                    display.LinetypeScale = EffectiveAnnotationLinetypeScale(
                        HostApplicationServices.WorkingDatabase ??
                        throw new InvalidOperationException("No working database for Section style."));

                    var color = display.Color;
                    if (!SectionSurfaceStyleContractLogic.TryValidateLive(
                            spec,
                            style.Name,
                            style.Description,
                            (short)color.ColorIndex,
                            color.ColorMethod.ToString(),
                            display.Visible,
                            display.Linetype,
                            out var error) ||
                        !double.IsFinite(display.LinetypeScale) ||
                        Math.Abs(display.LinetypeScale - EffectiveAnnotationLinetypeScale(
                            HostApplicationServices.WorkingDatabase ??
                            throw new InvalidOperationException("No working database for Section style."))) > 1e-9)
                        throw new InvalidOperationException(
                            $"Reserved Section style '{spec.Name}' failed read-back: {error}.");
                }
            }
            catch (Exception ex)
            {
                result.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.StyleMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "לא ניתן היה להכין סגנונות קיים/מתוכנן — ההחלה תבוטל ללא שינוי חלקי",
                    Message = ex.Message,
                });
                return false;
            }
        }

        private static void ApplySurfaceStyles(
            Transaction tr, ProjectProfile profile, SectionPlanRecord record,
            CivilDb.SampleLine sampleLine,
            SectionApplyRecordResult rec, bool stylesOk)
        {
            if (!stylesOk)
                throw new InvalidOperationException(
                    "Managed existing/design Section styles are unavailable (SEC-STYLE-MISSING).");
            var plannedSurfaces = record.PlannedSources
                .Where(source => source.Required &&
                                 string.Equals(source.PlannedState, "sampled", StringComparison.Ordinal) &&
                                 string.Equals(source.SourceType, "surface", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (plannedSurfaces.Count != 2 ||
                string.Equals(plannedSurfaces[0].SourceName, plannedSurfaces[1].SourceName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Record '{rec.RecordId}' does not carry one distinct planned EG/FG surface pair.");

            // PlanSurfaceSources writes the selected existing-ground source first and
            // the selected design source second. APPLY styles only those exact names;
            // an unrelated sampled surface is never guessed into either role.
            var expectedBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [plannedSurfaces[0].SourceName] = ExistingStyleName,
                [plannedSurfaces[1].SourceName] = DesignStyleName,
            };
            var patterns = ExistingPatterns(profile.Sections.Projection.ExistingSurfacePatterns);
            var reviewedPair = SectionSurfacePairPlanLogic.RequireExplicit(record, profile,
                sampleLine.Database.FingerprintGuid);
            if (reviewedPair == null &&
                (!patterns.Any(pattern => Wildcard(plannedSurfaces[0].SourceName, pattern)) ||
                patterns.Any(pattern => Wildcard(plannedSurfaces[1].SourceName, pattern))))
                throw new InvalidOperationException(
                    $"Planned EG/FG source order contradicts the approved existing-surface patterns for '{rec.RecordId}'.");

            var styledCounts = expectedBySource.Keys.ToDictionary(
                name => name, _ => 0, StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId secId in sampleLine.GetSectionIds())
            {
                try
                {
                    if (tr.GetObject(secId, OpenMode.ForWrite) is not CivilDb.Section section) continue;
                    // Corridor-surface Sections too, keyed by the surface's own name.
                    if (!SectionSourceService.IsSurfaceSection(section.SourceType)) continue;
                    var sourceName = section.SourceName ?? string.Empty;
                    if (!expectedBySource.TryGetValue(sourceName, out var expected))
                        continue;
                    styledCounts[sourceName]++;
                    section.StyleName = expected;
                    if (!string.Equals(section.StyleName, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Section '{section.SourceName}' style read-back differs from '{expected}'.");
                }
                catch (Exception ex)
                {
                    rec.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.StyleMissing,
                        Domain = SectionPlanLogic.Domain,
                        Severity = FindingSeverity.Error,
                        Title = "סגנון קיים/מתוכנן לא הוחל על אחד המשטחים — הרשומה תבוטל",
                        Message = ex.Message,
                        AffectedRecordIds = { rec.RecordId },
                    });
                    throw new InvalidOperationException(
                        $"Managed Section style application failed for record '{rec.RecordId}' (SEC-STYLE-MISSING).", ex);
                }
            }

            var mismatches = styledCounts.Where(pair => pair.Value != 1).ToList();
            if (mismatches.Count > 0)
                throw new InvalidOperationException(
                    $"Exact planned surface Section mapping failed for record '{rec.RecordId}': " +
                    string.Join(", ", mismatches.Select(pair => $"{pair.Key}={pair.Value}")) +
                    " (SEC-STYLE-MISSING).");
        }

        internal static void TryAttachBandSet(
            Transaction tr, CivilDocument civilDoc, CivilDb.SectionView view,
            ProjectProfile profile, SectionApplyRecordResult rec)
        {
            try
            {
                if (!view.IsWriteEnabled)
                    throw new InvalidOperationException(
                        "SectionView must be open ForWrite before importing a configured band set.");
                var bandSets = civilDoc.Styles.SectionViewBandSetStyles;
                var wanted = profile.Sections.Styles.BandSetStyle;
                if (string.IsNullOrWhiteSpace(wanted))
                    throw new InvalidOperationException(
                        "Configured band-set path was called without a configured style name.");
                if (!bandSets.Contains(wanted))
                    throw new InvalidOperationException(
                        $"Configured band-set style '{wanted}' is missing; no fallback is permitted.");

                var id = bandSets[wanted];
                if (id.IsNull || tr.GetObject(id, OpenMode.ForRead) is not
                        CivilStyles.SectionViewBandSetStyle configured ||
                    !string.Equals(configured.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Configured band-set style '{wanted}' could not be read back exactly.");

                List<string> expectedBottom;
                List<string> expectedTop;
                // Release the source-style native collections before importing the
                // style into the live view. Holding both wrappers across the Civil
                // mutation increases native re-entrancy/lifetime risk.
                {
                    using var configuredBottom = configured.GetBottomBandSetItems();
                    using var configuredTop = configured.GetTopBandSetItems();
                    expectedBottom = ReadBandStyleIdSequence(configuredBottom);
                    expectedTop = ReadBandStyleIdSequence(configuredTop);
                }
                if (expectedBottom.Count + expectedTop.Count == 0)
                    throw new InvalidOperationException(
                        $"Configured band-set style '{wanted}' contains no readable band items.");

                view.Bands.ImportBandSetStyle(id);
                using var viewBottom = view.Bands.GetBottomBandItems();
                using var viewTop = view.Bands.GetTopBandItems();
                var actualBottom = ReadBandStyleIdSequence(viewBottom);
                var actualTop = ReadBandStyleIdSequence(viewTop);
                if (!expectedBottom.SequenceEqual(actualBottom, StringComparer.OrdinalIgnoreCase) ||
                    !expectedTop.SequenceEqual(actualTop, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Configured band-set style '{wanted}' failed exact view read-back.");
            }
            catch (Exception ex)
            {
                rec.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.BandStyleMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "סגנון הסרגלים שהוגדר לא חובר ואומת — כל אצוות החתכים בוטלה",
                    Message = ex.Message,
                    RecommendedAction =
                        "יש לייבא את ה-Band Set שהוגדר בפרופיל, להריץ PLAN מחדש ורק אז APPLY.",
                    AffectedRecordIds = { rec.RecordId },
                });

                throw new InvalidOperationException(
                    $"Configured SectionView band-set contract failed " +
                    $"({SectionFindingCodes.BandStyleMissing}); APPLY must roll back.", ex);
            }
        }

        private static List<string> ReadBandStyleIdSequence(System.Collections.IEnumerable items)
        {
            var ids = new List<string>();
            foreach (var item in items)
            {
                if (item == null)
                    throw new InvalidOperationException("Band-set read-back contains a null item.");
                var property = item.GetType().GetProperty("BandStyleId");
                if (property?.GetValue(item) is not ObjectId styleId || styleId.IsNull)
                    throw new InvalidOperationException("Band-set item has no readable BandStyleId.");
                ids.Add(styleId.Handle.ToString());
            }
            return ids;
        }

        /// <summary>
        /// Civil may attach the drawing's default bands during SectionView.Create.
        /// A blank project band style means an explicit single-datum presentation,
        /// so replace that default with a product-owned EMPTY band-set style.
        ///
        /// Do not mutate a SectionView's copied item collections.  Civil 3D 2027's
        /// native GraphBandSet.SetBottomBandItems dereferenced a null internal band
        /// in the 2026-08-31 acceptance run.  ImportBandSetStyle is the supported API
        /// for replacing a graph's complete band set; importing a proven-empty style
        /// avoids both native collection setters.  The style and the view are read
        /// back before returning.  Any exception or non-empty read-back is a hard
        /// error so the caller aborts the single APPLY transaction.
        /// </summary>
        internal static void ClearBandSet(
            Transaction tr,
            CivilDocument civilDoc,
            CivilDb.SectionView view,
            SectionApplyRecordResult rec)
        {
            try
            {
                if (!view.IsWriteEnabled)
                    throw new InvalidOperationException(
                        "SectionView must be open ForWrite before clearing its band set.");
                var styles = civilDoc.Styles.SectionViewBandSetStyles;
                var existed = styles.Contains(EmptyBandSetStyleName);
                var styleId = existed
                    ? styles[EmptyBandSetStyleName]
                    : styles.Add(EmptyBandSetStyleName);

                if (styleId.IsNull)
                    throw new InvalidOperationException(
                        $"Civil returned a null ObjectId for '{EmptyBandSetStyleName}'.");

                var style = tr.GetObject(
                        styleId, existed ? OpenMode.ForRead : OpenMode.ForWrite) as
                    CivilStyles.SectionViewBandSetStyle
                    ?? throw new InvalidOperationException(
                        $"'{EmptyBandSetStyleName}' is not a SectionViewBandSetStyle.");

                if (!existed)
                    style.Description = EmptyBandSetStyleDescription;
                if (!string.Equals(
                        style.Description, EmptyBandSetStyleDescription,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Band-set style name collision: '{EmptyBandSetStyleName}' is not Mahod-owned.");

                using (var bottom = style.GetBottomBandSetItems())
                using (var top = style.GetTopBandSetItems())
                {
                    if (bottom.Count != 0 || top.Count != 0)
                        throw new InvalidOperationException(
                            $"Owned empty band-set style '{EmptyBandSetStyleName}' was modified " +
                            $"(bottom={bottom.Count}, top={top.Count}).");
                }

                view.Bands.ImportBandSetStyle(styleId);
                AssertViewHasNoBands(view);
            }
            catch (Exception ex)
            {
                rec.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.BandStyleMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "סרגלי ברירת המחדל של Civil לא הוסרו — כל אצוות החתכים בוטלה",
                    Message = $"{ex.GetType().Name}: {ex.Message}",
                    RecommendedAction =
                        "אין לשמור או למסור חתכים עם Band מקורי. יש לבדוק את סגנון " +
                        $"'{EmptyBandSetStyleName}', להריץ PLAN מחדש ורק אז APPLY.",
                    AffectedRecordIds = { rec.RecordId },
                });

                throw new InvalidOperationException(
                    $"Managed SectionView still has an unproven native band contract " +
                    $"({SectionFindingCodes.BandStyleMissing}); APPLY must roll back.", ex);
            }
        }

        private static void AssertViewHasNoBands(CivilDb.SectionView view)
        {
            using var bottom = view.Bands.GetBottomBandItems();
            using var top = view.Bands.GetTopBandItems();
            if (bottom.Count != 0 || top.Count != 0)
                throw new InvalidOperationException(
                    $"Empty band-set import did not clear the SectionView " +
                    $"(bottom={bottom.Count}, top={top.Count}).");
        }

        /// <summary>
        /// Pins the view's elevation range to what the sections actually contain.
        /// The automatic range took whatever the widest sampled surface spans and drew
        /// 30-300 m tall views around a 5 m road (engineer feedback, 30/08).
        /// </summary>
        /// <summary>Hard ceiling on a section view's elevation span, metres.</summary>
        internal const double MaxViewSpanM = 40.0;

        private static string[] ExistingPatterns(IReadOnlyList<string> fromProfile) =>
            fromProfile is { Count: > 0 } ? fromProfile.ToArray() : DefaultExistingPatterns;

        internal static void ClampElevationRange(
            Transaction tr, CivilDb.SectionView view, CivilDb.SampleLine sampleLine,
            SectionApplyRecordResult rec, IReadOnlyList<string> profileExistingPatterns,
            SectionSourceSelectionLogic.Identity? explicitExisting,
            SectionPlanRecord record,
            IReadOnlyList<double> projectedUtilityElevations)
        {
            try
            {
                // The EXISTING-GROUND surface anchors the band; the one PLAN-selected
                // DESIGN surface is the only other surface allowed to widen it.
                // Unioning every source let one spiky surface stretch a 25 m road
                // section into a 335 m tower — the first clamp did exactly that,
                // faithfully (live, 30/08).
                var patterns = ExistingPatterns(profileExistingPatterns);
                var plannedSurfaces = record.PlannedSources
                    .Where(source => source.Required &&
                                     string.Equals(source.PlannedState, "sampled", StringComparison.Ordinal) &&
                                     string.Equals(source.SourceType, "surface", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (plannedSurfaces.Count != 2 ||
                    string.IsNullOrWhiteSpace(plannedSurfaces[1].SourceName) ||
                    string.IsNullOrWhiteSpace(plannedSurfaces[1].SourceHandle))
                    throw new InvalidOperationException(
                        $"Record '{record.RecordId}' does not carry one PLAN-selected design surface for the elevation band.");
                // PLAN writes the existing-ground source first and the design second.
                var plannedDesign = plannedSurfaces[1];
                double egMin = double.MaxValue, egMax = double.MinValue;
                double fgMin = double.MaxValue, fgMax = double.MinValue;
                var explicitExistingMatches = 0;
                var designMatches = 0;
                foreach (ObjectId secId in sampleLine.GetSectionIds())
                {
                    if (tr.GetObject(secId, OpenMode.ForRead) is not CivilDb.Section s)
                        throw new InvalidOperationException(
                            $"SampleLine returned unreadable Section child {secId.Handle} while clamping elevation range.");
                    // Corridor-surface Sections are surfaces too.
                    if (!SectionSourceService.IsSurfaceSection(s.SourceType)) continue;
                    var lo = s.MinmumElevation;
                    var hi = s.MaximumElevation;
                    if (!double.IsFinite(lo) || !double.IsFinite(hi) || hi <= lo ||
                        (Math.Abs(lo) < 0.01 && Math.Abs(hi) < 0.01))
                        throw new InvalidOperationException(
                            $"Sampled surface Section '{s.SourceName}' has no finite positive elevation range.");
                    if (explicitExisting != null
                        ? string.Equals(s.SourceName, explicitExisting.Name, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(s.SourceId.Handle.ToString(), explicitExisting.Handle, StringComparison.OrdinalIgnoreCase)
                        : patterns.Any(p => Wildcard(s.SourceName ?? "", p)))
                    {
                        if (explicitExisting != null) explicitExistingMatches++;
                        egMin = Math.Min(egMin, lo);
                        egMax = Math.Max(egMax, hi);
                        continue;
                    }
                    var identity = SectionSourceService.ResolveLiveSourceIdentityStrict(
                        tr, s.SourceId, s.SourceType, s.SourceName,
                        $"Surface Section child {s.Handle}");
                    if (string.Equals(identity.Name, plannedDesign.SourceName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(identity.Handle, plannedDesign.SourceHandle, StringComparison.OrdinalIgnoreCase))
                    {
                        designMatches++;
                        fgMin = Math.Min(fgMin, lo);
                        fgMax = Math.Max(fgMax, hi);
                    }
                }

                if (explicitExisting != null && explicitExistingMatches != 1)
                    throw new InvalidOperationException(
                        "Reviewed existing-ground source was not sampled exactly once by name and handle; no union fallback is permitted.");
                if (!(egMin < egMax))
                    throw new InvalidOperationException(
                        "No sampled existing-ground surface supplied a finite elevation range; an automatic managed view is not deliverable.");
                if (designMatches != 1 || !(fgMin <= fgMax))
                    throw new InvalidOperationException(
                        $"PLAN-selected design surface '{plannedDesign.SourceName}' was not sampled exactly once ({designMatches}); the elevation band cannot be proven.");

                // SEC-B3 (review of 1.3.9): the band is what the section draws — EG/FG,
                // every projected utility with a PROVEN elevation, and the arrow headroom
                // only for the strip kinds that will carry an arrow — plus one small
                // drafting margin. The fixed ±5 m and the stacked 2 m / ceil(bus)+1 m
                // padding produced a composition taller than the road is wide.
                var utilities = (projectedUtilityElevations ?? Array.Empty<double>())
                    .Where(double.IsFinite).ToList();
                var inputs = new SectionViewElevationBandLogic.Inputs(
                    egMin, egMax, fgMin, fgMax,
                    utilities.Count == 0 ? null : utilities.Min(),
                    utilities.Count == 0 ? null : utilities.Max(),
                    SectionViewElevationBandLogic.ArrowHeadroomFor(
                        record.TrafficDirections.Select(direction => direction.StripKind)));
                if (!SectionViewElevationBandLogic.TryCompute(inputs, MaxViewSpanM,
                        out var band, out var bandError) || band == null)
                    throw new InvalidOperationException(
                        "Managed SectionView elevation band could not be derived: " + bandError);

                var expectedMin = band.Min;
                var expectedMax = band.Max;
                rec.ElevationBandEvidence = band.Evidence;
                view.IsElevationRangeAutomatic = false;
                view.ElevationMin = expectedMin;
                view.ElevationMax = expectedMax;
                if (view.IsElevationRangeAutomatic ||
                    Math.Abs(view.ElevationMin - expectedMin) > 0.01 ||
                    Math.Abs(view.ElevationMax - expectedMax) > 0.01)
                    throw new InvalidOperationException(
                        $"SectionView elevation range read-back differs from {expectedMin:F2}..{expectedMax:F2}.");
            }
            catch (Exception ex)
            {
                rec.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.LayoutUnresolved,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Error,
                    Title = "טווח הרומים של החתך לא הוחל — ההחלה תבוטל ללא שינוי חלקי",
                    Message = ex.Message,
                    AffectedRecordIds = { rec.RecordId },
                });
                throw new InvalidOperationException(
                    $"Managed SectionView elevation-range application failed for record '{rec.RecordId}' (SEC-LAYOUT-UNRESOLVED).", ex);
            }
        }

        // -------------------------------------------------------------- helpers

        /// <summary>
        /// The sample line's LEFT end, by Civil's own word (SampleLineVertex.Side).
        /// This is what makes the projected offsets carry the correct sign on any
        /// machine — the drawn direction of the CL line proves nothing.
        /// </summary>
        private static P2? LeftEndpoint(CivilDb.SampleLine sampleLine)
        {
            P2? best = null;
            var bestIndex = -1;
            foreach (CivilDb.SampleLineVertex v in sampleLine.Vertices)
            {
                if (v.Side != CivilDb.SampleLineVertexSideType.Left) continue;
                if (v.OffsetIndex <= bestIndex) continue;
                var location = v.Location;
                if (!double.IsFinite(location.X) || !double.IsFinite(location.Y))
                    throw new InvalidOperationException(
                        "SampleLine left endpoint is non-finite.");
                bestIndex = v.OffsetIndex;
                best = new P2(location.X, location.Y);
            }
            return best;
        }

        private static Point3d? MapXY(
            CivilDb.SectionView view, double offset, double elevation) =>
            SectionAnnotationPlacementContract.Map(view, offset, elevation);

        private static T? OpenByHandle<T>(
            Transaction tr, Database db, string handleHex, OpenMode mode = OpenMode.ForWrite)
            where T : class
        {
            if (!long.TryParse(handleHex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var hv)) return null;
            if (!db.TryGetObjectId(new Handle(hv), out var id)) return null;
            try { return tr.GetObject(id, mode) as T; } catch { return null; }
        }

        /// <summary>
        /// Dark ACI colors (blue 5, brown 34) disappear on the black model background
        /// (engineer screenshot, 31/08). Markers of those systems get a brighter
        /// TrueColor that stays legible on white prints too; bright ACIs untouched.
        /// </summary>
        private static void Brighten(Entity ent, short aci)
        {
            var rgb = aci switch
            {
                5 => (R: (byte)0, G: (byte)128, B: (byte)255),    // מים/מקורות: azure
                34 => (R: (byte)210, G: (byte)140, B: (byte)60),  // ביוב: light brown
                92 => (R: (byte)0, G: (byte)200, B: (byte)130),   // קולחין: bright teal-green
                _ => default,
            };
            if (rgb == default) return;
            ent.Color = Autodesk.AutoCAD.Colors.Color.FromRgb(rgb.R, rgb.G, rgb.B);
            var readBack = ent.Color.ColorValue;
            if (readBack.R != rgb.R || readBack.G != rgb.G || readBack.B != rgb.B)
                throw new InvalidOperationException(
                    $"TrueColor read-back failed for annotation ACI {aci}: " +
                    $"expected {rgb.R},{rgb.G},{rgb.B}; actual {readBack.R},{readBack.G},{readBack.B}.");
        }

        /// <summary>
        /// A requested linetype must land on the entity. The former try/catch let a
        /// missing definition fall back to CONTINUOUS silently, so a dashed existing
        /// ground guide could ship as a solid line under a green APPLY.
        /// </summary>
        private static Line NewLine(Point3d from, Point3d to, short colorIndex, string linetype)
        {
            var db = HostApplicationServices.WorkingDatabase ??
                     throw new InvalidOperationException("No working database for section line.");
            var line = new Line(from, to)
            {
                ColorIndex = colorIndex,
                LinetypeScale = EffectiveAnnotationLinetypeScale(db),
                LineWeight = LineWeight.ByLineWeightDefault,
                Transparency = new Transparency(SectionAnnotationResourceContracts.OpaqueAlpha),
                Visible = true,
            };
            if (string.IsNullOrEmpty(linetype)) return line;
            try
            {
                line.Linetype = linetype;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"סוג הקו '{linetype}' לא הוחל על קו העזר: {ex.Message} " +
                    $"({SectionFindingCodes.AnnotationLinetypeNotApplied})", ex);
            }
            if (!SectionAnnotationResourceContracts.LinetypeApplied(linetype, line.Linetype) ||
                !double.IsFinite(line.LinetypeScale) || line.LinetypeScale <= 0)
                throw new InvalidOperationException(
                    $"סוג הקו '{linetype}' נדרש אך קו העזר קיבל '{line.Linetype}' " +
                    $"({SectionFindingCodes.AnnotationLinetypeNotApplied})");
            return line;
        }

        /// <summary>
        /// The visible dash length is pattern × LTSCALE × entity scale, and — with
        /// MSLTSCALE on — ÷ CANNOSCALEVALUE. 1/LTSCALE alone rendered the protected
        /// 0.50 m dash as 50 m at 1:100 (review of 1.2.28); the entity scale now cancels
        /// all three, and the state it was drawn against is recorded as evidence.
        /// </summary>
        private static double EffectiveAnnotationLinetypeScale(Database db) =>
            AnnotationLinetypeDisplayLogic.EntityScale(ReadLinetypeDisplayState(db));

        /// <summary>Live LTSCALE / MSLTSCALE / CANNOSCALE state (APPLY and VERIFY read through this).</summary>
        internal static AnnotationLinetypeDisplayLogic.DrawingState ReadLinetypeDisplayState(Database db)
        {
            var scale = db.Cannoscale;
            return new AnnotationLinetypeDisplayLogic.DrawingState(
                db.Ltscale,
                Convert.ToBoolean(db.MsLtScale),
                scale?.Name,
                scale?.PaperUnits ?? double.NaN,
                scale?.DrawingUnits ?? double.NaN,
                scale?.Scale ?? double.NaN);
        }

        /// <summary>Canonical evidence string of the display state; identical text in APPLY and VERIFY.</summary>
        internal static string LinetypeDisplayContract(Database db) =>
            AnnotationLinetypeDisplayLogic.Describe(ReadLinetypeDisplayState(db));

        private static DBText NewText(
            string text, Point3d position, double height, short colorIndex, double rotation, bool centered)
        {
            var t = new DBText
            {
                TextString = text,
                Position = position,
                Height = height,
                ColorIndex = colorIndex,
                Rotation = rotation,
                WidthFactor = 1.0,
                Oblique = 0.0,
                IsMirroredInX = false,
                IsMirroredInY = false,
                Normal = Vector3d.ZAxis,
                Thickness = 0.0,
                LinetypeScale = EffectiveAnnotationLinetypeScale(
                    HostApplicationServices.WorkingDatabase ??
                    throw new InvalidOperationException("No working database for section text.")),
                LineWeight = LineWeight.ByLineWeightDefault,
                Transparency = new Transparency(SectionAnnotationResourceContracts.OpaqueAlpha),
                Visible = true,
                Annotative = AnnotativeStates.False,
            };
            // TextStyleName is read-only on DBText; resolve the proven style id.
            // Hebrew rendered through a default SHX is not an acceptable fallback.
            var db = HostApplicationServices.WorkingDatabase ??
                     throw new InvalidOperationException("No working database for section text.");
            var tst = (TextStyleTable)db.TextStyleTableId.GetObject(OpenMode.ForRead);
            if (!tst.Has(AnnoTextStyle))
                throw new InvalidOperationException(
                    $"Required TrueType text style '{AnnoTextStyle}' is missing.");
            t.TextStyleId = tst[AnnoTextStyle];
            if (centered)
            {
                t.HorizontalMode = TextHorizontalMode.TextCenter;
                t.AlignmentPoint = position;
            }
            return t;
        }

        /// <summary>Unique retirement name for a protected record that is being replaced.</summary>
        private static string LegacyLinetypeName(LinetypeTable table, string name)
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var candidate = $"{name}-LEGACY-{stamp}";
            for (var i = 2; table.Has(candidate); i++)
                candidate = $"{name}-LEGACY-{stamp}-{i}";
            return candidate;
        }

        /// <summary>
        /// Protected Mahod linetypes are deterministic table records, not aliases for
        /// whatever a drawing happens to call DASHED2/CENTER. Selected scope may create
        /// a missing protected record but never rewrite an existing shared record; batch
        /// normalizes and then proves every dash/text/shape field by read-back.
        /// </summary>
        private static void EnsureLinetypes(Transaction tr, Database db, bool allowModify)
        {
            var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            foreach (var spec in SectionAnnotationResourceContracts.RequiredLinetypeSpecs)
            {
                LinetypeTableRecord rec;
                if (table.Has(spec.Name))
                {
                    rec = (LinetypeTableRecord)tr.GetObject(table[spec.Name], OpenMode.ForRead);
                    var state = ReadLinetypeState(rec);
                    var violations = SectionAnnotationResourceContracts.ValidateLinetype(spec, state);
                    // A record carrying the exact Mahod description marker is this tool's
                    // own resource, not an office definition: like the annotation registry
                    // it may be normalized in any scope. An office record that merely
                    // shares the name is still never modified in selected scope. Live 07/09
                    // 15:41 and 16:31: the record written at 11:21 came back from the saved
                    // DWG with an element that reads as text (persisted TEXT flag on a
                    // plain dash) and every selected APPLY on the reopened drawing was refused.
                    var mahodOwned = SectionAnnotationResourceContracts.IsToolOwnedLinetype(spec, state);
                    var action = SharedResourceLogic.Decide(true, violations.Count == 0,
                        allowModify || mahodOwned ? SharedResourceLogic.Mode.NormalizeAll
                                                  : SharedResourceLogic.Mode.CreateOnlyNeverModify);
                    if (action == SharedResourceLogic.Action.NoOp) continue;
                    if (action == SharedResourceLogic.Action.Block)
                        throw new InvalidOperationException(
                            SharedResourceBlocked("סוג קו", spec.Name) +
                            " [" + string.Join("; ", violations) + "]");
                    if (rec.IsDependent)
                        throw new InvalidOperationException(
                            $"Protected linetype '{spec.Name}' is XREF-dependent and cannot be normalized.");
                    // An in-place rewrite cannot clear a persisted TEXT element flag:
                    // live 07/09 16:55 the re-initialised record still read back
                    // shape=1 on its gap element. Keep the old definition under a
                    // legacy name — every entity that references it draws exactly as
                    // before — and give the contract name to a fresh record.
                    table.UpgradeOpen();
                    rec.UpgradeOpen();
                    rec.Name = LegacyLinetypeName(table, spec.Name);
                    rec = new LinetypeTableRecord { Name = spec.Name };
                    table.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                else
                {
                    table.UpgradeOpen();
                    rec = new LinetypeTableRecord { Name = spec.Name };
                    table.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }

                WriteLinetypeContract(rec, spec);
                var readBack = SectionAnnotationResourceContracts.ValidateLinetype(
                    spec, ReadLinetypeState(rec));
                if (readBack.Count > 0)
                    throw new InvalidOperationException(
                        $"Protected linetype '{spec.Name}' failed semantic read-back " +
                        $"[{string.Join("; ", readBack)}].");
            }
        }

        private static void WriteLinetypeContract(
            LinetypeTableRecord rec, SectionAnnotationResourceContracts.LinetypeSpec spec)
        {
            rec.AsciiDescription = spec.Description;
            rec.IsScaledToFit = false;
            // A linetype record has no annotative protocol extension: reading
            // Annotative yields NotApplicable, but SETTING it throws the native
            // eNotImplementedYet (live 06/09 16:46 — the first APPLY of a Ready section
            // created its sample line and view, then rolled back here).
            //
            // Plain dashes are written as plain dashes: only the lengths and the
            // native scale=1 that AutoCAD's own DASHED2 carries. Calling the text,
            // shape-style and shape-number setters with empty/null/zero values on a
            // plain dash persists it as a TEXT element (DXF 74 flag) — the record
            // written live on 07/09 11:21 came back after save/reload with a style
            // pointer on its gap element and every selected APPLY on the reopened
            // drawing was refused (07/09 15:41, sections-apply-selected-20260907-124142).
            // Re-initialise the element table: a record that already carries flagged
            // elements keeps them when the count does not change. CONTINUOUS has zero
            // dashes, so zero is a legal intermediate count.
            rec.NumDashes = 0;
            rec.NumDashes = spec.DashLengths.Count;
            for (var i = 0; i < spec.DashLengths.Count; i++)
            {
                rec.SetDashLengthAt(i, spec.DashLengths[i]);
                rec.SetShapeScaleAt(i, 1.0);
            }
            rec.PatternLength = spec.PatternLength;
        }

        internal static SectionAnnotationResourceContracts.LinetypeState ReadLinetypeState(
            LinetypeTableRecord rec)
        {
            var elements = new List<SectionAnnotationResourceContracts.LinetypeElementState>();
            for (var i = 0; i < rec.NumDashes; i++)
            {
                var offset = rec.ShapeOffsetAt(i);
                elements.Add(new SectionAnnotationResourceContracts.LinetypeElementState(
                    rec.DashLengthAt(i), rec.ShapeNumberAt(i),
                    // TextAtOrNull: TextAt throws eNotApplicable on a plain dash (Civil 3D 2026), and
                    // MHD-DASHED2 / MHD-CENTER are plain dashes read back on every APPLY.
                    !rec.ShapeStyleAt(i).IsNull, TextAtOrNull(rec, i),
                    offset.X, offset.Y, rec.ShapeScaleAt(i), rec.ShapeRotationAt(i),
                    rec.ShapeIsUcsOrientedAt(i), rec.ShapeIsUprightAt(i)));
            }
            return new SectionAnnotationResourceContracts.LinetypeState(
                true, rec.IsDependent, rec.AsciiDescription, rec.IsScaledToFit,
                rec.Annotative.ToString(), rec.PatternLength, elements);
        }

        /// <summary>Every missing or semantically drifted protected linetype (VERIFY reads through this too).</summary>
        /// <summary>A dash element with no text has no text to read: null, which IsEmbeddedShapeOrText already treats so.</summary>
        private static string? TextAtOrNull(LinetypeTableRecord rec, int index)
        {
            try
            {
                return rec.TextAt(index);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.NotApplicable)
            {
                return null;
            }
        }

        internal static IReadOnlyList<string> InvalidLinetypes(Transaction tr, Database db)
        {
            var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            var invalid = new List<string>();
            foreach (var spec in SectionAnnotationResourceContracts.RequiredLinetypeSpecs)
            {
                if (!table.Has(spec.Name))
                {
                    invalid.Add(spec.Name + ": missing");
                    continue;
                }
                try
                {
                    var rec = (LinetypeTableRecord)tr.GetObject(table[spec.Name], OpenMode.ForRead);
                    var violations = SectionAnnotationResourceContracts.ValidateLinetype(
                        spec, ReadLinetypeState(rec));
                    if (violations.Count > 0)
                        invalid.Add(spec.Name + ": " + string.Join("; ", violations));
                }
                catch (Exception ex)
                {
                    invalid.Add(spec.Name + ": unreadable: " + ex.Message);
                }
            }

            return invalid;
        }

        /// <summary>Live layer contract state, shared by APPLY (normalize/validate) and VERIFY.</summary>
        internal static SectionAnnotationResourceContracts.LayerState ReadLayerState(
            Transaction tr, Database db, LayerTableRecord rec)
        {
            var color = rec.Color;
            var colorIndex = color.ColorMethod == ColorMethod.ByAci ? color.ColorIndex : (short)-1;
            var transparency = rec.Transparency;
            var linetype = tr.GetObject(rec.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
            var (viewportScanComplete, frozenPaperViewportCount) =
                InspectPaperViewportVisibility(tr, db, rec.ObjectId);
            return new SectionAnnotationResourceContracts.LayerState(
                true, rec.IsDependent, rec.IsOff, rec.IsFrozen, rec.IsPlottable, rec.IsLocked,
                rec.IsHidden, rec.ViewportVisibilityDefault, rec.HasOverrides,
                viewportScanComplete, frozenPaperViewportCount,
                colorIndex, transparency.IsByAlpha, transparency.Alpha,
                linetype?.Name, rec.LineWeight.ToString(), rec.Annotative.ToString());
        }

        private static (bool Complete, int FrozenCount) InspectPaperViewportVisibility(
            Transaction tr, Database db, ObjectId layerId)
        {
            var count = 0;
            try
            {
                foreach (var viewport in PaperViewports(tr, db))
                    if (viewport.IsLayerFrozenInViewport(layerId)) count++;
                return (true, count);
            }
            catch
            {
                return (false, count);
            }
        }

        private static IEnumerable<Viewport> PaperViewports(Transaction tr, Database db)
        {
            var viewportClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Viewport));
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId spaceId in table)
            {
                var space = tr.GetObject(spaceId, OpenMode.ForRead) as BlockTableRecord;
                if (space == null || !space.IsLayout ||
                    string.Equals(space.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (ObjectId id in space)
                {
                    if (id.IsNull || id.IsErased || !id.ObjectClass.IsDerivedFrom(viewportClass)) continue;
                    if (tr.GetObject(id, OpenMode.ForRead, openErased: false) is Viewport viewport &&
                        viewport.Number > 1 && viewport.On)
                        yield return viewport;
                }
            }
        }

        /// <summary>Live text-style contract state, shared by APPLY (normalize/validate) and VERIFY.</summary>
        internal static SectionAnnotationResourceContracts.TextStyleState ReadTextStyleState(TextStyleTableRecord rec)
        {
            var font = rec.Font;
            return new(true, font.TypeFace, rec.IsShapeFile, rec.BigFontFileName,
                rec.XScale, rec.ObliquingAngle, rec.IsVertical, rec.FlagBits,
                rec.TextSize, font.Bold, font.Italic, font.CharacterSet, font.PitchAndFamily,
                rec.Annotative.ToString(), rec.PaperOrientation.ToString());
        }

        private static string SharedResourceBlocked(string kind, string name) =>
            $"{kind} '{name}' קיים בשרטוט ואינו תואם את החוזה — החלת חתך נבחר אינה משנה משאבים " +
            "משותפים שחתכים אחרים מפנים אליהם; יש להריץ החלת אצווה שתנרמל אותו " +
            $"({SectionFindingCodes.SharedResourceChangeRequired})";

        private static void EnsureLayer(Transaction tr, Database db, string name, bool allowModify, StageLog? log)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord rec;
            if (lt.Has(name))
            {
                rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                if (rec.IsDependent)
                    throw new InvalidOperationException(
                        $"Reserved annotation layer '{name}' is XREF-dependent and cannot be normalized.");
                // Full contract, not "exists": a dark-blue or 90%-transparent layer hides
                // every ByLayer block reference while a visibility-only check stays green.
                var violations = SectionAnnotationResourceContracts.ValidateLayer(
                    ReadLayerState(tr, db, rec));
                var compliant = violations.Count == 0;
                var action = SharedResourceLogic.Decide(true, compliant,
                    allowModify ? SharedResourceLogic.Mode.NormalizeAll
                                : SharedResourceLogic.Mode.CreateOnlyNeverModify);
                if (action == SharedResourceLogic.Action.NoOp) return;
                if (action == SharedResourceLogic.Action.Block)
                    throw new InvalidOperationException(
                        SharedResourceBlocked("שכבה", name) + " [" + string.Join("; ", violations) + "]");
                rec.UpgradeOpen();
            }
            else
            {
                lt.UpgradeOpen();
                rec = new LayerTableRecord { Name = name };
                lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }

            rec.IsOff = false;
            rec.IsFrozen = false;
            rec.IsPlottable = true;
            rec.IsLocked = false;
            rec.IsHidden = false;
            rec.ViewportVisibilityDefault = false;
            if (rec.HasOverrides) rec.RemoveAllOverrides();
            rec.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci,
                SectionAnnotationResourceContracts.AnnotationLayerColorIndex);
            rec.Transparency = new Transparency(SectionAnnotationResourceContracts.OpaqueAlpha);
            rec.LinetypeObjectId = db.ContinuousLinetype;
            rec.LineWeight = LineWeight.ByLineWeightDefault;
            // Layers are never annotative and carry no annotative protocol extension:
            // the Annotative setter throws eNotImplementedYet (live 06/09 16:46). The
            // contract still rejects a layer that somehow reads Annotative == True.
            var thaw = new ObjectIdCollection(new[] { rec.ObjectId });
            foreach (var viewport in PaperViewports(tr, db).Where(v =>
                         v.IsLayerFrozenInViewport(rec.ObjectId)))
            {
                // A write outside model space: say exactly which sheet viewport changed.
                log?.Info($"apply.annotation_layer.thaw_viewport layer={name} viewport={viewport.Handle}");
                viewport.UpgradeOpen();
                viewport.ThawLayersInViewport(thaw.GetEnumerator());
            }
            var readBack = SectionAnnotationResourceContracts.ValidateLayer(
                ReadLayerState(tr, db, rec));
            if (readBack.Count > 0)
                throw new InvalidOperationException(
                    $"Reserved annotation layer '{name}' failed on/unfrozen/plottable/unlocked read-back " +
                    $"[{string.Join("; ", readBack)}].");
        }

        /// <summary>
        /// Hebrew labels need a TrueType font: the drawing's default SHX styles render
        /// Hebrew as question marks. Arial ships with Windows.
        /// </summary>
        private static void EnsureTextStyle(Transaction tr, Database db, bool allowModify)
        {
            var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            TextStyleTableRecord rec;
            if (tst.Has(AnnoTextStyle))
            {
                rec = (TextStyleTableRecord)tr.GetObject(
                    tst[AnnoTextStyle], OpenMode.ForRead);
                // Full contract: Arial alone is not enough — a 0.01 width factor, an
                // oblique angle, vertical or mirrored flags all render the labels unreadable.
                var violations = SectionAnnotationResourceContracts.ValidateTextStyle(ReadTextStyleState(rec));
                var compliant = violations.Count == 0;
                var action = SharedResourceLogic.Decide(true, compliant,
                    allowModify ? SharedResourceLogic.Mode.NormalizeAll
                                : SharedResourceLogic.Mode.CreateOnlyNeverModify);
                if (action == SharedResourceLogic.Action.NoOp) return;
                if (action == SharedResourceLogic.Action.Block)
                    throw new InvalidOperationException(
                        SharedResourceBlocked("סגנון טקסט", AnnoTextStyle) + " [" + string.Join("; ", violations) + "]");
                rec.UpgradeOpen();
            }
            else
            {
                tst.UpgradeOpen();
                rec = new TextStyleTableRecord { Name = AnnoTextStyle };
                tst.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }

            rec.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                AnnoTypeface, false, false, 0, 0);
            rec.BigFontFileName = string.Empty;
            rec.XScale = 1.0;
            rec.ObliquingAngle = 0.0;
            rec.IsVertical = false;
            rec.FlagBits = 0;
            rec.TextSize = 0.0;
            rec.Annotative = AnnotativeStates.False;
            var font = rec.Font;
            var readBack = SectionAnnotationResourceContracts.ValidateTextStyle(ReadTextStyleState(rec));
            if (readBack.Count > 0 || rec.IsShapeFile ||
                !string.Equals(font.TypeFace, AnnoTypeface, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Required text style '{AnnoTextStyle}' failed Arial TrueType read-back " +
                    $"[{string.Join("; ", readBack)}].");
        }
    }
}
