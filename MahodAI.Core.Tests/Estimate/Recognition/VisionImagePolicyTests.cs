using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

public sealed class VisionImagePolicyTests
{
    [Fact]
    public void RealGrayscalePng_IsAcceptedWithItsDimensions()
    {
        var check = VisionImagePolicy.Validate(TestPng.Create(16, 8));
        check.IsValid.Should().BeTrue(check.Reason);
        check.Width.Should().Be(16);
        check.Height.Should().Be(8);
    }

    [Fact]
    public void ChunkCrc_MatchesTheStandardCheckValueAndAnIndependentGzipChecksum()
    {
        VisionImagePolicy.Crc32(Encoding.ASCII.GetBytes("123456789")).Should().Be(0xCBF43926u);
        VisionImagePolicy.Crc32(Array.Empty<byte>()).Should().Be(0u);
        var random = new Random(7);
        // Non-empty only: a GZipStream that received no bytes may write no trailer at all.
        foreach (var length in new[] { 1, 4, 13, 1000 })
        {
            var data = new byte[length];
            random.NextBytes(data);
            VisionImagePolicy.Crc32(data).Should().Be(TestPng.GzipCrc(data));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    public void NonPng_IsRejected(string input)
    {
        byte[]? bytes = input switch
        {
            "null" => null,
            "empty" => Array.Empty<byte>(),
            "jpeg" => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(new byte[60]).ToArray(),
            _ => Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[60]).ToArray(),
        };
        VisionImagePolicy.Validate(bytes).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(513, 1)]
    [InlineData(1, 513)]
    [InlineData(0, 4)]
    public void DimensionsOutsideOneTo512_AreRejected(int width, int height)
    {
        var check = VisionImagePolicy.Validate(TestPng.Create(width, height));
        check.IsValid.Should().BeFalse();
        check.Reason.Should().Be("dimensions");
    }

    [Fact]
    public void FileAbove256KiB_IsRejectedBeforeParsing()
    {
        var png = TestPng.Create(4, 4, ("pHYs", new byte[VisionImagePolicy.MaxBytes]));
        png.Length.Should().BeGreaterThan(VisionImagePolicy.MaxBytes);
        VisionImagePolicy.Validate(png).Reason.Should().Be("too-large");
    }

    [Theory]
    [InlineData("tEXt")]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    [InlineData("eXIf")]
    [InlineData("tIME")]
    public void MetadataChunk_IsRejected(string chunk)
    {
        var png = TestPng.Create(4, 4, (chunk, Encoding.ASCII.GetBytes("Comment\0C:\\private\\source.dwg")));
        var check = VisionImagePolicy.Validate(png);
        check.IsValid.Should().BeFalse();
        check.Reason.Should().Be("metadata:" + chunk);
    }

    [Theory]
    [InlineData("iCCP")]
    [InlineData("acTL")]
    [InlineData("sPLT")]
    [InlineData("prVt")]
    public void ChunkOutsideTheAllowList_IsRejected(string chunk)
    {
        VisionImagePolicy.Validate(TestPng.Create(4, 4, (chunk, new byte[] { 1, 2, 3 }))).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CorruptChunkOrTrailingBytes_AreRejected()
    {
        var corrupt = TestPng.Create(4, 4);
        // The last IDAT data byte sits just before the IDAT CRC (4) and the IEND chunk (12).
        corrupt[corrupt.Length - 17] ^= 0xFF;
        VisionImagePolicy.Validate(corrupt).Reason.Should().Be("bad-crc");
        var trailing = TestPng.Create(4, 4).Concat(Encoding.ASCII.GetBytes("hidden")).ToArray();
        VisionImagePolicy.Validate(trailing).Reason.Should().Be("trailing-data");
    }

    [Fact]
    public void TryCreate_OwnsACopyAndBindsItsHash()
    {
        var png = TestPng.Create(4, 4);
        var expected = VisionImagePolicy.Sha256(png);
        VisionImagePolicy.TryCreate(png, VisionImagePolicy.GroupPreview, out var image, out var reason).Should().BeTrue(reason);
        png[png.Length - 17] ^= 0xFF;
        image!.Sha256.Should().Be(expected).And.MatchRegex("^[0-9a-f]{64}$");
        VisionImagePolicy.Sha256(image.ToArray()).Should().Be(expected);
        VisionImagePolicy.IsValidImage(image).Should().BeTrue();
        image.Kind.Should().Be(VisionImagePolicy.GroupPreview);
    }

    [Theory]
    [InlineData("screenshot")]
    [InlineData("")]
    [InlineData(null)]
    public void TryCreate_RejectsUnknownKind(string? kind)
    {
        VisionImagePolicy.TryCreate(TestPng.Create(4, 4), kind, out var image, out _).Should().BeFalse();
        image.Should().BeNull();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("no-permit")]
    [InlineData("blank-grantor")]
    [InlineData("local-time")]
    [InlineData("default-time")]
    [InlineData("duplicate-hash")]
    [InlineData("extra-hash")]
    [InlineData("other-context")]
    [InlineData("undeclared")]
    [InlineData("three-images")]
    public void Permit_MustNameThisContextAndExactlyTheseHashes(string variant)
    {
        var images = Enumerable.Range(0, variant == "three-images" ? 3 : 1).Select(index => TestPng.Image(4 + index)).ToArray();
        var hashes = images.Select(image => image.Sha256).ToArray();
        var request = FamilyAssistFixtures.Request(variant == "undeclared"
            ? Array.Empty<FamilyRankImage>()
            : images.Select(image => new FamilyRankImage(image.Sha256, image.Kind)).ToArray());
        var permit = new VisionSendPermit(request.ContextId, hashes, "Arthur", new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero));
        permit = variant switch
        {
            "blank-grantor" => permit with { GrantedBy = " " },
            "local-time" => permit with { GrantedAtUtc = new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.FromHours(3)) },
            "default-time" => permit with { GrantedAtUtc = default },
            "duplicate-hash" => permit with { ImageSha256 = new[] { hashes[0], hashes[0] } },
            "extra-hash" => permit with { ImageSha256 = hashes.Append(new string('b', 64)).ToArray() },
            "other-context" => permit with { ContextId = new string('e', 64) },
            _ => permit,
        };
        var allowed = VisionImagePolicy.IsPermitted(request, new VisionPayload(images, variant == "no-permit" ? null : permit));
        allowed.Should().Be(variant == "valid");
    }

