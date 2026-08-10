using Archives.FakeTool;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;
using System.Security.Cryptography;

namespace Archives.Tests;

public sealed class AcvTool5RunnerTests
{
    [Fact]
    public async Task Missing_keydat_split_prompt_receives_AuditionVN_selection()
    {
        await using var context = TestRunContext.Create("Sàn Aubiz có khoảng trắng");
        File.WriteAllText(Path.Combine(context.WorkingDirectory, ".fake-split-prompt"), string.Empty);

        var result = await context.Runner.RunAsync(context.CreateRequest(AcvTool5Operation.Extract));

        Assert.True(result.Succeeded);
        Assert.Equal(AcvTool5RunnerState.Completed, result.State);
        Assert.Equal(KeydatStatus.Missing, result.KeydatStatusBefore);
        Assert.Equal(KeydatStatus.PresentUnverified, result.KeydatStatusAfter);
        Assert.True(result.CountrySelectionSent);
        Assert.Contains(result.Progress, item => item.CurrentItemPath?.EndsWith("file.dds", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(Path.Combine(context.WorkingDirectory, "mẫu 015.keydat")));
    }

    [Fact]
    public async Task Existing_keydat_completes_without_waiting_for_prompt()
    {
        await using var context = TestRunContext.Create();
        File.WriteAllText(Path.Combine(context.WorkingDirectory, "mẫu 015.keydat"), "existing");

        var result = await context.Runner.RunAsync(context.CreateRequest(AcvTool5Operation.Extract));

        Assert.True(result.Succeeded);
        Assert.Equal(KeydatStatus.PresentUnverified, result.KeydatStatusBefore);
        Assert.False(result.CountrySelectionSent);
        Assert.DoesNotContain(result.Progress, item => item.State == AcvTool5RunnerState.WaitingForCountrySelection);
    }

    [Fact]
    public async Task Pack_reports_structured_progress_and_captures_stderr_separately()
    {
        await using var context = TestRunContext.Create();
        File.WriteAllText(Path.Combine(context.WorkingDirectory, "mẫu 015.keydat"), "existing");
        File.WriteAllText(Path.Combine(context.WorkingDirectory, ".fake-stderr"), string.Empty);

        var result = await context.Runner.RunAsync(context.CreateRequest(AcvTool5Operation.Pack));

        Assert.True(result.Succeeded);
        Assert.Contains("Packing:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("controlled stderr diagnostic", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(result.Progress, item => item.Operation == AcvTool5Operation.Pack && item.CurrentItemPath is not null);
    }

    [Fact]
    public async Task Pack_with_missing_keydat_sends_selection_and_generates_companion_file()
    {
        await using var context = TestRunContext.Create();

        var result = await context.Runner.RunAsync(context.CreateRequest(AcvTool5Operation.Pack));

        Assert.True(result.Succeeded);
        Assert.True(result.CountrySelectionSent);
        Assert.Equal(KeydatStatus.Missing, result.KeydatStatusBefore);
        Assert.Equal(KeydatStatus.PresentUnverified, result.KeydatStatusAfter);
        Assert.Contains(result.Progress, item => item.Operation == AcvTool5Operation.Pack && item.CurrentItemPath is not null);
    }

    [Fact]
    public async Task Exit_code_zero_without_expected_artifacts_is_not_success()
    {
        await using var context = TestRunContext.Create();
        File.WriteAllText(Path.Combine(context.WorkingDirectory, ".fake-no-artifact"), string.Empty);

        var result = await context.Runner.RunAsync(context.CreateRequest(AcvTool5Operation.Extract));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal(AcvTool5RunnerState.Failed, result.State);
        Assert.Contains(result.Diagnostics, item => item.Contains("progress", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, item => item.Contains("extracted files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Timeout_kills_process_and_returns_timed_out_state()
    {
        await using var context = TestRunContext.Create();
        File.WriteAllText(Path.Combine(context.WorkingDirectory, ".fake-hang"), string.Empty);

        var result = await context.Runner.RunAsync(
            context.CreateRequest(AcvTool5Operation.Extract) with { Timeout = TimeSpan.FromMilliseconds(300) });

        Assert.False(result.Succeeded);
        Assert.Equal(AcvTool5RunnerState.TimedOut, result.State);
        Assert.Contains(result.Diagnostics, item => item.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_kills_process_and_returns_cancelled_state()
    {
        await using var context = TestRunContext.Create();
        File.WriteAllText(Path.Combine(context.WorkingDirectory, ".fake-hang"), string.Empty);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await context.Runner.RunAsync(
            context.CreateRequest(AcvTool5Operation.Extract),
            cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(AcvTool5RunnerState.Cancelled, result.State);
        Assert.Contains(result.Diagnostics, item => item.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Relative_executable_path_is_rejected()
    {
        await using var context = TestRunContext.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => context.Runner.RunAsync(
            context.CreateRequest(AcvTool5Operation.Extract) with { ExecutablePath = "acv.exe" }));
    }

    [Fact]
    public async Task Missing_executable_is_rejected()
    {
        await using var context = TestRunContext.Create();
        var missing = Path.Combine(context.WorkingDirectory, "missing.exe");

        await Assert.ThrowsAsync<FileNotFoundException>(() => context.Runner.RunAsync(
            context.CreateRequest(AcvTool5Operation.Extract) with { ExecutablePath = missing }));
    }

    [Fact]
    public async Task Missing_working_archive_is_rejected()
    {
        await using var context = TestRunContext.Create();
        File.Delete(context.ArchivePath);

        await Assert.ThrowsAsync<FileNotFoundException>(() => context.Runner.RunAsync(
            context.CreateRequest(AcvTool5Operation.Extract)));
    }

    [Fact]
    public async Task Executable_outside_workspace_is_rejected()
    {
        await using var context = TestRunContext.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(outside, "not executable");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => context.Runner.RunAsync(
                context.CreateRequest(AcvTool5Operation.Extract) with { ExecutablePath = outside }));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void AuditionVN_profile_maps_to_country_selection_one()
    {
        var profile = GameRegionProfile.AuditionVietnam;

        Assert.Equal("audition_vn", profile.RegionId);
        Assert.Equal("AuditionVN", profile.DisplayName);
        Assert.Equal("1", profile.AcvToolCountrySelection);
    }

    [Fact]
    public async Task Country_selection_with_control_characters_is_rejected()
    {
        await using var context = TestRunContext.Create();
        var unsafeProfile = new GameRegionProfile("test", "Test", "1\n99");

        await Assert.ThrowsAsync<ArgumentException>(() => context.Runner.RunAsync(
            context.CreateRequest(AcvTool5Operation.Extract) with { RegionProfile = unsafeProfile }));
    }

    [Fact]
    public async Task Integrity_policy_rejects_modified_tool_before_process_launch()
    {
        await using var context = TestRunContext.Create();
        await File.AppendAllTextAsync(context.ExecutablePath, "modified");

        var exception = await Assert.ThrowsAsync<ArchiveToolIntegrityException>(() =>
            context.Runner.RunAsync(context.CreateRequest(AcvTool5Operation.Extract)));

        Assert.Equal(ArchiveToolIntegrityFailureReason.HashMismatch, exception.Result.FailureReason);
        Assert.False(File.Exists(Path.Combine(context.WorkingDirectory, ".fake-launched")));
    }

    private sealed class TestRunContext : IAsyncDisposable
    {
        private TestRunContext(string rootDirectory, string executablePath)
        {
            RootDirectory = rootDirectory;
            WorkingDirectory = Path.Combine(rootDirectory, "Working");
            Directory.CreateDirectory(WorkingDirectory);
            CopyFakeTool(executablePath, WorkingDirectory);
            ExecutablePath = Path.Combine(WorkingDirectory, Path.GetFileName(executablePath));
            ArchivePath = Path.Combine(WorkingDirectory, "mẫu 015.custom");
            File.WriteAllBytes(ArchivePath, [0x01, 0x02, 0x03]);
            Workspace = new TestWorkspace(rootDirectory, WorkingDirectory);
            var pathSecurity = new PathSecurity();
            var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ExecutablePath)));
            var manifest = new TrustedArchiveToolManifest(
            [
                new ArchiveToolDescriptor(
                    ArchiveToolIds.AcvTool5,
                    Path.GetFileName(ExecutablePath),
                    expectedHash,
                    ExpectedVersion: null,
                    IsApproved: true),
            ]);
            Runner = new AcvTool5Runner(
                pathSecurity,
                new ArchiveToolIntegrityPolicy(pathSecurity, manifest),
                new KeydatService(pathSecurity));
        }

        public string RootDirectory { get; }

        public string WorkingDirectory { get; }

        public string ExecutablePath { get; }

        public string ArchivePath { get; }

        public TestWorkspace Workspace { get; }

        public AcvTool5Runner Runner { get; }

        public static TestRunContext Create(string? folderName = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                folderName ?? $"acv-runner-{Guid.NewGuid():N}");
            return new(root, ResolveFakeToolExecutable());
        }

        public AcvTool5RunRequest CreateRequest(AcvTool5Operation operation) => new(
            operation,
            Workspace,
            ExecutablePath,
            Path.GetFileName(ArchivePath),
            "thư mục extract",
            GameRegionProfile.AuditionVietnam,
            TimeSpan.FromSeconds(10));

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static string ResolveFakeToolExecutable()
        {
            var assemblyPath = typeof(Program).Assembly.Location;
            var executablePath = Path.ChangeExtension(assemblyPath, ".exe");
            Assert.True(File.Exists(executablePath), $"Fake tool executable was not found at {executablePath}.");
            return executablePath;
        }

        private static void CopyFakeTool(string executablePath, string destinationDirectory)
        {
            var sourceDirectory = Path.GetDirectoryName(executablePath)!;
            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "Archives.FakeTool*"))
            {
                File.Copy(sourcePath, Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)), overwrite: true);
            }
        }
    }

    private sealed class TestWorkspace(string rootDirectory, string workingDirectory) : ISecureWorkspace
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public SecureWorkspacePaths Paths { get; } = new(
            rootDirectory,
            workingDirectory,
            Path.Combine(rootDirectory, "Extracted"),
            Path.Combine(rootDirectory, "BuildOutput"));

        public string ResolveRelativePath(string relativePath) => Path.Combine(workingDirectory, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
