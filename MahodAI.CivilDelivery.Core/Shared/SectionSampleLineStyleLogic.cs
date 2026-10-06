using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// A drawing's style enumeration order is not an engineering input. Preserve an
/// existing managed sample line unless the project explicitly requests another
/// style. This decision does not create or modify any shared style resource.
/// </summary>
public static class SectionSampleLineStyleLogic
{
    public enum OwnedState { Absent, Readable, Unreadable, Conflict }
    public enum Origin { ExplicitProject, ExistingOwned, NewDrawingDefault, Unresolved }

    public sealed record OwnedStyle(OwnedState State, string? Name = null, string? Error = null);
    public sealed record Decision(string? Name, Origin Source, string? Error = null)
    {
        public bool IsResolved => Source != Origin.Unresolved && !string.IsNullOrWhiteSpace(Name);
    }

    public static Decision Resolve(
        string? configured, IReadOnlyList<string> available, OwnedStyle owned)
    {
        if (available == null) throw new ArgumentNullException(nameof(available));
        if (owned == null) throw new ArgumentNullException(nameof(owned));
        if (owned.State is OwnedState.Unreadable or OwnedState.Conflict)
            return Failed(owned.Error ?? "owned-sample-line-style-unreadable-or-ambiguous");
        if (available.Any(string.IsNullOrWhiteSpace) ||
            available.Distinct(StringComparer.OrdinalIgnoreCase).Count() != available.Count)
            return Failed("sample-line-style-collection-incomplete-or-ambiguous");

        if (!string.IsNullOrWhiteSpace(configured))
            return available.Any(name => Matches(configured, name))
                ? new Decision(configured, Origin.ExplicitProject)
                : Failed("configured-sample-line-style-missing");

        if (owned.State == OwnedState.Readable)
            return available.Any(name => Matches(owned.Name, name))
                ? new Decision(owned.Name, Origin.ExistingOwned)
                : Failed("owned-sample-line-style-missing");

        if (owned.State != OwnedState.Absent)
            return Failed("owned-sample-line-style-state-invalid");
        if (available.Count == 0)
            return Failed("no-sample-line-style-available");

        // New managed geometry only. This is a visible provisional choice, not
        // an approved office standard, and APPLY must assign this exact style.
        return new Decision(available.OrderBy(name => name, StringComparer.Ordinal).First(),
            Origin.NewDrawingDefault);
    }

    public static bool Matches(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual) &&
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static Decision Failed(string error) => new(null, Origin.Unresolved, error);
}
