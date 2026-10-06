using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>User descriptions assist retrieval only. They never classify, measure, price or approve work.</summary>
public static class SemanticHintPolicy
{
    public sealed record Role(string Id, string Label, string Subject);
    public sealed record Draft(string RoleId, string Description)
    {
        public Draft() : this("unknown", "") { }
    }
    public sealed record Scope(string ProfileId, string DrawingPath, string DrawingHash,
        string RuleKey, string Layer, string MeasurementKind, string Unit, string SourcesHash)
    {
        public Scope() : this("", "", "", "", "", "", "", "") { }
    }
    public sealed record Hint
    {
        public Scope? Source { get; init; }
        public Draft Input { get; init; } = new();
        public string RecordedBy { get; init; } = "";
        public DateTime RecordedAtUtc { get; init; }
    }

    public static IReadOnlyList<Role> Roles { get; } = Array.AsReadOnly(new[]
    {
        new Role("unknown", "לא הוגדר", ""),
        new Role("curb", "אבן שפה", "אבן שפה"),
        new Role("sidewalk", "מדרכה / ריצוף", "מדרכה ריצוף"),
        new Role("road", "מסעה / אספלט", "מסעה אספלט"),
        new Role("marking", "סימון דרך", "סימון דרך"),
        new Role("sign", "תמרור", "תמרור"),
        new Role("drainage", "ניקוז", "ניקוז"),
        new Role("water", "מים", "מים"),
        new Role("sewer", "ביוב", "ביוב"),
        new Role("electricity", "חשמל", "חשמל"),
        new Role("lighting", "תאורה", "תאורה"),
        new Role("communication", "תקשורת", "תקשורת"),
        new Role("fence", "גדר", "גדר"),
        new Role("guardrail", "מעקה", "מעקה"),
        new Role("landscape", "גינון", "גינון"),
        new Role("earthwork", "עבודות עפר", "עבודות עפר"),
        new Role("other", "אחר — תיאור חופשי", ""),
    });

    public static bool TryContext(Draft? input, out string context, out string error)
    {
        context = ""; error = "";
        var role = Roles.FirstOrDefault(value => value.Id == input?.RoleId);
        if (role == null || input?.Description == null)
        { error = "יש לבחור משמעות מהרשימה."; return false; }
        if (input.Description.Any(char.IsControl))
        { error = "יש להזין תיאור בשורה אחת."; return false; }
        context = string.Join(" — ", new[] { role.Subject, input.Description.Trim() }
            .Where(value => value.Length > 0));
        if (context.Length > SemanticMappingAssist.MaxEngineerContextLength)
        {
            error = $"המשמעות והתיאור יחד מוגבלים ל־{SemanticMappingAssist.MaxEngineerContextLength} תווים; יש לקצר את התיאור. הקלט לא קוצר.";
            return false;
        }
        return true;
    }

    public static Scope Capture(string profileId, string drawingPath, string drawingHash,
        string ruleKey, IReadOnlyList<NeutralQuantityRecord> records)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(drawingPath) || !Path.IsPathFullyQualified(drawingPath) ||
            !CatalogIdentity.IsValidSha256(drawingHash) || string.IsNullOrWhiteSpace(ruleKey) || records.Count == 0 ||
            records.Any(record => record.ProjectProfileId != profileId ||
                !string.Equals(record.Classification.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.Source.Handle) || string.IsNullOrWhiteSpace(record.Source.EntityType) ||
                string.IsNullOrWhiteSpace(record.Source.DrawingPath) ||
                !CatalogIdentity.IsValidSha256(record.Source.DrawingHash)))
            throw new InvalidOperationException("זהות מקור התיאור חסרה או שייכת לקבוצה אחרת; יש לסרוק מחדש.");
        var layers = records.Select(record => SectionProjectionLogic.LayerLeaf(record.Source.Layer)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var kinds = records.Select(record => record.Measurement.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var units = records.Select(record => Units.Parse(record.Measurement.Unit).Canonical).Distinct(StringComparer.Ordinal).ToArray();
        if (layers.Length != 1 || string.IsNullOrWhiteSpace(layers[0]) || kinds.Length != 1 ||
            units.Length != 1 || units[0] == "?")
            throw new InvalidOperationException("לתיאור נדרשת קבוצה אחת בעלת שכבה, שיטת מדידה ויחידה אחידות.");
        // Run ids and catalog classifications change on review/rescan. Source objects,
        // insertion chains, raw measurements and CAD metadata must remain identical.
        // Recognition evidence (ev_* and its _status) is excluded: nearby text, legends and
        // samples depend on unrelated objects and are re-collected on every scan, so they
        // must never invalidate a saved hint (hints saved before ev_* existed stay valid).
        var sources = records.Select(record => JsonSerializer.Serialize(new
        {
            record.Source, record.Measurement.Kind, record.Measurement.Method,
            record.Measurement.RawValue, Unit = Units.Parse(record.Measurement.Unit).Canonical,
            record.Measurement.GeometryEvidence,
            Parameters = record.Measurement.Parameters
                .Where(pair => !IsRecognitionEvidenceKey(pair.Key) && !QuantityGeometryEvidence.IsGeometryKey(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        })).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new(profileId, Path.GetFullPath(drawingPath).ToUpperInvariant(), drawingHash.ToLowerInvariant(),
            ruleKey, layers[0].ToUpperInvariant(), kinds[0].ToLowerInvariant(), units[0], ArtifactHash.Sha256OfText(JsonSerializer.Serialize(sources)));
    }

    /// <summary>mahod-evidence/1 keys (<c>ev_*</c>, including <c>ev_*_status</c>) never take part in the persistent scope hash.</summary>
    public static bool IsRecognitionEvidenceKey(string? key) =>
        key != null && key.StartsWith("ev_", StringComparison.Ordinal);

    public static bool IsValid(Hint? hint) => hint?.Source is { } source &&
        !string.IsNullOrWhiteSpace(source.ProfileId) && !string.IsNullOrWhiteSpace(source.RuleKey) &&
        !string.IsNullOrWhiteSpace(source.Layer) && !string.IsNullOrWhiteSpace(source.MeasurementKind) &&
        !string.IsNullOrWhiteSpace(source.DrawingPath) && Path.IsPathFullyQualified(source.DrawingPath) && CatalogIdentity.IsValidSha256(source.DrawingHash) &&
        CatalogIdentity.IsValidSha256(source.SourcesHash) && Units.Parse(source.Unit).Canonical != "?" &&
        !string.IsNullOrWhiteSpace(hint.RecordedBy) && !hint.RecordedBy.Any(char.IsControl) &&
        hint.RecordedAtUtc != default && hint.RecordedAtUtc.Kind == DateTimeKind.Utc &&
        TryContext(hint.Input, out var context, out _) && context.Length > 0;

}
