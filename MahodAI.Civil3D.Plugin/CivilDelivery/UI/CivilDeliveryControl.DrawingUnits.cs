using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void RefreshDrawingUnitsSummary(bool profileUsable)
    {
        if (!profileUsable || _profile == null || Doc() is not { } document)
        {
            DrawingUnitsSummary.Text = "פתח שרטוט ופרופיל תקין כדי לבדוק יחידות פיזיות.";
            ShowUnitsLine(DrawingUnitDeclarationReview.UnitsLine(null, null));
            return;
        }
        try
        {
            // b24: the same resolution the scan uses (host extents included), shown in the project tab and as one
            // fixed line next to "סרוק כמויות" — before anybody relies on SI quantities or prices.
            var resolved = HostDrawingUnitService.Resolve((int)document.Database.Insunits,
                document.Database.FingerprintGuid, document.Name, _profile,
                HostDrawingUnitService.Extents(document.Database), HostDrawingUnitService.CivilUnits(document.Database));
            DrawingUnitsSummary.Text = DrawingUnitDeclarationReview.Describe(resolved);
            ShowUnitsLine(DrawingUnitDeclarationReview.UnitsLine(resolved, null));
        }
        catch (Exception ex)
        {
            DrawingUnitsSummary.Text = "לא ניתן לקרוא את זהות היחידות: " + ex.Message;
            ShowUnitsLine(DrawingUnitDeclarationReview.UnitsLine(null, ex.Message));
        }
    }

    /// <summary>
    /// b24 (Codex 12:24 C): before a scan, a review need observed for this exact drawing is written once into the profile
    /// (schema 7) as a technical record — not an approval — so it cannot vanish when the extents or the Civil settings
    /// cannot be read later. Identical evidence writes nothing. A failed write starts no scan and gives no SI.
    /// </summary>
    private bool EnsureUnitReviewRecorded(Autodesk.AutoCAD.ApplicationServices.Document document)
    {
        if (_profile == null) return true;
        try
        {
            // Cheap first look with the open document; the decision scope (CAS evidence) is captured only to write.
            var live = document.Database;
            var quick = HostDrawingUnitService.Resolve((int)live.Insunits, live.FingerprintGuid, document.Name, _profile,
                HostDrawingUnitService.Extents(live), HostDrawingUnitService.CivilUnits(live));
            var record = PhysicalDrawingUnitPolicy.NewReviewRecord(quick, live.FingerprintGuid, document.Name,
                _profile.DrawingUnitReviews, DateTime.UtcNow);
            if (record == null) return true;
            // Codex 13:02 §3: once a review need is observed, no earlier scan or estimate of this palette is usable —
            // invalidated before the write is attempted, and it stays invalid if the write fails.
            InvalidateEstimateEvidence("נדרשת בדיקת יחידות לשרטוט הזה — תוצאות סריקה ואומדן קודמות אינן תקפות.");
            var scope = CaptureProfileDecisionScope("רישום בירור יחידות");
            if (!ReferenceEquals(scope.Document, document))
                throw new InvalidOperationException("השרטוט הפעיל השתנה — בירור היחידות לא נרשם.");
            var db = scope.Document.Database;
            // The first observation is what gets recorded: a second read that no longer sees the reason (for example
            // unreadable extents) never cancels it. It must name the drawing of the verified scope, or nothing is written.
            if (!PhysicalDrawingUnitPolicy.IsReviewOf(record, db.FingerprintGuid, scope.Identity.DrawingPath))
                throw new InvalidOperationException("זהות השרטוט השתנתה בין התצפית לרישום — בירור היחידות לא נרשם, והסריקה לא תתחיל.");
            // The same rule as the observation (b25, Codex 15:12): an open record of this drawing, or any record unless
            // this is a new conflict found after its earlier review was closed.
            if (PhysicalDrawingUnitPolicy.IsRecorded(quick, db.FingerprintGuid, scope.Identity.DrawingPath,
                    scope.Profile.DrawingUnitReviews))
                return true;   // already recorded in the verified profile
            if (!scope.ExpectedState.SourceExisted)
                throw new InvalidOperationException("אין פרופיל שמור לפרויקט — בירור היחידות לא נרשם ולא נוצר פרופיל חדש.");
            var updated = CloneProfileForDecision(scope.Profile);
            (updated.DrawingUnitReviews ??= new List<ProjectProfile.DrawingUnitReview>()).Add(record);
            RequireProfileDecisionScope(scope);
            var saved = ProjectProfileWriter.SaveTechnicalRecord(updated, scope.ExpectedState.TargetPath,
                "technical: unit review pending (" + record.Kind + ")", scope.ExpectedState);
            PublishSavedProfile(saved);
            Log("נרשם בירור יחידות לשרטוט הזה (רשומה טכנית, לא אישור): " + record.Reason);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("רישום בירור יחידות", ex);
            SetStatus("בירור היחידות לא נרשם — הסריקה לא התחילה, ואין כמויות במטרים. נסה שוב לאחר טעינת הפרופיל.");
            return false;
        }
    }

    private void ShowUnitsLine((string Text, string? ToolTip) line)
    {
        EstimateUnitsLine.Text = line.Text;
        EstimateUnitsLine.ToolTip = line.ToolTip;
    }

    private void OnDrawingUnits(object sender, RoutedEventArgs e)
    {
        if (_busyProgress != null || _pendingWorkflowSaveDocument != null) return;
        if (Doc() is not { } document || _profile == null)
        {
            SetStatus("פתח שרטוט ופרופיל תקין לפני בדיקת יחידות פיזיות.");
            return;
        }
        try
        {
            // b24: an explicit INSUNITS is reviewable too (confirm it, or record a different unit by evidence); the
            // fixed units line already shows the value, so opening this is a decision and needs the saved file.
            if (!EnsureSavedForAction("הצהרת יחידות פיזיות", OnDrawingUnits)) return;
            var scope = CaptureProfileDecisionScope("הצהרת יחידות פיזיות");
            var fingerprint = scope.Document.Database.FingerprintGuid;
            var raw = (int)scope.Document.Database.Insunits;
            var civilUnits = HostDrawingUnitService.CivilUnits(scope.Document.Database);
            var current = HostDrawingUnitService.Resolve(raw, fingerprint, scope.Identity.DrawingPath, scope.Profile,
                HostDrawingUnitService.Extents(scope.Document.Database), civilUnits);
            var previous = scope.Profile.DrawingUnitDeclarations.FirstOrDefault(item =>
                PhysicalDrawingUnitPolicy.MatchesIdentity(item, fingerprint, scope.Identity.DrawingPath));
            var currentEvidence = DrawingUnitDeclarationReview.Describe(current) + (previous == null ? "" :
                $"\nהצהרה קודמת: {previous.ApprovedBy} · {previous.ApprovedAtUtc:yyyy-MM-dd HH:mm} UTC" +
                $"\nנימוק: {previous.Reason}\nמקור: {previous.Source}");
            var review = new DrawingUnitsReviewDialog(new(scope.Identity.DrawingPath,
                fingerprint, raw, scope.Identity.DrawingHash, currentEvidence, previous != null, current.Suspicion,
                ApproverContext.Session.Name, civilUnits));
            CivilModalHost.ShowFromPalette(review);
            var decision = review.ApprovedDeclaration;
            if (decision == null && !review.RevocationRequested)
            {
                SetStatus("בדיקת היחידות הסתיימה ללא שינוי."); return;
            }
            // b25 (Codex 16:10 P2-1): the live drawing — identity, raw unit and, for a decision, the Civil evidence shown —
            // is read again after the dialog and the preview cleanup, against the same original scope.
            var db = scope.Document.Database;
            var updated = DrawingUnitDeclarationReview.PrepareCommit(decision, review.RevocationRequested, fingerprint,
                scope.Identity.DrawingPath, raw, civilUnits,
                () => ((int)db.Insunits, db.FingerprintGuid),
                () => HostDrawingUnitService.CivilUnits(db),
                () => RequireProfileDecisionScope(scope),
                () => TryClearPreview("הצהרת יחידות פיזיות"),
                () => CloneProfileForDecision(scope.Profile), DateTime.UtcNow);
            if (updated == null) return;
            var saved = ProjectProfileWriter.Save(updated, scope.ExpectedState.TargetPath,
                (review.RevocationRequested ? "revoke physical host unit declaration: " : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "physical host units: INSUNITS={0}; {1}; unit={2}; metres_per_unit={3:G9}; ",
                    raw, decision!.DecisionKind ?? "unitless-metres", decision.PhysicalUnitCode, decision.MetresPerUnit)) +
                DrawingUnitDeclarationReview.AuditText(review.Reason.Text) + " | " + DrawingUnitDeclarationReview.AuditText(review.Source.Text),
                DrawingUnitDeclarationReview.AuditText(review.Approver.Text), scope.ExpectedState,
                new Dictionary<string, string> { [scope.Identity.DrawingPath] = scope.Identity.DrawingHash });
            ResetDisplayedSectionPlanAttempt();
            InvalidateEstimateEvidence("הצהרת היחידות השתנתה — יש למדוד מחדש לפני שיוך, תמחור או ייצוא.");
            PublishSavedProfile(saved);
            SectionDetail.Text = "הגדרת היחידות עודכנה — יש להריץ תכנון חתכים מחדש.";
            RefreshDashboard();
            SetStatus(review.RevocationRequested
                ? "ההצהרה לשרטוט הזה בוטלה — היחידות ייקראו מחדש מהמקור. ה-DWG לא שונה."
                : $"הכרעת היחידות נשמרה לשרטוט הזה בלבד ({DrawingUnitDeclarationReview.UnitName(decision!.PhysicalUnitCode ?? -1)}) · אושר ע\"י {decision.ApprovedBy} — כעת תכנון חתכים או סריקת כמויות. ה-DWG לא שונה.");
        }
        catch (Exception ex) { ShowError("יחידות פיזיות", ex); }
        finally { RefreshGates(); }
    }
}

