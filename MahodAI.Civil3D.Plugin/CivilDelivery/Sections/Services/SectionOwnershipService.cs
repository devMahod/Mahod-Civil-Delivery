using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Persists and reads Mahod Civil Delivery ownership metadata on Civil/AutoCAD
    /// objects using ExtensionDictionary + Xrecord (plan §7.12). Object names are
    /// never identity; the Xrecord is.
    /// </summary>
    public static class SectionOwnershipService
    {
        /// <summary>Writes (or replaces) ownership metadata on an object open for write.</summary>
        public static void Write(Transaction tr, DBObject obj, OwnershipMetadata meta)
        {
            if (obj.ExtensionDictionary.IsNull)
            {
                obj.UpgradeOpen();
                obj.CreateExtensionDictionary();
            }

            var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForWrite);

            using var buffer = new ResultBuffer();
            foreach (var pair in meta.ToPairs())
            {
                buffer.Add(new TypedValue((int)DxfCode.Text, $"{pair.Key}={pair.Value}"));
            }

            if (dict.Contains(OwnershipMetadata.DictionaryKey))
            {
                var existing = (Xrecord)tr.GetObject(
                    dict.GetAt(OwnershipMetadata.DictionaryKey), OpenMode.ForWrite);
                existing.Data = buffer;
            }
            else
            {
                var xrec = new Xrecord { Data = buffer };
                dict.SetAt(OwnershipMetadata.DictionaryKey, xrec);
                tr.AddNewlyCreatedDBObject(xrec, true);
            }
        }

        /// <summary>Reads ownership metadata; null when the object is not tool-owned.</summary>
        public static OwnershipMetadata? Read(Transaction tr, DBObject obj)
        {
            if (obj.ExtensionDictionary.IsNull) return null;
            var dict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead);
            if (!dict.Contains(OwnershipMetadata.DictionaryKey)) return null;

            if (tr.GetObject(dict.GetAt(OwnershipMetadata.DictionaryKey), OpenMode.ForRead)
                is not Xrecord xrec)
                throw new InvalidDataException(
                    "Mahod ownership dictionary entry is not an Xrecord.");
            if (xrec.Data == null)
                throw new InvalidDataException(
                    "Mahod ownership Xrecord contains no data.");

            var pairs = new List<KeyValuePair<string, string>>();
            foreach (TypedValue tv in xrec.Data)
            {
                if (tv.TypeCode != (int)DxfCode.Text || tv.Value is not string s)
                    throw new InvalidDataException(
                        "Mahod ownership Xrecord contains a non-text value.");
                var idx = s.IndexOf('=');
                if (idx <= 0)
                    throw new InvalidDataException(
                        "Mahod ownership Xrecord contains a malformed key/value pair.");
                pairs.Add(new KeyValuePair<string, string>(s[..idx], s[(idx + 1)..]));
            }

            try
            {
                return OwnershipMetadata.FromPairs(pairs) ??
                       throw new InvalidDataException(
                           "Mahod ownership dictionary carries a foreign owner marker.");
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "Mahod ownership metadata is malformed and cannot be treated as unowned.", ex);
            }
        }

        /// <summary>True only when the object carries OUR ownership record (plan: never delete look-alikes).</summary>
        public static bool IsToolOwned(Transaction tr, DBObject obj) => Read(tr, obj) != null;
    }
}
