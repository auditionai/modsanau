using Npgsql;

namespace Gateway.Tests;

public sealed class SupabaseRlsHardeningTests
{
    private static readonly string[] UserOwnedTables =
    [
        "credit_wallets",
        "credit_reservations",
        "credit_ledger",
        "credit_refunds",
        "credit_idempotency",
        "ai_jobs",
    ];

    [Fact]
    public void Plan83_migration_audits_every_user_owned_table_and_only_grants_safe_read_columns()
    {
        var sql = File.ReadAllText(MigrationPath("202608120004_plan83_supabase_rls_hardening.sql"));

        Assert.All(UserOwnedTables, table =>
        {
            Assert.Contains($"ALTER TABLE private.{table} ENABLE ROW LEVEL SECURITY;", sql,
                StringComparison.Ordinal);
            Assert.Contains($"ALTER TABLE private.{table} FORCE ROW LEVEL SECURITY;", sql,
                StringComparison.Ordinal);
        });
        Assert.Equal(5, Count(sql, "FOR SELECT TO authenticated"));
        Assert.Equal(5, Count(sql, "USING ((SELECT auth.uid()) = user_id);"));
        Assert.Contains("credit_idempotency remains server-only", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ON TABLE private.credit_idempotency TO authenticated", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("provider_request_id", AuthenticatedGrants(sql), StringComparison.Ordinal);
        Assert.DoesNotContain("lease_token", AuthenticatedGrants(sql), StringComparison.Ordinal);
        Assert.DoesNotContain("request_hash", AuthenticatedGrants(sql), StringComparison.Ordinal);
        Assert.DoesNotContain("idempotency_key", AuthenticatedGrants(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan83_migration_grants_no_client_mutation_or_function_execution()
    {
        var sql = File.ReadAllText(MigrationPath("202608120004_plan83_supabase_rls_hardening.sql"));
        var grants = AuthenticatedGrants(sql);

        Assert.Contains("GRANT USAGE ON SCHEMA private TO authenticated;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT INSERT", grants, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT UPDATE", grants, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT DELETE", grants, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE", grants, StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC, anon, authenticated;", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER DEFAULT PRIVILEGES IN SCHEMA private REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;",
            sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT USAGE ON SCHEMA private TO anon", sql, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Real_postgres_rls_blocks_cross_user_sensitive_columns_mutation_and_rpc()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("AUDITION_POSTGRES_ADMIN_CONNECTION")!;
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        Assert.True(adminBuilder.Host is "127.0.0.1" or "localhost" or "::1",
            "PLAN 83 integration database must be loopback-only.");

        var databaseName = $"audition_plan83_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, """
            DO $roles$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                    CREATE ROLE anon NOLOGIN;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                    CREATE ROLE authenticated NOLOGIN;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
                    CREATE ROLE service_role NOLOGIN BYPASSRLS;
                END IF;
            END
            $roles$;
            """);
        await ExecuteAsync(admin, $"CREATE DATABASE {databaseName};");

        try
        {
            var databaseBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
            };
            await using var database = new NpgsqlConnection(databaseBuilder.ConnectionString);
            await database.OpenAsync();
            await ExecuteAsync(database, """
                CREATE SCHEMA auth;
                CREATE FUNCTION auth.uid()
                RETURNS uuid
                LANGUAGE sql
                STABLE
                SET search_path = pg_catalog
                AS $auth_uid$
                    SELECT NULLIF(current_setting('request.jwt.claim.sub', true), '')::uuid
                $auth_uid$;
                REVOKE ALL ON SCHEMA auth FROM PUBLIC, anon;
                REVOKE ALL ON FUNCTION auth.uid() FROM PUBLIC, anon;
                GRANT USAGE ON SCHEMA auth TO authenticated, service_role;
                GRANT EXECUTE ON FUNCTION auth.uid() TO authenticated, service_role;
                """);
            foreach (var migration in Directory.GetFiles(MigrationsDirectory(), "*.sql").Order())
            {
                await ExecuteAsync(database, await File.ReadAllTextAsync(migration));
            }

            var owner = Guid.NewGuid();
            var stranger = Guid.NewGuid();
            await SeedOwnerRowsAsync(database, owner);
            await AssertCatalogPolicyAsync(database);
            await AssertOwnerReadsAsync(database, owner, expectedRows: true);
            await AssertOwnerReadsAsync(database, stranger, expectedRows: false);
            await AssertDeniedAsync(database, owner,
                "SELECT request_hash FROM private.credit_idempotency LIMIT 1");
            await AssertDeniedAsync(database, owner,
                "SELECT provider_request_id FROM private.ai_jobs LIMIT 1");
            await AssertDeniedAsync(database, owner,
                "SELECT authority_reference FROM private.credit_ledger LIMIT 1");
            await AssertDeniedAsync(database, owner,
                "UPDATE private.credit_wallets SET available_credits = 999 WHERE user_id = auth.uid()");
            await AssertDeniedAsync(database, owner,
                "DELETE FROM private.credit_ledger WHERE user_id = auth.uid()");
            await AssertDeniedAsync(database, owner,
                "SELECT * FROM private.credit_grant(auth.uid(), 1, 'forged', 'forged', repeat('A', 64))");
            await AssertAnonDeniedAsync(database);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
        }
    }

    private static async Task SeedOwnerRowsAsync(NpgsqlConnection connection, Guid owner)
    {
        var reservation = Guid.NewGuid();
        var capture = Guid.NewGuid();
        var refund = Guid.NewGuid();
        var hash = new string('A', 64);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.credit_wallets(user_id, available_credits, reserved_credits)
                VALUES ($1, 80, 20)
            """, owner);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.credit_reservations(
                reservation_id, user_id, reserved_credits, status)
                VALUES ($2, $1, 20, 'open')
            """, owner, reservation);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
                available_delta, reserved_delta, available_after, reserved_after, reservation_id)
                VALUES ($3, $1, 'capture', 15, 5, -20, 85, 0, $2)
            """, owner, reservation, capture);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.credit_ledger(transaction_id, user_id, entry_type, amount,
                available_delta, reserved_delta, available_after, reserved_after,
                reservation_id, related_transaction_id)
                VALUES ($4, $1, 'refund', 5, 5, 0, 90, 0, $2, $3)
            """, owner, reservation, capture, refund);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.credit_refunds(user_id, captured_transaction_id,
                refund_transaction_id, amount) VALUES ($1, $2, $3, 5)
            """, owner, capture, refund);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.credit_idempotency(user_id, operation, idempotency_key,
                request_hash, available_credits, reserved_credits, reservation_id, transaction_id)
                VALUES ($1, 'refund', 'fixture-refund', $2, 90, 0, $3, $4)
            """, owner, hash, reservation, refund);
        await ExecuteParameterizedAsync(connection, """
            INSERT INTO private.ai_jobs(user_id, operation, input_metadata, status,
                pricing_version, reserved_credits, reservation_id, idempotency_key, request_hash)
                VALUES ($1, 'Generate',
                    '{"inputReference":"inputs/one","promptLength":1,"inputBytes":0}'::jsonb,
                    'Queued', 'v1', 20, $2, 'fixture-job', $3)
            """, owner, reservation, hash);
    }

    private static async Task AssertCatalogPolicyAsync(NpgsqlConnection connection)
    {
        var rows = await QueryAsync(connection, """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                count(p.polname) FILTER (WHERE p.polcmd = 'r') AS select_policies,
                count(p.polname) FILTER (WHERE p.polcmd <> 'r') AS mutation_policies
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policy p ON p.polrelid = c.oid
            WHERE n.nspname = 'private' AND c.relname IN
                ('credit_wallets', 'credit_reservations', 'credit_ledger',
                 'credit_refunds', 'credit_idempotency', 'ai_jobs')
            GROUP BY c.relname, c.relrowsecurity, c.relforcerowsecurity
            ORDER BY c.relname
            """);

        Assert.Equal(UserOwnedTables.Length, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.True((bool)row[1]);
            Assert.True((bool)row[2]);
            Assert.Equal(0L, (long)row[4]);
            Assert.Equal(row[0] is "credit_idempotency" ? 0L : 1L, (long)row[3]);
        });
    }

    private static async Task AssertOwnerReadsAsync(
        NpgsqlConnection connection,
        Guid subject,
        bool expectedRows)
    {
        await ExecuteAsync(connection, "SET ROLE authenticated;");
        try
        {
            await ScalarAsync(connection, "SELECT set_config('request.jwt.claim.sub', $1, false)",
                subject.ToString("D"));
            var queries = new[]
            {
                "SELECT count(user_id) FROM private.credit_wallets",
                "SELECT count(reservation_id) FROM private.credit_reservations",
                "SELECT count(transaction_id) FROM private.credit_ledger",
                "SELECT count(refund_id) FROM private.credit_refunds",
                "SELECT count(job_id) FROM private.ai_jobs",
            };
            foreach (var query in queries)
            {
                var count = (long)(await ScalarAsync(connection, query))!;
                Assert.Equal(expectedRows, count > 0);
            }
        }
        finally
        {
            await ExecuteAsync(connection, "RESET ROLE;");
        }
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection connection, Guid subject, string sql)
    {
        await ExecuteAsync(connection, "SET ROLE authenticated;");
        try
        {
            await ScalarAsync(connection, "SELECT set_config('request.jwt.claim.sub', $1, false)",
                subject.ToString("D"));
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
        finally
        {
            await ExecuteAsync(connection, "RESET ROLE;");
        }
    }

    private static async Task AssertAnonDeniedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, "SET ROLE anon;");
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, "SELECT user_id FROM private.credit_wallets"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
        finally
        {
            await ExecuteAsync(connection, "RESET ROLE;");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteParameterizedAsync(
        NpgsqlConnection connection,
        string sql,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<IReadOnlyList<object[]>> QueryAsync(
        NpgsqlConnection connection,
        string sql,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private static string AuthenticatedGrants(string sql)
    {
        var start = sql.IndexOf("GRANT USAGE ON SCHEMA private TO authenticated;", StringComparison.Ordinal);
        var end = sql.IndexOf("REVOKE ALL ON FUNCTION", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return sql[start..end];
    }

    private static string MigrationPath(string fileName) => Path.Combine(MigrationsDirectory(), fileName);

    private static string MigrationsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations");
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    "AUDITION_POSTGRES_ADMIN_CONNECTION")))
            {
                Skip = "Requires an isolated PostgreSQL admin connection for the PLAN 83 RLS integration gate.";
            }
        }
    }
}
