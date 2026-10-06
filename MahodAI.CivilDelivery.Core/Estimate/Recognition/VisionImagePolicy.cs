using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>The outcome of checking one image. <see cref="Reason"/> is a fixed token, never image content.</summary>
public sealed record VisionImageCheck(bool IsValid, string Reason, int Width, int Height);

/// <summary>
/// A PNG that passed <see cref="VisionImagePolicy.Validate"/>, with its SHA-256. It is created only through
/// <see cref="VisionImagePolicy.TryCreate"/>, owns a private copy of the bytes, and never exposes them to a serializer.
/// Rendering the group preview or legend crop is not part of this type (and not implemented yet).
/// </summary>
public sealed class VisionImage
{
    private readonly byte[] _png;

    internal VisionImage(byte[] png, string kind, string sha256, int width, int height)
    {
        _png = png ?? throw new ArgumentNullException(nameof(png));
        Kind = kind;
        Sha256 = sha256;
        Width = width;
        Height = height;
    }

    /// <summary><c>group-preview</c> or <c>legend-crop</c>.</summary>
    public string Kind { get; }
    public string Sha256 { get; }
    public int Width { get; }
    public int Height { get; }
    public int Length => _png.Length;
    public byte[] ToArray() => (byte[])_png.Clone();
    internal byte[] Bytes => _png;
}

/// <summary>
/// The engineer's per-request consent to send exactly these images for exactly this request context. The host creates
/// it only when the organisation policy enables vision, after showing the images; it does not outlive one send.
/// </summary>
public sealed record VisionSendPermit(string ContextId, IReadOnlyList<string> ImageSha256, string GrantedBy, DateTimeOffset GrantedAtUtc);

/// <summary>Validated images and the permit that must cover them. Without a matching permit nothing is sent.</summary>
public sealed record VisionPayload(IReadOnlyList<VisionImage> Images, VisionSendPermit? Permit);

/// <summary>
/// Fail-closed gate for images that may accompany a family-ranking request: a small, metadata-free PNG, at most two per
/// request, each bound by SHA-256 to an explicit permit. Pure; never throws on malformed input.
/// </summary>
public static class VisionImagePolicy
{
    public const int MaxImages = 2;
    public const int MaxDimension = 512;
    public const int MaxBytes = 256 * 1024;
    public const string GroupPreview = "group-preview";
    public const string LegendCrop = "legend-crop";

    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Text, EXIF and time chunks can carry drawing names, paths, user names or dates.</summary>
    private static readonly HashSet<string> MetadataChunks = new(StringComparer.Ordinal) { "tEXt", "zTXt", "iTXt", "eXIf", "tIME" };

