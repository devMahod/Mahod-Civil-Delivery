using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Source delegation checks only; native DWT/SaveAs lifecycle is a separate acceptance test.</summary>
public sealed class UnsavedDrawingIdentityWiringTests
{
    private static string Read(string relative) => File.ReadAllText(Path.Combine(
        typeof(UnsavedDrawingIdentityWiringTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "MahodPluginSourceDir").Value!, "CivilDelivery", relative));

    [Fact]
    public void NativeCaptureReadsNamedFlagAndUsesSharedGuardBeforeAnyHash()
    {
        var source = Read("Estimate/DrawingRevisionTracker.cs");
        source.Should().Contain("SavedDrawingPathPolicy.Capture(")
            .And.Contain("GetSystemVariable(\"DWGTITLED\")")
            .And.Contain("() => doc.Name, () => doc.Database.Filename")
            .And.Contain("if (identity.Failure != null)")
            .And.Contain("if (identity.IsSaved && failure == null &&")
            .And.Contain("identity.ReadSavedHash(ArtifactHash.Sha256OfFile)")
            .And.NotContain("Path.GetFullPath(db.Filename)")
            .And.NotContain("SetSystemVariable");
        source.IndexOf("var identity = CaptureSavedDrawingIdentity(doc)")
            .Should().BeLessThan(source.IndexOf("identity.ReadSavedHash("));
        source.Should().Contain("return new LiveSnapshot(\"\", \"\", before, null, identity.Failure);");
        source.IndexOf("return new LiveSnapshot(\"\", \"\", before, null, identity.Failure);")
            .Should().BeLessThan(source.IndexOf("GetSystemVariable(\"DBMOD\")"));
        var ready = Read("Sections/Services/ProjectSetupService.cs");
        ready.Should().Contain("var failure = source.Failure ?? EstimateSourceSnapshotPolicy.InitialFailure(")
            .And.Contain("if (failure != null || source.DbMod != 0)");
    }

    [Fact]
    public void UntitledProfileUsesSessionSeedBeforeAnyPersistedProfileResolution()
    {
        var source = Read("Sections/Services/ActiveProjectProfileService.cs");
        var method = source[source.IndexOf("public ActiveLoadResult LoadForDocument(")..
            source.IndexOf("private static ActiveLoadResult SelectionFailure(")];
        var caller = method[..method.IndexOf("internal static ActiveLoadResult LoadForCapturedDrawingIdentity(")];
        var captured = method[method.IndexOf("internal static ActiveLoadResult LoadForCapturedDrawingIdentity(")..];
        caller.Should().Contain("return LoadForCapturedDrawingIdentity(doc, drawingIdentity, workflow, explicitSelector,")
            .And.Contain("Environment.GetEnvironmentVariable(\"MHD_PROFILE_ID\")")
            .And.Contain("Environment.GetEnvironmentVariable(\"MHD_PROFILE_DIR\")");
        method.Should().Contain("DrawingRevisionTracker.CaptureSavedDrawingIdentity(doc)")
            .And.Contain("if (!drawingIdentity.IsSaved)")
            .And.Contain("SavedDrawingPathPolicy.UnsavedProfileId(documentToken)")
            .And.Contain("return pending;")
            .And.Contain("var savedPath = drawingIdentity.DrawingPath;")
            .And.NotContain("doc.Database.Filename");
        method.IndexOf("return pending;").Should().BeLessThan(method.IndexOf("var result = LoadForDrawing("));
        captured.Should().Contain("ExistingProjectProfileSelection.Resolve(documentToken, savedPath, explicitSelector)")
            .And.Contain("ExistingProjectProfileSelection.RequireProfileIdentity(sessionBinding, result.Profile?.ProfileId)")
            .And.NotContain("Environment.GetEnvironmentVariable");
        captured.IndexOf("return pending;").Should().BeLessThan(
            captured.IndexOf("ExistingProjectProfileSelection.Resolve("));
    }

    [Fact]
    public void ExistingSaveQueueCancelsBeforeResumeAndReloadsAfterSuccessfulSave_BeforeProfileWrite()
    {
        var save = Read("UI/CivilDeliveryControl.SaveGuidance.cs");
        save.Should().Contain("doc.CommandCancelled += OnWorkflowSaveCancelled;")
            .And.Contain("_pendingWorkflowSaveContinuation = null;")
            .And.Contain("_pendingWorkflowSaveToken = null;")
            .And.Contain("SavedDrawingContinuationPolicy.CanConsume(")
            .And.Contain("SavedDrawingContinuationPolicy.Decide(")
            .And.Contain("ReloadProfile();");
        var continuation = save[save.IndexOf("_pendingWorkflowSaveContinuation = () =>")..
            save.IndexOf("doc.CommandCancelled += OnWorkflowSaveCancelled;")];
        continuation.IndexOf("ReloadProfile();").Should().BeLessThan(continuation.IndexOf("continuation(this, new RoutedEventArgs());"));
        var start = Read("UI/CivilDeliveryControl.EstimateProjectStart.cs");
        start.IndexOf("RequireProfileDecisionScope(scope);").Should().BeLessThan(start.IndexOf("EstimateProjectStartService.Save("));
        var scope = Read("UI/CivilDeliveryControl.ProfileDecisionScope.cs");
        scope.Should().Contain("ProjectSetupService.CaptureReadySource(scope.Document, scope.Operation)")
            .And.Contain("ProjectProfileWriter.RequireExpectedStateUnchanged(scope.Profile, scope.ExpectedState)");
    }
}
