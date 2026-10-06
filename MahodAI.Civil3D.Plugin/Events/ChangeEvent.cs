using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.Events
{
    /// <summary>
    /// Types of changes that can occur to drawing objects.
    /// </summary>
    public enum ChangeType
    {
        Added,
        Modified,
        Deleted
    }

    /// <summary>
    /// Categories of Civil 3D objects.
    /// </summary>
    public static class ObjectCategories
    {
        public const string Alignment = "Alignment";
        public const string Profile = "Profile";
        public const string Surface = "Surface";
        public const string Corridor = "Corridor";
        public const string PipeNetwork = "PipeNetwork";
        public const string Pipe = "Pipe";
        public const string Structure = "Structure";
        public const string Assembly = "Assembly";
        public const string PointGroup = "PointGroup";
        public const string FeatureLine = "FeatureLine";
        public const string Layer = "Layer";
        public const string Other = "Other";
    }

    /// <summary>
    /// Represents a change to a single drawing object.
    /// </summary>
    public class ChangeEvent
    {
        /// <summary>
        /// Unique identifier for the changed object.
        /// </summary>
        public string ObjectId { get; set; } = string.Empty;

        /// <summary>
        /// Handle string for the object (persists across sessions).
        /// </summary>
        public string Handle { get; set; } = string.Empty;

        /// <summary>
        /// Type/class name of the object.
        /// </summary>
        public string ObjectType { get; set; } = string.Empty;

        /// <summary>
        /// Object category (Alignment, Profile, Surface, etc.).
        /// </summary>
        public string Category { get; set; } = ObjectCategories.Other;

        /// <summary>
        /// Name of the object (if available).
        /// </summary>
        public string? ObjectName { get; set; }

        /// <summary>
        /// Type of change (Added, Modified, Deleted).
        /// </summary>
        public ChangeType ChangeType { get; set; }

        /// <summary>
        /// Timestamp when the change was detected.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Affected station range (for linear objects).
        /// </summary>
        public StationRange? AffectedStations { get; set; }

        /// <summary>
        /// List of properties that changed (for Modified events).
        /// </summary>
        public List<string>? ChangedProperties { get; set; }

        /// <summary>
        /// Parent object ID (e.g., alignment for profile).
        /// </summary>
        public string? ParentObjectId { get; set; }
    }

    /// <summary>
    /// Station range for linear objects.
    /// </summary>
    public class StationRange
    {
        public double Start { get; set; }
        public double End { get; set; }

        public StationRange() { }

        public StationRange(double start, double end)
        {
            Start = start;
            End = end;
        }

        /// <summary>
        /// Checks if this range overlaps with another.
        /// </summary>
        public bool Overlaps(StationRange other)
        {
            return Start <= other.End && End >= other.Start;
        }

        /// <summary>
        /// Expands this range to include another.
        /// </summary>
        public void ExpandTo(StationRange other)
        {
            Start = Math.Min(Start, other.Start);
            End = Math.Max(End, other.End);
        }
    }

    /// <summary>
    /// Represents a batch of changes to be reported.
    /// </summary>
    public class ChangeBatch
    {
        /// <summary>
        /// Unique batch identifier.
        /// </summary>
        public string BatchId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Timestamp when batch was created.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// List of changes in this batch.
        /// </summary>
        public List<ChangeEvent> Changes { get; set; } = new();

        /// <summary>
        /// Summary counts.
        /// </summary>
        public ChangeSummary Summary => new()
        {
            Added = Changes.Count(c => c.ChangeType == ChangeType.Added),
            Modified = Changes.Count(c => c.ChangeType == ChangeType.Modified),
            Deleted = Changes.Count(c => c.ChangeType == ChangeType.Deleted)
        };

        /// <summary>
        /// Adds a change to the batch, merging if duplicate.
        /// </summary>
        public void AddChange(ChangeEvent change)
        {
            // Check if we already have a change for this object
            var existing = Changes.FirstOrDefault(c => c.ObjectId == change.ObjectId);

            if (existing != null)
            {
                // Merge changes
                if (existing.ChangeType == ChangeType.Added && change.ChangeType == ChangeType.Deleted)
                {
                    // Added then deleted = no net change
                    Changes.Remove(existing);
                }
                else if (existing.ChangeType == ChangeType.Added && change.ChangeType == ChangeType.Modified)
                {
                    // Added then modified = still just added
                    // Update name/properties if available
                    if (change.ObjectName != null) existing.ObjectName = change.ObjectName;
                }
                else if (existing.ChangeType == ChangeType.Modified && change.ChangeType == ChangeType.Deleted)
                {
                    // Modified then deleted = deleted
                    existing.ChangeType = ChangeType.Deleted;
                }
                else if (existing.ChangeType == ChangeType.Modified && change.ChangeType == ChangeType.Modified)
                {
                    // Multiple modifications = combine affected ranges
                    if (existing.AffectedStations != null && change.AffectedStations != null)
                    {
                        existing.AffectedStations.ExpandTo(change.AffectedStations);
                    }
                    // Combine changed properties
                    if (change.ChangedProperties != null)
                    {
                        existing.ChangedProperties ??= new List<string>();
                        foreach (var prop in change.ChangedProperties)
                        {
                            if (!existing.ChangedProperties.Contains(prop))
                                existing.ChangedProperties.Add(prop);
                        }
                    }
                }
            }
            else
            {
                Changes.Add(change);
            }
        }

        /// <summary>
        /// Checks if batch has any changes.
        /// </summary>
        public bool HasChanges => Changes.Count > 0;
    }

    /// <summary>
    /// Summary of changes in a batch.
    /// </summary>
    public class ChangeSummary
    {
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public int Total => Added + Modified + Deleted;
    }
}
