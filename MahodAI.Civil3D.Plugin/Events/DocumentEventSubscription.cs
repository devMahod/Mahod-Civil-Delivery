using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Events
{
    /// <summary>
    /// Manages event subscriptions for a single AutoCAD document.
    /// Filters and categorizes database events for Civil 3D objects.
    /// </summary>
    public class DocumentEventSubscription : IDisposable
    {
        private readonly Document _document;
        private readonly Database _database;
        private readonly Action<ChangeEvent> _onChangeDetected;

        private bool _disposed;
        private bool _subscribed;

        // Track object states for change detection
        private readonly Dictionary<long, ObjectState> _objectStates = new();

        /// <summary>
        /// Creates event subscriptions for a document.
        /// </summary>
        public DocumentEventSubscription(Document document, Action<ChangeEvent> onChangeDetected)
        {
            _document = document;
            _database = document.Database;
            _onChangeDetected = onChangeDetected;
        }

        /// <summary>
        /// Subscribes to database events.
        /// </summary>
        public void Subscribe()
        {
            if (_subscribed || _disposed) return;

            _database.ObjectModified += Database_ObjectModified;
            _database.ObjectAppended += Database_ObjectAppended;
            _database.ObjectErased += Database_ObjectErased;

            _subscribed = true;
        }

        /// <summary>
        /// Unsubscribes from database events.
        /// </summary>
        public void Unsubscribe()
        {
            if (!_subscribed || _disposed) return;

            _database.ObjectModified -= Database_ObjectModified;
            _database.ObjectAppended -= Database_ObjectAppended;
            _database.ObjectErased -= Database_ObjectErased;

            _subscribed = false;
        }

        private void Database_ObjectAppended(object sender, ObjectEventArgs e)
        {
            try
            {
                ProcessObjectChange(e.DBObject, ChangeType.Added);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ObjectAppended error: {ex.Message}");
            }
        }

        private void Database_ObjectModified(object sender, ObjectEventArgs e)
        {
            try
            {
                ProcessObjectChange(e.DBObject, ChangeType.Modified);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ObjectModified error: {ex.Message}");
            }
        }

        private void Database_ObjectErased(object sender, ObjectErasedEventArgs e)
        {
            try
            {
                ProcessObjectChange(e.DBObject, ChangeType.Deleted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ObjectErased error: {ex.Message}");
            }
        }

        private void ProcessObjectChange(DBObject obj, ChangeType changeType)
        {
            if (obj == null || obj.ObjectId.IsNull) return;

            // Get object category
            var category = CategorizeObject(obj);

            // Skip non-Civil 3D objects and internal AutoCAD objects
            if (category == ObjectCategories.Other) return;

            var change = new ChangeEvent
            {
                ObjectId = obj.ObjectId.ToString(),
                Handle = obj.Handle.ToString(),
                ObjectType = obj.GetType().Name,
                Category = category,
                ChangeType = changeType
            };

            // Get object name if available
            change.ObjectName = GetObjectName(obj);

            // Get parent reference for child objects
            change.ParentObjectId = GetParentObjectId(obj);

            // Track for station range detection on modifications
            if (changeType == ChangeType.Modified)
            {
                change.AffectedStations = DetectAffectedStations(obj);
            }

            _onChangeDetected(change);
        }

        private string CategorizeObject(DBObject obj)
        {
            var typeName = obj.GetType().Name;

            // Check namespace for Civil 3D objects
            var ns = obj.GetType().Namespace ?? string.Empty;

            if (!ns.Contains("Civil") && !ns.Contains("Autodesk.Civil"))
            {
                // Check for known Civil 3D type names
                if (!IsCivil3DTypeName(typeName))
                    return ObjectCategories.Other;
            }

            return typeName switch
            {
                "Alignment" => ObjectCategories.Alignment,
                "Profile" => ObjectCategories.Profile,
                "TinSurface" or "GridSurface" or "TinVolumeSurface" or "GridVolumeSurface" => ObjectCategories.Surface,
                "Corridor" => ObjectCategories.Corridor,
                "Network" => ObjectCategories.PipeNetwork,
                "Pipe" => ObjectCategories.Pipe,
                "Structure" => ObjectCategories.Structure,
                "Assembly" => ObjectCategories.Assembly,
                "PointGroup" or "CogoPoint" => ObjectCategories.PointGroup,
                "FeatureLine" => ObjectCategories.FeatureLine,
                "LayerTableRecord" => ObjectCategories.Layer,
                _ when typeName.Contains("Alignment") => ObjectCategories.Alignment,
                _ when typeName.Contains("Profile") => ObjectCategories.Profile,
                _ when typeName.Contains("Surface") => ObjectCategories.Surface,
                _ when typeName.Contains("Corridor") => ObjectCategories.Corridor,
                _ when typeName.Contains("Pipe") => ObjectCategories.Pipe,
                _ when typeName.Contains("Structure") => ObjectCategories.Structure,
                _ => ObjectCategories.Other
            };
        }

        private bool IsCivil3DTypeName(string typeName)
        {
            return typeName switch
            {
                "Alignment" or "Profile" or "TinSurface" or "GridSurface" or
                "Corridor" or "Network" or "Pipe" or "Structure" or
                "Assembly" or "PointGroup" or "FeatureLine" => true,
                _ => false
            };
        }

        private string? GetObjectName(DBObject obj)
        {
            try
            {
                var nameProp = obj.GetType().GetProperty("Name");
                return nameProp?.GetValue(obj) as string;
            }
            catch
            {
                return null;
            }
        }

        private string? GetParentObjectId(DBObject obj)
        {
            try
            {
                // For profiles, get the alignment ID
                if (obj.GetType().Name == "Profile")
                {
                    var alignmentIdProp = obj.GetType().GetProperty("AlignmentId");
                    if (alignmentIdProp != null)
                    {
                        var alignmentId = alignmentIdProp.GetValue(obj) as ObjectId?;
                        if (alignmentId.HasValue && !alignmentId.Value.IsNull)
                            return alignmentId.Value.ToString();
                    }
                }

                // For pipes/structures, get the network ID
                if (obj.GetType().Name == "Pipe" || obj.GetType().Name == "Structure")
                {
                    var networkIdProp = obj.GetType().GetProperty("NetworkId");
                    if (networkIdProp != null)
                    {
                        var networkId = networkIdProp.GetValue(obj) as ObjectId?;
                        if (networkId.HasValue && !networkId.Value.IsNull)
                            return networkId.Value.ToString();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private StationRange? DetectAffectedStations(DBObject obj)
        {
            try
            {
                // For alignments and profiles, check for station range properties
                var startStationProp = obj.GetType().GetProperty("StartingStation");
                var endStationProp = obj.GetType().GetProperty("EndingStation");

                if (startStationProp != null && endStationProp != null)
                {
                    var start = startStationProp.GetValue(obj) as double?;
                    var end = endStationProp.GetValue(obj) as double?;

                    if (start.HasValue && end.HasValue)
                    {
                        return new StationRange(start.Value, end.Value);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Unsubscribe();
            _objectStates.Clear();
        }
    }

    /// <summary>
    /// Tracks object state for change detection.
    /// </summary>
    internal class ObjectState
    {
        public string ObjectId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public DateTime LastModified { get; set; }
        public Dictionary<string, object?> Properties { get; set; } = new();
    }
}
