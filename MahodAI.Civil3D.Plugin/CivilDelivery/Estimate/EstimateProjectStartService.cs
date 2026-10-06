using System;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>A first estimate profile or missing discipline review, not section setup.</summary>
internal static class EstimateProjectStartService
{
    /// <summary>
    /// <paramref name="Discipline"/>: the engineer's declaration — "roads" or "landscape" — which selects the BoQ library
    /// (schema 6, Codex 01:27, 02/10). Null keeps the legacy profile (roads library, no field written); the dialog always
    /// asks for an explicit choice.
    /// </summary>
    internal sealed record Decision(string ProjectName, string ApprovedBy, bool ConfirmSourceScope, string? Discipline = null);
    internal const string DisciplineMissing = "יש לבחור תחום עבודה: כבישים ותנועה או פיתוח נופי וגינון";
    internal const string Guidance = "אפשר להתחיל אומדן בלי CL, חתכים או מקורות דגימה. " +
        "בדוק את זהות הפרויקט ואת המקורות בחלון אחד; רק אישורך ישמור פרופיל ויתחיל מדידה. " +
        "שיוכים, מחירים ועבודות עפר אינם מאושרים בשלב זה.";

    internal static bool NeedsStart(ProjectProfile? profile, ProjectProfileWriter.ExpectedProfileState? expected) =>
        profile != null && expected != null &&
        (!expected.SourceExisted || string.IsNullOrWhiteSpace(profile.Estimate.Discipline));

    internal static string? DecisionFailure(Decision? decision)
    {
        if (decision == null) return "לא אושרה התחלת האומדן";
        if (string.IsNullOrWhiteSpace(decision.ProjectName)) return "יש להזין שם פרויקט";
        if (string.IsNullOrWhiteSpace(decision.ApprovedBy)) return "יש להזין את שם המאשר";
        if (!decision.ConfirmSourceScope) return "יש לאשר במפורש את היקף המקורות המוצג";
        if (decision.Discipline != null && !ProjectProfileSchemaPolicy.Disciplines.Contains(decision.Discipline, StringComparer.Ordinal))
            return "התחום שנבחר אינו מוכר";
        return null;
    }

    internal static ProjectProfileWriter.SaveResult? Save(EstimateWorkflowService workflow,
        ProjectProfile profile, ProjectProfileWriter.ExpectedProfileState expected, Decision? decision)
    {
        if (decision == null) return null; // Cancellation does not read, mutate or persist the seed.
        var invalid = DecisionFailure(decision);
        if (invalid != null) throw new InvalidOperationException(invalid);
        if (!NeedsStart(profile, expected))
            throw new InvalidOperationException("הפרופיל כבר נשמר; אין להחליף אותו במסלול התחלת פרויקט");
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expected);
        if (expected.SourceExisted && string.IsNullOrWhiteSpace(decision.Discipline))
            throw new InvalidOperationException(DisciplineMissing);
        // Stage the edited display name on a clone; identity and all other decisions stay unchanged.
        var staged = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(profile, SectionsWorkflowService.Json),
            SectionsWorkflowService.Json) ?? throw new InvalidOperationException("לא ניתן להכין את פרופיל האומדן");
        staged.ProjectName = decision.ProjectName.Trim();
        // The declared discipline is written with the first profile (schema 6); null leaves a legacy roads profile.
        if (decision.Discipline != null) staged.Estimate.Discipline = decision.Discipline;
        // A book/section setup may have persisted the seed before estimate start. Completing its discipline must
        // not clear SourceSelection, re-approve all XREFs, or replace existing engineering decisions.
        if (expected.SourceExisted)
            return ProjectProfileWriter.Save(staged, expected.TargetPath,
                "estimate project name and explicit discipline reviewed; existing source scope and decisions preserved",
                decision.ApprovedBy.Trim(), expected);
        return workflow.ApproveCompleteDiscoveryScope(staged, decision.ApprovedBy.Trim(), expected.TargetPath, expected);
    }
}
