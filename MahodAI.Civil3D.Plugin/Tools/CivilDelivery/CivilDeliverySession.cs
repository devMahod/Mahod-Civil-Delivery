using System;
using System.IO;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;
using EstimateNs = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using SectionsNs = MahodAI.Civil3D.Plugin.CivilDelivery.Sections;

namespace MahodAI.Civil3D.Plugin.Tools.CivilDelivery
{
    /// <summary>
    /// Shared session state for the Civil Delivery workflow: the direct commands and
    /// the AI tools operate on the SAME last plan/apply/scan so the engineer sees one
    /// consistent workflow regardless of entry point.
    /// </summary>
    public static class CivilDeliverySession
    {
        public sealed record SectionsContext(
            SectionPlan? Plan,
            SectionApplyResult? Apply,
            ProjectProfile? Profile,
            string? ProfileHash,
            string? ProfileSource,
            string? ProfileWriteTarget);

        public sealed record SetupContext(
            SectionsNs.Contracts.ProjectSetupScan? Scan,
            ProjectProfile? Profile,
            string? ProfileHash,
            string? ProfileWriteTarget);

        public sealed record EstimateContext(
            EstimateNs.EstimateWorkflowService.ScanResult? Scan,
            MahodAI.CivilDelivery.Estimate.EstimateResult? Estimate,
            ProjectProfile? Profile,
            string? ProfileHash,
            string? ProfileWriteTarget);

        private static readonly object Lock = new();

        public static SectionPlan? LastPlan { get; private set; }
        public static SectionApplyResult? LastApply { get; private set; }
        public static string? LastPlanProfileHash { get; private set; }
        public static ProjectProfile? LastProfile { get; private set; }
        public static EstimateNs.EstimateWorkflowService.ScanResult? LastScan { get; private set; }
        public static MahodAI.CivilDelivery.Estimate.EstimateResult? LastEstimate { get; private set; }
        public static SectionsNs.Contracts.ProjectSetupScan? LastSetupScan { get; private set; }
        public static string? LastSetupProfileWriteTarget { get; private set; }
        private static ProjectProfile? LastSetupProfile { get; set; }
        private static string? LastSetupProfileHash { get; set; }
        private static ProjectProfile? LastSectionsProfile { get; set; }
        private static string? LastSectionsProfileHash { get; set; }
        private static string? LastSectionsProfileSource { get; set; }
        private static string? LastSectionsProfileWriteTarget { get; set; }
        private static ProjectProfile? LastEstimateProfile { get; set; }
        private static string? LastEstimateProfileHash { get; set; }
        private static string? LastEstimateProfileWriteTarget { get; set; }

        public static void SetSetupScan(
            SectionsNs.Contracts.ProjectSetupScan scan, ProjectProfile profile, string? profileHash,
            string profileWriteTarget)
        {
            if (string.IsNullOrWhiteSpace(profileWriteTarget) ||
                !Path.IsPathFullyQualified(profileWriteTarget))
                throw new ArgumentException(
                    "An authoritative profile write target is required.", nameof(profileWriteTarget));
            lock (Lock)
            {
                LastSetupScan = scan;
                LastSetupProfile = profile;
                LastSetupProfileHash = profileHash;
                LastProfile = profile;
                LastPlanProfileHash = profileHash;
                LastSectionsProfile = profile;
                LastSectionsProfileHash = profileHash;
                LastSectionsProfileSource = null;
                LastSetupProfileWriteTarget = profileWriteTarget;
                LastSectionsProfileWriteTarget = LastSetupProfileWriteTarget;
            }
        }

