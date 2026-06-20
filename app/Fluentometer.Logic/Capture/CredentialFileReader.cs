using System;
using System.IO;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Canonical two-phase credential file read helper.
/// <para>
/// Phase 1: <c>File.GetAttributes</c> — combines the existence check and the
/// reparse-point check into a single syscall (G-1 + G-6), eliminating the TOCTOU
/// race that exists when <c>File.Exists</c> is called first and then
/// <c>GetAttributes</c>. If the path carries <see cref="FileAttributes.ReparsePoint"/>
/// (symlink or NTFS junction), the result is <see cref="ReadResult.IsReparsePoint"/>.
/// </para>
/// <para>
/// Phase 2: <c>File.ReadAllText</c> — called only when phase 1 confirms the path is
/// a regular (non-reparse) file.
/// </para>
/// </summary>
/// <remarks>
/// Design constraints (NON-NEGOTIABLE):
/// <list type="bullet">
///   <item>This helper is STATIC. No base class, no inheritance.</item>
///   <item>It performs ZERO deserialization, ZERO field access, and ZERO
///   <see cref="RedactedString"/> wrapping. All of those belong at the per-provider
///   call site so each provider's G-2 audit is independently verifiable by reading.</item>
///   <item>It performs ZERO path resolution. <c>CODEX_HOME</c> expansion and similar
///   policy lives in each provider's <c>Paths</c> class (G-7).</item>
///   <item>G-11: No catch block references <c>ex</c> or <c>ex.Message</c>. File-access
///   exception messages on Windows embed the file path, which contains the Windows
///   username. Forwarding the message would violate the secret-field ban list.</item>
/// </list>
/// </remarks>
public static class CredentialFileReader
{
    /// <summary>Result of a two-phase credential file read.</summary>
    public sealed record ReadResult
    {
        /// <summary>File was absent or the containing directory was not found.</summary>
        public bool IsNotFound { get; init; }

        /// <summary>
        /// File carried <see cref="FileAttributes.ReparsePoint"/> (symlink or NTFS
        /// junction). The file was NOT read.
        /// </summary>
        public bool IsReparsePoint { get; init; }

        /// <summary>File was read successfully; <see cref="Json"/> contains the content.</summary>
        public bool IsSuccess { get; init; }

        /// <summary>
        /// An I/O error other than not-found occurred (permissions, lock, etc.).
        /// Maps to the caller's "not found" treatment per G-11.
        /// </summary>
        public bool IsIoError { get; init; }

        /// <summary>
        /// Raw file content. Non-null only when <see cref="IsSuccess"/> is true.
        /// </summary>
        public string? Json { get; init; }

        // ── Static factory helpers ────────────────────────────────────────────
        internal static readonly ReadResult NotFound = new() { IsNotFound = true };
        internal static readonly ReadResult Symlink = new() { IsReparsePoint = true };
        internal static readonly ReadResult IoError = new() { IsIoError = true };
        internal static ReadResult Success(string json) => new() { IsSuccess = true, Json = json };
    }

    /// <summary>
    /// Executes the two-phase credential file read for <paramref name="path"/>.
    /// </summary>
    /// <param name="path">
    /// Absolute path to the credential file. Must be pre-resolved by the caller (G-7).
    /// </param>
    /// <returns>
    /// A <see cref="ReadResult"/> discriminating between not-found, reparse-point
    /// rejection, I/O error, and success (with raw JSON content).
    /// </returns>
    public static ReadResult Read(string path)
    {
        // ── Phase 1: existence + reparse-point check (single syscall) ────────
        // G-1 + G-6: GetAttributes is one syscall that both detects absence
        // (FileNotFoundException) and yields the attribute flags needed to check
        // for ReparsePoint — no separate File.Exists call that would open a TOCTOU
        // window between "does it exist?" and "is it a regular file?".
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return ReadResult.NotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return ReadResult.NotFound;
        }
        catch (Exception)
        {
            // G-11: other I/O errors (permissions, locks) — never forward ex.Message.
            return ReadResult.IoError;
        }

        // G-6: reparse-point check on the attributes retrieved above.
        // NO additional I/O between the GetAttributes call and this flag test.
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
            return ReadResult.Symlink;

        // ── Phase 2: content read ─────────────────────────────────────────────
        // G-1: this ReadAllText is the sole read of the file content.
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return ReadResult.NotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return ReadResult.NotFound;
        }
        catch (Exception)
        {
            // G-11: other I/O errors — never forward ex.Message.
            return ReadResult.IoError;
        }

        return ReadResult.Success(json);
    }
}
