using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.Diagnostics;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Infrastructure.Logging;

public sealed class DiagnosticExportService(
    IAppPaths appPaths,
    IPathSecurity pathSecurity,
    IArchiveExportDestinationValidator destinationValidator,
    ISensitiveDataRedactor redactor,
    ILogger<DiagnosticExportService>? logger = null) : IDiagnosticExportService
{
    private const int MaximumLogFiles = 14;
    private const long MaximumLogFileBytes = 11 * 1024 * 1024;
    private const long MaximumTotalLogBytes = 154 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly ILogger<DiagnosticExportService> _logger =
        logger ?? NullLogger<DiagnosticExportService>.Instance;

    public async Task<DiagnosticExportResult> ExportAsync(
        DiagnosticExportRequest request,
        IProgress<DiagnosticExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new(DiagnosticExportPhase.Validating, 0, "DIAGNOSTIC_EXPORT_VALIDATING"));
        if (request is null || string.IsNullOrWhiteSpace(request.OutputFileName))
            return Fail(DiagnosticExportFailureReason.InvalidRequest, "DIAGNOSTIC_EXPORT_REQUEST_INVALID");
        if (request.IncludeUserContent)
            return Fail(DiagnosticExportFailureReason.UserContentNotAllowed,
                "DIAGNOSTIC_EXPORT_USER_CONTENT_NOT_ALLOWED");
        if (!ArchiveExportFileContract.TryCreate("diagnostics.zip", out var zipContract))
            return Fail(DiagnosticExportFailureReason.ExportFailed, "DIAGNOSTIC_EXPORT_CONTRACT_INVALID");

        var destinationResult = destinationValidator.Validate(new ArchiveExportDestinationRequest(
            request.OutputDirectory, request.OutputFileName, zipContract,
            ArchiveExportOverwritePolicy.RejectExisting));
        if (!destinationResult.Succeeded || destinationResult.Destination is not { } destination)
            return Fail(DiagnosticExportFailureReason.DestinationInvalid,
                "DIAGNOSTIC_EXPORT_DESTINATION_INVALID");

        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logs = CollectLogs();
            progress?.Report(new(DiagnosticExportPhase.CollectingLogs, 10,
                "DIAGNOSTIC_EXPORT_LOGS_COLLECTED"));

            temporaryPath = pathSecurity.ResolvePathWithinRoot(destination.CanonicalDirectory,
                $".{Guid.NewGuid():N}.diagnostics.tmp");
            pathSecurity.EnsureNoReparsePoints(destination.CanonicalDirectory, temporaryPath);
            progress?.Report(new(DiagnosticExportPhase.CreatingBundle, 15,
                "DIAGNOSTIC_EXPORT_BUNDLE_CREATING"));
            await CreateBundleAsync(temporaryPath, logs, progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(DiagnosticExportPhase.Promoting, 95,
                "DIAGNOSTIC_EXPORT_PROMOTING"));
            File.Move(temporaryPath, destination.FullPath, overwrite: false);
            temporaryPath = null;
            var size = new FileInfo(destination.FullPath).Length;
            progress?.Report(new(DiagnosticExportPhase.Completed, 100,
                "DIAGNOSTIC_EXPORT_COMPLETED"));
            return DiagnosticExportResult.Success(destination.FullPath, logs.Count, size);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new(DiagnosticExportPhase.Cancelled, 100,
                "DIAGNOSTIC_EXPORT_CANCELLED"));
            return DiagnosticExportResult.CancelledResult();
        }
        catch (DiagnosticExportException exception)
        {
            _logger.LogWarning("Diagnostic export rejected with {DiagnosticCode}", exception.DiagnosticCode);
            progress?.Report(new(DiagnosticExportPhase.Failed, 100, exception.DiagnosticCode));
            return Fail(exception.Reason, exception.DiagnosticCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                     or InvalidDataException or NotSupportedException
                                                     or DecoderFallbackException)
        {
            _logger.LogWarning("Diagnostic export failed with {ExceptionType}", exception.GetType().Name);
            progress?.Report(new(DiagnosticExportPhase.Failed, 100,
                "DIAGNOSTIC_EXPORT_FAILED"));
            return Fail(DiagnosticExportFailureReason.ExportFailed, "DIAGNOSTIC_EXPORT_FAILED");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Diagnostic export cleanup failed with {ExceptionType}",
                        exception.GetType().Name);
                }
            }
        }
    }

    private IReadOnlyList<FileInfo> CollectLogs()
    {
        try
        {
            if (!Directory.Exists(appPaths.LogsDirectory)) return [];
            pathSecurity.EnsureNoReparsePoints(appPaths.RootDirectory, appPaths.LogsDirectory);
            var logs = new DirectoryInfo(appPaths.LogsDirectory)
                .EnumerateFiles("audition-mod-studio-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .Take(MaximumLogFiles)
                .ToArray();
            long total = 0;
            foreach (var log in logs)
            {
                pathSecurity.EnsureNoReparsePoints(appPaths.LogsDirectory, log.FullName);
                if (log.Length is < 0 or > MaximumLogFileBytes
                    || checked(total += log.Length) > MaximumTotalLogBytes)
                    throw new DiagnosticExportException(DiagnosticExportFailureReason.LogTooLarge,
                        "DIAGNOSTIC_EXPORT_LOG_TOO_LARGE");
            }
            return logs;
        }
        catch (DiagnosticExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                     or InvalidOperationException)
        {
            throw new DiagnosticExportException(DiagnosticExportFailureReason.UnsafeLogSource,
                "DIAGNOSTIC_EXPORT_LOG_SOURCE_UNSAFE", exception);
        }
    }

    private async Task CreateBundleAsync(
        string temporaryPath,
        IReadOnlyList<FileInfo> logs,
        IProgress<DiagnosticExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        var manifestEntry = archive.CreateEntry("diagnostic-manifest.json", CompressionLevel.Optimal);
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(manifestStream, new DiagnosticManifest(
                SchemaVersion: 1,
                GeneratedAt: DateTimeOffset.UtcNow,
                ApplicationVersion: SafeApplicationVersion(),
                Runtime: RuntimeInformation.FrameworkDescription,
                OperatingSystem: OperatingSystem.IsWindows() ? "Windows" : "Unsupported",
                ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
                ExportedLogCount: logs.Count,
                UserContentIncluded: false,
                ExcludedCategories:
                [
                    "projects", "images", "templates", "archives", "workspaces",
                    "secure-template-cache", "settings", "credentials",
                ]), JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < logs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var log = logs[index];
            pathSecurity.EnsureNoReparsePoints(appPaths.LogsDirectory, log.FullName);
            var contents = await ReadBoundedTextAsync(log.FullName, cancellationToken).ConfigureAwait(false);
            if (contents.IndexOf('\0') >= 0)
                throw new DiagnosticExportException(DiagnosticExportFailureReason.UnsafeLogSource,
                    "DIAGNOSTIC_EXPORT_LOG_SOURCE_UNSAFE");
            var entry = archive.CreateEntry($"logs/log-{index + 1:D2}.log", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false), leaveOpen: false);
            await writer.WriteAsync(redactor.Redact(contents).AsMemory(), cancellationToken).ConfigureAwait(false);
            var percent = 15 + (int)Math.Round((index + 1d) / Math.Max(1, logs.Count) * 75d);
            progress?.Report(new(DiagnosticExportPhase.CreatingBundle, percent,
                "DIAGNOSTIC_EXPORT_LOG_ADDED"));
        }
    }

    private static async Task<string> ReadBoundedTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumLogFileBytes)
            throw new DiagnosticExportException(DiagnosticExportFailureReason.LogTooLarge,
                "DIAGNOSTIC_EXPORT_LOG_TOO_LARGE");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (stream.Length > MaximumLogFileBytes)
            throw new DiagnosticExportException(DiagnosticExportFailureReason.LogTooLarge,
                "DIAGNOSTIC_EXPORT_LOG_TOO_LARGE");
        return text;
    }

    private static string SafeApplicationVersion()
    {
        var version = typeof(DiagnosticExportService).Assembly.GetName().Version?.ToString(3);
        return !string.IsNullOrWhiteSpace(version) && version.Length <= 32 ? version : "0.0.0";
    }

    private static DiagnosticExportResult Fail(DiagnosticExportFailureReason reason, string code) =>
        DiagnosticExportResult.Failure(reason, code);

    private sealed record DiagnosticManifest(
        int SchemaVersion,
        DateTimeOffset GeneratedAt,
        string ApplicationVersion,
        string Runtime,
        string OperatingSystem,
        string ProcessArchitecture,
        int ExportedLogCount,
        bool UserContentIncluded,
        string[] ExcludedCategories);

    private sealed class DiagnosticExportException(
        DiagnosticExportFailureReason reason,
        string diagnosticCode,
        Exception? innerException = null) : Exception(diagnosticCode, innerException)
    {
        public DiagnosticExportFailureReason Reason { get; } = reason;
        public string DiagnosticCode { get; } = diagnosticCode;
    }
}
