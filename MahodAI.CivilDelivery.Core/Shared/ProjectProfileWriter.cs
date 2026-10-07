using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Persists engineer-approved project configuration back into the versioned
    /// ProjectProfile YAML — with provenance, a bumped version, and a timestamped
    /// backup of the previous file. Approvals are durable project data, never
    /// hidden machine-local state (locked plan §6.2).
    /// </summary>
    public static class ProjectProfileWriter
    {
        public sealed record SaveResult(string Path, string BackupPath, int NewVersion, string NewHash);

        /// <summary>
        /// Compare-and-swap evidence captured when a profile-backed workflow starts.
        /// Source and target are deliberately separate: an installed machine may load
        /// a repository default while its writable runtime target does not yet exist.
        /// </summary>
        public sealed record ExpectedProfileState(
            string SourcePath,
            string SourceHash,
            string TargetPath,
            bool TargetExisted,
            string? TargetHash,
            bool SourceExisted,
            string? SourceEffectiveHash);

        public static ExpectedProfileState CaptureExpectedState(
            string sourcePath, string sourceHash, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !Path.IsPathFullyQualified(sourcePath))
                throw new ArgumentException("An absolute profile source path is required.", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(targetPath) ||
                !Path.IsPathFullyQualified(targetPath))
                throw new ArgumentException("An absolute profile target path is required.", nameof(targetPath));
            if (string.IsNullOrWhiteSpace(sourceHash))
                throw new ArgumentException("The loaded profile hash is required.", nameof(sourceHash));

            var source = Path.GetFullPath(sourcePath);
            var target = Path.GetFullPath(targetPath);
            if (!File.Exists(source))
                throw new FileNotFoundException("The loaded profile source no longer exists.", source);
            var actualSourceHash = HashProfileFile(source);
            if (!string.Equals(actualSourceHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The loaded profile source bytes no longer match their supplied SHA-256.");

            var targetExists = File.Exists(target);
            return new ExpectedProfileState(
                source, sourceHash, target, targetExists,
                targetExists ? HashProfileFile(target) : null,
                SourceExisted: true,
                SourceEffectiveHash: null);
        }

        /// <summary>
        /// Captures compare-and-swap evidence for a drawing-derived, in-memory setup
        /// seed whose authoritative runtime target does not yet exist.  The seed is
        /// not published as an approved profile merely to make discovery possible;
        /// its exact effective hash and the target's absence are instead pinned until
        /// the engineer explicitly saves setup.
        /// </summary>
        public static ExpectedProfileState CaptureExpectedGeneratedState(
            ProjectProfile seedProfile,
            string sourceHash,
            string targetPath)
        {
            ArgumentNullException.ThrowIfNull(seedProfile);
            if (!CatalogIdentity.IsValidSha256(sourceHash))
                throw new ArgumentException(
                    "The generated profile seed requires its exact SHA-256.",
                    nameof(sourceHash));
            if (string.IsNullOrWhiteSpace(targetPath) ||
                !Path.IsPathFullyQualified(targetPath))
                throw new ArgumentException(
                    "An absolute profile target path is required.", nameof(targetPath));

            var target = Path.GetFullPath(targetPath);
            var targetExists = File.Exists(target);
            if (targetExists)
                throw new InvalidOperationException(
                    "A generated setup seed is invalid because its runtime profile target already exists; reload that profile instead.");

            return new ExpectedProfileState(
                target,
                sourceHash,
                target,
                TargetExisted: false,
                TargetHash: null,
                SourceExisted: false,
                SourceEffectiveHash: EstimateTraceIdentity.EffectiveProfileHash(seedProfile));
        }

        /// <summary>
        /// Proves that an unpublished setup seed is still the exact in-memory object
        /// captured at discovery/load time.  Callers invoke this before applying the
        /// engineer's intended mutation; the ordinary target CAS is repeated by
        /// <see cref="Save"/> immediately before publication.
        /// </summary>
        public static void RequireGeneratedSourceUnchanged(
            ProjectProfile seedProfile, ExpectedProfileState expectedState)
        {
            ArgumentNullException.ThrowIfNull(seedProfile);
            ArgumentNullException.ThrowIfNull(expectedState);
            if (expectedState.SourceExisted ||
                !CatalogIdentity.IsValidSha256(expectedState.SourceHash) ||
                !CatalogIdentity.IsValidSha256(expectedState.SourceEffectiveHash) ||
                !string.Equals(
                    EstimateTraceIdentity.EffectiveProfileHash(seedProfile),
                    expectedState.SourceEffectiveHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The generated project-profile seed changed after discovery; the decision must be restarted.");
        }

        /// <summary>
        /// Rechecks the exact source/target state captured when the workflow loaded.
        /// This is intentionally public so scans and review dialogs can fail before
        /// they spend time computing from a profile that lost the runtime-target CAS.
        /// </summary>
        public static void RequireExpectedFilesUnchanged(ExpectedProfileState expectedState)
        {
            ArgumentNullException.ThrowIfNull(expectedState);
            var source = Path.GetFullPath(expectedState.SourcePath);
            var target = Path.GetFullPath(expectedState.TargetPath);
            if (expectedState.SourceExisted &&
                (!File.Exists(source) ||
                 !string.Equals(HashProfileFile(source), expectedState.SourceHash,
                     StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The source profile changed after the workflow began; reload before continuing.");

            var targetExists = File.Exists(target);
            if (targetExists != expectedState.TargetExisted)
                throw new InvalidOperationException(
                    expectedState.TargetExisted
                        ? "The expected runtime profile disappeared after the workflow began."
                        : "A runtime profile appeared after the workflow began; reload it instead of overwriting it.");
            if (expectedState.TargetExisted &&
                (string.IsNullOrWhiteSpace(expectedState.TargetHash) ||
                 !string.Equals(HashProfileFile(target), expectedState.TargetHash,
                     StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The runtime profile changed after the workflow began; reload the newer bytes.");
        }

        public static void RequireExpectedStateUnchanged(
            ProjectProfile profile, ExpectedProfileState expectedState)
        {
            ArgumentNullException.ThrowIfNull(profile);
            RequireExpectedFilesUnchanged(expectedState);
            if (!expectedState.SourceExisted)
                RequireGeneratedSourceUnchanged(profile, expectedState);
        }

        /// <summary>
        /// Writes the profile to <paramref name="path"/>. The caller supplies what
        /// changed and who approved it; this method records that in provenance.
        /// </summary>
        public static SaveResult Save(
            ProjectProfile profile,
            string path,
            string changeSummary,
            string approvedBy,
            ExpectedProfileState expectedState,
            IReadOnlyDictionary<string, string>? extraSourceHashes = null)
        {
            ArgumentNullException.ThrowIfNull(expectedState);
            if (string.IsNullOrWhiteSpace(approvedBy))
                throw new ArgumentException("An approver is required — configuration is an engineering decision.", nameof(approvedBy));
            return RaiseSaved(Publish(profile, path, changeSummary, approvedBy, expectedState, extraSourceHashes));
        }

        /// <summary>
        /// Raised after every successful save, on the saving thread. An open palette listens so a profile
        /// changed under it — a decision saved from the MahodAI chat — is reloaded instead of being
        /// overwritten from a stale copy. A listener can never fail the save.
        /// </summary>
        public static event Action<SaveResult>? Saved;

        private static SaveResult RaiseSaved(SaveResult saved)
        {
            var handlers = Saved;
            if (handlers == null) return saved;
            foreach (Action<SaveResult> handler in handlers.GetInvocationList())
            {
                try { handler(saved); }
                catch { /* a listener's failure is its own; the profile is saved */ }
            }
            return saved;
        }

        /// <summary>
        /// b24 (Codex 12:24 C): publishes a technical record (a unit review that was observed) through the same CAS and
        /// atomic replace, without claiming an approval: Provenance.ApprovedBy / ApprovedAtUtc / Source are untouched and
        /// the header says so. Only the version advances, so every other writer sees the change.
        /// </summary>
        public static SaveResult SaveTechnicalRecord(
            ProjectProfile profile, string path, string changeSummary, ExpectedProfileState expectedState)
        {
            ArgumentNullException.ThrowIfNull(expectedState);
            return RaiseSaved(Publish(profile, path, changeSummary, null, expectedState, null));
        }

        private static SaveResult Publish(
            ProjectProfile profile,
            string path,
            string changeSummary,
            string? approvedBy,
            ExpectedProfileState expectedState,
            IReadOnlyDictionary<string, string>? extraSourceHashes)
        {
            var unitErrors = PhysicalDrawingUnitPolicy.ValidateDeclarations(
                profile.DrawingUnitDeclarations, profile.ProfileId)
                .Concat(PhysicalDrawingUnitPolicy.ValidateReviews(profile.DrawingUnitReviews, profile.ProfileId))
                .Concat(PhysicalDrawingUnitPolicy.ValidateDecisionLinks(
                    profile.DrawingUnitDeclarations, profile.DrawingUnitReviews, profile.ProfileId)).ToList();
            if (unitErrors.Count != 0)
                throw new ArgumentException(unitErrors[0].Message, nameof(profile));

            // The family section is validated the way the loader will read it back, so an
            // incomplete or edited decision is refused here instead of being written and
            // then making the whole reloaded profile unusable.
            var familyErrors = FamilyDecisionPolicy.Validate(profile.Estimate, profile.ProfileId)
                .Concat(EditionLinkPolicy.Validate(profile.Estimate, profile.ProfileId)).ToList();
            if (familyErrors.Count != 0)
                throw new ArgumentException(
                    $"{familyErrors[0].Code}: {familyErrors[0].Title}. {familyErrors[0].Message}", nameof(profile));

            // Schema 5 gate, before CAS, provenance or any file: a version this build does not know is never written,
            // and malformed mappings or scoped approvals are refused instead of making the reloaded profile unusable.
            if (!ProjectProfileSchemaPolicy.IsSupportedVersion(profile.SchemaVersion))
                throw new ArgumentException(
                    $"Unsupported profile schema_version {profile.SchemaVersion}; this build does not write it.", nameof(profile));
            var schemaErrors = ProjectProfileSchemaPolicy.ContentFindings(profile);
            if (schemaErrors.Count != 0)
                throw new ArgumentException(
                    $"{schemaErrors[0].Code}: {schemaErrors[0].Title}. {schemaErrors[0].Message}", nameof(profile));

            // First CAS check is intentionally before provenance or any caller-visible
            // mutation. A stale workflow cannot even look approved in memory.
            ValidateExpectedState(profile, path, expectedState, validateGeneratedSeed: true);
            var targetAtEntry = CaptureTargetState(path);

            // Save mutates provenance as part of the engineering decision. Keep a
            // complete in-memory snapshot so a serialization/filesystem failure
            // cannot make the active palette look approved when no durable profile
            // was written.
            var oldVersion = profile.Provenance.Version;
            var oldApprovedBy = profile.Provenance.ApprovedBy;
            var oldApprovedAtUtc = profile.Provenance.ApprovedAtUtc;
            var oldSource = profile.Provenance.Source;
            var oldSourceHashes = new Dictionary<string, string>(
                profile.Provenance.SourceHashes, StringComparer.Ordinal);
            var oldSchemaVersion = profile.SchemaVersion;

            string? temporaryPath = null;
            try
            {
                // Schema gate: a profile that carries family decisions is written as schema 2,
                // which a schema-1-only build refuses instead of silently dropping (and on its
                // next save erasing) the section. Without family decisions the file stays
                // schema 1 and remains readable by older builds. The in-memory profile is
                // bumped too, so its effective hash equals that of the reloaded file.
                if (profile.Estimate.FamilyDecisions.Count > 0 &&
                    profile.SchemaVersion < FamilyDecisionPolicy.FamilyDecisionsSchemaVersion)
                    profile.SchemaVersion = FamilyDecisionPolicy.FamilyDecisionsSchemaVersion;
                // Edition links: schema 3, which a schema-2 build refuses rather than dropping the links on load.
                if (profile.Estimate.EditionLinks.Count > 0 &&
                    profile.SchemaVersion < EditionLinkPolicy.EditionLinksSchemaVersion)
                    profile.SchemaVersion = EditionLinkPolicy.EditionLinksSchemaVersion;
                // Old readers must refuse instead of dropping the exact visual binding on their next save.
                if (profile.Estimate.FamilyDecisions.Any(d => d.Selectors.Any(s => s.VisualBinding != null)) &&
                    profile.SchemaVersion < FamilyVisualBindingPolicy.ProfileSchemaVersion)
                    profile.SchemaVersion = FamilyVisualBindingPolicy.ProfileSchemaVersion;
                // Schema 5 only with schema-5 content, so every other profile keeps its exact bytes; never downgraded.
                if (ProjectProfileSchemaPolicy.HasSchema5Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema5)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema5;
                // Schema 6 only with a declared discipline; a legacy profile keeps its version and bytes.
                if (ProjectProfileSchemaPolicy.HasSchema6Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema6)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema6;
                // Schema 7 only with a reviewed explicit-INSUNITS unit decision (b24); unitless declarations keep theirs.
                if (ProjectProfileSchemaPolicy.HasSchema7Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema7)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema7;
                // Schema 8 only with a Civil-bound unitless decision or a second review record of one drawing (b25).
                if (ProjectProfileSchemaPolicy.HasSchema8Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema8)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema8;
                // Schema 9 only with a decision bound to a Civil reading the API does not name (b26).
                if (ProjectProfileSchemaPolicy.HasSchema9Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema9)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema9;
                if (ProjectProfileSchemaPolicy.HasSchema10Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema10)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema10;
                if (ProjectProfileSchemaPolicy.HasSchema11Content(profile) &&
                    profile.SchemaVersion < ProjectProfileSchemaPolicy.Schema11)
                    profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema11;

                profile.Provenance.Version += 1;
                if (approvedBy != null)
                {
                    profile.Provenance.ApprovedBy = approvedBy.Trim();
                    profile.Provenance.ApprovedAtUtc = DateTime.UtcNow;
                    profile.Provenance.Source = string.IsNullOrWhiteSpace(profile.Provenance.Source)
                        ? changeSummary
                        : profile.Provenance.Source + " | " + changeSummary;
                }

                if (extraSourceHashes != null)
                {
                    foreach (var kv in extraSourceHashes)
                        profile.Provenance.SourceHashes[kv.Key] = kv.Value;
                }

                var serializer = new SerializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
                    .Build();

                var header =
                    "# Mahod Civil Delivery — Project Profile (managed file)\n" +
                    $"# Last change: {changeSummary}\n" +
                    (approvedBy != null
                        ? $"# Approved by: {approvedBy.Trim()} at {profile.Provenance.ApprovedAtUtc:O}\n"
                        : $"# Technical record (no engineering approval) at {DateTime.UtcNow:O}\n") +
                    $"# Profile version: {profile.Provenance.Version}\n" +
                    "# Engineering values are only written here through explicit approval.\n";

                var yaml = header + serializer.Serialize(profile);
                var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                                ?? throw new InvalidOperationException("Profile target has no parent directory.");
                Directory.CreateDirectory(directory);

                // Write and flush a same-directory temporary file first. The final
                // move/replace is atomic on the target volume; Civil can therefore
                // never observe a half-written YAML if the process is interrupted.
                temporaryPath = Path.Combine(
                    directory, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(
                           temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 4096, options: FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(yaml);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // A sibling lock file serializes publishers across Civil processes
                // and workstations sharing the profile directory.  The
                // expected bytes are re-read only after the lock is held, so two
                // processes starting from the same hash cannot both replace the
                // target successfully (the loser observes the winner's bytes).
                using var publicationLock = AcquirePublicationLock(path);
                ValidateExpectedState(profile, path, expectedState,
                    validateGeneratedSeed: false);
                ValidateTargetState(path, targetAtEntry);

                var backupPath = string.Empty;
                if (File.Exists(path))
                {
                    backupPath = UniqueBackupPath(path);
                    File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
                temporaryPath = null;

                return new SaveResult(
                    path, backupPath, profile.Provenance.Version,
                    ArtifactHash.Sha256OfText(yaml));
            }
            catch
            {
                profile.SchemaVersion = oldSchemaVersion;
                profile.Provenance.Version = oldVersion;
                profile.Provenance.ApprovedBy = oldApprovedBy;
                profile.Provenance.ApprovedAtUtc = oldApprovedAtUtc;
                profile.Provenance.Source = oldSource;
                profile.Provenance.SourceHashes.Clear();
                foreach (var kv in oldSourceHashes)
                    profile.Provenance.SourceHashes[kv.Key] = kv.Value;

                if (!string.IsNullOrWhiteSpace(temporaryPath))
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
                throw;
            }
        }

        /// <summary>
        /// Withdraws exactly the publication represented by <paramref name="saved"/>
        /// after a downstream evidence failure.  The same cross-machine lock used by
        /// <see cref="Save"/> is held while the just-written hash is re-read and the
        /// backup is restored, so rollback can never overwrite a newer approved save.
        /// </summary>
        public static void RestoreAfterFailedPublication(SaveResult saved)
        {
            ArgumentNullException.ThrowIfNull(saved);
            var path = Path.GetFullPath(saved.Path);

            string? previousText = null;
            string? previousHash = null;
            if (!string.IsNullOrWhiteSpace(saved.BackupPath))
            {
                var backup = Path.GetFullPath(saved.BackupPath);
                if (!File.Exists(backup))
                    throw new FileNotFoundException(
                        "The pre-decision profile backup is missing; safe restoration is impossible.",
                        backup);
                previousText = File.ReadAllText(backup);
                previousHash = ArtifactHash.Sha256OfText(previousText);
                var previous = ProjectProfileLoader.LoadFromText(previousText);
                if (!previous.IsUsable || previous.Profile == null)
                    throw new InvalidOperationException(
                        "The pre-decision profile backup is not a usable project profile.");
            }

            using var publicationLock = AcquirePublicationLock(path);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "The just-written profile is missing; safe restoration cannot prove its target.",
                    path);
            var currentHash = HashProfileFile(path);
            if (!string.Equals(currentHash, saved.NewHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The profile changed after the decision save; restoration refused to overwrite newer bytes.");

            if (previousText == null)
            {
                File.Delete(path);
                if (File.Exists(path))
                    throw new IOException(
                        "The newly created decision profile could not be withdrawn.");
                return;
            }

            AtomicTextFile.WriteAllText(path, previousText);
            if (!string.Equals(HashProfileFile(path), previousHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "The restored profile does not equal the pre-decision backup.");
        }

        private sealed record TargetState(bool Existed, string? Hash);

        private sealed class PublicationLock : IDisposable
        {
            private FileStream? _stream;

            internal PublicationLock(FileStream stream) => _stream = stream;

            public void Dispose()
            {
                _stream?.Dispose();
                _stream = null;
            }
        }

        private static PublicationLock AcquirePublicationLock(string path)
        {
            var target = Path.GetFullPath(path);
            var canonical = target.ToUpperInvariant();
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var directory = Path.GetDirectoryName(target)
                            ?? throw new InvalidOperationException(
                                "Profile target has no parent directory.");
            var lockPath = Path.Combine(
                directory,
                "." + Path.GetFileName(target) + "." + digest[..16] + ".mhd-profile.lock");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            IOException? lastContention = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    // The lock lives next to the target, so FileShare.None is also
                    // honoured when MHD_PROFILE_DIR points at an SMB/shared folder.
                    // Unlike a Local named mutex, this serializes publishers from
                    // different Windows sessions and workstations.
                    var stream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
                    return new PublicationLock(stream);
                }
                catch (IOException ex)
                {
                    lastContention = ex;
                    Thread.Sleep(50);
                }
            }

            throw new TimeoutException(
                "Timed out waiting for another process or workstation to finish publishing this project profile.",
                lastContention);
        }

        private static TargetState CaptureTargetState(string path)
        {
            var target = Path.GetFullPath(path);
            var existed = File.Exists(target);
            return new TargetState(existed, existed ? HashProfileFile(target) : null);
        }

        private static void ValidateTargetState(string path, TargetState expected)
        {
            var target = Path.GetFullPath(path);
            var exists = File.Exists(target);
            if (exists != expected.Existed ||
                (exists && !string.Equals(HashProfileFile(target), expected.Hash,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The profile target changed while the approved save was being prepared; the newer bytes were not overwritten.");
        }

        private static void ValidateExpectedState(
            ProjectProfile profile,
            string requestedPath,
            ExpectedProfileState expected,
            bool validateGeneratedSeed)
        {
            var requested = Path.GetFullPath(requestedPath);
            var target = Path.GetFullPath(expected.TargetPath);
            if (!string.Equals(requested, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The requested profile target differs from the target captured by the workflow.");
            RequireExpectedFilesUnchanged(expected);
            if (!expected.SourceExisted && validateGeneratedSeed)
            {
                if (!CatalogIdentity.IsValidSha256(expected.SourceHash) ||
                    !CatalogIdentity.IsValidSha256(expected.SourceEffectiveHash))
                    throw new InvalidOperationException(
                        "The generated project-profile seed evidence is incomplete; save refused the unproven setup state.");
            }

        }

        private static string HashProfileFile(string path) =>
            ArtifactHash.Sha256OfText(File.ReadAllText(path));

        private static string UniqueBackupPath(string path)
        {
            var stem = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            var candidate = stem;
            for (var suffix = 1; File.Exists(candidate); suffix++)
                candidate = stem + "-" + suffix;
            return candidate;
        }

        /// <summary>
        /// The writable profile location for an installed engineer machine. A repo
        /// checkout keeps its own copy; runtime approvals land under LOCALAPPDATA so
        /// an install is never required to be writable.
        /// </summary>
        public static string RuntimeProfilePath(string profileId) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "profiles", profileId, "project-profile.yaml");
    }
}