/// <summary>Host-free update of an already isolated profile clone; unrelated declarations survive.</summary>
internal static class DrawingUnitDeclarationReview
{
    // ProjectProfileWriter also emits a comment header. Multiline review fields
    // remain intact in the declaration's YAML, but must not escape that comment.
    internal static string AuditText(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>How a Civil drawing-unit reading is named in the units review (b26: a value the API does not name is
    /// stated as unmapped with its value — never as a guessed unit, never as "unreadable"; Codex 17:57).</summary>
    internal static string CivilName(PhysicalDrawingUnitPolicy.CivilUnitEvidence? civil) => civil?.Status switch
    {
        PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed => UnitName(civil.UnitCode ?? -1),
        PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther =>
            $"ערך שה-API של Civil אינו ממפה ליחידה ({civil.UnitCode})",
        PhysicalDrawingUnitPolicy.CivilUnitEvidence.Unreadable => "לא קריא",
        _ => "לא רלוונטי",
    };

    internal static string UnitName(int code) => code switch
    {
        0 => "ללא יחידה (Unitless)", 1 => "אינץ'", 2 => "רגל", 3 => "מייל", 4 => "מילימטר", 5 => "סנטימטר",
        6 => "מטר", 7 => "קילומטר", 10 => "יארד", 14 => "דצימטר", 21 => "רגל מדידה (US)",
        _ => $"קוד יחידה {code}",
    };

    internal static string Describe(PhysicalDrawingUnitPolicy.Resolution units)
    {
        var observed = $"הגדרת ההכנסה שנקראה: INSUNITS = {units.RawUnitCode} ({UnitName(units.RawUnitCode)}). ";
        if (units.UsedDeclaration && units.DecisionKind is null or PhysicalDrawingUnitPolicy.UnitlessMetres)
            return observed + "היחידה הפיזית היא מטר לפי הצהרה מאושרת לשרטוט הזה בלבד" +
                (units.DecisionKind == null ? "" : ", קשורה ליחידות Civil שנצפו בעת ההכרעה") + ". XREF אינו מאושר באמצעותה.";
        if (units.UsedDeclaration)
            return observed + $"היחידה הפיזית היא {UnitName(units.EffectiveUnitCode ?? -1)} (×{units.LinearToMetres:G9} למטר) " +
                (units.DecisionKind == PhysicalDrawingUnitPolicy.ConfirmRecordedUnit
                    ? "לפי אישור מפורש של היחידה הרשומה" : "לפי הכרעה מפורשת על יחידה שונה") +
                " לשרטוט הזה בלבד. XREF אינו מאושר באמצעותה.";
        if (units.IsSupported)
            return observed + $"יחידה מפורשת במקור; מטרים לכל יחידת שרטוט: {units.LinearToMetres:G9}. אם יש ראיה שהמודל צויר ביחידה אחרת — יחידות פיזיות.";
        return observed + (units.FailureCode == PhysicalDrawingUnitPolicy.OriginalUnitChanged
            ? "היחידה השתנתה מאז האישור. בדוק ובטל את ההצהרה הקודמת לפני תכנון או סריקה חדשים."
            : units.NeedsReview && units.RawUnitCode == PhysicalDrawingUnitPolicy.Unitless
            ? units.Suspicion + " עד הכרעה הכמויות נמדדות ביחידות שרטוט ואינן מתומחרות. ביחידות פיזיות: הצהרה מחודשת מול ראיית Civil, או ביטול ההצהרה."
            : units.NeedsReview
            ? units.Suspicion + " עד הכרעה הכמויות נמדדות ביחידות שרטוט ואינן מתומחרות. ביחידות פיזיות: אישור היחידה הרשומה, או יחידה שונה לפי ראיה."
            : "היחידה הפיזית אינה מוכרעת. אין הנחת מטרים אוטומטית; ניתן להצהיר לאחר בדיקת המקור.");
    }

    /// <summary>The fixed line and its tooltip, from one resolution or one read failure — never a mix of the two, so a
    /// failed refresh leaves no tooltip of an earlier decision (Codex 11:48).</summary>
    internal static (string Text, string? ToolTip) UnitsLine(PhysicalDrawingUnitPolicy.Resolution? units, string? error) =>
        error != null ? ("יחידות: לא ניתן לקרוא — " + error, null)
        : units == null ? ("", null)
        : (Compact(units), Summary(units));

    /// <summary>The one-line form next to "סרוק כמויות": recorded unit → unit used, factor, state.</summary>
    internal static string Compact(PhysicalDrawingUnitPolicy.Resolution units)
    {
        var recorded = $"יחידות: {UnitName(units.RawUnitCode)} ({units.RawUnitCode})";
        if (!units.IsSupported)
            return recorded + (units.NeedsReview
                ? " — לא הוכרעו, נדרשת בדיקה; ללא תמחור" : " — לא הוכרעו; ללא תמחור");
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} → {1} ×{2:G9} · {3}",
            recorded, UnitName(units.EffectiveUnitCode ?? -1), units.LinearToMetres,
            units.UsedDeclaration ? "הוכרע לשרטוט הזה" : "לפי מטא-דאטה");
    }

