using AuditionModStudio.Gateway.Services;
using Npgsql;

namespace Gateway.Tests;

[CollectionDefinition("Postgres integration", DisableParallelization = true)]
public sealed class PostgresIntegrationCollection;

[Collection("Postgres integration")]
public sealed class CreditConcurrencyIntegrationTests
{
    [Fact]
    public void Plan85_migration_repairs_existing_functions_without_a_second_credit_schema()
    {
        var sql = File.ReadAllText(Path.Combine(MigrationsDirectory(),
            "202608120006_plan85_credit_concurrency_fix.sql"));

        Assert.All(new[] { "grant", "reserve", "capture", "release", "refund" }, operation =>
            Assert.Contains($"CREATE OR REPLACE FUNCTION private.credit_{operation}", sql,
                StringComparison.Ordinal));
        Assert.Equal(5, Count(sql, "CREATE OR REPLACE FUNCTION private.credit_"));
        Assert.Equal(5, Count(sql, "GRANT EXECUTE ON FUNCTION private.credit_"));
        Assert.Contains("UPDATE private.credit_wallets AS w SET", sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE private.credit_reservations AS r SET", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE SCHEMA", sql, StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC, anon, authenticated, service_role;", sql, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Real_postgres_executes_complete_reserve_capture_release_refund_lifecycle()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var user = User();

        var grant = await database.Service.GrantAsync(user, Amount(100), "test:lifecycle",
            Key("grant-lifecycle"));
        var reservation = await database.Service.ReserveAsync(user, Amount(60), Key("reserve-lifecycle"));
        var capture = await database.Service.CaptureAsync(user, reservation.ReservationId!.Value,
            Amount(40), Key("capture-lifecycle"));
        var refund = await database.Service.RefundAsync(user, capture.TransactionId!.Value,
            Amount(15), Key("refund-lifecycle"));
        var snapshot = await database.Service.GetAsync(user);
        var account = await database.Account.GetAsync(user);

        AssertApplied(grant);
        AssertApplied(reservation);
        AssertApplied(capture);
        AssertApplied(refund);
        Assert.Equal(new TrustedCreditSnapshot(75, 0), snapshot.Snapshot);
        Assert.Equal(TrustedServiceStatus.Succeeded, account.Status);
        Assert.Equal(75, account.Snapshot!.AvailableCredits);
        Assert.Equal(0, account.Snapshot.ReservedCredits);
        Assert.Equal(100, account.Snapshot.CreditsGranted);
        Assert.Equal(25, account.Snapshot.CreditsUsed);
        Assert.Equal(4, account.Snapshot.TransactionCount);
        Assert.Equal(["refund", "capture", "reserve", "grant"],
            account.Snapshot.Transactions.Select(item => item.Kind));
        Assert.Equal(["grant", "reserve", "capture", "refund"],
            await database.StringsAsync("SELECT entry_type FROM private.credit_ledger WHERE user_id=$1 ORDER BY created_at",
                user.UserId));
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_refunds WHERE user_id=$1", user.UserId));
    }

    [PostgresFact]
    public async Task Verified_payment_fulfillment_now_grants_once_through_the_same_ledger()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        var payment = new VerifiedPaymentEvent("stripe", "evt_plan85", "cs_plan85", userId,
            "credits.small", 499, "usd", 50, new string('A', 64), new string('B', 64));

        var applied = await database.PaymentFulfillment.ApplyAsync(payment);
        var replay = await database.PaymentFulfillment.ApplyAsync(payment);

        Assert.Equal(new PaymentApplyResult(PaymentApplyStatus.Applied, "PAYMENT_APPLIED"), applied);
        Assert.Equal(new PaymentApplyResult(PaymentApplyStatus.Replay, "PAYMENT_IDEMPOTENT_REPLAY"), replay);
        Assert.Equal(new TrustedCreditSnapshot(50, 0),
            (await database.Service.GetAsync(new AuthenticatedGatewayUser(userId))).Snapshot);
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.payment_events WHERE user_id=$1", userId));
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_ledger WHERE user_id=$1 AND entry_type='grant'", userId));
    }

    [PostgresFact]
    public async Task Parallel_reservations_cannot_overspend_one_wallet()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var user = User();
        AssertApplied(await database.Service.GrantAsync(user, Amount(100), "test:reserve-race",
            Key("grant-reserve-race")));

        var results = await StartTogetherAsync(8, index => database.Service.ReserveAsync(
            user, Amount(30), Key($"reserve-race-{index}")));

