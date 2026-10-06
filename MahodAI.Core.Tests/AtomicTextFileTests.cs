using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class AtomicTextFileTests
{
    [Fact]
    public void WriteAllText_PublishesCompleteUtf8WithoutBom_AndLeavesNoPendingFile()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "nested", "evidence.json");
            const string payload = "{\"status\":\"מאומת\",\"rows\":138}";

            AtomicTextFile.WriteAllText(path, payload);

            File.ReadAllText(path, Encoding.UTF8).Should().Be(payload);
            var bytes = File.ReadAllBytes(path);
            (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                .Should().BeFalse("evidence uses deterministic UTF-8 without a BOM");
            Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*")
                .Should().BeEmpty("a successful publish must not leak its sibling staging file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteAllText_ReplacesAnExistingArtifactAsOneCompletePayload()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "run_manifest.json");
            File.WriteAllText(path, new string('x', 32_768));
            var replacement = string.Join('|', Enumerable.Range(0, 10_000));

            AtomicTextFile.WriteAllText(path, replacement);

            File.ReadAllText(path).Should().Be(replacement);
            Directory.EnumerateFiles(root).Should().Equal(path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteAllText_WhenPublishIsDenied_PreservesOldArtifactAndCleansPendingFile()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "apply_result.json");
            const string authoritative = "{\"status\":\"Verified\",\"revision\":1}";
            File.WriteAllText(path, authoritative);

            Action publish;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                publish = () => AtomicTextFile.WriteAllText(
                    path, "{\"status\":\"Verified\",\"revision\":2}");
                var failure = Record.Exception(publish);
                failure.Should().NotBeNull(
                    "the locked authoritative path cannot be replaced on Windows");
                (failure is IOException or UnauthorizedAccessException).Should().BeTrue(
                    $"the platform should report a sharing/permission failure, actual={failure?.GetType().Name}");
            }

            File.ReadAllText(path).Should().Be(authoritative,
                "a failed atomic publish must never truncate or partially replace prior evidence");
            Directory.EnumerateFiles(root, "*.tmp-*").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteAllText_RejectsInvalidInputsBeforePublishingAnything()
    {
        var blankPath = () => AtomicTextFile.WriteAllText(" ", "payload");
        var nullContents = () => AtomicTextFile.WriteAllText("artifact.json", null!);

        blankPath.Should().Throw<ArgumentException>();
        nullContents.Should().Throw<ArgumentNullException>();
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcd-atomic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
