using System;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

internal static class MeasurementDraftErrorText
{
    // Translate only the observed pre-write saved-DWG mismatch. Unknown errors,
    // post-write withdrawal failures and their recovery semantics stay untouched.
    internal static Exception Describe(Exception error) => error is InvalidOperationException &&
        string.Equals(error.Message,
            "ייצוא מדידות לבדיקה נעצר: מקור השרטוט אינו תואם לסריקה: the saved DWG bytes changed since the quantity scan",
            StringComparison.Ordinal)
        ? new InvalidOperationException(
            "קובץ ה-DWG השמור השתנה מאז סריקת הכמויות, ולכן אי אפשר לייצא את הסריקה הישנה.\n\n" +
            "לחץ על «סרוק כמויות» כדי למדוד מחדש מהקובץ המעודכן, ולאחר סיום הסריקה נסה שוב «ייצא מדידות לבדיקה». " +
            "לא נוצר קובץ ייצוא בניסיון זה.", error)
        : error;
}
