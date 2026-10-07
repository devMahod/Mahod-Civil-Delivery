using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The single owner of "what gets cut into a section".
    ///
    /// Creating a SampleLineGroup does not decide what a section contains. Civil exposes
    /// every eligible source (surfaces, corridors, pipe networks) on the group and
    /// samples only those flagged <c>IsSampled</c>. Without this step the views come out
    /// geometrically correct and professionally empty — the first thing Nataly would
    /// notice, because the existing utility systems are the point of the deliverable.
    ///
    /// Every natively sampled source claimed by the alignment-wide plan must be present
    /// and enabled. A mismatch aborts the atomic APPLY; producing a section that silently
    /// lacks a promised surface or utility would be worse than producing no section.
    ///
    /// Pressure pipe networks are absent by necessity: Civil 3D 2027's
    /// <c>SectionSourceType</c> has no pressure-network member (TinSurface, Grading,
    /// Corridor, CorridorSurface, PipeNetwork, Material, GridSurface), so they cannot be
    /// sampled natively. The coverage report carries that as unsupported-with-reason.
    /// </summary>
    public static class SectionSourceService
    {
        /// <summary>What sampling was actually turned on, for the evidence record.</summary>
        public sealed record Outcome(
            List<string> Enabled,
            List<string> AlreadyEnabled,
            List<string> Disabled,
            List<string> RequestedButAbsent,
            List<DeliveryFinding> Findings);

        /// <summary>
        /// Exact identity of one live sampled source. Names are retained for the Civil
        /// UI contract, but a name alone is not identity: surfaces, corridors and pipe
        /// networks can legally share text across their separate collections.
        /// </summary>
        public sealed record SampledSourceIdentity(
            string Name,
            string Kind,
            string Handle);

        /// <summary>
        /// Fail-closed VERIFY snapshot. <see cref="IsValid"/> is false whenever the
        /// native collection, an IsSampled flag, a sampled SourceId, its object/name,
        /// or the aborted read transaction cannot be proven. A partial list is never
        /// represented as a successful snapshot.
        /// </summary>
        public sealed record SampledSourceSnapshot(
            bool IsValid,
            IReadOnlyList<SampledSourceIdentity> Entries,
            string? Error)
        {
            public IReadOnlyList<string> Names => Entries.Select(entry => entry.Name).ToList();

            public static SampledSourceSnapshot Invalid(string error) =>
                new(false, Array.Empty<SampledSourceIdentity>(), error);
        }

        /// <summary>
        /// Enables sampling for this record's planned sources and expected utilities.
        /// Must be called BEFORE the sample line is created, otherwise the new sample
        /// line is cut against whatever the group happened to be sampling.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// A source in the exact native group plan could not be enabled or is not offered
        /// by the group. The caller aborts the whole batch — half-sampled sections are
        /// not a deliverable.
        /// </exception>
        public static Outcome EnableSampling(
            Transaction tr,
            CivilDb.SampleLineGroup group,
            SectionPlanRecord record,
            IReadOnlyCollection<string> exactGroupSourceNames,
            IReadOnlyCollection<string> requiredGroupSourceNames,
            StageLog? log = null,
            SamplingReconciliationLogic.Mode mode = SamplingReconciliationLogic.Mode.Batch)
        {
            var enabled = new List<string>();
            var already = new List<string>();
            var disabled = new List<string>();
            var findings = new List<DeliveryFinding>();

            var planned = record.PlannedSources.Where(s => s.PlannedState == "sampled").ToList();
            var utilities = record.UtilityCoverage?.Represented ?? new List<string>();
            var desired = new HashSet<string>(exactGroupSourceNames, StringComparer.OrdinalIgnoreCase);
            var requiredForGroup = new HashSet<string>(requiredGroupSourceNames, StringComparer.OrdinalIgnoreCase);

            log?.Begin("apply.section_sources",
                $"planned={planned.Count} utilities={utilities.Count} exact_group={desired.Count}");

            CivilDb.SectionSourceCollection sources;
            try
            {
                sources = group.GetSectionSources();
            }
            catch (Exception ex)
            {
                log?.Fail("apply.section_sources", ex);
                throw new InvalidOperationException(
                    $"Section sources could not be read from the sample line group: {ex.Message} " +
                    $"({SectionFindingCodes.SourceSamplingFailed})", ex);
            }

            // Names as the engineer sees them in Prospector; that is the only identity
            // shared by the profile, the coverage report and the drawing.
            var matchedUtilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedDesired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Decide the whole reconciliation BEFORE touching any source. Sampling is
            // a property of the SHARED group: toggling any source — enabling as much
            // as disabling — re-samples every sample line in it, i.e. every omitted
            // record's sections. A selected-record APPLY on a shared group therefore
            // proceeds only when nothing has to change, and refuses up front otherwise
            // (1.2.24 review, 02/09).
            var currentSampling = new List<(string? Name, bool Sampled)>();
            foreach (CivilDb.SectionSource probe in sources)
                currentSampling.Add((ResolveName(tr, probe), probe.IsSampled));
            var reconciliation = SamplingReconciliationLogic.Decide(currentSampling, desired, mode);
            if (reconciliation.IsBlocked)
            {
                log?.End("apply.section_sources",
                    $"refused mode={mode} delta={string.Join(",", reconciliation.Blocked)}");
                throw new InvalidOperationException(
                    "קבוצת הדגימה משותפת לחתכים נוספים ומקורותיה היו משתנים (" +
                    string.Join(", ", reconciliation.Blocked) +
                    ") — החלת חתך נבחר אינה מדגימה מחדש חתכים אחרים באותה קבוצה. יש להריץ החלת " +
                    "אצווה שתיישר את הקבוצה, או ליישר את מקורות הדגימה ידנית " +
                    $"({SectionFindingCodes.GroupSamplingForeign})");
            }
            log?.Info($"apply.section_sources.plan mode={mode} enable={reconciliation.ToEnable.Count} " +
                      $"disable={reconciliation.ToDisable.Count} noop={reconciliation.IsNoOp}");
            if (mode is SamplingReconciliationLogic.Mode.SelectedSharedGroup or
                SamplingReconciliationLogic.Mode.BatchSharedGroup)
            {
                // Not blocked ⇒ the group already samples exactly the plan. Return before
                // any setter or second pass: a group containing work outside this exact
                // APPLY scope is never touched, whether the scope is selected or batch.
                if (reconciliation.MissingDesired.Count > 0)
                    throw new InvalidOperationException(
                        "Planned section sources missing from group sources: " +
                        string.Join(", ", reconciliation.MissingDesired) +
                        $" ({SectionFindingCodes.SourceMissing})");
                log?.End("apply.section_sources",
                    $"shared-group no-op mode={mode} already={reconciliation.AlreadySampled.Count}");
                return new Outcome(enabled, reconciliation.AlreadySampled.ToList(), disabled,
                    new List<string>(), findings);
            }

            foreach (CivilDb.SectionSource source in sources)
            {
                var sourceName = ResolveName(tr, source);
                var displayName = sourceName ?? $"<unresolved {source.SourceType}>";

                // This is a tool-owned group. Reconciliation must converge old 1.2.x
                // wildcard sampling to the exact alignment-scoped plan, not merely
                // turn the desired pair on and leave eight foreign surfaces sampled.
                if (sourceName == null || !desired.Contains(sourceName))
                {
                    if (!source.IsSampled) continue;
                    try
                    {
                        source.IsSampled = false;
                        disabled.Add(displayName);
                        log?.End("apply.section_sources.disable", $"{displayName} ({source.SourceType})");
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected source '{displayName}' could not be disabled: {ex.Message} " +
                            $"({SectionFindingCodes.SourceSamplingFailed})", ex);
                    }
                    continue;
                }

                matchedDesired.Add(sourceName);

                var utilityMatch = utilities.FirstOrDefault(
                    u => string.Equals(u, sourceName, StringComparison.OrdinalIgnoreCase));

                if (source.IsSampled)
                {
                    already.Add(sourceName);
                    if (utilityMatch != null) matchedUtilities.Add(utilityMatch);
                    continue;
                }

                try
                {
                    source.IsSampled = true;
                    enabled.Add(sourceName);
                    if (utilityMatch != null) matchedUtilities.Add(utilityMatch);
                    log?.End("apply.section_sources.enable", $"{sourceName} ({source.SourceType})");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Planned source '{sourceName}' could not be sampled: {ex.Message} " +
                        $"({SectionFindingCodes.SourceSamplingFailed})", ex);
                }
            }

            // Every name in the exact native group plan must be offered. "Represented"
            // is a delivery claim, not an optional hint; committing without it and
            // hoping a later manual VERIFY catches the gap would leave a partial result.
            var missingDesired = desired
                .Where(name => !matchedDesired.Contains(name))
                .ToList();
            if (missingDesired.Count > 0)
            {
                var requiredMissing = missingDesired
                    .Where(requiredForGroup.Contains)
                    .ToList();
                throw new InvalidOperationException(
                    (requiredMissing.Count > 0
                        ? "Required section sources missing from group sources: "
                        : "Planned section sources missing from group sources: ") +
                    string.Join(", ", missingDesired) + $" ({SectionFindingCodes.SourceMissing})");
            }

            // No absent utility can reach this branch because every represented native
            // source belongs to the exact group plan above. Keep the evidence field for
            // artifact compatibility with earlier APPLY results.
            var absentUtilities = utilities.Where(u => !matchedUtilities.Contains(u)).ToList();
            foreach (var name in absentUtilities)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = SectionFindingCodes.SourceMissing,
                    Domain = SectionPlanLogic.Domain,
                    Severity = FindingSeverity.Warning,
                    Title = $"'{Bidi.Ltr(name)}' אינו זמין כמקור לחתך בשרטוט הזה",
                    Message =
                        "The coverage report expects it in this section, but Civil does not offer " +
                        "it on this sample line group, so it will not appear.",
                    RecommendedAction =
                        "Check the source exists in this drawing (or is XREF-attached and enabled), " +
                        "or correct the project profile.",
                });
            }

            log?.End("apply.section_sources",
                $"enabled={enabled.Count} already={already.Count} disabled={disabled.Count} " +
                $"absent={absentUtilities.Count}");

            return new Outcome(enabled, already, disabled, absentUtilities, findings);
        }

        /// <summary>
        /// Reads back which sources are sampled. VERIFY uses this rather than trusting
        /// what APPLY believed it did.
        /// </summary>
        public static List<string> SampledSourceNames(Transaction tr, CivilDb.SampleLineGroup group)
        {
            // APPLY uses this read-back inside its atomic transaction. Throwing is
            // intentional: a partial/unresolved read must roll the whole APPLY back,
            // never become apparently complete evidence.
            return ReadSampledSourcesStrict(tr, group)
                .Select(entry => entry.Name)
                .ToList();
        }

        /// <summary>
        /// Reads sampled source names without persisting any database change.
        ///
        /// Civil 3D 2027's <see cref="CivilDb.SampleLineGroup.GetSectionSources"/>
        /// has a native write-open precondition even though enumerating the returned
        /// collection is logically a read. Calling it on a group opened ForRead raises
        /// AutoCAD's fatal <c>eNotOpenForWrite</c> assertion rather than a safely
        /// catchable managed exception. Keep that unusual API requirement confined to
        /// this short nested transaction and always abort it so a Civil lazy update can
        /// never turn VERIFY into a drawing mutation.
        /// </summary>
        public static SampledSourceSnapshot SampledSourceNamesReadOnly(
            Database db, ObjectId groupId)
        {
            if (groupId.IsNull || groupId.IsErased)
                return SampledSourceSnapshot.Invalid("SampleLineGroup id is null or erased.");

            using var sourceTr = db.TransactionManager.StartTransaction();
            SampledSourceSnapshot snapshot;
            try
            {
                var group = sourceTr.GetObject(
                    groupId, OpenMode.ForWrite, openErased: false) as CivilDb.SampleLineGroup
                    ?? throw new InvalidOperationException(
                        $"Object {groupId.Handle} is not a readable SampleLineGroup.");
                snapshot = new SampledSourceSnapshot(
                    true, ReadSampledSourcesStrict(sourceTr, group), null);
            }
            catch (Exception ex)
            {
                snapshot = SampledSourceSnapshot.Invalid(ex.Message);
            }

            try
            {
                sourceTr.Abort();
            }
            catch (Exception ex)
            {
                return SampledSourceSnapshot.Invalid(
                    "The VERIFY source-read transaction could not be aborted: " + ex.Message);
            }
            return snapshot;
        }

        private static IReadOnlyList<SampledSourceIdentity> ReadSampledSourcesStrict(
            Transaction tr, CivilDb.SampleLineGroup group)
        {
            CivilDb.SectionSourceCollection sources;
            try
            {
                sources = group.GetSectionSources();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"SampleLineGroup {group.Handle} sources could not be enumerated.", ex);
            }

            var entries = new List<SampledSourceIdentity>();
            try
            {
                foreach (CivilDb.SectionSource source in sources)
                {
                    bool sampled;
                    try { sampled = source.IsSampled; }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"A source in SampleLineGroup {group.Handle} has an unreadable IsSampled flag.", ex);
                    }
                    if (!sampled) continue;
                    entries.Add(ResolveSampledIdentityStrict(tr, source));
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"SampleLineGroup {group.Handle} source enumeration stopped before completion.", ex);
            }

            var duplicateNames = entries
                .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Where(grouping => grouping.Count() != 1)
                .Select(grouping => grouping.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (duplicateNames.Count > 0)
                throw new InvalidOperationException(
                    "Sampled source names are not unique, so name-based plan evidence is ambiguous: " +
                    string.Join(", ", duplicateNames));

            return entries
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.Handle, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static SampledSourceIdentity ResolveSampledIdentityStrict(
            Transaction tr, CivilDb.SectionSource source)
        {
            ObjectId sourceId;
            CivilDb.SectionSourceType sourceType;
            string? sourceName;
            try
            {
                sourceId = source.SourceId;
                sourceType = source.SourceType;
                sourceName = source.SourceName;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "A sampled source has unreadable identity properties.", ex);
            }

            if (sourceId.IsNull || sourceId.IsErased)
                throw new InvalidOperationException(
                    "A sampled source points to a null or erased source object.");

            return ResolveLiveSourceIdentityStrict(
                tr, sourceId, sourceType, sourceName, "sampled source");
        }

        /// <summary>
        /// One source-identity reader shared by the group snapshot and its generated
        /// Section children. Civil exposes SourceName/SourceType/SourceId on both;
        /// requiring the exact triple is the one-to-one contract behind
        /// SampleLine.GetSectionId(sourceId).
        /// </summary>
        /// <summary>
        /// The Section types that sample a surface. CorridorSurface was missing from every surface
        /// filter, so a corridor top planned as the design surface was never found and APPLY rolled
        /// back (MahodAI live test, 2026-09-30).
        /// </summary>
        internal static bool IsSurfaceSection(CivilDb.SectionSourceType sourceType) =>
            sourceType is CivilDb.SectionSourceType.TinSurface or CivilDb.SectionSourceType.GridSurface or CivilDb.SectionSourceType.CorridorSurface;

        /// <summary>
        /// The corridor surface a Section named "&lt;corridor&gt; &lt;surface&gt;" samples, as the
        /// surface object PLAN knows (its own name and handle), or null.
        /// </summary>
        internal static SampledSourceIdentity? ResolveCorridorSurface(
            Transaction tr, CivilDb.Corridor corridor, string sectionSourceName)
        {
            var name = sectionSourceName.Trim();
            foreach (CivilDb.CorridorSurface corridorSurface in corridor.CorridorSurfaces)
            {
                if (!string.Equals(name, corridor.Name + " " + corridorSurface.Name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, corridorSurface.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (corridorSurface.SurfaceId.IsNull ||
                    tr.GetObject(corridorSurface.SurfaceId, OpenMode.ForRead) is not CivilDb.Surface surface)
                    return null;
                return new SampledSourceIdentity(surface.Name, "surface", surface.Handle.ToString());
            }
            return null;
        }

        internal static SampledSourceIdentity ResolveLiveSourceIdentityStrict(
            Transaction tr,
            ObjectId sourceId,
            CivilDb.SectionSourceType sourceType,
            string? sourceName,
            string context)
        {
            if (sourceId.IsNull || sourceId.IsErased)
                throw new InvalidOperationException(
                    $"A {context} points to a null or erased source object.");
            if (string.IsNullOrWhiteSpace(sourceName))
                throw new InvalidOperationException(
                    $"A {context} has no readable Civil SourceName.");

            DBObject obj;
            try
            {
                obj = tr.GetObject(sourceId, OpenMode.ForRead, openErased: false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The {context} object {sourceId.Handle} could not be opened.", ex);
            }

            // A corridor-surface Section's SourceId is the CORRIDOR and its SourceName is
            // "<corridor> <surface>"; PLAN planned the surface itself, so resolve to that.
            if (sourceType == CivilDb.SectionSourceType.CorridorSurface && obj is CivilDb.Corridor corridor)
                return ResolveCorridorSurface(tr, corridor, sourceName)
                    ?? throw new InvalidOperationException(
                        $"The {context} '{sourceName}' names no surface of corridor '{corridor.Name}'.");

            var typeToken = sourceType.ToString();
            var kind = typeToken.Contains("PipeNetwork", StringComparison.OrdinalIgnoreCase)
                ? "pipe-network"
                : typeToken.Contains("CorridorSurface", StringComparison.OrdinalIgnoreCase)
                    ? "surface"
                    : typeToken.Contains("Corridor", StringComparison.OrdinalIgnoreCase)
                    ? "corridor"
                    : typeToken.Contains("Surface", StringComparison.OrdinalIgnoreCase)
                        ? "surface"
                        : null;
            if (kind == null)
                throw new InvalidOperationException(
                    $"The {context} '{sourceName}' has unsupported SectionSourceType '{typeToken}'.");

            return new SampledSourceIdentity(
                sourceName.Trim(), kind, obj.Handle.ToString());
        }

        private static string? ResolveName(Transaction tr, CivilDb.SectionSource source)
        {
            try
            {
                var obj = tr.GetObject(source.SourceId, OpenMode.ForRead);
                return obj switch
                {
                    CivilDb.Surface s => s.Name,
                    CivilDb.Corridor c => c.Name,
                    CivilDb.Network n => n.Name,
                    _ => obj.GetType().Name,
                };
            }
            catch { return null; }
        }
    }
}
