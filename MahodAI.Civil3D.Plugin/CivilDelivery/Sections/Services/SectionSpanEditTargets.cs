using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Current measured targets, not new PLAN geometry or transferred approvals.</summary>
internal static class SectionSpanEditTargets
{
    internal const string ResolvedEditReason = "resolved-span-name-edit";

    internal static IEnumerable<SectionUnresolvedSpanPlan> ForRecord(
        SectionPlanRecord record, bool includeResolvedSpans)
    {
        var unresolved = record.PresentationCoverage.UnresolvedSpans.AsEnumerable();
        if (!includeResolvedSpans) return unresolved;
        return unresolved.Concat(record.PresentationCoverage.ResolvedSpans.Select(span =>
            new SectionUnresolvedSpanPlan
            {
                FromOffsetM = span.FromOffsetM, ToOffsetM = span.ToOffsetM, WidthM = span.WidthM,
                LeftKind = span.LeftKind, RightKind = span.RightKind,
                Reason = ResolvedEditReason,
                SuggestedLabel = span.Label,
            }));
    }
}
