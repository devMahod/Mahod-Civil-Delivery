using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Services.State
{
    /// <summary>
    /// Manages drawing state for tracking changes between analysis sessions.
    /// Stores current and previous snapshots to enable delta computation.
    /// </summary>
    public class DrawingStateStore
    {
        private readonly object _lock = new();
        private DrawingDataModel? _currentState;
        private DrawingDataModel? _previousState;
        private readonly List<DrawingDelta> _history = new();
        private const int MaxHistoryCount = 10;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #region Properties

        /// <summary>
        /// Current drawing state (most recent extraction)
        /// </summary>
        public DrawingDataModel? CurrentState => _currentState;

        /// <summary>
        /// Previous drawing state (for delta comparison)
        /// </summary>
        public DrawingDataModel? PreviousState => _previousState;

        /// <summary>
        /// Whether any state has been captured
        /// </summary>
        public bool HasState => _currentState != null;

        /// <summary>
        /// Whether delta computation is possible (requires both states)
        /// </summary>
        public bool CanComputeDelta => _currentState != null && _previousState != null;

        /// <summary>
        /// History of computed deltas
        /// </summary>
        public IReadOnlyList<DrawingDelta> History => _history.AsReadOnly();

        #endregion

        #region Public Methods

        /// <summary>
        /// Update state with new extraction result.
        /// Current state becomes previous, new state becomes current.
        /// </summary>
        public void UpdateState(DrawingDataModel newState)
        {
            if (newState == null)
                throw new ArgumentNullException(nameof(newState));

            lock (_lock)
            {
                _previousState = _currentState;
                _currentState = newState;

                // If we have both states, compute and store delta
                if (_previousState != null)
                {
                    var delta = ComputeDeltaInternal();
                    if (delta != null)
                    {
                        _history.Add(delta);

                        // Trim history if too long
                        while (_history.Count > MaxHistoryCount)
                            _history.RemoveAt(0);
                    }
                }
            }
        }

        /// <summary>
        /// Compute delta between current and previous states
        /// </summary>
        public DrawingDelta? ComputeDelta()
        {
            lock (_lock)
            {
                if (!CanComputeDelta)
                    return null;

                return ComputeDeltaInternal();
            }
        }

        /// <summary>
        /// Get current state serialized as JSON for AI consumption
        /// </summary>
        public string GetCurrentStateJson()
        {
            lock (_lock)
            {
                if (_currentState == null)
                    return "{}";

                try
                {
                    return JsonSerializer.Serialize(_currentState, JsonOptions);
                }
                catch
                {
                    return "{}";
                }
            }
        }

        /// <summary>
        /// Get delta serialized as JSON for AI consumption
        /// </summary>
        public string GetDeltaJson()
        {
            lock (_lock)
            {
                if (!CanComputeDelta)
                    return "{}";

                var delta = ComputeDeltaInternal();
                if (delta == null)
                    return "{}";

                try
                {
                    return JsonSerializer.Serialize(delta, JsonOptions);
                }
                catch
                {
                    return "{}";
                }
            }
        }

        /// <summary>
        /// Clear all state (e.g., when drawing closes)
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _currentState = null;
                _previousState = null;
                _history.Clear();
            }
        }

        /// <summary>
        /// Get recent history entries
        /// </summary>
        public List<DrawingDelta> GetHistory(int count = 5)
        {
            lock (_lock)
            {
                return _history
                    .OrderByDescending(d => d.Timestamp)
                    .Take(count)
                    .ToList();
            }
        }

        #endregion

        #region Delta Computation

        private DrawingDelta? ComputeDeltaInternal()
        {
            if (_currentState == null || _previousState == null)
                return null;

            var delta = new DrawingDelta
            {
                Timestamp = DateTime.UtcNow,
                PreviousFileName = _previousState.FileName,
                CurrentFileName = _currentState.FileName
            };

            // Compare alignment counts
            CompareAlignments(delta);

            // Compare profile data
            CompareProfiles(delta);

            // Compare geometry analysis
            CompareGeometryAnalysis(delta);

            // Compare entity counts
            CompareEntityCounts(delta);

            // Determine if there were significant changes
            delta.HasSignificantChanges = delta.Changes.Count > 0 ||
                                          delta.AddedAlignments.Count > 0 ||
                                          delta.RemovedAlignments.Count > 0 ||
                                          delta.ComplianceChanges.Count > 0;

            return delta;
        }

        private void CompareAlignments(DrawingDelta delta)
        {
            var prevNames = _previousState?.Alignments?.Select(a => a.Name).ToHashSet() ?? new HashSet<string>();
            var currNames = _currentState?.Alignments?.Select(a => a.Name).ToHashSet() ?? new HashSet<string>();

            // Find added alignments
            foreach (var name in currNames.Except(prevNames))
            {
                delta.AddedAlignments.Add(name);
                delta.Changes.Add(new ChangeItem
                {
                    ObjectType = "Alignment",
                    ObjectName = name,
                    ChangeType = "Added",
                    Description = $"New alignment '{name}' added"
                });
            }

            // Find removed alignments
            foreach (var name in prevNames.Except(currNames))
            {
                delta.RemovedAlignments.Add(name);
                delta.Changes.Add(new ChangeItem
                {
                    ObjectType = "Alignment",
                    ObjectName = name,
                    ChangeType = "Removed",
                    Description = $"Alignment '{name}' removed"
                });
            }

            // Compare existing alignments for modifications
            foreach (var name in prevNames.Intersect(currNames))
            {
                var prev = _previousState?.Alignments?.FirstOrDefault(a => a.Name == name);
                var curr = _currentState?.Alignments?.FirstOrDefault(a => a.Name == name);

                if (prev != null && curr != null)
                {
                    // Check length change
                    if (Math.Abs(prev.Length - curr.Length) > 0.01)
                    {
                        delta.ModifiedAlignments.Add(name);
                        delta.Changes.Add(new ChangeItem
                        {
                            ObjectType = "Alignment",
                            ObjectName = name,
                            ChangeType = "Modified",
                            Property = "Length",
                            OldValue = $"{prev.Length:F2}m",
                            NewValue = $"{curr.Length:F2}m",
                            Description = $"Alignment '{name}' length changed from {prev.Length:F2}m to {curr.Length:F2}m"
                        });
                    }

                    // Check min radius change
                    if (Math.Abs(prev.MinRadius - curr.MinRadius) > 0.1)
                    {
                        if (!delta.ModifiedAlignments.Contains(name))
                            delta.ModifiedAlignments.Add(name);

                        delta.Changes.Add(new ChangeItem
                        {
                            ObjectType = "Alignment",
                            ObjectName = name,
                            ChangeType = "Modified",
                            Property = "MinRadius",
                            OldValue = $"{prev.MinRadius:F1}m",
                            NewValue = $"{curr.MinRadius:F1}m",
                            Description = $"Alignment '{name}' min radius changed from {prev.MinRadius:F1}m to {curr.MinRadius:F1}m"
                        });
                    }
                }
            }
        }

        private void CompareProfiles(DrawingDelta delta)
        {
            var prevProfiles = _previousState?.Profiles ?? new List<ProfileInfo>();
            var currProfiles = _currentState?.Profiles ?? new List<ProfileInfo>();

            // Check for grade changes
            foreach (var curr in currProfiles)
            {
                var prev = prevProfiles.FirstOrDefault(p =>
                    p.AlignmentName == curr.AlignmentName && p.ProfileName == curr.ProfileName);

                if (prev != null)
                {
                    if (Math.Abs(prev.MaxGradePercent - curr.MaxGradePercent) > 0.1)
                    {
                        delta.Changes.Add(new ChangeItem
                        {
                            ObjectType = "Profile",
                            ObjectName = $"{curr.AlignmentName}/{curr.ProfileName}",
                            ChangeType = "Modified",
                            Property = "MaxGrade",
                            OldValue = $"{prev.MaxGradePercent:F2}%",
                            NewValue = $"{curr.MaxGradePercent:F2}%",
                            Description = $"Profile max grade changed from {prev.MaxGradePercent:F2}% to {curr.MaxGradePercent:F2}%"
                        });
                    }
                }
            }
        }

        private void CompareGeometryAnalysis(DrawingDelta delta)
        {
            // Compare spiral count
            var prevSpirals = _previousState?.TotalSpiralCount ?? 0;
            var currSpirals = _currentState?.TotalSpiralCount ?? 0;

            if (prevSpirals != currSpirals)
            {
                delta.Changes.Add(new ChangeItem
                {
                    ObjectType = "Spirals",
                    ChangeType = "CountChanged",
                    OldValue = prevSpirals.ToString(),
                    NewValue = currSpirals.ToString(),
                    Description = $"Spiral count changed from {prevSpirals} to {currSpirals}"
                });
            }

            // Compare curve count
            var prevCurves = _previousState?.TotalCurveCount ?? 0;
            var currCurves = _currentState?.TotalCurveCount ?? 0;

            if (prevCurves != currCurves)
            {
                delta.Changes.Add(new ChangeItem
                {
                    ObjectType = "Curves",
                    ChangeType = "CountChanged",
                    OldValue = prevCurves.ToString(),
                    NewValue = currCurves.ToString(),
                    Description = $"Curve count changed from {prevCurves} to {currCurves}"
                });
            }
        }

        private void CompareEntityCounts(DrawingDelta delta)
        {
            var prevCount = _previousState?.TotalEntityCount ?? 0;
            var currCount = _currentState?.TotalEntityCount ?? 0;

            if (Math.Abs(prevCount - currCount) > 10) // Only report significant changes
            {
                delta.Changes.Add(new ChangeItem
                {
                    ObjectType = "Drawing",
                    ChangeType = "EntityCountChanged",
                    OldValue = prevCount.ToString(),
                    NewValue = currCount.ToString(),
                    Description = $"Total entity count changed from {prevCount} to {currCount}"
                });
            }
        }

        #endregion
    }

    /// <summary>
    /// Represents changes between two drawing states
    /// </summary>
    public class DrawingDelta
    {
        public DateTime Timestamp { get; set; }
        public string PreviousFileName { get; set; } = string.Empty;
        public string CurrentFileName { get; set; } = string.Empty;
        public bool HasSignificantChanges { get; set; }

        // Alignment changes
        public List<string> AddedAlignments { get; set; } = new();
        public List<string> RemovedAlignments { get; set; } = new();
        public List<string> ModifiedAlignments { get; set; } = new();

        // Detailed changes
        public List<ChangeItem> Changes { get; set; } = new();

        // Compliance changes
        public List<ComplianceChange> ComplianceChanges { get; set; } = new();

        /// <summary>
        /// Get a summary of changes for display
        /// </summary>
        public string GetSummary()
        {
            var parts = new List<string>();

            if (AddedAlignments.Count > 0)
                parts.Add($"{AddedAlignments.Count} alignment(s) added");
            if (RemovedAlignments.Count > 0)
                parts.Add($"{RemovedAlignments.Count} alignment(s) removed");
            if (ModifiedAlignments.Count > 0)
                parts.Add($"{ModifiedAlignments.Count} alignment(s) modified");
            if (ComplianceChanges.Count > 0)
                parts.Add($"compliance status changed");

            return parts.Count > 0 ? string.Join(", ", parts) : "No significant changes detected";
        }
    }

    /// <summary>
    /// Individual change item
    /// </summary>
    public class ChangeItem
    {
        public string ObjectType { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
        public string Property { get; set; } = string.Empty;
        public string OldValue { get; set; } = string.Empty;
        public string NewValue { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Compliance status change
    /// </summary>
    public class ComplianceChange
    {
        public int PreviousCount { get; set; }
        public int CurrentCount { get; set; }
        public string ChangeDirection { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
