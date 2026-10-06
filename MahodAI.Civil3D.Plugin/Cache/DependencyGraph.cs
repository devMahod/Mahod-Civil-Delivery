using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Cache
{
    /// <summary>
    /// Tracks object relationships for cascade invalidation.
    /// When a parent object changes, dependent objects' caches are invalidated.
    /// </summary>
    public class DependencyGraph
    {
        // Parent -> Children mapping
        private readonly Dictionary<string, HashSet<string>> _dependencies = new();

        // Child -> Parents mapping (reverse lookup)
        private readonly Dictionary<string, HashSet<string>> _reverseDependencies = new();

        private readonly object _lock = new();

        /// <summary>
        /// Registers a dependency between objects.
        /// </summary>
        /// <param name="parentId">Parent object ID (e.g., Alignment)</param>
        /// <param name="childId">Child object ID (e.g., Profile)</param>
        public void AddDependency(string parentId, string childId)
        {
            lock (_lock)
            {
                // Add forward dependency
                if (!_dependencies.TryGetValue(parentId, out var children))
                {
                    children = new HashSet<string>();
                    _dependencies[parentId] = children;
                }
                children.Add(childId);

                // Add reverse dependency
                if (!_reverseDependencies.TryGetValue(childId, out var parents))
                {
                    parents = new HashSet<string>();
                    _reverseDependencies[childId] = parents;
                }
                parents.Add(parentId);
            }
        }

        /// <summary>
        /// Removes a dependency between objects.
        /// </summary>
        public void RemoveDependency(string parentId, string childId)
        {
            lock (_lock)
            {
                if (_dependencies.TryGetValue(parentId, out var children))
                {
                    children.Remove(childId);
                    if (children.Count == 0)
                        _dependencies.Remove(parentId);
                }

                if (_reverseDependencies.TryGetValue(childId, out var parents))
                {
                    parents.Remove(parentId);
                    if (parents.Count == 0)
                        _reverseDependencies.Remove(childId);
                }
            }
        }

        /// <summary>
        /// Removes an object and all its dependencies.
        /// </summary>
        public void RemoveObject(string objectId)
        {
            lock (_lock)
            {
                // Remove as parent
                if (_dependencies.TryGetValue(objectId, out var children))
                {
                    foreach (var child in children.ToList())
                    {
                        if (_reverseDependencies.TryGetValue(child, out var childParents))
                        {
                            childParents.Remove(objectId);
                            if (childParents.Count == 0)
                                _reverseDependencies.Remove(child);
                        }
                    }
                    _dependencies.Remove(objectId);
                }

                // Remove as child
                if (_reverseDependencies.TryGetValue(objectId, out var parents))
                {
                    foreach (var parent in parents.ToList())
                    {
                        if (_dependencies.TryGetValue(parent, out var parentChildren))
                        {
                            parentChildren.Remove(objectId);
                            if (parentChildren.Count == 0)
                                _dependencies.Remove(parent);
                        }
                    }
                    _reverseDependencies.Remove(objectId);
                }
            }
        }

        /// <summary>
        /// Gets all objects that depend on the given object (cascade down).
        /// </summary>
        public IEnumerable<string> GetDependents(string objectId)
        {
            var result = new List<string>();
            lock (_lock)
            {
                var visited = new HashSet<string>();
                var queue = new Queue<string>();
                queue.Enqueue(objectId);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (visited.Contains(current)) continue;
                    visited.Add(current);

                    if (_dependencies.TryGetValue(current, out var children))
                    {
                        foreach (var child in children)
                        {
                            if (!visited.Contains(child))
                            {
                                result.Add(child);
                                queue.Enqueue(child);
                            }
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Gets direct children of an object.
        /// </summary>
        public IEnumerable<string> GetDirectDependents(string objectId)
        {
            lock (_lock)
            {
                if (_dependencies.TryGetValue(objectId, out var children))
                {
                    return children.ToList();
                }
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Gets all objects that the given object depends on (traverse up).
        /// </summary>
        public IEnumerable<string> GetDependencies(string objectId)
        {
            var result = new List<string>();
            lock (_lock)
            {
                var visited = new HashSet<string>();
                var queue = new Queue<string>();
                queue.Enqueue(objectId);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (visited.Contains(current)) continue;
                    visited.Add(current);

                    if (_reverseDependencies.TryGetValue(current, out var parents))
                    {
                        foreach (var parent in parents)
                        {
                            if (!visited.Contains(parent))
                            {
                                result.Add(parent);
                                queue.Enqueue(parent);
                            }
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Gets direct parents of an object.
        /// </summary>
        public IEnumerable<string> GetDirectDependencies(string objectId)
        {
            lock (_lock)
            {
                if (_reverseDependencies.TryGetValue(objectId, out var parents))
                {
                    return parents.ToList();
                }
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Checks if there's any dependency between two objects.
        /// </summary>
        public bool HasDependency(string parentId, string childId)
        {
            lock (_lock)
            {
                if (_dependencies.TryGetValue(parentId, out var children))
                {
                    return children.Contains(childId);
                }
                return false;
            }
        }

        /// <summary>
        /// Clears all dependencies.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _dependencies.Clear();
                _reverseDependencies.Clear();
            }
        }

        /// <summary>
        /// Gets the total number of dependency relationships.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _dependencies.Values.Sum(v => v.Count);
                }
            }
        }

        /// <summary>
        /// Gets statistics about the dependency graph.
        /// </summary>
        public DependencyGraphStats GetStats()
        {
            lock (_lock)
            {
                return new DependencyGraphStats
                {
                    TotalObjects = _dependencies.Keys.Union(_reverseDependencies.Keys).Distinct().Count(),
                    TotalRelationships = _dependencies.Values.Sum(v => v.Count),
                    ParentCount = _dependencies.Count,
                    ChildCount = _reverseDependencies.Count,
                    MaxChildren = _dependencies.Values.Any() ? _dependencies.Values.Max(v => v.Count) : 0,
                    MaxParents = _reverseDependencies.Values.Any() ? _reverseDependencies.Values.Max(v => v.Count) : 0
                };
            }
        }
    }

    /// <summary>
    /// Statistics about the dependency graph.
    /// </summary>
    public class DependencyGraphStats
    {
        public int TotalObjects { get; set; }
        public int TotalRelationships { get; set; }
        public int ParentCount { get; set; }
        public int ChildCount { get; set; }
        public int MaxChildren { get; set; }
        public int MaxParents { get; set; }
    }
}
