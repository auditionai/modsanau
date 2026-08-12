using System.Collections.Immutable;
using AuditionModStudio.Gateway.Services;

namespace Gateway.Tests;

public sealed class AiExecutionTests
{
    [Fact]
    public async Task Supported_operation_routes_trusted_profile_persists_output_then_completes_once()
    {
        var context = Create(TrustedAiProviderOutcome.Succeeded);

        var result = await context.Service.ExecuteNextAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(AiJobStatus.Completed, result.FinalState);
        Assert.Equal(1, context.Provider.CallCount);
        Assert.Equal(1, context.Jobs.CompleteCount);
        Assert.Equal(0, context.Jobs.FailCount);
        Assert.Equal("trusted-model", context.Provider.Request!.Profile.ModelProfileId);
        Assert.Equal(AiContentKind.ProviderOutput, context.Store.LastPutKind);
    }

    [Fact]
    public async Task Source_aligned_mask_is_required_before_provider_call()
    {
        var context = Create(TrustedAiProviderOutcome.Succeeded, maskHeight: 3);

        var result = await context.Service.ExecuteNextAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(0, context.Provider.CallCount);
        Assert.Equal(1, context.Jobs.FailCount);
    }

    [Fact]
    public async Task Ambiguous_provider_outcome_holds_reservation_for_reconciliation()
    {
        var context = Create(TrustedAiProviderOutcome.Ambiguous);

        var result = await context.Service.ExecuteNextAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(AiJobStatus.ReconciliationRequired, result.FinalState);
        Assert.Equal(1, context.Jobs.ReconciliationCount);
        Assert.Equal(0, context.Jobs.FailCount);
        Assert.Equal(0, context.Jobs.CompleteCount);
    }

    [Fact]
    public async Task Invalid_success_output_requires_reconciliation_instead_of_capture_or_release()
    {
        var context = Create(TrustedAiProviderOutcome.Succeeded, validOutput: false);

        var result = await context.Service.ExecuteNextAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(AiJobStatus.ReconciliationRequired, result.FinalState);
        Assert.Equal(1, context.Jobs.ReconciliationCount);
        Assert.Equal(0, context.Jobs.CompleteCount);
        Assert.Equal(0, context.Jobs.FailCount);
    }

    [Fact]
    public async Task Provider_timeout_after_dispatch_requires_reconciliation_without_capture_or_release()
    {
        var context = Create(TrustedAiProviderOutcome.Succeeded);
        context.Provider.Exception = new TimeoutException("simulated ambiguous timeout");

        var result = await context.Service.ExecuteNextAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(AiJobStatus.ReconciliationRequired, result.FinalState);
        Assert.Equal(1, context.Jobs.ReconciliationCount);
        Assert.Equal(0, context.Jobs.CompleteCount);
        Assert.Equal(0, context.Jobs.FailCount);
    }

    [Theory]
    [InlineData(TrustedAiProviderOutcome.ClearFailure)]
    [InlineData(TrustedAiProviderOutcome.Unavailable)]
    public async Task Clear_or_unavailable_provider_failure_uses_job_failure_release_path(
        TrustedAiProviderOutcome outcome)
    {
        var context = Create(outcome);

        var result = await context.Service.ExecuteNextAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(1, context.Jobs.FailCount);
        Assert.Equal(0, context.Jobs.CompleteCount);
    }

    [Fact]
    public void Catalog_rejects_unknown_public_option_and_never_accepts_arbitrary_model()
    {
        var catalog = new TrustedAiProviderCatalog([
            new(TrustedAiOperation.Upscale, "standard", "trusted-provider", "trusted-model")]);

        Assert.True(catalog.TryResolve(TrustedAiOperation.Upscale, "standard", out var profile));
        Assert.Equal("trusted-model", profile!.ModelProfileId);
        Assert.False(catalog.TryResolve(TrustedAiOperation.Upscale, "attacker/model", out _));
    }

