using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Helper to find Civil 3D objects by name.
    /// Avoids duplicating lookup logic across tools.
    /// </summary>
    public static class ObjectFinder
    {
        /// <summary>
        /// Finds an alignment by name.
        /// </summary>
        /// <returns>ObjectId of the alignment, or null if not found.</returns>
        public static ObjectId? FindAlignment(CivilDocument civilDoc, Transaction tr, string name)
        {
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                var alignment = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                if (alignment != null && alignment.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
            return null;
        }

        /// <summary>
        /// Finds a profile by name, optionally filtering by parent alignment name.
        /// </summary>
        /// <returns>ObjectId of the profile, or null if not found.</returns>
        public static ObjectId? FindProfile(CivilDocument civilDoc, Transaction tr, string profileName, string? alignmentName = null)
        {
            foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
            {
                var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                if (alignment == null) continue;

                if (!string.IsNullOrEmpty(alignmentName) &&
                    !alignment.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    var profile = tr.GetObject(profileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                    if (profile != null && profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return profileId;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Finds the parent alignment name for a given profile.
        /// </summary>
        public static string? FindAlignmentNameForProfile(CivilDocument civilDoc, Transaction tr, string profileName)
        {
            foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
            {
                var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                if (alignment == null) continue;

                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    var profile = tr.GetObject(profileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                    if (profile != null && profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return alignment.Name;
                    }
                }
            }
            return null;
        }
    }
}
