using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace MahodAI.Civil3D.Plugin.Utilities;

/// <summary>
/// Shared helpers for finding Civil 3D objects by name.
/// Checks both siteless and site-based collections (MahodCivilNet pattern).
/// </summary>
public static class CivilObjectFinder
{
    /// <summary>
    /// Find alignment by name, checking both siteless and site-based alignments.
    /// </summary>
    public static CivilAlignment? FindAlignmentByName(
        Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, string name)
    {
        // 1. Check siteless alignments (most common)
        foreach (ObjectId id in civilDoc.GetAlignmentIds())
        {
            var al = tr.GetObject(id, OpenMode.ForRead) as CivilAlignment;
            if (al != null && string.Equals(al.Name, name, StringComparison.OrdinalIgnoreCase))
                return al;
        }

        // 2. Check site-based alignments
        try
        {
            foreach (ObjectId siteId in civilDoc.GetSiteIds())
            {
                var site = tr.GetObject(siteId, OpenMode.ForRead) as Site;
                if (site == null) continue;
                foreach (ObjectId id in site.GetAlignmentIds())
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivilAlignment;
                    if (al != null && string.Equals(al.Name, name, StringComparison.OrdinalIgnoreCase))
                        return al;
                }
            }
        }
        catch { /* Sites may not be supported in all configurations */ }

        return null;
    }

    /// <summary>
    /// Find profile by name, optionally filtered by alignment.
    /// </summary>
    public static Profile? FindProfileByName(
        Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
        string profileName, string? alignmentName = null)
    {
        // Get all alignments to search
        var alignmentIds = new List<ObjectId>();
        foreach (ObjectId id in civilDoc.GetAlignmentIds())
            alignmentIds.Add(id);
        try
        {
            foreach (ObjectId siteId in civilDoc.GetSiteIds())
            {
                var site = tr.GetObject(siteId, OpenMode.ForRead) as Site;
                if (site == null) continue;
                foreach (ObjectId id in site.GetAlignmentIds())
                    alignmentIds.Add(id);
            }
        }
        catch { }

        foreach (ObjectId alId in alignmentIds)
        {
            var al = tr.GetObject(alId, OpenMode.ForRead) as CivilAlignment;
            if (al == null) continue;
            if (alignmentName != null && !string.Equals(al.Name, alignmentName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (ObjectId pfId in al.GetProfileIds())
            {
                var pf = tr.GetObject(pfId, OpenMode.ForRead) as Profile;
                if (pf != null && string.Equals(pf.Name, profileName, StringComparison.OrdinalIgnoreCase))
                    return pf;
            }
        }
        return null;
    }

    /// <summary>
    /// Get ALL alignment IDs (siteless + site-based).
    /// </summary>
    public static List<ObjectId> GetAllAlignmentIds(
        Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc)
    {
        var ids = new List<ObjectId>();
        foreach (ObjectId id in civilDoc.GetAlignmentIds())
            ids.Add(id);
        try
        {
            foreach (ObjectId siteId in civilDoc.GetSiteIds())
            {
                var site = tr.GetObject(siteId, OpenMode.ForRead) as Site;
                if (site == null) continue;
                foreach (ObjectId id in site.GetAlignmentIds())
                    ids.Add(id);
            }
        }
        catch { }
        return ids;
    }

    /// <summary>
    /// Find surface by name.
    /// </summary>
    public static CivilSurface? FindSurfaceByName(
        Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, string name)
    {
        foreach (ObjectId id in civilDoc.GetSurfaceIds())
        {
            var surface = tr.GetObject(id, OpenMode.ForRead) as CivilSurface;
            if (surface != null && string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
                return surface;
        }
        return null;
    }

    /// <summary>
    /// Find corridor by name.
    /// </summary>
    public static Corridor? FindCorridorByName(
        Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, string name)
    {
        foreach (ObjectId id in civilDoc.CorridorCollection)
        {
            var corridor = tr.GetObject(id, OpenMode.ForRead) as Corridor;
            if (corridor != null && string.Equals(corridor.Name, name, StringComparison.OrdinalIgnoreCase))
                return corridor;
        }
        return null;
    }

    /// <summary>
    /// Find pipe network by name.
    /// </summary>
    public static Network? FindNetworkByName(
        Transaction tr, Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, string name)
    {
        foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
        {
            var network = tr.GetObject(id, OpenMode.ForRead) as Network;
            if (network != null && string.Equals(network.Name, name, StringComparison.OrdinalIgnoreCase))
                return network;
        }
        return null;
    }
}
