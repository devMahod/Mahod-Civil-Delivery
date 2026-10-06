using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>
/// Renders the shape of a measured group for visual recognition: the sampled host geometry of a few records,
/// scaled into a unit box and drawn as dark lines on white. The image carries no coordinates, scale, text,
/// layer names or metadata chunks, so the only thing it can show a vision model is the shape. Pure and host-free.
/// Sending it anywhere still needs the engineer's explicit permission for this exact image.
/// </summary>
public static class GroupPreviewRenderer
{
    public const int DefaultSize = 256;
    public const int MaxRecords = 12;

    /// <summary>
    /// The usable <c>ev_geometry_sample</c> polylines of up to <see cref="MaxRecords"/> records, in record order.
    /// A sample marked <c>"closed":true</c> is stored without repeating its first point; it is returned closed
    /// (last point joined back to the first) so a closed area never draws as an open "U". A one-point sample (a
    /// block or structure insertion point) shows nothing about what the object is, so it is not drawn: a group of
    /// only such samples has no preview, which the caller reports instead of an image.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Samples(IEnumerable<NeutralQuantityRecord> records)
    {
        var result = new List<IReadOnlyList<(double X, double Y)>>();
        foreach (var record in records)
        {
            if (result.Count >= MaxRecords) break;
            if (!EvidenceReader.TryObservation(record.Measurement.Parameters, EvidenceKeys.GeometrySample, out var observation) ||
                observation.ValueKind != JsonValueKind.Object ||
                !observation.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                continue;
            var line = new List<(double X, double Y)>();
            foreach (var point in points.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) { line.Clear(); break; }
                var x = point[0].ValueKind == JsonValueKind.Number ? point[0].GetDouble() : double.NaN;
                var y = point[1].ValueKind == JsonValueKind.Number ? point[1].GetDouble() : double.NaN;
                if (!double.IsFinite(x) || !double.IsFinite(y)) { line.Clear(); break; }
                line.Add((x, y));
                if (line.Count >= 64) break;
            }
            var closed = observation.TryGetProperty("closed", out var closedFlag) && closedFlag.ValueKind == JsonValueKind.True;
            if (closed && line.Count >= 3 && line[^1] != line[0]) line.Add(line[0]);
            if (line.Count >= 2) result.Add(line);
        }
        return result;
    }

    /// <summary>A grayscale PNG of the polylines, or null when there is nothing to draw.</summary>
    public static byte[]? Render(IReadOnlyList<IReadOnlyList<(double X, double Y)>> polylines, int size = DefaultSize)
    {
        if (size is < 32 or > 512) throw new ArgumentOutOfRangeException(nameof(size));
        var points = polylines.SelectMany(p => p).ToList();
        if (points.Count < 2) return null;
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        var span = Math.Max(maxX - minX, maxY - minY);
        if (!(span > 1e-9)) return null;
        var margin = size * 0.08;
        var scale = (size - 2 * margin) / span;
        var offsetX = margin + ((size - 2 * margin) - (maxX - minX) * scale) / 2;
        var offsetY = margin + ((size - 2 * margin) - (maxY - minY) * scale) / 2;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);
        foreach (var line in polylines)
            for (var i = 1; i < line.Count; i++)
            {
                // Y grows upwards in the drawing and downwards in the image.
                var (x0, y0) = (offsetX + (line[i - 1].X - minX) * scale, size - 1 - (offsetY + (line[i - 1].Y - minY) * scale));
                var (x1, y1) = (offsetX + (line[i].X - minX) * scale, size - 1 - (offsetY + (line[i].Y - minY) * scale));
                DrawLine(pixels, size, x0, y0, x1, y1);
            }
        return EncodePng(pixels, size);
    }

    private static void DrawLine(byte[] pixels, int size, double x0, double y0, double x1, double y1)
    {
        var steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
        for (var s = 0; s <= Math.Max(steps, 1); s++)
        {
            var t = steps == 0 ? 0 : (double)s / steps;
            var x = (int)Math.Round(x0 + (x1 - x0) * t);
            var y = (int)Math.Round(y0 + (y1 - y0) * t);
            for (var dx = 0; dx <= 1; dx++)
                for (var dy = 0; dy <= 1; dy++)
                {
                    var px = x + dx; var py = y + dy;
                    if (px >= 0 && px < size && py >= 0 && py < size) pixels[py * size + px] = 20;
                }
        }
    }

    /// <summary>Minimal PNG: IHDR (8-bit grayscale), one zlib IDAT, IEND. No text, time or EXIF chunks.</summary>
    private static byte[] EncodePng(byte[] pixels, int size)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < size; y++)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(pixels, y * size, size);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zlib);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)size);
        WriteBigEndian(header, 4, (uint)size);
        header[8] = 8; header[9] = 0; header[10] = 0; header[11] = 0; header[12] = 0;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(crcInput));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
