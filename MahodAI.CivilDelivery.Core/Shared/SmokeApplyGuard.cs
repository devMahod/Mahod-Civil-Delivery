using System;
using System.IO;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// The shipped smoke command performs a real batch APPLY on the open drawing. It
    /// is non-interactive by design, so the only gate is WHERE the drawing lives: the
    /// documented writable fixture folder, or an explicit operator override that names
    /// the exact file. Anything else — an unsaved drawing, a project drawing, a path
    /// that merely contains the folder name — is refused before any transaction opens.
    /// </summary>
    public static class SmokeApplyGuard
    {
        public const string OverrideVariable = "MHD_SMOKE_APPLY_OK";

        /// <summary>Null when the smoke APPLY may proceed; otherwise the reason it must not.</summary>
        public static string? Refusal(string? drawingPath, string? fixturesRoot, string? overrideValue)
        {
            if (string.IsNullOrWhiteSpace(drawingPath))
                return "the drawing is not a saved file; smoke APPLY runs only on a saved fixture copy.";

            string fullDrawing;
            try { fullDrawing = Path.GetFullPath(drawingPath.Trim()); }
            catch (Exception ex)
            {
                return $"the drawing path cannot be resolved ({ex.GetType().Name}); smoke APPLY refused.";
            }

            var fileName = Path.GetFileName(fullDrawing);
            if (!string.IsNullOrWhiteSpace(overrideValue) &&
                string.Equals(overrideValue.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!string.IsNullOrWhiteSpace(fixturesRoot))
            {
                string fullRoot;
                try { fullRoot = Path.GetFullPath(fixturesRoot.Trim()); }
                catch (Exception ex)
                {
                    return $"the fixture folder cannot be resolved ({ex.GetType().Name}); smoke APPLY refused.";
                }
                var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? fullRoot
                    : fullRoot + Path.DirectorySeparatorChar;
                if (fullDrawing.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            return $"'{fileName}' is not inside the writable fixture folder" +
                   (string.IsNullOrWhiteSpace(fixturesRoot) ? string.Empty : $" '{fixturesRoot}'") +
                   $"; run make-fixture.ps1 and open the copy, or set {OverrideVariable}={fileName} deliberately.";
        }
    }
}