    [Fact]
    public async Task Content_store_lookup_is_owner_and_kind_scoped()
    {
        var owner = new AuthenticatedGatewayUser(Guid.NewGuid());
        var item = Content(owner, "source_1", AiContentKind.SourceImage, 2, 2);
        var store = new StubContentStore(item);

        Assert.NotNull(await store.GetAsync(owner, new("source_1"), AiContentKind.SourceImage));
        Assert.Null(await store.GetAsync(new(Guid.NewGuid()), new("source_1"), AiContentKind.SourceImage));
        Assert.Null(await store.GetAsync(owner, new("source_1"), AiContentKind.Mask));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, item.Metadata.ContentId.Value);
    }

    private static Context Create(TrustedAiProviderOutcome outcome, int maskHeight = 2,
        bool validOutput = true)
    {
        var owner = new AuthenticatedGatewayUser(Guid.NewGuid());
        var source = Content(owner, "source_1", AiContentKind.SourceImage, 2, 2);
        var mask = Content(owner, "mask_1", AiContentKind.Mask, 2, maskHeight);
        var store = new StubContentStore(source, mask);
        var jobs = new StubJobs(owner);
        var provider = new StubProvider(outcome);
        var catalog = new TrustedAiProviderCatalog([
            new(TrustedAiOperation.Inpaint, "standard", "trusted-provider", "trusted-model")]);
        return new(new(jobs, store, new StubValidator(validOutput), catalog, provider), jobs, store, provider);
    }

    private static AiStoredContent Content(AuthenticatedGatewayUser owner, string id, AiContentKind kind,
        int width, int height) => new(new(new(id), owner.UserId, kind, "image/png", width, height, 8,
            new string('A', 64), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
        ImmutableArray.Create<byte>(1, 2, 3, 4, 5, 6, 7, 8));

    private sealed record Context(AiJobExecutionService Service, StubJobs Jobs,
        StubContentStore Store, StubProvider Provider);

    private sealed class StubJobs(AuthenticatedGatewayUser owner) : IAiJobWorkerService
    {
        private readonly AiJobSnapshot _job = new(Guid.NewGuid(), TrustedAiOperation.Inpaint,
            AiJobStatus.Processing, new("source_1", 6, null, null, 8, "mask_1", "standard", "repair", 2, 2),
            7, null, "v1", null, null, null, 1, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public int CompleteCount { get; private set; }
        public int FailCount { get; private set; }
        public int ReconciliationCount { get; private set; }
        public Task<AiJobWorkerLease?> ClaimAsync(int leaseSeconds, CancellationToken cancellationToken = default) =>
            Task.FromResult<AiJobWorkerLease?>(new(_job, owner, Guid.NewGuid()));
        public Task<AiJobOperationResult> CompleteAsync(Guid jobId, Guid leaseToken, long finalCredits,
            string outputReference, string providerRequestId, CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            return Task.FromResult(new AiJobOperationResult(AiJobOperationStatus.Succeeded, "AI_JOB_COMPLETED",
                _job with
                {
                    Status = AiJobStatus.Completed,
                    FinalCredits = finalCredits,
                    OutputReference = outputReference
                }));
        }
        public Task<AiJobOperationResult> FailAsync(Guid jobId, Guid leaseToken, string providerRequestId,
            string errorCode, bool retryable, CancellationToken cancellationToken = default)
        {
            FailCount++;
            return Task.FromResult(new AiJobOperationResult(AiJobOperationStatus.Succeeded, "AI_JOB_FAILED",
                _job with { Status = AiJobStatus.Failed, ErrorCode = errorCode }));
        }
        public Task<AiJobOperationResult> RequireReconciliationAsync(Guid jobId, Guid leaseToken,
            string providerRequestId, string errorCode, CancellationToken cancellationToken = default)
        {
            ReconciliationCount++;
            return Task.FromResult(new AiJobOperationResult(AiJobOperationStatus.Succeeded,
                "AI_JOB_RECONCILIATION_REQUIRED", _job with
                { Status = AiJobStatus.ReconciliationRequired, ErrorCode = errorCode }));
        }
    }

    private sealed class StubContentStore(params AiStoredContent[] items) : IAiContentStore
    {
        public AiContentKind? LastPutKind { get; private set; }
        public Task<AiContentStoreResult> PutAsync(AuthenticatedGatewayUser owner, AiContentKind kind,
            AiValidatedMedia media, CancellationToken cancellationToken = default)
        {
            LastPutKind = kind;
            var metadata = new AiContentMetadata(new("output_1"), owner.UserId, kind, media.MediaType,
                media.Width, media.Height, media.Bytes.Length, new string('B', 64), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1));
            return Task.FromResult(new AiContentStoreResult(true, "AI_CONTENT_STORED", metadata));
        }
        public Task<AiStoredContent?> GetAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
            AiContentKind expectedKind, CancellationToken cancellationToken = default) => Task.FromResult(
            items.FirstOrDefault(item => item.Metadata.OwnerUserId == owner.UserId
                && item.Metadata.ContentId == contentId && item.Metadata.Kind == expectedKind));
        public Task<bool> DeleteAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubProvider(TrustedAiProviderOutcome outcome) : ITrustedAiProvider
    {
        public int CallCount { get; private set; }
        public TrustedAiProviderRequest? Request { get; private set; }
        public Exception? Exception { get; set; }
        public Task<TrustedAiProviderResult> ExecuteAsync(TrustedAiProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            if (Exception is not null) throw Exception;
            return Task.FromResult(new TrustedAiProviderResult(outcome,
                outcome == TrustedAiProviderOutcome.Succeeded ? "AI_PROVIDER_SUCCEEDED" : "AI_PROVIDER_FAILED",
                "provider-request-1", outcome == TrustedAiProviderOutcome.Succeeded ? "image/png" : null,
                outcome == TrustedAiProviderOutcome.Succeeded ? ImmutableArray.Create<byte>(1, 2, 3) : []));
        }
    }

    private sealed class StubValidator(bool valid) : IAiMediaValidator
    {
        public Task<AiValidatedMedia?> ValidateAsync(string mediaType, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => Task.FromResult(valid
            ? new AiValidatedMedia(mediaType, 2, 2, ImmutableArray.CreateRange(bytes.ToArray()))
            : null);
    }
}