    [Theory]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", true)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":false,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":\"true\",\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/2\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\",\"key\":\"x\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":false,\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T11:00:00+03:00\"}", false)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00\"}", false)]
    [InlineData("[true]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void OrganisationPolicy_IsEnabledOnlyByTheExactAgreedShape(string json, bool expected)
    {
        VisionOrganizationPolicy.IsEnabled(Encoding.UTF8.GetBytes(json)).Should().Be(expected);
    }

    [Fact]
    public void OrganisationPolicy_AcceptsUtf8BomAndRejectsOversizeOrMissingFile()
    {
        var valid = Encoding.UTF8.GetBytes("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}");
        VisionOrganizationPolicy.IsEnabled(Encoding.UTF8.GetPreamble().Concat(valid).ToArray()).Should().BeTrue();
        VisionOrganizationPolicy.IsEnabled(valid.Concat(Enumerable.Repeat((byte)' ', VisionOrganizationPolicy.MaxFileBytes)).ToArray()).Should().BeFalse();
        VisionOrganizationPolicy.IsEnabled(null).Should().BeFalse();
    }
}

/// <summary>
/// Builds real PNG files (zlib-compressed grayscale scanlines) with chunk CRCs taken from gzip's trailer, an
/// implementation independent of the policy's own CRC.
/// </summary>
internal static class TestPng
{
    public static byte[] Create(int width, int height, params (string Type, byte[] Data)[] extra)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; // bit depth; colour type 0 (grayscale), compression 0, filter 0, no interlace
        Chunk(stream, "IHDR", header);
        foreach (var (type, data) in extra) Chunk(stream, type, data);
        var raw = new byte[Math.Max(1, height) * (width + 1)];
        for (var index = 0; index < raw.Length; index++) raw[index] = (byte)(index % (width + 1) == 0 ? 0 : 0x80);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", Array.Empty<byte>());
        return stream.ToArray();
    }

    public static VisionImage Image(int size, string kind = VisionImagePolicy.GroupPreview)
    {
        VisionImagePolicy.TryCreate(Create(size, size), kind, out var image, out var reason).Should().BeTrue(reason);
        return image!;
    }

    public static uint GzipCrc(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(data);
        var bytes = output.ToArray();
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8, 4));
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, GzipCrc(typed));
        stream.Write(buffer);
    }
}
