using System.Windows;
using Grid = System.Windows.Controls.Grid;
namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;
public partial class CivilDeliveryControl
{
    private BoqPanel? _boqPanel;

    /// <summary>
    /// The "כתב כמויות" tab holds the bill of quantities itself (owner 2026-11-11: "always opened, like we had before, but all
    /// in one window"): mark a polygon or take the whole drawing, a loader with Cancel while it reads, the estimate under it.
    /// Until then the tab header only launched MHD_BOQ (BoqTabLauncher) and a separate window opened. The tab's earlier,
    /// hidden markup stays where it is (other code and the palette layout tests still name its controls).
    /// </summary>
    private void InitializeBoqLauncher()
    {
        var panel = _boqPanel = new BoqPanel();
        var earlier = BoqLauncherTab.Content as UIElement;
        BoqLauncherTab.Content = null;
        var host = new Grid();
        if (earlier != null) host.Children.Add(earlier);
        host.Children.Add(panel);
        BoqLauncherTab.Content = host;
        panel.MarkArea += () => BoqPanelRunner.Start(panel, p => BoqPanelRunner.Bill(p, area: true));
        panel.WholeDrawing += () => BoqPanelRunner.Start(panel, p => BoqPanelRunner.Bill(p, area: false));
        panel.Export += () => BoqPanelRunner.Start(panel, BoqPanelRunner.Export);
        panel.Details += table => BoqPanelRunner.Start(panel, p => BoqPanelRunner.Details(p, table));
        panel.EarthMap += () => BoqPanelRunner.Start(panel, BoqPanelRunner.ToggleEarthMap);
        panel.EarthSettings += () => BoqPanelRunner.Start(panel, BoqPanelRunner.EarthSettings);
    }

    /// <summary>Selects the BOQ tab and returns its panel (MHD_BOQ / MHD_BOQ_AREA show their result there).</summary>
    internal BoqPanel ShowBoqPanel()
    {
        Tabs.SelectedItem = BoqLauncherTab;
        return _boqPanel!;
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
