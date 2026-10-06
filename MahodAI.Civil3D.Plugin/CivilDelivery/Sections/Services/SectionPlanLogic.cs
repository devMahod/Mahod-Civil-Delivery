using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Pure (Civil-independent) planning rules: alignment-candidate classification,
    /// station/skew/extent derivation, numbering validation, rerun action decision.
    /// The Civil-side resolver feeds real geometry; unit tests feed synthetic data.
    /// The locked plan's no-silent-selection contract (§7.3) lives HERE so both the
    /// direct command and the AI route share exactly one implementation.
    /// </summary>
    public static class SectionPlanLogic
    {
        public const string Domain = "sections";
        internal const double CrossingDecisionStationToleranceM = 0.001;

        /// <summary>
        /// Classifies candidate crossings for one CL record and, on the unambiguous
        /// path, derives station/skew/extents from the CL geometry (§7.3, §7.4).
        /// </summary>
        public static void ResolveAlignment(
            SectionPlanRecord record,
            IReadOnlyList<AlignmentCrossing> crossings,
            ProjectProfile profile)
        {
            record.CandidateCrossings.Clear();
            record.CandidateCrossings.AddRange(crossings);

            var cl = record.Cl;
            var a = new Pt2(cl.WcsEndpoints[0], cl.WcsEndpoints[1]);
            var b = new Pt2(cl.WcsEndpoints[2], cl.WcsEndpoints[3]);

            if (a.DistanceTo(b) < 0.001)
            {
                Fail(record, SectionFindingCodes.ClDegenerate, FindingSeverity.Error,
                    "CL geometry is degenerate (zero length)");
                return;
            }

            // A durable crossing decision identifies the exact candidate by BOTH
            // alignment and station.  Alignment-only approval cannot disambiguate a
            // loop that crosses the same CL more than once.
            var exactDecisions = profile.Sections.Decisions.Crossings
                .Where(d => SameSource(cl, d.SourceDrawingHash, d.SourceHandle))
                .ToList();
            if (exactDecisions.Count > 0)
            {
                if (exactDecisions.Count != 1 || !IsApprovedCrossingDecision(exactDecisions[0]))
                {
                    Review(record, SectionFindingCodes.AlignmentAmbiguous, FindingSeverity.ReviewRequired,
                        "הכרעת החצייה השמורה אינה מלאה או שאינה יחידה",
                        "נדרשים תוואי+תחנה מדויקים, מאשר וחותמת זמן; יש לאשר מחדש מהטבלה.");
                    return;
                }

                var decision = exactDecisions[0];
                var exact = crossings.Where(c =>
                        string.Equals(c.AlignmentName, decision.AlignmentName,
                            StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(c.Station - decision.Station!.Value) <= CrossingDecisionStationToleranceM)
                    .ToList();
                if (exact.Count != 1)
                {
                    Review(record, SectionFindingCodes.AlignmentAmbiguous, FindingSeverity.ReviewRequired,
                        $"החצייה שאושרה ({Bidi.Ltr(decision.AlignmentName!)} @ {decision.Station:F3}) " +
                        "אינה קיימת עוד כמועמד יחיד בגאומטריה הנוכחית",
                        "קובץ המקור או התוואי השתנו; יש לבחור מחדש מועמד מדויק.");
                    return;
                }

                SelectCrossing(record, exact[0], a, b, profile);
                return;
            }

            // Legacy explicit user-confirmed alignment mapping still wins over
            // geometry-count rules, but cannot select one of multiple stations.
            var explicitMap = profile.Sections.Alignments.ExplicitSourceToAlignment;
            var mappedName = explicitMap.TryGetValue(cl.SourceHandle, out var m) ? m : null;

            var byAlignment = crossings.GroupBy(c => c.AlignmentName).ToList();

            if (crossings.Count == 0)
            {
                Review(record, SectionFindingCodes.ClNoIntersection, FindingSeverity.ReviewRequired,
                    "קו ה-CL אינו חוצה אף תוואי מותר",
                    $"tolerance={profile.Sections.Cl.IntersectionToleranceM?.ToString() ?? "exact"}");
                return;
            }

            if (mappedName != null)
            {
                var mapped = byAlignment.FirstOrDefault(g =>
                    string.Equals(g.Key, mappedName, StringComparison.OrdinalIgnoreCase));
                if (mapped == null)
                {
                    Review(record, SectionFindingCodes.AlignmentAmbiguous, FindingSeverity.ReviewRequired,
                        $"הפרופיל משייך את קו ה-CL לתוואי '{Bidi.Ltr(mappedName)}' אך הקו אינו חוצה אותו");
                    return;
                }
                SelectCrossingGroup(record, mapped.ToList(), a, b, profile);
                return;
            }

            if (byAlignment.Count > 1)
            {
                Review(record, SectionFindingCodes.AlignmentAmbiguous, FindingSeverity.ReviewRequired,
                    $"קו ה-CL חוצה {byAlignment.Count} תוואים אפשריים: " +
                    string.Join(", ", byAlignment.Select(g => g.Key)),
                    "יש לבחור את התוואי המיועד; אפשר לקבע את הבחירה בפרופיל הפרויקט.");
                return;
            }

            SelectCrossingGroup(record, byAlignment[0].ToList(), a, b, profile);
        }

        private static void SelectCrossingGroup(
            SectionPlanRecord record, List<AlignmentCrossing> group, Pt2 a, Pt2 b, ProjectProfile profile)
        {
            if (group.Count > 1)
            {
                Review(record, SectionFindingCodes.ClMultipleIntersections, FindingSeverity.ReviewRequired,
                    $"קו ה-CL חוצה את התוואי '{Bidi.Ltr(group[0].AlignmentName)}' {group.Count} פעמים " +
                    $"(תחנות {string.Join(", ", group.Select(c => c.Station.ToString("F2")))})",
                    "יש לבחור את תחנת החצייה המיועדת.");
                return;
            }

            SelectCrossing(record, group[0], a, b, profile);
        }

        private static void SelectCrossing(
            SectionPlanRecord record, AlignmentCrossing crossing, Pt2 a, Pt2 b, ProjectProfile profile)
        {
            record.SelectedAlignment = crossing.AlignmentName;
            record.SelectedCrossing = crossing;
            record.Station = crossing.Station;

            // 1.4.1 (984): an engineer-chosen "station markers" mode turns each short tick into a section of the
            // approved half-width; the ordinary mode only warns about a short line. Never inferred from length.
            var drawnLength = a.DistanceTo(b);
            var cl = profile.Sections.Cl;
            if (SectionStationMarkerLogic.IsActive(cl))
            {
                if (!SectionStationMarkerLogic.TryExtend(a, b, new Pt2(crossing.Point[0], crossing.Point[1]),
                        cl.StationMarkerHalfWidthM, out var extendedA, out var extendedB, out var markerReason))
                {
                    Review(record, SectionFindingCodes.ClDegenerate, FindingSeverity.ReviewRequired,
                        markerReason!, "יש להזין חצי-רוחב חיובי בהגדרת הפרויקט, או לבחור קו חתך תקין.");
                    return;
                }
                a = extendedA;
                b = extendedB;
                // Same in-place update as the max-half-width cap below: every stored offset describes these endpoints.
                record.Cl.WcsEndpoints[0] = a.X;
                record.Cl.WcsEndpoints[1] = a.Y;
                record.Cl.WcsEndpoints[2] = b.X;
                record.Cl.WcsEndpoints[3] = b.Y;
                record.Findings.Add(SectionStationMarkerLogic.ExtendedFinding(
                    record.RecordId, drawnLength, cl.StationMarkerHalfWidthM!.Value, cl.StationMarkerApprovedBy));
            }
            else if (drawnLength < SectionStationMarkerLogic.ShortLineWarningM)
            {
                record.Findings.Add(SectionStationMarkerLogic.ShortLineWarning(record.RecordId, drawnLength));
            }

            var clDir = SectionMath.DirectionDeg(a, b);
            record.SkewDeg = Math.Round(SectionMath.SkewFromNormalDeg(clDir, crossing.TangentDeg), 4);

            var x = new Pt2(crossing.Point[0], crossing.Point[1]);
            var (oa, ob, left, right) = SectionMath.ExtentsFromEndpoints(a, b, x, crossing.TangentDeg);
            record.EndpointOffsetA = Math.Round(oa, 4);
            record.EndpointOffsetB = Math.Round(ob, 4);
            record.LeftExtent = Math.Round(left, 4);
            record.RightExtent = Math.Round(right, 4);

            if (left < 0.01 || right < 0.01)
            {
                // Both CL endpoints on one side of the alignment: the "crossing" grazes
                // an endpoint. The swath cannot be derived — engineering review.
                Review(record, SectionFindingCodes.ClNoIntersection, FindingSeverity.ReviewRequired,
                    "קצוות קו ה-CL אינם משני צדי התוואי בנקודת החצייה; רוחב החתך לא מוגדר");
                return;
            }

            // A CL drawn 88 m long produces an 88 m section that dwarfs the road
            // (engineer feedback, 2026-08-30). The drawn line keeps deciding the
            // DIRECTION; the profile's cap limits the reach, symmetrically honest:
            // trimmed extents, trimmed endpoints, and a note saying so.
            if (profile.Sections.Projection.MaxHalfWidthM is { } maxHalf && maxHalf > 0)
            {
                var capped = SectionProjectionLogic.CapEndpoints(
                    new SectionProjectionLogic.P2(x.X, x.Y),
                    new SectionProjectionLogic.P2(a.X, a.Y),
                    new SectionProjectionLogic.P2(b.X, b.Y),
                    maxHalf);
                if (capped.Trimmed)
                {
                    record.Cl.WcsEndpoints[0] = capped.Ax;
                    record.Cl.WcsEndpoints[1] = capped.Ay;
                    record.Cl.WcsEndpoints[2] = capped.Bx;
                    record.Cl.WcsEndpoints[3] = capped.By;
                    // CapEndpoints limits distance along the actual CL. On a
                    // skewed cut this is not the same as alignment-perpendicular
                    // offset. Every stored offset must describe the NEW endpoints;
                    // otherwise lane-target interpolation points at the wrong WCS
                    // location and PLAN measures a larger swath than it samples.
                    var (newOa, newOb, newLeft, newRight) = SectionMath.ExtentsFromEndpoints(
                        new Pt2(capped.Ax, capped.Ay), new Pt2(capped.Bx, capped.By),
                        x, crossing.TangentDeg);
                    record.EndpointOffsetA = Math.Round(newOa, 4);
                    record.EndpointOffsetB = Math.Round(newOb, 4);
                    record.LeftExtent = Math.Round(newLeft, 4);
                    record.RightExtent = Math.Round(newRight, 4);
                    record.Findings.Add(new DeliveryFinding
                    {
                        Code = SectionFindingCodes.LayoutUnresolved,
                        Domain = Domain,
                        Severity = FindingSeverity.Info,
                        Title = $"רוחב החתך הוגבל ל-{maxHalf:F0} מ' לכל צד (הקו המקורי: " +
                                $"{left:F1} מ' שמאלה, {right:F1} מ' ימינה)",
                        Message = "max_half_width_m in the project profile caps the section reach.",
                        AffectedRecordIds = { record.RecordId },
                    });
                }
            }

            if (!SectionCutGeometry.TryFrame(record, out _))
            {
                Review(record, SectionFindingCodes.ClNoIntersection, FindingSeverity.ReviewRequired,
                    "גאומטריית קו ה-CL והחצייה אינן מגדירות מסגרת חתך ישרה ואמינה",
                    "The selected crossing must lie on the actual retained CL and define a left and right side.");
                return;
            }
            record.Status = DeliveryStatus.Ready;
        }

        /// <summary>
        /// Applies a durable explicit CL exclusion only after the current PLAN has
        /// independently reproduced the exact finding being excluded. A stale
        /// decision may never skip the resolver/source/style/presentation checks.
        /// </summary>
        public static bool TryApplyExplicitExclusion(
            SectionPlanRecord record, ProjectProfile profile)
        {
            var matches = profile.Sections.Decisions.Exclusions
                .Where(d => SameSource(record.Cl, d.SourceDrawingHash, d.SourceHandle))
                .ToList();
            if (matches.Count == 0) return false;

            if (matches.Count != 1 || !IsApprovedExclusionDecision(matches[0]))
            {
                Review(record, SectionFindingCodes.IncompleteBatch, FindingSeverity.ReviewRequired,
                    "החרגת ה-CL השמורה אינה מלאה או שאינה יחידה",
                    "החרגה מחייבת קוד ממצא, סיבה, מאשר וחותמת זמן; יש לאשר מחדש מהטבלה.");
                return false;
            }

            var exclusion = matches[0];
            var reproduced = record.Findings.FirstOrDefault(f =>
                string.Equals(f.Code, exclusion.FindingCode, StringComparison.Ordinal) &&
                IsExcludableFinding(f));
            if (reproduced == null)
            {
                Review(record, SectionFindingCodes.ExclusionStale, FindingSeverity.ReviewRequired,
                    $"החרגה שמורה עבור '{exclusion.FindingCode}' אינה תואמת ממצא פעיל ב-PLAN הנוכחי",
                    "אין לדלג על בדיקות בגלל החרגה היסטורית; יש להסיר/לעדכן את ההכרעה לאחר בדיקת המצב הנוכחי.");
                return false;
            }

            record.SectionId = !string.IsNullOrWhiteSpace(record.Cl.CandidateSectionNumber)
                ? record.Cl.CandidateSectionNumber
                : $"CL-{record.Cl.SourceHandle}";
            record.ExplicitExclusion = new SectionExclusionPlan
            {
                FindingCode = exclusion.FindingCode!,
                Reason = exclusion.Reason!,
                ApprovedBy = exclusion.ApprovedBy!,
                ApprovedAtUtc = exclusion.ApprovedAtUtc!.Value,
            };
            record.Action = PlanAction.Excluded;
            record.Status = DeliveryStatus.Ready;
            return true;
        }

        public static bool IsApprovedCrossingDecision(
            ProjectProfile.SectionsProfile.DecisionsProfile.CrossingDecision decision) =>
            !string.IsNullOrWhiteSpace(decision.SourceDrawingHash) &&
            !string.IsNullOrWhiteSpace(decision.SourceHandle) &&
            !string.IsNullOrWhiteSpace(decision.AlignmentName) &&
            decision.Station is { } station && double.IsFinite(station) &&
            !string.IsNullOrWhiteSpace(decision.ApprovedBy) &&
            decision.ApprovedAtUtc is not null;

        public static bool IsApprovedExclusionDecision(
            ProjectProfile.SectionsProfile.DecisionsProfile.ExclusionDecision decision) =>
            !string.IsNullOrWhiteSpace(decision.SourceDrawingHash) &&
            !string.IsNullOrWhiteSpace(decision.SourceHandle) &&
            !string.IsNullOrWhiteSpace(decision.FindingCode) &&
            !string.IsNullOrWhiteSpace(decision.Reason) &&
            !string.IsNullOrWhiteSpace(decision.ApprovedBy) &&
            decision.ApprovedAtUtc is not null;

        public static bool HasValidExplicitExclusion(SectionPlanRecord record) =>
            record.Action == PlanAction.Excluded &&
            record.ExplicitExclusion is { } x &&
            !string.IsNullOrWhiteSpace(x.FindingCode) &&
            !string.IsNullOrWhiteSpace(x.Reason) &&
            !string.IsNullOrWhiteSpace(x.ApprovedBy) &&
            x.ApprovedAtUtc != default;

        /// <summary>
        /// Only engineering scope/geometry findings may justify omitting a CL. Style,
        /// layout, source-integrity, ROW and traffic warnings must be fixed at their
        /// own gate and can never disappear behind the generic exclusion dialog.
        /// </summary>
        public static bool IsExcludableFinding(DeliveryFinding finding) =>
            finding.Severity is FindingSeverity.ReviewRequired or FindingSeverity.Error &&
            finding.Code is SectionFindingCodes.ClNoIntersection or
                SectionFindingCodes.AlignmentAmbiguous or
                SectionFindingCodes.ClMultipleIntersections or
                SectionFindingCodes.ClDegenerate or
                SectionFindingCodes.ClStationLabelMismatch or
                SectionFindingCodes.PresentationCoverageMissing or
                SectionFindingCodes.PlanMarksMissing or
                SectionFindingCodes.ManualSectionAmbiguous or
                SectionFindingCodes.ManualSectionGeometryMismatch or
                SectionFindingCodes.ManualSectionPresentationMismatch;

        /// <summary>Every discovered CL must be actionable/unchanged or explicitly excluded.</summary>
        public static List<SectionPlanRecord> UnresolvedBatchRecords(SectionPlan plan) =>
            plan.Records.Where(record =>
            {
                if (record.Action == PlanAction.Excluded)
                    return !HasValidExplicitExclusion(record);
                return record.Status != DeliveryStatus.Ready ||
                       record.Action is not (PlanAction.Create or PlanAction.Update or
                           PlanAction.Replace or PlanAction.Unchanged);
            }).ToList();

        /// <summary>
        /// Coverage policy for VERIFY. A committed flag is not proof that every PLAN
        /// row participated; each row must have applied evidence, unchanged evidence,
        /// or a complete explicit exclusion.
        /// </summary>
        public static List<SectionPlanRecord> UnresolvedVerificationRecords(
            SectionPlan plan, SectionApplyResult applied) =>
            plan.Records.Where(record =>
            {
                if (HasValidExplicitExclusion(record)) return false;
                var evidence = applied.Records.FirstOrDefault(r => r.RecordId == record.RecordId);
                return evidence?.Status != DeliveryStatus.Applied;
            }).ToList();

        private static bool SameSource(
            ClSourceRecord cl, string? sourceDrawingHash, string? sourceHandle) =>
            string.Equals(cl.SourceDrawingHash, sourceDrawingHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cl.SourceHandle, sourceHandle, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// §7.10: if explicit label text and geometry disagree beyond the approved
        /// tolerance → SEC-CL-STATION-LABEL-MISMATCH / REVIEW_REQUIRED. Numbering is
        /// validated only when the profile enables it; `1086 == 1+086` stays a
        /// hypothesis otherwise.
        /// </summary>
        public static void ValidateNumbering(SectionPlanRecord record, ProjectProfile profile)
        {
            var numbering = profile.Sections.Cl.Numbering;
            // Section identity, in evidence order:
            //   1. an explicit label the project actually drew next to the CL;
            //   2. otherwise a neutral station-derived id.
            // The station is ALWAYS kept separately, so "1086" is never quietly
            // treated as station 1+086 — that convention was never confirmed for this
            // project and is not assumed anywhere.
            record.SectionId = !string.IsNullOrWhiteSpace(record.Cl.CandidateSectionNumber)
                ? record.Cl.CandidateSectionNumber
                : record.Station is { } st
                    ? $"STA-{st:F0}"
                    : $"CL-{record.Cl.SourceHandle}";

            if (!numbering.StationValidationEnabled) return;
            if (record.Station is null || string.IsNullOrWhiteSpace(record.Cl.CandidateSectionNumber)) return;

            var digits = new string(record.Cl.CandidateSectionNumber.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return;
            if (!double.TryParse(digits, out var labelStation)) return;

            var tol = numbering.StationValidationToleranceM ?? 1.0;
            if (Math.Abs(labelStation - record.Station.Value) > tol)
            {
                Review(record, SectionFindingCodes.ClStationLabelMismatch, FindingSeverity.ReviewRequired,
                    $"תווית החתך '{record.Cl.CandidateSectionNumber}' נקראת כתחנה {labelStation:F0} " +
                    $"אך הגיאומטריה נותנת {record.Station:F2} (סבילות {tol} מ')");
            }
        }

        /// <summary>
        /// Utility accounting for one section (directive §16). Nataly's requirement is
        /// that existing systems appear in the sections, so this never returns silence:
        /// a utility present in the drawing but absent from the plan is reported as
        /// NOT_CONFIGURED, and one that cannot be represented is reported with its
        /// reason. Configuration remains the engineer's decision — this only makes the
        /// gap visible.
        /// </summary>
        public static void BuildUtilityCoverage(
            SectionPlanRecord record,
            IReadOnlyList<DiscoveredUtility> discovered,
            ProjectProfile profile)
        {
            var coverage = record.UtilityCoverage;
            coverage.Relevant.Clear();
            coverage.Represented.Clear();
            coverage.NotConfigured.Clear();
            coverage.Missing.Clear();
            coverage.Unsupported.Clear();

            coverage.Relevant.AddRange(discovered);

            var plannedUtilityTypes = new[]
            {
                "pipe-network", "pressure-network", "feature-line", "polyline-3d", "block",
            };
            var plannedUtilities = record.PlannedSources
                .Where(s => plannedUtilityTypes.Contains(s.SourceType, StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var planned in plannedUtilities)
            {
                var match = discovered.FirstOrDefault(d =>
                    ClInstructionReader.WildcardMatch(d.Name, planned.SourceName));

                if (match == null)
                {
                    // Configured (often required) but the drawing does not contain it.
                    coverage.Missing.Add(planned.SourceName);
                    continue;
                }

                if (!match.NativelySampleable && planned.PlannedState != "adapter")
                {
                    coverage.Unsupported[match.Name] =
                        $"{match.Kind} has no native Section Source path and no adapter is planned";
                    continue;
                }

                coverage.Represented.Add(match.Name);
            }

            foreach (var d in discovered)
            {
                if (coverage.Represented.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (coverage.Unsupported.ContainsKey(d.Name)) continue;

                if (d.NativelySampleable)
                    coverage.NotConfigured.Add(d.Name);
                else
                    coverage.Unsupported[d.Name] =
                        $"{d.Kind} cannot be sampled natively; a utility adapter is required";
            }

            coverage.Summary =
                $"{coverage.Represented.Count} represented, {coverage.NotConfigured.Count} present but not configured, " +
                $"{coverage.Missing.Count} configured but absent, {coverage.Unsupported.Count} unsupported";

            // --- findings ---------------------------------------------------
            foreach (var missing in coverage.Missing)
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceMissing,
                    Domain = Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"מקור המערכת '{Bidi.Ltr(missing)}' שהוגדר אינו קיים בשרטוט הזה",
                    RecommendedAction = "יש לצרף את השרטוט שמכיל אותו, או להסיר אותו מפרופיל הפרויקט.",
                    AffectedRecordIds = { record.RecordId },
                });
                record.Status = DeliveryStatus.ReviewRequired;
            }

            if (coverage.NotConfigured.Count > 0)
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.UtilityUnsupported,
                    Domain = Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"{coverage.NotConfigured.Count} מקורות מערכות קיימים בשרטוט אך לא הוגדרו " +
                            "כמקורות לחתך — החתכים לא יכללו אותם",
                    Message = string.Join(", ", coverage.NotConfigured),
                    RecommendedAction = "יש להריץ הגדרת פרויקט (" + Commands.CivilDeliveryCommandNames.Setup + ") ולבחור את מקורות המערכות שצריכים להופיע בחתכים.",
                    AffectedRecordIds = { record.RecordId },
                });
                record.Status = DeliveryStatus.ReviewRequired;
            }

            foreach (var (name, reason) in coverage.Unsupported)
            {
                record.Findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.UtilityUnsupported,
                    Domain = Domain,
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"המערכת '{Bidi.Ltr(name)}' עדיין לא ניתנת לייצוג בחתך",
                    Message = reason,
                    AffectedRecordIds = { record.RecordId },
                });
                record.Status = DeliveryStatus.ReviewRequired;
            }
        }

        /// <summary>
        /// One reusable scope gate for direct APPLY and every AI route that consumes a
        /// session-global plan. Civil can switch active documents while the process and
        /// tool session remain alive; a profile workflow can likewise replace the
        /// current profile without replacing LastPlan.
        /// </summary>
        public static string? ScopeStaleReason(
            SectionPlan plan,
            string? currentDrawing,
            ProjectProfile currentProfile,
            string? currentProfileHash)
        {
            if (!string.Equals(plan.ProjectProfileId, currentProfile.ProfileId,
                    StringComparison.Ordinal))
                return "פרופיל הפרויקט השתנה מאז התכנון — יש להריץ תכנון מחדש";
            if (string.IsNullOrWhiteSpace(plan.ProjectProfileHash) ||
                string.IsNullOrWhiteSpace(currentProfileHash))
                return "לא ניתן לאמת את גרסת פרופיל הפרויקט — יש להריץ תכנון מחדש";

            return ResultScope.For(plan.SourceDrawing, plan.ProjectProfileHash)
                .StaleReason(currentDrawing, currentProfileHash);
        }

        /// <summary>
        /// Exact native source set for a sample-line group. Civil sampling is a group
        /// property, so one record must not disable a source required by another sample
        /// line on the same alignment. The union is still exact and alignment-scoped.
        /// </summary>
        public sealed record GroupSamplingPlan(
            IReadOnlyList<string> Names,
            IReadOnlyList<string> RequiredNames);

        public static GroupSamplingPlan ExpectedGroupSampling(
            SectionPlan plan, string alignmentName)
        {
            var records = plan.Records.Where(r =>
                string.Equals(r.SelectedAlignment, alignmentName, StringComparison.OrdinalIgnoreCase));

            var sampledPlans = records
                .SelectMany(r => r.PlannedSources)
                .Where(s => string.Equals(s.PlannedState, "sampled", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var names = sampledPlans.Select(s => s.SourceName)
                .Concat(plan.Records
                    .Where(r => string.Equals(r.SelectedAlignment, alignmentName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(r => r.UtilityCoverage?.Represented ?? new List<string>()))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var required = sampledPlans
                .Where(s => s.Required)
                .Select(s => s.SourceName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new GroupSamplingPlan(names, required);
        }

        /// <summary>
        /// Rerun decision (plan §7.12): unchanged fingerprint → UNCHANGED; changed →
        /// UPDATE; missing → CREATE. Ownership conflicts surface as REVIEW.
        /// </summary>
        public static PlanAction DecideRerunAction(
            string? existingFingerprint, string newFingerprint, bool existingIsToolOwned)
        {
            if (existingFingerprint == null) return PlanAction.Create;
            if (!existingIsToolOwned) return PlanAction.ReviewRequired;
            return string.Equals(existingFingerprint, newFingerprint, StringComparison.Ordinal)
                ? PlanAction.Unchanged
                : PlanAction.Update;
        }

        /// <summary>Engineering-input fingerprint for a planned section (drives idempotency).</summary>
        public static string ComputeFingerprint(SectionPlanRecord record)
        {
            var fields = new Dictionary<string, object?>
            {
                ["annotation_layout"] = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.AnnotationLayoutVersion,
                ["cl_drawing_hash"] = record.Cl.SourceDrawingHash,
                ["cl_handle"] = record.Cl.SourceHandle,
                ["cl_wcs"] = record.Cl.WcsEndpoints.Select(v => (object?)v).ToArray(),
                ["alignment"] = record.SelectedAlignment,
                ["station"] = record.Station,
                ["skew"] = record.SkewDeg,
                ["left"] = record.LeftExtent,
                ["right"] = record.RightExtent,
                ["sources"] = record.PlannedSources
                    .OrderBy(s => s.SourceName, StringComparer.Ordinal)
                    .Select(s => (object?)$"{s.SourceType}:{s.SourceName}:{s.PlannedState}")
                    .ToArray(),
                ["projected_entities"] = record.ProjectedEntities
                    .OrderBy(p => p.ProjectionKey, StringComparer.Ordinal)
                    // A host-drawn utility is bound by path and handle, not by the host
                    // file hash, which every save changes (SourceHashForEvidence).
                    .Select(p => (object?)$"{p.ProjectionKey}:{p.SystemLabel}:{p.SourceLayer}:{p.SourceXref}:" +
                        $"{p.SourceDrawingPath}:{MahodAI.CivilDelivery.Shared.SectionProjectionLogic.SourceHashForEvidence(p.SourceXref, p.SourceDrawingHash)}:{p.SourceHandle}")
                    .ToArray(),
                ["projected_systems"] = record.ProjectedSystems
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .Select(s => (object?)s)
                    .ToArray(),
                ["utility_projection_coverage"] = new object?[]
                {
                    record.UtilityCoverage.ProjectionScanState.ToString(),
                    record.UtilityCoverage.ProjectionDrawingEntityCount,
                    record.UtilityCoverage.ProjectionSectionCrossingCount,
                },
                ["traffic_directions"] = record.TrafficDirections
                    .OrderBy(d => d.LaneMidOffsetM)
                    .Select(d => (object?)FormattableString.Invariant(
                        $"{d.FromOffsetM:R}:{d.ToOffsetM:R}:{d.LaneMidOffsetM:R}:{d.StripLabel}:{d.StripKind}:{d.EvidenceMode}:{d.State}:{d.Flow}:{d.OfficeCarView}:{d.DirectionSource}:{d.DirectionDigest}") +
                        (d.TrackEvidenceDigest == null ? "" : ":source-track-v1:" + d.TrackEvidenceDigest))
                    .ToArray(),
                ["presentation_coverage"] = new object?[]
                {
                    record.PresentationCoverage.EvidenceDigest,
                    record.PresentationCoverage.Complete,
                    record.PresentationCoverage.PlanMarkCount,
                    record.PresentationCoverage.DimensionMarkCount,
                    record.PresentationCoverage.WidthSpanCount,
                    record.PresentationCoverage.NamedStripCount,
                    record.PresentationCoverage.VehicleStripCount,
                    record.PresentationCoverage.OfficeCarStripCount,
                    record.PresentationCoverage.BoundarySource,
                    record.PresentationCoverage.BoundaryFromM,
                    record.PresentationCoverage.BoundaryToM,
                    record.PresentationCoverage.RowAuthorityState,
                    record.PresentationCoverage.AuthoritativeRowSourceKey,
                    record.PresentationCoverage.RowCandidateSourceKeys
                        .OrderBy(key => key, StringComparer.Ordinal)
                        .Select(key => (object?)key).ToArray(),
                    record.PresentationCoverage.DimensionMarks
                        .OrderBy(item => item.OffsetM)
                        .ThenBy(item => item.Kind, StringComparer.Ordinal)
                        .ThenBy(item => item.Label, StringComparer.Ordinal)
                        .ThenBy(item => item.ColorIndex)
                        .Select(item => (object?)FormattableString.Invariant(
                            $"{item.OffsetM:R}:{item.Kind}:{item.Label}:{item.ColorIndex}"))
                        .ToArray(),
                    record.PresentationCoverage.ExplicitSpanOverrides
                        .OrderBy(item => item.OffsetM)
                        .ThenBy(item => item.Label, StringComparer.Ordinal)
                        .ThenBy(item => item.Source, StringComparer.Ordinal)
                        .Select(item => (object?)FormattableString.Invariant(
                            $"{item.OffsetM:R}:{item.Label}:{item.Source}:{item.Evidence}"))
                        .ToArray(),
                    record.PresentationCoverage.UnresolvedSpans
                        .OrderBy(item => item.FromOffsetM)
                        .ThenBy(item => item.ToOffsetM)
                        .Select(item => (object?)FormattableString.Invariant(
                            $"{item.FromOffsetM:R}:{item.ToOffsetM:R}:{item.WidthM:R}:{item.LeftKind}:{item.RightKind}:{item.Reason}"))
                        .ToArray(),
                },
                ["styles"] = record.PlannedStyles
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => (object?)$"{kv.Key}={kv.Value}")
                    .ToArray(),
            };
            // Preserve historical/single-strip fingerprint bytes; only composite
            // source-track authorities add a new field.
            if (record.CompositeTrafficEnvelopes.Count != 0)
                fields["composite_traffic_tracks_v1"] = record.CompositeTrafficEnvelopes
                    .OrderBy(envelope => envelope.FromOffsetM).ThenBy(envelope => envelope.ToOffsetM)
                    .Select(envelope => (object?)System.Text.Json.JsonSerializer.Serialize(envelope)).ToArray();
            // Optional so existing managed legacy fingerprints do not change.
            if (record.ExplicitSurfacePair != null)
                fields["reviewed_surface_pair_v1"] = System.Text.Json.JsonSerializer.Serialize(record.ExplicitSurfacePair);
            if (record.TrafficStraightScopeEvidence != null)
                fields["traffic_native_straight_scope_v1"] = record.TrafficStraightScopeEvidence;
            return LogicalKeys.Fingerprint(fields);
        }

        private static void Review(SectionPlanRecord record, string code, FindingSeverity severity,
            string title, string message = "")
        {
            record.Findings.Add(new DeliveryFinding
            {
                Code = code, Domain = Domain, Severity = severity, Title = title, Message = message,
                AffectedRecordIds = { record.RecordId },
            });
            record.Status = DeliveryStatus.ReviewRequired;
            record.Action = PlanAction.ReviewRequired;
        }

        private static void Fail(SectionPlanRecord record, string code, FindingSeverity severity,
            string title, string message = "")
        {
            record.Findings.Add(new DeliveryFinding
            {
                Code = code, Domain = Domain, Severity = severity, Title = title, Message = message,
                AffectedRecordIds = { record.RecordId },
            });
            record.Status = DeliveryStatus.Failed;
        }
    }
}
