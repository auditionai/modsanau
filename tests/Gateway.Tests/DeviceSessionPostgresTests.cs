using AuditionModStudio.Gateway.Services;
using Npgsql;

namespace Gateway.Tests;

[Collection("Postgres integration")]
public sealed class DeviceSessionPostgresTests
{
    [PostgresFact]
    public async Task Real_database_enforces_registration_limit_ownership_revocation_and_rls()
    {
        await using var database = await DeviceSessionDatabase.CreateAsync();
        var owner = new AuthenticatedGatewayUser(Guid.NewGuid());
        var stranger = new AuthenticatedGatewayUser(Guid.NewGuid());
        var firstDevice = new DeviceSessionDescriptor(Guid.NewGuid(), "Máy chính", "1.0.0", "windows-x64");
        var secondDevice = new DeviceSessionDescriptor(Guid.NewGuid(), "Máy phụ", "1.0.0", "windows-x64");
        var excessDevice = new DeviceSessionDescriptor(Guid.NewGuid(), "Máy dư", "1.0.0", "windows-x64");
        await database.ExecuteAsync("INSERT INTO private.credit_wallets(user_id) VALUES ($1)", owner.UserId);

        var registered = await database.Service.RegisterAsync(owner, firstDevice, 2);
        var replay = await database.Service.RegisterAsync(owner, firstDevice, 2);
        var second = await database.Service.RegisterAsync(owner, secondDevice, 2);
        var excess = await database.Service.RegisterAsync(owner, excessDevice, 2);
        var listed = await database.Service.ListAsync(owner);

        Assert.Equal(DeviceSessionOperationStatus.Succeeded, registered.Status);
        Assert.Equal("DEVICE_SESSION_REGISTERED", registered.DiagnosticCode);
        Assert.True(registered.IsNewDevice);
        Assert.Equal(DeviceSessionOperationStatus.Succeeded, replay.Status);
        Assert.Equal("DEVICE_SESSION_IDEMPOTENT_REPLAY", replay.DiagnosticCode);
        Assert.False(replay.IsNewDevice);
        Assert.Equal(registered.Session!.SessionId, replay.Session!.SessionId);
        Assert.Equal(DeviceSessionOperationStatus.Succeeded, second.Status);
        Assert.Equal(DeviceSessionOperationStatus.Rejected, excess.Status);
        Assert.Equal("DEVICE_LIMIT_REACHED", excess.DiagnosticCode);
        Assert.Equal(2, listed.Sessions.Count);

        var active = await database.Service.ValidateAsync(owner, firstDevice.DeviceId,
            registered.Session.SessionId, TimeSpan.FromMinutes(15));
        var initialSeen = active.Session!.LastSeenAt;
        var throttled = await database.Service.ValidateAsync(owner, firstDevice.DeviceId,
            registered.Session.SessionId, TimeSpan.FromMinutes(15));
        Assert.Equal(initialSeen, throttled.Session!.LastSeenAt);

        await database.ExecuteAsync("""
            UPDATE private.user_device_sessions
            SET created_at = clock_timestamp() - interval '2 hours',
                last_seen_at = clock_timestamp() - interval '1 hour'
            WHERE session_id = $1
            """, registered.Session.SessionId);
        var refreshed = await database.Service.ValidateAsync(owner, firstDevice.DeviceId,
            registered.Session.SessionId, TimeSpan.FromMinutes(15));
        Assert.True(refreshed.Session!.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        var crossUser = await database.Service.RevokeAsync(stranger, registered.Session.SessionId);
        var revoked = await database.Service.RevokeAsync(owner, registered.Session.SessionId);
        var revokeReplay = await database.Service.RevokeAsync(owner, registered.Session.SessionId);
        var denied = await database.Service.ValidateAsync(owner, firstDevice.DeviceId,
            registered.Session.SessionId, TimeSpan.FromMinutes(15));
        var resurrect = await database.Service.RegisterAsync(owner, firstDevice, 2);

        Assert.Equal(DeviceSessionOperationStatus.NotFound, crossUser.Status);
        Assert.Equal("DEVICE_SESSION_NOT_FOUND", crossUser.DiagnosticCode);
        Assert.Equal(DeviceSessionOperationStatus.Succeeded, revoked.Status);
        Assert.Equal("DEVICE_SESSION_REVOKED", revoked.DiagnosticCode);
        Assert.NotNull(revoked.Session!.RevokedAt);
        Assert.Equal(DeviceSessionOperationStatus.Succeeded, revokeReplay.Status);
        Assert.Equal("DEVICE_SESSION_REVOKE_REPLAY", revokeReplay.DiagnosticCode);
        Assert.Equal(revoked.Session.RevokedAt, revokeReplay.Session!.RevokedAt);
        Assert.Equal(DeviceSessionOperationStatus.Rejected, denied.Status);
        Assert.Equal("DEVICE_SESSION_INACTIVE", denied.DiagnosticCode);
        Assert.Equal(DeviceSessionOperationStatus.Rejected, resurrect.Status);
        Assert.Equal("DEVICE_SESSION_REVOKED", resurrect.DiagnosticCode);
        Assert.Equal(1L, await database.ScalarAsync<long>(
            "SELECT count(*) FROM private.credit_wallets WHERE user_id = $1", owner.UserId));

        await database.AssertRlsAsync(owner.UserId, stranger.UserId);
        await AssertConcurrentLimitAsync(database);
    }

    private static async Task AssertConcurrentLimitAsync(DeviceSessionDatabase database)
    {
        var user = new AuthenticatedGatewayUser(Guid.NewGuid());
        var calls = new[]
        {
            database.Service.RegisterAsync(user,
                new DeviceSessionDescriptor(Guid.NewGuid(), "Concurrent A", "1.0.0", "windows-x64"), 1),
            database.Service.RegisterAsync(user,
                new DeviceSessionDescriptor(Guid.NewGuid(), "Concurrent B", "1.0.0", "windows-x64"), 1),
        };
        var results = await Task.WhenAll(calls);
        Assert.Single(results, result => result.Status == DeviceSessionOperationStatus.Succeeded);
        Assert.Single(results, result => result.DiagnosticCode == "DEVICE_LIMIT_REACHED");
        Assert.Equal(1L, await database.ScalarAsync<long>("""
            SELECT count(*) FROM private.user_device_sessions
            WHERE user_id = $1 AND revoked_at IS NULL
            """, user.UserId));
    }

    private sealed class DeviceSessionDatabase(
        NpgsqlConnectionStringBuilder adminBuilder,
        string databaseName,
        NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public PostgresDeviceSessionService Service { get; } = new(dataSource);

        public static async Task<DeviceSessionDatabase> CreateAsync()
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("AUDITION_POSTGRES_ADMIN_CONNECTION"));
            Assert.True(adminBuilder.Host is "127.0.0.1" or "localhost" or "::1",
                "PLAN 86 integration database must be loopback-only.");
            var databaseName = $"audition_plan86_{Guid.NewGuid():N}";
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await ExecuteOnConnectionAsync(admin, """
                DO $roles$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN CREATE ROLE service_role NOLOGIN BYPASSRLS; END IF;
                END
                $roles$;
                """);
            await ExecuteOnConnectionAsync(admin, $"CREATE DATABASE {databaseName};");
            var databaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                MaxPoolSize = 16,
            };
            await using (var connection = new NpgsqlConnection(databaseBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await ExecuteOnConnectionAsync(connection, """
                    CREATE SCHEMA auth;
                    CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE SET search_path = pg_catalog
                    AS $auth_uid$ SELECT NULLIF(current_setting('request.jwt.claim.sub', true), '')::uuid $auth_uid$;
                    REVOKE ALL ON SCHEMA auth FROM PUBLIC, anon;
                    REVOKE ALL ON FUNCTION auth.uid() FROM PUBLIC, anon;
                    GRANT USAGE ON SCHEMA auth TO authenticated, service_role;
                    GRANT EXECUTE ON FUNCTION auth.uid() TO authenticated, service_role;
                    """);
                foreach (var migration in Directory.GetFiles(MigrationsDirectory(), "*.sql").Order())
                    await ExecuteOnConnectionAsync(connection, await File.ReadAllTextAsync(migration));
            }
            return new DeviceSessionDatabase(adminBuilder, databaseName,
                NpgsqlDataSource.Create(databaseBuilder.ConnectionString));
        }

