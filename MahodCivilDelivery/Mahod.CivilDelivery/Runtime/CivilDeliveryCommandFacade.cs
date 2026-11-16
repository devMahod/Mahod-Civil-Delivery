using System.Diagnostics;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.Runtime;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.Utilities;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryCommandFacade))]

namespace MahodAI.Civil3D.Plugin.Runtime;

/// <summary>
/// The separate plugin's only registered command class. It holds no Civil Delivery field or signature, so a Core that
/// fails the pairing guard produces one clear message instead of a load exception inside a command; each body runs
/// only after the guard, through a NoInlining call.
/// </summary>
/// <remarks>
/// Mahod Impact usage (1.4.2): a typed MCD_* command is the standalone product's own door, so each one runs with
/// <see cref="MahodUsage.AmbientDoor"/> = <c>standalone</c> and the section/estimate steps inside it record that door
/// (CivilDeliveryUsage). Opening the palette, setup and the typed sections flow are also recorded as actions, under
/// the names MahodAI uses for its MHD_* twins. Not recorded here: MCD_ESTIMATE (its scan records
/// civildelivery_estimate itself), the after-save continuations (they resume the palette, which records its own steps
/// under the palette door), the smoke harnesses and MCD_CHECK. The usage strings are compile-time constants and
/// MahodUsage is BCL-only, so this class still touches no Core type before the guard.
/// </remarks>
public sealed class CivilDeliveryCommandFacade
{
    private static bool Ready()
    {
        if (CivilDeliveryLoadGuard.EnsureReady()) return true;
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + CivilDeliveryLoadGuard.UserMessage + "\n");
        return false;
    }

    [CommandMethod(CivilDeliveryCommandNames.Panel, CommandFlags.Modal)]
    public void Delivery() { if (Ready()) Typed(CivilDeliveryUsage.OpenAction, typedFlow: false, DeliveryBody); }
    [CommandMethod(CivilDeliveryCommandNames.AfterSave, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void DeliveryAfterSave() { if (Ready()) DeliveryAfterSaveBody(); }
    [CommandMethod(CivilDeliveryCommandNames.Estimate, CommandFlags.Modal)]
    public void Estimate() { if (Ready()) Typed(null, typedFlow: true, EstimateBody); }
    [CommandMethod(CivilDeliveryCommandNames.EstimateScanAfterSave, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void EstimateAfterSave() { if (Ready()) EstimateAfterSaveBody(); }
    [CommandMethod(CivilDeliveryCommandNames.SmokeEstimate, CommandFlags.Modal)]
    public void SmokeEstimate() { if (Ready()) Typed(null, typedFlow: true, SmokeEstimateBody); }
    [CommandMethod(CivilDeliveryCommandNames.Sections, CommandFlags.Modal)]
    public void Sections() { if (Ready()) Typed(CivilDeliveryUsage.SectionsAction, typedFlow: true, SectionsBody); }
    [CommandMethod(CivilDeliveryCommandNames.Setup, CommandFlags.Modal)]
    public void Setup() { if (Ready()) Typed(CivilDeliveryUsage.SetupAction, typedFlow: true, SetupBody); }
    [CommandMethod(CivilDeliveryCommandNames.SmokeDiscover, CommandFlags.Modal)]
    public void SmokeDiscover() { if (Ready()) Typed(null, typedFlow: true, SmokeDiscoverBody); }
    [CommandMethod(CivilDeliveryCommandNames.SmokeSections, CommandFlags.Modal)]
    public void SmokeSections() { if (Ready()) Typed(null, typedFlow: true, SmokeSectionsBody); }

    /// <summary>Identity of what is loaded: this plugin, its Core and the pairing verdict. Never needs Core.</summary>
    [CommandMethod("MCD_CHECK", CommandFlags.Modal)]
    public void Check()
    {
        var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        CivilDeliveryLoadGuard.EnsureReady();
        ed.WriteMessage("\n" + MahodCivilDeliveryApplication.Describe() + "\n" + CivilDeliveryLoadGuard.DiagnosticSummary + "\n");
    }

    /// <summary>
    /// Runs one typed command: under the standalone door when <paramref name="typedFlow"/> (its steps run inside the
    /// command), recording <paramref name="action"/>, when given, as completed, or failed if the body threw.
    /// </summary>
    private static void Typed(string? action, bool typedFlow, System.Action body)
    {
        var clock = Stopwatch.StartNew();
        var outer = MahodUsage.AmbientDoor;
        if (typedFlow) MahodUsage.AmbientDoor = MahodUsage.Standalone;
        bool ok = false;
        try
        {
            body();
            ok = true;
        }
        finally
        {
            MahodUsage.AmbientDoor = outer;
            if (action != null)
                MahodUsage.Action(CivilDeliveryUsage.Tool, action, ok ? MahodUsage.Completed : MahodUsage.Failed,
                    clock.ElapsedMilliseconds, MahodUsage.Standalone);
        }
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
