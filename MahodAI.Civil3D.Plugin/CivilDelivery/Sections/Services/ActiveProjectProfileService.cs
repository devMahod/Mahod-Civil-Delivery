using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Resolves the project profile for the active drawing: explicit tool selector,
    /// then an explicitly reviewed Document+path session selection, then MHD_PROFILE_ID.
    /// Otherwise the drawing's leading project number is used when present. An unrelated drawing receives
    /// its own deterministic, empty profile and is routed to setup; it can never
    /// silently inherit the shipped 6422 rules.
    /// </summary>
    public sealed class ActiveProjectProfileService
    {
        private static readonly Regex LeadingProjectNumber = new(
            @"^(?<id>[0-9]{4,8})(?=[^0-9]|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public sealed class ActiveLoadResult
        {
            public required ProjectProfileLoader.LoadResult Loaded { get; init; }
            public required string ProfileSource { get; init; }
            /// <summary>
            /// The authoritative destination for every approved profile mutation in
            /// this load scope.  It is deliberately distinct from ProfileSource:
            /// shipped/development baselines are readable inputs but are never the
            /// writable project contract.
            /// </summary>
            public required string ProfileWriteTarget { get; init; }
            /// <summary>
            /// Source/target CAS captured at the same instant as Loaded.  Callers must
            /// carry this object through scans and decisions; recapturing at click/end
            /// would adopt a concurrently-created runtime profile as if it were the
            /// engineer's original baseline.
            /// </summary>
            public ProjectProfileWriter.ExpectedProfileState? ProfileWriteState { get; init; }
            public bool IsGeneratedForDrawing { get; init; }

            public ProjectProfile? Profile => Loaded.Profile;
            public string? ProfileHash => Loaded.ProfileHash;
            public System.Collections.Generic.List<DeliveryFinding> Findings => Loaded.Findings;
            public bool IsUsable => Loaded.IsUsable;
            public System.Collections.Generic.IEnumerable<DeliveryFinding> ErrorFindings =>
                Loaded.Findings.Where(finding => finding.Severity >= FindingSeverity.Error);
        }

        internal sealed record ProfileSelection(
            string Selector,
            string DrawingStem,
            bool IsExplicit);

        public ActiveLoadResult LoadForDocument(
            Document doc,
            SectionsWorkflowService workflow,
            string? explicitSelector = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (workflow == null) throw new ArgumentNullException(nameof(workflow));

            var drawingIdentity = DrawingRevisionTracker.CaptureSavedDrawingIdentity(doc);
            return LoadForCapturedDrawingIdentity(doc, drawingIdentity, workflow, explicitSelector,
                Environment.GetEnvironmentVariable("MHD_PROFILE_ID"),
                Environment.GetEnvironmentVariable("MHD_PROFILE_DIR"));
        }

        // The native caller supplies the actual active-document snapshot and environment.
        // Keeping this path native-free permits a lifecycle test of the same seed,
        // session-binding, exact-file loader and identity checks, not a copied UI branch.
        internal static ActiveLoadResult LoadForCapturedDrawingIdentity(
            object documentToken,
            SavedDrawingPathPolicy.Identity drawingIdentity,
            SectionsWorkflowService workflow,
            string? explicitSelector = null,
            string? environmentSelector = null,
            string? environmentProfileDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(documentToken);
            ArgumentNullException.ThrowIfNull(drawingIdentity);
            ArgumentNullException.ThrowIfNull(workflow);
            if (drawingIdentity.Failure != null)
                return SelectionFailure(null, drawingIdentity.Failure);
            if (!drawingIdentity.IsSaved)
            {
                // Read-only session seed only. Never resolve/load a DWT-derived
                // project (or reuse another new document's Drawing1 profile).
                // OnScan/decision guards request the existing explicit QSAVE flow;
                // its continuation ReloadProfile derives the real ID after SaveAs.
                var pending = CreateUnconfiguredResult(new ProfileSelection(
                    SavedDrawingPathPolicy.UnsavedProfileId(documentToken),
                    drawingIdentity.DocumentName, false));
                pending.Findings.Insert(0, new DeliveryFinding
                {
                    Code = "SHR-PROFILE-DRAWING-SAVE-REQUIRED", Domain = "shared",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "יש לשמור את השרטוט בשם לפני שמירת פרופיל פרויקט",
                    Message = "השרטוט החדש אינו קובץ DWG שמור. פרופיל זמני זה אינו זהות התבנית ולא נשמר.",
                    RecommendedAction = "שמור בשם באמצעות Civil; לאחר השמירה ייטען פרופיל לפי נתיב ה-DWG שנבחר.",
                    ProjectProfileId = pending.Profile!.ProfileId,
                });
                return pending;
            }
            var savedPath = drawingIdentity.DrawingPath;
            var documentName = drawingIdentity.DocumentName;

            var sessionBinding = string.IsNullOrWhiteSpace(explicitSelector)
                ? ExistingProjectProfileSelection.Current(documentToken) : null;
            try { explicitSelector = ExistingProjectProfileSelection.Resolve(documentToken, savedPath, explicitSelector); }
            catch (InvalidOperationException ex)
            {
                return SelectionFailure(sessionBinding, ex.Message);
            }

            var result = LoadForDrawing(
                savedPath,
                documentName,
                workflow,
                explicitSelector,
                environmentSelector,
                environmentProfileDirectory);
            if (sessionBinding != null)
            {
                try { ExistingProjectProfileSelection.RequireProfileIdentity(sessionBinding, result.Profile?.ProfileId); }
                catch (InvalidOperationException ex) { return SelectionFailure(sessionBinding, ex.Message); }
            }
            return result;
        }

        private static ActiveLoadResult SelectionFailure(ExistingProjectProfileSelection.Binding? binding, string message) => new()
        {
            Loaded = new ProjectProfileLoader.LoadResult
            {
                Findings = { new DeliveryFinding { Code = "SHR-PROFILE-SELECTION-DRAWING-CHANGED",
                    Domain = "shared", Severity = FindingSeverity.Error, Title = "נדרשת בחירת פרופיל מחדש",
                    Message = message, RecommendedAction = "בחר פרופיל קיים עבור השרטוט והפרויקט הנוכחיים." } },
            },
            ProfileSource = binding?.ProfilePath ?? string.Empty,
            ProfileWriteTarget = binding?.ProfilePath ?? string.Empty,
        };

        internal static ActiveLoadResult LoadForDrawing(
            string? savedPath,
            string? documentName,
            SectionsWorkflowService workflow,
            string? explicitSelector = null,
            string? environmentSelector = null,
            string? environmentProfileDirectory = null)
        {
            if (workflow == null) throw new ArgumentNullException(nameof(workflow));

            var selection = SelectForDrawing(
                savedPath, documentName, explicitSelector, environmentSelector);
            var resolved = ProfileLocator.Resolve(selection.Selector);
            var loaded = workflow.LoadProfile(selection.Selector);
            if (loaded.Profile != null || resolved != null || selection.IsExplicit)
            {
                var source = string.IsNullOrWhiteSpace(resolved)
                    ? selection.Selector
                    : Path.GetFullPath(resolved);
                var target = ResolveWriteTarget(
                    selection, resolved, environmentProfileDirectory);
                ProjectProfileWriter.ExpectedProfileState? expected = null;
                if (loaded.Profile != null &&
                    CatalogIdentity.IsValidSha256(loaded.ProfileHash))
                {
                    if (!Path.IsPathFullyQualified(source))
                        throw new InvalidOperationException(
                            "A usable project profile did not resolve to an absolute source path.");
                    expected = ProjectProfileWriter.CaptureExpectedState(
                        source, loaded.ProfileHash!, target);
                }
                return new ActiveLoadResult
                {
                    Loaded = loaded,
                    ProfileSource = source,
                    ProfileWriteTarget = target,
                    ProfileWriteState = expected,
                };
            }

            // A drawing-derived selector with no persisted profile is deliberately
            // usable only as a setup seed. It contains no 6422 layers, sources,
            // catalog, price mappings or styles.
            return CreateUnconfiguredResult(selection);
        }

        internal static ActiveLoadResult CreateUnconfiguredResult(ProfileSelection selection)
        {
            var profile = CreateUnconfiguredProfile(selection.Selector, selection.DrawingStem);
            var serialized = JsonSerializer.Serialize(profile, SectionsWorkflowService.Json);
            var findings = ProjectProfileLoader.Validate(profile);
            var writeTarget = ProjectProfileWriter.RuntimeProfilePath(profile.ProfileId);
            findings.Insert(0, new DeliveryFinding
            {
                Code = "SHR-PROFILE-DRAWING-UNCONFIGURED",
                Domain = "shared",
                Severity = FindingSeverity.ReviewRequired,
                Title = "לשרטוט הפתוח עדיין אין פרופיל פרויקט מאושר",
                Message = $"נוצר פרופיל ריק ובטוח '{profile.ProfileId}' לפי שם השרטוט. " +
                          "יש להריץ הגדרת פרויקט לפני תכנון חתכים או אומדן.",
                RecommendedAction = "הפעל הגדרת פרויקט, בחר מקורות מתוך השרטוט ושמור באישור מהנדס.",
                ProjectProfileId = profile.ProfileId,
            });

            return new ActiveLoadResult
            {
                Loaded = new ProjectProfileLoader.LoadResult
                {
                    Profile = profile,
                    ProfileHash = ArtifactHash.Sha256OfText(serialized),
                    Findings = findings,
                },
                // A generated seed has no source file yet.  Use the exact future
                // runtime target as its authoritative identity (while
                // IsGeneratedForDrawing distinguishes the intentional absence) so
                // every setup/UI route can capture one target-absence CAS contract.
                ProfileSource = writeTarget,
                ProfileWriteTarget = writeTarget,
                ProfileWriteState = ProjectProfileWriter.CaptureExpectedGeneratedState(
                    profile, ArtifactHash.Sha256OfText(serialized), writeTarget),
                IsGeneratedForDrawing = true,
            };
        }

        /// <summary>
        /// Selects the only file an approval may mutate for this load. Explicit
        /// absolute files and profiles resolved from MHD_PROFILE_DIR retain their
        /// exact source. Drawing-id resolution of a shipped/development baseline is
        /// redirected to the per-user runtime profile so product files stay immutable.
        /// </summary>
        internal static string ResolveWriteTarget(
            ProfileSelection selection,
            string? resolvedSource,
            string? environmentProfileDirectory)
        {
            if (selection.IsExplicit)
            {
                var explicitPath = Path.GetFullPath(selection.Selector);
                if (Path.IsPathFullyQualified(selection.Selector) ||
                    (!string.IsNullOrWhiteSpace(resolvedSource) &&
                     string.Equals(explicitPath, Path.GetFullPath(resolvedSource),
                         StringComparison.OrdinalIgnoreCase)))
                    return explicitPath;
            }

            if (!string.IsNullOrWhiteSpace(resolvedSource) &&
                !string.IsNullOrWhiteSpace(environmentProfileDirectory))
            {
                var resolved = Path.GetFullPath(resolvedSource);
                var profileDirectory = Path.GetFullPath(environmentProfileDirectory);
                if (IsWithinDirectory(resolved, profileDirectory))
                    return resolved;
            }

            var profileId = !string.IsNullOrWhiteSpace(selection.Selector)
                ? selection.Selector
                : "unconfigured";
            return ProjectProfileWriter.RuntimeProfilePath(profileId);
        }

        internal static ProjectProfile? SelectUsableProfile(ActiveLoadResult loaded) =>
            loaded.IsUsable ? loaded.Profile : null;

        /// <summary>
        /// Reloads the authoritative profile bytes for an existing workflow.  A
        /// runtime target that appeared after PLAN supersedes the shipped baseline;
        /// otherwise the exact source selected by PLAN is reread.  Missing or corrupt
        /// expected files stay missing/corrupt -- this method never falls through to a
        /// different profile with the same id.
        /// </summary>
        internal static ActiveLoadResult ReloadForExistingWorkflow(
            Document doc,
            SectionsWorkflowService workflow,
            string projectProfileId,
            string? profileSource,
            string? profileWriteTarget)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (workflow == null) throw new ArgumentNullException(nameof(workflow));

            var selector = AuthoritativeReloadSelector(
                projectProfileId, profileSource, profileWriteTarget, File.Exists);
            var loaded = workflow.LoadProfile(selector);
            var source = Path.IsPathFullyQualified(selector)
                ? Path.GetFullPath(selector)
                : selector;
            var target = !string.IsNullOrWhiteSpace(profileWriteTarget)
                ? Path.GetFullPath(profileWriteTarget)
                : ProjectProfileWriter.RuntimeProfilePath(projectProfileId);
            ProjectProfileWriter.ExpectedProfileState? expected = null;
            if (loaded.Profile != null && CatalogIdentity.IsValidSha256(loaded.ProfileHash))
            {
                if (!Path.IsPathFullyQualified(source))
                    throw new InvalidOperationException(
                        "The authoritative workflow profile did not resolve to an absolute source path.");
                expected = ProjectProfileWriter.CaptureExpectedState(
                    source, loaded.ProfileHash!, target);
            }
            return new ActiveLoadResult
            {
                Loaded = loaded,
                ProfileSource = source,
                ProfileWriteTarget = target,
                ProfileWriteState = expected,
            };
        }

        /// <summary>
        /// Pure selector used by the live reload above and by regression tests.  An
        /// existing writable runtime contract wins.  If it does not exist, preserve
        /// the exact PLAN source even when that source was deleted: LoadProfile must
        /// then fail closed instead of silently resolving another same-id profile.
        /// </summary>
        internal static string AuthoritativeReloadSelector(
            string projectProfileId,
            string? profileSource,
            string? profileWriteTarget,
            Func<string, bool> fileExists)
        {
            if (fileExists == null) throw new ArgumentNullException(nameof(fileExists));
            if (!string.IsNullOrWhiteSpace(profileWriteTarget))
            {
                var target = Path.GetFullPath(profileWriteTarget);
                if (fileExists(target)) return target;
            }
            if (!string.IsNullOrWhiteSpace(profileSource))
                return Path.IsPathFullyQualified(profileSource)
                    ? Path.GetFullPath(profileSource)
                    : profileSource;
            if (string.IsNullOrWhiteSpace(projectProfileId))
                throw new ArgumentException(
                    "A project profile id or exact source is required.",
                    nameof(projectProfileId));
            return projectProfileId;
        }

        private static bool IsWithinDirectory(string path, string directory)
        {
            var relative = Path.GetRelativePath(directory, path);
            return !Path.IsPathFullyQualified(relative) &&
                   !string.Equals(relative, "..", StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }

        internal static ProfileSelection SelectForDrawing(
            string? savedPath,
            string? documentName,
            string? explicitSelector,
            string? environmentSelector)
        {
            if (!string.IsNullOrWhiteSpace(explicitSelector))
                return new ProfileSelection(
                    explicitSelector.Trim(), DrawingStem(savedPath, documentName), true);

            if (!string.IsNullOrWhiteSpace(environmentSelector))
                return new ProfileSelection(
                    environmentSelector.Trim(), DrawingStem(savedPath, documentName), true);

            var stem = DrawingStem(savedPath, documentName);
            var projectNumber = LeadingProjectNumber.Match(stem);
            if (projectNumber.Success)
                return new ProfileSelection(projectNumber.Groups["id"].Value, stem, false);

            // A filename alone is not a project identity: two unrelated folders
            // routinely contain Site.dwg.  Bind generated profiles to the canonical
            // saved path; explicit MHD_PROFILE_ID remains the portability override.
            var normalized = DrawingIdentity(savedPath, documentName);
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
            var safeStem = SafeStem(stem);
            return new ProfileSelection($"drawing-{safeStem}-{hash}", stem, false);
        }

        private static string DrawingIdentity(string? savedPath, string? documentName)
        {
            var candidate = !string.IsNullOrWhiteSpace(savedPath)
                ? savedPath.Trim()
                : documentName?.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return "UNSAVED-DRAWING";

            try
            {
                var identity = Path.IsPathFullyQualified(candidate)
                    ? Path.GetFullPath(candidate)
                    : candidate;
                return identity.Replace(Path.AltDirectorySeparatorChar,
                        Path.DirectorySeparatorChar)
                    .Trim()
                    .ToUpperInvariant();
            }
            catch
            {
                return candidate.ToUpperInvariant();
            }
        }

        internal static ProjectProfile CreateUnconfiguredProfile(string profileId, string drawingStem) =>
            new()
            {
                ProfileId = profileId,
                ProjectName = drawingStem,
                Provenance = new ProjectProfile.ProfileProvenance
                {
                    Source = "Generated from active drawing identity; engineer setup required.",
                },
            };

        private static string DrawingStem(string? savedPath, string? documentName)
        {
            var candidate = !string.IsNullOrWhiteSpace(savedPath) ? savedPath : documentName;
            if (string.IsNullOrWhiteSpace(candidate)) return "unsaved-drawing";

            try
            {
                var file = Path.GetFileName(candidate.Trim());
                var stem = Path.GetFileNameWithoutExtension(file);
                return string.IsNullOrWhiteSpace(stem) ? "unsaved-drawing" : stem;
            }
            catch
            {
                return "unsaved-drawing";
            }
        }

        private static string SafeStem(string value)
        {
            var builder = new StringBuilder();
            var previousWasDash = false;
            foreach (var c in value.ToLowerInvariant())
            {
                var keep = c is >= 'a' and <= 'z' or >= '0' and <= '9';
                if (keep)
                {
                    builder.Append(c);
                    previousWasDash = false;
                }
                else if (!previousWasDash && builder.Length > 0)
                {
                    builder.Append('-');
                    previousWasDash = true;
                }

                if (builder.Length >= 40) break;
            }

            var safe = builder.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(safe) ? "drawing" : safe;
        }
    }
}
