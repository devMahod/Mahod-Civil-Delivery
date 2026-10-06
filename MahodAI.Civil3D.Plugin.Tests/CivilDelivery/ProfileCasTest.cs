using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Explicit test-only baseline capture. Production APIs intentionally have no
    /// no-CAS overload; tests state whether their target starts absent or from exact
    /// existing bytes at the point the synthetic workflow begins.
    /// </summary>
    internal static class ProfileCasTest
    {
        internal static ProjectProfileWriter.SaveResult Save(
            ProjectProfile profile, string targetPath, string summary,
            string approvedBy,
            IReadOnlyDictionary<string, string>? extraSourceHashes = null) =>
            ProjectProfileWriter.Save(
                profile, targetPath, summary, approvedBy, For(profile, targetPath),
                extraSourceHashes);

        internal static ProjectProfileWriter.ExpectedProfileState For(
            ProjectProfile profile, string targetPath)
        {
            var target = Path.GetFullPath(targetPath);
            if (File.Exists(target))
            {
                var hash = ArtifactHash.Sha256OfText(File.ReadAllText(target));
                return ProjectProfileWriter.CaptureExpectedState(target, hash, target);
            }

            var sourceHash = ArtifactHash.Sha256OfText(
                JsonSerializer.Serialize(profile, SectionsWorkflowService.Json));
            return ProjectProfileWriter.CaptureExpectedGeneratedState(
                profile, sourceHash, target);
        }
    }
}
