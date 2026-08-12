using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Images;

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
    GatewayAiStudioOptions options,
    IAiTransportImageEncoder? imageEncoder = null,
    IImageImportService? imageImport = null) : IAiStudioService
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

    public async Task<AiStudioExecutionResult> ExecuteAsync(AiStudioExecutionRequest request,
        IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (imageEncoder is null || imageImport is null || !IsValidExecution(request))
            return new(false, false, "AI_STUDIO_REQUEST_INVALID", null);
        var activeJobId = Guid.Empty;
        try
        {
            progress?.Report(new(AiOperationPhase.Validating, 5, "AI_CONTENT_ENCODING"));
            var sourceBytes = await imageEncoder.EncodePngAsync(request.Source, cancellationToken)
                .ConfigureAwait(false);
            var maskBytes = request.Mask is null ? null
                : await imageEncoder.EncodeMaskPngAsync(request.Mask, cancellationToken).ConfigureAwait(false);
            if (sourceBytes is null || sourceBytes.Length > 16 * 1024 * 1024
                || request.Mask is not null && maskBytes is null)
                return new(false, false, "AI_CONTENT_ENCODING_FAILED", null);
            var sourceId = await UploadAsync("source", sourceBytes, cancellationToken).ConfigureAwait(false);
            var maskId = maskBytes is null ? null
                : await UploadAsync("mask", maskBytes, cancellationToken).ConfigureAwait(false);
            if (sourceId is null || maskBytes is not null && maskId is null)
                return new(false, false, "AI_CONTENT_UPLOAD_FAILED", null);

            progress?.Report(new(AiOperationPhase.Submitting, 15, "AI_JOB_SUBMITTING"));
            var body = JsonSerializer.Serialize(new
            {
                Operation = request.Operation.ToString(),
                InputMetadata = new
                {
                    InputReference = sourceId,
                    PromptLength = request.Prompt?.Value.Length ?? 0,
                    TargetWidth = request.TargetSize?.Width,
                    TargetHeight = request.TargetSize?.Height,
                    InputBytes = sourceBytes.Length,
                    MaskReference = maskId,
                    request.PublicOptionId,
                    Prompt = request.Prompt?.Value,
                    SourceWidth = request.Source.Width,
                    SourceHeight = request.Source.Height,
                },
                request.IdempotencyKey,
                ExpectedPricingVersion = (string?)null,
            });
            using var submit = new HttpRequestMessage(HttpMethod.Post, Endpoint("v1/ai/jobs"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!await AuthorizeAsync(submit, cancellationToken).ConfigureAwait(false))
                return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);
            using var submitResponse = await httpClient.SendAsync(submit,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!submitResponse.IsSuccessStatusCode) return new(false, false, "AI_JOB_SUBMIT_FAILED", null);
            using var submitDocument = await ReadJsonAsync(submitResponse, cancellationToken).ConfigureAwait(false);
            if (!submitDocument.RootElement.TryGetProperty("jobId", out var jobElement)
                || !jobElement.TryGetGuid(out var jobId))
                return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null);
            activeJobId = jobId;

            for (var attempt = 0; attempt < 300; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(AiOperationPhase.Processing, Math.Min(90, 20 + attempt / 4),
                    "AI_JOB_PROCESSING"));
                using var poll = new HttpRequestMessage(HttpMethod.Get, Endpoint($"v1/ai/jobs/{jobId:D}"));
                if (!await AuthorizeAsync(poll, cancellationToken).ConfigureAwait(false))
                    return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);
                using var pollResponse = await httpClient.SendAsync(poll,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!pollResponse.IsSuccessStatusCode) return new(false, false, "AI_JOB_STATUS_FAILED", null);
                using var pollDocument = await ReadJsonAsync(pollResponse, cancellationToken).ConfigureAwait(false);
                if (!TryGetBoundedString(pollDocument.RootElement, "status", 32, out var status))
                    return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null);
                if (status == "Completed")
                {
                    if (!TryGetBoundedString(pollDocument.RootElement, "outputReference", 80, out var contentId))
                        return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null);
                    return await DownloadAsync(contentId!, progress, cancellationToken).ConfigureAwait(false);
                }
                if (status is "Failed" or "Cancelled" or "ReconciliationRequired")
                    return new(false, status == "Cancelled", $"AI_JOB_{status.ToUpperInvariant()}", null);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            return new(false, false, "AI_JOB_POLL_TIMEOUT", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeJobId != Guid.Empty)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await RequestJobCancellationAsync(activeJobId, cancelTimeout.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
            }
            return new(false, true, "AI_STUDIO_CANCELLED", null);
        }
        catch (HttpRequestException) { return new(false, false, "AI_STUDIO_OFFLINE", null); }
        catch (InvalidDataException) { return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null); }
        catch (JsonException) { return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null); }
    }

    private async Task RequestJobCancellationAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Endpoint($"v1/ai/jobs/{jobId:D}/cancel"));
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false)) return;
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> UploadAsync(string kind, byte[] bytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint($"v1/ai/content/{kind}"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("image/png");
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false)) return null;
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return TryGetBoundedString(document.RootElement, "contentId", 80, out var contentId) ? contentId : null;
    }

    private async Task<AiStudioExecutionResult> DownloadAsync(string contentId,
        IProgress<AiOperationProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(AiOperationPhase.Receiving, 95, "AI_OUTPUT_DOWNLOADING"));
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint($"v1/ai/content/{contentId}"));
        if (!await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
            return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 16 * 1024 * 1024
            || response.Content.Headers.ContentType?.MediaType is not
                ("image/png" or "image/jpeg" or "image/webp" or "image/bmp"))
            return new(false, false, "AI_OUTPUT_DOWNLOAD_INVALID", null);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > 16 * 1024 * 1024)
                return new(false, false, "AI_OUTPUT_DOWNLOAD_INVALID", null);
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length is <= 0 or > 16 * 1024 * 1024)
            return new(false, false, "AI_OUTPUT_DOWNLOAD_INVALID", null);
        var imported = await imageImport!.ImportMemoryAsync(new(buffer.ToArray()), cancellationToken)
            .ConfigureAwait(false);
        return imported.Succeeded && imported.Image is not null
            ? new(true, false, "AI_OPERATION_COMPLETED", imported.Image)
            : new(false, imported.Cancelled, "AI_OUTPUT_IMPORT_FAILED", null);
    }

    private static bool IsValidExecution(AiStudioExecutionRequest request)
    {
        if (request.Source is null || request.IdempotencyKey.Length is < 1 or > 80
            || !request.IdempotencyKey.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')
            || string.IsNullOrWhiteSpace(request.PublicOptionId) || request.PublicOptionId.Length > 64)
            return false;
        var alignedMask = request.Mask is null || request.Mask.Width == request.Source.Width
            && request.Mask.Height == request.Source.Height;
        var expandsSource = request.TargetSize is { IsValid: true } target
            && target.Width >= request.Source.Width && target.Height >= request.Source.Height
            && (target.Width > request.Source.Width || target.Height > request.Source.Height);
        return alignedMask && request.Operation switch
        {
            AiStudioOperation.Inpaint or AiStudioOperation.ReplaceObject => request.Mask is not null
                && request.Prompt is { IsValid: true } && request.TargetSize is null,
            AiStudioOperation.RemoveObject => request.Mask is not null && request.Prompt is null
                && request.TargetSize is null,
            AiStudioOperation.Outpaint => request.Mask is null && request.Prompt is { IsValid: true }
                && expandsSource,
            AiStudioOperation.Upscale => request.Mask is null && request.Prompt is null
                && expandsSource,
            _ => false,
        };
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