    /// <summary>The full sentence (tooltip of the scan line) per Codex 11:14: recorded unit, the unit used for quantities, the
    /// factor, its source and the state. Areas and volumes use the square and cube of the linear factor.</summary>
    internal static string Summary(PhysicalDrawingUnitPolicy.Resolution units)
    {
        var recorded = $"הגדרת השרטוט: {UnitName(units.RawUnitCode)} ({units.RawUnitCode})";
        if (!units.IsSupported)
            return recorded + "; יחידה לחישוב: לא הוכרעה — מדידה ביחידות שרטוט בלבד, ללא תמחור; מצב: " +
                (units.FailureCode == PhysicalDrawingUnitPolicy.IdentityChanged ? "נדרשת בדיקת יחידות (הכרעה לשרטוט קשור)"
                    : units.FailureCode == PhysicalDrawingUnitPolicy.EvidenceChanged ? "נדרשת בדיקת יחידות (יחידות Civil השתנו)"
                    // Codex 18:18: the same kind covers a contradiction and an unmapped reading — the label stays neutral.
                    : units.NeedsReview && units.ReviewKind == PhysicalDrawingUnitPolicy.ReviewConflict ? "נדרשת בדיקת יחידות (בירור מול Civil)"
                    : units.NeedsReview ? "נדרשת בדיקת יחידות (חשד)"
                    : units.FailureCode == PhysicalDrawingUnitPolicy.OriginalUnitChanged ? "היחידה השתנתה מאז ההכרעה"
                    : "לא מוכרעת") + " — יחידות פיזיות בלשונית פרויקט.";
        var source = units.Authority switch
        {
            "explicit-insunits" => "הגדרת השרטוט",
            "approved-recorded-unit" => "אישור מפורש של היחידה הרשומה",
            "approved-different-unit" => "הכרעה מפורשת על יחידה שונה",
            _ => "הצהרה מאושרת לשרטוט Unitless",
        };
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}; יחידה לחישוב: {1}; המרה: ×{2:G9} למטר; מקור: {3}; מצב: {4}.",
            recorded, UnitName(units.EffectiveUnitCode ?? -1), units.LinearToMetres, source,
            units.UsedDeclaration ? "הוכרע לשרטוט הזה" : "לפי מטא-דאטה");
    }

    /// <summary>
    /// b25 (Codex 16:10 P2-1): the commit of one units review, host-free. Checks the original scope and the reviewed
    /// identity and raw unit, runs the preview cleanup, checks the scope again and — for a decision — reads the Civil
    /// evidence again: its status and unit code must be the ones shown and bound (a different detail text alone is the
    /// same evidence). Identity, raw unit and scope are checked once more after that reading; only then is the clone
    /// built, and a decision closes the drawing's reviews in it. A revocation needs no Civil reading. Returns null when
    /// the cleanup refused; throws when anything changed — then nothing is linked and nothing is written.
    /// </summary>
    internal static ProjectProfile? PrepareCommit(ProjectProfile.DrawingUnitDeclaration? decision, bool revoke,
        string fingerprint, string drawingPath, int raw, PhysicalDrawingUnitPolicy.CivilUnitEvidence shown,
        Func<(int Raw, string Fingerprint)> readIdentity, Func<PhysicalDrawingUnitPolicy.CivilUnitEvidence> readCivil,
        Action requireScope, Func<bool> clearPreview, Func<ProjectProfile> cloneProfile, DateTime nowUtc)
    {
        if (!revoke && decision == null) throw new ArgumentNullException(nameof(decision));
        if (!revoke && decision!.DecisionKind != null &&
            (!string.Equals(decision.CivilDrawingUnitStatus, shown.Status, StringComparison.Ordinal) ||
             decision.CivilDrawingUnitCode != shown.UnitCode))
            throw new InvalidOperationException("ההכרעה אינה קשורה ליחידות Civil שהוצגו — היא לא נשמרה. פתח אותה מחדש.");
        void RequireSameDrawing()
        {
            var live = readIdentity();
            if (live.Raw != raw || !string.Equals(fingerprint, live.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("זהות השרטוט או יחידותיו השתנו — ההצהרה לא נשמרה. פתח אותה מחדש.");
        }
        requireScope();
        RequireSameDrawing();
        if (!clearPreview()) return null;
        // Preview cleanup can execute host callbacks. Revalidate the same
        // original modal scope, never capture a new baseline after review.
        requireScope();
        if (!revoke)
        {
            var now = readCivil();
            if (!string.Equals(now.Status, shown.Status, StringComparison.Ordinal) || now.UnitCode != shown.UnitCode)
                throw new InvalidOperationException(
                    "יחידות Civil של השרטוט השתנו מאז שנפתחה ההכרעה — ההכרעה לא נשמרה ושום בירור לא נסגר. פתח את יחידות פיזיות מחדש.");
        }
        RequireSameDrawing();
        requireScope();
        var updated = cloneProfile();
        if (revoke) RemoveFromClone(updated, fingerprint, drawingPath);
        else
        {
            ApplyToClone(updated, decision!);
            // The same write closes this drawing's recorded review by the decision's digest (Codex 12:24 C).
            PhysicalDrawingUnitPolicy.LinkReviews(updated, decision!, nowUtc);
        }
        return updated;
    }

    internal static void ApplyToClone(ProjectProfile clone, ProjectProfile.DrawingUnitDeclaration decision)
    {
        ArgumentNullException.ThrowIfNull(clone); ArgumentNullException.ThrowIfNull(decision);
        var invalid = PhysicalDrawingUnitPolicy.ValidateDeclarations(new[] { decision });
        if (invalid.Count != 0) throw new ArgumentException(invalid[0].Message, nameof(decision));
        var checkedDecision = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(
            decision.DrawingFingerprint, decision.DrawingPath, decision.OriginalInsunitsCode ?? -1,
            decision.DecisionKind, decision.PhysicalUnitCode ?? -1,
            decision.ApprovedBy, decision.ApprovedAtUtc ?? default, decision.Reason, decision.Source,
            decision.DecisionKind == null ? null
                : new PhysicalDrawingUnitPolicy.CivilUnitEvidence(decision.CivilDrawingUnitStatus!, decision.CivilDrawingUnitCode));
        RemoveFromClone(clone, checkedDecision.DrawingFingerprint!, checkedDecision.DrawingPath!);
        clone.DrawingUnitDeclarations.Add(checkedDecision);
    }

    internal static void RemoveFromClone(ProjectProfile clone, string fingerprint, string drawingPath)
    {
        ArgumentNullException.ThrowIfNull(clone);
        if (!Guid.TryParse(fingerprint, out var guid) || guid == Guid.Empty ||
            PhysicalDrawingUnitPolicy.CanonicalDrawingPath(drawingPath) == null)
            throw new ArgumentException("זהות השרטוט חסרה — ההצהרה לא בוטלה.");
        clone.DrawingUnitDeclarations.RemoveAll(item =>
            PhysicalDrawingUnitPolicy.MatchesIdentity(item, fingerprint, drawingPath));
    }
}
