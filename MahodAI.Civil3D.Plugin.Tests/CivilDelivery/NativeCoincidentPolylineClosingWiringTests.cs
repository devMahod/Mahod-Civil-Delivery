using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Source wiring only; the Core tests execute geometry, and live Civil is separate.
public sealed class NativeCoincidentPolylineClosingWiringTests
{
    [Fact]
    public void NativeCertificateValidatesLiveIndexAndPreservesTheNonexactLastEndpoint()
    {
        var root = typeof(NativeCoincidentPolylineClosingWiringTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        var source = File.ReadAllText(Path.Combine(root, "CivilDelivery", "Sections", "Services", "SectionGeometryCollector.cs"));
        var start = source.IndexOf("case Polyline polyline:", StringComparison.Ordinal);
        var end = source.IndexOf("case Polyline3d polyline3d:", start, StringComparison.Ordinal);
        var branch = source[start..end];
        branch.Should().Contain("i != vertexCount - 1").And.Contain("i < 0")
            .And.Contain("polyline.NumberOfVertices != vertexCount || !polyline.Closed")
            .And.Contain("polyline.GetSegmentType(i) == SegmentType.Coincident")
            .And.Contain("}, maxScale)")
            .And.Contain("nativeCoincidentClosing: sourceRead.NativeCoincidentClosing")
            .And.Contain("if (sampled.SkippedNativeCoincidentClosingSegmentIndex == null)")
            .And.Contain("RemoveClosingDuplicate(vertices, closed)")
            .And.Contain("literal last endpoint and preceding live spans retained")
            .And.NotContain("SetPointAt").And.NotContain("SetBulgeAt");
        branch.IndexOf("polyline.NumberOfVertices != vertexCount", StringComparison.Ordinal).Should()
            .BeLessThan(branch.IndexOf("polyline.GetSegmentType(i)", StringComparison.Ordinal));
    }
}
