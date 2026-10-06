using System;
using System.Linq;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

public sealed partial class EstimateWorkflowService
{
    public SemanticHintStore.Snapshot SaveSemanticHint(ProjectProfile profile, ScanResult scan,
        string ruleKey, SemanticHintPolicy.Draft input, string recordedBy, SemanticHintStore store,
        string? expectedHintHash)
    {
        if (scan.ProfileWriteState == null ||
            scan.ProjectProfileId != profile.ProfileId ||
            !string.Equals(scan.ProjectProfileEffectiveHash, EstimateTraceIdentity.EffectiveProfileHash(profile),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("התיאור אינו שייך לפרופיל ולסריקה שנפתחו לבדיקה.");
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, scan.ProfileWriteState);
        var records = scan.Records.Where(record => string.Equals(record.Classification.RuleKey,
            ruleKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        var group = ManualMappingCaseScope.Collect(records, profile)
            .SingleOrDefault(value => string.Equals(value.RuleKey, ruleKey, StringComparison.Ordinal));
        if (group == null || group.Refusal != null)
            throw new InvalidOperationException(group?.Refusal ?? "קבוצת התיאור אינה קיימת בסריקה.");
        var scope = SemanticHintPolicy.Capture(profile.ProfileId, scan.SourceDrawing,
            scan.SourceDrawingHash ?? "", ruleKey, group.Records);
        return store.Save(scope, input, recordedBy, expectedHintHash);
    }
}
