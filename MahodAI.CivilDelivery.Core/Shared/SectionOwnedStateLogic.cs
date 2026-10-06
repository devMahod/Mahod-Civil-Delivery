using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Host-free proof gate for an idempotent Sections rerun.  Matching one metadata
    /// record is not enough to say UNCHANGED: the complete owned Civil pair and its
    /// live annotation registry must agree on identity, profile and fingerprint.
    /// </summary>
    public static class SectionOwnedStateLogic
    {
        public enum State
        {
            Absent,
            Complete,
            Repairable,
            Conflict,
        }

        public sealed record ObjectEvidence(
            string ObjectIdentity,
            string Feature,
            string Role,
            string ProjectProfileId,
            string InputFingerprint,
            bool LogicalKeyIsExact,
            string? ParentSampleLineIdentity = null);

        public sealed record RegistryEvidence(
            bool IsReadable,
            bool EntryExists,
            int EntryCount,
            int LiveEntryCount,
            bool OwnershipComplete,
            string? Error = null)
        {
            public bool IsComplete =>
                IsReadable && EntryExists && EntryCount > 0 && LiveEntryCount == EntryCount;
        }

        public sealed record Evaluation(State State, string Reason)
        {
            public bool AllowsUnchanged => State == SectionOwnedStateLogic.State.Complete;
        }

        /// <summary>Dead annotation references may be repaired only for an existing,
        /// unambiguous current-key owned target. A bare/orphan registry is not authority.</summary>
        public static bool CanRepairDeadAnnotations(
            IEnumerable<ObjectEvidence> candidates, string profileId, string fingerprint,
            bool inventoryReadable, RegistryEvidence registry)
        {
            var objects = candidates.ToList();
            if (objects.Count == 0 || objects.Any(x => !x.LogicalKeyIsExact) ||
                !registry.IsReadable || !registry.EntryExists || registry.EntryCount == 0)
                return false;
            return Evaluate(objects, profileId, fingerprint, inventoryReadable, registry).State
                is State.Complete or State.Repairable;
        }

        public static Evaluation Evaluate(
            IEnumerable<ObjectEvidence> candidates,
            string expectedProjectProfileId,
            string expectedInputFingerprint,
            bool inventoryReadable,
            RegistryEvidence registry)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (string.IsNullOrWhiteSpace(expectedProjectProfileId))
                throw new ArgumentException("A project profile id is required.",
                    nameof(expectedProjectProfileId));
            if (string.IsNullOrWhiteSpace(expectedInputFingerprint))
                throw new ArgumentException("An input fingerprint is required.",
                    nameof(expectedInputFingerprint));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var objects = candidates.ToList();
            if (!inventoryReadable)
                return new Evaluation(State.Conflict, "owned-inventory-unreadable");
            if (objects.Count == 0)
            {
                // A registry entry is itself managed state.  Calling this CREATE
                // would hide a legacy/orphan migration behind a new-object label and
                // could bypass callers that only reconcile UPDATE.  The global
                // inventory has already proved its exact live handles; force one
                // atomic repair.  An unreadable entry is conflict, never absence.
                if (!registry.IsReadable)
                    return new Evaluation(State.Conflict,
                        "annotation-registry-unreadable-without-owned-pair");
                if (registry.EntryExists)
                    return new Evaluation(State.Repairable,
                        "annotation-registry-requires-owned-pair-migration");
                return new Evaluation(State.Absent, "owned-pair-absent");
            }

            var unexpected = objects.Where(o =>
                    !string.Equals(o.Feature, "sections", StringComparison.OrdinalIgnoreCase) ||
                    !(string.Equals(o.Role, "sample-line", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(o.Role, "section-view", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unexpected.Count > 0)
                return new Evaluation(State.Conflict, "logical-key-role-collision");

            if (objects.Any(o => !string.Equals(
                    o.ProjectProfileId, expectedProjectProfileId, StringComparison.Ordinal)))
                return new Evaluation(State.Conflict, "project-profile-mismatch");

            var sampleLines = objects
                .Where(o => string.Equals(o.Role, "sample-line", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var views = objects
                .Where(o => string.Equals(o.Role, "section-view", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sampleLines.Count > 1)
                return new Evaluation(State.Conflict, "duplicate-owned-sample-line");
            if (views.Count > 1)
                return new Evaluation(State.Conflict, "duplicate-owned-section-view");
            if (sampleLines.Count == 0 && views.Count > 0)
                return new Evaluation(State.Conflict, "orphan-owned-section-view");
            if (sampleLines.Count == 1 && views.Count == 0)
                return new Evaluation(State.Repairable, "owned-section-view-missing");
            if (sampleLines.Count == 0)
                return new Evaluation(State.Conflict, "owned-role-set-incomplete");

            var sampleLine = sampleLines[0];
            var view = views[0];
            if (!string.Equals(view.ParentSampleLineIdentity, sampleLine.ObjectIdentity,
                    StringComparison.OrdinalIgnoreCase))
                return new Evaluation(State.Conflict, "owned-view-not-linked-to-owned-sample-line");

            if (!sampleLine.LogicalKeyIsExact || !view.LogicalKeyIsExact)
                return new Evaluation(State.Repairable, "legacy-logical-key-requires-migration");

            if (!string.Equals(sampleLine.InputFingerprint, expectedInputFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(view.InputFingerprint, expectedInputFingerprint,
                    StringComparison.Ordinal))
                return new Evaluation(State.Repairable, "owned-fingerprint-mismatch");

            if (!registry.IsReadable)
                return new Evaluation(State.Conflict, "annotation-registry-unreadable");
            if (!registry.EntryExists)
                return new Evaluation(State.Repairable, "annotation-registry-missing");
            if (registry.EntryCount == 0)
                return new Evaluation(State.Repairable, "annotation-registry-empty");
            if (registry.LiveEntryCount != registry.EntryCount)
                return new Evaluation(State.Repairable, "annotation-registry-has-dead-entries");
            if (!registry.OwnershipComplete)
                return new Evaluation(State.Repairable,
                    "annotation-ownership-migration-required");

            return new Evaluation(State.Complete, "owned-pair-and-annotations-proven");
        }
    }
}
