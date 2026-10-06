using System;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The layer an engineer sees: an entity on layer "0" inside a block (or XREF) is drawn on the layer of the
    /// reference that holds it, recursively. Project 984 (06.10) keeps its section stations as exploded Civil tick
    /// blocks (<c>AeccTickLine</c>, one LINE on "0") on HW-ALGN-SEC-NAME; by raw entity layer they were offered as
    /// "0" and mixed with unrelated host lines on layer "0". CL discovery and the CL reader use this one rule, so the
    /// layer chosen in setup is the layer that PLAN reads. Model-space entities keep their own layer unchanged.
    /// </summary>
    internal static class ClEffectiveLayer
    {
        public const string LayerZero = "0";

        /// <param name="entityLayer">The entity's own layer.</param>
        /// <param name="inheritedLayer">The effective layer of the containing reference; null in model space.</param>
        internal static string Resolve(string? entityLayer, string? inheritedLayer)
        {
            var own = entityLayer ?? string.Empty;
            return inheritedLayer != null && string.Equals(own, LayerZero, StringComparison.OrdinalIgnoreCase)
                ? inheritedLayer
                : own;
        }
    }
}