        Assert.Equal(3, results.Count(IsSucceeded));
        Assert.Equal(5, results.Count(result => result.Status == CreditLedgerOperationStatus.Rejected
                                               && result.DiagnosticCode == "CREDIT_INSUFFICIENT"));
        Assert.Equal(new TrustedCreditSnapshot(10, 90), (await database.Service.GetAsync(user)).Snapshot);
        Assert.Equal(3L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_reservations WHERE user_id=$1 AND status='open'", user.UserId));
        Assert.Equal(3L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_ledger WHERE user_id=$1 AND entry_type='reserve'", user.UserId));
    }

    [PostgresFact]
    public async Task Parallel_duplicate_key_applies_exactly_once_and_replays_one_result()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var user = User();
        AssertApplied(await database.Service.GrantAsync(user, Amount(100), "test:duplicate",
            Key("grant-duplicate")));

        var results = await StartTogetherAsync(8, _ => database.Service.ReserveAsync(
            user, Amount(20), Key("same-reserve-key")));

        Assert.All(results, result => Assert.Equal(CreditLedgerOperationStatus.Succeeded, result.Status));
        Assert.Single(results, result => result.DiagnosticCode == "CREDIT_APPLIED");
        Assert.Equal(7, results.Count(result => result.DiagnosticCode == "CREDIT_IDEMPOTENT_REPLAY"));
        Assert.Single(results.Select(result => result.ReservationId).Distinct());
        Assert.Single(results.Select(result => result.TransactionId).Distinct());
        Assert.Equal(new TrustedCreditSnapshot(80, 20), (await database.Service.GetAsync(user)).Snapshot);
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_ledger WHERE user_id=$1 AND entry_type='reserve'", user.UserId));
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_idempotency WHERE user_id=$1 AND operation='reserve' AND idempotency_key='same-reserve-key'",
            user.UserId));
    }

    [PostgresFact]
    public async Task Conflicting_payload_for_one_key_rejects_without_partial_mutation()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var user = User();
        AssertApplied(await database.Service.GrantAsync(user, Amount(100), "test:conflict",
            Key("grant-conflict")));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await gate.Task;
            return await database.Service.ReserveAsync(user, Amount(20), Key("conflicting-key"));
        });
        var second = Task.Run(async () =>
        {
            await gate.Task;
            return await database.Service.ReserveAsync(user, Amount(30), Key("conflicting-key"));
        });
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, IsSucceeded);
        Assert.Single(results, result => result.Status == CreditLedgerOperationStatus.Rejected
                                         && result.DiagnosticCode == "CREDIT_IDEMPOTENCY_CONFLICT");
        var snapshot = (await database.Service.GetAsync(user)).Snapshot!;
        Assert.Equal(100, snapshot.AvailableCredits + snapshot.ReservedCredits);
        Assert.Contains(snapshot.ReservedCredits, new long[] { 20, 30 });
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_ledger WHERE user_id=$1 AND entry_type='reserve'", user.UserId));
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_idempotency WHERE user_id=$1 AND operation='reserve' AND idempotency_key='conflicting-key'",
            user.UserId));
    }

    [PostgresFact]
    public async Task Capture_and_release_race_has_exactly_one_terminal_transition()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var user = User();
        AssertApplied(await database.Service.GrantAsync(user, Amount(100), "test:terminal-race",
            Key("grant-terminal-race")));
        var reservation = await database.Service.ReserveAsync(user, Amount(60), Key("reserve-terminal-race"));
        AssertApplied(reservation);
        var reservationId = reservation.ReservationId!.Value;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = Task.Run(async () =>
        {
            await gate.Task;
            return await database.Service.CaptureAsync(user, reservationId,
                Amount(40), Key("capture-terminal-race"));
        });
        var release = Task.Run(async () =>
        {
            await gate.Task;
            return await database.Service.ReleaseAsync(user, reservationId,
                Key("release-terminal-race"));
        });
        gate.SetResult();
        var results = await Task.WhenAll(capture, release);

        Assert.Single(results, IsSucceeded);
        Assert.Single(results, result => result.Status == CreditLedgerOperationStatus.Rejected
                                         && result.DiagnosticCode == "CREDIT_RESERVATION_NOT_OPEN");
        var state = Assert.Single(await database.StringsAsync(
            "SELECT status FROM private.credit_reservations WHERE reservation_id=$1",
            reservationId));
        Assert.Contains(state, new[] { "captured", "released" });
        var snapshot = (await database.Service.GetAsync(user)).Snapshot!;
        Assert.Equal(0, snapshot.ReservedCredits);
        Assert.Equal(state == "captured" ? 60 : 100, snapshot.AvailableCredits);
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_ledger WHERE reservation_id=$1 AND entry_type IN ('capture','release')",
            reservationId));
    }

    [PostgresFact]
    public async Task Parallel_refunds_cannot_exceed_captured_amount()
    {
        await using var database = await CreditDatabase.CreateAsync();
        var user = User();
        AssertApplied(await database.Service.GrantAsync(user, Amount(100), "test:refund-race",
            Key("grant-refund-race")));
        var reservation = await database.Service.ReserveAsync(user, Amount(80), Key("reserve-refund-race"));
        var capture = await database.Service.CaptureAsync(user, reservation.ReservationId!.Value,
            Amount(60), Key("capture-refund-race"));
        AssertApplied(capture);
        var captureTransactionId = capture.TransactionId!.Value;

        var results = await StartTogetherAsync(2, index => database.Service.RefundAsync(
            user, captureTransactionId, Amount(40), Key($"refund-race-{index}")));

        Assert.Single(results, IsSucceeded);
        Assert.Single(results, result => result.Status == CreditLedgerOperationStatus.Rejected
                                         && result.DiagnosticCode == "CREDIT_REFUND_EXCEEDS_CAPTURE");
        Assert.Equal(new TrustedCreditSnapshot(80, 0), (await database.Service.GetAsync(user)).Snapshot);
        Assert.Equal(1L, await database.LongAsync(
            "SELECT count(*) FROM private.credit_refunds WHERE captured_transaction_id=$1", captureTransactionId));
        Assert.Equal(40L, await database.LongAsync(
            "SELECT COALESCE(sum(amount),0) FROM private.credit_refunds WHERE captured_transaction_id=$1",
            captureTransactionId));
    }

    private static async Task<CreditLedgerOperationResult[]> StartTogetherAsync(
        int count, Func<int, Task<CreditLedgerOperationResult>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            await gate.Task;
            return await action(index);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static bool IsSucceeded(CreditLedgerOperationResult result) =>
        result.Status == CreditLedgerOperationStatus.Succeeded;

    private static void AssertApplied(CreditLedgerOperationResult result)
    {
        Assert.Equal(CreditLedgerOperationStatus.Succeeded, result.Status);
        Assert.Equal("CREDIT_APPLIED", result.DiagnosticCode);
        Assert.NotNull(result.Snapshot);
        Assert.NotNull(result.TransactionId);
    }

    private static AuthenticatedGatewayUser User() => new(Guid.NewGuid());
    private static PositiveCreditAmount Amount(long value) => new(value);
    private static CreditIdempotencyKey Key(string value) => new(value);

    private static string MigrationsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations");
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private sealed class CreditDatabase(
        NpgsqlConnectionStringBuilder adminBuilder,
        string databaseName,
        NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public PostgresCreditLedgerService Service { get; } = new(dataSource);
        public PostgresAccountQueryService Account { get; } = new(dataSource);
        public PostgresPaymentFulfillmentService PaymentFulfillment { get; } = new(dataSource);

        public static async Task<CreditDatabase> CreateAsync()
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("AUDITION_POSTGRES_ADMIN_CONNECTION"));
            Assert.True(adminBuilder.Host is "127.0.0.1" or "localhost" or "::1",
                "PLAN 85 integration database must be loopback-only.");
            var databaseName = $"audition_plan85_{Guid.NewGuid():N}";
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
                foreach (var migration in Directory.GetFiles(CreditConcurrencyIntegrationTests.MigrationsDirectory(), "*.sql").Order())
                    await ExecuteAsync(database, await File.ReadAllTextAsync(migration));
            }
            return new CreditDatabase(adminBuilder, databaseName,
                NpgsqlDataSource.Create(databaseBuilder.ConnectionString));
        }

        public async Task<long> LongAsync(string sql, params object[] values)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<string[]> StringsAsync(string sql, params object[] values)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await using var reader = await command.ExecuteReaderAsync();
            var output = new List<string>();
            while (await reader.ReadAsync()) output.Add(reader.GetString(0));
            return output.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await dataSource.DisposeAsync();
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

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDITION_POSTGRES_ADMIN_CONNECTION")))
                Skip = "Requires an isolated PostgreSQL admin connection for the PLAN 85 concurrency gate.";
        }
    }
}
