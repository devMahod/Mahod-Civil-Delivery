using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Live 2026-10-28: the busy-progress window (its own UI thread) loaded the shared theme first, and the corridor choice
/// built in code on the palette's thread then failed with "The calling thread cannot access this object because a
/// different thread owns it". Each UI thread keeps its own copy. The theme loads from the plugin assembly, whose
/// AutoCAD attributes this host cannot resolve, so the contract is pinned in the source.
/// </summary>
public sealed class CivilDeliveryThemeThreadTests
{
    private static string Source(params string[] parts) =>
        File.ReadAllText(parts.Aggregate(
            typeof(CivilDeliveryThemeThreadTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!,
            Path.Combine));

    [Fact]
    public void TheSharedTheme_IsKeptPerUiThread_BecauseTheProgressWindowHasItsOwn()
    {
        var theme = Source("CivilDelivery", "UI", "CivilDeliveryTheme.cs");
        theme.Should().Contain("[ThreadStatic] private static ResourceDictionary? _dictionary;")
            .And.Contain("public static ResourceDictionary Dictionary => _dictionary ??= Load();");

        var progress = Source("CivilDelivery", "UI", "BusyProgressWindow.cs");
        progress.Should().Contain("CivilDeliveryTheme.Apply(", "the progress window themes itself on its own thread");
    }
}
