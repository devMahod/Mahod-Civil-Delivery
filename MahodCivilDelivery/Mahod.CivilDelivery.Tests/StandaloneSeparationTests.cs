using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using Xunit;

namespace Mahod.CivilDelivery.Tests;

/// <summary>
/// What makes Mahod Civil Delivery a separate tool that can load beside ANY MahodAI: its own assembly names, its own
/// command names, one guarded command class, a Core identity that pairs with the Core it ships, and a ribbon that joins
/// MahodAI's tab without ever creating it. Read from the built DLLs' metadata — nothing Autodesk is loaded.
/// </summary>
public sealed class StandaloneSeparationTests
{
    /// <summary>Every command name MahodAI registers for the Civil Delivery it still carries (upstream 1.6.5).</summary>
    private static readonly string[] MahodAiCommands =
    {
        "MHD_CIVIL_DELIVERY", "MHD_DELIVERY_AFTER_SAVE", "MHD_ESTIMATE", "MHD_ESTIMATE_SCAN_AFTER_SAVE",
        "MHD_SECTIONS", "MHD_SETUP", "MHD_SMOKE_DISCOVER", "MHD_SMOKE_ESTIMATE", "MHD_SMOKE_SECTIONS",
    };

    private static string Output(string file) => Path.Combine(AppContext.BaseDirectory, file);

