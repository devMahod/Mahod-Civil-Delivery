using System.IO;

namespace Codex;

/// <summary>
/// Where the adopted Codex proposal tests write their YAML round-trips and observations when the Codex runner's
/// CODEX_ACCEPT92_OUTPUT is not set (the product lanes): a fresh directory under the temp folder, per process.
/// </summary>
internal static class AdoptedTestOutput
{
    private static readonly Lazy<string> RootValue = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "mahod-adopted-codex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    });

    public static string Root => RootValue.Value;
}
