using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.Tools.Creation;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools.Creation
{
    /// <summary>
    /// AutoCAD-free tests for clone_assembly_from_library: the clone-from-library upgrade.
    ///
    /// Covers what is reachable without a live Civil 3D document:
    ///  - <see cref="CloneAssemblyFromLibraryTool"/> metadata, registration, schema, and the
    ///    null-document guard;
    ///  - <see cref="AssemblyLibraryLocator"/> — the pure version→year mapping, source-assembly
    ///    naming, and library-path resolution (override file/dir, bundled per-version file,
    ///    cross-year fallback).
    /// The live ImportAssembly path requires a Civil 3D document and is marked Skip.
    /// </summary>
    public class CloneAssemblyFromLibraryToolTests
    {
        // Predictable, OS-independent path join + filesystem stub for ResolveLibraryPath tests.
        private static readonly Func<string, string, string> Combine = (a, b) => $"{a}/{b}";

        private static Func<string, bool> Exists(params string[] present)
        {
            var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        // ───────────────────────── Tool metadata / registration ─────────────────────────

        [Fact]
        public void Tool_HasExpectedMetadata()
        {
            var tool = new CloneAssemblyFromLibraryTool();
            tool.Name.Should().Be("clone_assembly_from_library");
            tool.Category.Should().Be(ToolCategories.Creation);
            tool.ParameterSchema.Should().NotBeNull();
        }

        [Fact]
        public void Tool_IsRegistered()
        {
            ToolRegistry.Instance.HasTool("clone_assembly_from_library").Should().BeTrue();
        }

        [Fact]
        public void Schema_RequiresRoadType_AndEnumeratesRoadTypes()
        {
            var tool = new CloneAssemblyFromLibraryTool();
            var schema = tool.ParameterSchema!.Value;

            schema.GetProperty("required").EnumerateArray()
                .Select(e => e.GetString())
                .Should().Contain("road_type");

            var enumValues = schema
                .GetProperty("properties")
                .GetProperty("road_type")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToArray();

            enumValues.Should().BeEquivalentTo(
                "urban_2lane", "rural_2lane", "divided_highway", "collector_local");
        }

        [Fact(Skip = "ExecuteAsync JIT-loads Acdbmgd (Civil 3D 2026) — unrunnable outside a live Civil 3D process; see CLAUDE.md env-dependent tests. The null-doc guard returns Fail(ExecutionFailed).")]
        [Trait("Category", "RequiresCivil3D")]
        public async Task Execute_NullCivilDoc_ReturnsFail()
        {
            var tool = new CloneAssemblyFromLibraryTool();
            var parameters = JsonDocument.Parse(
                """{"road_type": "rural_2lane"}""").RootElement;

            var result = await tool.ExecuteAsync(
                null!, null!, parameters, new ToolCache(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Error!.Code.Should().Be(ToolErrorCodes.ExecutionFailed);
        }

        // ───────────────────────── Version → product year ─────────────────────────

        [Theory]
        [InlineData(24, 2026)] // older majors collapse to the oldest supported install (2026)
        [InlineData(25, 2026)] // Civil 3D 2026 = R25.x
        [InlineData(26, 2027)] // Civil 3D 2027 = R26.x
        [InlineData(27, 2027)] // future majors resolve to the newest we ship
        public void YearForVersionMajor_MapsReleaseToProductYear(int major, int expectedYear)
        {
            AssemblyLibraryLocator.YearForVersionMajor(major).Should().Be(expectedYear);
        }

        [Theory]
        [InlineData(2026, "MahodAI_Assemblies_2026.dwg")]
        [InlineData(2027, "MahodAI_Assemblies_2027.dwg")]
        public void LibraryFileName_IsPerVersion(int year, string expected)
        {
            AssemblyLibraryLocator.LibraryFileName(year).Should().Be(expected);
        }

        // ───────────────────────── Source assembly naming ─────────────────────────

        [Theory]
        [InlineData("urban_2lane", "MahodAI_urban_2lane")]
        [InlineData("rural_2lane", "MahodAI_rural_2lane")]
        [InlineData("divided_highway", "MahodAI_divided_highway")]
        [InlineData("collector_local", "MahodAI_collector_local")]
        [InlineData("  Divided_Highway ", "MahodAI_divided_highway")] // trimmed + lowercased
        public void SourceAssemblyName_MatchesShellNamingConvention(string roadType, string expected)
        {
            AssemblyLibraryLocator.SourceAssemblyName(roadType).Should().Be(expected);
        }

        [Theory]
        [InlineData("nonsense")]
        [InlineData("")]
        [InlineData(null)]
        public void SourceAssemblyName_UnknownFallsBackToDefault(string? roadType)
        {
            AssemblyLibraryLocator.SourceAssemblyName(roadType)
                .Should().Be("MahodAI_rural_2lane");
        }

        // ───────────────────────── Stock-assembly fallback mapping ─────────────────────────

        [Theory]
        [InlineData("rural_2lane", "Basic Assembly.dwg", "Basic Assembly")]
        [InlineData("divided_highway", "Divided Highway.dwg", "Divided Highway")]
        [InlineData("urban_2lane", "Secondary Road Full Section.dwg", "Secondary Road Full Section")]
        [InlineData("collector_local", "Secondary Road Full Section.dwg", "Secondary Road Full Section")]
        [InlineData("  Divided_Highway ", "Divided Highway.dwg", "Divided Highway")] // trimmed + lowercased
        public void StockAssemblyFor_MapsRoadTypeToCivil3dStockAssembly(
            string roadType, string expectedFile, string expectedName)
        {
            var (file, name) = AssemblyLibraryLocator.StockAssemblyFor(roadType);
            file.Should().Be(expectedFile);
            name.Should().Be(expectedName);
        }

        [Theory]
        [InlineData("nonsense")]
        [InlineData("")]
        [InlineData(null)]
        public void StockAssemblyFor_UnknownFallsBackToBasicAssembly(string? roadType)
        {
            var (file, name) = AssemblyLibraryLocator.StockAssemblyFor(roadType);
            file.Should().Be("Basic Assembly.dwg");
            name.Should().Be("Basic Assembly");
        }

        // ───────────────────────── Library path resolution ─────────────────────────

        [Fact]
        public void Resolve_OverrideDwgFile_UsedVerbatimWhenPresent()
        {
            var path = "C:/lib/custom.dwg";
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: path, bundleDir: null, year: 2026,
                fileExists: Exists(path), combine: Combine);

            resolved.Should().Be(path);
        }

        [Fact]
        public void Resolve_OverrideDwgFile_MissingAndNoBundle_ReturnsNull()
        {
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: "C:/lib/custom.dwg", bundleDir: null, year: 2026,
                fileExists: Exists(/* nothing */), combine: Combine);

            resolved.Should().BeNull();
        }

        [Fact]
        public void Resolve_OverrideDirectory_PicksVersionedFile()
        {
            var versioned = "C:/lib/MahodAI_Assemblies_2027.dwg";
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: "C:/lib", bundleDir: null, year: 2027,
                fileExists: Exists(versioned), combine: Combine);

            resolved.Should().Be(versioned);
        }

        [Fact]
        public void Resolve_OverrideDirectory_FallsBackToOtherYearFile()
        {
            // Running 2027 but only the 2026 file is present in the override dir.
            var only2026 = "C:/lib/MahodAI_Assemblies_2026.dwg";
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: "C:/lib", bundleDir: null, year: 2027,
                fileExists: Exists(only2026), combine: Combine);

            resolved.Should().Be(only2026);
        }

        [Fact]
        public void Resolve_Bundled_PicksVersionedFile()
        {
            var bundled = "C:/bundle/Assemblies/MahodAI_Assemblies_2026.dwg";
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: null, bundleDir: "C:/bundle", year: 2026,
                fileExists: Exists(bundled), combine: Combine);

            resolved.Should().Be(bundled);
        }

        [Fact]
        public void Resolve_Bundled_FallsBackToOtherYearFile()
        {
            // Running 2026 but only the 2027 file is bundled (ReadDwgFile will then reject it,
            // which the tool turns into a clean fallback — resolution still returns the path).
            var only2027 = "C:/bundle/Assemblies/MahodAI_Assemblies_2027.dwg";
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: null, bundleDir: "C:/bundle", year: 2026,
                fileExists: Exists(only2027), combine: Combine);

            resolved.Should().Be(only2027);
        }

        [Fact]
        public void Resolve_NothingPresent_ReturnsNull()
        {
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: null, bundleDir: "C:/bundle", year: 2026,
                fileExists: Exists(/* nothing */), combine: Combine);

            resolved.Should().BeNull();
        }

        [Fact]
        public void Resolve_OverrideWins_OverBundle()
        {
            var overrideFile = "C:/custom/MahodAI_Assemblies_2026.dwg";
            var bundled = "C:/bundle/Assemblies/MahodAI_Assemblies_2026.dwg";
            var resolved = AssemblyLibraryLocator.ResolveLibraryPath(
                overridePath: "C:/custom", bundleDir: "C:/bundle", year: 2026,
                fileExists: Exists(overrideFile, bundled), combine: Combine);

            resolved.Should().Be(overrideFile);
        }

        [Fact(Skip = "Live ImportAssembly requires a Civil 3D document and a library .dwg")]
        [Trait("Category", "RequiresCivil3D")]
        public void Happy_ImportsAssembly_FromLibrary() { }
    }
}
