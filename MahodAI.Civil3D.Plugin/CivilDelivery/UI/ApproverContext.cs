using System;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// b24 (Codex 11:18): the one "current approver" of this Civil session. It is set only from a name somebody typed and
/// confirmed — never from the Windows account, a historical approver in the profile, or Claude/Codex — and the palette
/// status strip shows it with a way to change it. Decision dialogs start from it (or empty); an action without a dialog
/// of its own uses it, or asks for it once. Confirming a name approves no decision, and a name typed in one dialog
/// applies to that decision only.
/// </summary>
internal sealed class ApproverContext
{
    internal static ApproverContext Session { get; } = new();

    private string? _name;
    internal string? Name => _name;
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

    /// <summary>The current approver; when none is set, asks once (action, current) → typed name or null for cancel.</summary>
    internal string? Require(string action, Func<string, string?, string?> ask) =>
        _name ?? (Confirm(ask(action, _name)) ? _name : null);
}
