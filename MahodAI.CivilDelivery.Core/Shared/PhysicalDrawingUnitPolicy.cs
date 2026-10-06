using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Separates the observed INSUNITS value from a reviewed physical unit. This
/// declaration applies only to the exact host GUID + saved path. It neither
/// edits INSUNITS nor declares units for attached source drawings.
/// b24 (E4, la-038-ew: INSUNITS=2 and Civil drawing units feet, with coordinates in the ITM metre range; the scan
/// converted ×0.3048 without a word): an explicit unit is still honoured, but host coordinates that only fit the
/// Israeli grid in metres are a suspicion that needs a review before any SI or priced quantity — never a decision.
/// The review either confirms the recorded unit or records a different supported unit, each with its standard factor.
/// </summary>
public static class PhysicalDrawingUnitPolicy
{
    public const int Unitless = 0;
    public const int Metres = 6;
    public const string DeclarationInvalid = "SHR-DRAWING-UNIT-DECLARATION-INVALID";
    public const string DeclarationDuplicate = "SHR-DRAWING-UNIT-DECLARATION-DUPLICATE";
    public const string UnitUnknown = "SHR-DRAWING-UNIT-UNKNOWN";
    public const string IdentityInvalid = "SHR-DRAWING-UNIT-IDENTITY-INVALID";
    public const string OriginalUnitChanged = "SHR-DRAWING-UNIT-SOURCE-CHANGED";
    public const string ReviewNeeded = "SHR-DRAWING-UNIT-REVIEW-NEEDED";
    /// <summary>b24 (Codex 11:48): a reviewed decision exists for a related identity (same GUID or same saved path —
    /// SaveAs, a copied or replaced file) and set another unit than the one this drawing records. The recorded unit is
    /// not used silently; the drawing needs its own review.</summary>
    public const string IdentityChanged = "SHR-DRAWING-UNIT-IDENTITY-CHANGED";
    /// <summary>b24 (Codex 12:04 D): the Civil drawing-unit evidence a reviewed decision relied on changed or was lost.</summary>
    public const string EvidenceChanged = "SHR-DRAWING-UNIT-EVIDENCE-CHANGED";
    public const string ReviewInvalid = "SHR-DRAWING-UNIT-REVIEW-INVALID";
    /// <summary>b24 (Codex 13:02 §2): a reviewed explicit-unit decision without the review record of the same drawing
    /// that names its digest. Never completed silently; re-deciding in the units review writes both together.</summary>
    public const string DecisionUnlinked = "SHR-DRAWING-UNIT-DECISION-UNLINKED";

    /// <summary>Kinds of a technical unit-review record (b24, Codex 12:24 C).</summary>
    public const string ReviewSuspicion = "suspicion", ReviewConflict = "conflict", ReviewIdentity = "identity",
        ReviewDecision = "decision";
    public const string TechnicalRecorder = "Mahod Civil Delivery — technical record, not an approval";

    /// <summary>Civil 3D drawing settings → units, read only. Status first: absent, unreadable and observed differ.</summary>
    public sealed record CivilUnitEvidence(string Status, int? UnitCode, string? Detail = null)
    {
        public const string NotApplicable = "not-applicable", Unreadable = "unreadable", Observed = "observed";
        /// <summary>b26 (LA-40 live, 02.10 17:39; Codex 17:57): Civil returned a drawing-unit value the managed API does
        /// not name. In Civil 2027 <c>Autodesk.Civil.Settings.DrawingUnitType</c> defines only Feet=30 and Meters=2. Such a
        /// value proves no unit — not even a valid one — and is never mapped through COM or INSUNITS: this status is the
        /// domain of that managed enum, and <see cref="UnitCode"/> then holds its opaque value (not an INSUNITS code), so a
        /// later different value is a different reading.</summary>
        public const string ObservedOther = "observed-other";
        public static CivilUnitEvidence None { get; } = new(NotApplicable, null);
        public static CivilUnitEvidence Failed(string detail) => new(Unreadable, null, detail);
        public bool IsValid => Status switch
        {
            Observed => UnitCode is { } code && StandardMetresPerUnit(code) != null,
            ObservedOther => UnitCode is { } raw && raw != CivilApiMeters && raw != CivilApiFeet,
            NotApplicable or Unreadable => UnitCode == null,
            _ => false,
        };
    }

    /// <summary>The two values Civil 2027 names in <c>Autodesk.Civil.Settings.DrawingUnitType</c> (assembly metadata).</summary>
    public const int CivilApiMeters = 2, CivilApiFeet = 30;

    /// <summary>b26: Civil drawing units by the API value: metres and feet as observed INSUNITS codes, any other value
    /// as <see cref="CivilUnitEvidence.ObservedOther"/> — never a guessed unit and never "unreadable".</summary>
    public static CivilUnitEvidence CivilUnitsFromApi(int value, string? name) => value switch
    {
        CivilApiMeters => new(CivilUnitEvidence.Observed, 6),
        CivilApiFeet => new(CivilUnitEvidence.Observed, 2),
        _ => CivilUnitsFromName(name) is { Status: CivilUnitEvidence.Observed } named
            ? named
            : new(CivilUnitEvidence.ObservedOther, value, "DrawingUnitType=" + value.ToString(CultureInfo.InvariantCulture)),
    };

    /// <summary>How a Civil reading compares with a physical unit (Codex 17:57: "cannot compare" is never "agrees").</summary>
    public enum CivilComparison { NoEvidence, Agrees, Contradicts, Unmapped }

