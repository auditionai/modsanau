using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Archives;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Assets;
using AuditionModStudio.Core.Dds;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Infrastructure.Paths;

namespace Archives.Tests;

public sealed class TemplateAdminWorkflowTests
{
    [Fact]
    public async Task Unauthorized_request_is_rejected_before_archive_or_workspace_access()
    {
        using var context = new Context(authorized: false);
        var result = await context.Workflow.ExecuteAsync(context.Request("Z:\\does-not-exist.ab"));

        Assert.Equal(TemplateAdminStatus.Unauthorized, result.Status);
        Assert.Equal(0, context.Workspaces.CallCount);
        Assert.Equal(0, context.Publisher.CallCount);
    }

    [Fact]
    public async Task Explicit_archive_is_isolated_scanned_labeled_and_published_with_audit()
    {
        using var context = new Context();
        var request = context.Request(context.ArchivePath);

        var result = await context.Workflow.ExecuteAsync(request);

        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.True(context.Workspace.Disposed);
        Assert.Equal(1, context.Archive.CallCount);
        Assert.Same(context.Workspace, context.Scanner.LastWorkspace);
        Assert.Equal(File.ReadAllBytes(context.ArchivePath), context.OriginalBytes);
        var publish = Assert.IsType<TemplateAdminPublishRequest>(context.Publisher.LastRequest);
        Assert.Equal(TemplateAdminStorageEncryption.ServerManaged, publish.RequiredEncryption);
        Assert.Equal(request.Principal.SubjectId, publish.AuditEvent.AdminSubjectId);
        Assert.Equal("template_version_published", publish.AuditEvent.Action);
        Assert.Equal(new string('A', 64), Assert.Single(publish.DdsRecords).ContentSha256.Value);
        Assert.DoesNotContain(context.ArchivePath, publish.AuditEvent.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_label_and_version_conflict_fail_without_fabricated_publish_success()
    {
        using var context = new Context();
        var missingLabels = context.Request(context.ArchivePath) with { Labels = [] };
        var invalid = await context.Workflow.ExecuteAsync(missingLabels);
        Assert.Equal(TemplateAdminStatus.LabelValidationFailed, invalid.Status);
        Assert.Equal(0, context.Publisher.CallCount);

        context.Publisher.VersionConflict = true;
        var conflict = await context.Workflow.ExecuteAsync(context.Request(context.ArchivePath));
        Assert.Equal(TemplateAdminStatus.VersionConflict, conflict.Status);
        Assert.Null(conflict.Identity);
    }

    [Fact]
    public async Task Cancellation_is_structured_and_does_not_publish()
    {
        using var context = new Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Workflow.ExecuteAsync(context.Request(context.ArchivePath),
            cancellationToken: cancellation.Token);

        Assert.Equal(TemplateAdminStatus.Cancelled, result.Status);
        Assert.Equal(0, context.Publisher.CallCount);
    }

    private sealed class Context : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ams-admin-workflow-" + Guid.NewGuid().ToString("N"));
        public Context(bool authorized = true)
        {
            Directory.CreateDirectory(_root);
            ArchivePath = Path.Combine(_root, "upload.ab");
            OriginalBytes = Encoding.ASCII.GetBytes("explicit-admin-archive");
            File.WriteAllBytes(ArchivePath, OriginalBytes);
            Workspace = new StubWorkspace(Path.Combine(_root, "workspace"));
            Workspaces = new StubWorkspaceService(Workspace);
            Archive = new StubArchiveService();
            Scanner = new StubScanner();
            Publisher = new StubPublisher();
            Workflow = new TemplateAdminWorkflow(new StubAuthorizer(authorized), Publisher, Workspaces,
                Archive, Scanner, new StubDdsReader(), new GameRegionProfileCatalog(),
                new PathSecurity(), new FixedTimeProvider());
        }
        public string ArchivePath { get; }
        public byte[] OriginalBytes { get; }
        public StubWorkspace Workspace { get; }
        public StubWorkspaceService Workspaces { get; }
        public StubArchiveService Archive { get; }
        public StubScanner Scanner { get; }
        public StubPublisher Publisher { get; }
        public TemplateAdminWorkflow Workflow { get; }
        public TemplateAdminRequest Request(string path) => new(new(Guid.NewGuid()), path,
            new("audition"), new("pointer_mod"), new("pointer"), new("v1"), new("build-1"),
            ArchiveEngineType.AcvTool5, "audition_vn", "015",
            [new(new("logo"), new("texture.dds"), "Logo", new("logo"), "Main logo", ["logo"],
                true, true, new("fit"))]);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class StubAuthorizer(bool authorized) : ITemplateAdminAuthorizer
    {
        public Task<bool> IsTemplateAdminAsync(TemplateAdminPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(authorized);
        }
    }

    private sealed class StubPublisher : ITemplateAdminPublisher
    {
        public int CallCount { get; private set; }
        public bool VersionConflict { get; set; }
        public TemplateAdminPublishRequest? LastRequest { get; private set; }
        public Task<TemplateAdminPublishResult> PublishAtomicallyAsync(TemplateAdminPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(VersionConflict
                ? new(false, "TEMPLATE_ADMIN_VERSION_CONFLICT", null, null, [], null, true)
                : new TemplateAdminPublishResult(true, "TEMPLATE_ADMIN_PUBLISH_COMMITTED",
                    request.PackageManifest.Identity, TemplateAdminStorageEncryption.ServerManaged,
                    ImmutableArray.Create(new byte[64]), request.AuditEvent.OperationId));
        }
    }

    private sealed class StubWorkspaceService(StubWorkspace workspace) : IProjectArchiveWorkspaceService
    {
        public int CallCount { get; private set; }
        public Task<ProjectArchiveWorkspaceCreateResult> CreateAsync(ProjectArchiveWorkspaceCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ProjectArchiveWorkspaceCreateResult.Success(workspace));
        }
        public Task<ProjectArchiveWorkspaceValidationResult> ValidateAsync(IProjectArchiveWorkspace value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubArchiveService : IAuditionArchiveService
    {
        public int CallCount { get; private set; }
        public Task<ArchiveExtractResult> ExtractAsync(ArchiveExtractRequest request,
            IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ArchiveExtractResult(new(ArchiveOperation.Extract,
                ArchiveOperationState.Completed, true, 1, ArchiveFailureReason.None, null, [])));
        }
        public Task<ArchivePackResult> PackAsync(ArchivePackRequest request,
            IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubScanner : IArchiveAssetScanner
    {
        public IProjectArchiveWorkspace? LastWorkspace { get; private set; }
        public Task<ArchiveAssetScanResult> ScanAsync(IProjectArchiveWorkspace workspace,
            IProgress<ArchiveAssetScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastWorkspace = workspace;
            TextureAsset texture = new("texture.dds", "texture.dds", ".dds", string.Empty,
                128, DateTimeOffset.UnixEpoch, new string('A', 64));
            return Task.FromResult(ArchiveAssetScanResult.Success(new([texture], 0, texture.FileSize)));
        }
    }

    private sealed class StubDdsReader : IDdsMetadataReader
    {
        public Task<DdsMetadataReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(DdsMetadataReadResult.Success(new(6000, 1801, null, 1, 1, DdsFormat.BC3,
                DdsFormatSupport.Known, "DXT5", null, DdsHeaderType.Legacy, true, true,
                DdsAlphaMode.Interpolated, DdsColorSpace.Unknown, DdsResourceDimension.Texture2D,
                false, 1, 128, 128)));
    }

    private sealed class StubWorkspace : IProjectArchiveWorkspace
    {
        public StubWorkspace(string root)
        {
            var paths = new SecureWorkspacePaths(root, Path.Combine(root, "Working"),
                Path.Combine(root, "Extracted"), Path.Combine(root, "BuildOutput"));
            Directory.CreateDirectory(Path.Combine(paths.ExtractedDirectory, "015"));
            SecureWorkspace = new StubSecureWorkspace(paths);
            ArchiveWorkspace = new(SecureWorkspace, "upload.ab", "015");
        }
        public bool Disposed { get; private set; }
        public StubSecureWorkspace SecureWorkspace { get; }
        public ProjectArchiveWorkspaceDescriptor Descriptor => null!;
        public ArchiveWorkspace ArchiveWorkspace { get; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class StubSecureWorkspace(SecureWorkspacePaths paths) : ISecureWorkspace
    {
        public string Id => "admin-workspace";
        public SecureWorkspacePaths Paths => paths;
        public string ResolveRelativePath(string relativePath) => Path.Combine(paths.RootDirectory, relativePath);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
    }
}
