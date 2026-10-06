using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Extractors;
using MahodAI.Civil3D.Plugin.Models;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class ExtractorHelpersTests
    {
        #region SafeExecute<T>

        [Fact]
        public void SafeExecute_ReturnsResult_WhenOperationSucceeds()
        {
            var result = ExtractorHelpers.SafeExecute(() => 42, 0, "test");
            result.Should().Be(42);
        }

        [Fact]
        public void SafeExecute_ReturnsDefault_WhenOperationThrows()
        {
            var result = ExtractorHelpers.SafeExecute<int>(() => throw new InvalidOperationException("fail"), -1, "test");
            result.Should().Be(-1);
        }

        [Fact]
        public void SafeExecuteVoid_DoesNotThrow_WhenOperationFails()
        {
            var act = () => ExtractorHelpers.SafeExecute(() => throw new InvalidOperationException("fail"), "test");
            act.Should().NotThrow();
        }

        [Fact]
        public void SafeExecuteVoid_ExecutesOperation_WhenOperationSucceeds()
        {
            bool executed = false;
            ExtractorHelpers.SafeExecute(() => { executed = true; }, "test");
            executed.Should().BeTrue();
        }

        #endregion

        #region RadiansToDegrees / DegreesToRadians

        [Fact]
        public void RadiansToDegrees_ConvertsPiTo180()
        {
            ExtractorHelpers.RadiansToDegrees(Math.PI).Should().Be(180.0);
        }

        [Fact]
        public void RadiansToDegrees_ConvertsZeroToZero()
        {
            ExtractorHelpers.RadiansToDegrees(0).Should().Be(0.0);
        }

        [Fact]
        public void RadiansToDegrees_ConvertsTwoPiTo360()
        {
            ExtractorHelpers.RadiansToDegrees(2 * Math.PI).Should().Be(360.0);
        }

        [Fact]
        public void DegreesToRadians_Converts180ToPi()
        {
            ExtractorHelpers.DegreesToRadians(180.0).Should().BeApproximately(Math.PI, 1e-10);
        }

        [Fact]
        public void DegreesToRadians_ConvertsZeroToZero()
        {
            ExtractorHelpers.DegreesToRadians(0).Should().Be(0.0);
        }

        [Fact]
        public void RadiansToDegrees_And_DegreesToRadians_AreInverse()
        {
            double original = 45.0;
            var roundTrip = ExtractorHelpers.RadiansToDegrees(ExtractorHelpers.DegreesToRadians(original));
            roundTrip.Should().BeApproximately(original, 1e-10);
        }

        #endregion

        #region DirectionToBearing

        [Fact]
        public void DirectionToBearing_EastDirectionReturnsBearing90()
        {
            // 0 radians = East in AutoCAD → bearing = 90 degrees (East from North)
            ExtractorHelpers.DirectionToBearing(0).Should().Be(90.0);
        }

        [Fact]
        public void DirectionToBearing_NorthDirectionReturnsBearing0()
        {
            // PI/2 radians = North in AutoCAD → bearing = 0 degrees (North)
            ExtractorHelpers.DirectionToBearing(Math.PI / 2).Should().Be(0.0);
        }

        [Fact]
        public void DirectionToBearing_WestDirectionReturnsBearing270()
        {
            // PI radians = West in AutoCAD → bearing = 270 degrees
            ExtractorHelpers.DirectionToBearing(Math.PI).Should().Be(270.0);
        }

        [Fact]
        public void DirectionToBearing_SouthDirectionReturnsBearing180()
        {
            // 3*PI/2 radians = South in AutoCAD → bearing = 180 degrees
            ExtractorHelpers.DirectionToBearing(3 * Math.PI / 2).Should().Be(180.0);
        }

        [Fact]
        public void DirectionToBearing_NormalizesNegativeResult()
        {
            // Large negative direction should still normalize to 0-360
            var result = ExtractorHelpers.DirectionToBearing(-Math.PI / 2);
            result.Should().BeInRange(0, 360);
        }

        #endregion

        #region CalculateGradePercent

        [Fact]
        public void CalculateGradePercent_Returns5_ForFivePercentGrade()
        {
            ExtractorHelpers.CalculateGradePercent(5.0, 100.0).Should().Be(5.0);
        }

        [Fact]
        public void CalculateGradePercent_ReturnsZero_WhenHorizontalDistanceNearZero()
        {
            ExtractorHelpers.CalculateGradePercent(5.0, 0.0001).Should().Be(0);
        }

        [Fact]
        public void CalculateGradePercent_ReturnsNegative_ForDownhill()
        {
            ExtractorHelpers.CalculateGradePercent(-3.0, 100.0).Should().Be(-3.0);
        }

        [Fact]
        public void CalculateGradePercent_ReturnsZero_ForZeroElevationChange()
        {
            ExtractorHelpers.CalculateGradePercent(0, 100.0).Should().Be(0);
        }

        #endregion

        #region CalculateKValue

        [Fact]
        public void CalculateKValue_Returns100_ForStandardCurve()
        {
            // L=200, gradeIn=2%, gradeOut=4% → A = |4-2| = 2 → K = 200/2 = 100
            ExtractorHelpers.CalculateKValue(200, 2, 4).Should().Be(100);
        }

        [Fact]
        public void CalculateKValue_ReturnsZero_WhenGradesAreEqual()
        {
            ExtractorHelpers.CalculateKValue(200, 3, 3).Should().Be(0);
        }

        [Fact]
        public void CalculateKValue_HandlesNegativeGrades()
        {
            // L=300, gradeIn=-2%, gradeOut=1% → A = |1-(-2)| = 3 → K = 300/3 = 100
            ExtractorHelpers.CalculateKValue(300, -2, 1).Should().Be(100);
        }

        #endregion

        #region CalculateClothoidA

        [Fact]
        public void CalculateClothoidA_ReturnsCorrectValue()
        {
            // A = sqrt(100 * 400) = sqrt(40000) = 200
            ExtractorHelpers.CalculateClothoidA(100, 400).Should().Be(200);
        }

        [Fact]
        public void CalculateClothoidA_ReturnsZero_WhenLengthIsZero()
        {
            ExtractorHelpers.CalculateClothoidA(0, 400).Should().Be(0);
        }

        [Fact]
        public void CalculateClothoidA_ReturnsZero_WhenRadiusIsNegative()
        {
            ExtractorHelpers.CalculateClothoidA(100, -5).Should().Be(0);
        }

        #endregion

        #region EstimateCoordinateSystem

        [Fact]
        public void EstimateCoordinateSystem_ReturnsUnknown_ForNullExtents()
        {
            var crs = ExtractorHelpers.EstimateCoordinateSystem(null);
            crs.Name.Should().Be("Unknown");
            crs.IsEstimated.Should().BeTrue();
        }

        [Fact]
        public void EstimateCoordinateSystem_DetectsIsraelTMGrid()
        {
            var extents = new BoundingBox2D { MinX = 180000, MinY = 600000, MaxX = 200000, MaxY = 650000 };
            var crs = ExtractorHelpers.EstimateCoordinateSystem(extents);
            crs.Name.Should().Be("Israel TM Grid");
            crs.Epsg.Should().Be("EPSG:2039");
        }

        [Fact]
        public void EstimateCoordinateSystem_DetectsUTM()
        {
            var extents = new BoundingBox2D { MinX = 500000, MinY = 4000000, MaxX = 600000, MaxY = 4100000 };
            var crs = ExtractorHelpers.EstimateCoordinateSystem(extents);
            crs.Name.Should().Be("UTM (estimated)");
        }

        [Fact]
        public void EstimateCoordinateSystem_DetectsGeographic()
        {
            var extents = new BoundingBox2D { MinX = 34.0, MinY = 31.0, MaxX = 35.0, MaxY = 32.0 };
            var crs = ExtractorHelpers.EstimateCoordinateSystem(extents);
            crs.Name.Should().Be("Geographic (WGS84?)");
            crs.Epsg.Should().Be("EPSG:4326");
        }

        [Fact]
        public void EstimateCoordinateSystem_ReturnsUnknown_ForUnrecognizedCoords()
        {
            var extents = new BoundingBox2D { MinX = -500000, MinY = -500000, MaxX = -400000, MaxY = -400000 };
            var crs = ExtractorHelpers.EstimateCoordinateSystem(extents);
            crs.Name.Should().Be("Unknown");
        }

        #endregion

        #region GetPropertyValue / GetPropertyValueStruct

        [Fact]
        public void GetPropertyValue_ReturnsValue_ForExistingProperty()
        {
            var obj = new { Name = "test" };
            ExtractorHelpers.GetPropertyValue<string>(obj, "Name").Should().Be("test");
        }

        [Fact]
        public void GetPropertyValue_ReturnsNull_ForMissingProperty()
        {
            var obj = new { Name = "test" };
            ExtractorHelpers.GetPropertyValue<string>(obj, "Missing").Should().BeNull();
        }

        [Fact]
        public void GetPropertyValueStruct_ReturnsValue_ForExistingProperty()
        {
            var obj = new { Count = 42 };
            ExtractorHelpers.GetPropertyValueStruct<int>(obj, "Count").Should().Be(42);
        }

        [Fact]
        public void GetPropertyValueStruct_ReturnsNull_ForMissingProperty()
        {
            var obj = new { Count = 42 };
            ExtractorHelpers.GetPropertyValueStruct<int>(obj, "Missing").Should().BeNull();
        }

        #endregion

        #region Round / FormatNumber

        [Fact]
        public void Round_DefaultsTo3Decimals()
        {
            ExtractorHelpers.Round(3.14159).Should().Be(3.142);
        }

        [Fact]
        public void Round_RespectsCustomDecimals()
        {
            ExtractorHelpers.Round(3.14159, 1).Should().Be(3.1);
        }

        [Fact]
        public void FormatNumber_FormatsMillions()
        {
            var result = ExtractorHelpers.FormatNumber(2500000);
            result.Should().EndWith("M");
            result.Should().Contain("2");
            result.Should().Contain("50");
        }

        [Fact]
        public void FormatNumber_FormatsThousands()
        {
            var result = ExtractorHelpers.FormatNumber(1500);
            result.Should().EndWith("K");
            result.Should().Contain("1");
            result.Should().Contain("50");
        }

        [Fact]
        public void FormatNumber_FormatsSmallNumbers()
        {
            var result = ExtractorHelpers.FormatNumber(42.5);
            result.Should().NotEndWith("K");
            result.Should().NotEndWith("M");
            result.Should().Contain("42");
            result.Should().Contain("50");
        }

        [Fact]
        public void FormatNumber_FormatsNegativeMillions()
        {
            var result = ExtractorHelpers.FormatNumber(-2500000);
            result.Should().EndWith("M");
            result.Should().StartWith("-");
        }

        #endregion

        #region CreateIssue

        [Fact]
        public void CreateIssue_SetsAllRequiredFields()
        {
            var issue = ExtractorHelpers.CreateIssue("Q1", "Warning", "Alignment", "Title", "Message");
            issue.Id.Should().Be("Q1");
            issue.Severity.Should().Be("Warning");
            issue.Category.Should().Be("Alignment");
            issue.Title.Should().Be("Title");
            issue.Message.Should().Be("Message");
            issue.ObjectName.Should().BeNull();
            issue.Station.Should().BeNull();
            issue.Location.Should().BeNull();
            issue.SuggestedFix.Should().BeNull();
        }

        [Fact]
        public void CreateIssue_SetsOptionalFields()
        {
            var loc = new Point3D(1, 2, 3);
            var issue = ExtractorHelpers.CreateIssue("Q2", "Error", "Surface", "T", "M", "obj1", 100.5, loc, "fix it");
            issue.ObjectName.Should().Be("obj1");
            issue.Station.Should().Be(100.5);
            issue.Location.Should().BeSameAs(loc);
            issue.SuggestedFix.Should().Be("fix it");
        }

        #endregion
    }
}
