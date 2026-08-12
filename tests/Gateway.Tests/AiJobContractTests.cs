using AuditionModStudio.Gateway;
using AuditionModStudio.Gateway.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Gateway.Tests;

public sealed class AiJobContractTests
{
    private static readonly AuthenticatedGatewayUser User = new(Guid.Parse(
        "fdbacaf8-da84-447d-91d7-a13ee8088c60"));
    private static readonly AiJobInputMetadata Metadata = new("inputs/request-1", 12, 512, 512, 1024);

    [Fact]
    public void Job_idempotency_and_metadata_are_strictly_bounded()
    {
        Assert.Equal("request:1", new AiJobIdempotencyKey("request:1").Value);
        Assert.Throws<ArgumentException>(() => new AiJobIdempotencyKey(new string('a', 81)));
        Assert.Throws<ArgumentException>(() => new AiJobIdempotencyKey("unsafe/value"));
        Assert.True(Metadata.IsValid);
        Assert.False((Metadata with { InputReference = "../secret" }).IsValid);
        Assert.False((Metadata with { PromptLength = 4_001 }).IsValid);
        Assert.False((Metadata with { TargetHeight = null }).IsValid);
        Assert.False((Metadata with { InputBytes = 4 * 1_024 * 1_024 + 1 }).IsValid);
    }

    [Fact]
    public async Task Missing_database_configuration_fails_closed_for_job_operations()
    {
        var service = new UnavailableAiJobService();
        var key = new AiJobIdempotencyKey("request-1");

        var enqueue = await service.EnqueueAsync(User, TrustedAiOperation.Generate, Metadata, key);
        var get = await service.GetAsync(User, Guid.NewGuid());
        var list = await service.ListAsync(User);
        var cancel = await service.CancelAsync(User, Guid.NewGuid());

        Assert.All(new[] { enqueue, get, cancel }, result =>
            Assert.Equal(AiJobOperationStatus.Unavailable, result.Status));
        Assert.Equal(AiJobOperationStatus.Unavailable, list.Status);
    }

    [Fact]
    public async Task Stale_pricing_stops_before_database_and_returns_current_quote()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=unused.invalid;Database=app;Username=gateway;Password=unused;SSL Mode=Require");
        var quote = new AiPricingQuote(TrustedAiOperation.Generate, new AiCreditPrice(9),
            new AiPricingVersion("v2"), DateTimeOffset.UnixEpoch);
        var service = new PostgresAiJobService(dataSource,
            new StubPricingService(new(AiPricingStatus.PriceChanged, "AI_PRICE_CHANGED", quote)));

        var result = await service.EnqueueAsync(User, TrustedAiOperation.Generate, Metadata,
            new AiJobIdempotencyKey("request-1"), new AiPricingVersion("v1"));

        Assert.Equal(AiJobOperationStatus.PriceChanged, result.Status);
        Assert.Equal(9, result.CurrentQuote!.CreditCost.Value);
    }

    [Fact]
    public void Gateway_di_uses_fail_closed_job_service_without_database()
    {
        var services = new ServiceCollection();
        GatewayApplication.ConfigureServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<UnavailableAiJobService>(provider.GetRequiredService<IAiJobService>());
    }

    [Fact]
    public void Migration_defines_durable_transactional_state_machine_and_credit_lifecycle()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("BEGIN;", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT;", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE private.ai_jobs", sql, StringComparison.Ordinal);
        Assert.All(Enum.GetNames<AiJobStatus>(), status =>
            Assert.Contains($"'{status}'", sql, StringComparison.Ordinal));
        Assert.All(new[]
        {
            "private.ai_job_enqueue",
            "private.ai_job_claim",
            "private.ai_job_complete",
            "private.ai_job_fail",
            "private.ai_job_cancel",
        }, function => Assert.Contains($"FUNCTION {function}", sql, StringComparison.Ordinal));
        Assert.Contains("private.credit_reserve", sql, StringComparison.Ordinal);
        Assert.Contains("private.credit_capture", sql, StringComparison.Ordinal);
        Assert.True(Count(sql, "private.credit_release") >= 3);
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at <= clock_timestamp()", sql, StringComparison.Ordinal);
        Assert.Contains("AI_JOB_IDEMPOTENCY_CONFLICT", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (user_id, idempotency_key)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_enforces_private_ownership_boundary_and_no_raw_image_storage()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("ALTER TABLE private.ai_jobs ENABLE ROW LEVEL SECURITY;", sql,
            StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON TABLE private.ai_jobs FROM PUBLIC, anon, authenticated, service_role;", sql,
            StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON TABLE private.ai_jobs TO service_role;", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(" TO anon", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" TO authenticated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("bytea", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("octet_length(input_metadata::text) BETWEEN 2 AND 16384", sql,
            StringComparison.Ordinal);
    }

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "supabase", "migrations",
            "202608120002_plan62_ai_jobs.sql");
    }

    private static int Count(string value, string token) =>
        (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private sealed class StubPricingService(AiPricingResult result) : IAiPricingService
    {
        public Task<AiPricingResult> QuoteAsync(
            AuthenticatedGatewayUser user, TrustedAiOperation operation,
            AiPricingVersion? expectedVersion = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
