using System.Collections.Generic;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.CivilDelivery;

public sealed class CorridorRebuildLogicTests
{
    private const string Stored = @"C:\ProgramData\Autodesk\C3D 2025\enu\";
    private const string Here = @"C:\ProgramData\Autodesk\C3D 2026\enu";

    private static readonly CorridorRebuildLogic.Generator Stock = new("UseDotNet", Stored + "C3DStockSubassemblies.dll");
    private static readonly CorridorRebuildLogic.Generator Mahod = new("UseDotNet", Stored + "MahodSubassemblies.dll");

    [Fact]
    public void TheVersionFolder_ComesFromTheRunningCivil3D()
    {
        CorridorRebuildLogic.VersionFolder(@"C:\Program Files\Autodesk\AutoCAD 2026\C3D\AeccDbMgd.dll", @"C:\ProgramData")
            .Should().Be(Here);
        CorridorRebuildLogic.VersionFolder(@"D:\elsewhere\AeccDbMgd.dll", @"C:\ProgramData").Should().BeNull();
        CorridorRebuildLogic.YearOf(Here).Should().Be("2026");
    }

    [Fact]
    public void The6403Drawing_OnAComputerWithoutTheOfficesSubassemblies_MissesOnlyTheirDll()
    {
        // Live 2026-11-03: every corridor of 6403 stores C3D 2025 paths; this computer has the stock DLL for 2026 and
        // MahodSubassemblies.dll for 2027 only — R-14's rebuild computed nothing.
        var files = new HashSet<string> { Here + @"\C3DStockSubassemblies.dll", @"C:\ProgramData\Autodesk\C3D 2027\enu\MahodSubassemblies.dll" };
        CorridorRebuildLogic.MissingFiles(new[] { Stock, Mahod, Mahod }, Here, files.Contains)
            .Should().Equal("MahodSubassemblies.dll");
    }

    [Fact]
    public void TheDll_InTheRunningVersionsFolder_OrAtItsStoredPath_IsFound()
    {
        var installed = new HashSet<string> { Here + @"\C3DStockSubassemblies.dll", Here + @"\MahodSubassemblies.dll" };
        CorridorRebuildLogic.MissingFiles(new[] { Stock, Mahod }, Here, installed.Contains).Should().BeEmpty();

        var stored = new HashSet<string> { Stored + "C3DStockSubassemblies.dll", Stored + "MahodSubassemblies.dll" };
        CorridorRebuildLogic.MissingFiles(new[] { Stock, Mahod }, null, stored.Contains).Should().BeEmpty();
    }

    [Fact]
    public void NonDotNetGenerators_AndEmptyPaths_AreNotJudged()
    {
        CorridorRebuildLogic.MissingFiles(
                new[] { new CorridorRebuildLogic.Generator("UseVBA", "C3DStockSubassemblyScripts.dvb"), new CorridorRebuildLogic.Generator("UseDotNet", "") },
                Here, _ => false)
            .Should().BeEmpty();
    }
}
