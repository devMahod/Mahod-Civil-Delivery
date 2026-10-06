using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The engineer UI must carry the official logo INSIDE the shipped assembly. A file
    /// path into someone's Pictures folder would work on the build machine and fail on
    /// every other one.
    ///
    /// These checks read the compiled DLL as a PE file rather than loading it, because
    /// loading the plugin assembly outside Civil 3D drags in the Autodesk managed
    /// assemblies and fails for reasons that have nothing to do with the logo.
    /// </summary>
    public class EmbeddedBrandAssetTests
    {
        private const string WhiteLogoResource = "MahodAI.Civil3D.Plugin.assets.mahod_logo_white.png";

        private static string PluginSourceDir() =>
            typeof(EmbeddedBrandAssetTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string PluginDll()
        {
            // The test output folder holds the freshly built plugin assembly.
            var here = Path.GetDirectoryName(typeof(EmbeddedBrandAssetTests).Assembly.Location)!;
            var dll = Path.Combine(here, ProductAssembly.FileName);
            File.Exists(dll).Should().BeTrue($"the plugin assembly should sit beside the tests ({dll})");
            return dll;
        }

        /// <summary>Reads one embedded resource's bytes straight out of the PE image.</summary>
        private static byte[]? ReadEmbeddedResource(string dllPath, string resourceName)
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            var mr = pe.GetMetadataReader();

            foreach (var handle in mr.ManifestResources)
            {
                var res = mr.GetManifestResource(handle);
                if (mr.GetString(res.Name) != resourceName) continue;

                // Embedded resources live in the resources data directory; each is
                // prefixed with its own 4-byte length.
                var resourcesDir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
                var start = pe.PEHeaders.GetContainingSectionIndex(resourcesDir.RelativeVirtualAddress);
                var section = pe.PEHeaders.SectionHeaders[start];
                var offset = resourcesDir.RelativeVirtualAddress
                             - section.VirtualAddress
                             + section.PointerToRawData
                             + (int)res.Offset;

                fs.Position = offset;
                Span<byte> lengthBytes = stackalloc byte[4];
                fs.ReadExactly(lengthBytes);
                var length = BitConverter.ToInt32(lengthBytes);

                var payload = new byte[length];
                fs.ReadExactly(payload);
                return payload;
            }
            return null;
        }

        private static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        [Fact]
        public void ShippedAssembly_CarriesTheOfficialWhiteLogo()
        {
            var bytes = ReadEmbeddedResource(PluginDll(), WhiteLogoResource);

            bytes.Should().NotBeNull(
                $"the UI resolves the logo from '{WhiteLogoResource}'; without it the palette ships unbranded");
            bytes!.Length.Should().BeGreaterThan(1000);
        }

        [Fact]
        public void EmbeddedLogo_IsByteIdenticalToTheApprovedAsset()
        {
            // Not "looks like the logo" - the same bytes as the approved brand file.
            // Anything else means someone regenerated it, which was explicitly ruled out.
            var source = Path.Combine(PluginSourceDir(), "assets", "mahod_logo_white.png");
            File.Exists(source).Should().BeTrue();

            var embedded = ReadEmbeddedResource(PluginDll(), WhiteLogoResource);
            embedded.Should().NotBeNull();

            Sha256(embedded!).Should().Be(Sha256(File.ReadAllBytes(source)));
        }

        [Fact]
        public void EmbeddedLogo_IsARealPng()
        {
            var bytes = ReadEmbeddedResource(PluginDll(), WhiteLogoResource)!;

            bytes.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        }

        [Fact]
        public void UiSource_ResolvesTheLogoFromTheAssembly_NeverFromADiskPath()
        {
            // A hardcoded Pictures/Desktop/user path would work here and nowhere else.
            var xamlCs = File.ReadAllText(Path.Combine(
                PluginSourceDir(), "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));

            xamlCs.Should().Contain("GetManifestResourceStream",
                "the logo must come out of the assembly");
            xamlCs.Should().NotContain(@"C:\Users",
                "no shipped code may point at a developer's own folders");
            xamlCs.Should().NotContain("Pictures");
        }
    }
}
