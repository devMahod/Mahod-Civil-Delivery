using System.Windows;
namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;
public partial class CivilDeliveryControl
{
    private void OnBoqTabSelected(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Tabs) || Tabs.SelectedIndex != 1) return;
        // A launcher tab is always available again after the modeless host regains focus.
        Tabs.SelectedIndex = 3;
        Dispatcher.BeginInvoke(new System.Action(() => OnInfrastructureBoq(sender, e)));
        e.Handled = true;
    }
#if MAHOD_CD_STANDALONE
    private void OnInfrastructureBoq(object sender, RoutedEventArgs e) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute("MCD_BOQ ", true, false, false);
#else
    private void OnInfrastructureBoq(object sender, RoutedEventArgs e) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute("MHD_BOQ ", true, false, false);
#endif
}
