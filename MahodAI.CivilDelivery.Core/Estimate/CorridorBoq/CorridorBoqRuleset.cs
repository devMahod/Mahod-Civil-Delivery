using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq
{
    /// <summary>
    /// The project rules of the corridor bill (rulesets/6422_corridor_v1.json): which Civil link code is the design bottom,
    /// which surface is the existing ground, the stripping depth, which corridor code is which NTI item (and asphalt
    /// thickness), the assumption texts shown on the rows, and the 13 NTI unified 07-2026 items with their base prices as
    /// printed in the official pricebook file (its SHA-256 is kept).
    /// </summary>
    public sealed record CorridorBoqRuleset(
        string Project, string Version, string LinkCode, string ExistingGroundSurface, double HisufDepthM, string MethodText,
        string HisufNote, IReadOnlyDictionary<string, CorridorCodeRule> Codes, IReadOnlyDictionary<string, string> Items,
        IReadOnlyDictionary<string, string> Assumptions, CorridorPricebook Pricebook)
    {
        public const string EmbeddedResource6422 = "MahodAI.CivilDelivery.Estimate.CorridorBoq.rulesets.6422_corridor_v1.json";

        public static CorridorBoqRuleset LoadEmbedded6422()
        {
            using var stream = typeof(CorridorBoqRuleset).Assembly.GetManifestResourceStream(EmbeddedResource6422)
                ?? throw new InvalidOperationException("The corridor BoQ rules of project 6422 are not embedded in this build.");
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }

        public static CorridorBoqRuleset Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var m = r.GetProperty("measurement");
            var codes = new Dictionary<string, CorridorCodeRule>(StringComparer.Ordinal);
            foreach (var c in r.GetProperty("codes").EnumerateObject())
                codes[c.Name] = new CorridorCodeRule(c.Name, c.Value.GetProperty("kind").GetString()!, c.Value.GetProperty("item").GetString()!,
                    c.Value.TryGetProperty("thickness_m", out var t) ? t.GetDouble() : null,
                    c.Value.TryGetProperty("order", out var o) ? o.GetInt32() : null,
                    c.Value.TryGetProperty("note", out var n) ? n.GetString() : null);
            var items = r.GetProperty("items").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            var assumptions = r.GetProperty("assumptions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            var pb = r.GetProperty("pricebook");
            var pbItems = new Dictionary<string, CorridorPricebookItem>(StringComparer.Ordinal);
            foreach (var i in pb.GetProperty("items").EnumerateObject())
                pbItems[i.Name] = new CorridorPricebookItem(i.Name, i.Value.GetProperty("descr").GetString()!, i.Value.GetProperty("unit").GetString()!,
                    i.Value.GetProperty("base_price").GetDecimal());
            var rules = new CorridorBoqRuleset(r.GetProperty("project").GetString()!, r.GetProperty("version").GetString()!,
                m.GetProperty("link_code").GetString()!, m.GetProperty("existing_ground_surface").GetString()!,
                m.GetProperty("hisuf_depth_m").GetDouble(), m.GetProperty("method").GetString()!, m.GetProperty("hisuf_note").GetString()!,
                codes, items, assumptions,
                new CorridorPricebook(pb.GetProperty("id").GetString()!, pb.GetProperty("name").GetString()!, pb.GetProperty("edition").GetString()!,
                    pb.GetProperty("source_file").GetString()!, pb.GetProperty("source_sha256").GetString()!, pbItems));
            // Every item a code or a derived line points at must be priced from the pricebook excerpt — fail closed otherwise.
            foreach (var code in rules.Codes.Values.Select(c => c.Item).Concat(rules.Items.Values))
                if (!rules.Pricebook.Items.ContainsKey(code))
                    throw new InvalidDataException($"Corridor BoQ rules: item {code} is not in the pricebook excerpt.");
            return rules;
        }
    }

    public sealed record CorridorCodeRule(string Code, string Kind, string Item, double? ThicknessM, int? Order, string? Note);

    public sealed record CorridorPricebookItem(string Code, string Description, string Unit, decimal BasePrice);

    public sealed record CorridorPricebook(string Id, string Name, string Edition, string SourceFile, string SourceSha256,
        IReadOnlyDictionary<string, CorridorPricebookItem> Items);
}
