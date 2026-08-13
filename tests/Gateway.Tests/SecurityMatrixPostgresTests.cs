using AuditionModStudio.Gateway;
using AuditionModStudio.Gateway.Services;
using Npgsql;

namespace Gateway.Tests;

public sealed class SecurityMatrixPostgresTests
{
    private static readonly AiJobInputMetadata Metadata = new(
        "inputs/security-matrix", 24, 512, 512, 1024);

    [PostgresFact]
    public async Task Duplicate_ai_enqueue_reserves_credit_exactly_once_and_replays_one_job()
    {
        await using var database = await SecurityMatrixDatabase.CreateAsync();
        var owner = new AuthenticatedGatewayUser(Guid.NewGuid());
        var grant = await database.Credits.GrantAsync(owner, new(100), "security-matrix:grant",
            new("security-matrix-grant"));
        Assert.Equal(CreditLedgerOperationStatus.Succeeded, grant.Status);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await database.Jobs.EnqueueAsync(owner, TrustedAiOperation.Generate, Metadata,
                new("duplicate-ai-charge"));
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.Equal(AiJobOperationStatus.Succeeded, result.Status));
        Assert.Single(results.Select(result => result.Job!.JobId).Distinct());
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.ai_jobs WHERE user_id=$1", owner.UserId));
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_reservations WHERE user_id=$1", owner.UserId));
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_ledger WHERE user_id=$1 AND entry_type='reserve'", owner.UserId));
        Assert.Equal(new TrustedCreditSnapshot(80, 20), (await database.Credits.GetAsync(owner)).Snapshot);
    }

    [PostgresFact]
    public async Task Wrong_user_cannot_get_list_or_cancel_another_users_ai_job()
    {
        await using var database = await SecurityMatrixDatabase.CreateAsync();
        var owner = new AuthenticatedGatewayUser(Guid.NewGuid());
        var stranger = new AuthenticatedGatewayUser(Guid.NewGuid());
        var grant = await database.Credits.GrantAsync(owner, new(100), "security-matrix:owner",
            new("security-matrix-owner-grant"));
        Assert.Equal(CreditLedgerOperationStatus.Succeeded, grant.Status);
        var queued = await database.Jobs.EnqueueAsync(owner, TrustedAiOperation.Generate, Metadata,
            new("owner-job"));
        Assert.Equal(AiJobOperationStatus.Succeeded, queued.Status);

        var crossGet = await database.Jobs.GetAsync(stranger, queued.Job!.JobId);
        var crossList = await database.Jobs.ListAsync(stranger);
        var crossCancel = await database.Jobs.CancelAsync(stranger, queued.Job.JobId);
        var ownerGet = await database.Jobs.GetAsync(owner, queued.Job.JobId);

        Assert.Equal(AiJobOperationStatus.NotFound, crossGet.Status);
        Assert.Equal(AiJobOperationStatus.Succeeded, crossList.Status);
        Assert.Empty(crossList.Jobs);
        Assert.Equal(AiJobOperationStatus.NotFound, crossCancel.Status);
        Assert.Equal(AiJobOperationStatus.Succeeded, ownerGet.Status);
        Assert.Equal(AiJobStatus.Queued, ownerGet.Job!.Status);
    }

    private static string MigrationsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations");
    }

    private sealed class SecurityMatrixDatabase(
        NpgsqlConnectionStringBuilder adminBuilder,
        string databaseName,
        NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public PostgresCreditLedgerService Credits { get; } = new(dataSource);
        public PostgresAiJobService Jobs { get; } = new(dataSource, new FixedPricingService());

        public static async Task<SecurityMatrixDatabase> CreateAsync()
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("AUDITION_POSTGRES_ADMIN_CONNECTION"));
            Assert.True(adminBuilder.Host is "127.0.0.1" or "localhost" or "::1",
                "PLAN 89 security matrix database must be loopback-only.");
            var databaseName = $"audition_plan89_{Guid.NewGuid():N}";
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await ExecuteAsync(admin, """
                DO $roles$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN CREATE ROLE anon NOLOGIN; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN CREATE ROLE service_role NOLOGIN BYPASSRLS; END IF;
                END
                $roles$;
                """);
            await ExecuteAsync(admin, $"CREATE DATABASE {databaseName};");
            var databaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                MaxPoolSize = 32,
            };
            await using (var database = new NpgsqlConnection(databaseBuilder.ConnectionString))
            {
                await database.OpenAsync();
                await ExecuteAsync(database, """
                    CREATE SCHEMA auth;
                    CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE SET search_path = pg_catalog
                    AS $auth_uid$ SELECT NULLIF(current_setting('request.jwt.claim.sub', true), '')::uuid $auth_uid$;
                    REVOKE ALL ON SCHEMA auth FROM PUBLIC, anon;
                    REVOKE ALL ON FUNCTION auth.uid() FROM PUBLIC, anon;
                    GRANT USAGE ON SCHEMA auth TO authenticated, service_role;
                    GRANT EXECUTE ON FUNCTION auth.uid() TO authenticated, service_role;
                    """);
                foreach (var migration in Directory.GetFiles(MigrationsDirectory(), "*.sql").Order())
                    await ExecuteAsync(database, await File.ReadAllTextAsync(migration));
            }
            return new(adminBuilder, databaseName, NpgsqlDataSource.Create(databaseBuilder.ConnectionString));
        }

        public async Task<long> LongAsync(string sql, params object[] values)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await dataSource.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class FixedPricingService : IAiPricingService
    {
        public Task<AiPricingResult> QuoteAsync(AuthenticatedGatewayUser user, TrustedAiOperation operation,
            AiPricingVersion? expectedVersion = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiPricingResult(AiPricingStatus.Succeeded, "AI_PRICE_QUOTED",
                new(operation, new(20), new("security-matrix-v1"), DateTimeOffset.UnixEpoch)));
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDITION_POSTGRES_ADMIN_CONNECTION")))
                Skip = "Requires an isolated PostgreSQL admin connection for the PLAN 89 security matrix.";
        }
    }
}
