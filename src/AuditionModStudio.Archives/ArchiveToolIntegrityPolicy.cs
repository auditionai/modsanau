using System.Diagnostics;
using AuditionModStudio.Core.Paths;

namespace AuditionModStudio.Archives;

public sealed class ArchiveToolIntegrityPolicy(
    IPathSecurity pathSecurity,
    TrustedArchiveToolManifest manifest) : IArchiveToolExecutionPolicy
{
    public async Task EnsureApprovedAsync(
        string toolId,
        string executablePath,
        string trustedRootDirectory,
        CancellationToken cancellationToken)
    {
        var result = await VerifyAsync(
                toolId,
                executablePath,
                trustedRootDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Approved)
        {
            throw new ArchiveToolIntegrityException(result);
        }
    }

    public async Task<ArchiveToolIntegrityResult> VerifyAsync(
        string toolId,
        string executablePath,
        string trustedRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRootDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        if (!manifest.TryGetDescriptor(toolId, out var descriptor) || !descriptor.IsApproved)
        {
            return Failure(ArchiveToolIntegrityFailureReason.UnapprovedTool, toolId, descriptor);
        }

        if (!IsValidDescriptor(descriptor))
        {
            return Failure(ArchiveToolIntegrityFailureReason.InvalidManifest, toolId, descriptor);
        }

        string trustedRoot;
        string candidate;
        try
        {
            trustedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRootDirectory));
            candidate = Path.GetFullPath(executablePath);
            if (!Path.IsPathFullyQualified(trustedRoot) || !Path.IsPathFullyQualified(candidate))
            {
                return Failure(ArchiveToolIntegrityFailureReason.OutsideTrustedLocation, toolId, descriptor);
            }

            var relative = Path.GetRelativePath(trustedRoot, candidate);
            candidate = pathSecurity.ResolvePathWithinRoot(trustedRoot, relative);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Failure(ArchiveToolIntegrityFailureReason.OutsideTrustedLocation, toolId, descriptor);
        }

        try
        {
            pathSecurity.EnsureNoReparsePoints(trustedRoot, candidate);
        }
        catch (InvalidOperationException)
        {
            return Failure(ArchiveToolIntegrityFailureReason.ReparsePointRejected, toolId, descriptor);
        }

        if (!File.Exists(candidate))
        {
            return Failure(ArchiveToolIntegrityFailureReason.ExecutableMissing, toolId, descriptor);
        }

        var filenameComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!Path.GetFileName(candidate).Equals(descriptor.ExpectedFileName, filenameComparison))
        {
            return Failure(ArchiveToolIntegrityFailureReason.FilenameMismatch, toolId, descriptor);
        }

        var actualHash = await FileSha256.ComputeAsync(candidate, cancellationToken).ConfigureAwait(false);
        var observedVersion = GetFileVersion(candidate);
        if (!FileSha256.EqualsHex(descriptor.ExpectedSha256, actualHash))
        {
            return new(
                false,
                ArchiveToolIntegrityFailureReason.HashMismatch,
                toolId,
                descriptor.ExpectedSha256,
                actualHash,
                observedVersion);
        }

        if (descriptor.ExpectedFileName.Equals("acv.exe", StringComparison.OrdinalIgnoreCase)
            && Directory.EnumerateFiles(Path.GetDirectoryName(candidate)!, "*.dll", SearchOption.TopDirectoryOnly).Any())
        {
            return Failure(ArchiveToolIntegrityFailureReason.UnexpectedCompanion, toolId, descriptor,
                actualHash, observedVersion);
        }

        return new(
            true,
            ArchiveToolIntegrityFailureReason.None,
            toolId,
            descriptor.ExpectedSha256,
            actualHash,
            observedVersion);
    }

    private static bool IsValidDescriptor(ArchiveToolDescriptor descriptor) =>
        !string.IsNullOrWhiteSpace(descriptor.ToolId)
        && !string.IsNullOrWhiteSpace(descriptor.ExpectedFileName)
        && Path.GetFileName(descriptor.ExpectedFileName).Equals(descriptor.ExpectedFileName, StringComparison.Ordinal)
        && FileSha256.IsValidHex(descriptor.ExpectedSha256);

    private static string? GetFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ArchiveToolIntegrityResult Failure(
        ArchiveToolIntegrityFailureReason reason,
        string toolId,
        ArchiveToolDescriptor? descriptor,
        string? actualHash = null,
        string? observedVersion = null) => new(
            false,
            reason,
            toolId,
            descriptor?.ExpectedSha256,
            actualHash,
            observedVersion);
}
