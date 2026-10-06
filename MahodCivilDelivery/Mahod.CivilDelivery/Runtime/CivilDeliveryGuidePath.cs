using System;
using System.IO;

namespace MahodAI.Civil3D.Plugin.Runtime;

/// <summary>
/// Finds the guide belonging to the running standalone plugin's bundle, never the working directory.
/// BCL-only, like CivilDeliveryLoadGuard: this boundary must not require Core or Autodesk types.
/// A ready path is not proof of PDF contents, version, viewer availability or native-host acceptance.
/// </summary>
internal static class CivilDeliveryGuidePath
{
    internal const string GuideFileName = "MAHOD_CIVIL_DELIVERY_GUIDE_HE.pdf";

    internal enum State { Ready, MissingGuide, OutsideBundleLayout }

    // MissingGuide retains the expected path for an actionable repair-installation message.
    // OutsideBundleLayout has no guide path: do not guess or probe another installation.
    internal sealed record Result(State Status, string? GuidePath, string ReasonCode);

    /// <param name="pluginAssemblyLocation">Assembly.Location of a type in the running Mahod.CivilDelivery plugin.</param>
    /// <param name="fileExists">Optional test seam. Production callers omit it to use File.Exists.</param>
    internal static Result Resolve(string? pluginAssemblyLocation, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(pluginAssemblyLocation))
            return Outside("plugin_location_unavailable");

        string guidePath;
        try
        {
            // Reject relative paths and URIs before normalization; GetFullPath must never consult CWD.
            if (pluginAssemblyLocation.Contains("://", StringComparison.Ordinal) ||
                !Path.IsPathFullyQualified(pluginAssemblyLocation))
                return Outside("plugin_location_not_absolute_path");

            var pluginPath = Path.GetFullPath(pluginAssemblyLocation);
            var runtimeDirectory = Path.GetDirectoryName(pluginPath);
            var contentsDirectory = Path.GetDirectoryName(runtimeDirectory);
            var bundleDirectory = Path.GetDirectoryName(contentsDirectory);
            var runtime = Path.GetFileName(runtimeDirectory);
            if (!SameName(Path.GetFileName(pluginPath), "Mahod.CivilDelivery.dll") ||
                (runtime != "2026" && runtime != "2027") ||
                !SameName(Path.GetFileName(contentsDirectory), "Contents") ||
                !SameName(Path.GetFileName(bundleDirectory), "Mahod.CivilDelivery.bundle"))
                return Outside("plugin_bundle_layout_unrecognized");

            guidePath = Path.Combine(bundleDirectory!, "Help", GuideFileName);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Outside("plugin_location_invalid");
        }

        // Exactly one probe, only inside the validated layout. There is deliberately no fallback.
        try
        {
            return (fileExists ?? File.Exists)(guidePath)
                ? new Result(State.Ready, guidePath, "guide_found")
                : new Result(State.MissingGuide, guidePath, "guide_missing_or_unavailable");
        }
        catch (Exception)
        {
            // A failed availability probe is not a reason to crash the help action or choose another guide.
            return new Result(State.MissingGuide, guidePath, "guide_probe_failed");
        }
    }

    private static bool SameName(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static Result Outside(string reason) => new(State.OutsideBundleLayout, null, reason);
}
