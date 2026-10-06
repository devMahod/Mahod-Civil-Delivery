using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private const string SpanBatchDecisionStage = "SPAN-LABEL-BATCH-DECISION";

    // Gate-time eligibility only. Full source/evidence/CAS checks occur in the handler.
    private bool HasWholePlanSpanLabelCandidates() =>
        _profile != null && SectionSpanBatchReviewScope.EligibleRecords(_plan).Count > 0;

    private sealed record SectionBatchDecisionScope(
        ProfileDecisionScope ProfileScope, SectionSpanBatchReviewScope PlanScope);

    private void OnNameAllSectionSpans(object sender, RoutedEventArgs e)
    {
        if (!EnsureSavedForAction("שמות רצועות לכל התכנון", OnNameAllSectionSpans, replan: true)) return;
        if (_profile == null || _plan == null) return;
        try
        {
            if (!HasWholePlanSpanLabelCandidates())
            {
                SetStatus("אין כרגע רצועות שניתן לערוך באצווה. בדוק את ממצאי התכנון ובחירת הציר; לא נשמרו שינויים.");
                SectionDiagnostics.IsExpanded = true;
                RefreshGates();
                return;
            }

            var scope = new SectionBatchDecisionScope(
                CaptureProfileDecisionScope("שמות רצועות לכל התכנון"),
                SectionSpanBatchReviewScope.Capture(_plan));
            RequireSectionBatchDecisionScope(scope);
            var records = scope.PlanScope.TargetRecords;
            var dialog = new SectionSpanLabelDecisionDialog(
                records, initiallyApproveStrongSuggestions: false,
                previousDecisions: scope.ProfileScope.Profile.Sections.Decisions.SpanLabels);
            if (CivilModalHost.ShowFromPalette(dialog) != true) return;

            // Keep the pre-dialog baseline, including all target identities. Never
            // turn accepted choices into authority for a newly selected/reloaded plan.
            RequireSectionBatchDecisionScope(scope);
            var approvals = dialog.Approvals.ToArray();
            var profileForSave = CloneProfileForDecision(scope.ProfileScope.Profile);
            var count = scope.PlanScope.ApproveReviewedLabels(
                _plan, profileForSave, approvals, dialog.ApprovedBy, dialog.ApprovedAtUtc);
            RequireSectionBatchDecisionScope(scope);
            var saved = ProjectProfileWriter.Save(
                profileForSave, scope.ProfileScope.ExpectedState.TargetPath,
                $"whole-plan span labels: run={scope.PlanScope.Plan.RunId}; displayed_records={records.Count}; approved_spans={count}",
                dialog.ApprovedBy, expectedState: scope.ProfileScope.ExpectedState);
            PublishSavedProfile(saved);
            _apply = null;
            _verifySummary = null;
            _sectionDisplayStatuses.Clear();
            Log($"אושרו {count} שמות רצועות מתוך {records.Count} חתכים שהוצגו; יתר הרצועות נשארות לבדיקה.");
            SetStatus("שמות הרצועות המסומנות נשמרו — מריץ תכנון מחדש");
            OnPlan(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            ReloadProfile();
            ShowError("אישור שמות רצועות לכל התכנון", ex);
            RefreshGates();
        }
    }

    private void RequireSectionBatchDecisionScope(SectionBatchDecisionScope scope)
    {
        RequireProfileDecisionScope(scope.ProfileScope);
        scope.PlanScope.RequireUnchanged(_plan);
        RequireFreshSectionPlan(SpanBatchDecisionStage);
        SectionsWorkflowService.RequirePlanEvidence(scope.PlanScope.Plan);
        // Source/profile reads must not replace the original objects or baseline.
        RequireProfileDecisionScope(scope.ProfileScope);
        scope.PlanScope.RequireUnchanged(_plan);
    }
}

/// <summary>
/// Host-free whole-plan modal identity. The saved JSON snapshot catches in-place
/// changes, while exact references reject same-ID replacements, including rows not
/// currently selected. No grid selection is part of this batch authority.
/// </summary>
internal sealed class SectionSpanBatchReviewScope
{
    private readonly SectionPlanRecord[] _allRecords;
    private readonly string _planSnapshot;
    internal SectionPlan Plan { get; }
    internal IReadOnlyList<SectionPlanRecord> TargetRecords { get; }

    private SectionSpanBatchReviewScope(SectionPlan plan)
    {
        Plan = plan;
        _allRecords = plan.Records.ToArray();
        TargetRecords = Array.AsReadOnly(EligibleRecords(plan).ToArray());
        _planSnapshot = JsonSerializer.Serialize(plan, SectionsWorkflowService.Json);
    }

