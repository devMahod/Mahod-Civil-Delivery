using System.Windows;
namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;
public partial class CivilDeliveryControl
{
#if MAHOD_CD_STANDALONE
    private void OnInfrastructureBoq(object sender, RoutedEventArgs e) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute("MCD_INFRASTRUCTURE ", true, false, false);
#else
    private void OnInfrastructureBoq(object sender, RoutedEventArgs e) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute("MHD_INFRASTRUCTURE ", true, false, false);
#endif
}
