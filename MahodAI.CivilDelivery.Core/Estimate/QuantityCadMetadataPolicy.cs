using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Observational CAD fields cannot overwrite classification inputs such as
    /// block_name. Existing rule keys and measurement/source identity stay intact.
    /// These fields are evidence, never a second quantity or pricing instruction.
    /// </summary>
    public static class QuantityCadMetadataPolicy
    {
        public sealed record FieldSummary(
            string Key, IReadOnlyList<string> Values, int MissingCount, int RecordCount)
        {
            public bool IsMixed => Values.Count != 1 || MissingCount > 0;
            public string DisplayValue => !IsMixed
                ? Values[0]
                : "מעורב — " + string.Join(" | ", Values.Take(4)) +
                  (Values.Count > 4 ? $" | ועוד {Values.Count - 4} ערכים" : string.Empty) +
                  (MissingCount > 0 ? $" · חסר ב-{MissingCount} מתוך {RecordCount} רשומות" : string.Empty);
        }

        /// <summary>
        /// A shared rule key does not prove shared CAD style. Show every observed
        /// variant and missing coverage rather than borrowing the first record.
        /// </summary>
        public static IReadOnlyList<FieldSummary> Summarize(IEnumerable<QuantityMeasurement> measurements)
        {
            var rows = measurements.ToList();
            return rows.SelectMany(row => row.Parameters.Keys)
                .Where(key => key.StartsWith("cad_", StringComparison.Ordinal) && !QuantityGeometryEvidence.IsGeometryKey(key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => new FieldSummary(
                    key,
                    rows.Where(row => row.Parameters.ContainsKey(key))
                        .Select(row => row.Parameters[key]).Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    rows.Count(row => !row.Parameters.ContainsKey(key)), rows.Count))
                .ToList();
        }

        public static void AppendEvidence(
            QuantityMeasurement measurement, IReadOnlyDictionary<string, string> rawEvidence)
        {
            foreach (var item in rawEvidence)
                measurement.Parameters["cad_" + item.Key] = item.Value;
        }
    }
}
