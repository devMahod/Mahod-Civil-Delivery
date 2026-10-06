using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>The group preview a vision model may see: shape only, no coordinates, text or metadata.</summary>
public sealed class GroupPreviewRendererTests
{
    private static NeutralQuantityRecord Rec(string? sample, string status = "read") => new()
    {
        RecordId = "r-" + Guid.NewGuid().ToString("N"), ProjectProfileId = "SYNTHETIC", RunId = "SYNTHETIC",
        Source = new QuantitySource { Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = "1", EntityType = "X", Layer = "L" },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "polyline-length", RawValue = 1, Unit = "מטר",
            Parameters = sample == null ? new Dictionary<string, string>() : new Dictionary<string, string>
            {
                [EvidenceKeys.GeometrySample] = sample, [EvidenceKeys.GeometrySample + "_status"] = status,
            },
        },
        Classification = new QuantityClassification(),
    };

    [Fact]
    public void TheImageIsAPlainGrayscalePngWithoutCoordinatesOrTextChunks()
    {
        var records = new[]
        {
            Rec("{\"space\":\"host\",\"points\":[[184000.5,662000.25],[184030.5,662000.25],[184030.5,662010.25]]}"),
            Rec("{\"space\":\"host\",\"points\":[[184000.5,662005],[184030.5,662005]]}"),
        };
        var samples = GroupPreviewRenderer.Samples(records);
        samples.Should().HaveCount(2);
        var png = GroupPreviewRenderer.Render(samples)!;

        png.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        var chunks = new List<string>();
        for (var offset = 8; offset < png.Length;)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            chunks.Add(Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }
        chunks.Should().Equal("IHDR", "IDAT", "IEND");
        png.Length.Should().BeLessThan(64 * 1024);
        Encoding.Latin1.GetString(png).Should().NotContain("184000").And.NotContain("662000");
        GroupPreviewRenderer.Render(samples).Should().Equal(png, "rendering is deterministic");
    }

    [Fact]
    public void OnlyReadableSamplesAreDrawnAndNothingIsInvented()
    {
        GroupPreviewRenderer.Samples(new[]
        {
            Rec(null),
            Rec("{\"space\":\"host\",\"points\":[[0,0],[1,1]]}", "unavailable:Exception"),
            Rec("{\"space\":\"host\",\"points\":[[0,0],[\"x\",1]]}"),
            Rec("{\"space\":\"host\",\"points\":[[0,0]]}"),
            Rec("not json"),
        }).Should().BeEmpty();
        GroupPreviewRenderer.Render(Array.Empty<IReadOnlyList<(double, double)>>()).Should().BeNull();
        GroupPreviewRenderer.Render(new[] { (IReadOnlyList<(double, double)>)new[] { (5.0, 5.0), (5.0, 5.0) } }).Should().BeNull("a zero-size shape has no picture");
        FluentActions.Invoking(() => GroupPreviewRenderer.Render(new[] { (IReadOnlyList<(double, double)>)new[] { (0.0, 0.0), (1.0, 1.0) } }, 4096))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AClosedSampleIsDrawnClosedAndAnOpenOneStaysOpen()
    {
        // A 4-vertex parking bay as the collector writes it: closed, first point not repeated.
        const string bay = "[[0,0],[10,0],[10,10],[0,10]]";
        var closed = GroupPreviewRenderer.Samples(new[] { Rec("{\"space\":\"host\",\"closed\":true,\"points\":" + bay + "}") });
        closed.Single().Should().Equal((0d, 0d), (10d, 0d), (10d, 10d), (0d, 10d), (0d, 0d));
        var open = GroupPreviewRenderer.Samples(new[] { Rec("{\"space\":\"host\",\"closed\":false,\"points\":" + bay + "}") });
        open.Single().Should().HaveCount(4);
        // A closed sample that already repeats its first point is not closed twice.
        GroupPreviewRenderer.Samples(new[] { Rec("{\"closed\":true,\"points\":[[0,0],[10,0],[10,10],[0,0]]}") })
            .Single().Should().HaveCount(4);

        // The fourth (left) side exists only in the closed drawing.
        LeftSideIsDrawn(GroupPreviewRenderer.Render(closed)!).Should().BeTrue();
        LeftSideIsDrawn(GroupPreviewRenderer.Render(open)!).Should().BeFalse();
    }

    [Fact]
    public void TheCollectorsHatchPreviewIsDrawnAsAClosedOutline()
    {
        var shape = EvidenceShape.Rings(new[]
        {
            new (double X, double Y)[] { (0, 0), (10, 0), (10, 10), (0, 10) },
            new (double X, double Y)[] { (3, 3), (7, 3), (7, 7), (3, 7) },
        });
        var sample = EvidenceJson.GeometrySample(shape);
        var samples = GroupPreviewRenderer.Samples(new[] { Rec(sample.Json, sample.Status) });
        samples.Single().Should().HaveCount(5);
        LeftSideIsDrawn(GroupPreviewRenderer.Render(samples)!).Should().BeTrue();
    }

    // The renderer maps a 10 m square into [20.48, 235.52] px; its left side runs at x ≈ 20-21, y 20..235.
    private static bool LeftSideIsDrawn(byte[] png)
    {
        const int size = GroupPreviewRenderer.DefaultSize;
        var pixels = PreviewFixPixels(png, size);
        for (var y = 110; y <= 145; y++)
            for (var x = 18; x <= 23; x++)
                if (pixels[y * size + x] < 128) return true;
        return false;
    }

    /// <summary>Decodes the renderer's own PNG: one zlib IDAT of 8-bit grayscale rows, each with filter 0.</summary>
    private static byte[] PreviewFixPixels(byte[] png, int size)
    {
        byte[]? idat = null;
        for (var offset = 8; offset < png.Length;)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
            {
                idat = new byte[length];
                Array.Copy(png, offset + 8, idat, 0, length);
            }
            offset += 12 + length;
        }
        idat.Should().NotBeNull();
        using var input = new MemoryStream(idat!);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rows = raw.ToArray();
        rows.Should().HaveCount(size * (size + 1));
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            ((int)rows[y * (size + 1)]).Should().Be(0, "every row uses filter 0");
            Array.Copy(rows, y * (size + 1) + 1, pixels, y * size, size);
        }
        return pixels;
    }
}
