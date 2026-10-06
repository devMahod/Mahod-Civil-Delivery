using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.Events;
using MahodAI.Civil3D.Plugin.Tools;

namespace MahodAI.Civil3D.Plugin.Cache
{
    /// <summary>
    /// Manages cached analysis data for drawing objects.
    /// Integrates with dependency graph for cascade invalidation.
    /// </summary>
    public class DrawingAnalysisCache : IDisposable
    {
        private readonly ToolCache _dataCache;
        private readonly DependencyGraph _dependencyGraph;
        private readonly ConcurrentDictionary<string, ObjectCacheMetadata> _objectMetadata = new();

        private bool _disposed;

        /// <summary>
        /// Event raised when cache entries are invalidated.
        /// </summary>
        public event EventHandler<CacheInvalidationEventArgs>? CacheInvalidated;

        public DrawingAnalysisCache()
        {
            _dataCache = new ToolCache();
            _dependencyGraph = new DependencyGraph();
        }

        /// <summary>
        /// Gets the underlying tool cache.
        /// </summary>
        public ToolCache DataCache => _dataCache;

        /// <summary>
        /// Gets the dependency graph.
        /// </summary>
        public DependencyGraph Dependencies => _dependencyGraph;

        /// <summary>
        /// Registers an object in the cache with its dependencies.
        /// </summary>
        public void RegisterObject(string objectId, string objectType, string? objectName, string? parentId = null)
        {
            _objectMetadata[objectId] = new ObjectCacheMetadata
            {
                ObjectId = objectId,
                ObjectType = objectType,
                ObjectName = objectName,
                RegisteredAt = DateTime.UtcNow
            };

            if (!string.IsNullOrEmpty(parentId))
            {
                _dependencyGraph.AddDependency(parentId, objectId);
            }
        }

        /// <summary>
        /// Caches data for an object.
        /// </summary>
        public void CacheData(string objectId, string toolName, object data, TimeSpan? expiration = null)
        {
            var key = $"{toolName}:{objectId}";
            _dataCache.Set(key, data, expiration);

            if (_objectMetadata.TryGetValue(objectId, out var metadata))
            {
                metadata.LastCached = DateTime.UtcNow;
                metadata.CachedToolResults.Add(toolName);
            }
        }

        /// <summary>
        /// Gets cached data for an object.
        /// </summary>
        public bool TryGetData<T>(string objectId, string toolName, out T? data)
        {
            var key = $"{toolName}:{objectId}";
            return _dataCache.TryGet(key, out data);
        }

        /// <summary>
        /// Invalidates cache for an object and its dependents.
        /// </summary>
        public void InvalidateObject(string objectId)
        {
            var invalidated = new List<string> { objectId };

            // Get all dependents (cascade)
            var dependents = _dependencyGraph.GetDependents(objectId).ToList();
            invalidated.AddRange(dependents);

            // Remove cache entries for each object
            foreach (var id in invalidated)
            {
                InvalidateObjectDirect(id);
            }

            // Raise event
            CacheInvalidated?.Invoke(this, new CacheInvalidationEventArgs
            {
                InvalidatedObjects = invalidated,
                Reason = $"Object {objectId} changed"
            });
        }

        /// <summary>
        /// Invalidates cache for a single object without cascade.
        /// </summary>
        private void InvalidateObjectDirect(string objectId)
        {
            // Remove all tool results for this object
            _dataCache.RemoveByPattern($":{objectId}");

            // Update metadata
            if (_objectMetadata.TryGetValue(objectId, out var metadata))
            {
                metadata.CachedToolResults.Clear();
                metadata.InvalidatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Handles a change event from the drawing event manager.
        /// </summary>
        public void HandleChangeEvent(ChangeEvent change)
        {
            switch (change.ChangeType)
            {
                case ChangeType.Added:
                    RegisterObject(change.ObjectId, change.Category, change.ObjectName, change.ParentObjectId);
                    break;

                case ChangeType.Modified:
                    InvalidateObject(change.ObjectId);
                    break;

                case ChangeType.Deleted:
                    RemoveObject(change.ObjectId);
                    break;
            }
        }

        /// <summary>
        /// Handles a batch of changes.
        /// </summary>
        public void HandleChangeBatch(ChangeBatch batch)
        {
            foreach (var change in batch.Changes)
            {
                HandleChangeEvent(change);
            }
        }

        /// <summary>
        /// Removes an object from the cache completely.
        /// </summary>
        public void RemoveObject(string objectId)
        {
            // Invalidate first
            InvalidateObjectDirect(objectId);

            // Remove from dependency graph
            _dependencyGraph.RemoveObject(objectId);

            // Remove metadata
            _objectMetadata.TryRemove(objectId, out _);
        }

        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public DrawingCacheStats GetStats()
        {
            return new DrawingCacheStats
            {
                ObjectCount = _objectMetadata.Count,
                CacheEntryCount = _dataCache.Count,
                DataCacheStats = _dataCache.GetStatistics(),
                DependencyStats = _dependencyGraph.GetStats()
            };
        }

        /// <summary>
        /// Clears all cache data.
        /// </summary>
        public void Clear()
        {
            _dataCache.Clear();
            _dependencyGraph.Clear();
            _objectMetadata.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }

    /// <summary>
    /// Metadata for a cached object.
    /// </summary>
    internal class ObjectCacheMetadata
    {
        public string ObjectId { get; set; } = string.Empty;
        public string ObjectType { get; set; } = string.Empty;
        public string? ObjectName { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime? LastCached { get; set; }
        public DateTime? InvalidatedAt { get; set; }
        public HashSet<string> CachedToolResults { get; set; } = new();
    }

    /// <summary>
    /// Event args for cache invalidation.
    /// </summary>
    public class CacheInvalidationEventArgs : EventArgs
    {
        public List<string> InvalidatedObjects { get; set; } = new();
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Combined cache statistics.
    /// </summary>
    public class DrawingCacheStats
    {
        public int ObjectCount { get; set; }
        public int CacheEntryCount { get; set; }
        public CacheStatistics? DataCacheStats { get; set; }
        public DependencyGraphStats? DependencyStats { get; set; }
    }
}
