using System;
using System.Collections.Generic;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.Utilities
{
    /// <summary>
    /// Gives the engineer a STEADY, free crosshair for the duration of a plugin point pick, then
    /// restores every setting exactly as it was.
    ///
    /// WHY THIS EXISTS. Picking a road's start/end on a real Israeli survey base was effectively
    /// impossible (reported from the field 2026-07-28, road 73 = ONE block with 29,510 entities):
    ///
    ///   • INTersection snapping (OSMODE bit 32) recomputes intersections for every entity under
    ///     the aperture on EVERY cursor move — AutoCAD gave up move after move with "Too many
    ///     objects selected for INTERSECT" and the prompt sat there, unpickable.
    ///   • With survey linework that dense, ANY running snap then makes the crosshair jump from
    ///     one endpoint/centre to the next as the magnet acquires them — the engineer's "it
    ///     wiggles every time, it is hard to aim". Grid snap and ortho add their own jumps and
    ///     constraints on top.
    ///
    /// So for our own prompts we turn the aiming aids OFF entirely: no running object snaps, no
    /// grid snap, no ortho. The engineer clicks exactly where the crosshair is, which is what a
    /// route endpoint wants anyway — the router snaps the picked point onto the buildable mask
    /// afterwards, so snapping to some arbitrary survey vertex buys nothing.
    ///
    /// Every variable is captured before the change and restored on dispose (including on an
    /// exception or a cancelled prompt), so the engineer's own drafting settings survive.
    /// </summary>
    public sealed class InteractivePickScope : IDisposable
    {
        /// <summary>Variables that make the crosshair snap, jump or get constrained while aiming.</summary>
        private static readonly (string Name, short Value)[] SteadyPickSettings =
        {
            ("OSMODE", 0),      // running object snaps (endpoint/centre/intersection/extension/…)
            ("SNAPMODE", 0),    // grid snap — jumps the cursor to the snap increment
            ("ORTHOMODE", 0),   // ortho — constrains the second pick to H/V from the first
        };

        private readonly List<(string Name, object Value)> _restore = new();

        public InteractivePickScope()
        {
            foreach (var (name, value) in SteadyPickSettings)
            {
                try
                {
                    object current = AcApp.GetSystemVariable(name);
                    if (Convert.ToInt32(current) == value) continue;   // already off — nothing to restore
                    AcApp.SetSystemVariable(name, value);
                    _restore.Add((name, current));
                }
                catch (Exception ex)
                {
                    // Never let a system-variable quirk block the pick itself.
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] pick scope: {name} suspend failed: {ex.Message}");
                }
            }

            if (_restore.Count > 0)
            {
                MahodLogger.Info("[pick] steady crosshair: suspended " +
                                 string.Join(", ", _restore.ConvertAll(r => $"{r.Name}={r.Value}")));
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _restore)
            {
                try { AcApp.SetSystemVariable(name, value); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] pick scope: {name} restore failed: {ex.Message}");
                }
            }
            _restore.Clear();
        }
    }
}
