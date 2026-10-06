using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Explains the managed final section, not the diagnostic PLAN preview.
/// Source order is the same EG-first/FG-second contract used by ApplySurfaceStyles.
/// This is a PLAN explanation, never an assertion about the view currently on screen.
/// </summary>
internal static class SectionSurfaceLegendPresentation
{
    internal const string Legend = "מקרא חתך שנוצר: קו פני שטח ירוק מקווקו — מצב קיים; אדום רציף — משטח תכנון. האדום אינו רום הייחוס.";
    internal const string Help = "המקרא מתייחס לקווי פני השטח בחתך של הכלי, לא לצבעי סטטוס או לתצוגת האבחון. המקורות הנבחרים מפורטים בפרטי החתך. «רום קרקע קיימת בציר» הוא גובה המצב הקיים בציר (אם נקודת הייחוס צריכה להיות קרקע קיימת או תכנון — לאישור המהנדס/ת); עיגול מערכת הוא סימון מיקום בלבד, לא קוטר; שיפוע בחתך נמדד במישור החתך, ועשוי להיות שונה משיפוע רכיב בכביש מוטה.";

    internal static string DescribePlannedSources(IEnumerable<SectionSourcePlan> sources)
    {
        var selected = sources.Where(source => source.Required &&
            string.Equals(source.PlannedState, "sampled", StringComparison.Ordinal) &&
            string.Equals(source.SourceType, "surface", StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count != 2 || selected.Any(source => string.IsNullOrWhiteSpace(source.SourceName)) ||
            string.Equals(selected[0].SourceName, selected[1].SourceName, StringComparison.OrdinalIgnoreCase))
            return "מקורות פני השטח: טרם נקבע זוג תקין של מצב קיים ומשטח תכנון; אין להסיק מקור לפי צבע בלבד.";
        return $"מקורות לפי התכנון הנבחר — מצב קיים (ירוק מקווקו): {selected[0].SourceName}; " +
               $"משטח תכנון (אדום רציף): {selected[1].SourceName}.\n" +
               "זהו שיוך בתכנון; התאמת החתך הקיים למקורות נבדקת באמצעות אימות.";
    }
}
