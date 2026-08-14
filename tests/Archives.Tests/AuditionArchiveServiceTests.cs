using System.Security.Cryptography;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;

namespace Archives.Tests;

public sealed class AuditionArchiveServiceTests
{
    [Theory]
    [InlineData("015.ab", ".ab")]
    [InlineData("021.acv", ".acv")]
    [InlineData("Sàn Audition Việt Nam.custom", ".custom")]
    public void Descriptor_treats_extension_as_metadata(string fileName, string expectedExtension)
    {
        var descriptor = CreateTemplate(fileName);

        Assert.Equal(expectedExtension, descriptor.Extension);
        Assert.Equal(Path.GetFileNameWithoutExtension(fileName), descriptor.BaseName);
        Assert.Equal(ArchiveEngineType.AcvTool5, descriptor.EngineType);
    }

    [Fact]
    public void Requests_do_not_expose_raw_tool_arguments()
    {
        var propertyNames = typeof(ArchiveExtractRequest).GetProperties()
            .Concat(typeof(ArchivePackRequest).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Argument", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("CountrySelection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Executable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Descriptor_rejects_source_metadata_for_a_different_file()
    {
        Assert.Throws<ArgumentException>(() => new AuditionArchiveTemplate(
            "sample",
            "015.ab",
            Path.Combine("templates", "other.ab"),
            ArchiveEngineType.AcvTool5,
            "audition_vn",
            "015"));
    }

    [Fact]
    public void Descriptor_rejects_malformed_expected_hash()
    {
        Assert.Throws<ArgumentException>(() => new AuditionArchiveTemplate(
            "sample",
            "015.ab",
            Path.Combine("templates", "015.ab"),
            ArchiveEngineType.AcvTool5,
            "audition_vn",
            "015",
            sha256: "not-a-sha256"));
    }

    [Theory]
    [InlineData("archive.ab")]
    [InlineData("archive.acv")]
    [InlineData("archive.future")]
    public async Task Extract_behavior_is_independent_of_archive_extension(string fileName)
    {
        await using var context = TestContext.Create(fileName);

        var result = await context.Service.ExtractAsync(context.CreateExtractRequest());

        Assert.True(result.Command.Succeeded);
        Assert.Equal(1, context.Engine.ExtractCalls);
        Assert.True(File.Exists(Path.Combine(context.Workspace.Paths.WorkingDirectory, fileName)));
    }

    [Fact]
    public async Task Unsupported_engine_returns_structured_failure()
    {
        await using var context = TestContext.Create("015.ab", registerEngine: false);

        var result = await context.Service.ExtractAsync(context.CreateExtractRequest());

        Assert.False(result.Command.Succeeded);
        Assert.Equal(ArchiveFailureReason.UnsupportedEngine, result.Command.FailureReason);
        Assert.False(File.Exists(Path.Combine(context.Workspace.Paths.WorkingDirectory, "015.ab")));
    }

    [Fact]
    public async Task Extract_copies_pristine_source_and_never_mutates_it()
    {
        await using var context = TestContext.Create("Sàn Audition 015.ab");
        var sourceHashBefore = await ComputeHashAsync(context.SourcePath);

        var result = await context.Service.ExtractAsync(context.CreateExtractRequest());

        Assert.True(result.Command.Succeeded);
        Assert.Equal(sourceHashBefore, await ComputeHashAsync(context.SourcePath));
        var workingPath = Path.Combine(context.Workspace.Paths.WorkingDirectory, "Sàn Audition 015.ab");
        Assert.True(File.Exists(workingPath));
        Assert.NotEqual(context.SourcePath, workingPath);
        Assert.Equal(sourceHashBefore, await ComputeHashAsync(workingPath));
    }

    [Fact]
    public async Task Existing_working_archive_is_not_overwritten()
    {
        await using var context = TestContext.Create("015.ab");
        var workingPath = Path.Combine(context.Workspace.Paths.WorkingDirectory, "015.ab");
        await File.WriteAllTextAsync(workingPath, "existing working data");

        var result = await context.Service.ExtractAsync(context.CreateExtractRequest());

        Assert.False(result.Command.Succeeded);
        Assert.Equal(ArchiveFailureReason.InvalidWorkspace, result.Command.FailureReason);
        Assert.Equal("existing working data", await File.ReadAllTextAsync(workingPath));
        Assert.Equal(0, context.Engine.ExtractCalls);
    }

    [Fact]
    public async Task Verified_existing_working_archive_is_reused_without_overwrite()
    {
        await using var context = TestContext.Create("015.ab");
        var workingPath = Path.Combine(context.Workspace.Paths.WorkingDirectory, "015.ab");
        File.Copy(context.SourcePath, workingPath);
        var before = await ComputeHashAsync(workingPath);

        var result = await context.Service.ExtractAsync(context.CreateExtractRequest());

        Assert.True(result.Command.Succeeded);
        Assert.Equal(1, context.Engine.ExtractCalls);
        Assert.Equal(before, await ComputeHashAsync(workingPath));
    }

    [Fact]
    public async Task Pack_resolves_engine_without_exposing_tool_commands()
    {
        await using var context = TestContext.Create("015.ab");
        await File.WriteAllBytesAsync(
            Path.Combine(context.Workspace.Paths.WorkingDirectory, "015.ab"),
            [1, 2, 3]);

        var result = await context.Service.PackAsync(new(
            context.Template,
            context.ArchiveWorkspace,
            TimeSpan.FromSeconds(5)));

        Assert.True(result.Command.Succeeded);
        Assert.Equal(1, context.Engine.PackCalls);
        Assert.Equal("015.ab", result.Command.OutputRelativePath);
    }

    [Fact]
    public async Task Paths_with_spaces_and_Vietnamese_are_preserved_inside_managed_roots()
    {
        await using var context = TestContext.Create("Kho lưu trữ Việt Nam.mod");

        var result = await context.Service.ExtractAsync(context.CreateExtractRequest());

        Assert.True(result.Command.Succeeded);
        Assert.StartsWith(
            context.Workspace.Paths.WorkingDirectory,
            Path.Combine(context.Workspace.Paths.WorkingDirectory, "Kho lưu trữ Việt Nam.mod"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("thư mục giải nén", context.Template.ExpectedExtractFolderName);
    }

    private static AuditionArchiveTemplate CreateTemplate(string fileName) => new(
        "sample_archive",
        fileName,
        Path.Combine("templates", fileName),
        ArchiveEngineType.AcvTool5,
        "audition_vn",
        "thư mục giải nén",
        "1");

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private TestContext(string fileName, bool registerEngine)
        {
            Root = Path.Combine(Path.GetTempPath(), "ArchiveAbstractionTests", Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "Pristine");
            var sourceDirectory = Path.Combine(SourceRoot, "templates");
            Directory.CreateDirectory(sourceDirectory);
            SourcePath = Path.Combine(sourceDirectory, fileName);
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4]);

            var workspaceRoot = Path.Combine(Root, "Workspace");
            Workspace = new TestWorkspace(workspaceRoot);
            Template = CreateTemplate(fileName);
            ArchiveWorkspace = ArchiveWorkspace.Create(Workspace, Template);
            Engine = new FakeArchiveEngine();
            Service = new AuditionArchiveService(
                registerEngine ? [Engine] : [],
                new PathSecurity());
        }

        public string Root { get; }

        public string SourceRoot { get; }

        public string SourcePath { get; }

        public TestWorkspace Workspace { get; }

        public AuditionArchiveTemplate Template { get; }

        public ArchiveWorkspace ArchiveWorkspace { get; }

        public FakeArchiveEngine Engine { get; }

        public AuditionArchiveService Service { get; }

        public static TestContext Create(string fileName, bool registerEngine = true) => new(fileName, registerEngine);

        public ArchiveExtractRequest CreateExtractRequest() => new(
            Template,
            ArchiveWorkspace,
            new PristineArchiveSource(SourceRoot),
            TimeSpan.FromSeconds(5));

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWorkspace : ISecureWorkspace
    {
        public TestWorkspace(string root)
        {
            Paths = new(
                root,
                Directory.CreateDirectory(Path.Combine(root, "Working")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "Extracted")).FullName,
                Directory.CreateDirectory(Path.Combine(root, "BuildOutput")).FullName);
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public SecureWorkspacePaths Paths { get; }

        public string ResolveRelativePath(string relativePath) => Path.Combine(Paths.RootDirectory, relativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeArchiveEngine : IArchiveEngine
    {
        public ArchiveEngineType EngineType => ArchiveEngineType.AcvTool5;

        public int ExtractCalls { get; private set; }

        public int PackCalls { get; private set; }

        public Task<ArchiveCommandResult> ExtractAsync(
            ArchiveExtractRequest request,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractCalls++;
            return Task.FromResult(new ArchiveCommandResult(
                ArchiveOperation.Extract,
                ArchiveOperationState.Completed,
                true,
                1,
                ArchiveFailureReason.None,
                request.Workspace.ExtractDirectoryRelativePath,
                []));
        }

        public Task<ArchiveCommandResult> PackAsync(
            ArchivePackRequest request,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackCalls++;
            return Task.FromResult(new ArchiveCommandResult(
                ArchiveOperation.Pack,
                ArchiveOperationState.Completed,
                true,
                1,
                ArchiveFailureReason.None,
                request.Workspace.WorkingArchiveRelativePath,
                []));
        }
    }
}
