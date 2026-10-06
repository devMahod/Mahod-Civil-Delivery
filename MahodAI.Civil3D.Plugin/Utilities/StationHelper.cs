using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Utilities;

/// <summary>
/// Station equation conversion helpers ported from MahodCivilNet.
/// Handles raw station ↔ published station conversions for alignments with station equations.
/// </summary>
public static class StationHelper
{
    /// <summary>
    /// Convert raw station (internal Civil 3D distance along alignment) to published station
    /// (what the user sees, accounting for station equations).
    /// </summary>
    public static double RawToStation(Alignment alignment, double rawStation)
    {
        double result = rawStation;
        StationEquation? nearest = null;
        double minDist = double.MaxValue;

        try
        {
            foreach (StationEquation eq in alignment.StationEquations)
            {
                if (eq.RawStationBack <= rawStation)
                {
                    double dist = rawStation - eq.RawStationBack;
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = eq;
                    }
                }
            }
        }
        catch
        {
            // StationEquations may not be accessible - return raw station
            return rawStation;
        }

        if (nearest != null)
        {
            result = nearest.StationAhead + minDist;
        }
        return result;
    }

    /// <summary>
    /// Convert published station (user-visible) back to raw station (internal Civil 3D distance).
    /// </summary>
    public static double StationToRaw(Alignment alignment, double publishedStation)
    {
        double result = publishedStation;
        StationEquation? nearest = null;
        double minDist = double.MaxValue;

        try
        {
            foreach (StationEquation eq in alignment.StationEquations)
            {
                if (eq.StationAhead <= publishedStation)
                {
                    double dist = publishedStation - eq.StationAhead;
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = eq;
                    }
                }
            }
        }
        catch
        {
            return publishedStation;
        }

        if (nearest != null)
        {
            result = nearest.RawStationBack + minDist;
        }
        return result;
    }

    /// <summary>
    /// Check if an alignment has station equations (station numbering resets/changes).
    /// </summary>
    public static bool HasStationEquations(Alignment alignment)
    {
        try
        {
            return alignment.StationEquations.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get formatted station string with equations (e.g., "1+234.56").
    /// </summary>
    public static string FormatStation(Alignment alignment, double rawStation)
    {
        try
        {
            return alignment.GetStationStringWithEquations(rawStation);
        }
        catch
        {
            return rawStation.ToString("F2");
        }
    }
}
