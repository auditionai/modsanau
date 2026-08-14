using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class ProjectValidatorTests
{
    [Fact]
    public async Task Valid_project_returns_buildable_structured_info()
    {
        await using var context = new Context();

        var result = await context.ValidateAsync();

        Assert.True(result.CanBuild);
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains(result.Issues,
            issue => issue is { Severity: ProjectValidationSeverity.Info, Kind: ProjectValidationIssueKind.ValidatedTexture });
    }

    [Fact]
    public async Task Platform_separators_in_workspace_descriptor_match_normalized_project_paths()
    {
        await using var context = new Context(descriptorUsesBackslashes: true);

        var result = await context.ValidateAsync();

        Assert.True(result.CanBuild);
        Assert.DoesNotContain(result.Issues,
            issue => issue.DiagnosticCode == "PROJECT_VALIDATE_WORKSPACE_REFERENCE_INVALID");
    }

    [Fact]
    public async Task Missing_archive_folder_and_expected_file_are_structured_errors()
    {
        await using var context = new Context(includeTexture: false);
        File.Delete(context.ArchivePath);
        Directory.Delete(context.Paths.BuildOutputDirectory);

        var result = await context.ValidateAsync();

        Assert.False(result.CanBuild);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.MissingArchive);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.MissingFolder);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.MissingFile);
    }

    [Fact]
    public async Task Dimension_format_malformed_and_unexpected_filename_are_distinguished()
    {
        await using var context = new Context(
            actualMetadata: Metadata() with { Width = 4, Format = DdsFormat.BC1 },
            includeUnexpectedTexture: true);
        context.MetadataReader.MalformedFileName = "bad.dds";

        var result = await context.ValidateAsync();

        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.WrongDimensions);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.WrongDdsFormat);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.WrongFilename);

        context.Scanner.Catalog = Catalog("Texture/bad.dds");
        context.Cache.LoadResult = ProjectMetadataCacheLoadResult.Success([
            Snapshot("Texture/bad.dds")
        ]);
        result = await context.ValidateAsync();
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.MalformedDds);
    }

    [Fact]
    public async Task Pending_edit_and_tool_integrity_issue_block_build_without_mutation()
    {
        await using var context = new Context(savedRevision: 0, currentRevision: 1);
        context.Tool.Result = new(ProjectToolIntegrityStatus.Invalid, "TEST_TOOL_INVALID");
        var before = context.Project.EditState;

        var result = await context.ValidateAsync();

        Assert.False(result.CanBuild);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.PendingEdit);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.ToolIntegrityIssue);
        Assert.Same(before, context.Project.EditState);
    }

    [Fact]
    public async Task Missing_metadata_blocks_build_but_readable_dds_can_still_be_inspected()
    {
        await using var context = new Context();
        context.Cache.LoadResult = ProjectMetadataCacheLoadResult.Failure(
            ProjectMetadataCacheLoadStatus.Missing, "TEST_METADATA_MISSING");

        var result = await context.ValidateAsync();

        Assert.False(result.CanBuild);
        Assert.Equal(1, result.ErrorCount);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.MetadataBaselineUnavailable);
        Assert.Contains(result.Issues, issue => issue.Kind == ProjectValidationIssueKind.ValidatedTexture);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _root;

        public Context(
            DdsMetadata? actualMetadata = null,
            bool includeTexture = true,
            bool includeUnexpectedTexture = false,
            long savedRevision = 0,
            long currentRevision = 0,
            bool descriptorUsesBackslashes = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "ProjectValidatorTests", Guid.NewGuid().ToString("N"));
            Paths = new(
                _root,
                Path.Combine(_root, "Working"),
                Path.Combine(_root, "Extracted"),
                Path.Combine(_root, "BuildOutput"));
            Directory.CreateDirectory(Paths.WorkingDirectory);
            Directory.CreateDirectory(Path.Combine(Paths.ExtractedDirectory, "015", "Texture"));
            Directory.CreateDirectory(Paths.BuildOutputDirectory);
            ArchivePath = Path.Combine(Paths.WorkingDirectory, "015.ab");
            File.WriteAllText(ArchivePath, "archive");

            var secure = new StubSecureWorkspace(Paths);
            var template = new AuditionArchiveTemplate(
                "archive-015", "015.ab", "templates/015.ab", ArchiveEngineType.AcvTool5,
                "audition_vn", "015", "1", new string('A', 64), "audition-vn-2026");
            var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Workspace = new StubWorkspace(secure, template, projectId, descriptorUsesBackslashes);
            var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
            Project = AuditionProject.Create(
                1, projectId, "Project", new GameId("audition"), new ModId("login_mod"),
                Workspace.Descriptor.ArchiveTemplate.Identity,
                new(Workspace.Descriptor.WorkspaceId, new("Working/015.ab"), new("Extracted/015")),
                [], [], [], new(currentRevision, savedRevision, null, []),
                new(ProjectBuildStatus.NotBuilt, null, null, null), now, now).Project!;

            var paths = new List<string>();
            if (includeTexture)
            {
                paths.Add("Texture/logo.dds");
            }
            if (includeUnexpectedTexture)
            {
                paths.Add("Texture/unexpected.dds");
            }

            Scanner = new(Catalog(paths.ToArray()));
            Cache = new(ProjectMetadataCacheLoadResult.Success([Snapshot("Texture/logo.dds")]));
            MetadataReader = new(actualMetadata ?? Metadata());
            Tool = new();
            Validator = new(Cache, Scanner, MetadataReader, Tool);
        }

        public SecureWorkspacePaths Paths { get; }
        public string ArchivePath { get; }
        public AuditionProject Project { get; }
        public StubWorkspace Workspace { get; }
        public StubCache Cache { get; }
        public StubScanner Scanner { get; }
        public StubMetadataReader MetadataReader { get; }
        public StubTool Tool { get; }
        public ProjectValidator Validator { get; }

        public Task<ProjectValidationResult> ValidateAsync() =>
            Validator.ValidateAsync(new(Project, Workspace));

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubCache(ProjectMetadataCacheLoadResult result) : IProjectMetadataCache
    {
        public ProjectMetadataCacheLoadResult LoadResult { get; set; } = result;
        public Task<ProjectMetadataCacheLoadResult> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(LoadResult);
        public Task<ProjectMetadataCacheResult> StoreAsync(Guid projectId, IEnumerable<ProjectTextureMetadataSnapshot> textures, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectMetadataCacheResult> DeleteAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectMetadataCacheValidationResult> ValidateAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubScanner(ArchiveAssetCatalog catalog) : IArchiveAssetScanner
    {
        public ArchiveAssetCatalog Catalog { get; set; } = catalog;
        public Task<ArchiveAssetScanResult> ScanAsync(IProjectArchiveWorkspace workspace, IProgress<ArchiveAssetScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(ArchiveAssetScanResult.Success(Catalog));
    }

    public sealed class StubMetadataReader(DdsMetadata metadata) : IDdsMetadataReader
    {
        public string? MalformedFileName { get; set; }
        public Task<DdsMetadataReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.GetFileName(path).Equals(MalformedFileName, StringComparison.OrdinalIgnoreCase)
                ? DdsMetadataReadResult.Failure(DdsMetadataFailureReason.CorruptHeader, "TEST_DDS_MALFORMED")
                : DdsMetadataReadResult.Success(metadata));
    }

    private sealed class StubTool : IProjectToolIntegrityValidator
    {
        public ProjectToolIntegrityResult Result { get; set; } = new(ProjectToolIntegrityStatus.Valid, "TEST_TOOL_VALID");
        public Task<ProjectToolIntegrityResult> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace(
            ISecureWorkspace secure,
            AuditionArchiveTemplate template,
            Guid projectId,
            bool descriptorUsesBackslashes)
        {
            ArchiveWorkspace = ArchiveWorkspace.Create(secure, template);
            var now = DateTimeOffset.UtcNow;
            Descriptor = new(1, projectId, "Project", secure.Id,
                new(template.TemplateId.Value, template.TemplateVersion!.Value.Value,
                    template.ExpectedSha256!.Value.Value, template.EngineType, template.RegionProfileId,
                    template.CompatibleGameBuild!.Value.Value),
                descriptorUsesBackslashes ? "Working\\015.ab" : "Working/015.ab",
                descriptorUsesBackslashes ? "Extracted\\015" : "Extracted/015",
                "BuildOutput", null,
                ".project-archive-workspace.json", template.ExpectedSha256.Value.Value, now, now,
                ProjectArchiveWorkspaceState.Ready);
        }
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; }
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSecureWorkspace(SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id => "0123456789abcdef0123456789abcdef";
        public SecureWorkspacePaths Paths { get; } = paths;
        public string ResolveRelativePath(string relativePath)
        {
            var path = Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Paths.RootDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Path escaped workspace.", nameof(relativePath));
            }
            return path;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ArchiveAssetCatalog Catalog(params string[] paths) => new(
        paths.Select(path => (ArchiveAsset)new TextureAsset(
            path, Path.GetFileName(path), ".dds", Path.GetDirectoryName(path) ?? string.Empty,
            128, DateTimeOffset.UtcNow, new string('B', 64))).ToArray(), 1, paths.Length * 128L);

    private static ProjectTextureMetadataSnapshot Snapshot(string path) =>
        new(new(path), new(new string('B', 64)), Metadata(), null);

    private static DdsMetadata Metadata() => new(
        6000, 1801, null, 1, 1, DdsFormat.BC3, DdsFormatSupport.Known, "DXT5", null,
        DdsHeaderType.Legacy, true, true, DdsAlphaMode.Interpolated, DdsColorSpace.Unknown,
        DdsResourceDimension.Texture2D, false, 1, 10_806_128, 128);
}
