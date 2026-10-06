using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Test-only convenience overloads. Production deliberately exposes no mutation
    /// API without explicit compare-and-swap evidence; legacy unit tests establish a
    /// synthetic workflow boundary here instead of weakening that contract.
    /// </summary>
    internal static class EstimateCasTestExtensions
    {
        internal static ProjectProfileWriter.SaveResult SaveEarthworksDecision(
            this EstimateWorkflowService workflow, ProjectProfile profile,
            bool includeEarthworks, string? reason, string approvedBy,
            string targetPath) =>
            workflow.SaveEarthworksDecision(
                profile, includeEarthworks, reason, approvedBy, targetPath,
                ProfileCasTest.For(profile, targetPath));

        internal static ProjectProfileWriter.SaveResult SaveApprovedMappings(
            this EstimateWorkflowService workflow, ProjectProfile profile,
            CatalogSnapshot snapshot,
            IReadOnlyList<EstimateWorkflowService.MappingApproval> approvals,
            string approvedBy, string targetPath) =>
            workflow.SaveApprovedMappings(
                profile, snapshot, approvals, approvedBy,
                targetPath: targetPath,
                expectedProfileState: ProfileCasTest.For(profile, targetPath));

        internal static ProjectProfileWriter.SaveResult ApproveCompleteDiscoveryScope(
            this EstimateWorkflowService workflow, ProjectProfile profile,
            string approvedBy, string targetPath) =>
            workflow.ApproveCompleteDiscoveryScope(
                profile, approvedBy, targetPath,
                ProfileCasTest.For(profile, targetPath));

        internal static ProjectProfileWriter.SaveResult SaveIgnoredRuleDecision(
            this EstimateWorkflowService workflow, ProjectProfile profile,
            EstimateWorkflowService.ScanResult? scan, string ruleKey,
            bool excludeFromEstimate, string? reason, string approvedBy,
            string targetPath) =>
            workflow.SaveIgnoredRuleDecision(
                profile, scan, ruleKey, excludeFromEstimate, reason, approvedBy,
                targetPath, ProfileCasTest.For(profile, targetPath));

        internal static ProjectProfileWriter.SaveResult SaveIgnoredRuleDecisions(
            this EstimateWorkflowService workflow, ProjectProfile profile,
            EstimateWorkflowService.ScanResult scan,
            IReadOnlyList<EstimateWorkflowService.IgnoredRuleDecisionRequest> requests,
            string approvedBy, string targetPath) =>
            workflow.SaveIgnoredRuleDecisions(
                profile, scan, requests, approvedBy, targetPath,
                ProfileCasTest.For(profile, targetPath));

        internal static ProjectProfileWriter.SaveResult SaveApprovedClosedPolylineMapping(
            this EstimateWorkflowService workflow, ProjectProfile profile,
            CatalogSnapshot snapshot, EstimateWorkflowService.ScanResult scan,
            EstimateWorkflowService.MappingApproval approval,
            string alternativeRuleKey, string approvedBy, string targetPath) =>
            workflow.SaveApprovedClosedPolylineMapping(
                profile, snapshot, scan, approval, alternativeRuleKey, approvedBy,
                targetPath, ProfileCasTest.For(profile, targetPath));
    }
}
