using System.Collections.Immutable;

namespace AuditionModStudio.Gateway.Services;

public readonly record struct AiContentId
{
    public AiContentId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ArgumentException("AI content identifier is invalid.", nameof(value));
        Value = value;
    }
    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public enum AiContentKind { SourceImage, Mask, ProviderOutput }
public sealed record AiValidatedMedia(string MediaType, int Width, int Height, ImmutableArray<byte> Bytes)
{
    public bool IsValid => MediaType is "image/png" or "image/jpeg" or "image/webp" or "image/bmp"
        && Width is > 0 and <= 16_384 && Height is > 0 and <= 16_384
        && (long)Width * Height <= 100_000_000 && Bytes is { IsDefault: false, IsEmpty: false }
        && Bytes.Length <= 16 * 1024 * 1024;
}
public sealed record AiContentMetadata(AiContentId ContentId, Guid OwnerUserId, AiContentKind Kind,
    string MediaType, int Width, int Height, int ByteLength, string Sha256,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record AiStoredContent(AiContentMetadata Metadata, ImmutableArray<byte> Bytes);
public sealed record AiContentStoreResult(bool Succeeded, string DiagnosticCode, AiContentMetadata? Metadata);

public interface IAiContentStore
{
    Task<AiContentStoreResult> PutAsync(AuthenticatedGatewayUser owner, AiContentKind kind,
        AiValidatedMedia media, CancellationToken cancellationToken = default);
    Task<AiStoredContent?> GetAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
        AiContentKind expectedKind, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
        CancellationToken cancellationToken = default);
}

