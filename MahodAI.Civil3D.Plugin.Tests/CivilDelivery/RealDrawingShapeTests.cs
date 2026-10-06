using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Shapes taken verbatim from the first real run on the 6422 model
    /// (2026-08-19, run sections-verify-…-c2a28abb and plan-…-717caba1).
    ///
    /// Both defects below passed every synthetic test and failed on Nataly's data on
    /// the first click. These pin the real geometry so they cannot come back.
    /// </summary>
    public class RealDrawingShapeTests
    {
        // ----------------------------------------------------------- sagitta rule

        [Fact]
        public void ClDwgSectionLine_ThreeVerticesWithKinkAtAlignment_IsStraight()
        {
            // Record cl-7D0E: CL.dwg draws the section as first / crossing / last.
            var verts = new List<Pt2>
            {
                new(203988.983, 648598.664),
                new(203994.537, 648576.456),   // the alignment-crossing vertex
                new(203999.172, 648557.919),
            };

            SectionMath.MaxSagitta(verts).Should().BeLessThan(0.5,
                "a section drawn with a vertex at the alignment is still one straight line");
        }

        [Fact]
        public void RoadEdgePolyline_IsNotStraight()
        {
            // A 30-segment alignment polyline must stay REVIEW_REQUIRED.
            var verts = new List<Pt2>();
            for (int i = 0; i <= 30; i++)
                verts.Add(new Pt2(i * 10.0, 20.0 * System.Math.Sin(i * 0.4)));

            SectionMath.MaxSagitta(verts).Should().BeGreaterThan(0.5);
        }

        [Fact]
        public void TwoVertexLine_HasZeroSagitta()
        {
            var verts = new List<Pt2> { new(0, 0), new(100, 30) };
            SectionMath.MaxSagitta(verts).Should().Be(0.0);
        }

        [Fact]
        public void SagittaToleranceIsAProductConstant_NotAMagicNumber()
        {
            // The finding names the threshold; a future reader must find it in one place.
            MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services.ClInstructionReader
                .StraightSagittaToleranceM.Should().Be(0.5);
        }

        // ------------------------------------------- estimate: presentation layers

        [Theory]
        [InlineData("2000-des_SectionView", true)]   // 147 records on the real scan
        [InlineData("3000_SectionView", true)]       // 76 records on the real scan
        [InlineData("C-ROAD-SCTN-TEXT", true)]
        [InlineData("2000-PROFILEVIEW", true)]
        [InlineData("C-ANNO-LABEL", true)]
        [InlineData("SM-MODEL|MHD-SECT-ANNO", true)]
        [InlineData("SM-MODEL|MHD-SECT-ANNO-V6", true)]
        [InlineData("OUTER|INNER|C-ROAD-SCTN-TEXT", true)]
        [InlineData("CURB-EXST", false)]             // real kerbs: 33,476 m - must stay
        [InlineData("WALL-EX", false)]
        [InlineData("2000+110+W", false)]            // corridor design layer - must stay
        [InlineData("0-EZER", false)]
        [InlineData("MEKOROT-28-EX", false)]          // existing utility - must stay
        public void CivilPresentationLayers_AreNotQuantities(string layer, bool presentation)
        {
            // Measuring Civil's own section-view graphics put 223 fake records into the
            // real 6422 scan (2026-08-19). Construction layers must never be caught.
            MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.CivilQuantityExtractionService
                .IsCivilPresentationLayer(layer).Should().Be(presentation);
        }

        // --------------------------------------------------- verify endpoint rule

        [Fact]
        public void VerifyGeometry_AcceptsCivilsThreeVertexReversedSampleLine()
        {
            // Exactly what Civil handed back for cl-7D0E: reversed, with the
            // alignment-crossing vertex inserted. The ENDS are the CL endpoints.
            var expectedWcs = new[] { 203999.172, 648557.919, 203988.983, 648598.664 };
            var actual = new List<(double X, double Y)>
            {
                (203988.983, 648598.664),
                (203994.537, 648576.456),
                (203999.172, 648557.919),
            };

            VerifyEndpointsMatch(actual, expectedWcs).Should().BeTrue(
                "9 of 10 real checks passed and this one failed on vertex FORM, not geometry");
        }

        [Fact]
        public void VerifyGeometry_RejectsAMovedEndpoint()
        {
            var expectedWcs = new[] { 0.0, 0.0, 100.0, 0.0 };
            var actual = new List<(double X, double Y)> { (0, 0), (50, 0), (100, 5) };

            VerifyEndpointsMatch(actual, expectedWcs).Should().BeFalse(
                "a wrong endpoint is a wrong section, whatever the interior looks like");
        }

        [Fact]
        public void VerifyGeometry_RejectsASingleVertex()
        {
            var expectedWcs = new[] { 0.0, 0.0, 100.0, 0.0 };
            var actual = new List<(double X, double Y)> { (0, 0) };

            VerifyEndpointsMatch(actual, expectedWcs).Should().BeFalse();
        }

        // Mirrors SectionVerifyService.EndpointsMatch (private there; the plugin
        // assembly cannot be loaded outside Civil). Keep in lock-step.
        private static bool VerifyEndpointsMatch(List<(double X, double Y)> vertices, double[] wcs)
        {
            const double tol = 0.01;
            if (wcs.Length != 4 || vertices.Count < 2) return false;
            var a = (X: wcs[0], Y: wcs[1]);
            var b = (X: wcs[2], Y: wcs[3]);
            var first = vertices[0];
            var last = vertices[^1];
            bool Match((double X, double Y) p, (double X, double Y) q) =>
                System.Math.Abs(p.X - q.X) < tol && System.Math.Abs(p.Y - q.Y) < tol;
            return (Match(first, a) && Match(last, b)) || (Match(first, b) && Match(last, a));
        }
    }
}
