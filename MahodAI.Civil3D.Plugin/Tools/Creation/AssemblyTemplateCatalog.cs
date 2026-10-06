using System;
using System.Collections.Generic;
using System.Globalization;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Pure (AutoCAD-free) catalog of Israeli road-type cross-section templates for
    /// <see cref="CreateAssemblyTool"/>.
    ///
    /// Civil 3D's .NET API cannot build subassemblies (lanes/shoulders/curbs) from scratch,
    /// and no pre-built assembly library exists yet, so the tool ships an EMPTY named shell
    /// plus a Hebrew, ordered guide telling the engineer which Tool Palette subassemblies and
    /// widths to add for the chosen road type. This class owns the road-type catalog
    /// (MOT-2011 default widths) and the deterministic guide generation so both are
    /// unit-testable without a live Civil 3D document.
    /// </summary>
    public static class AssemblyTemplateCatalog
    {
        /// <summary>Canonical fallback road type when none / an invalid one is supplied.</summary>
        public const string DefaultRoadType = "rural_2lane";

        /// <summary>
        /// A resolved cross-section template: default widths plus which structural elements
        /// the section includes (curbs / median / daylight). Widths may be overridden by the
        /// caller via <see cref="Resolve"/>.
        /// </summary>
        public sealed class Template
        {
            /// <summary>Normalized road-type key (one of the supported enum values).</summary>
            public string RoadType { get; init; } = DefaultRoadType;

            /// <summary>Effective lane width in metres (override or catalog default).</summary>
            public double LaneWidth { get; init; }

            /// <summary>Effective shoulder width in metres (override or catalog default).</summary>
            public double ShoulderWidth { get; init; }

            /// <summary>True when the section uses raised curbs (urban / collector).</summary>
            public bool HasCurbs { get; init; }

            /// <summary>True when the section has a central median (divided highway).</summary>
            public bool HasMedian { get; init; }

            /// <summary>Median width in metres (only meaningful when <see cref="HasMedian"/>).</summary>
            public double MedianWidth { get; init; }

            /// <summary>True when the section ties to existing ground with daylight (rural / divided).</summary>
            public bool HasDaylight { get; init; }

            /// <summary>One-line Hebrew description of the cross-section.</summary>
            public string CrossSectionHe { get; init; } = string.Empty;

            /// <summary>True when the requested road type was unknown and the default was substituted.</summary>
            public bool FellBackToDefault { get; init; }
        }

        private sealed class CatalogEntry
        {
            public double LaneWidth { get; init; }
            public double ShoulderWidth { get; init; }
            public bool HasCurbs { get; init; }
            public bool HasMedian { get; init; }
            public double MedianWidth { get; init; }
            public bool HasDaylight { get; init; }
            public string CrossSectionHe { get; init; } = string.Empty;
        }

        // MOT-2011 default widths per road type (see SHARED CONTRACT / road-type catalog).
        private static readonly IReadOnlyDictionary<string, CatalogEntry> _catalog =
            new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["urban_2lane"] = new CatalogEntry
                {
                    LaneWidth = 3.65,
                    ShoulderWidth = 2.5,
                    HasCurbs = true,
                    HasMedian = false,
                    HasDaylight = false,
                    CrossSectionHe = "חתך עירוני דו-נתיבי עם אבני שפה",
                },
                ["rural_2lane"] = new CatalogEntry
                {
                    LaneWidth = 3.75,
                    ShoulderWidth = 2.5,
                    HasCurbs = false,
                    HasMedian = false,
                    HasDaylight = true,
                    CrossSectionHe = "חתך בינעירוני דו-נתיבי עם שוליים ומדרון התחברות לקרקע",
                },
                ["divided_highway"] = new CatalogEntry
                {
                    LaneWidth = 3.75,
                    ShoulderWidth = 3.0,
                    HasCurbs = false,
                    HasMedian = true,
                    MedianWidth = 3.0,
                    HasDaylight = true,
                    CrossSectionHe = "חתך דרך מהירה 2+2 עם רצועת הפרדה מרכזית ומדרון התחברות לקרקע",
                },
                ["collector_local"] = new CatalogEntry
                {
                    LaneWidth = 3.25,
                    ShoulderWidth = 1.5,
                    HasCurbs = true,
                    HasMedian = false,
                    HasDaylight = false,
                    CrossSectionHe = "חתך מאסף/מקומי דו-נתיבי עם אבני שפה",
                },
            };

        /// <summary>The supported road-type enum values, in catalog order.</summary>
        public static IReadOnlyList<string> SupportedRoadTypes { get; } = new[]
        {
            "urban_2lane",
            "rural_2lane",
            "divided_highway",
            "collector_local",
        };

        /// <summary>True when <paramref name="roadType"/> is a recognized catalog key.</summary>
        public static bool IsSupported(string? roadType) =>
            !string.IsNullOrWhiteSpace(roadType) && _catalog.ContainsKey(roadType!.Trim());

        /// <summary>
        /// Resolves a road type (case-insensitive, trimmed) into an effective template.
        /// Unknown / empty road types fall back to <see cref="DefaultRoadType"/> with
        /// <see cref="Template.FellBackToDefault"/> set. Positive width overrides replace the
        /// catalog defaults; non-positive or null overrides are ignored.
        /// </summary>
        public static Template Resolve(
            string? roadType,
            double? laneWidthOverride = null,
            double? shoulderWidthOverride = null)
        {
            // Normalize to the canonical lowercase key so RoadType always reports the
            // catalog's canonical form (e.g. "Divided_Highway " -> "divided_highway"),
            // not the caller's original casing/whitespace.
            string key = (roadType ?? string.Empty).Trim().ToLowerInvariant();
            bool fellBack = !_catalog.ContainsKey(key);
            if (fellBack)
                key = DefaultRoadType;

            var entry = _catalog[key];

            double laneWidth = laneWidthOverride is > 0 ? laneWidthOverride.Value : entry.LaneWidth;
            double shoulderWidth =
                shoulderWidthOverride is > 0 ? shoulderWidthOverride.Value : entry.ShoulderWidth;

            return new Template
            {
                RoadType = key,
                LaneWidth = laneWidth,
                ShoulderWidth = shoulderWidth,
                HasCurbs = entry.HasCurbs,
                HasMedian = entry.HasMedian,
                MedianWidth = entry.MedianWidth,
                HasDaylight = entry.HasDaylight,
                CrossSectionHe = entry.CrossSectionHe,
                FellBackToDefault = fellBack,
            };
        }

        /// <summary>
        /// Builds the ordered, Hebrew, per-element subassembly guide for the resolved template.
        /// Each step names the exact Tool Palette subassembly to add plus its width, in the
        /// order the engineer should add them (center-out): median (divided only) → lanes both
        /// sides → shoulders / curbs per type → daylight (rural / divided).
        /// </summary>
        public static IReadOnlyList<string> BuildSubassemblyGuide(Template template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));

            string lane = Meters(template.LaneWidth);
            string shoulder = Meters(template.ShoulderWidth);
            var steps = new List<string>();
            int n = 1;

            if (template.HasMedian)
            {
                steps.Add(
                    $"{n++}. הוסף רצועת הפרדה מרכזית (Medians ▸ MedianRaisedConstantSlope) " +
                    $"ברוחב {Meters(template.MedianWidth)} מ' במרכז החתך.");
            }

            steps.Add(
                $"{n++}. הוסף נתיב נסיעה משני הצדדים (Lanes ▸ LaneOutsideSuperWithWidening) " +
                $"ברוחב {lane} מ' ושיפוע ‎-2.0%‎ (Left + Right).");

            if (template.HasCurbs)
            {
                steps.Add(
                    $"{n++}. הוסף שוליים מרוצפים משני הצדדים (Shoulders ▸ ShoulderExtendSubbase) " +
                    $"ברוחב {shoulder} מ' (Left + Right).");
                steps.Add(
                    $"{n++}. הוסף אבני שפה משני הצדדים (Curbs ▸ UrbanCurbGutterGeneral) " +
                    "בקצה השוליים (Left + Right).");
            }
            else
            {
                steps.Add(
                    $"{n++}. הוסף שוליים לא מרוצפים משני הצדדים (Shoulders ▸ ShoulderExtendAll) " +
                    $"ברוחב {shoulder} מ' (Left + Right).");
            }

            if (template.HasDaylight)
            {
                steps.Add(
                    $"{n++}. הוסף מדרון התחברות לקרקע משני הצדדים " +
                    "(Daylight ▸ DaylightStandard / BasicSideSlopeCutDitch) (Left + Right).");
            }

            steps.Add(
                $"{n} לאחר הוספת כל ה-Subassemblies, לחץ \"המשך\" כדי שאמשיך ביצירת המסדרון (Corridor).");

            return steps;
        }

        /// <summary>
        /// Builds the Hebrew summary message: empty named shell created, the engineer must add
        /// the listed subassemblies and then press continue.
        /// </summary>
        public static string BuildMessage(string assemblyName, Template template, bool commandQueued)
        {
            string created = commandQueued
                ? $"נוצר אסמבלי ריק בשם '{assemblyName}'"
                : $"הוכן אסמבלי בשם '{assemblyName}' (יש ליצור אותו ידנית אם הפקודה לא רצה)";

            string fallbackNote = template.FellBackToDefault
                ? " (סוג הדרך לא זוהה — נבחר ברירת מחדל: בינעירוני דו-נתיבי)"
                : string.Empty;

            return
                $"{created}{fallbackNote}. {template.CrossSectionHe}. " +
                $"רוחב נתיב {Meters(template.LaneWidth)} מ', רוחב שוליים {Meters(template.ShoulderWidth)} מ'. " +
                "יש להוסיף את ה-Subassemblies לפי המדריך מתוך לוחות הכלים (Tool Palettes), " +
                "ולאחר מכן ללחוץ \"המשך\" כדי שאמשיך ביצירת המסדרון.";
        }

        private static string Meters(double value) =>
            value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
