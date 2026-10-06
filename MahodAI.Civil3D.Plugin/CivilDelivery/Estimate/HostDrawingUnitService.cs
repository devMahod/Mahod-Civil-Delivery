using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>One physical-unit boundary for the active host. Never mutates INSUNITS
/// or reinterprets the unit metadata of an external drawing.</summary>
internal static class HostDrawingUnitService
{
    internal static PhysicalDrawingUnitPolicy.Resolution Resolve(Database db, ProjectProfile? profile) =>
        Resolve((int)db.Insunits, db.FingerprintGuid, db.Filename, profile, Extents(db), CivilUnits(db));

    // Host-free boundary also used by the native adapter above. A missing
    // profile differs from a present profile with an invalid null list.
    internal static PhysicalDrawingUnitPolicy.Resolution Resolve(int rawUnitCode,
        string? drawingFingerprint, string? drawingPath, ProjectProfile? profile,
        PhysicalDrawingUnitPolicy.HostExtents? hostExtents = null,
        PhysicalDrawingUnitPolicy.CivilUnitEvidence? civilUnits = null)
    {
        if (profile != null && profile.DrawingUnitDeclarations == null)
            return PhysicalDrawingUnitPolicy.Failure(rawUnitCode,
                PhysicalDrawingUnitPolicy.DeclarationInvalid, "The profile unit-declaration list is null.");
        return PhysicalDrawingUnitPolicy.Resolve(rawUnitCode, drawingFingerprint,
            drawingPath, profile?.DrawingUnitDeclarations, hostExtents: hostExtents, civilUnits: civilUnits,
            reviews: profile?.DrawingUnitReviews);
    }

    // b24: the saved EXTMIN/EXTMAX header values, read only. An empty drawing reports inverted extents, which the
    // policy treats as "no evidence"; nothing here regenerates or edits the drawing.
    internal static PhysicalDrawingUnitPolicy.HostExtents? Extents(Database db)
    {
        try
        {
            var min = db.Extmin; var max = db.Extmax;
            return new PhysicalDrawingUnitPolicy.HostExtents(min.X, min.Y, max.X, max.Y);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }

    // b24 (Codex 12:04 D): Civil drawing settings → units, read only (Settings › Drawing › Units and Zone), by the
    // DrawingUnitType member name. Any failure is "unreadable" evidence, never a guess and never metres.
    internal static PhysicalDrawingUnitPolicy.CivilUnitEvidence CivilUnits(Database db)
    {
        try
        {
            var civil = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
            if (civil == null) return PhysicalDrawingUnitPolicy.CivilUnitEvidence.None;
            // b26 (LA-40 live; Codex 17:57): the 2027 API names only Feet and Meters, so the value decides — any other value
            // is an unmapped reading (no unit claimed), never "unreadable" (LA-40 read as unreadable in b24/b25).
            var units = civil.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits;
            return PhysicalDrawingUnitPolicy.CivilUnitsFromApi((int)units, units.ToString());
        }
        catch (System.Exception ex)
        {
            return PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static DrawingUnitPolicy.Scale Scale(PhysicalDrawingUnitPolicy.Resolution units) =>
        new(units.IsSupported, units.LinearToMetres, "INSUNITS=" + units.RawUnitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            units.IsSupported ? "מטר" : "יחידת שרטוט",
            units.IsSupported ? "מ\"ר" : "יחידת שרטוט²",
            units.IsSupported ? "מ\"ק" : "יחידת שרטוט³");

    internal static DeliveryFinding? Finding(PhysicalDrawingUnitPolicy.Resolution units, string profileId)
    {
        if (units.IsSupported) return null;
        var reviewNeeded = units.NeedsReview;
        return new DeliveryFinding
        {
            Code = EstimateFindingCodes.UnitUnknown, Domain = "estimate", Severity = FindingSeverity.Error,
            Title = reviewNeeded
                ? "נדרשת בדיקת יחידות — הכמויות נמדדו ביחידות שרטוט ואינן מתומחרות"
                : "היחידות הפיזיות אינן מאושרות — הכמויות אינן מתומחרות כמטרים",
            Message = reviewNeeded
                ? $"INSUNITS={units.RawUnitCode}; {units.Suspicion}"
                : $"INSUNITS={units.RawUnitCode}; {units.FailureCode}: {units.FailureReason}",
            RecommendedAction = reviewNeeded && units.RawUnitCode == PhysicalDrawingUnitPolicy.Unitless
                // b25 (Codex 15:12): a unitless host has no recorded unit to confirm — the way out is a new metres
                // declaration against the Civil evidence shown, or revoking the existing one.
                ? "בלשונית פרויקט פתח יחידות פיזיות: הצהר מחדש מול יחידות Civil המוצגות — עם נימוק, מקור ושם מאשר — או בטל את ההצהרה הקיימת. ה-DWG לא משתנה; לאחר מכן יש לסרוק מחדש."
                : reviewNeeded
                ? "בלשונית פרויקט פתח יחידות פיזיות ובחר: אישור היחידה הרשומה, או יחידה פיזית אחרת לפי ראיה — עם נימוק, מקור ושם מאשר. ה-DWG לא משתנה; לאחר מכן יש לסרוק מחדש."
                : "בלשונית פרויקט פתח יחידות פיזיות. לשרטוט Unitless המצויר במטרים ניתן לשמור הצהרת מהנדס מנומקת, בלי לשנות את DWG; לאחר מכן יש לסרוק מחדש.",
            ProjectProfileId = profileId,
        };
    }
}
