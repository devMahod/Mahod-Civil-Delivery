using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Schema 5 (Codex contract E7F6811B, 01/10): explicit price-book column mappings and scoped catalog approvals.
    /// A profile gets schema 5 only when it carries one of them; without them every older schema keeps its exact
    /// YAML and JSON bytes. The loader and the writer both apply this gate, and every caller must honour
    /// <c>IsUsable</c>: an older build still returns a Profile object for a schema-5 file it refuses.
    /// </summary>
    public static class ProjectProfileSchemaPolicy
    {
        // 1.4.1 merge (06.10): Schema 10 = Codex's estimate source selection, Schema 11 = Claude's CL scope / mode.
        public const int CurrentSchemaVersion = 11;
        public const int Schema10 = 10;
        public static bool HasSchema10Content(ProjectProfile profile) => profile.Estimate.SourceSelection != null;
        public const int Schema5 = 5;
        /// <summary>Schema 6: <see cref="ProjectProfile.EstimateProfile.Discipline"/>.</summary>
        public const int Schema6 = 6;
        /// <summary>Schema 7 (b24, Codex 11:18): a reviewed unit decision for a host with an explicit INSUNITS
        /// (<see cref="ProjectProfile.DrawingUnitDeclaration.DecisionKind"/>). A schema-6 build refuses the file instead of
        /// ignoring the review and falling back to SI by the raw unit; unitless declarations alone stay at their version.</summary>
        public const int Schema7 = 7;
        /// <summary>Schema 8 (b25, Codex 15:12): a unitless→metres decision bound to Civil evidence
        /// (<see cref="PhysicalDrawingUnitPolicy.UnitlessMetres"/>), or a drawing with more than one unit-review record (a
        /// later conflict recorded after its earlier review was closed). A schema-7 build refuses the file instead of
        /// reading the new kind as an invalid declaration or the second record as a broken list.</summary>
        public const int Schema8 = 8;
        /// <summary>Schema 9 (b26, LA-40 live 17:39): a unit decision bound to a Civil reading the API does not name
        /// (<see cref="PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther"/>). A schema-8 build refuses the file
        /// instead of reading that binding as an invalid declaration.</summary>
        public const int Schema9 = 9;
        /// <summary>Schema 11 (1.4.1, project 984): explicit CL scope and the station-markers CL mode in
        /// <see cref="ProjectProfile.SectionsProfile.ClProfile"/>. An older build refuses the file instead of reading a
        /// scoped CL source as host + file, or a station tick as a 0.5 m section.</summary>
        public const int Schema11 = 11;

        /// <summary>True when the CL profile carries schema-11 content (see <see cref="Schema11"/>).</summary>
        public static bool HasSchema11Content(ProjectProfile profile) =>
            profile.Sections.Cl is { } cl && (cl.LayerScope != null || cl.Mode != null ||
                cl.StationMarkerHalfWidthM != null || cl.StationMarkerApprovedBy != null);

        /// <summary>Hebrew problems of the schema-11 CL content; empty when valid. An explicit value never falls back.</summary>
        public static List<string> ClScopeProblems(ProjectProfile.SectionsProfile.ClProfile cl)
        {
            var problems = new List<string>();
            if (cl.LayerScope != null)
            {
                if (!string.Equals(cl.LayerScope, ProjectProfile.SectionsProfile.ClProfile.LayerScopeSourceFile, StringComparison.Ordinal))
                    problems.Add($"היקף קווי ה-CL '{cl.LayerScope}' אינו מוכר; רק 'source-file' או ריק.");
                else if (cl.SourceFiles.Count == 0 || cl.SourceFiles.Any(string.IsNullOrWhiteSpace))
                    problems.Add("היקף קווי ה-CL הוגדר לקובץ CL נפרד, אך לא הוגדר קובץ CL תקין.");
            }
            if (cl.Mode != null)
            {
                if (!string.Equals(cl.Mode, ProjectProfile.SectionsProfile.ClProfile.ModeStationMarkers, StringComparison.Ordinal))
                    problems.Add($"מצב קווי ה-CL '{cl.Mode}' אינו מוכר; רק 'station-markers' או ריק.");
                else if (cl.StationMarkerHalfWidthM is not { } w || !double.IsFinite(w) || w <= 0 ||
                         w > ProjectProfile.SectionsProfile.ClProfile.StationMarkerMaxHalfWidthM)
                    problems.Add("מצב סימוני תחנות דורש חצי-רוחב חיובי עד " +
                        $"{ProjectProfile.SectionsProfile.ClProfile.StationMarkerMaxHalfWidthM:F0} מ'.");
                else if (string.IsNullOrWhiteSpace(cl.StationMarkerApprovedBy))
                    problems.Add("מצב סימוני תחנות דורש שם מאשר.");
            }
            else if (cl.StationMarkerHalfWidthM != null || cl.StationMarkerApprovedBy != null)
                problems.Add("חצי-רוחב או מאשר לסימוני תחנות קיימים בלי המצב 'station-markers'.");
            return problems;
        }

        /// <summary>The disciplines a profile may declare; anything else is refused, never read as roads.</summary>
        public static readonly IReadOnlyList<string> Disciplines = new[] { "roads", "landscape" };

        /// <summary>True when the profile declares a discipline (schema-6 content).</summary>
        public static bool HasSchema6Content(ProjectProfile profile) => profile.Estimate.Discipline != null;

        /// <summary>True when a unit declaration carries a decision kind (schema-7 content).</summary>
        public static bool HasSchema7Content(ProjectProfile profile) =>
            profile.DrawingUnitDeclarations?.Any(d => d?.DecisionKind != null) == true ||
            profile.DrawingUnitReviews != null;

        /// <summary>True when the profile carries schema-9 content (see <see cref="Schema9"/>).</summary>
        public static bool HasSchema9Content(ProjectProfile profile) =>
            profile.DrawingUnitDeclarations?.Any(d => d?.CivilDrawingUnitStatus == PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther) == true;

        /// <summary>True when the profile carries schema-8 content (see <see cref="Schema8"/>).</summary>
        public static bool HasSchema8Content(ProjectProfile profile) =>
            profile.DrawingUnitDeclarations?.Any(d => d?.DecisionKind == PhysicalDrawingUnitPolicy.UnitlessMetres) == true ||
            profile.DrawingUnitReviews?.Where(r => r != null)
                // The identity the review rules use (Codex 16:10): the parsed GUID in one format and the canonical path.
                .GroupBy(r => (Guid.TryParse(r.DrawingFingerprint, out var guid) ? guid.ToString("D") : r.DrawingFingerprint,
                    PhysicalDrawingUnitPolicy.CanonicalDrawingPath(r.DrawingPath) ?? r.DrawingPath))
                .Any(g => g.Count() > 1) == true;

        private static readonly Regex Column = new("^[A-Z]{1,3}$", RegexOptions.CultureInvariant);

        public static bool IsSupportedVersion(int schemaVersion) => schemaVersion is >= 1 and <= CurrentSchemaVersion;

        /// <summary>True when the profile carries content that only schema 5 may hold (an explicit empty list counts).</summary>
        public static bool HasSchema5Content(ProjectProfile profile) =>
            (profile.Estimate.PriceBooks ?? new List<ProjectProfile.EstimateProfile.PriceBookEntry>()).Any(book => book?.Mapping != null) ||
            profile.Estimate.ScopedCatalogApprovals != null;

        /// <summary>A1-style column inside A..XFD.</summary>
        public static bool IsValidColumn(string? column)
        {
            if (column == null || !Column.IsMatch(column)) return false;
            var index = column.Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
            return index <= 16384;
        }

        /// <summary>The structural problems of a mapping bound to <paramref name="fileHash"/>; empty when valid.</summary>
        public static List<string> MappingProblems(
            ProjectProfile.EstimateProfile.PriceBookColumnMapping mapping, string? fileHash)
        {
            var problems = new List<string>();
            if (!CatalogIdentity.IsValidSha256(fileHash))
                problems.Add("למיפוי עמודות חייב להיות FileHash תקין של המחירון (64 תווים הקסדצימליים).");
            if (string.IsNullOrWhiteSpace(mapping.SheetName))
                problems.Add("למיפוי עמודות חסר שם גיליון.");
            if (mapping.HeaderRow is < 1 or > 1048576)
                problems.Add($"שורת הכותרת {mapping.HeaderRow} מחוץ לטווח 1–1048576.");
            var columns = new[] { mapping.CodeColumn, mapping.UnitColumn, mapping.PriceColumn }
                .Concat(mapping.DescriptionColumn == null ? Array.Empty<string?>() : new[] { mapping.DescriptionColumn })
                .ToArray();
            if (columns.Any(column => !IsValidColumn(column)))
                problems.Add("עמודת סעיף, יחידה ומחיר חייבות להיות אותיות A–XFD; תיאור — אותיות או ריק.");
            else if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
                problems.Add("כל תפקיד במיפוי חייב עמודה אחרת.");
            return problems;
        }

        /// <summary>Schema-5 findings of a loaded profile: the version rule plus <see cref="ContentFindings"/>.</summary>
        public static List<DeliveryFinding> Validate(ProjectProfile profile)
        {
            var findings = new List<DeliveryFinding>();
            if (HasSchema5Content(profile) && profile.SchemaVersion < Schema5)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA5-CONTENT",
                    Domain = "shared",
                    Severity = FindingSeverity.Error,
                    Title = "פרופיל עם מיפוי עמודות או אישורים ממוקדים חייב להיות בסכמה 5",
                    Message = $"schema_version {profile.SchemaVersion} carries schema-5 content.",
                    ProjectProfileId = profile.ProfileId,
                });
            if (HasSchema6Content(profile) && profile.SchemaVersion < Schema6)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA6-CONTENT",
                    Domain = "shared",
                    Severity = FindingSeverity.Error,
                    Title = "פרופיל שמצהיר על תחום (כבישים / נוף) חייב להיות בסכמה 6",
                    Message = $"schema_version {profile.SchemaVersion} carries estimate.discipline.",
                    ProjectProfileId = profile.ProfileId,
                });
            if (HasSchema7Content(profile) && profile.SchemaVersion < Schema7)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA7-CONTENT",
                    Domain = "shared",
                    Severity = FindingSeverity.Error,
                    Title = "פרופיל עם הכרעת יחידות לשרטוט בעל INSUNITS מפורש חייב להיות בסכמה 7",
                    Message = $"schema_version {profile.SchemaVersion} carries drawing_unit_declarations[].decision_kind or drawing_unit_reviews.",
                    ProjectProfileId = profile.ProfileId,
                });
            if (HasSchema8Content(profile) && profile.SchemaVersion < Schema8)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA8-CONTENT",
                    Domain = "shared",
                    Severity = FindingSeverity.Error,
                    Title = "פרופיל עם הצהרת מטרים קשורה לראיית Civil, או עם יותר מרשומת בירור אחת לשרטוט, חייב להיות בסכמה 8",
                    Message = $"schema_version {profile.SchemaVersion} carries decision_kind unitless-metres or several drawing_unit_reviews for one drawing.",
                    ProjectProfileId = profile.ProfileId,
                });
            if (HasSchema11Content(profile) && profile.SchemaVersion < Schema11)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA11-CONTENT",
                    Domain = "shared",
                    Severity = FindingSeverity.Error,
                    Title = "פרופיל עם היקף קווי CL או מצב סימוני תחנות חייב להיות בסכמה 11",
                    Message = $"schema_version {profile.SchemaVersion} carries sections.cl layer_scope/mode/station_marker_*.",
                    ProjectProfileId = profile.ProfileId,
                });
            if (HasSchema9Content(profile) && profile.SchemaVersion < Schema9)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA9-CONTENT",
                    Domain = "shared",
                    Severity = FindingSeverity.Error,
                    Title = "פרופיל עם הכרעת יחידות שקשורה לקריאת Civil שאינה מטר ואינה רגל חייב להיות בסכמה 9",
                    Message = $"schema_version {profile.SchemaVersion} carries civil_drawing_unit_status observed-other.",
                    ProjectProfileId = profile.ProfileId,
                });
            if (HasSchema10Content(profile) && profile.SchemaVersion < Schema10)
                findings.Add(new DeliveryFinding
                {
                    Code = "SHR-PROFILE-SCHEMA10-CONTENT", Domain = "shared", Severity = FindingSeverity.Error,
                    Title = "בחירת מקורות לכתב הכמויות מחייבת סכמה 10", ProjectProfileId = profile.ProfileId,
                });
            findings.AddRange(ContentFindings(profile));
            return findings;
        }

        /// <summary>
        /// Structural problems of the schema-5 content itself (mappings, scoped approvals), whatever the version field
        /// says. The writer checks these before any mutation; the version is set by the writer, not by the caller.
        /// </summary>
        public static List<DeliveryFinding> ContentFindings(ProjectProfile profile)
        {
            var findings = new List<DeliveryFinding>();
            void Add(string code, string title, string message) => findings.Add(new DeliveryFinding
            {
                Code = code,
                Domain = "shared",
                Severity = FindingSeverity.Error,
                Title = title,
                Message = message,
                ProjectProfileId = profile.ProfileId,
            });

            if (profile.Estimate.SourceSelection is { } selection)
                foreach (var problem in EstimateSourceSelectionPolicy.ValidationProblems(selection))
                    Add(EstimateSourceSelectionPolicy.InvalidScopeCode, "בחירת מקורות אינה תקינה", problem);
            foreach (var problem in ClScopeProblems(profile.Sections.Cl))
                Add("SHR-PROFILE-CL-SCOPE", "הגדרת קווי ה-CL בפרופיל אינה תקינה", problem);
            if (profile.Estimate.Discipline is { } discipline && !Disciplines.Contains(discipline, StringComparer.Ordinal))
                Add("SHR-PROFILE-DISCIPLINE-UNKNOWN", $"התחום '{discipline}' בפרופיל אינו מוכר",
                    "estimate.discipline must be 'roads' or 'landscape' (lower case). An unknown value is refused, never read as roads.");
            if (profile.Estimate.PriceBooks == null)
                Add("EST-PRICE-BOOKS-LIST-NULL", "estimate.price_books must be a list",
                    "Use an empty list when no price book is registered.");
            foreach (var book in (profile.Estimate.PriceBooks ?? new List<ProjectProfile.EstimateProfile.PriceBookEntry>())
                         .Where(book => book?.Mapping != null))
            {
                var problems = MappingProblems(book.Mapping!, book.FileHash);
                if (problems.Count > 0)
                    Add("SHR-PROFILE-PRICEBOOK-MAPPING", $"מיפוי העמודות של המחירון '{book.Id}' אינו תקין",
                        string.Join(" ", problems));
            }

            var approvals = profile.Estimate.ScopedCatalogApprovals;
            if (approvals != null)
            {
                foreach (var approval in approvals)
                {
                    var reason = ScopedApprovalProblem(approval);
                    if (reason != null)
                        Add("SHR-PROFILE-SCOPED-APPROVAL", "אישור ממוקד בפרופיל אינו שלם", reason);
                }
                var duplicateIds = approvals.Where(a => a?.ApprovalId != null)
                    .GroupBy(a => a!.ApprovalId!, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
                if (duplicateIds.Count > 0)
                    Add("SHR-PROFILE-SCOPED-APPROVAL", "אותו אישור ממוקד מופיע פעמיים",
                        "Duplicate approval_id: " + string.Join(", ", duplicateIds));
            }
            return findings;
        }

        private static string? ScopedApprovalProblem(ProjectProfile.EstimateProfile.ScopedCatalogApproval? approval)
        {
            if (approval == null) return "Empty scoped approval entry.";
            var item = approval.ItemApproval;
            var hashes = new[]
            {
                approval.ApprovalId, approval.PartitionHash, approval.MembersHash, approval.SpecificationHash,
                approval.EvidenceHash, approval.SourceScopeHash, item?.ApprovedCatalogHash, item?.ApprovedCatalogItemFingerprint,
            };
            if (hashes.Any(hash => !CatalogIdentity.IsValidSha256(hash))) return "A binding hash is missing or malformed.";
            if (approval.Status is not ("active" or "superseded" or "revoked")) return $"Unknown status '{approval.Status}'.";
            if (approval.Status == "superseded" && !CatalogIdentity.IsValidSha256(approval.SupersededBy))
                return "A superseded approval names its successor in superseded_by.";
            if (approval.Status == "revoked" && string.IsNullOrWhiteSpace(approval.RevokedReason))
                return "A revoked approval keeps its revoked_reason.";
            if (string.IsNullOrWhiteSpace(approval.WholeGroupId) || string.IsNullOrWhiteSpace(approval.RawRuleKey))
                return "Whole group id and raw rule key are required.";
            var members = approval.MemberRecordIds ?? new List<string>();
            if (members.Count == 0 || members.Any(string.IsNullOrWhiteSpace) ||
                members.Distinct(StringComparer.Ordinal).Count() != members.Count)
                return "Member record ids must be present, non-empty and unique.";
            if (item == null || string.IsNullOrWhiteSpace(item.CatalogCode) || string.IsNullOrWhiteSpace(item.ApprovedCatalogId) ||
                string.IsNullOrWhiteSpace(item.ExpectedUnit) || string.IsNullOrWhiteSpace(item.ApprovedBy) ||
                item.ApprovedAtUtc == null)
                return "The item approval is incomplete.";
            // A Local/Unspecified time would hash differently after save and reopen; refused, not converted (P2-1).
            if (item.ApprovedAtUtc.Value.Kind != DateTimeKind.Utc)
                return "approved_at_utc must be a UTC time.";
            if (string.IsNullOrWhiteSpace(approval.Reason)) return "An engineering reason is required.";
            return null;
        }

        /// <summary>UTC kind for the schema-5 approval times after a YAML load, like the family-decision times.</summary>
        public static void NormalizeTimestamps(ProjectProfile.EstimateProfile? estimate)
        {
            if (estimate?.SourceSelection?.ApprovedAtUtc is { } scopeTime)
                estimate.SourceSelection.ApprovedAtUtc = FamilyDecisionPolicy.ToUtc(scopeTime);
            foreach (var approval in estimate?.ScopedCatalogApprovals ?? new List<ProjectProfile.EstimateProfile.ScopedCatalogApproval>())
                if (approval?.ItemApproval?.ApprovedAtUtc is { } time)
                    approval.ItemApproval.ApprovedAtUtc = FamilyDecisionPolicy.ToUtc(time);
        }
    }
}
