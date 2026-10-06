using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MahodAI.Civil3D.Plugin.Services.Extraction
{
    /// <summary>
    /// Classifies CAD layers by Netivei Israel numeric codes and name patterns.
    /// </summary>
    public static class LayerCodeRegistry
    {
        /// <summary>Result of classifying a layer.</summary>
        public class LayerClassification
        {
            public string ElementType { get; set; } = "";
            public string HebrewName { get; set; } = "";
            public string Category { get; set; } = "";
            public int? NumericCode { get; set; }
        }

        // Netivei Israel numeric layer codes → element classification
        private static readonly Dictionary<int, (string elementType, string hebrew, string category)> CodeMap = new()
        {
            // Road markings
            { 801, ("dashed_marking", "סימון מקווקו", "marking") },
            { 802, ("solid_marking", "סימון רציף", "marking") },
            { 803, ("double_white_line", "קו כפול לבן", "marking") },
            { 804, ("center_line", "קו מרכזי", "marking") },
            { 805, ("lane_line", "קו הפרדת נתיבים", "marking") },
            { 806, ("edge_line", "קו שפה", "marking") },
            { 807, ("yellow_shoulder_line", "קו שוליים צהוב", "marking") },
            { 808, ("crosswalk", "מעבר חציה", "marking") },
            { 809, ("stop_line", "קו עצור", "marking") },

            // Road infrastructure
            { 810, ("sidewalk", "מדרכה", "pedestrian") },
            { 811, ("curb", "אבן שפה", "structure") },
            { 812, ("guardrail", "מעקה בטיחות", "safety") },
            { 813, ("barrier", "מחסום", "safety") },
            { 814, ("median", "אי הפרדה", "structure") },
            { 815, ("traffic_island", "אי תנועה", "structure") },
            { 816, ("shoulder", "שוליים", "road") },
            { 817, ("ditch", "תעלה", "drainage") },

            // Drainage
            { 820, ("drainage_pipe", "צינור ניקוז", "drainage") },
            { 821, ("manhole", "שוחת ביקורת", "drainage") },
            { 822, ("inlet", "בור ניקוז", "drainage") },

            // Signs
            { 830, ("regulatory_sign", "תמרור הסדר", "signs") },
            { 831, ("warning_sign", "תמרור אזהרה", "signs") },
            { 832, ("guide_sign", "תמרור הכוונה", "signs") },
        };

        // Name patterns → element classification (checked case-insensitively)
        private static readonly (string pattern, string elementType, string hebrew, string category)[] PatternMap =
        {
            ("SDWK", "sidewalk", "מדרכה", "pedestrian"),
            ("SIDEWALK", "sidewalk", "מדרכה", "pedestrian"),
            ("מדרכה", "sidewalk", "מדרכה", "pedestrian"),
            ("ISLAND", "traffic_island", "אי תנועה", "structure"),
            ("אי תנועה", "traffic_island", "אי תנועה", "structure"),
            ("אי הפרדה", "median", "אי הפרדה", "structure"),
            ("MEDIAN", "median", "אי הפרדה", "structure"),
            ("CURB", "curb", "אבן שפה", "structure"),
            ("אבן שפה", "curb", "אבן שפה", "structure"),
            ("GUARDRAIL", "guardrail", "מעקה בטיחות", "safety"),
            ("מעקה", "guardrail", "מעקה בטיחות", "safety"),
            ("BARRIER", "barrier", "מחסום", "safety"),
            ("מחסום", "barrier", "מחסום", "safety"),
            ("DITCH", "ditch", "תעלה", "drainage"),
            ("תעלה", "ditch", "תעלה", "drainage"),
            ("DRAIN", "drainage_pipe", "ניקוז", "drainage"),
            ("ניקוז", "drainage_pipe", "ניקוז", "drainage"),
            ("SHOULDER", "shoulder", "שוליים", "road"),
            ("שוליים", "shoulder", "שוליים", "road"),
            ("MARK", "road_marking", "סימון", "marking"),
            ("סימון", "road_marking", "סימון", "marking"),
            ("STRIPE", "road_marking", "סימון", "marking"),
            ("CROSSWALK", "crosswalk", "מעבר חציה", "marking"),
            ("מעבר חציה", "crosswalk", "מעבר חציה", "marking"),
        };

        // Regex to extract a leading numeric code from layer names (e.g., "801-MARKING" → 801)
        private static readonly Regex NumericCodeRegex = new(@"(?:^|\D)(\d{3})(?:\D|$)", RegexOptions.Compiled);

        /// <summary>
        /// Classify a layer by its name, optional color ACI, and linetype.
        /// Returns null if no classification matches.
        /// </summary>
        public static LayerClassification? Classify(string layerName, int? colorAci = null, string? linetype = null)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                return null;

            // 1. Try numeric code extraction
            var match = NumericCodeRegex.Match(layerName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int code))
            {
                if (CodeMap.TryGetValue(code, out var codeInfo))
                {
                    return new LayerClassification
                    {
                        ElementType = codeInfo.elementType,
                        HebrewName = codeInfo.hebrew,
                        Category = codeInfo.category,
                        NumericCode = code,
                    };
                }
            }

            // 2. Try pattern matching on layer name
            string upper = layerName.ToUpperInvariant();
            foreach (var (pattern, elementType, hebrew, category) in PatternMap)
            {
                if (upper.Contains(pattern.ToUpperInvariant()) || layerName.Contains(pattern))
                {
                    var classification = new LayerClassification
                    {
                        ElementType = elementType,
                        HebrewName = hebrew,
                        Category = category,
                    };

                    // Refine marking type using color ACI
                    if (category == "marking" && colorAci.HasValue)
                    {
                        if (colorAci.Value == 2 || colorAci.Value == 40 || colorAci.Value == 50)
                            classification.ElementType = "yellow_" + classification.ElementType;
                    }

                    return classification;
                }
            }

            return null;
        }

        /// <summary>
        /// Check if a layer is a marking layer (by code or pattern).
        /// </summary>
        public static bool IsMarkingLayer(string layerName)
        {
            var classification = Classify(layerName);
            return classification?.Category == "marking";
        }
    }
}
