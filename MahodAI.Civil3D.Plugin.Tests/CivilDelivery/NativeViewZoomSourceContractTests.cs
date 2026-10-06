using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class NativeViewZoomSourceContractTests
{
    private static string Read(params string[] parts)
    {
        var root = typeof(NativeViewZoomSourceContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    [Fact]
    public void NativeAdapterUsesAutodeskInverseTransformAndReadsBackWithoutCameraOrViewportReset()
    {
        var source = Read("CivilDelivery", "Estimate", "NativeViewZoomService.cs");
        source.Should().Contain("Matrix3d.Displacement(view.Target - Point3d.Origin)")
            .And.Contain("Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)")
            .And.Contain("Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse()")
            .And.Contain("Point3d.Origin.TransformBy(worldToEye)")
            .And.Contain("Vector3d.XAxis.TransformBy(worldToEye)")
            .And.Contain("ViewZoomPlan.TryCreate(")
            .And.Contain("view.Width / view.Height")
            .And.Contain("ed.UpdateScreen()")
            .And.Contain("using var actual = ed.GetCurrentView()")
            .And.Contain("Close(actual.CenterPoint.X, fit.CenterX)")
            .And.Contain("viewport == Convert.ToInt32(AcadApp.GetSystemVariable(\"CVPORT\"))")
            .And.NotContain("SendStringToExecute")
            .And.NotContain("view.Target =")
            .And.NotContain("view.ViewDirection =")
            .And.NotContain("view.ViewTwist =")
            .And.NotContain("SetSystemVariable");
    }

    [Fact]
    public void SectionShowOnlyReportsShownAfterSuccessfulZoom()
    {
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var show = ui[ui.IndexOf("private void OnShow(", StringComparison.Ordinal)..
            ui.IndexOf("private bool ZoomToClLines(", StringComparison.Ordinal)];
        var check = show.IndexOf("if (!QuantityLocatorService.ZoomTo(doc, padded))", StringComparison.Ordinal);
        var success = show.IndexOf("SetStatus(\"מוצג חתך \"", StringComparison.Ordinal);
        check.Should().BeGreaterThan(-1).And.BeLessThan(success);
        show[check..success].Should().Contain("מצב האימות לא השתנה")
            .And.Contain("return;");
        Read("CivilDelivery", "Estimate", "QuantityLocatorService.cs")
            .Should().Contain("NativeViewZoomService.TryZoom(doc, ext, margin: 1.25)")
            .And.NotContain("SendStringToExecute");
    }

    [Fact]
    public void ModelNavigationGuardPrecedesNativeViewAccessAndWrite()
    {
        var source = Read("CivilDelivery", "Estimate", "NativeViewZoomService.cs");
        var guard = source.IndexOf("if (!ViewZoomPlan.AllowsModelGeometry(tileMode, viewport)) return false;", StringComparison.Ordinal);
        guard.Should().BeGreaterThan(source.IndexOf("var tileMode = doc.Database.TileMode;", StringComparison.Ordinal))
            .And.BeLessThan(source.IndexOf("ed.GetCurrentView()", StringComparison.Ordinal))
            .And.BeLessThan(source.IndexOf("ed.SetCurrentView(view)", StringComparison.Ordinal));
        source.Should().Contain("tileMode == doc.Database.TileMode")
            .And.Contain("Model")
            .And.Contain("Layout");
    }

    [Fact]
    public void ClNavigationFailureDoesNotClaimThatKnownGeometryIsMissing()
    {
        var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
        var start = ui.IndexOf("// Nothing created yet: show where the section WILL be", StringComparison.Ordinal);
        var end = ui.IndexOf("// Zoom by the view's real extents", start, StringComparison.Ordinal);
        var cl = ui[start..end];
        var boundsGuard = cl.IndexOf("if (!ViewZoomPlan.HasFinitePlanBounds(row.Record.Cl.WcsEndpoints))", StringComparison.Ordinal);
        var zoom = cl.IndexOf("if (ZoomToClLines(new[] { row.Record }, 40.0))", StringComparison.Ordinal);
        boundsGuard.Should().BeGreaterThan(-1).And.BeLessThan(zoom);
        cl[boundsGuard..zoom].Should().Contain("גבולות קו ה-CL חסרים או אינם תקינים")
            .And.Contain("return;");
        cl[zoom..].Should().Contain("קו ה-CL קיים, אך לא ניתן להתמקד בו")
            .And.Contain("NativeViewZoomService.RecoveryGuidance")
            .And.NotContain("אין לה גיאומטריית CL")
            .And.NotContain("גבולות קו ה-CL חסרים");
    }
}
