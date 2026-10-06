using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    /// <summary>The exact drawing, immutable plan and record shown by a decision dialog.</summary>
    private sealed record SectionDecisionScope(
        Document Document,
        SectionPlan Plan,
        SectionPlanRecord Record,
        string RecordId,
        string Stage);

    private sealed record SectionDecisionWriteContext(
        ActiveProjectProfileService.ActiveLoadResult CurrentProfile,
        ProjectProfileWriter.ExpectedProfileState ExpectedState);

    private bool TryCaptureSectionDecisionScope(
        SectionPlanRecord record, string stage, out SectionDecisionScope scope)
    {
        try { scope = CaptureSectionDecisionScope(record, stage); return true; }
        catch (Exception ex)
        {
            scope = null!;
            ShowError("פתיחת הכרעת חתך", ex);
            return false;
        }
    }

    private SectionDecisionScope CaptureSectionDecisionScope(SectionPlanRecord record, string stage)
    {
        ArgumentNullException.ThrowIfNull(record);
        var document = Doc() ?? throw new InvalidOperationException(
            "אין שרטוט פעיל — יש לפתוח את השרטוט ולבחור חתך מחדש.");
        var plan = _plan ?? throw new InvalidOperationException(
            "אין תכנון חתכים פעיל — יש להריץ תכנון ולבחור חתך מחדש.");
        if (string.IsNullOrWhiteSpace(record.RecordId) ||
            string.IsNullOrWhiteSpace(stage))
            throw new InvalidOperationException(
                "זהות הכרעת החתך חסרה — יש לבחור חתך מתכנון עדכני.");

        var scope = new SectionDecisionScope(document, plan, record, record.RecordId, stage);
        RequireSectionDecisionIdentity(scope);
        return scope;
    }

    /// <summary>
    /// Runs after the final modal confirmation and before changing the profile copy
    /// or writing it. A save/replan cannot repair choices already accepted from a
    /// stale dialog: reject them and let the user reopen against current evidence.
    /// </summary>
    private SectionDecisionWriteContext RequireSectionDecisionScope(SectionDecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireSectionDecisionIdentity(scope);
        var currentProfile = RequireFreshSectionPlan(scope.Stage);
        var expectedState = CaptureExpectedProfileState(currentProfile);
        // Recheck identity after source/profile reads too. All comparisons are to
        // the captured objects, not merely to a reused record ID or a newer plan.
        RequireSectionDecisionIdentity(scope);
        return new SectionDecisionWriteContext(currentProfile, expectedState);
    }

    private void RequireSectionDecisionIdentity(SectionDecisionScope scope)
    {
        if (!ReferenceEquals(Doc(), scope.Document))
            throw new InvalidOperationException(
                "השרטוט הוחלף בזמן ההכרעה — הבחירה לא נשמרה. חזור לשרטוט הרצוי ופתח את ההכרעה מחדש.");
        if (!ReferenceEquals(_plan, scope.Plan))
            throw new InvalidOperationException(
                "התכנון הוחלף בזמן ההכרעה — הבחירה לא נשמרה. בחר חתך מהתכנון העדכני ופתח את ההכרעה מחדש.");

        var matchingRecords = scope.Plan.Records.Where(record =>
            string.Equals(record.RecordId, scope.RecordId, StringComparison.Ordinal)).ToList();
        if (matchingRecords.Count != 1 || !ReferenceEquals(matchingRecords[0], scope.Record) ||
            SectionsGrid.SelectedItem is not SectionRowViewModel selected ||
            !ReferenceEquals(selected.Record, scope.Record))
            throw new InvalidOperationException(
                "הרשומה הנבחרת השתנתה בזמן ההכרעה — הבחירה לא נשמרה. בחר את החתך הרצוי ופתח את ההכרעה מחדש.");
    }
}
