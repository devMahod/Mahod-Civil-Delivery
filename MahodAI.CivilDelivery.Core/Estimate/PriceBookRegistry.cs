using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Registers price books with a project and chooses the active one.
    ///
    /// An engineer brings a price book as a file (NTI download, Dekel export, their
    /// own sheet). Registering it means: prove it IS a price book (the loader can read
    /// it), copy it next to the profile under a stable id, pin its hash, and record who
    /// registered it and when. Making it active re-points the profile's catalog/pricing
    /// identity at that hash. Every estimate line then carries that hash, so a result is
    /// always traceable to the exact edition it was priced against.
    ///
    /// Pure: no Autodesk dependency; the UI and the AI tools both call this.
    /// </summary>
    public static class PriceBookRegistry
    {
        public sealed record RegisterResult(
            ProjectProfile.EstimateProfile.PriceBookEntry Entry,
            PriceBookXlsxLoader.Inspection Inspection,
            string StoredPath,
            bool MadeActive,
            bool StoredFileCreated);

        /// <summary>
        /// Inspects <paramref name="sourceXlsx"/>, copies it into <paramref name="profileDir"/>
        /// as <c>{id}.xlsx</c>, and adds/updates the registry entry on the profile.
        /// Throws when the workbook is not a readable price book.
        /// When supplied, <paramref name="expectedInspectionHash"/> must contain exactly
        /// 64 ASCII hexadecimal characters and bind registration to the preview's SHA-256.
        /// The current inspection hash must match it, ignoring case, before any directory,
        /// stored copy or registry entry is created. Null preserves registration without a preview binding.
        /// <paramref name="mapping"/> is the engineer's explicit sheet/column choice for exactly these bytes (b15,
        /// schema 5); null keeps the automatic first-sheet reading. The same mapping is used for the inspection, the
        /// copy recheck, activation and every later load, never an automatic fallback. A mapping is immutable under its
        /// entry id: another reading of the same bytes is a new entry. Without an explicit <paramref name="id"/>, a
        /// suggested id that already belongs to other bytes or another reading gets the next free suffix (a new
        /// edition in the same month is then registrable instead of a dead end).
        /// </summary>
        public static RegisterResult Register(
            ProjectProfile profile,
            string profileDir,
            string sourceXlsx,
            string registeredBy,
            string? id = null,
            string? publisher = null,
            string? edition = null,
            bool makeActive = false,
            string? expectedInspectionHash = null,
            PriceBookXlsxLoader.ColumnMapping? mapping = null)
        {
            if (string.IsNullOrWhiteSpace(registeredBy))
                throw new ArgumentException("An engineer name is required to register a price book.", nameof(registeredBy));

            if (expectedInspectionHash != null &&
                (expectedInspectionHash.Length != 64 || !expectedInspectionHash.All(Uri.IsHexDigit)))
                throw new ArgumentException(
                    "The preview SHA-256 must contain exactly 64 hexadecimal characters.", nameof(expectedInspectionHash));

            if (mapping != null && expectedInspectionHash != null &&
                !string.Equals(mapping.ExpectedFileHash, expectedInspectionHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "בחירת העמודות אינה שייכת לקובץ שנבדק בתצוגה המקדימה.", nameof(mapping));

            var inspection = mapping == null
                ? PriceBookXlsxLoader.Inspect(sourceXlsx)
                : PriceBookXlsxLoader.Inspect(sourceXlsx, mapping);
            if (!inspection.IsUsable)
                throw new InvalidOperationException(
                    "הקובץ אינו מחירון קריא: " +
                    (inspection.Problems.Count > 0 ? string.Join(" ", inspection.Problems)
                                                   : "לא נמצאו סעיפים במבנה פרק.תת־פרק.סעיף."));

            if (expectedInspectionHash != null &&
                !string.Equals(inspection.FileHash, expectedInspectionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "קובץ המחירון השתנה מאז התצוגה המקדימה. יש לבחור שוב \"טען מחירון\", לבדוק את התצוגה המקדימה ולאשר מחדש.");

            var storedMapping = ProfileMapping(mapping);
            var finalId = id != null
                ? NormalizeId(id)
                : FreeId(profile, profileDir, NormalizeId(SuggestId(inspection, sourceXlsx)), inspection.FileHash, storedMapping);
            var finalPublisher = publisher ?? inspection.Publisher ?? "לא ידוע";
            var finalEdition = edition ?? inspection.EditionNote;

            var matchingEntries = profile.Estimate.PriceBooks.Where(e =>
                string.Equals(e.Id, finalId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingEntries.Count > 1)
                throw new InvalidOperationException(
                    $"Price book id '{finalId}' is duplicated in the project registry.");
            var existingEntry = matchingEntries.SingleOrDefault();
            if (existingEntry != null &&
                !string.Equals(existingEntry.FileHash, inspection.FileHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Price book id '{finalId}' is immutable and already belongs to different bytes. Register a new edition under a new id.");
            if (existingEntry != null && !SameMapping(existingEntry.Mapping, storedMapping))
                throw new InvalidOperationException(
                    $"למחירון '{finalId}' כבר רשומה קריאת עמודות אחרת של אותו קובץ. קריאה אחרת נרשמת בשם חדש, " +
                    "והאישורים הקיימים אינם עוברים אליה.");

            Directory.CreateDirectory(profileDir);
            var storedPath = Path.Combine(profileDir, finalId + ".xlsx");
            var sourceFullPath = Path.GetFullPath(sourceXlsx);
            var storedFullPath = Path.GetFullPath(storedPath);
            var storedFileCreated = false;
            string? pendingCopy = null;
            try
            {
                if (File.Exists(storedFullPath))
                {
                    var storedHash = ArtifactHash.Sha256OfFile(storedFullPath);
                    if (!string.Equals(storedHash, inspection.FileHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Stored price-book path '{storedFullPath}' already contains different bytes; overwrite was refused.");
                }
                else if (!string.Equals(storedFullPath, sourceFullPath,
                             StringComparison.OrdinalIgnoreCase))
                {
                    pendingCopy = storedFullPath + ".pending-" + Guid.NewGuid().ToString("N");
                    File.Copy(sourceFullPath, pendingCopy, overwrite: false);
                    var copiedHash = ArtifactHash.Sha256OfFile(pendingCopy);
                    if (!string.Equals(copiedHash, inspection.FileHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The staged price-book copy failed SHA-256 verification.");
                    File.Move(pendingCopy, storedFullPath);
                    pendingCopy = null;
                    storedFileCreated = true;
                }

                var entry = existingEntry;
                if (entry == null)
                {
                    entry = new ProjectProfile.EstimateProfile.PriceBookEntry { Id = finalId, Mapping = storedMapping };
                    profile.Estimate.PriceBooks.Add(entry);
                }
                entry.Publisher = finalPublisher;
                entry.Edition = finalEdition;
                entry.File = finalId + ".xlsx";
                entry.FileHash = inspection.FileHash;
                entry.ItemCount = inspection.ItemCount;
                entry.RegisteredAtUtc = DateTime.UtcNow;
                entry.RegisteredBy = registeredBy;

                if (makeActive) MakeActive(profile, finalId, profileDir);
                return new RegisterResult(
                    entry, inspection, storedFullPath, makeActive, storedFileCreated);
            }
            catch
            {
                try { if (pendingCopy != null && File.Exists(pendingCopy)) File.Delete(pendingCopy); }
                catch { }
                try { if (storedFileCreated && File.Exists(storedFullPath)) File.Delete(storedFullPath); }
                catch { }
                throw;
            }
        }

        /// <summary>Points the profile's catalog/pricing identity at a registered book.</summary>
        public static void MakeActive(ProjectProfile profile, string id, string? profileDir = null)
        {
            var matches = profile.Estimate.PriceBooks.Where(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(matches.Count == 0
                    ? $"Price book '{id}' is not registered with this project."
                    : $"Price book '{id}' is duplicated in the project registry.");
            var entry = matches[0];

            if (string.IsNullOrWhiteSpace(entry.Id) ||
                string.IsNullOrWhiteSpace(entry.File) ||
                !CatalogIdentity.IsValidSha256(entry.FileHash))
                throw new InvalidOperationException(
                    $"Price book '{id}' has incomplete identity (id/file/64-hex SHA-256 required).");

            var filePath = Path.IsPathRooted(entry.File)
                ? Path.GetFullPath(entry.File)
                : string.IsNullOrWhiteSpace(profileDir)
                    ? throw new InvalidOperationException(
                        "The authoritative profile directory is required to activate a relative price-book file.")
                    : Path.GetFullPath(Path.Combine(profileDir, entry.File));
            if (!File.Exists(filePath))
                throw new FileNotFoundException(
                    $"Price book '{id}' cannot be activated because its file is missing.", filePath);
            var actualHash = ArtifactHash.Sha256OfFile(filePath);
            if (!string.Equals(actualHash, entry.FileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Price book '{id}' cannot be activated because its file hash changed.");
            if (entry.Mapping != null &&
                ProjectProfileSchemaPolicy.MappingProblems(entry.Mapping, entry.FileHash) is { Count: > 0 } mappingProblems)
                throw new InvalidOperationException(
                    $"Price book '{id}' cannot be activated: {string.Join(" ", mappingProblems)}");
            var loaderMapping = LoaderMapping(entry);
            var inspection = loaderMapping == null
                ? PriceBookXlsxLoader.Inspect(filePath)
                : PriceBookXlsxLoader.Inspect(filePath, loaderMapping);
            if (!inspection.IsUsable ||
                !string.Equals(inspection.FileHash, entry.FileHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Price book '{id}' cannot be activated because the workbook is unusable.");

            profile.Estimate.Catalog.CatalogFile = entry.File;
            profile.Estimate.Catalog.CatalogFileHash = entry.FileHash;
            profile.Estimate.Catalog.CatalogVersion = $"{entry.Publisher} {entry.Edition}".Trim();
            var pricing = profile.Estimate.Pricing ??=
                new ProjectProfile.EstimateProfile.PricingProfile();
            pricing.PriceBookSnapshotId = entry.Id;
            pricing.PriceBookHash = entry.FileHash;
        }

        /// <summary>The registered entry the profile currently prices against, if any.</summary>
        public static ProjectProfile.EstimateProfile.PriceBookEntry? Active(ProjectProfile profile)
        {
            var id = profile.Estimate.Pricing?.PriceBookSnapshotId;
            if (string.IsNullOrEmpty(id)) return null;
            return profile.Estimate.PriceBooks.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Back-fills the registry for a profile written before the registry existed:
        /// the legacy catalog/pricing keys become the first (active) entry.
        /// </summary>
        public static bool EnsureLegacyEntry(ProjectProfile profile)
        {
            if (profile.Estimate.PriceBooks.Count > 0) return false;
            var id = profile.Estimate.Pricing?.PriceBookSnapshotId;
            var hash = profile.Estimate.Pricing?.PriceBookHash ?? profile.Estimate.Catalog.CatalogFileHash;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(hash)) return false;

            profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
            {
                Id = id,
                Publisher = id.StartsWith("nti", StringComparison.OrdinalIgnoreCase) ? "נתיבי ישראל" : null,
                Edition = profile.Estimate.Catalog.CatalogVersion,
                File = profile.Estimate.Catalog.CatalogFile ?? id + ".xlsx",
                FileHash = hash,
                Notes = "migrated from legacy catalog/pricing keys",
            });
            return true;
        }

        // ------------------------------------------------------------- mapping

        /// <summary>The loader mapping of a registered entry (bound to its FileHash), or null for automatic reading.</summary>
        public static PriceBookXlsxLoader.ColumnMapping? LoaderMapping(ProjectProfile.EstimateProfile.PriceBookEntry entry) =>
            entry.Mapping is { } m
                ? new PriceBookXlsxLoader.ColumnMapping(entry.FileHash ?? "", m.SheetName ?? "", m.HeaderRow,
                    m.CodeColumn ?? "", m.DescriptionColumn, m.UnitColumn ?? "", m.PriceColumn ?? "")
                : null;

        /// <summary>The stored form of a loader mapping (no hash of its own: the entry's FileHash binds it).</summary>
        public static ProjectProfile.EstimateProfile.PriceBookColumnMapping? ProfileMapping(
            PriceBookXlsxLoader.ColumnMapping? mapping) =>
            mapping == null
                ? null
                : new ProjectProfile.EstimateProfile.PriceBookColumnMapping
                {
                    SheetName = mapping.SheetName,
                    HeaderRow = mapping.HeaderRow,
                    CodeColumn = mapping.CodeColumn,
                    DescriptionColumn = mapping.DescriptionColumn,
                    UnitColumn = mapping.UnitColumn,
                    PriceColumn = mapping.PriceColumn,
                };

        /// <summary>Exact equality of two stored readings; null (automatic) equals only null.</summary>
        public static bool SameMapping(
            ProjectProfile.EstimateProfile.PriceBookColumnMapping? a,
            ProjectProfile.EstimateProfile.PriceBookColumnMapping? b) =>
            a == null || b == null
                ? a == null && b == null
                : string.Equals(a.SheetName, b.SheetName, StringComparison.Ordinal) && a.HeaderRow == b.HeaderRow &&
                  string.Equals(a.CodeColumn, b.CodeColumn, StringComparison.Ordinal) &&
                  string.Equals(a.DescriptionColumn, b.DescriptionColumn, StringComparison.Ordinal) &&
                  string.Equals(a.UnitColumn, b.UnitColumn, StringComparison.Ordinal) &&
                  string.Equals(a.PriceColumn, b.PriceColumn, StringComparison.Ordinal);

        // ------------------------------------------------------------------ ids

        /// <summary>
        /// The suggested id, or the first "-2", "-3" … suffix that is free for these bytes and this reading: an id is
        /// reused only for the same FileHash and the same mapping, and never over a stored file with other bytes.
        /// </summary>
        private static string FreeId(
            ProjectProfile profile, string profileDir, string suggested, string fileHash,
            ProjectProfile.EstimateProfile.PriceBookColumnMapping? mapping)
        {
            for (var n = 1; n < 1000; n++)
            {
                var candidate = n == 1 ? suggested : $"{suggested}-{n}";
                var entries = profile.Estimate.PriceBooks.Where(e =>
                    string.Equals(e.Id, candidate, StringComparison.OrdinalIgnoreCase)).ToList();
                if (entries.Count > 1) continue;
                if (entries.Count == 1 &&
                    (!string.Equals(entries[0].FileHash, fileHash, StringComparison.OrdinalIgnoreCase) ||
                     !SameMapping(entries[0].Mapping, mapping)))
                    continue;
                var stored = Path.Combine(profileDir, candidate + ".xlsx");
                if (File.Exists(stored) &&
                    !string.Equals(ArtifactHash.Sha256OfFile(stored), fileHash, StringComparison.OrdinalIgnoreCase))
                    continue;
                return candidate;
            }
            throw new InvalidOperationException($"No free price-book id near '{suggested}'.");
        }

        private static string SuggestId(PriceBookXlsxLoader.Inspection insp, string sourcePath)
        {
            var pub = insp.Publisher switch
            {
                "דקל" => "dekel",
                "נתיבי ישראל" => "nti",
                _ => "book",
            };
            // Try to pull a MMYYYY / YYYY from the edition note or file name.
            var text = (insp.EditionNote ?? "") + " " + Path.GetFileNameWithoutExtension(sourcePath);
            var m = Regex.Match(text, @"(0[1-9]|1[0-2])[-/ ]?(20\d\d)");
            var stamp = m.Success ? m.Groups[1].Value + m.Groups[2].Value
                      : Regex.Match(text, @"20\d\d") is { Success: true } y ? y.Value
                      : DateTime.UtcNow.ToString("yyyyMM");
            return $"{pub}-{stamp}";
        }

        private static string NormalizeId(string id)
        {
            var cleaned = Regex.Replace(id.Trim().ToLowerInvariant(), @"[^a-z0-9\-_]+", "-").Trim('-');
            return cleaned.Length == 0 ? "book" : cleaned;
        }
    }
}