        public static void SetPlan(
            SectionPlan plan, ProjectProfile profile, string? profileHash,
            string profileWriteTarget, string? profileSource = null)
        {
            if (string.IsNullOrWhiteSpace(profileWriteTarget) ||
                !Path.IsPathFullyQualified(profileWriteTarget))
                throw new ArgumentException(
                    "An authoritative profile write target is required.", nameof(profileWriteTarget));
            lock (Lock)
            {
                LastPlan = plan;
                LastProfile = profile;
                LastPlanProfileHash = profileHash;
                LastSectionsProfile = profile;
                LastSectionsProfileHash = profileHash;
                LastSectionsProfileSource = profileSource;
                LastSectionsProfileWriteTarget = profileWriteTarget;
                LastApply = null;
            }
        }

        public static void SetApply(SectionApplyResult apply)
        {
            lock (Lock) { LastApply = apply; }
        }

        public static void ClearSectionsContext()
        {
            lock (Lock)
            {
                LastPlan = null;
                LastApply = null;
                LastSectionsProfile = null;
                LastSectionsProfileHash = null;
                LastSectionsProfileSource = null;
                LastSectionsProfileWriteTarget = null;
            }
        }

        public static void ClearSectionsApply()
        {
            lock (Lock) { LastApply = null; }
        }

        public static void SetScan(
            EstimateNs.EstimateWorkflowService.ScanResult scan, ProjectProfile profile,
            string? profileHash, string profileWriteTarget)
        {
            if (string.IsNullOrWhiteSpace(profileWriteTarget) ||
                !Path.IsPathFullyQualified(profileWriteTarget))
                throw new ArgumentException(
                    "An authoritative profile write target is required.", nameof(profileWriteTarget));
            lock (Lock)
            {
                LastScan = scan;
                LastEstimate = null;
                LastEstimateProfile = profile;
                LastEstimateProfileHash = profileHash;
                LastEstimateProfileWriteTarget = profileWriteTarget;
            }
        }

        public static void SetEstimate(MahodAI.CivilDelivery.Estimate.EstimateResult estimate)
        {
            lock (Lock)
            {
                if (LastScan == null ||
                    !string.Equals(LastScan.RunId, estimate.RunId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Estimate does not belong to the current quantity scan.");
                LastEstimate = estimate;
            }
        }

        public static void ClearEstimateResult()
        {
            lock (Lock) { LastEstimate = null; }
        }

        /// <summary>
        /// Atomic snapshot for section consumers. Reading four process-global
        /// properties independently could combine a plan with a profile changed by a
        /// neighbouring workflow between reads.
        /// </summary>
        public static SectionsContext GetSectionsContext()
        {
            lock (Lock)
                return new SectionsContext(
                    LastPlan, LastApply, LastSectionsProfile, LastSectionsProfileHash,
                    LastSectionsProfileSource, LastSectionsProfileWriteTarget);
        }

        /// <summary>
        /// Atomic setup snapshot. Setup used to read LastSetupScan, LastProfile and
        /// LastSetupProfileWriteTarget independently; a section PLAN between those
        /// reads could combine a setup scan for project A with project B's profile.
        /// </summary>
        public static SetupContext GetSetupContext()
        {
            lock (Lock)
                return new SetupContext(
                    LastSetupScan, LastSetupProfile, LastSetupProfileHash,
                    LastSetupProfileWriteTarget);
        }

        public static void ClearSetupContext()
        {
            lock (Lock)
            {
                LastSetupScan = null;
                LastSetupProfile = null;
                LastSetupProfileHash = null;
                LastSetupProfileWriteTarget = null;
            }
        }

        /// <summary>Atomic estimate snapshot, isolated from section/setup profile changes.</summary>
        public static EstimateContext GetEstimateContext()
        {
            lock (Lock)
                return new EstimateContext(
                    LastScan, LastEstimate, LastEstimateProfile, LastEstimateProfileHash,
                    LastEstimateProfileWriteTarget);
        }

        public static void ClearEstimateContext()
        {
            lock (Lock)
            {
                LastScan = null;
                LastEstimate = null;
                LastEstimateProfile = null;
                LastEstimateProfileHash = null;
                LastEstimateProfileWriteTarget = null;
            }
        }
    }
}
