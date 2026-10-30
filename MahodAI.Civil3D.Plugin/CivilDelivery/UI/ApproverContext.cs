using System;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Legacy decision APIs accept an audit actor. Normal UI choices use an explicitly unnamed marker;
/// a person is never inferred from Windows, an assistant, or historical profile records.
/// </summary>
internal sealed class ApproverContext
{
    internal static ApproverContext Session { get; } = new();

    private string? _name;
    internal string? Name => _name ?? NamelessDecisionField.Actor;
    internal event Action? Changed;

    /// <summary>One line with single spaces (it is written into the profile and its comment header); null when blank.</summary>
    internal static string? Normalize(string? typed)
    {
        if (typed == null) return null;
        var name = string.Join(" ", typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? null : name;
    }

    /// <summary>Sets the current approver from a typed and confirmed name; a blank name changes nothing.</summary>
    internal bool Confirm(string? typed)
    {
        var name = Normalize(typed);
        if (name == null) return false;
        if (!string.Equals(name, _name, StringComparison.Ordinal))
        {
            _name = name;
            Changed?.Invoke();
        }
        return true;
    }

    /// <summary>Identity entry is no longer a prerequisite for an explicit user choice.</summary>
    internal string? Require(string action, Func<string, string?, string?> ask) =>
        Name;
}