    internal static IReadOnlyList<SectionPlanRecord> EligibleRecords(SectionPlan? plan) =>
        plan == null ? Array.Empty<SectionPlanRecord>() : plan.Records
            .Where(record => record.Action != PlanAction.Excluded &&
                !string.IsNullOrWhiteSpace(record.SelectedAlignment) &&
                record.PresentationCoverage.UnresolvedSpans.Count > 0 &&
                (string.Equals(record.PresentationCoverage.RowAuthorityState,
                     "authoritative", StringComparison.Ordinal) ||
                 string.Equals(record.PresentationCoverage.RowAuthorityState,
                     "nocandidates", StringComparison.Ordinal)))
            .OrderBy(record => record.Station ?? double.MaxValue)
            .ThenBy(record => record.RecordId, StringComparer.Ordinal)
            .ToArray();

    internal static SectionSpanBatchReviewScope Capture(SectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Records.Any(record => string.IsNullOrWhiteSpace(record.RecordId)) ||
            plan.Records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() !=
                plan.Records.Count || EligibleRecords(plan).Count == 0)
            throw new InvalidOperationException(
                "אין אצוות רצועות בעלת זהויות חד-משמעיות — יש להריץ תכנון מחדש.");
        RequireUniquePhysicalSpanIdentities(EligibleRecords(plan));
        return new SectionSpanBatchReviewScope(plan);
    }

    private static void RequireUniquePhysicalSpanIdentities(IReadOnlyList<SectionPlanRecord> records)
    {
        // Persisted label decisions bind source/hash/handle/alignment and measured
        // bounds, not RecordId. Validate the ENTIRE displayed batch, not only the
        // checked subset: otherwise approving one row also approves its unchecked
        // physical twin on the next PLAN. Use the writer's boundary-match tolerance.
        const double toleranceM = 0.01;
        var groups = records.SelectMany(record => record.PresentationCoverage.UnresolvedSpans
                .Select(span => (Record: record, Span: span)))
            .GroupBy(item => (item.Record.Cl.SourceDrawingHash.ToUpperInvariant(),
                item.Record.Cl.SourceHandle.ToUpperInvariant(),
                item.Record.SelectedAlignment!.ToUpperInvariant()));
        foreach (var group in groups)
        {
            var spans = group.ToArray();
            for (var i = 0; i < spans.Length; i++)
            for (var j = i + 1; j < spans.Length; j++)
                if (Math.Abs(spans[i].Span.FromOffsetM - spans[j].Span.FromOffsetM) <= toleranceM &&
                    Math.Abs(spans[i].Span.ToOffsetM - spans[j].Span.ToOffsetM) <= toleranceM)
                    throw new InvalidOperationException(
                        $"שורות {spans[i].Record.RecordId} ו-{spans[j].Record.RecordId} מייצגות אותה רצועת מקור. " +
                        "אישור חלקי עלול להשפיע גם על שורה שלא סומנה; יש לתקן את כפילות המקור ולהריץ PLAN מחדש. לא נשמרו בחירות.");
        }
    }

    internal void RequireUnchanged(SectionPlan? current)
    {
        if (!ReferenceEquals(current, Plan) || Plan.Records.Count != _allRecords.Length ||
            Plan.Records.Where((record, index) => !ReferenceEquals(record, _allRecords[index])).Any() ||
            !string.Equals(_planSnapshot, JsonSerializer.Serialize(Plan, SectionsWorkflowService.Json),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "התכנון או אחת מרשומות האצווה השתנו בזמן ההכרעה — הבחירות לא נשמרו. " +
                "פתח את סקירת כל הרצועות מחדש על התכנון העדכני.");
    }

    internal int ApproveReviewedLabels(SectionPlan? current, ProjectProfile profile,
        IReadOnlyCollection<SectionDecisionProfileService.SpanLabelBatchApproval> approvals,
        string approvedBy, DateTime approvedAtUtc)
    {
        RequireUnchanged(current);
        // Only checked, currently displayed unresolved spans are being reviewed here.
        // Match the selected editor's exact-boundary engineer override: the legacy
        // additive decision leaves a conflicting source name unresolved on the next PLAN.
        foreach (var approval in approvals)
            if (!TargetRecords.Any(record => ReferenceEquals(record, approval.Record) &&
                record.PresentationCoverage.UnresolvedSpans.Any(span =>
                    approval.Span != null && span.FromOffsetM == approval.Span.FromOffsetM &&
                    span.ToOffsetM == approval.Span.ToOffsetM)))
                throw new InvalidOperationException(
                    "אחת הבחירות אינה רצועה שהוצגה בסקירת האצווה — לא נשמרו שינויים.");
        return SectionDecisionProfileService.ApproveEditedSpanLabelsBatch(
            profile, TargetRecords, approvals, approvedBy, approvedAtUtc);
    }
}
