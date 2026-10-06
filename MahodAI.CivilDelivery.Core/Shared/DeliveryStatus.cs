using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Shared record/workflow status vocabulary for both Civil Delivery domains
    /// (Sections and Estimate), per the locked build plan §5.2. One vocabulary,
    /// domain-specific finding codes.
    /// </summary>
    public enum DeliveryStatus
    {
        Discovered = 0,
        Ready = 1,
        PreviewReady = 2,
        Applied = 3,
        Verified = 4,
        Warning = 10,
        ReviewRequired = 20,
        Blocked = 30,
        Failed = 40,
    }

    /// <summary>Finding severity, ordered: worse severities have higher values.</summary>
    public enum FindingSeverity
    {
        Info = 0,
        Warning = 1,
        ReviewRequired = 2,
        Error = 3,
    }

    public static class DeliveryStatusRules
    {
        /// <summary>
        /// Batch status must reflect the worst unresolved required record (§5.2).
        /// "Worst" ranks problem states above progress states; among progress states
        /// the LEAST advanced record governs (a batch is only Verified when every
        /// required record is Verified).
        /// </summary>
        public static DeliveryStatus Aggregate(IReadOnlyCollection<DeliveryStatus> statuses)
        {
            if (statuses == null || statuses.Count == 0)
                return DeliveryStatus.Discovered;

            if (statuses.Contains(DeliveryStatus.Failed)) return DeliveryStatus.Failed;
            if (statuses.Contains(DeliveryStatus.Blocked)) return DeliveryStatus.Blocked;
            if (statuses.Contains(DeliveryStatus.ReviewRequired)) return DeliveryStatus.ReviewRequired;
            if (statuses.Contains(DeliveryStatus.Warning)) return DeliveryStatus.Warning;

            // Only progress states remain — least advanced governs.
            return statuses.Min();
        }

        /// <summary>
        /// The status a record may hold given its worst finding severity. A WARNING
        /// never silently changes an engineering result; REVIEW_REQUIRED and ERROR
        /// force the corresponding gate states (§5.2 rules).
        /// </summary>
        public static DeliveryStatus CapByFindings(DeliveryStatus intended, IEnumerable<DeliveryFinding> findings)
        {
            var worst = FindingSeverity.Info;
            foreach (var f in findings ?? Enumerable.Empty<DeliveryFinding>())
            {
                if (f.Severity > worst) worst = f.Severity;
            }

            return worst switch
            {
                FindingSeverity.Error => DeliveryStatus.Failed,
                FindingSeverity.ReviewRequired => DeliveryStatus.ReviewRequired,
                FindingSeverity.Warning when intended < DeliveryStatus.Warning => intended, // warning annotates, does not regress progress
                _ => intended,
            };
        }
    }
}
