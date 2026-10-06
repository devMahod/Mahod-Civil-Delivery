using System;
using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// Civil 3D holds the open drawing with write access. Evidence publication hashes that
/// drawing; with FileShare.Read the hash threw "being used by another process" and no
/// PLAN/estimate evidence could ever be published on a live drawing (6422, 2026-09-03).
/// </summary>
public sealed class ArtifactHashSharingTests
{
    [Fact]
    public void Sha256OfFile_Works_WhileAnotherHandleHoldsTheFileForWriting()
    {
        var path = Path.Combine(Path.GetTempPath(), "mahod-hash-" + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            var expected = ArtifactHash.Sha256OfFile(path);

            // The host keeps the drawing open ReadWrite and only shares reads/writes —
            // exactly the mode under which File.OpenRead fails with a sharing violation.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var whileOpen = () => ArtifactHash.Sha256OfFile(path);
                whileOpen.Should().NotThrow("evidence must publish beside a live Civil session");
                whileOpen().Should().Be(expected);

                var naive = () => { using var _ = File.OpenRead(path); };
                naive.Should().Throw<IOException>("this is the failure the fix removes");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Sha256OfFile_StillFailsClosed_WhenTheFileIsExclusivelyLocked()
    {
        var path = Path.Combine(Path.GetTempPath(), "mahod-hash-" + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            File.WriteAllBytes(path, new byte[] { 42 });
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = () => ArtifactHash.Sha256OfFile(path);
                locked.Should().Throw<IOException>("an exclusively locked file cannot be proven; never invent a hash");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