    /// <summary>
    /// b26: one comparison for every consumer. An observed unit agrees when its standard factor is the unit's, otherwise it
    /// contradicts; a value the API does not name is unmapped — it can neither confirm nor deny any unit; an unreadable
    /// or absent reading is no evidence.
    /// </summary>
    public static CivilComparison CompareCivil(CivilUnitEvidence civil, double? metresPerUnit) => civil.Status switch
    {
        CivilUnitEvidence.Observed => civil.UnitCode is { } code && StandardMetresPerUnit(code) == metresPerUnit
            ? CivilComparison.Agrees : CivilComparison.Contradicts,
        CivilUnitEvidence.ObservedOther => CivilComparison.Unmapped,
        _ => CivilComparison.NoEvidence,
    };

    /// <summary>A reading that keeps a unit from being used without an explicit decision: it contradicts the unit, or
    /// it cannot be compared with it. Unmapped evidence needs the review in every raw unit — it never yields automatic SI.</summary>
    public static bool NeedsCivilReview(CivilUnitEvidence civil, double? metresPerUnit) =>
        CompareCivil(civil, metresPerUnit) is CivilComparison.Contradicts or CivilComparison.Unmapped;

    /// <summary>The UnitsValue code of a Civil DrawingUnitType member name (enum names, never INSUNITS numbers).</summary>
    public static CivilUnitEvidence CivilUnitsFromName(string? drawingUnitTypeName) => drawingUnitTypeName switch
    {
        "Feet" => new(CivilUnitEvidence.Observed, 2),
        "Meters" => new(CivilUnitEvidence.Observed, 6),
        "Millimeters" => new(CivilUnitEvidence.Observed, 4),
        "Centimeters" => new(CivilUnitEvidence.Observed, 5),
        "Decimeters" => new(CivilUnitEvidence.Observed, 14),
        "Inches" => new(CivilUnitEvidence.Observed, 1),
        null => CivilUnitEvidence.Failed("Civil drawing units were not returned."),
        _ => CivilUnitEvidence.Failed("Unknown Civil drawing unit '" + drawingUnitTypeName + "'."),
    };

    /// <summary>Decision kinds for a host with an explicit INSUNITS. A declaration without a kind is the original
    /// unitless-host metres declaration; its YAML, evidence and digest are unchanged.</summary>
    public const string ConfirmRecordedUnit = "confirm-recorded-unit";
    public const string DifferentPhysicalUnit = "different-physical-unit";
    /// <summary>b25 (Codex 15:12): a unitless host declared metres by the units review, bound to the Civil drawing-unit
    /// evidence it was made against and closed by its review record like every other kind. The kind-less form above is
    /// only read from existing profiles; it never gains these fields, so its bytes and digest stay as approved.</summary>
    public const string UnitlessMetres = "unitless-metres";

    /// <summary>The physical units an engineer can choose as "a different unit by evidence" — each with its standard
    /// factor from <see cref="StandardMetresPerUnit"/>, never a typed number.</summary>
    public static IReadOnlyList<int> DeclarableUnits { get; } = new[] { 6, 4, 5, 14, 2, 21, 1 };

    /// <summary>Host model-space extents in drawing units (EXTMIN/EXTMAX, x and y only).</summary>
    public sealed record HostExtents(double MinX, double MinY, double MaxX, double MaxY);

    public sealed record Resolution(
        bool IsSupported,
        int RawUnitCode,
        int? EffectiveUnitCode,
        double LinearToMetres,
        bool UsedDeclaration,
        string? DeclarationDigest,
        string Evidence,
        string? FailureCode,
        string? FailureReason)
    {
        public bool AllowsMetricSections => IsSupported && EffectiveUnitCode == Metres;

        /// <summary>The units must be reviewed (suspicion, conflict or a related decision); the way out is the units review.</summary>
        public bool NeedsReview => FailureCode is ReviewNeeded or IdentityChanged or EvidenceChanged or DecisionUnlinked;

        /// <summary>Where the effective unit comes from: explicit-insunits, approved-unitless-host-declaration,
        /// approved-recorded-unit, approved-different-unit, review-needed or unresolved.</summary>
        public string Authority { get; init; } = "unresolved";
        public string? DecisionKind { get; init; }
        /// <summary>Why the units need a review (Hebrew, for the engineer); null when no review is needed.</summary>
        public string? Suspicion { get; init; }
        /// <summary>What kind of review (suspicion / conflict / identity) a NeedsReview result records; null otherwise.</summary>
        public string? ReviewKind { get; init; }
        /// <summary>b25 (Codex 16:10 P2-2): the need lasts only while this reading lasts — Civil evidence lost, or a new
        /// reading that does not contradict the decided unit. It blocks meanwhile but is never recorded, so a passing
        /// unreadable state does not become a review of its own.</summary>
        public bool Transient { get; init; }
    }

