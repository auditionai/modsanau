using System.Diagnostics;
using AuditionModStudio.Core.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuditionModStudio.Archives;

public sealed class AcvTool5Runner(
    IPathSecurity pathSecurity,
    IArchiveToolExecutionPolicy executionPolicy,
    ILogger<AcvTool5Runner>? logger = null) : IArchiveToolRunner
{
    private readonly ILogger<AcvTool5Runner> _logger = logger ?? NullLogger<AcvTool5Runner>.Instance;

    public async Task<AcvTool5RunResult> RunAsync(
        AcvTool5RunRequest request,
        IProgress<AcvTool5Progress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var context = ResolveContext(request);
        await executionPolicy.EnsureApprovedAsync(context.ExecutablePath, cancellationToken).ConfigureAwait(false);

        var operationState = request.Operation == AcvTool5Operation.Extract
            ? AcvTool5RunnerState.Extracting
            : AcvTool5RunnerState.Packing;
        var stateMachine = new AcvTool5StateMachine(request.Operation);
        var progressItems = new List<AcvTool5Progress>();
        var diagnostics = new List<string>();
        var stdout = new BoundedTextCapture(request.MaximumDiagnosticCharacters);
        var stderr = new BoundedTextCapture(request.MaximumDiagnosticCharacters);
        var parser = new AcvTool5OutputParser();
        var keydatBefore = File.Exists(context.KeydatPath) ? KeydatStatus.Present : KeydatStatus.Missing;
        var selectionSent = 0;
        var processedItems = 0;

        void Report(AcvTool5RunnerState nextState, string? currentItem = null)
        {
            if (stateMachine.Current != nextState)
            {
                stateMachine.TransitionTo(nextState);
            }

            var item = new AcvTool5Progress(request.Operation, nextState, currentItem, processedItems);
            progressItems.Add(item);
            progress?.Report(item);
        }

        var startingProgress = new AcvTool5Progress(
            request.Operation,
            AcvTool5RunnerState.Starting,
            null,
            processedItems);
        progressItems.Add(startingProgress);
        progress?.Report(startingProgress);
        _logger.LogInformation("Starting ACV Tool 5 {Operation} operation in an isolated workspace", request.Operation);

        using var process = new Process
        {
            StartInfo = AcvTool5CommandBuilder.Create(
                context.ExecutablePath,
                context.WorkingDirectory,
                request.Operation,
                context.ArchiveArgument,
                context.ExtractDirectoryArgument),
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("The archive tool process could not be started.");
        }

        Report(operationState);

        async Task HandleEventsAsync(IReadOnlyList<AcvTool5ParsedEvent> parsedEvents)
        {
            foreach (var parsedEvent in parsedEvents)
            {
                switch (parsedEvent.Kind)
                {
                    case AcvTool5ParsedEventKind.CountrySelectionRequested
                        when keydatBefore == KeydatStatus.Missing
                             && Interlocked.CompareExchange(ref selectionSent, 1, 0) == 0:
                        Report(AcvTool5RunnerState.WaitingForCountrySelection);
                        await process.StandardInput.WriteLineAsync(request.RegionProfile.AcvToolCountrySelection)
                            .ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                        Report(operationState);
                        break;

                    case AcvTool5ParsedEventKind.ExtractItem
                        when request.Operation == AcvTool5Operation.Extract:
                        processedItems++;
                        Report(operationState, parsedEvent.Value);
                        break;

                    case AcvTool5ParsedEventKind.PackItem
                        when request.Operation == AcvTool5Operation.Pack:
                        processedItems++;
                        Report(operationState, parsedEvent.Value);
                        break;

                    case AcvTool5ParsedEventKind.KnownError:
                        diagnostics.Add("ACV Tool 5 reported an error in standard output.");
                        break;
                }
            }
        }

        async Task ReadStandardOutputAsync()
        {
            var buffer = new char[4096];
            while (true)
            {
                var read = await process.StandardOutput.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                stdout.Append(buffer.AsSpan(0, read));
                await HandleEventsAsync(parser.Feed(buffer.AsSpan(0, read))).ConfigureAwait(false);
            }

            await HandleEventsAsync(parser.Complete()).ConfigureAwait(false);
        }

        async Task ReadStandardErrorAsync()
        {
            var buffer = new char[4096];
            while (true)
            {
                var read = await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                stderr.Append(buffer.AsSpan(0, read));
            }
        }

        var stdoutTask = ReadStandardOutputAsync();
        var stderrTask = ReadStandardErrorAsync();
        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var combinedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(combinedSource.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var terminalState = cancellationToken.IsCancellationRequested
                ? AcvTool5RunnerState.Cancelled
                : AcvTool5RunnerState.TimedOut;
            TerminateProcessTree(process, diagnostics);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            Report(terminalState);
            diagnostics.Add(terminalState == AcvTool5RunnerState.Cancelled
                ? "The archive operation was cancelled by the caller."
                : "The archive operation exceeded its configured timeout.");
            _logger.LogWarning("ACV Tool 5 {Operation} ended in state {State}", request.Operation, terminalState);
            return CreateResult(
                request.Operation,
                stateMachine.Current,
                process.ExitCode,
                stdout,
                stderr,
                keydatBefore,
                context.KeydatPath,
                selectionSent,
                progressItems,
                diagnostics);
        }
        catch
        {
            TerminateProcessTree(process, diagnostics);
            throw;
        }

        Report(AcvTool5RunnerState.VerifyingArtifacts);
        VerifyResult(context, request.Operation, process.ExitCode, processedItems, keydatBefore, diagnostics);
        Report(diagnostics.Count == 0 ? AcvTool5RunnerState.Completed : AcvTool5RunnerState.Failed);

        _logger.Log(
            stateMachine.Current == AcvTool5RunnerState.Completed ? LogLevel.Information : LogLevel.Error,
            "ACV Tool 5 {Operation} finished with state {State}, exit code {ExitCode}, and {ProcessedItemCount} progress items",
            request.Operation,
            stateMachine.Current,
            process.ExitCode,
            processedItems);

        return CreateResult(
            request.Operation,
            stateMachine.Current,
            process.ExitCode,
            stdout,
            stderr,
            keydatBefore,
            context.KeydatPath,
            selectionSent,
            progressItems,
            diagnostics);
    }

    private static void ValidateRequest(AcvTool5RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Workspace);
        ArgumentNullException.ThrowIfNull(request.RegionProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArchiveRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExtractDirectoryRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionProfile.RegionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionProfile.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionProfile.AcvToolCountrySelection);

        if (request.RegionProfile.AcvToolCountrySelection.Length > 3
            || request.RegionProfile.AcvToolCountrySelection.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("The country selection must contain only one to three ASCII digits.", nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
        {
            throw new ArgumentException("The archive tool executable path must be absolute.", nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A finite positive timeout is required.");
        }

        if (request.MaximumDiagnosticCharacters is < 1024 or > 10_485_760)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The diagnostic capture limit is outside the supported range.");
        }
    }

    private ResolvedRunContext ResolveContext(AcvTool5RunRequest request)
    {
        var workingDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.Workspace.Paths.WorkingDirectory));
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("The secure workspace working directory does not exist.");
        }

        var executablePath = Path.GetFullPath(request.ExecutablePath);
        var executableRelativePath = Path.GetRelativePath(workingDirectory, executablePath);
        var approvedExecutablePath = pathSecurity.ResolvePathWithinRoot(workingDirectory, executableRelativePath);
        pathSecurity.EnsureNoReparsePoints(workingDirectory, approvedExecutablePath);
        if (!File.Exists(approvedExecutablePath))
        {
            throw new FileNotFoundException("The archive tool executable was not found in the secure workspace.", approvedExecutablePath);
        }

        var archivePath = pathSecurity.ResolvePathWithinRoot(workingDirectory, request.ArchiveRelativePath);
        pathSecurity.EnsureNoReparsePoints(workingDirectory, archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The working archive was not found.", archivePath);
        }

        var extractDirectoryPath = pathSecurity.ResolvePathWithinRoot(
            workingDirectory,
            request.ExtractDirectoryRelativePath);
        pathSecurity.EnsureNoReparsePoints(workingDirectory, extractDirectoryPath);

        var archiveArgument = Path.GetRelativePath(workingDirectory, archivePath);
        var extractDirectoryArgument = Path.GetRelativePath(workingDirectory, extractDirectoryPath);
        var keydatPath = Path.Combine(
            Path.GetDirectoryName(archivePath)!,
            $"{Path.GetFileNameWithoutExtension(archivePath)}.keydat");
        pathSecurity.EnsureNoReparsePoints(workingDirectory, keydatPath);

        return new(
            workingDirectory,
            approvedExecutablePath,
            archivePath,
            extractDirectoryPath,
            keydatPath,
            archiveArgument,
            extractDirectoryArgument);
    }

    private static void VerifyResult(
        ResolvedRunContext context,
        AcvTool5Operation operation,
        int exitCode,
        int processedItems,
        KeydatStatus keydatBefore,
        ICollection<string> diagnostics)
    {
        if (exitCode != 0)
        {
            diagnostics.Add($"The archive tool exited with code {exitCode}.");
        }

        if (keydatBefore == KeydatStatus.Missing && !File.Exists(context.KeydatPath))
        {
            diagnostics.Add("The expected workspace keydat was not generated.");
        }

        if (processedItems == 0)
        {
            diagnostics.Add(operation == AcvTool5Operation.Extract
                ? "No extract progress item was observed."
                : "No pack progress item was observed.");
        }

        if (operation == AcvTool5Operation.Extract)
        {
            if (!Directory.Exists(context.ExtractDirectoryPath)
                || !Directory.EnumerateFiles(context.ExtractDirectoryPath, "*", SearchOption.AllDirectories).Any())
            {
                diagnostics.Add("The expected extracted files were not produced.");
            }
        }
        else if (!File.Exists(context.ArchivePath) || new FileInfo(context.ArchivePath).Length == 0)
        {
            diagnostics.Add("The packed working archive is missing or empty.");
        }
    }

    private static void TerminateProcessTree(Process process, ICollection<string> diagnostics)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
        {
            diagnostics.Add("The child process tree could not be terminated by the platform.");
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    private static AcvTool5RunResult CreateResult(
        AcvTool5Operation operation,
        AcvTool5RunnerState state,
        int exitCode,
        BoundedTextCapture stdout,
        BoundedTextCapture stderr,
        KeydatStatus keydatBefore,
        string keydatPath,
        int selectionSent,
        IReadOnlyList<AcvTool5Progress> progress,
        IReadOnlyList<string> diagnostics) => new(
            operation,
            state,
            state == AcvTool5RunnerState.Completed,
            exitCode,
            stdout.ToString(),
            stderr.ToString(),
            stdout.IsTruncated,
            stderr.IsTruncated,
            keydatBefore,
            File.Exists(keydatPath) ? KeydatStatus.Present : KeydatStatus.Missing,
            selectionSent != 0,
            progress,
            diagnostics);

    private sealed record ResolvedRunContext(
        string WorkingDirectory,
        string ExecutablePath,
        string ArchivePath,
        string ExtractDirectoryPath,
        string KeydatPath,
        string ArchiveArgument,
        string ExtractDirectoryArgument);
}
