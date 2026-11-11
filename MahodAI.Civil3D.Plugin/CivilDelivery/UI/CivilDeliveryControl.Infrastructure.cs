using System.Windows;
namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;
public partial class CivilDeliveryControl
{
    private BoqTabLauncher? _boqTabLauncher, _boqAreaTabLauncher;
    private void InitializeBoqLauncher()
    {
        _boqTabLauncher ??= new BoqTabLauncher(Tabs, BoqLauncherTab, ProjectTab,
            () => UiGuard.Run("פתיחת כתב כמויות", () => OnInfrastructureBoq(this, new RoutedEventArgs())));
        // The marked area from the palette too, not only from the command line (owner 2026-11-11).
        _boqAreaTabLauncher ??= new BoqTabLauncher(Tabs, BoqAreaLauncherTab, ProjectTab,
            () => UiGuard.Run("כתב כמויות לאזור", () => RunCommand(BoqAreaCommand)));
    }
    private static void RunCommand(string command) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute(command + " ", true, false, false);
#if MAHOD_CD_STANDALONE
    private const string BoqAreaCommand = "MCD_BOQ_AREA";
    private void OnInfrastructureBoq(object sender, RoutedEventArgs e) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute("MCD_BOQ ", true, false, false);
#else
    private const string BoqAreaCommand = "MHD_BOQ_AREA";
    private void OnInfrastructureBoq(object sender, RoutedEventArgs e) =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?
            .SendStringToExecute("MHD_BOQ ", true, false, false);
#endif
}
