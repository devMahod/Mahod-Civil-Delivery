using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Shared predicate for the seven profile-modify tools. Permits Layout,
    /// Design, and FG (FinishedGround) profiles. Blocks ExistingGround /
    /// Surface / Measured — those are read-only by construction (sampled from
    /// a surface or measured data, not a design artefact).
    ///
    /// Rationale: real drawings rarely carry a true "Layout" profile; the
    /// corridor's top-of-pavement is exposed as FG, and engineers still want
    /// the fix pipeline to adjust its PVIs / grades. Blocking FG up-front
    /// makes the entire fix workflow no-op on production drawings.
    /// </summary>
    internal static class ProfileTypeGuard
    {
        public static bool IsModifiable(CivilDb.ProfileType type)
        {
            var s = type.ToString();
            return s == "Layout"
                || s == "Design"
                || s == "FG"
                || s.Contains("FinishedGround");
        }
    }
}