public interface IAiMediaValidator
{
    Task<AiValidatedMedia?> ValidateAsync(string mediaType, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedAiProviderProfile(TrustedAiOperation Operation, string PublicOptionId,
    string ProviderId, string ModelProfileId);
public interface ITrustedAiProviderCatalog
{
    bool TryResolve(TrustedAiOperation operation, string publicOptionId, out TrustedAiProviderProfile? profile);
}
public sealed class TrustedAiProviderCatalog : ITrustedAiProviderCatalog
{
    private readonly IReadOnlyDictionary<(TrustedAiOperation, string), TrustedAiProviderProfile> _profiles;
    public TrustedAiProviderCatalog(IEnumerable<TrustedAiProviderProfile> profiles)
    {
        _profiles = profiles.Where(profile => Enum.IsDefined(profile.Operation)
                && IsOption(profile.PublicOptionId) && IsOption(profile.ProviderId) && IsOption(profile.ModelProfileId))
            .ToDictionary(profile => (profile.Operation, profile.PublicOptionId), profile => profile);
    }
    public static bool TryCreate(IEnumerable<TrustedAiProviderProfile> profiles,
        out TrustedAiProviderCatalog? catalog)
    {
        catalog = null;
        var values = profiles.ToArray();
        if (values.Any(profile => !Enum.IsDefined(profile.Operation)
                || !IsOption(profile.PublicOptionId) || !IsOption(profile.ProviderId)
                || !IsOption(profile.ModelProfileId))
            || values.GroupBy(profile => (profile.Operation, profile.PublicOptionId)).Any(group => group.Count() > 1))
            return false;
        catalog = new(values);
        return true;
    }
    public bool TryResolve(TrustedAiOperation operation, string publicOptionId,
        out TrustedAiProviderProfile? profile) => _profiles.TryGetValue((operation, publicOptionId), out profile);
    private static bool IsOption(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public enum TrustedAiProviderOutcome { Succeeded, ClearFailure, Ambiguous, Unavailable }
public sealed record TrustedAiProviderRequest(TrustedAiOperation Operation, TrustedAiProviderProfile Profile,
    AiStoredContent Source, AiStoredContent? Mask, string? Prompt, int? TargetWidth, int? TargetHeight);
public sealed record TrustedAiProviderResult(TrustedAiProviderOutcome Outcome, string DiagnosticCode,
    string ProviderRequestId, string? MediaType, ImmutableArray<byte> OutputBytes);
public interface ITrustedAiProvider
{
    Task<TrustedAiProviderResult> ExecuteAsync(TrustedAiProviderRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AiWorkerExecutionResult(bool Succeeded, string DiagnosticCode, AiJobStatus? FinalState);
public interface IAiJobExecutionService
{
    Task<AiWorkerExecutionResult> ExecuteNextAsync(CancellationToken cancellationToken = default);
}

public sealed class AiJobExecutionService(IAiJobWorkerService jobs, IAiContentStore content,
    IAiMediaValidator mediaValidator, ITrustedAiProviderCatalog catalog, ITrustedAiProvider provider)
    : IAiJobExecutionService
{
    public async Task<AiWorkerExecutionResult> ExecuteNextAsync(CancellationToken cancellationToken = default)
    {
        var lease = await jobs.ClaimAsync(300, cancellationToken).ConfigureAwait(false);
        if (lease is null) return new(false, "AI_JOB_NONE_AVAILABLE", null);
        var metadata = lease.Job.InputMetadata;
        if (!TryContentId(metadata.InputReference, out var sourceId)
            || metadata.MaskReference is not null && !TryContentId(metadata.MaskReference, out _)
            || !catalog.TryResolve(lease.Job.Operation, metadata.PublicOptionId, out var profile))
            return await FailAsync(lease, "AI_JOB_INPUT_INVALID", cancellationToken).ConfigureAwait(false);

        var source = await content.GetAsync(lease.Owner, sourceId, AiContentKind.SourceImage, cancellationToken)
            .ConfigureAwait(false);
        AiStoredContent? mask = null;
        if (metadata.MaskReference is not null)
        {
            TryContentId(metadata.MaskReference, out var maskId);
            mask = await content.GetAsync(lease.Owner, maskId, AiContentKind.Mask, cancellationToken)
                .ConfigureAwait(false);
        }
        if (source is null || source.Metadata.Width != metadata.SourceWidth
            || source.Metadata.Height != metadata.SourceHeight || source.Metadata.ByteLength != metadata.InputBytes
            || metadata.MaskReference is not null && mask is null
            || mask is not null && (mask.Metadata.Width != source.Metadata.Width
                                    || mask.Metadata.Height != source.Metadata.Height))
            return await FailAsync(lease, "AI_JOB_CONTENT_INVALID", cancellationToken).ConfigureAwait(false);

        TrustedAiProviderResult providerResult;
        try
        {
            providerResult = await provider.ExecuteAsync(new(lease.Job.Operation, profile!, source, mask,
                metadata.Prompt, metadata.TargetWidth, metadata.TargetHeight), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException)
        {
            var reconciled = await jobs.RequireReconciliationAsync(lease.Job.JobId, lease.LeaseToken,
                "provider-unknown", "AI_PROVIDER_OUTCOME_AMBIGUOUS", cancellationToken).ConfigureAwait(false);
            return new(false, reconciled.DiagnosticCode, reconciled.Job?.Status);
        }
        if (providerResult.Outcome == TrustedAiProviderOutcome.Ambiguous)
        {
            var reconciled = await jobs.RequireReconciliationAsync(lease.Job.JobId, lease.LeaseToken,
                SafeProviderId(providerResult.ProviderRequestId), "AI_PROVIDER_OUTCOME_AMBIGUOUS", cancellationToken)
                .ConfigureAwait(false);
            return new(false, reconciled.DiagnosticCode, reconciled.Job?.Status);
        }
        if (providerResult.Outcome != TrustedAiProviderOutcome.Succeeded || providerResult.MediaType is null)
            return await FailAsync(lease, providerResult.DiagnosticCode, cancellationToken,
                providerResult.ProviderRequestId).ConfigureAwait(false);

        var output = await mediaValidator.ValidateAsync(providerResult.MediaType,
            providerResult.OutputBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            var reconciled = await jobs.RequireReconciliationAsync(lease.Job.JobId, lease.LeaseToken,
                SafeProviderId(providerResult.ProviderRequestId), "AI_PROVIDER_OUTPUT_INVALID", cancellationToken)
                .ConfigureAwait(false);
            return new(false, reconciled.DiagnosticCode, reconciled.Job?.Status);
        }
        var expectedWidth = lease.Job.Operation is TrustedAiOperation.Outpaint or TrustedAiOperation.Upscale
            ? metadata.TargetWidth : source.Metadata.Width;
        var expectedHeight = lease.Job.Operation is TrustedAiOperation.Outpaint or TrustedAiOperation.Upscale
            ? metadata.TargetHeight : source.Metadata.Height;
        if (output.Width != expectedWidth || output.Height != expectedHeight)
        {
            var reconciled = await jobs.RequireReconciliationAsync(lease.Job.JobId, lease.LeaseToken,
                SafeProviderId(providerResult.ProviderRequestId), "AI_PROVIDER_DIMENSIONS_INVALID", cancellationToken)
                .ConfigureAwait(false);
            return new(false, reconciled.DiagnosticCode, reconciled.Job?.Status);
        }
        var stored = await content.PutAsync(lease.Owner, AiContentKind.ProviderOutput, output, cancellationToken)
            .ConfigureAwait(false);
        if (!stored.Succeeded || stored.Metadata is null)
        {
            var reconciled = await jobs.RequireReconciliationAsync(lease.Job.JobId, lease.LeaseToken,
                SafeProviderId(providerResult.ProviderRequestId), "AI_OUTPUT_PERSISTENCE_UNCERTAIN", cancellationToken)
                .ConfigureAwait(false);
            return new(false, reconciled.DiagnosticCode, reconciled.Job?.Status);
        }
        var completed = await jobs.CompleteAsync(lease.Job.JobId, lease.LeaseToken,
            lease.Job.ReservedCredits, stored.Metadata.ContentId.Value,
            SafeProviderId(providerResult.ProviderRequestId), cancellationToken).ConfigureAwait(false);
        if (completed.Status != AiJobOperationStatus.Succeeded)
        {
            var reconciled = await jobs.RequireReconciliationAsync(lease.Job.JobId, lease.LeaseToken,
                SafeProviderId(providerResult.ProviderRequestId), "AI_COMPLETION_UNCERTAIN", cancellationToken)
                .ConfigureAwait(false);
            return new(false, reconciled.DiagnosticCode, reconciled.Job?.Status);
        }
        return new(completed.Status == AiJobOperationStatus.Succeeded, completed.DiagnosticCode,
            completed.Job?.Status);
    }

    private async Task<AiWorkerExecutionResult> FailAsync(AiJobWorkerLease lease, string code,
        CancellationToken cancellationToken, string providerRequestId = "not-started")
    {
        var failed = await jobs.FailAsync(lease.Job.JobId, lease.LeaseToken,
            SafeProviderId(providerRequestId), SafeCode(code), false, cancellationToken).ConfigureAwait(false);
        return new(false, failed.DiagnosticCode, failed.Job?.Status);
    }
    private static bool TryContentId(string value, out AiContentId id)
    {
        try { id = new(value); return true; } catch (ArgumentException) { id = default; return false; }
    }
    private static string SafeProviderId(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':') ? value : "provider-unknown";
    private static string SafeCode(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
        ? value : "AI_PROVIDER_FAILED";
}

public sealed class UnavailableAiContentStore : IAiContentStore
{
    public Task<AiContentStoreResult> PutAsync(AuthenticatedGatewayUser owner, AiContentKind kind,
        AiValidatedMedia media, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiContentStoreResult(false, "AI_CONTENT_STORE_UNAVAILABLE", null));
    public Task<AiStoredContent?> GetAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
        AiContentKind expectedKind, CancellationToken cancellationToken = default) => Task.FromResult<AiStoredContent?>(null);
    public Task<bool> DeleteAsync(AuthenticatedGatewayUser owner, AiContentId contentId,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
public sealed class UnavailableAiMediaValidator : IAiMediaValidator
{
    public Task<AiValidatedMedia?> ValidateAsync(string mediaType, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) => Task.FromResult<AiValidatedMedia?>(null);
}
public sealed class UnavailableTrustedAiProvider : ITrustedAiProvider
{
    public Task<TrustedAiProviderResult> ExecuteAsync(TrustedAiProviderRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(new TrustedAiProviderResult(
        TrustedAiProviderOutcome.Unavailable, "AI_PROVIDER_UNAVAILABLE", "not-started", null, []));
}
