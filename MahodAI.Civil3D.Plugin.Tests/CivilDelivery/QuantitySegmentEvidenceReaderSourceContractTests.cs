using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Source contract of the live plan-geometry collector for the BoQ one-object rule: which entities give a chain, the
    /// +Z plan gate of the reference, the native arc read, the Core tessellation (unit-tested in
    /// QuantityGeometryEvidenceTests, never re-implemented against AutoCAD types), both bounds and the block insertion
    /// point. Code shape only; no Autodesk member runs.
    /// </summary>
    public sealed class QuantitySegmentEvidenceReaderSourceContractTests
    {
        private static string Reader()
        {
            var root = typeof(QuantitySegmentEvidenceReaderSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;
            return File.ReadAllText(Path.Combine(root, "CivilDelivery", "Estimate", "QuantitySegmentEvidenceReader.cs"));
        }

        [Fact]
        public void EveryLengthEntityOfTheReferenceGetsItsPlanChainFromTheCoreTessellation()
        {
            var reader = Reader();
            foreach (var entity in new[]
                     {
                         "case Line line:", "case Polyline polyline:", "case Polyline2d polyline2d:", "case Polyline3d polyline3d:",
                         "case Arc arc:", "case Circle circle:", "case BlockReference block:", "case Hatch h:",
                     })
                reader.Should().Contain(entity);
            reader.Should().Contain("QuantityGeometryEvidence.TessellatePolyline(")
                .And.Contain("QuantityGeometryEvidence.TessellateArc(")
                .And.Contain("QuantityGeometryEvidence.TessellateCircle(")
                // The arc maths lives in Core, where it is unit-tested; the plugin only reads native values.
                .And.NotContain("Math.Atan").And.NotContain("Math.Cos").And.NotContain("Math.Sin");
            // A bulge is read only for a segment the polyline itself reports as an arc (never the unused bulge of a
            // coincident closing span or of an open polyline's last vertex).
            reader.Should().Contain("polyline.GetSegmentType(i) == SegmentType.Arc ? polyline.GetBulgeAt(i) : 0.0")
                .And.Contain("vertex.Bulge")
                // Arc density is bounded in metres through the entity database's own INSUNITS.
                .And.Contain("DrawingUnitPolicy.Resolve(entity.Database.Insunits).LinearToMetres");
        }

        [Fact]
        public void OnlyPlanObjectsGiveChainsAndTheStoredVertexBoundKeepsItsStatus()
        {
            var reader = Reader();
            // ARC and CIRCLE need a +Z normal, like boq_geometry.entity_chords. LWPOLYLINE and POLYLINE2D also accept a
            // face-down (-Z) normal: WCS vertices and mirrored bulges (review 30/09 — they were counted by drawn length).
            Regex.Matches(reader, @"if \(!IsPlan\((arc|circle)\.Normal\)\)").Count.Should().Be(2);
            Regex.Matches(reader, @"if \(!IsPlan\((polyline|polyline2d)\.Normal\) && !faceDown\)").Count.Should().Be(2);
            reader.Should().Contain("vertices.Add((p.X, p.Y, faceDown ? -bulge : bulge));")
                .And.Contain("var position = faceDown ? polyline2d.VertexPosition(vertex) : vertex.Position;")
                .And.Contain("faceDown ? -vertex.Bulge : vertex.Bulge");
            Regex.Matches(reader, @"QuantityGeometryEvidence\.StatusNonPlanNormal").Count.Should().Be(4);
            reader.Should().NotContain("polyline3d.Normal", "a 3D polyline is measured by the XY of its vertices");
            Regex.Matches(reader, @"QuantityGeometryEvidence\.OverLimit\(").Count.Should().Be(3,
                "the stored-vertex bound of LWPOLYLINE / POLYLINE2D / POLYLINE3D");
            reader.Should().Contain("vertex.VertexType != Vertex2dType.SimpleVertex")
                .And.Contain("vertex.VertexType != Vertex3dType.SimpleVertex");
        }

        [Fact]
        public void TheInsertionPointHasItsOwnKeyAndAClosedChainIsPublishedWithItsFlag()
        {
            var reader = Reader();
            reader.Should().Contain("values[QuantityGeometryEvidence.RawInsertPoint] = QuantityGeometryEvidence.FormatPoint(position.X, position.Y);")
                .And.Contain("BlockReference => QuantityGeometryEvidence.RawInsertPointStatus")
                .And.Contain("Hatch => QuantityGeometryEvidence.RawHatchStatus")
                .And.Contain("_ => QuantityGeometryEvidence.RawSegmentsStatus");
            // Closed chains (polylines, circles) carry the flag and never a repeated first point: the consumer closes them.
            reader.Should().Contain("values[QuantityGeometryEvidence.RawSegmentsClosed] = closed ? \"true\" : \"false\";")
                .And.Contain("Put(values, status, points, closed: true);");
        }
    }
}