    private static string Source(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", relative)));

    private static IReadOnlyList<string> NameConstants() => typeof(CivilDeliveryCommandNames)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    [Fact]
    public void EveryCommandIsMcd_AndNoneIsACommandMahodAiRegisters()
    {
        var names = NameConstants();
        names.Should().HaveCount(9).And.OnlyHaveUniqueItems();
        names.Should().OnlyContain(n => n.StartsWith("MCD_", StringComparison.Ordinal));
        names.Intersect(MahodAiCommands, StringComparer.OrdinalIgnoreCase).Should().BeEmpty();
    }

    [Fact]
    public void TheAssembliesHaveNamesNoMahodAiBuildUses()
    {
        using var plugin = new PEReader(File.OpenRead(Output("Mahod.CivilDelivery.dll")));
        using var core = new PEReader(File.OpenRead(Output("Mahod.CivilDelivery.Core.dll")));
        var pluginMd = plugin.GetMetadataReader();
        var coreMd = core.GetMetadataReader();
        pluginMd.GetString(pluginMd.GetAssemblyDefinition().Name).Should().Be("Mahod.CivilDelivery");
        coreMd.GetString(coreMd.GetAssemblyDefinition().Name).Should().Be("Mahod.CivilDelivery.Core");
        // The plugin must reference the renamed Core, never MahodAI's Core or MahodAI's plugin.
        var references = pluginMd.AssemblyReferences.Select(h => pluginMd.GetString(pluginMd.GetAssemblyReference(h).Name)).ToList();
        references.Should().Contain("Mahod.CivilDelivery.Core")
            .And.NotContain("MahodAI.CivilDelivery.Core")
            .And.NotContain("MahodAI.Civil3D.Plugin");
        File.Exists(Output("MahodAI.Civil3D.Plugin.dll")).Should().BeFalse("the separate lane must not carry the MahodAI fork");
    }

    [Fact]
    public void TheOnlyRegisteredCommandClassIsTheGuardedFacade_AndItRegistersExactlyTheMcdNames()
    {
        using var pe = new PEReader(File.OpenRead(Output("Mahod.CivilDelivery.dll")));
        var md = pe.GetMetadataReader();

        var commandClasses = md.GetAssemblyDefinition().GetCustomAttributes()
            .Select(md.GetCustomAttribute)
            .Where(a => AttributeTypeName(md, a) == "Autodesk.AutoCAD.Runtime.CommandClassAttribute")
            .Select(a => { var b = md.GetBlobReader(a.Value); b.ReadUInt16(); return b.ReadSerializedString(); })
            .ToList();
        commandClasses.Should().ContainSingle().Which.Should().StartWith("MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryCommandFacade");

        var facade = md.TypeDefinitions.Select(md.GetTypeDefinition)
            .Single(t => md.GetString(t.Name) == "CivilDeliveryCommandFacade");
        var registered = facade.GetMethods().Select(md.GetMethodDefinition)
            .SelectMany(m => m.GetCustomAttributes().Select(md.GetCustomAttribute))
            .Where(a => AttributeTypeName(md, a) == "Autodesk.AutoCAD.Runtime.CommandMethodAttribute")
            .Select(a => { var b = md.GetBlobReader(a.Value); b.ReadUInt16(); return b.ReadSerializedString()!; })
            .ToList();
        registered.Should().BeEquivalentTo(NameConstants().Append("MCD_CHECK"));
    }

    [Fact]
    public void TheEmbeddedCoreIdentityNamesTheCoreThatShipsBesideThePlugin()
    {
        using var stream = typeof(CivilDeliveryCommandNames).Assembly
            .GetManifestResourceStream("MahodAI.CivilDelivery.CoreIdentity.v1");
        stream.Should().NotBeNull();
        var values = new StreamReader(stream!).ReadToEnd()
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        values.Should().HaveCount(4);
        values["schema"].Should().Be("1");
        values["name"].Should().Be("Mahod.CivilDelivery.Core");
        values["tfm"].Should().Be("net10.0-windows");
        values["sha256"].Should().BeEquivalentTo(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Output("Mahod.CivilDelivery.Core.dll")))));
    }

    [Fact]
    public void TheRibbonJoinsMahodAisTabButNeverCreatesIt()
    {
        var ribbon = Source("Mahod.CivilDelivery/CivilDeliveryRibbon.cs");
        ribbon.Should().Contain("internal const string MahodTabId = \"MahodAI_Tab\";")
            .And.Contain("internal const string OwnTabId = \"MahodCivilDelivery_Tab\";");
        // The only tab it ever creates carries its own id.
        System.Text.RegularExpressions.Regex.Matches(ribbon, @"new RibbonTab\b").Should().HaveCount(1);
        ribbon.Should().Contain("new RibbonTab { Id = OwnTabId, Title = \"Mahod\" }");
        // The old button is hidden, never removed; tab edits never happen inside the tabs' own change event.
        ribbon.Should().Contain("item.IsVisible = false;").And.NotContain("Items.Remove(");
        ribbon.Should().Contain("tabs.CollectionChanged += (_, _) => QueuePlace();");
    }

    [Fact]
    public void TheRibbonButtonHasItsIconAndNoEmptyPanelIsLeft()
    {
        // Live 29.09.2026: the button showed text only, and MahodAI's emptied panel kept its title on the tab.
        var assembly = typeof(CivilDeliveryCommandNames).Assembly;
        foreach (var name in new[] { "civil_delivery_32.png", "civil_delivery_16.png" })
        {
            using var stream = assembly.GetManifestResourceStream("Mahod.CivilDelivery.assets." + name);
            stream.Should().NotBeNull(name + " is embedded");
            var header = new byte[8];
            stream!.ReadExactly(header);
            header.Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, name + " is a PNG");
        }
        var ribbon = Source("Mahod.CivilDelivery/CivilDeliveryRibbon.cs");
        ribbon.Should().Contain("LargeImage = Icon(\"civil_delivery_32.png\"),")
            .And.Contain("Image = Icon(\"civil_delivery_16.png\"),")
            .And.Contain("if (hidOne && !panel.Source.Items.Any(i => i.IsVisible)) panel.IsVisible = false;");
    }

    [Fact]
    public void TheSeparatePluginWritesItsOwnLog()
    {
        var logger = typeof(CivilDeliveryCommandNames).Assembly.GetType("MahodAI.Civil3D.Plugin.Utilities.MahodLogger")!;
        logger.GetField("LogName", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()
            .Should().Be("mahod_civil_delivery");
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference) return "";
        var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (ctor.Parent.Kind != HandleKind.TypeReference) return "";
        var type = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
        return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
    }
}
