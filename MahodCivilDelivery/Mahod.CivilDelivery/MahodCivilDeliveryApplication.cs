using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.Runtime;
using MahodAI.Civil3D.Plugin.Utilities;

[assembly: ExtensionApplication(typeof(MahodAI.Civil3D.Plugin.MahodCivilDeliveryApplication))]

namespace MahodAI.Civil3D.Plugin;

/// <summary>
/// Start-up of the separate Mahod Civil Delivery plugin: resolve its own dependencies from its own bundle folder,
/// verify the paired Core before any Civil Delivery type is touched, and put its button on the Mahod ribbon tab.
/// </summary>
/// <remarks>
/// Runs beside MahodAI in the same Civil 3D and must never depend on it or disturb it: nothing here reads MahodAI's
/// files, and a failure blocks Civil Delivery only.
/// </remarks>
public sealed class MahodCivilDeliveryApplication : IExtensionApplication
{
    private static readonly string PluginDir =
        Path.GetDirectoryName(typeof(MahodCivilDeliveryApplication).Assembly.Location) ?? string.Empty;

    public void Initialize()
    {
        // AutoCAD does not probe a bundle's Contents folder for managed dependencies.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveOwnAssembly;

        var ready = CivilDeliveryLoadGuard.EnsureReady();
        try
        {
            if (ready) MahodLogger.Info(Describe() + "; " + CivilDeliveryLoadGuard.DiagnosticSummary);
            else MahodLogger.Warning(Describe() + "; " + CivilDeliveryLoadGuard.DiagnosticSummary);
        }
        catch { /* diagnostics never stop start-up */ }

        CivilDeliveryRibbon.Install();
    }

    public void Terminate()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveOwnAssembly;
    }

    internal static string Describe()
    {
        var plugin = typeof(MahodCivilDeliveryApplication).Assembly;
        var informational = plugin.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        return $"Mahod Civil Delivery {informational}; plugin={plugin.Location}";
    }

    /// <summary>
    /// Only names that live in this plugin's own folder; Core only through the pairing guard. Anything else (and any
    /// name another plugin already loaded) is left to the runtime, so MahodAI's copies are never replaced.
    /// </summary>
    private static Assembly? ResolveOwnAssembly(object? sender, ResolveEventArgs args)
    {
        try
        {
            if (string.IsNullOrEmpty(PluginDir)) return null;
            var name = new AssemblyName(args.Name);
            if (string.IsNullOrEmpty(name.Name)) return null;
            if (string.Equals(name.Name, CivilDeliveryLoadGuard.CoreName, StringComparison.OrdinalIgnoreCase))
                return CivilDeliveryLoadGuard.ResolveCoreRequest(name);

            var candidate = Path.Combine(PluginDir, name.Name + ".dll");
            if (!File.Exists(candidate)) return null;
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                var loadedName = loaded.GetName();
                if (string.Equals(loadedName.Name, name.Name, StringComparison.OrdinalIgnoreCase) &&
                    (name.Version == null || loadedName.Version >= name.Version))
                    return loaded;
            }
            return Assembly.LoadFrom(candidate);
        }
        catch
        {
            return null;
        }
    }
}
