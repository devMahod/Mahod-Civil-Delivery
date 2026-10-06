using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// The ONE definition of the layout evidence an APPLY artifact carries for a
    /// managed SectionView and of how VERIFY reads it back. Batch APPLY captures it
    /// after the sheet is arranged; selected APPLY captures it where the view was
    /// created. Both sides use these two functions, so the artifact can never again
    /// be written by one path and expected by another (1.2.24 review, 02/09: selected
    /// APPLY skipped the only writer and every selected VERIFY failed by design).
    /// </summary>
    public static class LayoutEvidenceContract
    {
        public sealed record Evidence(double[] Bounds, double[] Location);

        public sealed record Verdict(bool BoundsPass, bool LocationPass)
        {
            public bool Pass => BoundsPass && LocationPass;
        }

        public static Evidence Capture(
            double minX, double minY, double maxX, double maxY,
            double locationX, double locationY,
            double deltaX = 0.0, double deltaY = 0.0)
        {
            foreach (var v in new[] { minX, minY, maxX, maxY, locationX, locationY, deltaX, deltaY })
            {
                if (!double.IsFinite(v))
                    throw new ArgumentOutOfRangeException(nameof(v), "Layout evidence must be finite.");
            }
            if (maxX < minX || maxY < minY)
                throw new ArgumentOutOfRangeException(nameof(maxX), "Layout extents are inverted.");

            return new Evidence(
                new[] { minX + deltaX, minY + deltaY, maxX + deltaX, maxY + deltaY },
                new[] { locationX, locationY });
        }

        /// <summary>Missing evidence on either side is a failure, never a pass.</summary>
        public static Verdict Verify(
            double[]? expectedBounds, double[]? expectedLocation,
            double[]? actualBounds, double[]? actualLocation,
            double toleranceM)
        {
            return new Verdict(
                Near(expectedBounds, actualBounds, 4, toleranceM),
                Near(expectedLocation, actualLocation, 2, toleranceM));
        }

        private static bool Near(double[]? expected, double[]? actual, int length, double tolerance)
        {
            if (expected == null || actual == null) return false;
            if (expected.Length != length || actual.Length != length) return false;
            if (!double.IsFinite(tolerance) || tolerance < 0) return false;
            for (var i = 0; i < length; i++)
            {
                if (!double.IsFinite(expected[i]) || !double.IsFinite(actual[i])) return false;
                if (Math.Abs(expected[i] - actual[i]) > tolerance) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Decides how a SampleLineGroup's sampled sources are reconciled to the plan.
    /// Sampling is a property of the SHARED group: toggling any source — enabling
    /// as much as disabling — re-samples every sample line in it. A batch APPLY owns
    /// the whole group and converges it. A selected-record APPLY may converge only a
    /// group it has to itself; on a group shared with other work it may proceed
    /// solely when the sampled set is already exactly what the plan needs, and must
    /// refuse any delta before the first setter (review, 02/09).
    /// </summary>
    public static class SamplingReconciliationLogic
    {
        public enum Mode
        {
            Batch,
            BatchSharedGroup,
            SelectedExclusiveGroup,
            SelectedSharedGroup,
        }

        public sealed record Plan(
            Mode Mode,
            IReadOnlyList<string> ToEnable,
            IReadOnlyList<string> ToDisable,
            IReadOnlyList<string> AlreadySampled,
            IReadOnlyList<string> MissingDesired,
            IReadOnlyList<string> Blocked)
        {
            public bool IsBlocked => Blocked.Count > 0;
            public bool IsNoOp => ToEnable.Count == 0 && ToDisable.Count == 0 && Blocked.Count == 0;
        }

        public static Plan Decide(
            IEnumerable<(string? Name, bool Sampled)> current,
            IEnumerable<string> desired,
            Mode mode)
        {
            var wanted = new HashSet<string>(
                desired.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            var toEnable = new List<string>();
            var toDisable = new List<string>();
            var already = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, sampled) in current)
            {
                var display = string.IsNullOrWhiteSpace(name) ? "<unresolved>" : name!;
                if (name == null || !wanted.Contains(name))
                {
                    if (sampled) toDisable.Add(display);
                    continue;
                }

                seen.Add(name);
                if (sampled) already.Add(name);
                else toEnable.Add(name);
            }

            var missing = wanted.Where(n => !seen.Contains(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            toEnable.Sort(StringComparer.OrdinalIgnoreCase);
            toDisable.Sort(StringComparer.OrdinalIgnoreCase);
            already.Sort(StringComparer.OrdinalIgnoreCase);

            if (mode is Mode.SelectedSharedGroup or Mode.BatchSharedGroup &&
                (toEnable.Count > 0 || toDisable.Count > 0))
            {
                var blocked = toEnable.Select(n => "+" + n)
                    .Concat(toDisable.Select(n => "-" + n))
                    .ToList();
                return new Plan(mode, Array.Empty<string>(), Array.Empty<string>(),
                    already, missing, blocked);
            }

            return new Plan(mode, toEnable, toDisable, already, missing, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Whether a tool-owned SampleLine may be erased or re-sampled: only when every
    /// attached SectionView is tool-owned too, and only when the children could be
    /// read at all. A read failure is not "no children" — a manual/foreign view drawn
    /// from our sample line would otherwise lose its parent (review, 02/09).
    /// </summary>
    public static class OwnedSampleLineChildrenLogic
    {
        public sealed record Verdict(bool Readable, IReadOnlyList<string> ForeignHandles)
        {
            public bool IsSafeToMutate => Readable && ForeignHandles.Count == 0;
        }

        public static Verdict Decide(bool readable, IEnumerable<(string Handle, bool Owned)> children)
        {
            if (!readable) return new Verdict(false, Array.Empty<string>());
            var foreign = children.Where(c => !c.Owned)
                .Select(c => string.IsNullOrWhiteSpace(c.Handle) ? "<unknown>" : c.Handle)
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToList();
            return new Verdict(true, foreign);
        }
    }

    /// <summary>
    /// Exact authorization predicate for treating a live Civil object as one record's
    /// owned SampleLine/SectionView.  A logical-key collision is never sufficient on
    /// its own: feature, role and project are part of the ownership boundary.  The
    /// source-handle fallback exists only for the documented v1-key migration and is
    /// guarded by that same boundary.
    /// </summary>
    public static class SectionRecordOwnershipLogic
    {
        public static bool IsOwnedByRecord(
            OwnershipMetadata? metadata,
            string projectProfileId,
            string role,
            string logicalKey,
            string sourceClHandle,
            bool allowLegacySourceHandle = true)
        {
            if (metadata == null ||
                !string.Equals(metadata.Feature, "sections", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.Role, role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.ProjectProfileId, projectProfileId, StringComparison.Ordinal))
                return false;

            if (string.Equals(metadata.LogicalKey, logicalKey, StringComparison.Ordinal))
                return true;

            return allowLegacySourceHandle &&
                   !string.IsNullOrWhiteSpace(sourceClHandle) &&
                   !string.IsNullOrWhiteSpace(metadata.SourceClHandle) &&
                   string.Equals(metadata.SourceClHandle, sourceClHandle,
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Shared drawing resources (annotation layer, text style, section styles, the
    /// presentation style, office block definitions) are referenced by EVERY section.
    /// A selected-record APPLY may create a resource that does not exist yet — nothing
    /// references it, so no other section can change — but may never modify an
    /// existing one; a required change blocks. A batch APPLY normalizes as before.
    /// </summary>
    public static class SharedResourceLogic
    {
        public enum Mode
        {
            /// <summary>Batch: create or normalize.</summary>
            NormalizeAll,

            /// <summary>Selected: create when absent, validate when present, block on any delta.</summary>
            CreateOnlyNeverModify,
        }

        public enum Action
        {
            NoOp,
            Create,
            Modify,
            Block,
        }

        public static Action Decide(bool exists, bool matchesContract, Mode mode)
        {
            if (!exists) return Action.Create;
            if (matchesContract) return Action.NoOp;
            return mode == Mode.NormalizeAll ? Action.Modify : Action.Block;
        }
    }

    /// <summary>
    /// Axis-aligned overlap between SectionView extents, fail-closed: a view whose
    /// extents are non-finite, inverted or of zero area cannot be proven separate.
    /// The SELECTED view itself invalid ⇒ the check fails; another view invalid ⇒
    /// it is reported as unmeasurable; neither is ever counted as "no overlap".
    /// </summary>
    public static class LayoutOverlapLogic
    {
        public readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
        {
            public bool IsValid =>
                double.IsFinite(MinX) && double.IsFinite(MinY) &&
                double.IsFinite(MaxX) && double.IsFinite(MaxY) &&
                MaxX > MinX && MaxY > MinY;
        }

        public sealed record Report(
            bool SelfValid,
            IReadOnlyList<string> CollidingIds,
            IReadOnlyList<string> UnmeasurableIds)
        {
            public bool IsClean => SelfValid && CollidingIds.Count == 0 && UnmeasurableIds.Count == 0;
        }

        /// <summary>Both boxes must be valid; the caller classifies invalid ones first.</summary>
        public static bool Overlaps(Box a, Box b, double tolerance = 1e-9)
        {
            if (!a.IsValid || !b.IsValid)
                throw new ArgumentException("Overlap is defined only for valid boxes; classify invalid boxes first.");
            var separated = a.MaxX <= b.MinX + tolerance ||
                            b.MaxX <= a.MinX + tolerance ||
                            a.MaxY <= b.MinY + tolerance ||
                            b.MaxY <= a.MinY + tolerance;
            return !separated;
        }

        public static Report Inspect(
            Box self, IEnumerable<(string Id, Box Box)> others, double tolerance = 1e-9)
        {
            var colliding = new List<string>();
            var unmeasurable = new List<string>();
            var selfValid = self.IsValid;
            foreach (var (id, box) in others)
            {
                if (!box.IsValid) { unmeasurable.Add(id); continue; }
                if (selfValid && Overlaps(self, box, tolerance)) colliding.Add(id);
            }
            colliding.Sort(StringComparer.Ordinal);
            unmeasurable.Sort(StringComparer.Ordinal);
            return new Report(selfValid, colliding, unmeasurable);
        }
    }
}
