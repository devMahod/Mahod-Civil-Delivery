using System;
using System.Linq;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Shared by preview, dressing and APPLY: explicit roles are PLAN/profile identities,
/// not the position of an arbitrary manual Section or a pattern guessed from its name.</summary>
internal static class SectionSurfacePairPlanLogic
{
    internal static ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair? RequireExplicit(
        SectionPlanRecord record, ProjectProfile profile, string drawingFingerprint)
    {
        var evidence = record.ExplicitSurfacePair;
        if (evidence == null)
        {
            if (profile.Sections.Sources.SurfacePairs.Any(p => string.Equals(
                    p.AlignmentName, record.SelectedAlignment, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("PLAN lacks the current reviewed surface pair; re-plan.");
            return null;
        }
        var planned = record.PlannedSources.Where(s => s.Required && s.PlannedState == "sampled" &&
            string.Equals(s.SourceType, "surface", StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = SectionSourceSelectionLogic.SelectExplicitPair(drawingFingerprint,
            record.SelectedAlignment, evidence.AlignmentHandle,
            planned.Select(s => new SectionSourceSelectionLogic.Identity(s.SourceName, s.SourceHandle ?? "")).ToArray(),
            profile.Sections.Sources.SurfacePairs);
        if (!selected.IsValid || selected.Pair != evidence || planned.Length != 2 ||
            !Same(planned[0].SourceName, evidence.ExistingName) ||
            !Same(planned[0].SourceHandle, evidence.ExistingHandle) ||
            !Same(planned[1].SourceName, evidence.DesignName) ||
            !Same(planned[1].SourceHandle, evidence.DesignHandle))
            throw new InvalidOperationException("PLAN/profile EG/FG identities disagree: " + selected.Error);
        return evidence;
    }
    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