        public async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, params object[] values)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public async Task AssertRlsAsync(Guid owner, Guid stranger)
        {
            var columns = await StringsAsync("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'private' AND table_name = 'user_device_sessions'
                ORDER BY ordinal_position
                """);
            Assert.Equal(new[] { "session_id", "user_id", "device_id", "display_name", "client_version",
                "platform", "created_at", "last_seen_at", "revoked_at" }, columns);
            Assert.DoesNotContain(columns, column => column.Contains("token", StringComparison.OrdinalIgnoreCase)
                || column.Contains("ip", StringComparison.OrdinalIgnoreCase)
                || column.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));

            await using var connection = await dataSource.OpenConnectionAsync();
            await ExecuteOnConnectionAsync(connection, "SET ROLE authenticated;");
            try
            {
                await ScalarOnConnectionAsync(connection,
                    "SELECT set_config('request.jwt.claim.sub', $1, false)", owner.ToString("D"));
                Assert.True((long)(await ScalarOnConnectionAsync(connection,
                    "SELECT count(session_id) FROM private.user_device_sessions"))! > 0);
                await ScalarOnConnectionAsync(connection,
                    "SELECT set_config('request.jwt.claim.sub', $1, false)", stranger.ToString("D"));
                Assert.Equal(0L, (long)(await ScalarOnConnectionAsync(connection,
                    "SELECT count(session_id) FROM private.user_device_sessions"))!);
                var mutation = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteOnConnectionAsync(connection,
                        "UPDATE private.user_device_sessions SET display_name = 'forged'"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, mutation.SqlState);
                var rpc = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOnConnectionAsync(connection,
                    "SELECT * FROM private.device_session_revoke(gen_random_uuid(), gen_random_uuid())"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rpc.SqlState);
            }
            finally
            {
                await ExecuteOnConnectionAsync(connection, "RESET ROLE;");
            }
        }

        private async Task<string[]> StringsAsync(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await reader.ReadAsync()) values.Add(reader.GetString(0));
            return values.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await dataSource.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await ExecuteOnConnectionAsync(admin, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
        }

        private static async Task ExecuteOnConnectionAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<object?> ScalarOnConnectionAsync(
            NpgsqlConnection connection, string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteScalarAsync();
        }
    }

    private static string MigrationsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations");
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                "AUDITION_POSTGRES_ADMIN_CONNECTION")))
                Skip = "Requires an isolated loopback PostgreSQL admin connection for the PLAN 86 gate.";
        }
    }
}
