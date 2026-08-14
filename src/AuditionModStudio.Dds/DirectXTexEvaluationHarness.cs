using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Dds;

public sealed class DirectXTexEvaluationHarness(
    IPathSecurity pathSecurity,
    IDdsMetadataReader metadataReader,
    DirectXTexEvaluationToolDescriptor approvedTool,
    ILogger<DirectXTexEvaluationHarness>? logger = null) : IDirectXTexEvaluationHarness
{
    private const int MaximumTexconvDimension = 16_384;
    private readonly ILogger<DirectXTexEvaluationHarness> _logger =
        logger ?? NullLogger<DirectXTexEvaluationHarness>.Instance;

    public async Task<DirectXTexEvaluationResult> RunAsync(
        DirectXTexEvaluationRequest request,
        IProgress<DirectXTexEvaluationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogInformation(
            "Starting DirectXTex evaluation operation {Operation}",
            request.Operation);
        var state = DirectXTexEvaluationState.Starting;
        Report(state);

        try
        {
            var validationFailure = ValidateRequest(request);
            if (validationFailure is not null)
            {
                return Failure(state, DirectXTexEvaluationFailureReason.InvalidRequest, validationFailure);
            }

            state = DirectXTexEvaluationState.ValidatingInput;
            Report(state);
            var input = await ValidateInputAsync(request, cancellationToken).ConfigureAwait(false);
            if (input.Failure is not null)
            {
                return input.Failure;
            }

            state = DirectXTexEvaluationState.ProvisioningTool;
            Report(state);
            var provisioned = await ProvisionToolAsync(request, cancellationToken).ConfigureAwait(false);
            if (provisioned.Failure is not null)
            {
                return provisioned.Failure;
            }

            var outputDirectory = pathSecurity.ResolvePathWithinRoot(
                request.Workspace.Paths.BuildOutputDirectory,
                request.OutputSubdirectory);
            pathSecurity.EnsureNoReparsePoints(
                request.Workspace.Paths.BuildOutputDirectory,
                outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            var outputExtension = request.Operation == DirectXTexEvaluationOperation.DecodeToPng
                ? ".png"
                : ".dds";
            var outputPath = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(input.Path) + outputExtension);
            pathSecurity.EnsureNoReparsePoints(
                request.Workspace.Paths.BuildOutputDirectory,
                outputPath);
            if (File.Exists(outputPath))
            {
                return Failure(state, DirectXTexEvaluationFailureReason.InvalidRequest, "DIRECTXTEX_OUTPUT_EXISTS");
            }

            state = DirectXTexEvaluationState.Running;
            Report(state);
            var processResult = await RunProcessAsync(
                provisioned.ToolPath!,
                provisioned.WorkingDirectory!,
                request,
                input.Path!,
                outputDirectory,
                cancellationToken).ConfigureAwait(false);
            if (processResult.TerminalFailure is not null)
            {
                return processResult.TerminalFailure;
            }

            if (processResult.ExitCode != 0)
            {
                return Failure(
                    DirectXTexEvaluationState.Failed,
                    DirectXTexEvaluationFailureReason.ToolFailed,
                    "DIRECTXTEX_TOOL_EXITED_NONZERO",
                    processResult);
            }

            state = DirectXTexEvaluationState.VerifyingOutput;
            Report(state);
            var verified = await VerifyOutputAsync(
                request,
                outputPath,
                processResult,
                cancellationToken).ConfigureAwait(false);
            if (!verified.Succeeded)
            {
                return verified;
            }

            state = DirectXTexEvaluationState.Completed;
            Report(state);
            _logger.LogInformation(
                "DirectXTex evaluation operation {Operation} completed",
                request.Operation);
            return verified with { FinalState = state };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "DirectXTex evaluation operation {Operation} was cancelled",
                request.Operation);
            return Failure(
                DirectXTexEvaluationState.Cancelled,
                DirectXTexEvaluationFailureReason.Cancelled,
                "DIRECTXTEX_CANCELLED");
        }
        catch (Win32Exception)
        {
            _logger.LogError(
                "DirectXTex evaluation operation {Operation} could not start the approved tool",
                request.Operation);
            return Failure(
                DirectXTexEvaluationState.Failed,
                DirectXTexEvaluationFailureReason.ProcessStartFailed,
                "DIRECTXTEX_PROCESS_START_FAILED");
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            _logger.LogError(
                "DirectXTex evaluation operation {Operation} failed controlled validation or I/O",
                request.Operation);
            return Failure(
                DirectXTexEvaluationState.Failed,
                DirectXTexEvaluationFailureReason.InvalidRequest,
                "DIRECTXTEX_CONTROLLED_IO_FAILURE");
        }

        void Report(DirectXTexEvaluationState nextState) =>
            progress?.Report(new(request.Operation, nextState));
    }

    private async Task<InputValidation> ValidateInputAsync(
        DirectXTexEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var inputPath = request.Workspace.ResolveRelativePath(request.InputRelativePath);
        pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.RootDirectory, inputPath);
        if (!File.Exists(inputPath))
        {
            return InputValidation.Fail(Failure(
                DirectXTexEvaluationState.ValidatingInput,
                DirectXTexEvaluationFailureReason.InputMissing,
                "DIRECTXTEX_INPUT_MISSING"));
        }

        var length = new FileInfo(inputPath).Length;
        if (length <= 0 || length > request.MaximumInputBytes)
        {
            return InputValidation.Fail(Failure(
                DirectXTexEvaluationState.ValidatingInput,
                DirectXTexEvaluationFailureReason.InputTooLarge,
                "DIRECTXTEX_INPUT_SIZE_REJECTED"));
        }

        int width;
        int height;
        DdsMetadata? ddsMetadata = null;
        if (request.Operation == DirectXTexEvaluationOperation.DecodeToPng)
        {
            if (!string.Equals(Path.GetExtension(inputPath), ".dds", StringComparison.OrdinalIgnoreCase))
            {
                return InputValidation.Fail(Failure(
                    DirectXTexEvaluationState.ValidatingInput,
                    DirectXTexEvaluationFailureReason.UnsupportedInput,
                    "DIRECTXTEX_DECODE_REQUIRES_DDS"));
            }

            var metadataResult = await metadataReader.ReadAsync(inputPath, cancellationToken).ConfigureAwait(false);
            if (metadataResult.FailureReason == DdsMetadataFailureReason.Cancelled)
            {
                return InputValidation.Fail(Failure(
                    DirectXTexEvaluationState.Cancelled,
                    DirectXTexEvaluationFailureReason.Cancelled,
                    "DIRECTXTEX_CANCELLED"));
            }

            if (!metadataResult.IsSuccess)
            {
                return InputValidation.Fail(Failure(
                    DirectXTexEvaluationState.ValidatingInput,
                    DirectXTexEvaluationFailureReason.InvalidInput,
                    "DIRECTXTEX_INVALID_DDS_INPUT"));
            }

            ddsMetadata = metadataResult.Metadata!;
            width = ddsMetadata.Width;
            height = ddsMetadata.Height;
        }
        else
        {
            if (!string.Equals(Path.GetExtension(inputPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                return InputValidation.Fail(Failure(
                    DirectXTexEvaluationState.ValidatingInput,
                    DirectXTexEvaluationFailureReason.UnsupportedInput,
                    "DIRECTXTEX_ENCODE_REQUIRES_PNG"));
            }

            var dimensions = await PngHeaderReader.ReadDimensionsAsync(inputPath, cancellationToken).ConfigureAwait(false);
            if (dimensions is null)
            {
                return InputValidation.Fail(Failure(
                    DirectXTexEvaluationState.ValidatingInput,
                    DirectXTexEvaluationFailureReason.InvalidInput,
                    "DIRECTXTEX_INVALID_PNG_INPUT"));
            }

            (width, height) = dimensions.Value;
        }

        if (!HasSafePixelCount(width, height, request.MaximumPixelCount))
        {
            return InputValidation.Fail(Failure(
                DirectXTexEvaluationState.ValidatingInput,
                DirectXTexEvaluationFailureReason.InputTooLarge,
                "DIRECTXTEX_INPUT_PIXEL_LIMIT_EXCEEDED"));
        }

        if (ddsMetadata is not null && !HasSufficientKnownPayload(ddsMetadata, length))
        {
            return InputValidation.Fail(Failure(
                DirectXTexEvaluationState.ValidatingInput,
                DirectXTexEvaluationFailureReason.InvalidInput,
                "DIRECTXTEX_DDS_PAYLOAD_REJECTED"));
        }

        return new(inputPath, null);
    }

    private async Task<ToolProvisioning> ProvisionToolAsync(
        DirectXTexEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.ToolSourcePath))
        {
            return ToolProvisioning.Fail(Failure(
                DirectXTexEvaluationState.ProvisioningTool,
                DirectXTexEvaluationFailureReason.ToolMissing,
                "DIRECTXTEX_TOOL_MISSING"));
        }

        var sourcePath = Path.GetFullPath(request.ToolSourcePath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        pathSecurity.EnsureNoReparsePoints(sourceDirectory, sourcePath);
        if (!string.Equals(Path.GetFileName(sourcePath), approvedTool.FileName, StringComparison.OrdinalIgnoreCase)
            || !await HashMatchesAsync(sourcePath, approvedTool.Sha256, cancellationToken).ConfigureAwait(false))
        {
            return ToolProvisioning.Fail(Failure(
                DirectXTexEvaluationState.ProvisioningTool,
                DirectXTexEvaluationFailureReason.ToolIntegrityMismatch,
                "DIRECTXTEX_TOOL_INTEGRITY_MISMATCH"));
        }

        var toolDirectory = pathSecurity.ResolvePathWithinRoot(
            request.Workspace.Paths.WorkingDirectory,
            "DirectXTexEvaluation");
        Directory.CreateDirectory(toolDirectory);
        pathSecurity.EnsureNoReparsePoints(request.Workspace.Paths.WorkingDirectory, toolDirectory);
        var destinationPath = pathSecurity.ResolvePathWithinRoot(toolDirectory, approvedTool.FileName);
        if (!File.Exists(destinationPath))
        {
            var temporaryPath = pathSecurity.ResolvePathWithinRoot(
                toolDirectory,
                $"{Guid.NewGuid():N}.tmp");
            try
            {
                await CopyFileAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temporaryPath, destinationPath);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    // A concurrent operation provisioned the same pinned binary first.
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        if (!await HashMatchesAsync(destinationPath, approvedTool.Sha256, cancellationToken).ConfigureAwait(false))
        {
            return ToolProvisioning.Fail(Failure(
                DirectXTexEvaluationState.ProvisioningTool,
                DirectXTexEvaluationFailureReason.ToolIntegrityMismatch,
                "DIRECTXTEX_PROVISIONED_TOOL_INTEGRITY_MISMATCH"));
        }

        return new(destinationPath, toolDirectory, null);
    }

    private async Task<ProcessResult> RunProcessAsync(
        string executablePath,
        string workingDirectory,
        DirectXTexEvaluationRequest request,
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var toolDirectory = Path.GetDirectoryName(executablePath)!;
        pathSecurity.EnsureNoReparsePoints(toolDirectory, executablePath);
        if (Directory.EnumerateFileSystemEntries(toolDirectory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(executablePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            || !await HashMatchesAsync(executablePath, approvedTool.Sha256, cancellationToken).ConfigureAwait(false))
        {
            return ProcessResult.Fail(Failure(
                DirectXTexEvaluationState.Failed,
                DirectXTexEvaluationFailureReason.ToolIntegrityMismatch,
                "DIRECTXTEX_PRELAUNCH_TOOL_INTEGRITY_MISMATCH"));
        }

        using var process = new Process
        {
            StartInfo = DirectXTexCommandBuilder.Create(
                executablePath,
                workingDirectory,
                request,
                inputPath,
                outputDirectory),
        };

        WindowsProcessLaunchHardening.ApplyProcessDllPolicy();
        if (!process.Start())
        {
            return ProcessResult.Fail(Failure(
                DirectXTexEvaluationState.Failed,
                DirectXTexEvaluationFailureReason.ProcessStartFailed,
                "DIRECTXTEX_PROCESS_START_FAILED"));
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, request.MaximumDiagnosticCharacters);
        var stderrTask = ReadBoundedAsync(process.StandardError, request.MaximumDiagnosticCharacters);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var cancelled = cancellationToken.IsCancellationRequested;
            return ProcessResult.Fail(Failure(
                cancelled ? DirectXTexEvaluationState.Cancelled : DirectXTexEvaluationState.TimedOut,
                cancelled
                    ? DirectXTexEvaluationFailureReason.Cancelled
                    : DirectXTexEvaluationFailureReason.TimedOut,
                cancelled ? "DIRECTXTEX_CANCELLED" : "DIRECTXTEX_TIMED_OUT"));
        }

        var captures = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new(process.ExitCode, captures[0].Text, captures[1].Text, captures.Any(item => item.Truncated), null);
    }

    private async Task<DirectXTexEvaluationResult> VerifyOutputAsync(
        DirectXTexEvaluationRequest request,
        string outputPath,
        ProcessResult process,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            return Failure(
                DirectXTexEvaluationState.Failed,
                DirectXTexEvaluationFailureReason.OutputMissing,
                "DIRECTXTEX_OUTPUT_MISSING",
                process);
        }

        var outputRelativePath = Path.GetRelativePath(request.Workspace.Paths.RootDirectory, outputPath);
        if (request.Operation == DirectXTexEvaluationOperation.DecodeToPng)
        {
            var dimensions = await PngHeaderReader.ReadDimensionsAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (dimensions is null || !HasSafePixelCount(dimensions.Value.Width, dimensions.Value.Height, request.MaximumPixelCount))
            {
                return Failure(
                    DirectXTexEvaluationState.Failed,
                    DirectXTexEvaluationFailureReason.OutputInvalid,
                    "DIRECTXTEX_INVALID_PNG_OUTPUT",
                    process);
            }

            return Success(
                process,
                outputRelativePath,
                dimensions.Value.Width,
                dimensions.Value.Height,
                null);
        }

        var metadataResult = await metadataReader.ReadAsync(outputPath, cancellationToken).ConfigureAwait(false);
        var metadata = metadataResult.Metadata;
        if (!metadataResult.IsSuccess
            || metadata is null
            || metadata.Width != request.TargetWidth
            || metadata.Height != request.TargetHeight
            || metadata.Format != request.TargetFormat
            || metadata.EffectiveMipLevelCount != request.MipLevelCount
            || metadata.HeaderType != request.TargetHeaderType
            || request.TargetHeaderType == DdsHeaderType.Dx10
                && metadata.ColorSpace != request.TargetColorSpace
            || metadata.ResourceDimension != DdsResourceDimension.Texture2D
            || metadata.IsCubemap
            || metadata.ArraySize != 1)
        {
            return Failure(
                DirectXTexEvaluationState.Failed,
                DirectXTexEvaluationFailureReason.OutputInvalid,
                "DIRECTXTEX_DDS_OUTPUT_MISMATCH",
                process);
        }

        return Success(process, outputRelativePath, metadata.Width, metadata.Height, metadata);
    }

    private static string? ValidateRequest(DirectXTexEvaluationRequest request)
    {
        if (request.Workspace is null
            || string.IsNullOrWhiteSpace(request.ToolSourcePath)
            || !Path.IsPathFullyQualified(request.ToolSourcePath)
            || string.IsNullOrWhiteSpace(request.InputRelativePath)
            || string.IsNullOrWhiteSpace(request.OutputSubdirectory)
            || request.Timeout <= TimeSpan.Zero
            || request.Timeout == Timeout.InfiniteTimeSpan
            || request.MaximumInputBytes is < 1 or > 2_147_483_648
            || request.MaximumPixelCount is < 1 or > 268_435_456
            || request.MaximumDiagnosticCharacters is < 1024 or > 1_048_576)
        {
            return "DIRECTXTEX_INVALID_REQUEST";
        }

        if (request.Operation != DirectXTexEvaluationOperation.DecodeToPng)
        {
            if (request.TargetWidth is null or <= 0 or > MaximumTexconvDimension
                || request.TargetHeight is null or <= 0 or > MaximumTexconvDimension
                || !HasSafePixelCount(request.TargetWidth.Value, request.TargetHeight.Value, request.MaximumPixelCount)
                || request.MipLevelCount <= 0
                || request.MipLevelCount > CalculateMaximumMipLevels(request.TargetWidth.Value, request.TargetHeight.Value)
                || request.TargetFormat is not (DdsFormat.BC1 or DdsFormat.BC3 or DdsFormat.Rgba8 or DdsFormat.Bgra8)
                || request.TargetColorSpace is not (DdsColorSpace.Linear or DdsColorSpace.Srgb)
                || request.TargetHeaderType == DdsHeaderType.Legacy && request.TargetColorSpace == DdsColorSpace.Srgb
                || request.TargetFormat == DdsFormat.BC1
                    && request.TargetAlphaSemantics == DdsTargetAlphaSemantics.Full)
            {
                return "DIRECTXTEX_INVALID_ENCODE_SETTINGS";
            }
        }

        return null;
    }

    private static int CalculateMaximumMipLevels(int width, int height)
    {
        var levels = 1;
        var maximum = Math.Max(width, height);
        while (maximum > 1)
        {
            maximum /= 2;
            levels++;
        }

        return levels;
    }

    private static bool HasSafePixelCount(int width, int height, long maximumPixelCount)
    {
        try
        {
            return checked((long)width * height) <= maximumPixelCount;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool HasSufficientKnownPayload(DdsMetadata metadata, long fileLength)
    {
        if (metadata.ResourceDimension != DdsResourceDimension.Texture2D
            || metadata.IsCubemap
            || metadata.ArraySize != 1)
        {
            return false;
        }

        var blockBytes = metadata.Format switch
        {
            DdsFormat.BC1 or DdsFormat.BC4 => 8,
            DdsFormat.BC2 or DdsFormat.BC3 or DdsFormat.BC5 or DdsFormat.BC6H or DdsFormat.BC7 => 16,
            _ => 0,
        };

        try
        {
            long payloadBytes = 0;
            var width = metadata.Width;
            var height = metadata.Height;
            for (var level = 0u; level < metadata.EffectiveMipLevelCount; level++)
            {
                if (blockBytes != 0)
                {
                    var blockWidth = Math.Max(1, checked((width + 3) / 4));
                    var blockHeight = Math.Max(1, checked((height + 3) / 4));
                    payloadBytes = checked(payloadBytes + checked((long)blockWidth * blockHeight * blockBytes));
                }
                else if (metadata.Format is DdsFormat.Rgba8 or DdsFormat.Bgra8)
                {
                    payloadBytes = checked(payloadBytes + checked((long)width * height * 4));
                }
                else
                {
                    return false;
                }

                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            return checked((long)metadata.PixelDataOffset + payloadBytes) <= fileLength;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static async Task<bool> HashMatchesAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<Capture> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                return new(builder.ToString(), truncated);
            }

            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, count));
            }

            truncated |= count > remaining;
        }
    }

    private static DirectXTexEvaluationResult Success(
        ProcessResult process,
        string outputRelativePath,
        int width,
        int height,
        DdsMetadata? metadata) => new(
            true,
            DirectXTexEvaluationState.VerifyingOutput,
            DirectXTexEvaluationFailureReason.None,
            null,
            process.ExitCode,
            outputRelativePath,
            width,
            height,
            metadata,
            process.StandardOutput,
            process.StandardError,
            process.DiagnosticsTruncated);

    private static DirectXTexEvaluationResult Failure(
        DirectXTexEvaluationState state,
        DirectXTexEvaluationFailureReason reason,
        string errorCode,
        ProcessResult? process = null) => new(
            false,
            state,
            reason,
            errorCode,
            process?.ExitCode,
            null,
            null,
            null,
            null,
            process?.StandardOutput ?? string.Empty,
            process?.StandardError ?? string.Empty,
            process?.DiagnosticsTruncated ?? false);

    private sealed record InputValidation(string? Path, DirectXTexEvaluationResult? Failure)
    {
        public static InputValidation Fail(DirectXTexEvaluationResult failure) => new(null, failure);
    }

    private sealed record ToolProvisioning(
        string? ToolPath,
        string? WorkingDirectory,
        DirectXTexEvaluationResult? Failure)
    {
        public static ToolProvisioning Fail(DirectXTexEvaluationResult failure) => new(null, null, failure);
    }

    private sealed record ProcessResult(
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        bool DiagnosticsTruncated,
        DirectXTexEvaluationResult? TerminalFailure)
    {
        public static ProcessResult Fail(DirectXTexEvaluationResult failure) =>
            new(null, string.Empty, string.Empty, false, failure);
    }

    private sealed record Capture(string Text, bool Truncated);
}
