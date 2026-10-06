using System;
using System.Collections.Generic;
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
    /// Employee installations must carry the exact two office DWGs Natalie approved;
    /// resolving them from a developer's Downloads folder would pass here and fail for
    /// every coworker. Read the PE without loading Autodesk dependencies.
    /// </summary>
    public class EmbeddedSectionVehicleBlockTests
    {
        private sealed record ExpectedAsset(
            string FileName, string ResourceName, string Sha256, int Length);

        private static readonly IReadOnlyList<ExpectedAsset> Assets = new[]
        {
            new ExpectedAsset(
                "HW-CARFRBK-01.dwg",
                "MahodAI.Civil3D.Plugin.assets.sections.HW-CARFRBK-01.dwg",
                "e2823d50d85e2f669944cdb68b2cc87f44090801cda75a86f66a6cc11ea38968",
                47784),
            new ExpectedAsset(
                "HW-CARFRFRW-01.dwg",
                "MahodAI.Civil3D.Plugin.assets.sections.HW-CARFRFRW-01.dwg",
                "412e0e6f226271aa1a66a2749f598c2a2f13a237d94d0eb567e07ec206924428",
                56406),
        };

        private static string PluginSourceDir() =>
            typeof(EmbeddedSectionVehicleBlockTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string PluginDll()
        {
            var here = Path.GetDirectoryName(
                typeof(EmbeddedSectionVehicleBlockTests).Assembly.Location)!;
            var dll = Path.Combine(here, ProductAssembly.FileName);
            File.Exists(dll).Should().BeTrue();
            return dll;
        }

        [Fact]
        public void AssemblyCarriesBothApprovedOfficeDwgs_ByteForByte()
        {
            foreach (var expected in Assets)
            {
                var repoAsset = Path.Combine(PluginSourceDir(), "assets", expected.FileName);
                File.Exists(repoAsset).Should().BeTrue(expected.FileName);
                var source = File.ReadAllBytes(repoAsset);
                source.Length.Should().Be(expected.Length);
                Sha(source).Should().Be(expected.Sha256);
                System.Text.Encoding.ASCII.GetString(source, 0, 6).Should().Be("AC1015");

                var embedded = ReadEmbeddedResource(PluginDll(), expected.ResourceName);
                embedded.Should().NotBeNull(expected.ResourceName);
                embedded!.Should().Equal(source,
                    "the original office DWG must not be converted or regenerated");
                Sha(embedded!).Should().Be(expected.Sha256);
            }
        }

        private static byte[]? ReadEmbeddedResource(string dllPath, string resourceName)
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            var mr = pe.GetMetadataReader();
            foreach (var handle in mr.ManifestResources)
            {
                var resource = mr.GetManifestResource(handle);
                if (mr.GetString(resource.Name) != resourceName) continue;
                var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
                var sectionIndex = pe.PEHeaders.GetContainingSectionIndex(directory.RelativeVirtualAddress);
                var section = pe.PEHeaders.SectionHeaders[sectionIndex];
                var offset = directory.RelativeVirtualAddress - section.VirtualAddress +
                             section.PointerToRawData + (int)resource.Offset;
                fs.Position = offset;
                Span<byte> size = stackalloc byte[4];
                fs.ReadExactly(size);
                var bytes = new byte[BitConverter.ToInt32(size)];
                fs.ReadExactly(bytes);
                return bytes;
            }
            return null;
        }

        private static string Sha(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
