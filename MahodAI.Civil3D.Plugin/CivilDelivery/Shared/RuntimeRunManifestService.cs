using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Shared
{
    /// <summary>One file input and, when already proven by its producer, its hash.</summary>
    internal sealed record RunManifestInput(string? Path, string? Hash = null);

    internal sealed record RunManifestArtifactInput(string Path, string ExpectedHash);

    internal sealed record PublishedArtifactProof(
        string Path, string Hash, string ProducerRunId, string Feature, string Operation);

    /// <summary>
    /// One provenance path for every Civil Delivery route.  Direct commands and AI
    /// tools supply operation-specific counts/findings/inputs; this service owns the
    /// runtime binary identity, artifact inventory, file hashing and serialization.
    /// </summary>
    internal static class RuntimeRunManifestService
    {
        private sealed record ArtifactEvidence(
            string PublishedPath, string PhysicalPath, string? ExpectedHash = null);

        internal static string Write(
            string runId,
            string feature,
            string operation,
            string? projectProfileId,
            string? projectProfileHash,
            DeliveryStatus status,
            IReadOnlyDictionary<string, int> counts,
            IEnumerable<DeliveryFinding> findings,
            IEnumerable<RunManifestInput> inputs,
            IEnumerable<RunManifestArtifactInput>? relatedArtifacts = null,
            string? scope = null,
            string? selectedRecordId = null,
            string? runsRoot = null,
            string? publishedRunsRoot = null)
        {
            var identity = PluginRuntimeIdentityResolver.FromAssembly(
                typeof(RuntimeRunManifestService).Assembly);
            var runArtifacts = ExistingRunArtifacts(runId, runsRoot, publishedRunsRoot).ToList();
            var related = (relatedArtifacts ?? Enumerable.Empty<RunManifestArtifactInput>())
                .Select(item =>
                {
                    if (string.IsNullOrWhiteSpace(item.Path) ||
                        !IsSha256(item.ExpectedHash))
                        throw new InvalidDataException(
                            "Related evidence requires a path and an exact SHA-256.");
                    var fullPath = Path.GetFullPath(item.Path);
                    return new ArtifactEvidence(fullPath, fullPath, item.ExpectedHash);
                })
                .ToList();
            var artifacts = runArtifacts.Concat(related).ToList();
            var manifest = Compose(
                runId, feature, operation, projectProfileId, projectProfileHash,
                status, counts, findings, inputs,
                artifacts.Select(artifact => artifact.PublishedPath), identity, TryCivilVersion(),
                scope, selectedRecordId);
            foreach (var artifact in artifacts
                         .GroupBy(item => item.PublishedPath, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                if (!File.Exists(artifact.PhysicalPath))
                    throw new FileNotFoundException(
                        "Required evidence artifact is missing; manifest publication was refused.",
                        artifact.PhysicalPath);
                var actualHash = ArtifactHash.Sha256OfFile(artifact.PhysicalPath);
                if (!string.IsNullOrWhiteSpace(artifact.ExpectedHash) &&
                    !string.Equals(artifact.ExpectedHash, actualHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Related evidence changed after it was proven: {artifact.PublishedPath}");
                manifest.ArtifactHashes[artifact.PublishedPath] = actualHash;
            }
            if (manifest.ArtifactHashes.Count != manifest.Artifacts.Count)
                throw new IOException(
                    "Not every manifest artifact received an exact SHA-256 digest.");
            return RunManifestWriter.Write(manifest, runsRoot);
        }

        /// <summary>
        /// Proves a prerequisite against both its producer manifest and the exact
        /// object that the downstream operation is about to consume. Re-hashing a
        /// modified file into a new manifest can therefore never make it green.
        /// </summary>
        internal static PublishedArtifactProof RequirePublishedArtifact<T>(
            string runId,
            string artifactName,
            T payload,
            JsonSerializerOptions serializerOptions,
            string feature,
            params string[] allowedOperations)
        {
            var artifactPath = ArtifactPath(runId, artifactName);
            var manifestPath = ArtifactPath(runId, "run_manifest.json");
            if (!File.Exists(artifactPath) || !File.Exists(manifestPath))
                throw new FileNotFoundException(
                    "Required producer artifact or manifest is missing.", artifactPath);

            RunManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<RunManifest>(
                               File.ReadAllText(manifestPath), RunManifestWriter.JsonOptions)
                           ?? throw new InvalidDataException("Producer manifest is empty.");
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException("Producer manifest is unreadable.", ex);
            }

            var fullArtifactPath = Path.GetFullPath(artifactPath);
            var actualHash = ArtifactHash.Sha256OfFile(fullArtifactPath);
            var payloadHash = ArtifactHash.Sha256OfText(
                JsonSerializer.Serialize(payload, serializerOptions));
            var allowed = allowedOperations.Any(operation => string.Equals(
                operation, manifest.Operation, StringComparison.Ordinal));
            var listed = manifest.Artifacts.Any(path => string.Equals(
                Path.GetFullPath(path), fullArtifactPath, StringComparison.OrdinalIgnoreCase));
            var recordedHash = manifest.ArtifactHashes
                .FirstOrDefault(pair => string.Equals(
                    Path.GetFullPath(pair.Key), fullArtifactPath,
                    StringComparison.OrdinalIgnoreCase)).Value;

            if (manifest.SchemaVersion < 2 ||
                !string.Equals(manifest.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(manifest.Feature, feature, StringComparison.Ordinal) ||
                !allowed || !listed || !IsSha256(recordedHash) ||
                !string.Equals(recordedHash, actualHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(payloadHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Producer evidence does not match the consumed {artifactName} object.");

            return new PublishedArtifactProof(
                fullArtifactPath, actualHash, manifest.RunId,
                manifest.Feature, manifest.Operation);
        }

        internal static RunManifest Compose(
            string runId,
            string feature,
            string operation,
            string? projectProfileId,
            string? projectProfileHash,
            DeliveryStatus status,
            IReadOnlyDictionary<string, int> counts,
            IEnumerable<DeliveryFinding> findings,
            IEnumerable<RunManifestInput> inputs,
            IEnumerable<string> artifacts,
            PluginRuntimeIdentity pluginIdentity,
            string? civilVersion,
            string? scope = null,
            string? selectedRecordId = null)
        {
            var now = DateTime.UtcNow;
            var manifest = new RunManifest
            {
                RunId = runId,
                Feature = feature,
                Operation = operation,
                Scope = scope,
                SelectedRecordId = selectedRecordId,
                StartedAtUtc = now,
                CompletedAtUtc = now,
                CivilVersion = civilVersion,
                PluginGitSha = pluginIdentity.GitSha,
                PluginBuildVersion = pluginIdentity.BuildVersion,
                PluginPackageRevision = pluginIdentity.PackageRevision,
                PluginAssemblySha256 = pluginIdentity.AssemblySha256,
                ProjectProfileId = projectProfileId,
                ProjectProfileHash = projectProfileHash,
                ResultStatus = status,
            };

            var seenDrawings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Every input is re-proven against its file, but each file is read once per
            // manifest: the estimate scan of the full 6422 working copy supplied one
            // input per record (74,900) and this loop hashed the 51 MB host DWG and the
            // XREFs on P:\ for every one of them — 31 minutes per manifest (live 07/09
            // 18:25 -> 18:56, then again for the mapping proposals).
            var presence = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var provenHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool Exists(string candidate)
            {
                if (!presence.TryGetValue(candidate, out var exists))
                    presence[candidate] = exists = File.Exists(candidate);
                return exists;
            }
            string HashOnce(string candidate)
            {
                if (!provenHashes.TryGetValue(candidate, out var proven))
                    provenHashes[candidate] = proven = ArtifactHash.Sha256OfFile(candidate);
                return proven;
            }
            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Path))
                    throw new InvalidDataException(
                        "Manifest input has no path; evidence publication was refused.");
                var path = input.Path.Trim();
                string hash;
                if (!string.IsNullOrWhiteSpace(input.Hash))
                {
                    hash = input.Hash.Trim();
                    if (!IsSha256(hash))
                        throw new InvalidDataException(
                            $"Manifest input has an invalid SHA-256: {path}");
                    if (Exists(path))
                    {
                        var actual = HashOnce(path);
                        if (!string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                $"Manifest input changed before publication: {path}");
                    }
                }
                else
                {
                    if (!Exists(path))
                        throw new FileNotFoundException(
                            "Manifest input is missing and has no producer hash.", path);
                    hash = HashOnce(path);
                }

                if (manifest.InputHashesByPath.TryGetValue(path, out var existingHash) &&
                    !string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Manifest input path was supplied with conflicting hashes: {path}");
                manifest.InputHashesByPath[path] = hash;
                if (seenDrawings.Add(path)) manifest.InputDrawings.Add(path);
                if (seenHashes.Add(hash)) manifest.InputHashes.Add(hash);
            }

            foreach (var count in counts) manifest.RecordCounts[count.Key] = count.Value;
            foreach (var finding in findings
                         .GroupBy(f => f.FindingId, StringComparer.Ordinal)
                         .Select(g => g.First()))
                manifest.CountFinding(finding);
            manifest.Artifacts.AddRange(artifacts
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
            return manifest;
        }

        internal static string ArtifactPath(string runId, string name) =>
            Path.Combine(RunManifestWriter.DefaultRunsRoot, runId, name);

        private static IEnumerable<ArtifactEvidence> ExistingRunArtifacts(
            string runId, string? runsRoot = null, string? publishedRunsRoot = null)
        {
            var dir = Path.Combine(runsRoot ?? RunManifestWriter.DefaultRunsRoot, runId);
            // Artifact discovery is part of the evidence contract.  If the directory
            // exists but cannot be enumerated, publishing a manifest with an empty
            // artifact list would create a false-green bundle.  Let the exception reach
            // PersistEvidenceBundle so the complete pending run is discarded and the
            // authoritative result is downgraded to Failed.
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir)
                    .Where(path => !string.Equals(
                        Path.GetFileName(path), "run_manifest.json",
                        StringComparison.OrdinalIgnoreCase))
                    // Evidence bundles are assembled under a private sibling
                    // directory and then published with one directory rename.
                    // Record the authoritative post-rename path, never the
                    // disappearing .pending-* assembly path.
                    .Select(path => new ArtifactEvidence(
                        Path.Combine(
                            publishedRunsRoot ?? runsRoot ?? RunManifestWriter.DefaultRunsRoot,
                            runId,
                            Path.GetFileName(path)),
                        path))
                    .ToArray()
                : Array.Empty<ArtifactEvidence>();
        }

        private static bool IsSha256(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
            value.All(Uri.IsHexDigit);

        private static string? TryCivilVersion()
        {
            try
            {
                return Autodesk.AutoCAD.ApplicationServices.Core.Application.Version.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
