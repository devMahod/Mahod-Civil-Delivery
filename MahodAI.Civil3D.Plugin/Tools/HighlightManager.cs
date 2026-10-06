using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Manages temporary object highlighting (color override) for the fix workflow.
    /// Saves original colors and restores them when highlights are cleared.
    /// </summary>
    public class HighlightManager
    {
        private readonly Dictionary<ObjectId, int> _highlightedObjects = new();
        private const int DefaultHighlightColor = 2; // Yellow

        /// <summary>
        /// Whether any objects are currently highlighted.
        /// </summary>
        public bool HasHighlights => _highlightedObjects.Count > 0;

        /// <summary>
        /// Currently highlighted ObjectIds.
        /// </summary>
        public IReadOnlyCollection<ObjectId> HighlightedObjectIds => _highlightedObjects.Keys.ToList().AsReadOnly();

        /// <summary>
        /// Highlights a set of objects by changing their color.
        /// </summary>
        /// <param name="objectIds">ObjectIds to highlight.</param>
        /// <param name="colorIndex">ACI color index (default: 2 = yellow).</param>
        public void HighlightObjects(IEnumerable<ObjectId> objectIds, int colorIndex = DefaultHighlightColor)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                var db = doc.Database;
                using var tr = db.TransactionManager.StartTransaction();

                try
                {
                    var pending = new Dictionary<ObjectId, int>();

                    foreach (var objectId in objectIds)
                    {
                        if (objectId.IsNull || objectId.IsErased) continue;
                        if (_highlightedObjects.ContainsKey(objectId)) continue;

                        var entity = tr.GetObject(objectId, OpenMode.ForWrite) as Entity;
                        if (entity == null) continue;

                        // Save original color and apply highlight
                        pending[objectId] = entity.ColorIndex;
                        entity.ColorIndex = colorIndex;
                    }

                    tr.Commit();

                    // Merge pending into tracked highlights only after successful commit
                    foreach (var kvp in pending)
                    {
                        _highlightedObjects[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] HighlightObjects error: {ex.Message}");
                    tr.Abort();
                }
            }
        }

        /// <summary>
        /// Clears all highlights, restoring original colors.
        /// </summary>
        public void ClearHighlights()
        {
            if (_highlightedObjects.Count == 0) return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                _highlightedObjects.Clear();
                return;
            }

            using (doc.LockDocument())
            {
                var db = doc.Database;
                using var tr = db.TransactionManager.StartTransaction();

                try
                {
                    foreach (var kvp in _highlightedObjects)
                    {
                        if (kvp.Key.IsNull || kvp.Key.IsErased) continue;

                        try
                        {
                            var entity = tr.GetObject(kvp.Key, OpenMode.ForWrite) as Entity;
                            if (entity != null)
                            {
                                entity.ColorIndex = kvp.Value;
                            }
                        }
                        catch
                        {
                            // Object may have been deleted
                        }
                    }

                    tr.Commit();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] ClearHighlights error: {ex.Message}");
                    tr.Abort();
                }
            }

            _highlightedObjects.Clear();
        }

        /// <summary>
        /// Clears highlight for a single object.
        /// </summary>
        public void ClearHighlight(ObjectId objectId)
        {
            if (!_highlightedObjects.TryGetValue(objectId, out var originalColor)) return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                _highlightedObjects.Remove(objectId);
                return;
            }

            using (doc.LockDocument())
            {
                var db = doc.Database;
                using var tr = db.TransactionManager.StartTransaction();

                try
                {
                    if (!objectId.IsNull && !objectId.IsErased)
                    {
                        var entity = tr.GetObject(objectId, OpenMode.ForWrite) as Entity;
                        if (entity != null)
                        {
                            entity.ColorIndex = originalColor;
                        }
                    }

                    tr.Commit();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] ClearHighlight error: {ex.Message}");
                    tr.Abort();
                }
            }

            _highlightedObjects.Remove(objectId);
        }
    }
}
