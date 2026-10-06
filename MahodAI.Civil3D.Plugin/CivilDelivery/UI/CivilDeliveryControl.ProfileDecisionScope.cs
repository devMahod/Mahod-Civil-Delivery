using System;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private sealed record ProfileDecisionScope(
        Document Document,
        ProjectProfile Profile,
        ProjectProfileWriter.ExpectedProfileState ExpectedState,
        ProfileDecisionIdentity Identity,
        string Operation,
        EstimateWorkflowService.ScanResult? EstimateScan = null);

    private ProfileDecisionScope CaptureProfileDecisionScope(string operation)
    {
        var document = Doc() ?? throw new InvalidOperationException("אין שרטוט פעיל.");
        var profile = _profile ?? throw new InvalidOperationException("אין פרופיל פרויקט פעיל.");
        var source = ProjectSetupService.CaptureReadySource(document, operation);
        var expectedState = CaptureExpectedProfileState();
        var identity = CaptureProfileDecisionIdentity(document, profile, source);
        ProfileDecisionIdentityGuard.RequireComplete(identity);
        return new ProfileDecisionScope(document, profile, expectedState, identity, operation);
    }

    // Estimate continuations have published, originally saved scan evidence. Reuse
    // its full freshness contract (including view-only DBMOD=16), not the stricter
    // initial-source guard. This overload must never start or authorize a scan.
    private ProfileDecisionScope CaptureProfileDecisionScope(
        string operation, EstimateWorkflowService.ScanResult scan)
    {
        var document = Doc() ?? throw new InvalidOperationException("אין שרטוט פעיל.");
        var profile = _profile ?? throw new InvalidOperationException("אין פרופיל פרויקט פעיל.");
        var identity = CaptureEstimateProfileDecisionIdentity(document, profile, scan, operation);
        // Keep the scan's original CAS; CaptureExpectedProfileState is an initial
        // source helper and deliberately requires DBMOD=0 even for view changes.
        var expectedState = scan.ProfileWriteState ?? throw new InvalidOperationException(
            "לסריקה אין ראיית מקור/יעד פרופיל מקורית — יש לסרוק מחדש.");
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);
        ProfileDecisionIdentityGuard.RequireComplete(identity);
        return new ProfileDecisionScope(document, profile, expectedState, identity, operation, scan);
    }

    /// <summary>
    /// Rechecks the original modal scope. It never reloads a replacement profile or
    /// captures a new CAS baseline to make old choices fit changed project data.
    /// </summary>
    private void RequireProfileDecisionScope(ProfileDecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ReferenceEquals(Doc(), scope.Document) || !ReferenceEquals(_profile, scope.Profile))
            throw new InvalidOperationException(ProfileDecisionIdentityGuard.ChangedMessage);
        ProfileDecisionIdentity current;
        if (scope.EstimateScan is { } scan)
            current = CaptureEstimateProfileDecisionIdentity(scope.Document, scope.Profile, scan, scope.Operation);
        else
        {
            var source = ProjectSetupService.CaptureReadySource(scope.Document, scope.Operation);
            current = CaptureProfileDecisionIdentity(scope.Document, scope.Profile, source);
        }
        ProfileDecisionIdentityGuard.RequireUnchanged(scope.Identity, current);
        ProjectProfileWriter.RequireExpectedStateUnchanged(scope.Profile, scope.ExpectedState);
    }

    private ProfileDecisionIdentity CaptureEstimateProfileDecisionIdentity(
        Document document, ProjectProfile profile, EstimateWorkflowService.ScanResult scan, string operation)
    {
        EstimateProfileDecisionScanGuard.RequireOriginalFreshScan(scan, () => _scan,
            original => EstimateWorkflowService.RequireFreshForDecision(document, original, operation));
        if (!ReferenceEquals(Doc(), document) || !ReferenceEquals(_profile, profile))
            throw new InvalidOperationException(ProfileDecisionIdentityGuard.ChangedMessage);
        // These are the original scan's identities, just verified against the live
        // drawing, external sources, original profile CAS and published scan bytes.
        return new ProfileDecisionIdentity(document, profile, _profileHash ?? "", _profileSource ?? "",
            RequireProfileWriteTarget(), EstimateTraceIdentity.EffectiveProfileHash(profile),
            scan.SourceDrawing, scan.SourceDrawingHash ?? "", scan.DatabaseRevision ?? "");
    }

    private ProfileDecisionIdentity CaptureProfileDecisionIdentity(
        Document document, ProjectProfile profile, ProjectSetupService.SourceEvidence source) => new(
            document, profile, _profileHash ?? "", _profileSource ?? "", RequireProfileWriteTarget(),
            EstimateTraceIdentity.EffectiveProfileHash(profile),
            source.DrawingPath, source.DrawingHash, source.DatabaseRevision);
}

