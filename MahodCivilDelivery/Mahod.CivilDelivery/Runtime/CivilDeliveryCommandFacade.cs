using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryCommandFacade))]

namespace MahodAI.Civil3D.Plugin.Runtime;

/// <summary>
/// The separate plugin's only registered command class. It holds no Civil Delivery field or signature, so a Core that
/// fails the pairing guard produces one clear message instead of a load exception inside a command; each body runs
/// only after the guard, through a NoInlining call.
/// </summary>
public sealed class CivilDeliveryCommandFacade
{
    private static bool Ready()
    {
        if (CivilDeliveryLoadGuard.EnsureReady()) return true;
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + CivilDeliveryLoadGuard.UserMessage + "\n");
        return false;
    }

    [CommandMethod(CivilDeliveryCommandNames.Panel, CommandFlags.Modal)]
    public void Delivery() { if (Ready()) DeliveryBody(); }
    [CommandMethod(CivilDeliveryCommandNames.AfterSave, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void DeliveryAfterSave() { if (Ready()) DeliveryAfterSaveBody(); }
    [CommandMethod(CivilDeliveryCommandNames.Estimate, CommandFlags.Modal)]
    public void Estimate() { if (Ready()) EstimateBody(); }
    [CommandMethod(CivilDeliveryCommandNames.EstimateScanAfterSave, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void EstimateAfterSave() { if (Ready()) EstimateAfterSaveBody(); }
    [CommandMethod(CivilDeliveryCommandNames.SmokeEstimate, CommandFlags.Modal)]
    public void SmokeEstimate() { if (Ready()) SmokeEstimateBody(); }
    [CommandMethod(CivilDeliveryCommandNames.Sections, CommandFlags.Modal)]
    public void Sections() { if (Ready()) SectionsBody(); }
    [CommandMethod(CivilDeliveryCommandNames.Setup, CommandFlags.Modal)]
    public void Setup() { if (Ready()) SetupBody(); }
    [CommandMethod(CivilDeliveryCommandNames.SmokeDiscover, CommandFlags.Modal)]
    public void SmokeDiscover() { if (Ready()) SmokeDiscoverBody(); }
    [CommandMethod(CivilDeliveryCommandNames.SmokeSections, CommandFlags.Modal)]
    public void SmokeSections() { if (Ready()) SmokeSectionsBody(); }

    /// <summary>Identity of what is loaded: this plugin, its Core and the pairing verdict. Never needs Core.</summary>
    [CommandMethod("MCD_CHECK", CommandFlags.Modal)]
    public void Check()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        CivilDeliveryLoadGuard.EnsureReady();
        ed.WriteMessage("\n" + MahodCivilDeliveryApplication.Describe() + "\n" + CivilDeliveryLoadGuard.DiagnosticSummary + "\n");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DeliveryBody() => new MhdCivilDeliveryCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DeliveryAfterSaveBody() => new MhdCivilDeliveryCommand().ResumeAfterSave();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EstimateBody() => new MhdEstimateCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EstimateAfterSaveBody() => new MhdEstimateScanAfterSaveCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SmokeEstimateBody() => new MhdSmokeEstimateCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SectionsBody() => new MhdSectionsCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetupBody() => new MhdSetupCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SmokeDiscoverBody() => new MhdSmokeDiscoverCommand().Run();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SmokeSectionsBody() => new MhdSmokeSectionsCommand().Run();
}
