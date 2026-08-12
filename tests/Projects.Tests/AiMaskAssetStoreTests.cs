using System.Buffers.Binary;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Core.Projects;
using AuditionModStudio.Core.Workspaces;
using AuditionModStudio.Projects;

namespace Projects.Tests;

public sealed class AiMaskAssetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ams-plan64-{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_writes_exact_mask_atomically_inside_project_workspace()
    {
        var store = new AiMaskAssetStore();
        await using var workspace = new TestProjectWorkspace(new TestSecureWorkspace(_root));
        var mask = new AiMask(2, 2, [0, 64, 128, 255]);

        var result = await store.SaveAsync(workspace, mask);

        Assert.True(result.Succeeded);
        Assert.Equal(AiMaskAssetStore.MaskRelativePath, result.RelativePath);
        var target = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(result.RelativePath!);
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal("AMSMASK1"u8.ToArray(), bytes[..8]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(mask.Opacity.ToArray(), bytes[20..]);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.tmp"));
    }

    [Fact]
    public async Task Cancelled_replacement_preserves_previous_asset_and_removes_temporary_file()
    {
        var store = new AiMaskAssetStore();
        await using var workspace = new TestProjectWorkspace(new TestSecureWorkspace(_root));
        Assert.True((await store.SaveAsync(workspace, new AiMask(1, 1, [17]))).Succeeded);
        var target = workspace.ArchiveWorkspace.SecureWorkspace.ResolveRelativePath(
            AiMaskAssetStore.MaskRelativePath);
        var before = await File.ReadAllBytesAsync(target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(workspace, new AiMask(1, 1, [99]), cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class TestProjectWorkspace(TestSecureWorkspace secureWorkspace) : IProjectArchiveWorkspace
    {
        public ProjectArchiveWorkspaceDescriptor Descriptor { get; } = new(
            1, Guid.NewGuid(), "Mask test", "mask-test",
            new("template", "1", new string('0', 64), ArchiveEngineType.AcvTool5, "audition_vn"),
            "Working/015.ab", "Extracted", "BuildOutput", null, "project.json", new string('0', 64),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectArchiveWorkspaceState.Ready);

        public ArchiveWorkspace ArchiveWorkspace { get; } = new(secureWorkspace, "015.ab", "015");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSecureWorkspace(string root) : ISecureWorkspace
    {
        public string Id => "mask-test";
        public SecureWorkspacePaths Paths { get; } = new(root, Path.Combine(root, "Working"),
            Path.Combine(root, "Extracted"), Path.Combine(root, "BuildOutput"));

        public string ResolveRelativePath(string relativePath)
        {
            if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Contains(".."))
                throw new ArgumentException("Relative path must stay inside the test workspace.", nameof(relativePath));
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
