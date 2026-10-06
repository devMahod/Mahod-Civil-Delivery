using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Engineer-readable projections of the deterministic results. The UI binds to
    /// these; it never re-derives an engineering value. Anything shown here came out
    /// of the same services the direct commands and the MahodAI tools call.
    /// </summary>
    public sealed class SectionRowViewModel
    {
        public required SectionPlanRecord Record { get; init; }

        /// <summary>
        /// PLAN is immutable evidence used by APPLY/VERIFY, so the palette must not
        /// mutate its record status just to repaint a row.  Later workflow results can
        /// override the display status while the original plan remains trustworthy.
        /// </summary>
        public DeliveryStatus? StatusOverride { get; init; }
        public string? GlobalPlanningBlockReason { get; init; }
        public string? CreationBlockReason { get; init; }

        private DeliveryStatus EffectiveStatus => !string.IsNullOrWhiteSpace(GlobalPlanningBlockReason) ||
            !string.IsNullOrWhiteSpace(CreationBlockReason)
            ? DeliveryStatus.Blocked : StatusOverride ?? Record.Status;

        public string SectionId => Record.SectionId ?? Record.Cl.SourceHandle;
        public string ClEvidence =>
            $"{Bidi.Ltr(Record.Cl.SourceEntityType)} · שכבה {Bidi.Ltr(Record.Cl.SourceLayer)} · אורך {Record.Cl.Length:F1} מ'" +
            (Record.Cl.SourceXref != null
                ? " · מתוך " + Bidi.Ltr(System.IO.Path.GetFileName(Record.Cl.SourceXref))
                : "");

        public string Alignment => Record.SelectedAlignment ?? "—";

        public string Candidates
        {
            get
            {
                var names = Record.CandidateCrossings.Select(c => c.AlignmentName).Distinct().ToList();
                if (names.Count == 0) return "אין חיתוך";
                if (names.Count == 1 && Record.SelectedAlignment != null) return names[0];
                return string.Join(", ", names) + (Record.SelectedAlignment == null ? "  (דרושה הכרעה)" : "");
            }
        }

        public string Station => Record.Station?.ToString("F2") ?? "—";
        public string Skew => Record.SkewDeg is { } s ? $"{s:F1}°" : "—";
        public string Extents =>
            Record.LeftExtent is { } l && Record.RightExtent is { } r ? $"{l:F1} / {r:F1}" : "—";

        public string Sources =>
            Record.PlannedSources.Count == 0
                ? "—"
                : string.Join(", ", Record.PlannedSources.Select(p => p.SourceName));

        public string Utilities
        {
            get
            {
                // "אין מערכות בשרטוט" told the engineer her systems don't exist while
                // they sat in the UT XREF as polylines (recording, 2026-08-30). Sampled
                // Civil sources and projected XREF linework are both named now.
                // A record whose CL never resolved (bent polyline, no alignment) had
                // NOTHING computed - claiming "no systems in drawing" there reads as a
                // wrong engineering statement (live table, 31/08). Silence is honest.
                if (Record.SelectedAlignment == null || Record.SelectedCrossing == null)
                    return "—";

                var u = Record.UtilityCoverage;
                var projected = Record.ProjectedSystems;
                var parts = new List<string>();
                if (projected.Count > 0)
                    parts.Add(string.Join(", ", projected.Distinct()));
                if (u.Represented.Count > 0) parts.Add($"{u.Represented.Count} נדגמות");
                if (u.NotConfigured.Count > 0) parts.Add($"{u.NotConfigured.Count} לא מוגדרות");
                if (u.Missing.Count > 0) parts.Add($"{u.Missing.Count} חסרות");
                if (u.Unsupported.Count > 0) parts.Add($"{u.Unsupported.Count} לא נתמכות");

                if (parts.Count == 0)
                    return SectionProjectionLogic.SystemsSummary(
                        u.Represented,
                        projected,
                        u.ProjectionScanState,
                        u.ProjectionDrawingEntityCount,
                        u.ProjectionSectionCrossingCount);

                if (u.ProjectionScanState == UtilityProjectionScanState.Blocked)
                    parts.Add("סריקת XREF חסומה — הכיסוי חלקי");
                else if (u.ProjectionScanState == UtilityProjectionScanState.Disabled)
                    parts.Add("מערכות XREF לא נסרקו");
                else if (u.ProjectionScanState == UtilityProjectionScanState.NotRun)
                    parts.Add("בדיקת מערכות טרם הושלמה");
                return string.Join(" · ", parts);
            }
        }

        public string Styles =>
            Record.PlannedStyles.Count == 0
                ? "—"
                : string.Join(" · ", Record.PlannedStyles.Select(kv => kv.Value));

        public string Layout =>
            Record.PlannedLayoutPosition is { Length: 2 } p ? $"{p[0]:F0}, {p[1]:F0}" : "—";

        public string Action => Record.Action switch
        {
            PlanAction.Create => "יצירה",
            PlanAction.Update => "עדכון",
            PlanAction.Replace => "החלפה",
            PlanAction.Unchanged => "ללא שינוי",
            PlanAction.ReviewRequired => "דרושה בדיקה",
            PlanAction.Excluded => "הוחרג באישור",
            _ => Record.Action.ToString(),
        };

        public bool CanChooseCrossing =>
            Record.Action != PlanAction.Excluded &&
            Record.Status != DeliveryStatus.Ready &&
            Record.CandidateCrossings.Count > 0 &&
            Record.Findings.Any(f => f.Code is SectionFindingCodes.AlignmentAmbiguous or
                SectionFindingCodes.ClMultipleIntersections);

        public bool CanExclude =>
            Record.Action != PlanAction.Excluded &&
            Record.Status != DeliveryStatus.Ready &&
            Record.Findings.Any(SectionPlanLogic.IsExcludableFinding);

        public bool CanResolveTrafficDirection =>
            Record.Action != PlanAction.Excluded &&
            Record.TrafficDirections.Any(direction => !direction.IsResolved);

        public bool CanEditTrafficDirections =>
            Record.Action != PlanAction.Excluded &&
            !string.IsNullOrWhiteSpace(Record.SelectedAlignment) &&
            Record.TrafficDirections.Count > 0;

        public bool CanNameSpans =>
            Record.Action != PlanAction.Excluded &&
            !string.IsNullOrWhiteSpace(Record.SelectedAlignment) &&
            (string.Equals(Record.PresentationCoverage.RowAuthorityState,
                 "authoritative", System.StringComparison.Ordinal) ||
             string.Equals(Record.PresentationCoverage.RowAuthorityState,
                 "nocandidates", System.StringComparison.Ordinal)) &&
            Record.PresentationCoverage.UnresolvedSpans.Count > 0;

        public bool CanApproveRowSource =>
            Record.Action != PlanAction.Excluded &&
            Record.PresentationCoverage.RowCandidateSourceKeys.Count > 0 &&
            !string.Equals(Record.PresentationCoverage.RowAuthorityState,
                "authoritative", StringComparison.Ordinal);

        public bool CanEditSpanLabels =>
            Record.Action != PlanAction.Excluded &&
            !string.IsNullOrWhiteSpace(Record.SelectedAlignment) &&
            (Record.PresentationCoverage.RowAuthorityState is "authoritative" or "nocandidates") &&
            (Record.PresentationCoverage.UnresolvedSpans.Count > 0 || Record.PresentationCoverage.ResolvedSpans.Count > 0);

        public string TrafficDirections
        {
            get
            {
                if (Record.TrafficDirections.Count == 0) return "—";
                var resolved = Record.TrafficDirections.Count(direction => direction.IsResolved);
                return resolved == Record.TrafficDirections.Count
                    ? $"{resolved}/{Record.TrafficDirections.Count} מוכרעים"
                    : $"{resolved}/{Record.TrafficDirections.Count} · דרושה הכרעה";
            }
        }

        /// <summary>
        /// SEC-B1 (review of 1.3.9): when the approved ROW source has no ROW line at
        /// this cut the panel says so, instead of the section silently ending at the
        /// last plan mark. Null when a two-sided ROW envelope was found.
        /// </summary>
        public string? Boundary => SectionPlanBlockerSummaryLogic.DescribeBoundary(
            Record.PresentationCoverage.BoundarySource,
            Record.PresentationCoverage.RowAuthorityState);

        public string DecisionEvidence => Record.ExplicitExclusion is { } x
            ? $"הוחרג ע\"י {x.ApprovedBy} ב-{x.ApprovedAtUtc:u}: {x.Reason}"
            : "";

        public string Status => Record.Action == PlanAction.Excluded
            ? "הוחרג באישור"
            : EffectiveStatus switch
        {
            DeliveryStatus.Ready => "מוכן",
            DeliveryStatus.ReviewRequired => "דרושה בדיקה",
            DeliveryStatus.Blocked => "חסום",
            DeliveryStatus.Failed => "נכשל",
            DeliveryStatus.Applied => "הוחל",
            DeliveryStatus.Verified => "אומת",
            _ => EffectiveStatus.ToString(),
        };

        /// <summary>Ready / review / blocked drives the row colour in the grid.</summary>
        public string Severity => EffectiveStatus switch
        {
            DeliveryStatus.Ready or DeliveryStatus.Applied or DeliveryStatus.Verified => "ok",
            DeliveryStatus.ReviewRequired => "review",
            _ => "blocked",
        };

        public string Findings =>
            Record.Findings.Count == 0
                ? ""
                : string.Join(" | ", Record.Findings.Select(Bidi.FindingLine));

        public bool HasFindings => Record.Findings.Count > 0;

        public string NextStep => Record.Action == PlanAction.Excluded ? "הוחרג באישור"
            : !string.IsNullOrWhiteSpace(GlobalPlanningBlockReason) ? "טיפול בחסימת התכנון"
            : !string.IsNullOrWhiteSpace(CreationBlockReason) ? CreationBlockReason
            : EffectiveStatus == DeliveryStatus.Verified ? "אומת — אפשר להציג"
            : EffectiveStatus == DeliveryStatus.Applied ? "אימות התוצר"
            : CanChooseCrossing ? "בחירת חצייה"
            : CanApproveRowSource ? "בחירת מקור זכות דרך"
            : CanNameSpans ? "שמות רצועות"
            : CanResolveTrafficDirection ? "כיווני נסיעה"
            : EffectiveStatus == DeliveryStatus.Ready
                ? Record.Action == PlanAction.Unchanged ? "קיים — ללא שינוי"
                    : Record.Findings.Any(finding => finding.Code == SectionFindingCodes.AnnotationRegistryRepairable)
                        ? "עדכון ושחזור חתך"
                    : Record.Action is PlanAction.Update or PlanAction.Replace ? "מוכן לעדכון" : "מוכן ליצירה"
                : "בדיקת פרטי החתך";
    }

    /// <summary>One measured quantity group with its mapping state, engineer-readable.</summary>
    public sealed class QuantityRowViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public required string RuleKey { get; init; }
        public required string Layer { get; init; }
        public required string EntityType { get; init; }
        public required string Method { get; init; }
        public required int ObjectCount { get; init; }
        public required double Quantity { get; init; }
        public required string Unit { get; init; }

        public string? CatalogCode { get; set; }
        public string? CatalogDescription { get; set; }
        public string SourceCategory { get; set; } = "לא מסווג";
        public string CatalogDescriptionDisplay => !string.IsNullOrWhiteSpace(CatalogCode)
            ? string.IsNullOrWhiteSpace(CatalogDescription) ? "חסר תיאור במחירון הפעיל" : CatalogDescription
            : ProjectRuleGoverned || string.IsNullOrWhiteSpace(ProposedCode) ? "טרם אושר סעיף"
            : string.Equals(_proposalDescriptionCode, ProposedCode, StringComparison.OrdinalIgnoreCase) &&
              !string.IsNullOrWhiteSpace(_proposalDescription)
                ? _proposalDescription : "חסר תיאור להצעה במחירון הפעיל";
        public string? ProposedCode { get; set; }
        public string? ProposalReason { get; set; }
        private string? _proposalDescriptionCode;
        private string? _proposalDescription;

        /// <summary>Display only, from the exact proposed code in the current book; never mapping or price authority.</summary>
        public void RefreshProposalDescription(CatalogSnapshot? catalog)
        {
            var description = !string.IsNullOrWhiteSpace(ProposedCode) && catalog != null &&
                catalog.Items.TryGetValue(ProposedCode, out var item) ? item.Description : null;
            if (_proposalDescriptionCode == ProposedCode && _proposalDescription == description) return;
            _proposalDescriptionCode = ProposedCode;
            _proposalDescription = description;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CatalogDescriptionDisplay)));
        }
        /// <summary>L05: why the project's quantity rules govern (or could not prove) this group; never an approval or a price.</summary>
        public string? ProjectRuleNote { get; set; }
        /// <summary>The project rules leave this group no generic proposal for its raw sum (L05).</summary>
        public bool ProjectRuleGoverned { get; set; }
        /// <summary>What the catalog column shows instead of a proposal (e.g. "לפי כללי הפרויקט", "לא סווג בכללים").</summary>
        public string? ProjectRuleLabel { get; set; }
        // Display/search evidence only. It is never passed as an approved mapping.
        private string _proposalSearchText = "";
        public string ProposalSearchText
        {
            get => _proposalSearchText;
            set
            {
                if (_proposalSearchText == value) return;
                _proposalSearchText = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProposalSearchText)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CatalogCodeDetail)));
            }
        }
        public string CatalogCodeDetail => !string.IsNullOrWhiteSpace(CatalogCode)
            ? CatalogDescription ?? CatalogCode : "הצעות לבדיקה בלבד — לא שיוך מאושר\n" + ProposalSearchText;
        public string? Price { get; set; }
        public required string MappingState { get; set; }
        public string Findings { get; set; } = "";
        public string? AlternativeRuleKey { get; set; }
        public string? AlternativeQuantityDisplay { get; set; }
        public string? AlternativeCatalogCode { get; set; }

        private string? _historicalReason;
        public string? HistoricalReason
        {
            get => _historicalReason;
            set
            {
                if (_historicalReason == value) return;
                _historicalReason = value;
                foreach (var name in new[] { nameof(IsHistorical), nameof(StatusDisplay), nameof(PriceDisplay),
                             nameof(StatusDetail), nameof(Severity), nameof(CanApproveCatalogMapping), nameof(IsBulkNoiseCandidate) })
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }
        public bool IsHistorical => HistoricalReason != null;
        private EstimateQuantityPresentationPolicy.State? DisplayPresentation => IsHistorical
            ? EstimateQuantityHistoryPolicy.Presentation(HistoricalReason!) : Presentation;

        private EstimateQuantityPresentationPolicy.State? _presentation;
        public EstimateQuantityPresentationPolicy.State? Presentation
        {
            get => _presentation;
            set
            {
                if (_presentation == value) return;
                _presentation = value;
                foreach (var name in new[] { nameof(StatusDisplay), nameof(PriceDisplay), nameof(StatusDetail), nameof(Severity) })
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string StatusDisplay => DisplayPresentation?.Summary ?? (MappingState == "מאושר" ? "שיוך מאושר" : MappingState);
        public string? PriceDisplay => DisplayPresentation?.PriceDisplay ?? (DisplayPresentation == null ? Price : null);
        public string StatusDetail => DisplayPresentation?.Detail ?? "מצב שיוך בלבד — תוצאת המדידה והתמחור טרם נבדקה.";

        /// <summary>The engineer marked this group as not a construction quantity.</summary>
        public bool IsIgnored { get; set; }

        /// <summary>What the significance classifier thinks, before any engineer decision.</summary>
        public QuantitySignificance.Verdict? Verdict { get; set; }

        /// <summary>
        /// The bulk route is intentionally closed to ordinary quantities, utilities,
        /// implausible magnitudes and anything already mapped.  It only presents exact
        /// groups that the deterministic classifier can prove are station geometry or
        /// auxiliary drawing furniture; the service validates the same rule again.
        /// </summary>
        public bool IsBulkNoiseCandidate =>
            !IsHistorical &&
            !IsIgnored &&
            !string.IsNullOrWhiteSpace(RuleKey) &&
            !string.Equals(RuleKey, "(ללא חוק)", System.StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(CatalogCode) &&
            Verdict is
            {
                Kind: QuantitySignificance.Kind.StationGeometry or
                      QuantitySignificance.Kind.Auxiliary,
                Reason: { Length: > 0 },
            };

        public string SignificanceReason => Verdict?.Reason ?? "—";

        /// <summary>
        /// True only when the exact same closed-polyline source handles produced the
        /// area/perimeter alternative.  Similar layer names are not enough.
        /// </summary>
        public bool HasClosedPolylineAlternative =>
            !string.IsNullOrWhiteSpace(AlternativeRuleKey);

        public bool IsUnselectedClosedPolylineAlternative =>
            HasClosedPolylineAlternative &&
            string.IsNullOrWhiteSpace(CatalogCode) &&
            !string.IsNullOrWhiteSpace(AlternativeCatalogCode);

        /// <summary>
        /// Once one exact closed-polyline sibling is mapped, the other is an audited
        /// exclusion, never a second catalog choice. An ignored group must likewise be
        /// returned to the estimate before it can be mapped.
        /// </summary>
        public bool CanApproveCatalogMapping =>
            !IsHistorical &&
            !IsIgnored &&
            !(HasClosedPolylineAlternative &&
              !string.IsNullOrWhiteSpace(AlternativeCatalogCode));

        public string QuantityDisplay => $"{Quantity:N2} {Unit}";
        /// <summary>Layer first - it is what the engineer recognises; count and type after.</summary>
        public string Source => $"{Bidi.Ltr(Layer)} · {ObjectCount} × {Bidi.Ltr(EntityType)}";

        /// <summary>The measurement method in one Hebrew word; the raw name stays in the detail panel.</summary>
        public string MethodDisplay => Method.Split('+')[0] switch
        {
            "polyline-length" or "closed-polyline-perimeter" or
                "closed-polyline2d-perimeter" or "polyline2d-length" or "polyline3d-length" or
                "line-length" or "arc-length" or "spline-length" or "circle-circumference" or "length" => "אורך",
            "closed-polyline-area" or "closed-polyline2d-area" or "hatch-area" or
                "hatch-linear-boundary-area" or "hatch-line-arc-boundary-area" or "area" => "שטח",
            "hatch-exact-retrace-linear-area" or "hatch-exact-retrace-mixed-line-area" => "שטח (שחזור מדויק)",
            "block-count" or "count" => "ספירה",
            _ => Method,
        };

        /// <summary>
        /// The catalog code when approved; otherwise the top proposal marked as such, so
        /// 5,000 unmapped rows read as "here is a suggestion" instead of an empty column.
        /// 01/10 (Arthur): the word "הצעה", not a "?" mixed into right-to-left text, and the code isolated LTR.
        /// </summary>
        public string CatalogCodeDisplay =>
            CatalogCode != null ? Bidi.Ltr(CatalogCode) : ProjectRuleGoverned ? ProjectRuleLabel ?? "לפי כללי הפרויקט"
            : ProposedCode != null ? "הצעה: " + Bidi.Ltr(ProposedCode) : "—";

        public string Severity => IsHistorical ? "review" : IsIgnored ? "muted" : Presentation?.Severity ?? "review";
    }

    /// <summary>What the UI is allowed to do right now — the state gate.</summary>
    public sealed class WorkflowGate
    {
        public bool CanPlan { get; set; }
        public bool CanPreview { get; set; }
        public bool CanClearPreview { get; set; }
        public bool CanApply { get; set; }
        public bool IsNoOpBaseline { get; set; }
        public bool CanVerify { get; set; }
        public bool CanShow { get; set; }
        public bool CanResolveRecords { get; set; }
        public string? GlobalPlanningBlockReason { get; private set; }
        public string Reason { get; set; } = "";

        /// <summary>
        /// Derives the gate from real state. Apply is refused while any record is
        /// blocked or the plan is stale, and Show/Verify need a committed result — a
        /// disabled button with a reason beats an action that silently does nothing.
        /// </summary>
        public static WorkflowGate From(
            bool profileUsable,
            SectionPlan? plan,
            bool planStale,
            SectionApplyResult? apply,
            bool previewShown)
            => From(profileUsable, plan, planStale, apply, previewShown, null);

        public static WorkflowGate From(
            bool profileUsable,
            SectionPlan? plan,
            bool planStale,
            SectionApplyResult? apply,
            bool previewShown,
            string? verifySummary)
        {
            var g = new WorkflowGate { CanPlan = profileUsable };

            if (!profileUsable)
            {
                g.Reason = "הפרופיל אינו תקין — יש להריץ הגדרת פרויקט";
                return g;
            }
            if (plan == null)
            {
                g.Reason = "יש להריץ תכנון כדי להתחיל";
                return g;
            }
            if (planStale)
            {
                g.Reason = "התוכנית אינה עדכנית (השרטוט או הפרופיל השתנו) — יש להריץ תכנון מחדש";
                return g;
            }

            var globalBlockers = plan.Findings
                .Where(finding =>
                    finding.Severity >= FindingSeverity.ReviewRequired &&
                    (finding.ResolvedAtUtc == null ||
                     string.IsNullOrWhiteSpace(finding.ResolvedBy) ||
                     string.IsNullOrWhiteSpace(finding.Resolution)))
                .ToList();
            if (globalBlockers.Count > 0)
            {
                g.CanShow = plan.Records.Count > 0;
                g.CanClearPreview = previewShown;
                // Reviewing an independent ROW/name decision is not APPLY. A
                // known geometry-reader failure must not hide those useful
                // editors, but source/identity/evidence failures still do.
                g.CanResolveRecords = apply is not { Committed: true } &&
                    CanReviewDecisionsWithGeometryFailures(plan, globalBlockers);
                // Plan-level findings scoped to other rows must not make an
                // independently usable row appear globally blocked. Known geometry
                // decision-review remains available through its existing strict gate.
                var blockingAllRecords = globalBlockers
                    .Where(finding => finding.AffectedRecordIds.Count == 0).ToList();
                // 1.4.1 D1: unavailable XREFs lead, grouped by name, with Reload / fix-path guidance.
                var xrefBlocking = blockingAllRecords.Where(f => XrefAvailabilityText.TryParse(f, out _)).ToList();
                var otherBlocking = blockingAllRecords.Except(xrefBlocking).ToList();
                if (!g.CanResolveRecords && blockingAllRecords.Count > 0)
                    g.GlobalPlanningBlockReason = xrefBlocking.Count > 0
                        ? XrefAvailabilityText.Summarize(xrefBlocking) +
                          (otherBlocking.Count > 0
                              ? $"\nועוד {otherBlocking.Count} חסימות כלליות (בפרטים למטה)." : "")
                        : blockingAllRecords[0].Title +
                          (blockingAllRecords.Count > 1
                              ? $" · ועוד {blockingAllRecords.Count - 1} חסימות כלליות (בפרטים למטה)." : "");
                // SEC-m1 (review of 1.3.9): one grouped Hebrew line built from each
                // finding's own file/layer/reason evidence; codes and handles stay in
                // the details panel. "Ready rows are unaffected" is said only when every
                // blocker is scoped to other records and a Ready record is outside them.
                var scopedOnly = globalBlockers.All(finding => finding.AffectedRecordIds.Count > 0);
                var affectedIds = new HashSet<string>(
                    globalBlockers.SelectMany(finding => finding.AffectedRecordIds), StringComparer.Ordinal);
                var unaffectedReady = scopedOnly && plan.Records.Any(record =>
                    record.Status == DeliveryStatus.Ready && !affectedIds.Contains(record.RecordId));
                g.Reason = SectionPlanBlockerSummaryLogic.Describe(
                    globalBlockers,
                    recordId => plan.Records.FirstOrDefault(record =>
                            string.Equals(record.RecordId, recordId, StringComparison.Ordinal))
                        is { } match ? match.SectionId ?? match.Cl.SourceHandle : null,
                    unaffectedReady,
                    // r11: the guided card and the blocker details list every XREF name; the table summary counts.
                    xrefNamesShownElsewhere: true);
                if (g.CanResolveRecords)
                    g.Reason += " · אפשר להשלים הכרעות מקור ושמות; יצירה ואימות נשארים חסומים עד לתיקון הגאומטריה.";
                return g;
            }

            g.CanPreview = plan.Records.Count > 0;
            g.CanClearPreview = previewShown;

            var applicable = plan.Records
                .Where(r => r.Status == DeliveryStatus.Ready)
                .Where(r => r.Action is PlanAction.Create or PlanAction.Update or PlanAction.Replace)
                .ToList();
            var unresolved = SectionPlanLogic.UnresolvedBatchRecords(plan).Count;
            g.CanResolveRecords = unresolved > 0;

            if (unresolved > 0)
            {
                g.Reason = SectionTaskSummary(plan) +
                           " — יש להשלים את המשימות או להחריג CL רק מסיבה הנדסית מאושרת";
            }
            else if (applicable.Count == 0)
            {
                var unchanged = plan.Records.Count(r => r.Action == PlanAction.Unchanged);
                var excluded = plan.Records.Count(SectionPlanLogic.HasValidExplicitExclusion);
                // A signed all-exclusion plan may publish its decision bundle without
                // touching the drawing.  Unchanged is deliberately different: PLAN's
                // classification is not object-level APPLY/VERIFY evidence, so the UI
                // must not offer a green no-op baseline for it.
                if (plan.Records.Count > 0 && excluded == plan.Records.Count)
                {
                    g.CanApply = true;
                    g.IsNoOpBaseline = true;
                    g.Reason = $"אין אובייקטים להחלה · {excluded} רשומות מוחרגות באישור" +
                               " — יש לפרסם את ראיות ההחרגה";
                }
                else if (unchanged > 0 && unchanged + excluded == plan.Records.Count)
                {
                    g.Reason = $"אין שינוי להחלה · {unchanged} חתכים קיימים" +
                               (excluded > 0 ? $" · {excluded} מוחרגים באישור" : "") +
                               " — PLAN לבדו אינו ראיית אימות";
                }
                else
                {
                    g.Reason = "אין רשומות מוכנות להחלה";
                }
            }
            else
            {
                g.CanApply = true;
                g.Reason = $"{applicable.Count} חתכים מוכנים להחלה";
            }

            // "Show" works from the plan onwards: before apply it zooms to the CL line,
            // after apply to the created section view.
            g.CanShow = true;
            if (apply is { Committed: true })
            {
                // The plan is consumed: applying it again would be a second run on the
                // same inputs, which is what PLAN (idempotency) is for. The reason line
                // now tells the story forward - applied, then verified.
                var verifyUnresolved = SectionPlanLogic.UnresolvedVerificationRecords(plan, apply);
                g.CanVerify = verifyUnresolved.Count == 0;
                g.CanApply = false;
                var applied = apply.Records.Count(r => r.Status == DeliveryStatus.Applied);
                var unchanged = apply.Records.Count(r => r.ActionTaken == PlanAction.Unchanged);
                g.Reason = verifyUnresolved.Count > 0
                    ? $"האימות חסום — חסרות ראיות החלה/החרגה ל-{verifyUnresolved.Count} רשומות PLAN"
                    : verifySummary ??
                      ($"הוחלו {applied} חתכים" + (unchanged > 0 ? $" · {unchanged} ללא שינוי" : "") + " — לאימות מול המודל: אמת");
            }
            return g;
        }

        private static bool CanReviewDecisionsWithGeometryFailures(
            SectionPlan plan, IReadOnlyCollection<DeliveryFinding> blockers) =>
            SectionInputIntegrityService.HasMetricUnitEvidence(plan) &&
            !string.IsNullOrWhiteSpace(plan.SourceDrawing) &&
            !string.IsNullOrWhiteSpace(plan.SourceDatabaseRevision) &&
            SectionVehicleDirectionPlanner.IsSha256(plan.ProjectProfileHash) &&
            SectionPlanLogic.UnresolvedBatchRecords(plan).Count > 0 &&
            blockers.All(finding =>
                finding.Code == SectionFindingCodes.ProjectionGeometryUnsupported &&
                finding.SourceRefs.Count > 0 &&
                finding.SourceRefs.All(source =>
                    !string.IsNullOrWhiteSpace(source.SourcePathOrUri) &&
                    SectionVehicleDirectionPlanner.IsSha256(source.DrawingChecksum)));

        public static string SectionTaskSummary(SectionPlan plan)
        {
            var ready = plan.Records.Count(record => record.Status == DeliveryStatus.Ready);
            var row = plan.Records.Count(record =>
                record.PresentationCoverage.RowCandidateSourceKeys.Count > 0 &&
                !string.Equals(record.PresentationCoverage.RowAuthorityState,
                    "authoritative", StringComparison.Ordinal));
            var spans = plan.Records.Sum(record =>
                record.PresentationCoverage.UnresolvedSpans.Count);
            var directions = plan.Records.Sum(record =>
                record.TrafficDirections.Count(direction => !direction.IsResolved));
            var noMarks = plan.Records.Count(record =>
                record.SelectedCrossing != null &&
                record.PresentationCoverage.PlanMarkCount == 0 &&
                !record.PresentationCoverage.Complete);
            var noIntersection = plan.Records.Count(record => record.Findings.Any(finding =>
                string.Equals(finding.Code, SectionFindingCodes.ClNoIntersection,
                    StringComparison.Ordinal)));

            var tasks = new List<string>
            {
                $"{plan.Records.Count} רשומות",
                $"{ready} מוכנות",
            };
            if (row > 0) tasks.Add($"מקור ROW חסר ב־{row} " + (row == 1 ? "חתך" : "חתכים"));
            if (spans > 0) tasks.Add($"{spans} שמות רצועות");
            if (directions > 0) tasks.Add($"{directions} כיווני נסיעה");
            if (noMarks > 0) tasks.Add($"{noMarks} ללא מקור רצועות חוצה");
            if (noIntersection > 0) tasks.Add($"{noIntersection} ללא חיתוך");
            return string.Join(" · ", tasks);
        }
    }
}