    public static Resolution Resolve(int rawUnitCode, string? drawingFingerprint,
        string? drawingPath,
        IEnumerable<ProjectProfile.DrawingUnitDeclaration>? declarations,
        bool isHostDrawing = true, HostExtents? hostExtents = null, CivilUnitEvidence? civilUnits = null,
        IEnumerable<ProjectProfile.DrawingUnitReview>? reviews = null)
    {
        civilUnits ??= CivilUnitEvidence.None;
        // Host approvals must never be inherited by an XREF, even when a copied
        // source happens to retain the same GUID. The source reader owns scaling.
        if (!isHostDrawing)
            return ExplicitUnit(rawUnitCode);

        var entries = declarations?.ToList() ?? new List<ProjectProfile.DrawingUnitDeclaration>();
        var errors = ValidateDeclarations(entries);
        if (errors.Count != 0)
            return Failure(rawUnitCode, errors[0].Code, errors[0].Message);
        var reviewList = reviews?.ToList() ?? new List<ProjectProfile.DrawingUnitReview>();
        var reviewErrors = ValidateReviews(reviewList);
        if (reviewErrors.Count != 0)
            return Failure(rawUnitCode, reviewErrors[0].Code, reviewErrors[0].Message);

        var validGuid = Guid.TryParse(drawingFingerprint, out var guid) && guid != Guid.Empty;
        var path = CanonicalDrawingPath(drawingPath);
        if (entries.Count != 0 && (!validGuid || path == null))
        {
            // A new drawing can already have explicit units without a saved
            // path. Unrelated declarations do not invalidate those units. A
            // missing GUID cannot prove that declarations are unrelated, and
            // a missing path must never hide a changed unit on an approved GUID.
            if (validGuid && path == null)
            {
                var sameGuid = entries.Where(d => Guid.Parse(d.DrawingFingerprint!) == guid).ToList();
                if (sameGuid.Any(d => rawUnitCode != d.OriginalInsunitsCode))
                    return Failure(rawUnitCode, OriginalUnitChanged,
                        "The observed INSUNITS changed on a declared drawing GUID. Save the drawing and explicitly review the stale declaration before a new PLAN/scan.");
                var explicitUnit = Undeclared(rawUnitCode, hostExtents, civilUnits);
                if (sameGuid.Count == 0 && (explicitUnit.IsSupported || explicitUnit.FailureCode == ReviewNeeded))
                    return explicitUnit;
            }
            return Failure(rawUnitCode, IdentityInvalid,
                "Cannot compare existing unit declarations without the live drawing GUID and saved full DWG path.");
        }

        var declaration = entries.SingleOrDefault(d => MatchesIdentity(d, drawingFingerprint, drawingPath));
        if (declaration != null)
        {
            if (rawUnitCode != declaration.OriginalInsunitsCode)
                return Failure(rawUnitCode, OriginalUnitChanged,
                    "The observed INSUNITS changed after approval. Remove or explicitly review the stale unit declaration before a new PLAN/scan.");

            var digest = DeclarationHash(declaration);
            // A recorded review for this drawing is closed only by the decision whose digest it names (Codex 12:24 C).
            if (reviewList.Any(r => SameIdentity(r.DrawingFingerprint, r.DrawingPath, guid, path!) &&
                                    !string.Equals(r.ResolvedByDigest, digest, StringComparison.Ordinal)))
                return Failure(rawUnitCode, ReviewNeeded,
                    "A recorded unit review for this drawing is not resolved by the current decision. Review the units again.")
                    with { Authority = "review-needed", ReviewKind = ReviewIdentity, Suspicion =
                        "נרשם בירור יחידות לשרטוט הזה שההכרעה הנוכחית אינה סוגרת. נדרשת הכרעה מחדש." };
            if (declaration.DecisionKind != null &&
                !reviewList.Any(r => SameIdentity(r.DrawingFingerprint, r.DrawingPath, guid, path!) &&
                                     string.Equals(r.ResolvedByDigest, digest, StringComparison.Ordinal)))
                return Failure(rawUnitCode, DecisionUnlinked,
                    "A reviewed explicit-unit decision has no review record of this drawing that names its digest. Decide the units again.")
                    with { Authority = "review-needed", ReviewKind = ReviewIdentity, Suspicion =
                        "הכרעת היחידות לשרטוט הזה לא נקשרה לרשומת הבירור שלה. פתח יחידות פיזיות והכרע מחדש — שתיהן נשמרות יחד." };
            if (declaration.DecisionKind != null)
            {
                // The decision relied on the Civil evidence observed when it was made; a changed value, a lost
                // observation or a new one reopens the review (extents are not bound — ordinary edits move them).
                if (!string.Equals(declaration.CivilDrawingUnitStatus, civilUnits.Status, StringComparison.Ordinal) ||
                    declaration.CivilDrawingUnitCode != civilUnits.UnitCode)
                    return Failure(rawUnitCode, EvidenceChanged,
                        "The Civil drawing-unit evidence this decision relied on changed or was lost. Review the units again.")
                        with { Authority = "review-needed", ReviewKind = ReviewConflict, Suspicion = string.Format(CultureInfo.InvariantCulture,
                            "יחידות Civil השתנו מאז ההכרעה (בהכרעה: {0}; עכשיו: {1}). ההכרעה לא תשמש עד בירור חדש.",
                            Describe(declaration.CivilDrawingUnitStatus, declaration.CivilDrawingUnitCode),
                            Describe(civilUnits.Status, civilUnits.UnitCode)),
                            // b25 (Codex 16:10 P2-2): a new observed unit that contradicts the decided one is recorded like
                            // any conflict, so it outlives a later unreadable or reverted reading — b26 (Codex 17:57): so is
                            // a new value the API does not name; only a lost or an agreeing reading is transient.
                            Transient = !NeedsCivilReview(civilUnits, declaration.MetresPerUnit) };
                return ReviewedExplicitUnit(rawUnitCode, declaration, digest, guid, path!);
            }
            // b25 (Codex 15:12): an existing kind-less metres declaration stays as approved, but Civil drawing units
            // observed in another unit contradict it — it is not used until somebody decides again, and nothing in the
            // record changes. Unreadable or absent Civil evidence is neither metres nor a contradiction.
            // b26 (Codex 17:57): a value the API does not name cannot confirm metres either — it is not passed silently.
            if (NeedsCivilReview(civilUnits, 1.0))
                return Failure(rawUnitCode, ReviewNeeded,
                    "The Civil drawing units contradict, or cannot be compared with, the existing unitless→metres declaration. It stays unchanged but is not used; review the units.")
                    with { Authority = "review-needed", ReviewKind = ReviewConflict, Suspicion = string.Format(CultureInfo.InvariantCulture,
                        CompareCivil(civilUnits, 1.0) == CivilComparison.Unmapped
                            ? "יחידות השרטוט ב-Civil ({0}) אינן מאפשרות לאשר את הצהרת המטרים הקיימת לשרטוט הזה. ההצהרה לא שונתה ולא נמחקה, אבל לא תשמש עד הכרעה חדשה."
                            : "יחידות השרטוט ב-Civil ({0}) סותרות את הצהרת המטרים הקיימת לשרטוט הזה. ההצהרה לא שונתה ולא נמחקה, אבל לא תשמש עד הכרעה חדשה.",
                        Describe(civilUnits.Status, civilUnits.UnitCode)) };
            return new Resolution(true, rawUnitCode, Metres, 1.0, true, digest,
                JsonSerializer.Serialize(new
                {
                    contract = "physical-drawing-unit-v1", raw_insunits = rawUnitCode,
                    effective_unit = Metres, metres_per_unit = 1.0,
                    authority = "approved-unitless-host-declaration", declaration_sha256 = digest,
                    drawing_fingerprint = guid.ToString("D"), drawing_path = path,
                    approved_by = declaration.ApprovedBy!.Trim(),
                    approved_at_utc = declaration.ApprovedAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                    reason = declaration.Reason!.Trim(), source = declaration.Source!.Trim(),
                }), null, null) { Authority = "approved-unitless-host-declaration" };
        }

        // No decision for this exact identity. A decision on a related identity that set another unit than the one
        // this drawing records must not silently give way to the recorded unit (Codex 11:48); one that agrees with it,
        // or an unrelated drawing, keeps the ordinary route.
        var undeclared = Undeclared(rawUnitCode, hostExtents, civilUnits);
        // A review recorded for this exact drawing stays open until a decision resolves it — even when the extents or
        // the Civil settings that raised it can no longer be read (Codex 12:24 C).
        if (undeclared.IsSupported && validGuid && path != null &&
            reviewList.Where(r => SameIdentity(r.DrawingFingerprint, r.DrawingPath, guid, path))
                .OrderBy(r => r.ResolvedByDigest != null).FirstOrDefault() is { } recorded)
            return Failure(rawUnitCode, ReviewNeeded,
                "A unit review recorded for this drawing has no decision. Review the units.")
                with { Authority = "review-needed", ReviewKind = recorded.Kind, Suspicion = recorded.Reason };
        if (undeclared.IsSupported && validGuid && path != null && (entries.Any(d =>
                (Guid.Parse(d.DrawingFingerprint!) == guid ||
                 string.Equals(CanonicalDrawingPath(d.DrawingPath), path, StringComparison.OrdinalIgnoreCase)) &&
                d.PhysicalUnitCode != undeclared.EffectiveUnitCode) ||
            reviewList.Any(r => Guid.Parse(r.DrawingFingerprint!) == guid ||
                                string.Equals(CanonicalDrawingPath(r.DrawingPath), path, StringComparison.OrdinalIgnoreCase))))
            return Failure(rawUnitCode, IdentityChanged,
                "A unit decision for the same drawing GUID or the same saved path set another unit than the recorded INSUNITS. This drawing needs its own unit review; no decision is carried over between files.")
                with { Authority = "review-needed", ReviewKind = ReviewIdentity, Suspicion =
                    "לשרטוט קשור (אותו GUID או אותו נתיב — למשל שמירה בשם או העתקה, או מזהה משותף לשרטוט שנבדק) נרשמו בירור או הכרעת יחידות. " +
                    "ההכרעה לא עוברת בין קבצים, והיחידה הרשומה לא תשמש בשקט — נדרש אישור נפרד לשרטוט הזה." };
        return undeclared;
    }

