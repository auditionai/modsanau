using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Cloud;

public sealed record GatewayAiStudioOptions(Uri BaseUri)
{
    public bool IsValid => BaseUri is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" }
                           && string.IsNullOrEmpty(BaseUri.UserInfo)
                           && string.IsNullOrEmpty(BaseUri.Query)
                           && string.IsNullOrEmpty(BaseUri.Fragment);
}

public sealed class GatewayAiStudioService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IAuthenticationService authenticationService,
    GatewayAiStudioOptions options) : IAiStudioService
{
    private const int MaximumResponseBytes = 256 * 1_024;

    public async Task<AiStudioQuoteResult> GetQuoteAsync(
        AiStudioOperation operation,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(operation) || !options.IsValid)
        {
            return new(false, "AI_STUDIO_REQUEST_INVALID", null);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("v1/ai/pricing/quote"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { Operation = operation.ToString() }),
                Encoding.UTF8, "application/json"),
        };
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new(false, "AI_STUDIO_AUTH_REQUIRED", null);
        }
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, "AI_STUDIO_QUOTE_UNAVAILABLE", null);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!TryGetBoundedString(root, "pricingVersion", 64, out var version)
                || !root.TryGetProperty("creditCost", out var creditCostElement)
                || !creditCostElement.TryGetInt64(out var creditCost) || creditCost <= 0)
            {
                return new(false, "AI_STUDIO_RESPONSE_INVALID", null);
            }
            return new(true, "AI_PRICE_QUOTED", new(creditCost, version!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(false, "AI_STUDIO_OFFLINE", null); }
        catch (InvalidDataException) { return new(false, "AI_STUDIO_RESPONSE_INVALID", null); }
        catch (JsonException) { return new(false, "AI_STUDIO_RESPONSE_INVALID", null); }
    }

    public async Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("v1/ai/jobs"));
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new(false, "AI_STUDIO_AUTH_REQUIRED", []);
        }
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, "AI_STUDIO_HISTORY_UNAVAILABLE", []);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("jobs", out var jobsElement)
                || jobsElement.ValueKind != JsonValueKind.Array || jobsElement.GetArrayLength() > 100)
            {
                return new(false, "AI_STUDIO_RESPONSE_INVALID", []);
            }
            var jobs = new List<AiStudioJobSummary>();
            foreach (var element in jobsElement.EnumerateArray())
            {
                if (!TryReadJob(element, out var job)) return new(false, "AI_STUDIO_RESPONSE_INVALID", []);
                jobs.Add(job!);
            }
            return new(true, "AI_JOB_LISTED", jobs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(false, "AI_STUDIO_OFFLINE", []); }
        catch (InvalidDataException) { return new(false, "AI_STUDIO_RESPONSE_INVALID", []); }
        catch (JsonException) { return new(false, "AI_STUDIO_RESPONSE_INVALID", []); }
    }

    public async Task<AiStudioHistoryResult> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) return new(false, "AI_STUDIO_REQUEST_INVALID", []);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Endpoint($"v1/ai/jobs/{jobId:D}/cancel"));
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new(false, "AI_STUDIO_AUTH_REQUIRED", []);
        }
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, "AI_JOB_CANCEL_REJECTED", []);
            return await GetHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(false, "AI_STUDIO_OFFLINE", []); }
    }

    private async Task<bool> AuthorizeAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var refresh = await authenticationService.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
                if (!refresh.Succeeded) return false;
                session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            if (session is null || string.IsNullOrWhiteSpace(session.AccessToken)
                || session.AccessToken.Length > 16 * 1_024) return false;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (HttpRequestException) { return false; }
    }

    private static bool TryReadJob(JsonElement element, out AiStudioJobSummary? job)
    {
        job = null;
        if (!element.TryGetProperty("jobId", out var idElement) || !idElement.TryGetGuid(out var id)
            || !TryGetBoundedString(element, "operation", 32, out var operationText)
            || !Enum.TryParse<AiStudioOperation>(operationText, out var operation)
            || !TryGetBoundedString(element, "status", 32, out var stateText)
            || !Enum.TryParse<AiStudioJobState>(stateText, out var state)
            || !element.TryGetProperty("reservedCredits", out var reservedElement)
            || !reservedElement.TryGetInt64(out var reserved) || reserved <= 0
            || !element.TryGetProperty("createdAt", out var createdElement)
            || !createdElement.TryGetDateTimeOffset(out var createdAt)) return false;
        long? final = null;
        if (element.TryGetProperty("finalCredits", out var finalElement)
            && finalElement.ValueKind != JsonValueKind.Null)
        {
            if (!finalElement.TryGetInt64(out var finalValue) || finalValue <= 0 || finalValue > reserved) return false;
            final = finalValue;
        }
        var canCancel = state is AiStudioJobState.Pending or AiStudioJobState.Queued or AiStudioJobState.Processing;
        job = new(id, operation, state, reserved, final, createdAt, canCancel);
        return true;
    }

    private static bool TryGetBoundedString(
        JsonElement element, string propertyName, int maximumLength, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Gateway response is oversized.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Gateway response is oversized.");
            buffer.Write(bytes, 0, read);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Uri Endpoint(string relativePath) => new(options.BaseUri, relativePath);
}
