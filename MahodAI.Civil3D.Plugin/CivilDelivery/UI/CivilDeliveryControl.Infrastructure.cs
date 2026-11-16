using System.Windows;
namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;
public partial class CivilDeliveryControl
{
    /// <summary>
    /// The bill of quantities left Civil Delivery on 2026-11-16 (owner: "Bill of quantities and typical section as
    /// separate tools"): it is the Mahod BOQ tool, in its own repo and window. This tab keeps the project-profile
    /// estimate markup that other code and the palette layout tests still name; it is not shown.
    /// </summary>
    private void InitializeBoqLauncher()
    {
        BoqLauncherTab.Visibility = Visibility.Collapsed;
        if (ReferenceEquals(Tabs.SelectedItem, BoqLauncherTab)) Tabs.SelectedIndex = 0;
    }
}
