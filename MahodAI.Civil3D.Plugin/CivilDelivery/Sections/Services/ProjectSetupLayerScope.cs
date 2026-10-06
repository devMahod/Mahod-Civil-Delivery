using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Whether the CL layers chosen in setup may be scoped to the separate CL drawing (1.4.1, Codex 11:51). Decided by the
    /// candidates' explicit source identity, never by their explanation text: a layer the host also offered is mixed and
    /// keeps the legacy host + file reading, so no host line is excluded silently.
    /// </summary>
    internal static class ProjectSetupLayerScope
    {
        internal static bool AllFromSeparateFileOnly(IReadOnlyCollection<string> chosenLayers, ProjectSetupScan scan)
        {
            ArgumentNullException.ThrowIfNull(chosenLayers);
            ArgumentNullException.ThrowIfNull(scan);
            if (scan.ExternalClHashes.Count == 0 || chosenLayers.Count == 0) return false;
            return chosenLayers.All(layer =>
            {
                var rows = scan.ClLayerCandidates
                    .Where(c => string.Equals(c.Layer, layer, StringComparison.OrdinalIgnoreCase)).ToList();
                return rows.Count > 0 && rows.All(c => c.FromSeparateFile && !c.FromHost);
            });
        }
    }
}
