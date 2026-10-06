using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionGeometryCollectorSourceContractTests
{
    private static string Source()
    {
        var pluginSrc = typeof(SectionGeometryCollectorSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections",
            "Services", "SectionGeometryCollector.cs"));
    }

    [Fact]
    public void CurvesRetainNativeSampling_WhileLwPolylineUsesRawOcsSegmentsAndFullWorldTransform()
    {
        var source = Source();

        source.Should().Contain("case Arc arc:")
            .And.Contain("var sweep = arc.TotalAngle")
            .And.Contain("case Circle circle:")
            .And.Contain("case Polyline2d polyline2d:")
            .And.Contain("points.Add(polyline2d.VertexPosition(vertex))")
            .And.Contain("bulges.Add(vertex.Bulge)")
            .And.Contain("point = polyline2d.GetPointAtParameter(parameter)")
            .And.Contain("curve.GetPointAtParameter(parameter)")
            .And.Contain("ArcTessellationSegmentCount(")
            .And.Contain("MaxCurveSagittaM")
            .And.NotContain("Legacy Polyline2d contains bulged segments; exact host sampling is unavailable.")
            .And.NotContain("case Polyline polyline:\n                    {\n                        closed = polyline.Closed;\n                        var vertices = new List<V3>(polyline.NumberOfVertices)",
                "the old endpoint-only chord extraction must not return");

        var lwStart = source.IndexOf("case Polyline polyline:", StringComparison.Ordinal);
        var lwEnd = source.IndexOf("case Polyline3d polyline3d:", lwStart, StringComparison.Ordinal);
        lwStart.Should().BeGreaterThan(0);
        lwEnd.Should().BeGreaterThan(lwStart);
        source[lwStart..lwEnd].Should().Contain("polyline.GetPoint2dAt(i)")
            .And.Contain("polyline.GetBulgeAt(i)")
            .And.Contain("SectionPolylineSegmentSampling.ReadSource(vertexCount, closed,")
            .And.Contain("var sourceVertices = sourceRead.Vertices")
            .And.Contain("SectionPolylineSegmentSampling.Sample(sourceVertices, closed,")
            .And.Contain("var maxScale = MaxLinearScale(transform)")
            .And.Contain("maxScale, MaxCurveSagittaM")
            .And.Contain("var normal = polyline.Normal")
            .And.Contain("transform * Matrix3d.PlaneToWorld(normal)")
            .And.Contain("Point3d.Origin.TransformBy(ocsToWorld)")
            .And.Contain("Vector3d.XAxis.TransformBy(ocsToWorld)")
            .And.Contain("Vector3d.YAxis.TransformBy(ocsToWorld)")
            .And.Contain("Vector3d.ZAxis.TransformBy(ocsToWorld)")
            .And.Contain("var elevation = polyline.Elevation")
            .And.Contain("SectionPolylineSegmentSampling.ToWcs(sampled, elevation,")
            .And.Contain("unused closing bulge was not read")
            .And.Contain("RemoveClosingDuplicate(vertices, closed)")
            .And.NotContain("GetPointAtParameter(",
                "a malformed unused closing bulge must not prevent sampling the finite live spans through whole-Curve evaluation");
    }

    [Fact]
    public void NativePolylineFailuresRetainTheExactGetterOrSamplingStage()
    {
        var source = Source();
        source.Should().Contain("readStage = $\"Polyline.GetPoint2dAt({i})\"")
            .And.Contain("readStage = $\"Polyline.GetBulgeAt({i})\"")
            .And.Contain("readStage = \"Polyline.Normal\"")
            .And.Contain("readStage = \"Polyline.Elevation\"")
            .And.Contain("read_stage={readStage}");
    }

    [Fact]
    public void LeaderOnMatchingLayer_IsIgnoredAsDraftingAnnotationBeforeGeometryGate()
    {
        var source = Source();
        var classification = source.IndexOf("var mark = Classify(", StringComparison.Ordinal);
        var selected = source.IndexOf("if (selected == null) return;", classification,
            StringComparison.Ordinal);
        var leaderGuard = source.IndexOf("if (entity is Leader) return;", selected,
            StringComparison.Ordinal);
        var extraction = source.IndexOf("TryExtractVertices(", leaderGuard,
            StringComparison.Ordinal);

        selected.Should().BeGreaterThan(classification);
        leaderGuard.Should().BeGreaterThan(selected);
        extraction.Should().BeGreaterThan(leaderGuard,
            "drafting Leaders must not become crossings or unsupported-geometry blockers");
    }

    [Fact]
    public void MatchingUnsupportedCurveOrProxy_AddsAPlanBlockingFinding()
    {
        var source = Source();
        var classification = source.IndexOf("var mark = Classify(", StringComparison.Ordinal);
        var extraction = source.IndexOf("TryExtractVertices(", classification, StringComparison.Ordinal);

        classification.Should().BeGreaterThan(0);
        extraction.Should().BeGreaterThan(classification,
            "only entities on a projection/plan-mark system should enter the fail-closed gate");
        source.Should().Contain("IsPotentialProjectionGeometry")
            .And.Contain("entity is Curve")
            .And.Contain("FeatureLine")
            .And.Contain("Proxy")
            .And.Contain("SectionFindingCodes.ProjectionGeometryUnsupported")
            .And.Contain("Severity = FindingSeverity.Error")
            .And.Contain("projection-geometry-fail-closed")
            .And.Contain("AddEvidenceForMatchedExternal(source, \"projection-unsupported\"")
            .And.NotContain("catch { return null; }");
    }

    [Fact]
    public void PreservedRuntimeProfileRulesReceiveTheNonMutatingObservedOverlay()
    {
        var source = Source();

        source.Should().Contain("WithObservedPlanMarkDefaults(")
            .And.Contain("ToConfigs(profile.Sections.Projection.PlanMarkRules)",
                "PLAN/APPLY collection must enrich preserved runtime profiles at read time")
            .And.NotContain("profile.Sections.Projection.PlanMarkRules.Add",
                "installer-preserved CL/ROW/approval state must never be rewritten by the overlay");
    }

    [Fact]
    public void FailedGeometryGetsNativeConservativeBoundsWithoutDroppingTheFinding()
    {
        var source = Source();
        source.Should().Contain("ProjectionRole = role")
            .And.Contain("SourceBoundsWcs = loopBounds ?? TryFailureBounds(entity, transform)")
            .And.Contain("partial.Loops.Where(loop => loop.Failure != null)")
            .And.Contain("{loop.Failure}\", loop.Bounds)")
            .And.Contain("var extents = entity.GeometricExtents")
            .And.Contain("min.X > max.X || min.Y > max.Y || min.Z > max.Z")
            .And.Contain("from x in new[] { extents.MinPoint.X, extents.MaxPoint.X }")
            .And.Contain("from y in new[] { extents.MinPoint.Y, extents.MaxPoint.Y }")
            .And.Contain("from z in new[] { extents.MinPoint.Z, extents.MaxPoint.Z }")
            .And.Contain("new Point3d(x, y, z).TransformBy(transform)")
            .And.Contain("SectionProjectionFailureScope.HasUsableBounds(bounds)");
        var guard = source.IndexOf("min.X > max.X", StringComparison.Ordinal);
        var transform = source.IndexOf("var points = (from x", guard, StringComparison.Ordinal);
        transform.Should().BeGreaterThan(guard, "invalid native extents must not be repaired by sorting corners");
    }

    [Fact]
    public void PlanAndApplyRouteTheSameFailureScopeAndPreserveUtilityEvidence()
    {
        var pluginSrc = typeof(SectionGeometryCollectorSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        string Read(string name) => File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections", "Services", name));
        var plan = Read("SectionPlanService.cs");
        plan.Should().Contain("SectionProjectionFailureScope.AffectsCut(f, record.Cl.WcsEndpoints)")
            .And.Contain("SectionProjectionFailureScope.ForPlan(f, failureCuts)")
            .And.Contain("SectionProjectionFailureScope.BlocksUtilityScan(f, record.Cl.WcsEndpoints)")
            .And.NotContain("plan.Findings.AddRange(projectable.Findings);");
        var apply = Read("SectionDecorationService.cs");
        var guard = apply.IndexOf("SectionProjectionFailureScope.AffectsCut(f, target.Cl.WcsEndpoints)", StringComparison.Ordinal);
        var mutate = apply.IndexOf("EnsureLayer(tr, db, AnnoLayer", StringComparison.Ordinal);
        guard.Should().BeGreaterThan(0);
        mutate.Should().BeGreaterThan(guard, "APPLY must reject a newly affecting source failure before annotation mutations");
        apply[guard..mutate].Should().Contain("throw new InvalidOperationException");
    }
}
