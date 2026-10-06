using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>Stable SHA-256 helpers used for drawing fingerprints, input hashes and evidence manifests.</summary>
    public static class ArtifactHash
    {
        /// <summary>
        /// Hashes a file that another process may hold open for writing. Civil 3D keeps
        /// the open drawing locked with write access; File.OpenRead (FileShare.Read)
        /// therefore threw "being used by another process" the moment evidence
        /// publication hashed the host drawing, and PLAN/estimate evidence could never be
        /// published on a real drawing (found live on 6422, 2026-09-03). Read sharing
        /// with writers and deleters is the only mode that works beside a live host.
        /// </summary>
        public static string Sha256OfFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        public static string Sha256OfText(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();
        }

        /// <summary>Short (16 hex chars) form for object names / logical keys where full hashes are unwieldy.</summary>
        public static string Short(string fullHash) =>
            string.IsNullOrEmpty(fullHash) ? string.Empty : fullHash[..Math.Min(16, fullHash.Length)];
    }
}
