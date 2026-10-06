using System;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>UI reuse contract: keep the exact reading; the registry rechecks bytes at commit.</summary>
internal static class KnownPriceBookReuse
{
    // Presentation only for the exact pre-write dirty-drawing refusal. Never weaken the source/CAS gate,
    // retry a registration, or replace an unrelated inner failure with a misleading "save" instruction.
    internal static Exception DescribePreWriteFailure(Exception failure)
    {
        const string prefix = "רישום מחירון מוכר requires one saved, unchanged active drawing: " +
            "the active drawing has unsaved changes (DBMOD=";
        var message = failure.Message;
        if (failure is not InvalidOperationException || !message.StartsWith(prefix, StringComparison.Ordinal) ||
            !message.EndsWith(")", StringComparison.Ordinal) ||
            !int.TryParse(message.Substring(prefix.Length, message.Length - prefix.Length - 1),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var dbmod) || dbmod <= 0)
            return failure;
        return new InvalidOperationException(
            "לפני רישום מחירון מוכר יש לשמור את השרטוט הפתוח.\n\n" +
            $"Civil מסמן שינויים שטרם נשמרו (DBMOD={dbmod}); גם שינוי תצוגה עשוי לדרוש שמירה.\n" +
            "שמור את השרטוט ב-Civil, המתן לסיום השמירה ולחץ שוב על ׳מוכר…׳.\n\n" +
            "לא נרשם מחירון ולא נמחקה הסריקה הקיימת. אם השתנו נתוני המקור, יש לסרוק מחדש לפני המשך העבודה.", failure);
    }

    internal static PriceBookXlsxLoader.ColumnMapping? Mapping(KnownPriceBookIndex.Offer offer) =>
        PriceBookRegistry.LoaderMapping(new ProjectProfile.EstimateProfile.PriceBookEntry
        { FileHash = offer.Entry.Sha256, Mapping = offer.Entry.Mapping });

    // This catches only the convenience-index write, never registration/profile persistence.
    internal static string? Remember(string directory, PriceBookRegistry.RegisterResult registered,
        Action<string, PriceBookRegistry.RegisterResult>? write = null)
    {
        try { (write ?? KnownPriceBookIndex.Remember)(directory, registered); return null; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "המחירון נרשם בפרויקט, אך לא נשמר ברשימת המחירונים המוכרים במחשב זה. " + ex.Message;
        }
    }

    internal static void RequirePublished(ProjectProfile? actual, string? actualHash,
        string? actualTarget, ProjectProfile expected, string expectedHash, string expectedTarget)
    {
        var a = actual == null ? null : PriceBookRegistry.Active(actual);
        var e = PriceBookRegistry.Active(expected);
        if (actual?.ProfileId != expected.ProfileId ||
            !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualTarget, expectedTarget, StringComparison.OrdinalIgnoreCase) ||
            a == null || e == null || a.Id != e.Id ||
            !string.Equals(a.FileHash, e.FileHash, StringComparison.OrdinalIgnoreCase) ||
            !PriceBookRegistry.SameMapping(a.Mapping, e.Mapping))
            throw new InvalidOperationException("המחירון נשמר, אך המסך לא נטען מאותו פרופיל מאומת. אין אישור להמשיך עם מחירון אחר. טען מחדש את פרופיל הפרויקט.");
    }
}
