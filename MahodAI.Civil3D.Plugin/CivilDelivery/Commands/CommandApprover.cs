using Autodesk.AutoCAD.EditorInput;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands;

/// <summary>b24 (Codex 11:18): a command-line decision uses the confirmed session approver, or a name typed at the
/// prompt; a blank or cancelled prompt writes nothing. Never the Windows account.</summary>
internal static class CommandApprover
{
    internal static string? Require(Editor ed, string action)
    {
        if (ApproverContext.Session.Name is { } name)
        {
            ed.WriteMessage($"\nמאשר נוכחי: {name}\n");
            return name;
        }
        var result = ed.GetString(new PromptStringOptions($"\nשם המאשר עבור {action} (ריק = ביטול): ") { AllowSpaces = true });
        if (result.Status != PromptStatus.OK || !ApproverContext.Session.Confirm(result.StringResult))
        {
            ed.WriteMessage("\nלא נקבע מאשר — לא נשמר דבר.\n");
            return null;
        }
        return ApproverContext.Session.Name;
    }
}
