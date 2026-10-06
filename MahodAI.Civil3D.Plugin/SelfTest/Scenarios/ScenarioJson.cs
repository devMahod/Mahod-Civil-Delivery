using System;
using System.Linq;
using System.Text.Json;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// Tiny JSON helpers for asserting on tool-result shape.
    ///
    /// Lookups ignore case and underscores on purpose: a scenario asserts that the
    /// DATA is there, not that a property spells itself <c>total_count</c> vs
    /// <c>totalCount</c>. Naming is the protocol contract suite's job
    /// (MahodAI.Civil3D.Plugin.Tests/Contracts) — keeping the two concerns apart stops
    /// an in-host run from going red over a serializer setting.
    /// </summary>
    internal static class ScenarioJson
    {
        public static bool TryFind(JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind != JsonValueKind.Object) return false;

            var wanted = Normalize(name);
            foreach (var prop in obj.EnumerateObject())
            {
                if (Normalize(prop.Name) == wanted)
                {
                    value = prop.Value;
                    return true;
                }
            }
            return false;
        }

        public static JsonElement? Find(JsonElement obj, string name) =>
            TryFind(obj, name, out var v) ? v : null;

        /// <summary>Element count, or -1 when the property is missing or not an array.</summary>
        public static int ArrayCount(JsonElement obj, string name)
        {
            if (!TryFind(obj, name, out var arr)) return -1;
            return arr.ValueKind == JsonValueKind.Array ? arr.GetArrayLength() : -1;
        }

        public static string? String(JsonElement obj, string name)
        {
            if (!TryFind(obj, name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }

        public static double? Number(JsonElement obj, string name)
        {
            if (!TryFind(obj, name, out var v)) return null;
            return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        }

        /// <summary>First element of an array property, or null.</summary>
        public static JsonElement? FirstOf(JsonElement obj, string name)
        {
            if (!TryFind(obj, name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in arr.EnumerateArray()) return item;
            return null;
        }

        private static string Normalize(string s) =>
            new string(s.Where(c => c != '_').ToArray()).ToLowerInvariant();
    }
}
