using System.Security.Cryptography;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Offers the bundled NTI edition through the same explicit project registration as imported books.</summary>
public static class BundledPriceBooks
{
    public const string FileName = "nti-unified-2026-07.xls";
    // Fresh download from the official unified-price-book page, verified 2026-10-30.
    public const string Sha256 = "884B052CE443546F0FD9F6CC18F478107CB8C3D19A5AD9039DE951845E549613";
    private const string ResourceName = "Mahod.CivilDelivery.PriceBooks." + FileName;

    public static IReadOnlyList<KnownPriceBookIndex.Offer> Offers(string indexDirectory, ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Estimate.PriceBooks.Any(e => string.Equals(e.FileHash, Sha256, StringComparison.OrdinalIgnoreCase)
                                                && e.Mapping == null))
            return Array.Empty<KnownPriceBookIndex.Offer>();
        using var resource = typeof(BundledPriceBooks).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("המחירון הכלול בתוסף חסר. יש להתקין את התוסף מחדש.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("חתימת המחירון הכלול בתוסף אינה תקינה.");
        var directory = Path.Combine(indexDirectory, "bundled", Sha256.ToLowerInvariant());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path) || !string.Equals(ArtifactHash.Sha256OfFile(path), Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var pending = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(pending, bytes);
                File.Move(pending, path, overwrite: true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        return new[] { new KnownPriceBookIndex.Offer(new KnownPriceBookIndex.Entry(path, Sha256,
            "נתיבי ישראל", "המחירון האחוד · יולי 2026 · כלול בתוסף", 9783, null, null, null)) };
    }
}
