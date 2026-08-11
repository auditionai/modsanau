using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Exports;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Exports;
using AuditionModStudio.Infrastructure.Paths;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ArchiveExportServiceTests
{
    [Fact]
    public async Task Validated_build_artifact_is_exported_byte_identically()
    {
        await using var context = new Context();
        var progress = new List<ArchiveExportPhase>();
        var result = await context.Service.ExportAsync(
            context.Request(), new CallbackProgress<ArchiveExportProgress>(item => progress.Add(item.Phase)));

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(context.SourceBytes, await File.ReadAllBytesAsync(context.DestinationPath));
        Assert.Equal(context.SourceHash, result.Sha256!.Value.Value);
        Assert.Equal(context.SourceBytes.Length, result.Size);
        Assert.Equal(ArchiveExportPhase.Completed, progress[^1]);
        Assert.Empty(context.TransactionArtifacts());
    }

    [Fact]
    public async Task Existing_destination_is_preserved_without_explicit_overwrite()
    {
        await using var context = new Context();
        await File.WriteAllTextAsync(context.DestinationPath, "old");
        var result = await context.Service.ExportAsync(context.Request());
        Assert.Equal(ArchiveExportFailureReason.DestinationInvalid, result.FailureReason);
        Assert.Equal("old", await File.ReadAllTextAsync(context.DestinationPath));
    }

    [Fact]
    public async Task Explicit_overwrite_atomically_replaces_existing_destination()
    {
        await using var context = new Context();
        await File.WriteAllTextAsync(context.DestinationPath, "old");
        var result = await context.Service.ExportAsync(
            context.Request(ArchiveExportOverwritePolicy.ReplaceExisting));
        Assert.True(result.Succeeded);
        Assert.Equal(context.SourceBytes, await File.ReadAllBytesAsync(context.DestinationPath));
        Assert.Empty(context.TransactionArtifacts());
    }

    [Fact]
    public async Task Non_succeeded_build_state_is_rejected()
    {
        await using var context = new Context(ProjectBuildStatus.Dirty);
        var result = await context.Service.ExportAsync(context.Request());
        Assert.Equal(ArchiveExportFailureReason.BuildArtifactUnavailable, result.FailureReason);
        Assert.False(File.Exists(context.DestinationPath));
    }

    [Fact]
    public async Task Changed_build_artifact_is_rejected_before_copy()
    {
        await using var context = new Context();
        await File.AppendAllTextAsync(context.SourcePath, "tampered");
        var result = await context.Service.ExportAsync(context.Request());
        Assert.Equal(ArchiveExportFailureReason.BuildArtifactInvalid, result.FailureReason);
        Assert.False(File.Exists(context.DestinationPath));
    }

    [Fact]
    public async Task Candidate_hash_mismatch_preserves_existing_destination()
    {
        await using var context = new Context(fileOperations: new HashFaultOperations(HashFault.Candidate));
        await File.WriteAllTextAsync(context.DestinationPath, "old");
        var result = await context.Service.ExportAsync(
            context.Request(ArchiveExportOverwritePolicy.ReplaceExisting));
        Assert.Equal(ArchiveExportFailureReason.VerificationFailed, result.FailureReason);
        Assert.Equal("old", await File.ReadAllTextAsync(context.DestinationPath));
        Assert.Empty(context.TransactionArtifacts());
    }

    [Fact]
    public async Task Final_verification_failure_rolls_back_exact_previous_bytes()
    {
        var operations = new HashFaultOperations(HashFault.Final);
        await using var context = new Context(fileOperations: operations);
        var previous = Encoding.UTF8.GetBytes("previous archive bytes");
        await File.WriteAllBytesAsync(context.DestinationPath, previous);
        operations.FinalPath = context.DestinationPath;

        var result = await context.Service.ExportAsync(
            context.Request(ArchiveExportOverwritePolicy.ReplaceExisting));

        Assert.Equal(ArchiveExportFailureReason.VerificationFailed, result.FailureReason);
        Assert.Equal(previous, await File.ReadAllBytesAsync(context.DestinationPath));
        Assert.Empty(context.TransactionArtifacts());
    }

    [Fact]
    public async Task Rollback_failure_is_reported_and_recovery_backup_is_retained()
    {
        var operations = new HashFaultOperations(HashFault.Final, failRollback: true);
        await using var context = new Context(fileOperations: operations);
        await File.WriteAllTextAsync(context.DestinationPath, "previous");
        operations.FinalPath = context.DestinationPath;

        var result = await context.Service.ExportAsync(
            context.Request(ArchiveExportOverwritePolicy.ReplaceExisting));

        Assert.Equal(ArchiveExportFailureReason.RollbackFailed, result.FailureReason);
        Assert.Contains(context.TransactionArtifacts(), path => path.EndsWith(".export.backup"));
    }

    [Fact]
    public async Task Cancellation_before_operation_creates_no_destination()
    {
        await using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await context.Service.ExportAsync(context.Request(), cancellationToken: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.False(File.Exists(context.DestinationPath));
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _root;

        public Context(
            ProjectBuildStatus status = ProjectBuildStatus.Succeeded,
            IArchiveExportFileOperations? fileOperations = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveExportServiceTests", Guid.NewGuid().ToString("N"));
            var paths = new SecureWorkspacePaths(
                Path.Combine(_root, "Project"),
                Path.Combine(_root, "Project", "Working"),
                Path.Combine(_root, "Project", "Extracted"),
                Path.Combine(_root, "Project", "BuildOutput"));
            Directory.CreateDirectory(paths.WorkingDirectory);
            Directory.CreateDirectory(paths.ExtractedDirectory);
            Directory.CreateDirectory(paths.BuildOutputDirectory);
            DestinationDirectory = Path.Combine(_root, "Exports Đích");
            Directory.CreateDirectory(DestinationDirectory);
            DestinationPath = Path.Combine(DestinationDirectory, "Final 015.ab");

            var secure = new TestSecureWorkspace("0123456789abcdef0123456789abcdef", paths);
            var template = new AuditionArchiveTemplate(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");
            var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Workspace = new TestProjectWorkspace(secure, template, projectId);
            SourcePath = secure.ResolveRelativePath("BuildOutput/Output/015.ab");
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            SourceBytes = Encoding.UTF8.GetBytes("validated final build artifact");
            File.WriteAllBytes(SourcePath, SourceBytes);
            SourceHash = Convert.ToHexString(SHA256.HashData(SourceBytes));
            var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
            Project = AuditionProject.Create(
                1, projectId, "Project", new GameId("audition"), new ModId("login_mod"),
                Workspace.Descriptor.ArchiveTemplate.Identity,
                new(Workspace.Descriptor.WorkspaceId, new("Working/015.ab"), new("Extracted/015")),
                [], [], [], new(0, 0, null, []),
                status == ProjectBuildStatus.Succeeded
                    ? new(status, now, new("BuildOutput/Output/015.ab"), new(SourceHash))
                    : new(status, null, null, null),
                now, now).Project!;

            var pathSecurity = new PathSecurity();
            var destinationValidator = new ArchiveExportDestinationValidator(
                pathSecurity, new SystemExportDestinationFileSystem());
            Service = new(destinationValidator, pathSecurity,
                fileOperations ?? new SystemArchiveExportFileOperations());
        }

        public byte[] SourceBytes { get; }
        public string SourceHash { get; }
        public string SourcePath { get; }
        public string DestinationDirectory { get; }
        public string DestinationPath { get; }
        public AuditionProject Project { get; }
        public TestProjectWorkspace Workspace { get; }
        public ArchiveExportService Service { get; }

        public ArchiveExportRequest Request(
            ArchiveExportOverwritePolicy policy = ArchiveExportOverwritePolicy.RejectExisting) =>
            new(Project, Workspace, DestinationDirectory, "Final 015.ab", policy);

        public string[] TransactionArtifacts() => Directory
            .EnumerateFiles(DestinationDirectory, ".*.export.*")
            .ToArray();

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private enum HashFault { Candidate, Final }

    private sealed class HashFaultOperations(HashFault fault, bool failRollback = false)
        : IArchiveExportFileOperations
    {
        private readonly SystemArchiveExportFileOperations _inner = new();
        private int _replaceCount;
        public string? FinalPath { get; set; }
        public bool FileExists(string path) => _inner.FileExists(path);
        public long GetFileLength(string path) => _inner.GetFileLength(path);
        public Task CopyDurablyAsync(string sourcePath, string destinationPath, CancellationToken token) =>
            ((IArchiveExportFileOperations)_inner).CopyDurablyAsync(sourcePath, destinationPath, token);
        public Task<string> ComputeSha256Async(string path, CancellationToken token) =>
            (fault == HashFault.Candidate && path.EndsWith(".export.tmp", StringComparison.Ordinal)
                || fault == HashFault.Final && string.Equals(path, FinalPath, StringComparison.OrdinalIgnoreCase))
                    ? Task.FromResult(new string('0', 64))
                    : _inner.ComputeSha256Async(path, token);
        public void Move(string sourcePath, string destinationPath) => _inner.Move(sourcePath, destinationPath);
        public void Replace(string sourcePath, string destinationPath, string backupPath)
        {
            _replaceCount++;
            if (failRollback && _replaceCount > 1)
            {
                throw new IOException("Simulated rollback failure.");
            }
            _inner.Replace(sourcePath, destinationPath, backupPath);
        }
        public void Delete(string path) => _inner.Delete(path);
    }

    private sealed class TestProjectWorkspace : IProjectArchiveWorkspace
    {
        public TestProjectWorkspace(ISecureWorkspace secure, AuditionArchiveTemplate template, Guid projectId)
        {
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(1, projectId, "Project", secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                "Working/015.ab", "Extracted/015", "BuildOutput", null,
                ".project-archive-workspace.json", new string('B', 64), now, now,
                ProjectArchiveWorkspaceState.Ready);
        }
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecureWorkspace(string id, SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id { get; } = id;
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath)
        {
            var path = Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
            var prefix = Path.TrimEndingDirectorySeparator(Paths.RootDirectory) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException();
            }
            return path;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
