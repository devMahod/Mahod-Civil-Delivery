using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class DrawingSaveContextSourceTests
{
    private static string Read(string file)
    {
        var root = typeof(DrawingSaveContextSourceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(x => x.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", file));
    }

    [Fact]
    public void RepeatedLoadedUsesNamedIdempotentHooksAndObservesActiveDocument()
    {
        var source = Read("CivilDeliveryControl.xaml.cs");
        var start = source.IndexOf("private void HookDocumentEvents()", StringComparison.Ordinal);
        var end = source.IndexOf("private static string? CurrentDrawing()", start, StringComparison.Ordinal);
        var hooks = source[start..end];
        foreach (var pair in new[] { ("DocumentActivated", "OnObservedDocumentActivated"),
                     ("DocumentToBeDeactivated", "OnObservedDocumentDeactivating"),
                     ("DocumentToBeDestroyed", "OnObservedDocumentDestroying") })
        {
            Assert.Contains(pair.Item1 + " -= " + pair.Item2, hooks);
            Assert.Contains(pair.Item1 + " += " + pair.Item2, hooks);
        }
        Assert.Contains("HookDrawingSaveContext();", hooks);
        Assert.DoesNotContain("+= (_,", hooks);
        Assert.Contains("Dispatcher.Invoke(new Action(OnDrawingChanged));",
            Read("CivilDeliveryControl.DrawingSaveContext.cs"));
    }

    [Fact]
    public void ChangedDrawingClearsCompleteApplyVerifyIdentityButDoesNotCancelSameDocumentContinuation()
    {
        var source = Read("CivilDeliveryControl.xaml.cs");
        var start = source.IndexOf("private void OnDrawingChanged()", StringComparison.Ordinal);
        var end = source.IndexOf("private void RefreshDashboard()", start, StringComparison.Ordinal);
        var changed = source[start..end];
        Assert.Contains("!ReferenceEquals(_pendingWorkflowSaveDocument, activeDocument)", changed);
        Assert.DoesNotContain("ResetForExplicitProjectProfileSelection(", changed);
        var section = changed[changed.IndexOf("if (sectionChanged)", StringComparison.Ordinal)..changed.IndexOf("if (estimateChanged)", StringComparison.Ordinal)];
        foreach (var field in new[] { "_plan", "_apply", "_applyPlanRunId", "_lastVerifyResult", "_verifySummary", "_sectionResultsDrawing" })
            Assert.Contains(field + " = null;", section);
        foreach (var field in new[] { "_scan", "_historicalScan", "_estimateResult", "_catalog", "_estimateResultsDrawing" })
            Assert.Contains(field + " = null;", changed);
        Assert.Contains("ReloadProfile();", changed);
        Assert.Contains("RefreshDashboard();", changed);
        Assert.Contains("RefreshDrawingLabel();", changed);
    }

    [Fact]
    public void SaveObserverUsesOnlyCheapIdentityAndNeverCommandsOrExplicitProfileReset()
    {
        var observer = Read("CivilDeliveryControl.DrawingSaveContext.cs");
        Assert.Contains("Dispatcher.BeginInvoke(new Action(() =>", observer);
        Assert.Contains("document.Database.UnmanagedObject != database", observer);
        Assert.Contains("!ReferenceEquals(document, Doc())", observer);
        Assert.Contains("generation != _drawingSaveContextGeneration", observer);
        Assert.Contains("var currentName = document.Name;", observer);
        Assert.Contains("OnDrawingChanged();", observer);
        Assert.DoesNotContain("CaptureLive(", observer);
        Assert.DoesNotContain("SendStringToExecute(", observer);
        Assert.DoesNotContain("ReloadProfile();", observer);
        Assert.DoesNotContain("RefreshDashboard();", observer);
        Assert.DoesNotContain("ResetForExplicitProjectProfileSelection(", observer);
    }
}
