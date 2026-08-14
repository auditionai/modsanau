using System.Reflection;
using AuditionModStudio.Gateway;
using AuditionModStudio.Gateway.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Gateway.Tests;

public sealed class CreditLedgerContractTests
{
    private static readonly AuthenticatedGatewayUser User = new(Guid.Parse(
        "fd64d019-06f9-4212-aaf0-c38615a88a0f"));

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public void Idempotency_key_rejects_ambiguous_or_unsafe_values(string value)
    {
        Assert.Throws<ArgumentException>(() => new CreditIdempotencyKey(value));
    }

    [Fact]
    public void Idempotency_key_and_positive_amount_are_bounded_value_objects()
    {
        var key = new CreditIdempotencyKey("ai-job:123_retry-1.0");

        Assert.Equal("ai-job:123_retry-1.0", key.Value);
        Assert.Throws<ArgumentException>(() => new CreditIdempotencyKey(new string('a', 129)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositiveCreditAmount(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositiveCreditAmount(-1));
        Assert.Equal(1, new PositiveCreditAmount(1).Value);
    }

    [Fact]
    public async Task Missing_database_configuration_fails_closed_for_every_ledger_operation()
    {
        var service = new UnavailableCreditLedgerService();
        var key = new CreditIdempotencyKey("request-1");
        var amount = new PositiveCreditAmount(1);

        var query = await service.GetAsync(User);
        var commands = await Task.WhenAll(
            service.GrantAsync(User, amount, "admin:grant-1", key),
            service.ReserveAsync(User, amount, key),
            service.CaptureAsync(User, Guid.NewGuid(), amount, key),
            service.ReleaseAsync(User, Guid.NewGuid(), key),
            service.RefundAsync(User, Guid.NewGuid(), amount, key));

        Assert.Equal(TrustedServiceStatus.Unavailable, query.Status);
        Assert.All(commands, result =>
        {
            Assert.Equal(CreditLedgerOperationStatus.Unavailable, result.Status);
            Assert.Equal("CREDIT_SERVICE_UNAVAILABLE", result.DiagnosticCode);
            Assert.Null(result.Snapshot);
        });
    }

    [Fact]
    public async Task Postgres_service_rejects_invalid_server_commands_before_network_access()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=unused.invalid;Database=app;Username=gateway;Password=unused;SSL Mode=Require");
        var service = new PostgresCreditLedgerService(dataSource);
        var emptyUser = new AuthenticatedGatewayUser(Guid.Empty);
        var amount = new PositiveCreditAmount(1);
        var key = new CreditIdempotencyKey("request-1");

        var query = await service.GetAsync(emptyUser);
        var grant = await service.GrantAsync(User, amount, "contains space", key);
        var capture = await service.CaptureAsync(User, Guid.Empty, amount, key);
        var release = await service.ReleaseAsync(User, Guid.Empty, key);
        var refund = await service.RefundAsync(User, Guid.Empty, amount, key);

        Assert.Equal(TrustedServiceStatus.Rejected, query.Status);
        Assert.Equal("CREDIT_AUTHORITY_REFERENCE_INVALID", grant.DiagnosticCode);
        Assert.Equal("CREDIT_RESERVATION_INVALID", capture.DiagnosticCode);
        Assert.Equal("CREDIT_RESERVATION_INVALID", release.DiagnosticCode);
        Assert.Equal("CREDIT_TRANSACTION_INVALID", refund.DiagnosticCode);
        Assert.All(new[] { grant, capture, release, refund }, result =>
            Assert.Equal(CreditLedgerOperationStatus.Rejected, result.Status));
    }

    [Fact]
    public void Database_configuration_requires_tls_and_is_always_redacted()
    {
        var insecure = Options("Host=db.example;Database=app;Username=gateway;Password=server-secret;SSL Mode=Disable");
        var secure = Options("Host=db.example;Database=app;Username=gateway;Password=server-secret;SSL Mode=VerifyFull");

        Assert.False(insecure.TryGetCreditDatabaseConnectionString(out _));
        Assert.True(secure.TryGetCreditDatabaseConnectionString(out var normalized));
        Assert.Contains("SSL Mode=VerifyFull", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server-secret", secure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("db.example", secure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Gateway_di_uses_one_fail_closed_instance_when_database_is_unconfigured()
    {
        var services = new ServiceCollection();
        GatewayApplication.ConfigureServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var ledger = provider.GetRequiredService<ICreditLedgerService>();
        var query = provider.GetRequiredService<ITrustedCreditQueryService>();

        Assert.IsType<UnavailableCreditLedgerService>(ledger);
        Assert.Same(ledger, query);
    }

    [Fact]
    public void Migration_defines_transactional_append_only_credit_authority()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("BEGIN;", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT;", sql, StringComparison.Ordinal);
        Assert.All(new[]
        {
            "private.credit_wallets",
            "private.credit_reservations",
            "private.credit_ledger",
            "private.credit_refunds",
            "private.credit_idempotency",
        }, table => Assert.Contains($"CREATE TABLE {table}", sql, StringComparison.Ordinal));
        Assert.All(new[]
        {
            "private.credit_grant",
            "private.credit_reserve",
            "private.credit_capture",
            "private.credit_release",
            "private.credit_refund",
        }, function => Assert.Contains($"FUNCTION {function}", sql, StringComparison.Ordinal));
        Assert.Equal(5, Count(sql, "pg_advisory_xact_lock"));
        Assert.Equal(5, Count(sql, "PERFORM private.assert_credit_request"));
        Assert.True(Count(sql, "FOR UPDATE") >= 8);
        Assert.Equal(3, Count(sql, "append_only"));
        Assert.Contains("UNIQUE (user_id, operation, idempotency_key)", sql, StringComparison.Ordinal);
        Assert.Contains("CREDIT_IDEMPOTENCY_CONFLICT", sql, StringComparison.Ordinal);
        Assert.Contains("CREDIT_REFUND_EXCEEDS_CAPTURE", sql, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_denies_client_roles_and_only_grants_server_execution()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("REVOKE ALL ON SCHEMA private FROM PUBLIC, anon, authenticated;", sql,
            StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC, anon, authenticated, service_role;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALL TABLES IN SCHEMA private", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALL FUNCTIONS IN SCHEMA private", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION private.credit_", sql.Split("GRANT USAGE")[0],
            StringComparison.Ordinal);
        Assert.DoesNotContain("TO anon", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TO authenticated", sql, StringComparison.Ordinal);
        Assert.Equal(5, Count(sql, "GRANT EXECUTE ON FUNCTION private.credit_"));
        Assert.Equal(7, Count(sql, "TO service_role;"));
    }

    [Fact]
    public void Gateway_exposes_no_credit_mutation_request_contract()
    {
        var endpointAssembly = typeof(GatewayApplication).Assembly;
        var endpointNamespaceTypes = endpointAssembly.GetTypes()
            .Where(type => type.Namespace == "AuditionModStudio.Gateway.Endpoints")
            .ToArray();

        Assert.DoesNotContain(endpointNamespaceTypes, type =>
            !type.Name.StartsWith("Admin", StringComparison.Ordinal) &&
            type.Name.Contains("Credit", StringComparison.Ordinal)
            && type.Name.EndsWith("Request", StringComparison.Ordinal));
        Assert.DoesNotContain(endpointNamespaceTypes.SelectMany(type => type.GetProperties()), property =>
            property.Name.Contains("Balance", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Cost", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Refund", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Payment", StringComparison.OrdinalIgnoreCase));
    }

    private static TrustedGatewayOptions Options(string connectionString) => new()
    {
        CreditDatabaseConnectionString = connectionString,
    };

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations",
            "202608120001_plan60_credit_ledger.sql");
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;
}