    /// <summary>
    /// Every other chunk is refused as well (allow-list): only image data and colour-interpretation chunks remain.
    /// iCCP is excluded because it carries a free-text profile name; APNG/private chunks are excluded by construction.
    /// </summary>
    private static readonly HashSet<string> AllowedChunks = new(StringComparer.Ordinal)
    { "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "sBIT", "bKGD", "pHYs" };

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static VisionImageCheck Validate(byte[]? png)
    {
        if (png == null || png.Length == 0) return Fail("empty");
        if (png.Length > MaxBytes) return Fail("too-large");
        // Signature + IHDR chunk (25) + IEND chunk (12) is the smallest possible file.
        if (png.Length < Signature.Length + 25 + 12 || !new ReadOnlySpan<byte>(png, 0, Signature.Length).SequenceEqual(Signature))
            return Fail("not-png");
        int width = 0, height = 0, index = 0;
        var offset = Signature.Length;
        var hasData = false;
        while (true)
        {
            if (png.Length - offset < 12) return Fail("truncated");
            var length = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(png, offset, 4));
            if (length > (uint)(png.Length - offset - 12)) return Fail("truncated");
            var size = (int)length;
            if (!IsChunkType(new ReadOnlySpan<byte>(png, offset + 4, 4))) return Fail("bad-chunk");
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(png, offset + 8 + size, 4));
            if (Crc32(new ReadOnlySpan<byte>(png, offset + 4, 4 + size)) != crc) return Fail("bad-crc");
            if (index == 0 && type != "IHDR") return Fail("no-header");
            if (MetadataChunks.Contains(type)) return Fail("metadata:" + type);
            if (!AllowedChunks.Contains(type)) return Fail("chunk:" + type);
            switch (type)
            {
                case "IHDR":
                {
                    if (index != 0 || size != 13) return Fail("bad-header");
                    var w = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(png, offset + 8, 4));
                    var h = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(png, offset + 12, 4));
                    if (w == 0 || h == 0 || w > MaxDimension || h > MaxDimension) return Fail("dimensions");
                    width = (int)w;
                    height = (int)h;
                    var bitDepth = png[offset + 16];
                    var colorType = png[offset + 17];
                    if (!ValidDepth(colorType, bitDepth) || png[offset + 18] != 0 || png[offset + 19] != 0 || png[offset + 20] > 1)
                        return Fail("bad-header");
                    break;
                }
                case "IDAT":
                    hasData = true;
                    break;
                case "IEND":
                    if (size != 0 || !hasData) return Fail("bad-end");
                    // Nothing may follow IEND: trailing bytes are an unchecked channel.
                    return offset + 12 == png.Length ? new VisionImageCheck(true, "ok", width, height) : Fail("trailing-data");
            }
            offset += 12 + size;
            index++;
        }
    }

    /// <summary>Validates a private copy of <paramref name="png"/> and binds it to its SHA-256.</summary>
    public static bool TryCreate(byte[]? png, string? kind, out VisionImage? image, out string reason)
    {
        image = null;
        if (kind == null || (kind != GroupPreview && kind != LegendCrop))
        {
            reason = "kind";
            return false;
        }
        var copy = png == null ? null : (byte[])png.Clone();
        var check = Validate(copy);
        if (!check.IsValid || copy == null)
        {
            reason = check.Reason;
            return false;
        }
        image = new VisionImage(copy, kind, Sha256(copy), check.Width, check.Height);
        reason = check.Reason;
        return true;
    }

    public static string Sha256(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Re-validates an image handed over by another component: kind, PNG rules and its SHA-256.</summary>
    public static bool IsValidImage(VisionImage? image) =>
        image != null && (image.Kind == GroupPreview || image.Kind == LegendCrop) && Validate(image.Bytes).IsValid &&
        string.Equals(Sha256(image.Bytes), image.Sha256, StringComparison.Ordinal);

    /// <summary>
    /// True only when the request declares exactly the payload's images (same order, hashes and kinds), every image is
    /// still a valid PNG with that hash, there are at most <see cref="MaxImages"/> distinct images, and the permit names
    /// this request's context and exactly the same set of hashes, with a grantor and a UTC grant time.
    /// </summary>
    public static bool IsPermitted(FamilyRankRequest? request, VisionPayload? vision)
    {
        if (request == null || vision?.Images == null || vision.Permit is not { } permit) return false;
        var images = vision.Images;
        var declared = request.Images ?? Array.Empty<FamilyRankImage>();
        if (images.Count is < 1 or > MaxImages || declared.Count != images.Count) return false;
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var reference = declared[i];
            if (reference == null || !IsValidImage(image) ||
                !string.Equals(reference.Sha256, image.Sha256, StringComparison.Ordinal) ||
                !string.Equals(reference.Kind, image.Kind, StringComparison.Ordinal)) return false;
        }
        var hashes = images.Select(image => image.Sha256).ToArray();
        if (hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Length) return false;
        var granted = permit.ImageSha256;
        return granted != null && granted.Count == hashes.Length &&
               granted.Distinct(StringComparer.Ordinal).Count() == hashes.Length &&
               granted.All(hash => hashes.Contains(hash, StringComparer.Ordinal)) &&
               string.Equals(permit.ContextId, request.ContextId, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(permit.GrantedBy) && permit.GrantedBy.Length <= 200 && !permit.GrantedBy.Any(char.IsControl) &&
               permit.GrantedAtUtc != default && permit.GrantedAtUtc.Offset == TimeSpan.Zero;
    }

    /// <summary>The PNG chunk CRC (ISO-HDLC CRC-32, the same polynomial as gzip).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
            if (!((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z'))) return false;
        return true;
    }

    private static bool ValidDepth(byte colorType, byte bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false,
    };

    private static VisionImageCheck Fail(string reason) => new(false, reason, 0, 0);
}

/// <summary>
/// Thrown by a family-ranking transport that refuses to send images, before anything is built or sent. It carries a
/// fixed reason only (never request data, image bytes or a key), so the assist can offer the text-only path.
/// </summary>
public sealed class VisionNotPermittedException : InvalidOperationException
{
    public VisionNotPermittedException(bool organizationPolicyDisabled)
        : base(organizationPolicyDisabled
            ? "Semantic assistant images are disabled by the organisation policy."
            : "Semantic assistant images are not permitted.")
    {
        OrganizationPolicyDisabled = organizationPolicyDisabled;
    }

    /// <summary>True when the organisation policy is off; false when the images or their permit were refused.</summary>
    public bool OrganizationPolicyDisabled { get; }
}

/// <summary>
/// The organisation's non-secret vision switch:
/// <c>{"schema":"mahod-semantic-vision/1","enabled":true,"granted_by":"…","granted_at_utc":"…Z"}</c>. Anything else —
/// absent, oversized, malformed, duplicate or unknown properties, a non-UTC time — means disabled. The Civil host reads
/// it from a machine-wide file that only administrators can write
/// (<c>%ProgramData%\MahodAI_Civil3D\civil-delivery\semantic-vision.policy.json</c>, see
/// SemanticMappingAssistantConfiguration), never from the user profile: the engineer who grants the per-request
/// <see cref="VisionSendPermit"/> must not be able to switch the organisation policy on as well.
/// </summary>
public static class VisionOrganizationPolicy
{
    public const string Schema = "mahod-semantic-vision/1";
    public const int MaxFileBytes = 4096;
    private static readonly string[] Properties = { "schema", "enabled", "granted_by", "granted_at_utc" };

    public static bool IsEnabled(byte[]? json)
    {
        if (json == null || json.Length is 0 or > MaxFileBytes) return false;
        var start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(json, start, json.Length - start),
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || !Properties.Contains(property.Name, StringComparer.Ordinal)) return false;
            if (names.Count != Properties.Length) return false;
            var schema = root.GetProperty("schema");
            var grantedBy = root.GetProperty("granted_by");
            var grantedAt = root.GetProperty("granted_at_utc");
            return schema.ValueKind == JsonValueKind.String && schema.GetString() == Schema &&
                   root.GetProperty("enabled").ValueKind == JsonValueKind.True &&
                   grantedBy.ValueKind == JsonValueKind.String && grantedBy.GetString() is { } by &&
                   !string.IsNullOrWhiteSpace(by) && by.Length <= 200 && !by.Any(char.IsControl) &&
                   grantedAt.ValueKind == JsonValueKind.String && grantedAt.GetString() is { } at &&
                   at.EndsWith("Z", StringComparison.Ordinal) &&
                   DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var grantedTime) &&
                   grantedTime.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
