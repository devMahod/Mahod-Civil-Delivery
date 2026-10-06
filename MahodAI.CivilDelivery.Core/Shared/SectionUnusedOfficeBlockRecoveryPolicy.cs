using System;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Admission only, not permission to normalize a shared definition. Native callers
/// must obtain both complete reference and singleton Purge proofs in their locked
/// transaction, then atomically retain the old definition and import a new one.
/// </summary>
public static class SectionUnusedOfficeBlockRecoveryPolicy
{
    public sealed record Evidence(
        bool SelectedScope,
        SectionFurnitureLogic.OfficeCarView View,
        string BlockName,
        string? Comments,
        string SourceEntityFingerprint,
        string ExistingEntityFingerprint,
        bool OrdinaryLocalDefinition,
        bool HeaderMatches,
        bool AttestationMatches,
        int? CompleteActiveReferenceCount,
        bool? SingletonPurgeRetainedExactDefinition,
        bool RetirementNameAvailable);

    public static string? RetirementRejection(Evidence evidence)
    {
        if (!evidence.SelectedScope) return "Recovery is limited to selected scope.";
        if (evidence.View != SectionFurnitureLogic.OfficeCarView.Front &&
            evidence.View != SectionFurnitureLogic.OfficeCarView.Rear)
            return "Unknown office asset.";
        if (!string.Equals(evidence.BlockName,
                SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(evidence.View),
                StringComparison.Ordinal) ||
            !BlockDefinitionFingerprintLogic.IsToolProvenance(evidence.Comments,
                SectionOfficeVehicleAssetEvidenceLogic.SourceComment(evidence.View)))
            return "The definition does not have exact protected tool provenance.";
        if (!SectionVehicleDirectionPlanner.IsSha256(evidence.SourceEntityFingerprint) ||
            !SectionVehicleDirectionPlanner.IsSha256(evidence.ExistingEntityFingerprint) ||
            !string.Equals(evidence.SourceEntityFingerprint, evidence.ExistingEntityFingerprint,
                StringComparison.OrdinalIgnoreCase))
            return "The existing entities do not exactly match the pinned source entities.";
        if (!evidence.OrdinaryLocalDefinition)
            return "Dynamic, anonymous, XREF or layout definitions cannot be retired.";
        if (evidence.HeaderMatches && evidence.AttestationMatches)
            return "A current definition must be reused without retirement.";
        if (evidence.CompleteActiveReferenceCount != 0)
            return "Complete absence of active direct and nested references is not proven.";
        if (evidence.SingletonPurgeRetainedExactDefinition != true)
            return "Absence of other hard references is not proven by singleton Purge.";
        if (!evidence.RetirementNameAvailable)
            return "The retirement name is already occupied.";
        return null;
    }
}
