using System.Windows;
namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;
public partial class CivilDeliveryControl
{
    private BoqTabLauncher? _boqTabLauncher;
    private void InitializeBoqLauncher() => _boqTabLauncher ??= new BoqTabLauncher(
        Tabs, BoqLauncherTab, ProjectTab,
        () => UiGuard.Run("פתיחת כתב כמויות", () => OnInfrastructureBoq(this, new RoutedEventArgs())));
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
