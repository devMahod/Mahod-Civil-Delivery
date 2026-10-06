using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// 1.4.1 C3 (984 live b36, Codex 12:34): six unloaded background XREFs of the host made the setup scan "incomplete"
/// and blocked saving even though the CL was chosen from its own file. With an explicit separate-file scope the host is
/// not walked for CL candidates (same boundary as the reader); nothing unread is declared fine. Without that scope
/// (legacy host scope) the unloaded XREF still blocks; inside the chosen CL file the external scan still throws.
/// </summary>
public class SetupScopeDiscoveryTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { typeof(SetupScopeDiscoveryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value! }.Concat(parts).ToArray()));

    [Fact]
    public void Separate_file_scope_skips_host_CL_discovery_but_still_reads_host_alignments_and_sources()
    {
        var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
        var scan = scanner.IndexOf("public ProjectSetupScan Scan(");
        var gate = scanner.IndexOf("var hostClScoped = !ClInstructionReader.IsSourceFileScoped(profile.Sections.Cl);", scan);
        var walk = scanner.IndexOf("? AccumulateModelSpace(db, tr, log, scan)", scan);
        var skip = scanner.IndexOf(": SkippedHostGeometry(log);", scan);
        var alignments = scanner.IndexOf("var alignments = CollectAlignments(civilDoc, tr, log, scan);", scan);
        var sources = scanner.IndexOf("civilDoc.GetSurfaceIds()", scan);
        gate.Should().BeGreaterThan(scan);
        walk.Should().BeGreaterThan(gate);
        skip.Should().BeGreaterThan(walk);
        alignments.Should().BeGreaterThan(scan).And.BeLessThan(gate, "alignments are read from the host either way");
        sources.Should().BeGreaterThan(skip, "surfaces/corridors are read from the host either way");
    }

    [Fact]
    public void A_separate_CL_file_is_still_scanned_strictly()
    {
        // ScanClFile has no scan to mark: an unloaded XREF inside the chosen CL drawing still throws (fail-closed).
        var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
        var clFile = scanner.IndexOf("public ExternalClScan ScanClFile(");
        scanner.IndexOf("geometry = AccumulateModelSpace(side, sideTr, log);", clFile).Should().BeGreaterThan(clFile);
        scanner.Should().Contain("throw new InvalidOperationException($\"Incomplete CL discovery: {stage}\", error);");
    }

    [Fact]
    public void An_unavailable_XREF_is_one_Hebrew_error_per_definition_and_keeps_the_scan_incomplete()
    {
        var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
        var mark = scanner.IndexOf("private static void MarkIncomplete(");
        scanner.IndexOf("scan.ScanComplete = false;", mark).Should().BeGreaterThan(mark);
        scanner.IndexOf("if (error is XrefUnavailableException xref)", mark).Should().BeGreaterThan(mark);
        scanner.Should().Contain("Severity = FindingSeverity.Error,\n                    Title = title,")
            .And.Contain("XREF לא טעון או לא פתור:");
    }
}