/// <summary>Host-free identity boundary; the caller must perform the full scan freshness check.</summary>
internal static class EstimateProfileDecisionScanGuard
{
    internal static void RequireOriginalFreshScan(
        EstimateWorkflowService.ScanResult original,
        Func<EstimateWorkflowService.ScanResult?> currentScan,
        Action<EstimateWorkflowService.ScanResult> requireFresh)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(currentScan);
        ArgumentNullException.ThrowIfNull(requireFresh);
        if (!ReferenceEquals(original, currentScan()))
            throw new InvalidOperationException(ProfileDecisionIdentityGuard.ChangedMessage);
        requireFresh(original);
        if (!ReferenceEquals(original, currentScan()))
            throw new InvalidOperationException(ProfileDecisionIdentityGuard.ChangedMessage);
    }
}

/// <summary>Host-free identity comparison so replacement/in-place changes have behavioral tests.</summary>
internal sealed record ProfileDecisionIdentity(
    object Document,
    object Profile,
    string ProfileHash,
    string ProfileSource,
    string ProfileWriteTarget,
    string EffectiveProfileHash,
    string DrawingPath,
    string DrawingHash,
    string DatabaseRevision);

internal static class ProfileDecisionIdentityGuard
{
    internal const string ChangedMessage =
        "השרטוט או פרופיל הפרויקט השתנו בזמן ההכרעה — הבחירה לא נשמרה. פתח את ההכרעה מחדש על הנתונים העדכניים.";

    internal static void RequireComplete(ProfileDecisionIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Document == null || value.Profile == null ||
            string.IsNullOrWhiteSpace(value.ProfileHash) ||
            string.IsNullOrWhiteSpace(value.ProfileSource) ||
            string.IsNullOrWhiteSpace(value.ProfileWriteTarget) ||
            string.IsNullOrWhiteSpace(value.EffectiveProfileHash) ||
            string.IsNullOrWhiteSpace(value.DrawingPath) ||
            string.IsNullOrWhiteSpace(value.DrawingHash) ||
            string.IsNullOrWhiteSpace(value.DatabaseRevision))
            throw new InvalidOperationException("זהות מקור ההכרעה חסרה — יש לטעון את הפרויקט מחדש.");
    }

    internal static void RequireUnchanged(ProfileDecisionIdentity expected, ProfileDecisionIdentity current)
    {
        RequireComplete(expected);
        RequireComplete(current);
        if (!ReferenceEquals(expected.Document, current.Document) ||
            !ReferenceEquals(expected.Profile, current.Profile) ||
            !string.Equals(expected.ProfileHash, current.ProfileHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.ProfileSource, current.ProfileSource, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.ProfileWriteTarget, current.ProfileWriteTarget, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.EffectiveProfileHash, current.EffectiveProfileHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.DrawingPath, current.DrawingPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.DrawingHash, current.DrawingHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.DatabaseRevision, current.DatabaseRevision, StringComparison.Ordinal))
            throw new InvalidOperationException(ChangedMessage);
    }
}
