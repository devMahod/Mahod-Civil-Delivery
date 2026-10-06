using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Scope/identity of the existing project-specific lane, not a generic project-rules resolver.</summary>
internal static class CorridorBoqExportGuard
{
    internal static bool SupportsEmbeddedRules(string? profileId) =>
        string.Equals(profileId, "6422", StringComparison.Ordinal);

    internal static CorridorBoqRuleset LoadEmbeddedRules(out string rulesetSha256)
    {
        using var stream = typeof(CorridorBoqRuleset).Assembly.GetManifestResourceStream(CorridorBoqRuleset.EmbeddedResource6422)
            ?? throw new InvalidDataException("כללי הקורידורים של פרויקט 6422 חסרים בבנייה.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        rulesetSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return CorridorBoqRuleset.Parse(reader.ReadToEnd());
    }

    internal static void RequireIdentity(string? profileId, CorridorBoqRuleset rules, string rulesetSha256)
    {
        if (!SupportsEmbeddedRules(profileId) || !string.Equals(profileId, rules.Project, StringComparison.Ordinal))
            throw new InvalidDataException("מסלול כמויות מקורידורים זה משתמש בכללי פרויקט 6422 בלבד. לפרויקט אחר נדרשת תצורת כללים מאושרת משלו; לא הוחלו עליו מחירי או הנחות 6422.");
        if (!IsSha256(rulesetSha256) || string.IsNullOrWhiteSpace(rules.Version) ||
            string.IsNullOrWhiteSpace(rules.Pricebook.Id) || string.IsNullOrWhiteSpace(rules.Pricebook.Edition) ||
            !IsSha256(rules.Pricebook.SourceSha256))
            throw new InvalidDataException("זהות כללי הקורידורים או מקור המחירון חסרה; לא נוצר כתב כמויות.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        System.Linq.Enumerable.All(value, Uri.IsHexDigit);
}
