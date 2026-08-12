using AuditionModStudio.Core.Auth;
using AuditionModStudio.Security;
using System.Text;
using System.Text.Json;

namespace Security.Tests;

public sealed class WindowsCredentialSessionStoreTests
{
    [Fact]
    public async Task Oversized_session_is_rejected_before_windows_credential_write()
    {
        using var store = new WindowsCredentialSessionStore();
        var oversized = new AuthSessionSecrets(
            new string('a', 3_000), "refresh", DateTimeOffset.UtcNow.AddHours(1));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(oversized));
    }

    [Fact]
    public void Implementation_does_not_accept_a_plain_json_storage_path()
    {
        var constructors = typeof(WindowsCredentialSessionStore).GetConstructors();

        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
        Assert.DoesNotContain(typeof(WindowsCredentialSessionStore).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public),
            field => field.FieldType == typeof(FileInfo) || field.FieldType == typeof(DirectoryInfo));
    }

    [Fact]
    public async Task Legacy_plan58_document_is_migrated_to_schema_v1_in_same_credential()
    {
        var backend = new FakeBackend
        {
            Value = JsonSerializer.SerializeToUtf8Bytes(new
            {
                accessToken = "legacy-access",
                refreshToken = "legacy-refresh",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }),
        };
        using var store = new WindowsCredentialSessionStore(backend);

        var session = await store.LoadAsync();

        Assert.Equal("legacy-access", session!.AccessToken);
        Assert.Equal(1, backend.WriteCount);
        Assert.Contains("\"schemaVersion\":1", Encoding.UTF8.GetString(backend.Value!));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":99,\"accessToken\":\"a\",\"refreshToken\":\"r\",\"expiresAt\":\"2026-08-12T00:00:00Z\"}")]
    [InlineData("{\"schemaVersion\":1,\"accessToken\":\"a\",\"refreshToken\":\"r\",\"expiresAt\":\"2026-08-12T00:00:00Z\",\"unknown\":true}")]
    public async Task Corrupt_future_or_unknown_session_fails_closed(string json)
    {
        var backend = new FakeBackend { Value = Encoding.UTF8.GetBytes(json) };
        using var store = new WindowsCredentialSessionStore(backend);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.Equal(0, backend.WriteCount);
    }

    [Fact]
    public async Task Rotation_replaces_session_and_delete_is_idempotent()
    {
        var backend = new FakeBackend();
        using var store = new WindowsCredentialSessionStore(backend);
        await store.SaveAsync(Session("one"));
        await store.SaveAsync(Session("two"));

        var loaded = await store.LoadAsync();
        await store.DeleteAsync();
        await store.DeleteAsync();

        Assert.Equal("access-two", loaded!.AccessToken);
        Assert.Null(await store.LoadAsync());
        Assert.Equal(2, backend.DeleteCount);
    }

    [Fact]
    public async Task Multiple_store_instances_serialize_access_to_the_single_credential_target()
    {
        var backend = new FakeBackend { OperationDelay = TimeSpan.FromMilliseconds(25) };
        using var first = new WindowsCredentialSessionStore(backend);
        using var second = new WindowsCredentialSessionStore(backend);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SaveAsync(Session(index.ToString())))));

        Assert.Equal(1, backend.MaximumConcurrentOperations);
        Assert.Equal(8, backend.WriteCount);
    }

    [Fact]
    public async Task Legacy_migration_write_failure_does_not_return_stale_success()
    {
        var backend = new FakeBackend
        {
            Value = JsonSerializer.SerializeToUtf8Bytes(new
            {
                accessToken = "legacy-access",
                refreshToken = "legacy-refresh",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }),
            WriteException = new IOException("credential unavailable"),
        };
        using var store = new WindowsCredentialSessionStore(backend);

        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
    }

    private static AuthSessionSecrets Session(string suffix) =>
        new($"access-{suffix}", $"refresh-{suffix}", DateTimeOffset.UtcNow.AddHours(1));

    private sealed class FakeBackend : IWindowsCredentialBackend
    {
        private byte[]? _value;
        private int _active;
        public byte[]? Value { get => _value?.ToArray(); set => _value = value?.ToArray(); }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int MaximumConcurrentOperations { get; private set; }
        public TimeSpan OperationDelay { get; init; }
        public Exception? WriteException { get; init; }

        public byte[]? Read(string targetName, int maximumBytes)
        {
            Enter();
            try { Delay(); return _value?.ToArray(); }
            finally { Exit(); }
        }

        public void Write(string targetName, byte[] bytes)
        {
            Enter();
            try
            {
                Delay();
                if (WriteException is not null) throw WriteException;
                _value = bytes.ToArray(); WriteCount++;
            }
            finally { Exit(); }
        }

        public void Delete(string targetName)
        {
            Enter();
            try { Delay(); _value = null; DeleteCount++; }
            finally { Exit(); }
        }

        private void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrentOperations = Math.Max(MaximumConcurrentOperations, active);
        }
        private void Exit() => Interlocked.Decrement(ref _active);
        private void Delay() { if (OperationDelay > TimeSpan.Zero) Thread.Sleep(OperationDelay); }
    }
}