    /// <summary>
    /// A coordinate heuristic, not a CRS: the host extents lie inside the Israeli grid (ITM) as measured in metres
    /// while the recorded unit is neither metres nor unitless. Missing or invalid extents prove nothing either way.
    /// </summary>
    public static string? CoordinateSuspicion(int rawUnitCode, HostExtents? extents)
    {
        if (rawUnitCode == Metres || rawUnitCode == Unitless || extents == null) return null;
        var (x0, y0, x1, y1) = (extents.MinX, extents.MinY, extents.MaxX, extents.MaxY);
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1) ||
            x0 > x1 || y0 > y1) return null;
        if (x0 < 100_000 || x1 > 300_000 || y0 < 350_000 || y1 > 1_350_000) return null;
        return string.Format(CultureInfo.InvariantCulture,
            "קואורדינטות השרטוט (E {0:N0}–{1:N0}, N {2:N0}–{3:N0}) נמצאות בטווח רשת ישראל (ITM) כשהיא נמדדת במטרים, " +
            "אבל היחידה הרשומה בשרטוט אינה מטר. זה חשד בלבד, לא הוכחת יחידה.", x0, x1, y0, y1);
    }

    /// <summary>The standard metres per drawing unit of a supported UnitsValue code; null when unsupported.</summary>
    public static double? StandardMetresPerUnit(int unitCode)
    {
        var unit = ExplicitUnit(unitCode);
        return unit.IsSupported ? unit.LinearToMetres : null;
    }

    public static ProjectProfile.DrawingUnitDeclaration CreateApprovedMetresDeclaration(
        string? drawingFingerprint, string? drawingPath, int rawUnitCode,
        string? approvedBy, DateTime approvedAtUtc, string? reason, string? source) =>
        CreateApprovedDeclaration(drawingFingerprint, drawingPath, rawUnitCode, null, Metres,
            approvedBy, approvedAtUtc, reason, source);

    /// <summary>One reviewed decision for the exact host: a null kind is the unitless→metres declaration; for an
    /// explicit INSUNITS the kind confirms the recorded unit or records a different declarable unit. The factor is
    /// always the standard factor of the physical unit.</summary>
    public static ProjectProfile.DrawingUnitDeclaration CreateApprovedDeclaration(
        string? drawingFingerprint, string? drawingPath, int rawUnitCode, string? decisionKind, int physicalUnitCode,
        string? approvedBy, DateTime approvedAtUtc, string? reason, string? source, CivilUnitEvidence? civilUnits = null)
    {
        // A reviewed explicit-unit decision records the Civil evidence it was made against; the unitless form records none.
        var civil = decisionKind == null ? null : civilUnits ?? CivilUnitEvidence.None;
        var declaration = new ProjectProfile.DrawingUnitDeclaration
        {
            DrawingFingerprint = Guid.TryParse(drawingFingerprint, out var guid)
                ? guid.ToString("D") : drawingFingerprint,
            DrawingPath = CanonicalDrawingPath(drawingPath),
            OriginalInsunitsCode = rawUnitCode,
            PhysicalUnitCode = physicalUnitCode,
            MetresPerUnit = StandardMetresPerUnit(physicalUnitCode) ?? double.NaN,
            DecisionKind = decisionKind,
            CivilDrawingUnitStatus = civil?.Status,
            CivilDrawingUnitCode = civil?.UnitCode,
            Approved = true,
            ApprovedBy = approvedBy?.Trim(),
            ApprovedAtUtc = approvedAtUtc,
            Reason = reason?.Trim(),
            Source = source?.Trim(),
        };
        var errors = ValidateDeclarations(new[] { declaration });
        if (errors.Count != 0)
            throw new ArgumentException(errors[0].Message, nameof(drawingFingerprint));
        return declaration;
    }

    public static bool MatchesIdentity(ProjectProfile.DrawingUnitDeclaration? declaration,
        string? drawingFingerprint, string? drawingPath)
    {
        var path = CanonicalDrawingPath(drawingPath);
        return declaration != null && path != null &&
            Guid.TryParse(drawingFingerprint, out var liveGuid) && liveGuid != Guid.Empty &&
            Guid.TryParse(declaration.DrawingFingerprint, out var approvedGuid) && liveGuid == approvedGuid &&
            string.Equals(path, CanonicalDrawingPath(declaration.DrawingPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lexically canonical Windows saved path; no IO or environment expansion.</summary>
    public static string? CanonicalDrawingPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = value.Trim().Replace('/', '\\');
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.IndexOfAny(new[] { '*', '?', '%', '\0' }) >= 0 ||
                path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return null;
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase)) return null;
            // Reject Windows aliases (trailing dot/space and alternate data streams)
            // instead of letting them bypass the exact path approval boundary.
            var tail = path.Substring(Path.GetPathRoot(path)!.Length);
            if (tail.Contains(':') || tail.Split('\\').Any(p => p.EndsWith(' ') || p.EndsWith('.')))
                return null;
            return path.ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    public static List<DeliveryFinding> ValidateDeclarations(
        IEnumerable<ProjectProfile.DrawingUnitDeclaration>? declarations, string? profileId = null)
    {
        var findings = new List<DeliveryFinding>();
        void Add(string code, string message) => findings.Add(new DeliveryFinding
        {
            Code = code, Domain = "shared", Severity = FindingSeverity.Error,
            Title = "הצהרת היחידות הפיזיות אינה תקפה", Message = message,
            ProjectProfileId = profileId,
            RecommendedAction = "יש לשמור הכרעת יחידות מאושרת אחת לשרטוט המארח המדויק, או להסיר הצהרה ישנה ולבדוק את היחידות מחדש.",
        });
        if (declarations == null)
        {
            Add(DeclarationInvalid, "drawing_unit_declarations must be a list; use an empty list when no declaration exists.");
            return findings;
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in declarations)
        {
            if (d == null)
            {
                Add(DeclarationInvalid, "A drawing unit declaration cannot be null.");
                continue;
            }
            var hasGuid = Guid.TryParse(d.DrawingFingerprint, out var guid) && guid != Guid.Empty;
            var path = CanonicalDrawingPath(d.DrawingPath);
            if (!hasGuid || path == null || !HasValidUnits(d) || !d.Approved ||
                string.IsNullOrWhiteSpace(d.ApprovedBy) ||
                d.ApprovedAtUtc is not { Kind: DateTimeKind.Utc } utc || utc == DateTime.MinValue ||
                string.IsNullOrWhiteSpace(d.Reason) || string.IsNullOrWhiteSpace(d.Source))
                Add(DeclarationInvalid,
                    "A declaration requires a nonempty drawing GUID + fully qualified DWG path, explicit approval, approver, UTC timestamp, reason and source, and one of: observed INSUNITS 0 with physical unit 6 (metres) and factor 1 (kind-less as approved earlier, or unitless-metres with the Civil evidence it was decided against); an explicit INSUNITS confirmed as itself (confirm-recorded-unit); or an explicit INSUNITS recorded as a different declarable unit (different-physical-unit) — always with the standard factor of the physical unit.");
            if (hasGuid && path != null && !identities.Add(guid.ToString("D") + "|" + path))
                Add(DeclarationDuplicate,
                    "More than one unit declaration targets the same drawing GUID and canonical path; keep one reviewed declaration, even when values agree.");
        }
        return findings;
    }

    private static bool HasValidUnits(ProjectProfile.DrawingUnitDeclaration d)
    {
        if (d.MetresPerUnit is not { } factor || !double.IsFinite(factor) || d.OriginalInsunitsCode is not { } raw ||
            d.PhysicalUnitCode is not { } physical) return false;
        if (d.DecisionKind == null ? d.CivilDrawingUnitStatus != null || d.CivilDrawingUnitCode != null
                : d.CivilDrawingUnitStatus == null ||
                  !new CivilUnitEvidence(d.CivilDrawingUnitStatus, d.CivilDrawingUnitCode).IsValid)
            return false;
        return d.DecisionKind switch
        {
            null or UnitlessMetres => raw == Unitless && physical == Metres && factor == 1.0,
            ConfirmRecordedUnit => raw != Unitless && physical == raw && StandardMetresPerUnit(raw) == factor,
            DifferentPhysicalUnit => raw != Unitless && physical != raw && DeclarableUnits.Contains(physical) &&
                StandardMetresPerUnit(physical) == factor,
            _ => false,
        };
    }

    private static Resolution ReviewedExplicitUnit(int rawUnitCode, ProjectProfile.DrawingUnitDeclaration declaration,
        string digest, Guid guid, string path)
    {
        // A reviewed unitless→metres decision keeps the authority every consumer already knows for that route.
        var authority = declaration.DecisionKind switch
        {
            ConfirmRecordedUnit => "approved-recorded-unit",
            UnitlessMetres => "approved-unitless-host-declaration",
            _ => "approved-different-unit",
        };
        var factor = declaration.MetresPerUnit!.Value;
        return new Resolution(true, rawUnitCode, declaration.PhysicalUnitCode, factor, true, digest,
            JsonSerializer.Serialize(new
            {
                contract = "physical-drawing-unit-v1", raw_insunits = rawUnitCode,
                effective_unit = declaration.PhysicalUnitCode, metres_per_unit = factor,
                authority, decision_kind = declaration.DecisionKind, declaration_sha256 = digest,
                civil_drawing_unit_status = declaration.CivilDrawingUnitStatus,
                civil_drawing_unit = declaration.CivilDrawingUnitCode,
                drawing_fingerprint = guid.ToString("D"), drawing_path = path,
                approved_by = declaration.ApprovedBy!.Trim(),
                approved_at_utc = declaration.ApprovedAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                reason = declaration.Reason!.Trim(), source = declaration.Source!.Trim(),
            }), null, null) { Authority = authority, DecisionKind = declaration.DecisionKind };
    }

    // An undeclared host: its explicit unit, unless the Civil drawing units contradict it or its coordinates make it
    // suspect. The raw values stay readable for the review, but nothing is presented as SI until somebody decides.
    private static Resolution Undeclared(int rawUnitCode, HostExtents? hostExtents, CivilUnitEvidence civilUnits)
    {
        var explicitUnit = ExplicitUnit(rawUnitCode);
        if (!explicitUnit.IsSupported) return explicitUnit;
        // b26 (Codex 17:57): an unmapped Civil value needs the review for every recorded unit — never automatic SI.
        if (NeedsCivilReview(civilUnits, explicitUnit.LinearToMetres))
            return Failure(rawUnitCode, ReviewNeeded,
                "The Civil drawing units contradict, or cannot be compared with, INSUNITS. Neither source decides alone; review the units.")
                with { Authority = "review-needed", ReviewKind = ReviewConflict, Suspicion = string.Format(CultureInfo.InvariantCulture,
                    CompareCivil(civilUnits, explicitUnit.LinearToMetres) == CivilComparison.Unmapped
                        ? "יחידות השרטוט ב-Civil ({0}) אינן ניתנות להשוואה ליחידה הרשומה ב-INSUNITS (קוד {1}). נדרשת הכרעה מפורשת לפני מדידה ביחידות פיזיות."
                        : "יחידות השרטוט ב-Civil ({0}) שונות מהיחידה הרשומה ב-INSUNITS (קוד {1}). אף אחת מהן לא קובעת לבדה — נדרשת הכרעה.",
                    Describe(civilUnits.Status, civilUnits.UnitCode), rawUnitCode) };
        if (CoordinateSuspicion(rawUnitCode, hostExtents) is not { } suspicion)
            return explicitUnit;
        return Failure(rawUnitCode, ReviewNeeded,
            "The host coordinates fit the Israeli grid in metres while INSUNITS records another unit. Review the units: confirm the recorded unit or record a different supported unit with its evidence.")
            with { Authority = "review-needed", ReviewKind = ReviewSuspicion, Suspicion = suspicion };
    }

    private static bool SameIdentity(string? fingerprint, string? drawingPath, Guid guid, string canonicalPath) =>
        Guid.TryParse(fingerprint, out var other) && other == guid &&
        string.Equals(CanonicalDrawingPath(drawingPath), canonicalPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the review record belongs to exactly this drawing (GUID + canonical saved path).</summary>
    public static bool IsReviewOf(ProjectProfile.DrawingUnitReview review, string? drawingFingerprint, string? drawingPath) =>
        Guid.TryParse(drawingFingerprint, out var guid) && guid != Guid.Empty && CanonicalDrawingPath(drawingPath) is { } path &&
        SameIdentity(review.DrawingFingerprint, review.DrawingPath, guid, path);

    /// <summary>The digest a resolved review names (the same digest the resolution carries).</summary>
    public static string DigestOf(ProjectProfile.DrawingUnitDeclaration declaration)
    {
        var invalid = ValidateDeclarations(new[] { declaration });
        if (invalid.Count != 0) throw new ArgumentException(invalid[0].Message, nameof(declaration));
        return DeclarationHash(declaration);
    }

    /// <summary>
    /// True when the profile already holds what <paramref name="units"/> would record for this exact drawing: an open
    /// record of it, or — for anything but a conflict — any record of it. A conflict observed after every record of
    /// the drawing was closed (an existing metres declaration that Civil now contradicts) is new: it is recorded again,
    /// so the review cannot vanish when the Civil settings become unreadable later (Codex 15:12).
    /// </summary>
    public static bool IsRecorded(Resolution units, string? drawingFingerprint, string? drawingPath,
        IEnumerable<ProjectProfile.DrawingUnitReview>? existing)
    {
        if (!Guid.TryParse(drawingFingerprint, out var guid) || guid == Guid.Empty ||
            CanonicalDrawingPath(drawingPath) is not { } path) return false;
        var own = existing?.Where(r => r != null && SameIdentity(r.DrawingFingerprint, r.DrawingPath, guid, path)).ToList()
                  ?? new List<ProjectProfile.DrawingUnitReview>();
        return own.Any(r => r.ResolvedByDigest == null) || (own.Count != 0 && units.ReviewKind != ReviewConflict);
    }

    /// <summary>
    /// The technical record to write when a review need is first observed for this exact drawing; null when there is
    /// nothing to record or <see cref="IsRecorded"/> (identical evidence never causes another write).
    /// </summary>
    public static ProjectProfile.DrawingUnitReview? NewReviewRecord(Resolution units, string? drawingFingerprint,
        string? drawingPath, IEnumerable<ProjectProfile.DrawingUnitReview>? existing, DateTime observedAtUtc)
    {
        if (!units.NeedsReview || units.ReviewKind is null || units.Transient) return null;
        if (!Guid.TryParse(drawingFingerprint, out var guid) || guid == Guid.Empty ||
            CanonicalDrawingPath(drawingPath) is not { } path) return null;
        if (IsRecorded(units, drawingFingerprint, drawingPath, existing)) return null;
        return new ProjectProfile.DrawingUnitReview
        {
            DrawingFingerprint = guid.ToString("D"), DrawingPath = path, ObservedInsunitsCode = units.RawUnitCode,
            Kind = units.ReviewKind, Reason = units.Suspicion, Evidence = units.Evidence,
            ObservedAtUtc = DateTime.SpecifyKind(observedAtUtc, DateTimeKind.Utc), RecordedBy = TechnicalRecorder,
        };
    }

    /// <summary>
    /// Within the same profile write as a decision: every review recorded for that exact drawing names the decision's
    /// digest; a reviewed explicit-unit decision without any record leaves one ("decision"), so revoking it later can
    /// never fall back to trusting the raw unit. Revocation never deletes a record (Codex 12:24 C).
    /// </summary>
    public static void LinkReviews(ProjectProfile clone, ProjectProfile.DrawingUnitDeclaration decision, DateTime atUtc)
    {
        var digest = DigestOf(decision);
        var guid = Guid.Parse(decision.DrawingFingerprint!);
        var path = CanonicalDrawingPath(decision.DrawingPath)!;
        var own = clone.DrawingUnitReviews?.Where(r => SameIdentity(r.DrawingFingerprint, r.DrawingPath, guid, path)).ToList()
                  ?? new List<ProjectProfile.DrawingUnitReview>();
        foreach (var record in own) record.ResolvedByDigest = digest;
        if (own.Count == 0 && decision.DecisionKind != null)
            (clone.DrawingUnitReviews ??= new List<ProjectProfile.DrawingUnitReview>()).Add(new ProjectProfile.DrawingUnitReview
            {
                DrawingFingerprint = guid.ToString("D"), DrawingPath = path, ObservedInsunitsCode = decision.OriginalInsunitsCode,
                Kind = ReviewDecision, Reason = "הכרעת יחידות יזומה לשרטוט הזה (לא מחשד).",
                Evidence = "{}", ObservedAtUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc), RecordedBy = TechnicalRecorder,
                ResolvedByDigest = digest,
            });
    }

    /// <summary>Every reviewed explicit-unit decision has the review record of its drawing naming its digest (the
    /// unitless form needs none). The writer refuses a gap; the loader reports it so the units review can repair it.</summary>
    public static List<DeliveryFinding> ValidateDecisionLinks(IEnumerable<ProjectProfile.DrawingUnitDeclaration>? declarations,
        IEnumerable<ProjectProfile.DrawingUnitReview>? reviews, string? profileId = null, FindingSeverity severity = FindingSeverity.Error)
    {
        var findings = new List<DeliveryFinding>();
        var reviewList = reviews?.ToList() ?? new List<ProjectProfile.DrawingUnitReview>();
        foreach (var d in declarations ?? Enumerable.Empty<ProjectProfile.DrawingUnitDeclaration>())
        {
            if (d?.DecisionKind == null || ValidateDeclarations(new[] { d }).Count != 0) continue;
            var guid = Guid.Parse(d.DrawingFingerprint!);
            var path = CanonicalDrawingPath(d.DrawingPath)!;
            var digest = DeclarationHash(d);
            if (reviewList.Any(r => r != null && SameIdentity(r.DrawingFingerprint, r.DrawingPath, guid, path) &&
                                    string.Equals(r.ResolvedByDigest, digest, StringComparison.Ordinal)))
                continue;
            findings.Add(new DeliveryFinding
            {
                Code = DecisionUnlinked, Domain = "shared", Severity = severity, ProjectProfileId = profileId,
                Title = "הכרעת יחידות בלי רשומת הבירור שלה",
                Message = $"The unit decision for {path} has no review record of that drawing naming its digest.",
                RecommendedAction = "פתח יחידות פיזיות לשרטוט הזה והכרע מחדש; ההכרעה ורשומת הבירור נשמרות יחד. אין להשלים רשומה ידנית.",
            });
        }
        return findings;
    }

    public static List<DeliveryFinding> ValidateReviews(IEnumerable<ProjectProfile.DrawingUnitReview>? reviews, string? profileId = null)
    {
        var findings = new List<DeliveryFinding>();
        if (reviews == null) return findings;
        // b25: a drawing keeps its closed records and may gain a new one for a later conflict, but at most one is open.
        var openIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in reviews)
        {
            var guid = Guid.Empty;
            var path = r == null ? null : CanonicalDrawingPath(r.DrawingPath);
            var ok = r != null && Guid.TryParse(r.DrawingFingerprint, out guid) && guid != Guid.Empty && path != null &&
                     r.Kind is ReviewSuspicion or ReviewConflict or ReviewIdentity or ReviewDecision &&
                     !string.IsNullOrWhiteSpace(r.Reason) && r.Evidence != null &&
                     r.ObservedAtUtc is { Kind: DateTimeKind.Utc } && !string.IsNullOrWhiteSpace(r.RecordedBy) &&
                     (r.ResolvedByDigest == null || System.Text.RegularExpressions.Regex.IsMatch(r.ResolvedByDigest, "^[a-f0-9]{64}$")) &&
                     (r.ResolvedByDigest != null || openIdentities.Add(guid.ToString("D") + "|" + path));
            if (!ok)
                findings.Add(new DeliveryFinding
                {
                    Code = ReviewInvalid, Domain = "shared", Severity = FindingSeverity.Error,
                    Title = "רשומת בירור היחידות אינה תקפה", ProjectProfileId = profileId,
                    Message = "A unit review needs a drawing GUID + fully qualified DWG path (at most one open record per drawing), a known kind, a reason, evidence, a UTC time, a recorder and, when resolved, a SHA-256 decision digest.",
                    RecommendedAction = "יש לשחזר את הפרופיל מגיבוי או לסרוק מחדש; אין למחוק רשומת בירור כדי לעקוף אותה.",
                });
        }
        return findings;
    }

    private static string Describe(string? status, int? code) => status switch
    {
        CivilUnitEvidence.Observed => "קוד יחידה " + code?.ToString(CultureInfo.InvariantCulture),
        CivilUnitEvidence.ObservedOther => "ערך שה-API של Civil אינו ממפה ליחידה: " + code?.ToString(CultureInfo.InvariantCulture),
        CivilUnitEvidence.Unreadable => "לא קריא",
        CivilUnitEvidence.NotApplicable => "לא רלוונטי",
        _ => "לא נרשם",
    };

    private static string DeclarationHash(ProjectProfile.DrawingUnitDeclaration d) =>
        ArtifactHash.Sha256OfText(d.DecisionKind == null
            ? JsonSerializer.Serialize(new
            {
                contract = "physical-drawing-unit-v1",
                drawing_fingerprint = Guid.Parse(d.DrawingFingerprint!).ToString("D"),
                drawing_path = CanonicalDrawingPath(d.DrawingPath),
                original_insunits = d.OriginalInsunitsCode,
                physical_unit = d.PhysicalUnitCode, metres_per_unit = d.MetresPerUnit,
                approved = d.Approved, approved_by = d.ApprovedBy!.Trim(),
                approved_at_utc = d.ApprovedAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                reason = d.Reason!.Trim(), source = d.Source!.Trim(),
            })
            // A reviewed explicit unit also binds its decision kind (b24); the unitless digest above is unchanged.
            : JsonSerializer.Serialize(new
            {
                contract = "physical-drawing-unit-v1",
                drawing_fingerprint = Guid.Parse(d.DrawingFingerprint!).ToString("D"),
                drawing_path = CanonicalDrawingPath(d.DrawingPath),
                original_insunits = d.OriginalInsunitsCode,
                physical_unit = d.PhysicalUnitCode, metres_per_unit = d.MetresPerUnit,
                approved = d.Approved, approved_by = d.ApprovedBy!.Trim(),
                approved_at_utc = d.ApprovedAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                reason = d.Reason!.Trim(), source = d.Source!.Trim(),
                decision_kind = d.DecisionKind,
                civil_drawing_unit_status = d.CivilDrawingUnitStatus, civil_drawing_unit = d.CivilDrawingUnitCode,
            }));

    private static Resolution ExplicitUnit(int unitCode)
    {
        // UnitsValue numeric ABI, shared by both supported host versions.
        var factor = unitCode switch
        {
            1 => 0.0254, 2 => 0.3048, 3 => 1609.344, 4 => 0.001,
            5 => 0.01, 6 => 1.0, 7 => 1000.0, 8 => 0.0000000254,
            9 => 0.0000254, 10 => 0.9144, 11 => 1e-10, 12 => 1e-9,
            13 => 1e-6, 14 => 0.1, 15 => 10.0, 16 => 100.0,
            17 => 1e9, 18 => 149_597_870_700.0, 19 => 9_460_730_472_580_800.0,
            20 => 30_856_775_814_913_673.0, 21 => 1200.0 / 3937.0,
            22 => 100.0 / 3937.0, 23 => 3600.0 / 3937.0, 24 => 6_336_000.0 / 3937.0,
            _ => 0.0,
        };
        if (factor <= 0)
            return Failure(unitCode, UnitUnknown,
                "No supported physical unit is established. Unitless host drawings require an explicit approved declaration; source XREF units are reviewed independently.");
        return new Resolution(true, unitCode, unitCode, factor, false, null,
            JsonSerializer.Serialize(new { contract = "physical-drawing-unit-v1", raw_insunits = unitCode,
                effective_unit = unitCode, metres_per_unit = factor, authority = "explicit-insunits" }), null, null)
            { Authority = "explicit-insunits" };
    }

    /// <summary>Shared unsupported result for policy and host-adapter failures;
    /// keeps the evidence schema valid without granting any physical unit.</summary>
    public static Resolution Failure(int rawUnitCode, string code, string reason) =>
        // Identity factor preserves raw evidence values; IsSupported=false and
        // effective=null forbid claiming SI. A finite value keeps failure evidence
        // serializable with strict JSON (never NaN/Infinity in the contract).
        new(false, rawUnitCode, null, 1.0, false, null,
            JsonSerializer.Serialize(new { contract = "physical-drawing-unit-v1", raw_insunits = rawUnitCode,
                effective_unit = (int?)null, authority = "unresolved", failure_code = code }), code, reason);
}
