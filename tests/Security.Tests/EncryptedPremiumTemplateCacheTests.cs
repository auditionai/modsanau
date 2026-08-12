using System.Security.Cryptography;
using System.Text;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Core.Paths;
using AuditionModStudio.Security;

namespace Security.Tests;

public sealed class EncryptedPremiumTemplateCacheTests
{
    [Fact]
    public async Task Store_and_materialize_roundtrip_uses_ciphertext_and_exact_identity_path()
    {
        using var context = Create();
        var bytes = Encoding.UTF8.GetBytes("premium-template-plaintext-marker");
        var manifest = Manifest(bytes);

        var stored = await context.Cache.StoreAsync(manifest, new MemoryStream(bytes));
        var result = await context.Cache.MaterializeAsync(manifest, context.Workspace);

        Assert.True(stored.Succeeded, stored.DiagnosticCode);
        Assert.True(result.Succeeded, result.DiagnosticCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.MaterializedPath!));
        var cachePath = CachePath(context, manifest);
        Assert.True(File.Exists(cachePath));
        Assert.DoesNotContain("premium-template-plaintext-marker",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cachePath)), StringComparison.Ordinal);
        Assert.DoesNotContain("audition", cachePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Corruption_is_detected_and_cache_entry_is_removed_without_plaintext_publish()
    {
        using var context = Create();
        var bytes = RandomNumberGenerator.GetBytes(4096); var manifest = Manifest(bytes);
        Assert.True((await context.Cache.StoreAsync(manifest, new MemoryStream(bytes))).Succeeded);
        var path = CachePath(context, manifest); var encrypted = await File.ReadAllBytesAsync(path);
        encrypted[^20] ^= 1; await File.WriteAllBytesAsync(path, encrypted);

        var result = await context.Cache.MaterializeAsync(manifest, context.Workspace);

        Assert.Equal(PremiumTemplateCacheStatus.Corrupt, result.Status);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(context.Workspace));
    }

    [Fact]
    public async Task Key_loss_removes_unusable_entry_and_allows_recovery_store()
    {
        using var context = Create(); var bytes = RandomNumberGenerator.GetBytes(2048); var manifest = Manifest(bytes);
        Assert.True((await context.Cache.StoreAsync(manifest, new MemoryStream(bytes))).Succeeded);
        context.Protector.FailUnprotect = true;

        var lost = await context.Cache.MaterializeAsync(manifest, context.Workspace);
        context.Protector.FailUnprotect = false;
        var recovered = await context.Cache.StoreAsync(manifest, new MemoryStream(bytes));

        Assert.Equal(PremiumTemplateCacheStatus.KeyUnavailable, lost.Status);
        Assert.True(recovered.Succeeded);
        Assert.True(File.Exists(CachePath(context, manifest)));
    }

    [Fact]
    public async Task Truncated_oversized_or_hash_mismatched_source_never_publishes_cache_entry()
    {
        using var context = Create(); var bytes = RandomNumberGenerator.GetBytes(1024); var manifest = Manifest(bytes);

        var truncated = await context.Cache.StoreAsync(manifest, new MemoryStream(bytes[..^1]));
        var oversized = await context.Cache.StoreAsync(manifest, new MemoryStream([.. bytes, (byte)1]));
        var changed = bytes.ToArray(); changed[0] ^= 1;
        var mismatched = await context.Cache.StoreAsync(manifest, new MemoryStream(changed));

        Assert.All(new[] { truncated, oversized, mismatched }, result =>
            Assert.Equal(PremiumTemplateCacheStatus.Corrupt, result.Status));
        Assert.False(File.Exists(CachePath(context, manifest)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(CachePath(context, manifest))!, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_same_identity_writes_are_serialized_and_remain_readable()
    {
        using var context = Create(); var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        var manifest = Manifest(bytes);

        var stores = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => context.Cache.StoreAsync(manifest, new MemoryStream(bytes))));
        var materialized = await context.Cache.MaterializeAsync(manifest, context.Workspace);

        Assert.All(stores, result => Assert.True(result.Succeeded, result.DiagnosticCode));
        Assert.True(materialized.Succeeded, materialized.DiagnosticCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(materialized.MaterializedPath!));
    }

    [Fact]
    public async Task Case_distinct_versions_do_not_collide_on_case_insensitive_filesystem()
    {
        using var context = Create(); var bytes = RandomNumberGenerator.GetBytes(128);
        var upper = Manifest(bytes, "V1"); var lower = Manifest(bytes, "v1");

        Assert.True((await context.Cache.StoreAsync(upper, new MemoryStream(bytes))).Succeeded);
        Assert.True((await context.Cache.StoreAsync(lower, new MemoryStream(bytes))).Succeeded);

        Assert.NotEqual(CachePath(context, upper), CachePath(context, lower));
        Assert.True((await context.Cache.MaterializeAsync(upper, context.Workspace)).Succeeded);
        Assert.True((await context.Cache.MaterializeAsync(lower, context.Workspace)).Succeeded);
    }

    [Fact]
    public async Task Destination_outside_managed_workspace_is_rejected()
    {
        using var context = Create(); var bytes = RandomNumberGenerator.GetBytes(32); var manifest = Manifest(bytes);
        Assert.True((await context.Cache.StoreAsync(manifest, new MemoryStream(bytes))).Succeeded);

        var result = await context.Cache.MaterializeAsync(manifest, context.Paths.RootDirectory);

        Assert.Equal(PremiumTemplateCacheStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task Cancelled_write_does_not_publish_partial_entry()
    {
        using var context = Create(); var bytes = RandomNumberGenerator.GetBytes(1024); var manifest = Manifest(bytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();

        var result = await context.Cache.StoreAsync(manifest, new MemoryStream(bytes), cancellation.Token);

        Assert.Equal(PremiumTemplateCacheStatus.Cancelled, result.Status);
        Assert.False(File.Exists(CachePath(context, manifest)));
    }

    [Fact]
    public void Windows_dpapi_wraps_and_unwraps_without_embedding_key()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsDpapiTemplateCacheKeyProtector();
        var key = RandomNumberGenerator.GetBytes(32); var context = RandomNumberGenerator.GetBytes(32);
        var wrapped = protector.Protect(key, context); var restored = protector.Unprotect(wrapped, context);
        Assert.Equal(key, restored); Assert.NotEqual(key, wrapped);
        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(context);
        CryptographicOperations.ZeroMemory(wrapped); CryptographicOperations.ZeroMemory(restored);
    }

    private static TestContext Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ams-cache-tests", Guid.NewGuid().ToString("N"));
        var paths = new FakePaths(root); paths.EnsureDirectoriesExist();
        var workspace = Path.Combine(paths.WorkspacesDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace); var protector = new FakeProtector();
        return new(root, paths, workspace, protector, new(paths, protector));
    }

    private static PremiumTemplatePackageManifest Manifest(byte[] bytes, string version = "v1") => new(
        new(new("pointer"), new(version), new(Convert.ToHexString(SHA256.HashData(bytes))), new("build-1")),
        new GameId("audition"), new ModId("pointer_mod"), bytes.Length,
        PremiumTemplatePackageManifest.PackageMediaType);
    private static string CachePath(TestContext context, PremiumTemplatePackageManifest manifest)
    {
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"AMS-CACHE-V1\n{manifest.Identity.TemplateId.Value}\n{manifest.Identity.Version.Value}\n{manifest.Identity.Sha256.Value}"));
        return Path.Combine(context.Paths.SecureTemplateCacheDirectory, manifest.Identity.TemplateId.Value,
            Convert.ToHexString(identity) + ".cache");
    }

    private sealed record TestContext(string Root, FakePaths Paths, string Workspace, FakeProtector Protector,
        EncryptedPremiumTemplateCache Cache) : IDisposable
    { public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } } }

    private sealed class FakeProtector : ITemplateCacheKeyProtector
    {
        public bool FailUnprotect { get; set; }
        public byte[] Protect(ReadOnlySpan<byte> key, ReadOnlySpan<byte> context) => Xor(key, context);
        public byte[] Unprotect(ReadOnlySpan<byte> wrappedKey, ReadOnlySpan<byte> context) =>
            FailUnprotect ? throw new CryptographicException("Key unavailable.") : Xor(wrappedKey, context);
        private static byte[] Xor(ReadOnlySpan<byte> value, ReadOnlySpan<byte> context)
        { var output = value.ToArray(); for (var i = 0; i < output.Length; i++) output[i] ^= context[i % context.Length]; return output; }
    }

    private sealed class FakePaths(string root) : IAppPaths
    {
        public string RootDirectory { get; } = root;
        public string LogsDirectory { get; } = Path.Combine(root, "Logs");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string ProjectsDirectory { get; } = Path.Combine(root, "Projects");
        public string TempDirectory { get; } = Path.Combine(root, "Temp");
        public string SettingsDirectory { get; } = Path.Combine(root, "Settings");
        public string DownloadsDirectory { get; } = Path.Combine(root, "Downloads");
        public string SecureTemplateCacheDirectory { get; } = Path.Combine(root, "SecureTemplateCache");
        public string BackupsDirectory { get; } = Path.Combine(root, "Backups");
        public string WorkspacesDirectory { get; } = Path.Combine(root, "Temp", "Workspaces");
        public IReadOnlyCollection<string> ManagedDirectories => [RootDirectory, LogsDirectory, CacheDirectory,
            ProjectsDirectory, TempDirectory, SettingsDirectory, DownloadsDirectory, SecureTemplateCacheDirectory,
            BackupsDirectory, WorkspacesDirectory];
        public void EnsureDirectoriesExist() { foreach (var path in ManagedDirectories) Directory.CreateDirectory(path); }
    }
}
