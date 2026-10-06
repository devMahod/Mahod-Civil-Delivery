using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Diagnostic-only publication of the detached measured-layout inputs that one
    /// selected-record VERIFY handed to the existing solver. VERIFY builds the record
    /// from values it already read; this class never re-reads the drawing, solves a
    /// layout, changes a check or a status, or writes outside the private pending
    /// bundle of that same VERIFY run, whose manifest then lists and hashes the file.
    /// </summary>
    internal static class SectionLayoutInputCapture
    {
        internal const string ArtifactName = "canonical_layout_inputs.json";

        internal const string Captured = "captured";
        internal const string NotCaptured = "not-captured";

        // layout_stage names where the unchanged VERIFY layout path stopped. Only the
        // last one is the check's own PASS; a captured snapshot alone never is.
        internal const string RefusedBeforeCapture = "refused-before-capture";
        internal const string ComputeFailedAfterCapture = "compute-failed-after-capture";
        internal const string NativeReadbackMismatch = "native-readback-mismatch";
        internal const string LayoutExact = "layout-exact";

        // The run-artifact writer settings, except that a non-finite number would be
        // written literally instead of failing the bundle or being rounded away.
        private static readonly JsonSerializerOptions Json = new(SectionsWorkflowService.Json)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        /// <summary>
        /// Pure projection used by selected-record VERIFY after its unchanged layout
        /// check. <paramref name="captured"/> is null when a guard stopped before the
        /// solver boundary; inputs and counts then stay null with the real error.
        /// <paramref name="output"/> is non-null exactly when the layout was computed.
        /// </summary>
        internal static SectionLayoutInputCaptureRecord ForRecord(
            string recordId, string? logicalKey, string? sectionViewHandle,
            IReadOnlyList<SectionLayoutAnnotationMetadata>? metadata, string? registryError,
            SectionMeasuredLayoutInputs? captured, SectionLayoutCapturedOutput? output,
            string? layoutError) => new()
        {
            RecordId = recordId,
            LogicalKey = logicalKey,
            SectionViewHandle = sectionViewHandle,
            LayoutStage = captured == null ? RefusedBeforeCapture
                : output == null ? ComputeFailedAfterCapture
                : layoutError != null ? NativeReadbackMismatch
                : LayoutExact,
            // Mirrors native_measured_label_layout_exact exactly.
            LayoutCheckPass = output != null && layoutError == null,
            LayoutError = layoutError,
            AnnotationRegistryError = metadata == null ? registryError ?? "missing entry" : null,
            Inputs = captured == null ? null : Inputs(captured),
            Counts = captured == null ? null : Counts(captured, metadata),
            LabelMetadata = metadata?.ToList(),
            LayoutOutput = output,
        };

        /// <summary>
        /// Copies display metadata that the registry read already holds (no getter on
        /// a native object) and joins APPLY CoreSemantics by exact handle. EntityId,
        /// BlockDefinitionId and every other native reference are left out.
        /// </summary>
        internal static List<SectionLayoutAnnotationMetadata> Metadata(
            IEnumerable<LiveSectionAnnotationEvidence> entries,
            IReadOnlyList<SectionCoreAnnotationEvidence> coreAnnotations) =>
            entries.Select(entry => new SectionLayoutAnnotationMetadata
            {
                Handle = entry.Handle,
                EntityType = entry.EntityType,
                Text = entry.Text,
                TextStyleName = entry.TextStyleName,
                TextHeight = entry.TextHeight,
                TextRotation = entry.TextRotation,
                TextWidthFactor = entry.TextWidthFactor,
                TextOblique = entry.TextOblique,
                TextPosition = entry.TextPosition?.ToArray(),
                TextAlignmentPoint = entry.TextAlignmentPoint?.ToArray(),
                TextHorizontalMode = entry.TextHorizontalMode,
                TextVerticalMode = entry.TextVerticalMode,
                TextMirroredInX = entry.TextMirroredInX,
                TextMirroredInY = entry.TextMirroredInY,
                CoreSemantics = coreAnnotations
                    .Where(core => string.Equals(core.Handle, entry.Handle, StringComparison.OrdinalIgnoreCase))
                    .Select(core => new SectionLayoutCoreSemantic { Kind = core.Kind, SemanticKey = core.SemanticKey })
                    .ToList(),
            }).ToList();

        /// <summary>Solver output by value, kept apart from the captured inputs.</summary>
        internal static SectionLayoutCapturedOutput Output(
            SectionAnnotationPlacementContract.MeasuredLabelLayout layout) => new()
        {
            PlacedLabels = layout.ExpectedPositionsByHandle.Select(pair => new SectionLayoutPlacedLabel
            {
                Handle = pair.Key,
                ExpectedPosition = new[] { pair.Value.X, pair.Value.Y, pair.Value.Z },
                FinalBounds = layout.FinalBoundsByHandle.TryGetValue(pair.Key, out var bounds)
                    ? Bounds(bounds) : null,
            }).ToList(),
            OverallBounds = Bounds(layout.OverallBounds),
            Leaders = layout.Leaders.Select(leader => new SectionLayoutCapturedLeader
            {
                Start = new[] { leader.Start.X, leader.Start.Y, leader.Start.Z },
                End = new[] { leader.End.X, leader.End.Y, leader.End.Z },
            }).ToList(),
            Clearance = layout.Clearance,
        };

        /// <summary>
        /// Pure envelope over the published VERIFY verdict. Without a record the reason
        /// says where VERIFY stopped; no array is invented for a capture that never ran.
        /// </summary>
        internal static SectionLayoutInputCaptureArtifact Compose(
            SectionLayoutInputCaptureIdentity identity, SectionVerifyResult result)
        {
            var record = result.LayoutInputCapture;
            var selected = result.Records.Where(item => string.Equals(
                item.RecordId, result.SelectedRecordId, StringComparison.Ordinal)).ToList();
            var recordResult = selected.Count == 1 ? selected[0] : null;
            var reason = record == null
                ? result.Records.Count == 0
                    ? $"VERIFY returned before per-record read-back (status={result.Status}); " +
                      "the measured-layout boundary was not reached."
                    : "VERIFY did not reach the measured-layout boundary of the selected record; see verify.failed_checks."
                : record.Inputs == null
                    ? record.LayoutError ?? "The measured-layout input observer was not invoked."
                    : null;
            return new SectionLayoutInputCaptureArtifact
            {
                CaptureStatus = record?.Inputs != null ? Captured : NotCaptured,
                CaptureReason = reason,
                Identity = identity,
                Verify = new SectionLayoutInputCaptureVerifyOutcome
                {
                    Scope = result.Scope,
                    VerifyStatus = result.Status.ToString(),
                    RecordStatus = recordResult?.Status.ToString(),
                    FailedChecks = recordResult?.Checks.Where(check => !check.Pass)
                        .Select(check => check.Check).ToList(),
                    ErrorFindings = result.Findings
                        .Where(finding => finding.Severity == FindingSeverity.Error)
                        .Select(finding => finding.Code).Distinct(StringComparer.Ordinal).ToList(),
                },
                Record = record,
            };
        }

        /// <summary>
        /// Identities of this publication. Hash failures are data, not exceptions: the
        /// run manifest independently re-hashes the host and refuses publication if it
        /// cannot. Assembly identity is the same best-effort resolver the manifest uses.
        /// </summary>
        internal static SectionLayoutInputCaptureIdentity Identity(
            Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult applied, SectionVerifyResult result)
        {
            string? hostPath = null;
            try { hostPath = doc.Database.Filename; } catch { }
            string? hostHash = null;
            string? hostHashError = null;
            if (string.IsNullOrWhiteSpace(hostPath))
                hostHashError = "Host drawing path is unavailable.";
            else if (!File.Exists(hostPath))
                hostHashError = "Host drawing is not a saved file on disk.";
            else
            {
                try { hostHash = ArtifactHash.Sha256OfFile(hostPath); }
                catch (Exception ex) { hostHashError = $"{ex.GetType().Name}: {ex.Message}"; }
            }
            var plugin = typeof(SectionLayoutInputCapture).Assembly;
            var core = typeof(SectionAnnotationPlacementLogic).Assembly;
            var pluginIdentity = PluginRuntimeIdentityResolver.FromAssembly(plugin);
            var coreIdentity = PluginRuntimeIdentityResolver.FromAssembly(core);
            return new SectionLayoutInputCaptureIdentity
            {
                HostPath = hostPath,
                HostSha256 = hostHash,
                HostHashError = hostHashError,
                PostApplyDatabaseRevision = applied.PostApplyDatabaseRevision,
                VerifiedDatabaseRevision = result.VerifiedDatabaseRevision,
                PluginAssemblyPath = LoadedFrom(plugin),
                PluginAssemblySha256 = pluginIdentity.AssemblySha256,
                PluginBuildVersion = pluginIdentity.BuildVersion,
                PluginGitSha = pluginIdentity.GitSha,
                PluginPackageRevision = pluginIdentity.PackageRevision,
                CoreAssemblyPath = LoadedFrom(core),
                CoreAssemblySha256 = coreIdentity.AssemblySha256,
                CoreBuildVersion = coreIdentity.BuildVersion,
                ProjectProfileId = profile.ProfileId,
                ProjectProfileSha256 = profileHash,
                PlanRunId = plan.RunId,
                ApplyRunId = applied.RunId,
                VerifyRunId = result.RunId,
                SelectedRecordId = result.SelectedRecordId,
            };
        }

        /// <summary>
        /// Called only by SectionsWorkflowService.PersistVerifyEvidence for a
        /// selected-record result, inside PersistEvidenceBundle after verify_result.json
        /// and before the manifest, so the file exists only as part of that bundle.
        /// </summary>
        internal static string Stage(
            string pendingRoot, Document doc, ProjectProfile profile, string? profileHash,
            SectionPlan plan, SectionApplyResult applied, SectionVerifyResult result) =>
            Write(pendingRoot, result.RunId,
                Compose(Identity(doc, profile, profileHash, plan, applied, result), result));

        internal static string Write(
            string pendingRoot, string runId, SectionLayoutInputCaptureArtifact artifact)
        {
            // Never a standalone or post-publication write: only the private
            // .pending-* root that PersistEvidenceBundle publishes as a whole.
            if (string.IsNullOrWhiteSpace(pendingRoot) ||
                !Path.GetFileName(Path.TrimEndingDirectorySeparator(pendingRoot))
                    .StartsWith(".pending-", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Layout input capture is staged only inside a pending evidence bundle.");
            var directory = Path.Combine(pendingRoot, runId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ArtifactName);
            AtomicTextFile.WriteAllText(path, JsonSerializer.Serialize(artifact, Json));
            return path;
        }

        private static SectionLayoutCapturedInputs Inputs(SectionMeasuredLayoutInputs captured) => new()
        {
            TopLabels = captured.TopLabels.Select(Label).ToList(),
            BottomLabels = captured.BottomLabels.Select(Label).ToList(),
            FixedObstacles = captured.FixedObstacles.Select(Bounds).ToList(),
            MedianTextHeight = captured.MedianTextHeight,
            Clearance = captured.Clearance,
            MaximumRise = captured.MaximumRise,
        };

        private static SectionLayoutCaptureCounts Counts(
            SectionMeasuredLayoutInputs captured,
            IReadOnlyList<SectionLayoutAnnotationMetadata>? metadata)
        {
            var ids = captured.TopLabels.Concat(captured.BottomLabels).Select(label => label.Id).ToList();
            var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Report the join as found: a missing or repeated handle stays visible.
            var perHandle = metadata?.GroupBy(entry => entry.Handle, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            return new SectionLayoutCaptureCounts
            {
                TopLabels = captured.TopLabels.Count,
                BottomLabels = captured.BottomLabels.Count,
                MovableLabels = ids.Count,
                UniqueLabelIds = distinct.Count,
                DuplicateLabelIds = ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToList(),
                FixedObstacles = captured.FixedObstacles.Count,
                RegisteredAnnotations = metadata?.Count,
                LabelIdsWithSingleMetadata = perHandle == null ? null
                    : distinct.Count(id => perHandle.TryGetValue(id, out var count) && count == 1),
                LabelIdsWithoutMetadata = perHandle == null ? null
                    : distinct.Where(id => !perHandle.ContainsKey(id)).ToList(),
                LabelIdsWithDuplicateMetadata = perHandle == null ? null
                    : distinct.Where(id => perHandle.TryGetValue(id, out var count) && count > 1).ToList(),
            };
        }

        private static SectionLayoutCapturedLabel Label(SectionAnnotationPlacementLogic.LabelBox label) => new()
        {
            Id = label.Id,
            Band = label.Band,
            Anchor = new SectionLayoutCapturedPoint { X = label.Anchor.X, Y = label.Anchor.Y },
            Ink = Bounds(label.Ink),
        };

        private static SectionLayoutCapturedBounds Bounds(SectionAnnotationPlacementLogic.Bounds bounds) => new()
        {
            MinX = bounds.MinX,
            MinY = bounds.MinY,
            MaxX = bounds.MaxX,
            MaxY = bounds.MaxY,
        };

        private static string? LoadedFrom(Assembly assembly)
        {
            try { return string.IsNullOrWhiteSpace(assembly.Location) ? null : assembly.Location; }
            catch { return null; }
        }
    }
}
