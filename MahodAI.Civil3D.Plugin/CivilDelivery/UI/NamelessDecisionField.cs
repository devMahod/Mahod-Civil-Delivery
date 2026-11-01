using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Legacy audit records retain an explicit unnamed UI actor, never a fabricated human name.</summary>
internal static class NamelessDecisionField
{
    internal const string Actor = "user-choice:no-name";
    internal static void Attach(Window window, TextBox field)
    {
        window.Loaded += (_, _) =>
        {
            field.Text = Actor;
            field.Visibility = Visibility.Collapsed;
            foreach (var text in Descendants(window).OfType<TextBlock>())
            {
                var label = text.Text.Trim();
                if (label.Length < 100 && (label is "מאשר:" or "מאשר/ת:" or "שם הבודק/ת:" ||
                    label.StartsWith("שם המאשר") || label.StartsWith("שם מאשר"))) text.Visibility = Visibility.Collapsed;
            }
        };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
