using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools;

/// <summary>Adapter wiring only. Native SDK execution/rollback is a separate acceptance test.</summary>
public sealed class VerifiedCreationToolWiringTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        typeof(VerifiedCreationToolWiringTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(item => item.Key == "MahodPluginSourceDir").Value!,"Tools","Creation",file));

    [Fact]
    public void SampleGroupRequiresEveryTypedChildReadbackAndNeverQueuesWork()
    {
        var source = Source("CreateSampleLineGroupTool.cs");
        source.Should().Contain("SampleLineGroup.Create(groupName, alignment.ObjectId)")
            .And.Contain("SampleLine.Create(name, groupId, new Point2dCollection")
            .And.Contain("alignment.PointLocation(station, offset")
            .And.Contain("line.Vertices[i].Location").And.Contain("line.GeometricExtents")
            .And.Contain("CreationReadbackPolicy.RequireSample(").And.Contain("CreationReadbackPolicy.RequireGroup(")
            .And.NotContain("SendStringToExecute").And.NotContain("GetMethod(").And.NotContain(".Invoke(");
        source.IndexOf("CreationReadbackPolicy.RequireGroup(").Should().BeLessThan(source.IndexOf("ToolResult.Ok("));
        source.Should().Contain("catch (OperationCanceledException) { throw; }").And.Contain("ToolResult.Fail(");
    }

    [Fact]
    public void AlignmentImportsExactSelectedGeometryWithoutDeletingOrSmoothingTheSource()
    {
        var source = Source("CreateAlignmentFromPolylineTool.cs");
        source.Should().Contain("\"polyline_handle\"").And.Contain("CreationReadbackPolicy.SelectSource(")
            .And.Contain("new PolylineOptions { PlineId = source.ObjectId, AddCurvesBetweenTangents = false, EraseExistingEntities = false }")
            .And.Contain("CivilAlignment.Create(civilDoc, options, name,")
            .And.Contain("source.GetPointAtParameter(parameter)").And.Contain("alignment.PointLocation(")
            .And.Contain("CreationReadbackPolicy.RequireAlignment(").And.Contain("alignment.GeometricExtents")
            .And.NotContain("GetMethods(").And.NotContain(".Invoke(").And.NotContain("SendStringToExecute");
        source.Split("CivilAlignment.Create(").Length.Should().Be(2,"there is exactly one real creation attempt, never an empty fallback");
        source.IndexOf("CreationReadbackPolicy.RequireAlignment(").Should().BeLessThan(source.IndexOf("ToolResult.Ok("));
    }
}
