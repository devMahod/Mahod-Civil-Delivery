using System;
using System.IO;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Classifies one native FindElevationAtXY result without converting an API failure into a surface hole.
    /// Coordinates and elevation are in drawing units. The collector binds TOutsideException to Civil's
    /// PointNotOnEntityException; the generic seam permits host-free tests without loading Autodesk.
    /// </summary>
    internal static class CorridorGroundElevation
    {
        internal static double? Read<TOutsideException>(Func<double, double, double> findElevation, double x, double y)
            where TOutsideException : Exception
        {
            ArgumentNullException.ThrowIfNull(findElevation);
            var location = FormattableString.Invariant($"XY=({x:R}, {y:R}) [drawing units]");
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new InvalidDataException($"surface-elevation-invalid-coordinate: FindElevationAtXY at {location}");

            double z;
            try
            {
                z = findElevation(x, y);
            }
            catch (TOutsideException)
            {
                // This typed native condition alone means no surface elevation at the requested point.
                return null;
            }
            catch (Exception ex)
            {
                // The collector stores ex.Message on the failed station. Embed the original exception text
                // (including its type and inner chain) as well as retaining the actual InnerException.
                throw new InvalidDataException(
                    $"surface-elevation-read-failed: FindElevationAtXY at {location}; {ex}", ex);
            }

            if (!double.IsFinite(z))
                throw new InvalidDataException(FormattableString.Invariant(
                    $"surface-elevation-nonfinite: FindElevationAtXY at {location} returned Z={z:R} [drawing units]"));
            return z;
        }
    }
}
