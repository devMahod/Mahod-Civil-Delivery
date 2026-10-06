using System;
using System.IO;
using System.Security.Cryptography;

namespace MahodAI.Civil3D.Plugin.Runtime;

/// <summary>Copies the Ready resolver's local PDF to a unique verified temp file.
/// No viewer launch, bundle writes, fallback path, overwrite or cleanup of reader-owned files.</summary>
internal static class CivilDeliveryGuideTempCopy
{
    internal const long MaximumBytes = 25L * 1024 * 1024;
    internal enum State { Ready, InvalidSource, MissingSource, SourceUnavailable, TempCollision, CopyFailed, HashMismatch }
    internal sealed record Result(State Status, string? TempPath, string? SourceSha256, string ReasonCode,
        string? Detail = null, string? ResidualDirectory = null);

    internal static Result Prepare(CivilDeliveryGuidePath.Result? resolved)
    {
        try { return PrepareCore(resolved, Path.GetTempPath(), Guid.NewGuid); }
        catch (Exception ex) when (IsFileError(ex))
        { return Fail(State.CopyFailed, "temp_root_unavailable", ex.Message); }
    }

    // Internal deterministic filesystem test seams. Production calls Prepare only.
    internal static Result PrepareCore(CivilDeliveryGuidePath.Result? resolved, string tempRoot,
        Func<Guid> nextId, Action<Stream>? afterCopyForTest = null)
    {
        string? source = resolved?.GuidePath;
        if (resolved?.Status != CivilDeliveryGuidePath.State.Ready || string.IsNullOrWhiteSpace(source))
            return Fail(State.InvalidSource, "resolver_not_ready");
        try
        {
            if (!LocalAbsolute(source) ||
                !string.Equals(Path.GetFileName(source), CivilDeliveryGuidePath.GuideFileName, StringComparison.Ordinal))
                return Fail(State.InvalidSource, "guide_path_or_basename_invalid");
        }
        catch (Exception ex) when (IsFileError(ex))
        { return Fail(State.InvalidSource, "guide_path_or_basename_invalid", ex.Message); }
        if (!LocalAbsolute(tempRoot)) return Fail(State.CopyFailed, "temp_root_not_local_absolute");

        string? ownedDirectory = null;
        string phase = "source";
        try
        {
            // On Windows this denies new writers and delete/rename for the complete copy+verification window.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length < 5 || input.Length > MaximumBytes)
                return Fail(State.InvalidSource, "guide_size_outside_5_bytes_to_25MiB");
            var header = new byte[5];
            input.ReadExactly(header);
            if (!header.AsSpan().SequenceEqual("%PDF-"u8))
                return Fail(State.InvalidSource, "guide_pdf_header_invalid");

            phase = "temp";
            string root = Path.Combine(Path.GetFullPath(tempRoot), "MahodCivilDelivery-guide");
            Directory.CreateDirectory(root);
            string destination = Path.Combine(root, nextId().ToString("N"));
            if (Directory.Exists(destination) || File.Exists(destination))
                return Fail(State.TempCollision, "temp_guid_already_exists");
            // BCL creates this staging directory uniquely. Move never merges into an existing directory.
            // Keep it on the same volume as Path.GetTempPath, which is also the production tempRoot.
            ownedDirectory = Directory.CreateTempSubdirectory("MahodCivilDelivery-guide-stage-").FullName;
            try { Directory.Move(ownedDirectory, destination); }
            catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
            { return Fail(State.TempCollision, "temp_guid_already_exists", residual: ownedDirectory); }
            ownedDirectory = destination;

            string path = Path.Combine(destination, CivilDeliveryGuidePath.GuideFileName);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            output.Write(header);
            sourceHash.AppendData(header);
            var buffer = new byte[64 * 1024];
            long count = header.Length;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                count += read;
                if (count > MaximumBytes)
                    return Fail(State.InvalidSource, "guide_grew_beyond_limit", residual: ownedDirectory);
                sourceHash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            if (count != input.Length)
                return Fail(State.CopyFailed, "guide_length_changed", residual: ownedDirectory);
            byte[] expected = sourceHash.GetHashAndReset();
            output.Flush(flushToDisk: true);
            afterCopyForTest?.Invoke(output);
            output.Flush(flushToDisk: true);
            output.Position = 0;
            byte[] actual = SHA256.HashData(output);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                return Fail(State.HashMismatch, "temp_copy_sha256_mismatch", residual: ownedDirectory);
            // Both streams dispose before control returns to the caller; no bundle lock survives.
            return new Result(State.Ready, path, Convert.ToHexString(expected), "guide_temp_copy_verified");
        }
        catch (FileNotFoundException ex) when (phase == "source")
        { return Fail(State.MissingSource, "guide_source_missing", ex.Message); }
        catch (DirectoryNotFoundException ex) when (phase == "source")
        { return Fail(State.MissingSource, "guide_source_missing", ex.Message); }
        catch (Exception ex) when (IsFileError(ex))
        {
            return Fail(phase == "source" ? State.SourceUnavailable : State.CopyFailed,
                phase == "source" ? "guide_source_unavailable" : "guide_temp_copy_failed",
                ex.Message, ownedDirectory);
        }
    }

    private static bool LocalAbsolute(string path)
    {
        // No UNC/device path or URI. Resolver layout validation remains resolver182B's responsibility.
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal))
            return false;
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/') && Path.IsPathFullyQualified(path);
    }
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static Result Fail(State state, string reason, string? detail = null, string? residual = null)
        => new(state, null, null, reason, detail, residual);
}
