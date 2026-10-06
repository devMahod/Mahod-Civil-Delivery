using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Session-level cache for tool execution results.
    /// Supports automatic expiration and LRU eviction.
    /// </summary>
    public class ToolCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly int _maxEntries;
        private readonly TimeSpan _defaultExpiration;
        private readonly object _evictionLock = new();

        /// <summary>
        /// Default maximum cache entries.
        /// </summary>
        public const int DefaultMaxEntries = 1000;

        /// <summary>
        /// Default cache entry expiration.
        /// </summary>
        public static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(30);

        public ToolCache(int maxEntries = DefaultMaxEntries, TimeSpan? defaultExpiration = null)
        {
            _maxEntries = maxEntries;
            _defaultExpiration = defaultExpiration ?? DefaultExpiration;
        }

        /// <summary>
        /// Gets a cached value if it exists and is not expired.
        /// </summary>
        public bool TryGet<T>(string key, out T? value)
        {
            if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                entry.LastAccessed = DateTime.UtcNow;
                System.Threading.Interlocked.Increment(ref entry._hitCount);

                if (entry.Value is T typedValue)
                {
                    value = typedValue;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Sets a value in the cache.
        /// </summary>
        public void Set<T>(string key, T value, TimeSpan? expiration = null)
        {
            var entry = new CacheEntry
            {
                Key = key,
                Value = value,
                CreatedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                Expiration = expiration ?? _defaultExpiration
            };

            _cache[key] = entry;

            // Check if eviction is needed
            if (_cache.Count > _maxEntries)
            {
                EvictOldEntries();
            }
        }

        /// <summary>
        /// Gets a value or creates it using the factory if not cached.
        /// </summary>
        public T GetOrCreate<T>(string key, Func<T> factory, TimeSpan? expiration = null)
        {
            if (TryGet<T>(key, out var value) && value != null)
            {
                return value;
            }

            lock (_evictionLock)
            {
                // Double-check after acquiring lock
                if (TryGet<T>(key, out value) && value != null)
                {
                    return value;
                }

                value = factory();
                Set(key, value, expiration);
                return value;
            }
        }

        /// <summary>
        /// Removes a specific entry from the cache.
        /// </summary>
        public bool Remove(string key)
        {
            return _cache.TryRemove(key, out _);
        }

        /// <summary>
        /// Removes all entries matching a pattern.
        /// </summary>
        public int RemoveByPattern(string pattern)
        {
            var keysToRemove = _cache.Keys
                .Where(k => k.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int removed = 0;
            foreach (var key in keysToRemove)
            {
                if (_cache.TryRemove(key, out _))
                    removed++;
            }

            return removed;
        }

        /// <summary>
        /// Removes all entries for a specific object ID.
        /// </summary>
        public int InvalidateObject(string objectId)
        {
            return RemoveByPattern($":{objectId}:");
        }

        /// <summary>
        /// Removes all entries for a specific tool.
        /// </summary>
        public int InvalidateTool(string toolName)
        {
            return RemoveByPattern($"{toolName}:");
        }

        /// <summary>
        /// Clears all entries from the cache.
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            var entries = _cache.Values.ToList();
            var validEntries = entries.Where(e => !e.IsExpired).ToList();

            return new CacheStatistics
            {
                TotalEntries = _cache.Count,
                ValidEntries = validEntries.Count,
                ExpiredEntries = entries.Count - validEntries.Count,
                TotalHits = entries.Sum(e => e.HitCount),
                OldestEntry = entries.Any() ? entries.Min(e => e.CreatedAt) : null,
                NewestEntry = entries.Any() ? entries.Max(e => e.CreatedAt) : null
            };
        }

        /// <summary>
        /// Evicts expired and least recently used entries.
        /// </summary>
        private void EvictOldEntries()
        {
            lock (_evictionLock)
            {
                if (_cache.Count <= _maxEntries)
                    return;

                // First, remove expired entries
                var expiredKeys = _cache
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _cache.TryRemove(key, out _);
                }

                // If still over limit, remove LRU entries
                if (_cache.Count > _maxEntries)
                {
                    var entriesToRemove = _cache
                        .OrderBy(kvp => kvp.Value.LastAccessed)
                        .Take(_cache.Count - _maxEntries + (_maxEntries / 10)) // Remove extra 10% for buffer
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in entriesToRemove)
                    {
                        _cache.TryRemove(key, out _);
                    }
                }
            }
        }

        /// <summary>
        /// Current number of entries in cache.
        /// </summary>
        public int Count => _cache.Count;

        private static readonly Dictionary<string, string[]> _invalidationMap = new()
        {
            ["modify_profile_grade"] = new[] { "get_profile_geometry", "get_profile_elevation_at_station", "sample_profile_elevations", "validate_profile" },
            ["modify_alignment_curve_radius"] = new[] { "get_alignment_geometry", "validate_alignment", "find_intersections" },
            ["modify_alignment_design_speed"] = new[] { "get_alignment_geometry", "validate_alignment", "get_drawing_summary" },
            ["modify_corridor_slope"] = new[] { "get_corridor_info", "get_corridor_cross_section" },
            ["modify_corridor_width"] = new[] { "get_corridor_info", "get_corridor_cross_section" },
            ["modify_surface_point_elevation"] = new[] { "get_surface_elevation", "sample_surface_profile" },
            ["create_corridor_surface"] = new[] { "get_corridor_info", "get_corridor_cross_section", "list_corridors" },
            ["create_surface"] = new[] { "list_surfaces", "get_surface_info", "get_drawing_summary" },
            ["create_alignment_from_polyline"] = new[] { "list_alignments", "get_drawing_summary", "list_objects" },
            ["create_offset_alignment"] = new[] { "list_alignments", "get_drawing_summary", "list_objects", "find_intersections" },
            ["optimize_profile_pvis"] = new[] { "get_profile_geometry", "get_profile_elevation_at_station", "sample_profile_elevations", "validate_profile" },
            ["create_sample_line_group"] = new[] { "get_drawing_summary" },
        };

        public void InvalidateRelated(string toolName)
        {
            if (_invalidationMap.TryGetValue(toolName, out var relatedTools))
            {
                foreach (var related in relatedTools)
                    RemoveByPattern(related + ":");
            }
        }
    }

    /// <summary>
    /// Individual cache entry.
    /// </summary>
    internal class CacheEntry
    {
        public string Key { get; set; } = string.Empty;
        public object? Value { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessed { get; set; }
        public TimeSpan Expiration { get; set; }
        internal int _hitCount;
        public int HitCount { get => _hitCount; set => _hitCount = value; }

        public bool IsExpired => DateTime.UtcNow - CreatedAt > Expiration;
    }

    /// <summary>
    /// Cache statistics.
    /// </summary>
    public class CacheStatistics
    {
        public int TotalEntries { get; set; }
        public int ValidEntries { get; set; }
        public int ExpiredEntries { get; set; }
        public long TotalHits { get; set; }
        public DateTime? OldestEntry { get; set; }
        public DateTime? NewestEntry { get; set; }
    }
}
