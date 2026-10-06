using System;
using System.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using Xunit;
using P = MahodAI.Civil3D.Plugin.Tools.Creation.CreationReadbackPolicy.Point;
using B = MahodAI.Civil3D.Plugin.Tools.Creation.CreationReadbackPolicy.Bounds;

namespace MahodAI.Core.Tests;

/// <summary>Executable readback rules, not native Civil creation or transaction execution.</summary>
public sealed class VerifiedCreationReadbackTests
{
    private static CreationReadbackPolicy.SampleProof Line(string handle = "A1", double station = 10) =>
        new(handle, station, new[] { new P(-20,0), new P(0,0), new P(30,0) }, new B(-20,0,30,0), true);

    [Fact]
    public void RequestedRangeIncludesEveryRegularStationAndExactEndOnce()
    {
        CreationReadbackPolicy.Stations(100,150,101,127,10,20,30).Should().Equal(101,111,121,127);
        CreationReadbackPolicy.Stations(0,100,0,100,10,20,30).Should().HaveCount(11).And.EndWith(100);
        CreationReadbackPolicy.Stations(0,1,0,0.0000003,0.0000001,20,30).Should().HaveCount(4);
    }

    [Theory]
    [InlineData(double.NaN, 100, 10, 20, 30)]
    [InlineData(-1, 100, 10, 20, 30)]
    [InlineData(0, 101, 10, 20, 30)]
    [InlineData(10, 10, 10, 20, 30)]
    [InlineData(0, 100, 0, 20, 30)]
    [InlineData(0, 100, double.PositiveInfinity, 20, 30)]
    [InlineData(0, 100, 10, 0, 30)]
    [InlineData(0, 100, 10, 20, -1)]
    [InlineData(0, 100, 0.000001, 20, 30)]
    public void InvalidOrUnboundedRequestRefusesWithoutTruncating(double start, double end, double interval, double left, double right)
    {
        Action run = () => CreationReadbackPolicy.Stations(0,100,start,end,interval,left,right);
        run.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompleteGroupWithExactAsymmetricExtentsCanPass()
    {
        var lines = new[] { Line("A1",10), Line("A2",20) };
        foreach (var line in lines) CreationReadbackPolicy.RequireSample(line.Station,new(-20,0),new(0,0),new(30,0),line);
        CreationReadbackPolicy.RequireGroup(new[] {10d,20},lines,new[] {"A2","A1"});
        ToolResult.Ok(new { verified = true }).MayCommitProductionObject.Should().BeTrue();
    }

    [Theory]
    [InlineData("station")]
    [InlineData("parent")]
    [InlineData("empty")]
    [InlineData("missing-center")]
    [InlineData("wrong-extent")]
    [InlineData("unreadable-extents")]
    [InlineData("bent")]
    [InlineData("nonfinite")]
    public void MissingOrDifferentSampleGeometryCannotAuthorizeCommit(string problem)
    {
        var line = Line();
        line = problem switch
        {
            "station" => line with { Station = 10.001 },
            "parent" => line with { ParentExact = false },
            "empty" => line with { Vertices = Array.Empty<P>() },
            "missing-center" => line with { Vertices = new[] {new P(-20,0),new P(30,0)} },
            "wrong-extent" => line with { Vertices = new[] {new P(-10,0),new P(0,0),new P(30,0)} },
            "unreadable-extents" => line with { Extents = default },
            "bent" => line with { Vertices = new[] {new P(-20,0),new P(0,0),new P(5,0.001),new P(30,0)} },
            _ => line with { Vertices = new[] {new P(-20,0),new P(double.NaN,0),new P(30,0)} }
        };
        Action accept = () => CreationReadbackPolicy.RequireSample(10,new(-20,0),new(0,0),new(30,0),line);
        accept.Should().Throw<InvalidOperationException>();
        ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "readback rejected").MayCommitProductionObject.Should().BeFalse();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("wrong-station")]
    public void GroupIdAloneOrIncompleteChildClosureIsNeverSuccess(string problem)
    {
        var lines = new[] {Line("A1",10),Line("A2",20)};
        var handles = new[] {"A1","A2"};
        switch(problem)
        {
            case "empty": lines = Array.Empty<CreationReadbackPolicy.SampleProof>(); handles = Array.Empty<string>(); break;
            case "partial": lines = new[] {lines[0]}; handles = new[] {"A1"}; break;
            case "duplicate": handles = new[] {"A1","A1"}; break;
            case "foreign": handles = new[] {"A1","B9"}; break;
            case "wrong-station": lines[1] = Line("A2",21); break;
        }
        Action accept = () => CreationReadbackPolicy.RequireGroup(new[] {10d,20},lines,handles);
        accept.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SourceSelectionRequiresUniqueLayerOrOneExactRequestedHandle()
    {
        CreationReadbackPolicy.SelectSource(new[] {"A1"},null).Should().Be("A1");
        CreationReadbackPolicy.SelectSource(new[] {"A1","B2"},"b2").Should().Be("B2");
        foreach(var candidates in new[] {Array.Empty<string>(),new[] {"A1","B2"},new[] {"A1","A1"}})
        {
            Action select = () => CreationReadbackPolicy.SelectSource(candidates,null);
            select.Should().Throw<InvalidOperationException>();
        }
        Action missing = () => CreationReadbackPolicy.SelectSource(new[] {"A1"},"B2");
        missing.Should().Throw<InvalidOperationException>();
        Action invalid = () => CreationReadbackPolicy.SelectSource(new[] {"A1"},"A1/B2");
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PositiveCurvedSourceReadbackIncludesInteriorGeometryAndNonzeroStations()
    {
        var points = new[] {new P(-10,0),new P(0,10),new P(10,0)};
        var length = 10*Math.PI;
        CreationReadbackPolicy.RequireAlignment(length,length,100,100+length,1,points,points,new(-10,0,10,10),true);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("length")]
    [InlineData("station")]
    [InlineData("interior")]
    [InlineData("extents")]
    [InlineData("source-erased")]
    public void EmptyFallbackOrGeometricMismatchNeverPassesAlignmentReadback(string problem)
    {
        var points = new[] {new P(-10,0),new P(0,10),new P(10,0)};
        var actual = problem == "interior" ? new[] {points[0],new P(0,0),points[2]} : points;
        var length = 10*Math.PI;
        Action accept = () => CreationReadbackPolicy.RequireAlignment(length,problem == "length" ? 0 : length,
            100,100+length+(problem == "station" ? 1 : 0),problem == "empty" ? 0 : 1,points,actual,
            problem == "extents" ? default : new B(-10,0,10,10),problem != "source-erased");
        accept.Should().Throw<InvalidOperationException>();
    }
}
