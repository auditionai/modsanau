using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Cloud;

public sealed record EdgeFunctionOptions(Uri BaseUri)
{
    public bool IsValid => BaseUri is { IsAbsoluteUri: true, Scheme: "https" }
                           && string.IsNullOrEmpty(BaseUri.UserInfo)
                           && string.IsNullOrEmpty(BaseUri.Fragment);
}

public sealed class EdgeFunctionAiStudioService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IAuthenticationService authenticationService,
    EdgeFunctionOptions options,
    IAiTransportImageEncoder? imageEncoder = null,
    IImageImportService? imageImport = null) : IAiStudioService
{
    private const int MaximumResponseBytes = 256 * 1_024;

    public async Task<AiStudioQuoteResult> GetQuoteAsync(
        AiStudioOperation operation,
        CancellationToken cancellationToken = default)
    {
        // Trạm Sáng Tạo doesn't have pricing API, return estimated cost
        if (!Enum.IsDefined(operation) || !options.IsValid)
        {
            return new(false, "AI_STUDIO_REQUEST_INVALID", null);
        }

        var estimatedCost = operation switch
        {
            AiStudioOperation.Generate => 35L,
            AiStudioOperation.Edit => 35L,
            AiStudioOperation.Inpaint => 35L,
            AiStudioOperation.Outpaint => 50L,
            AiStudioOperation.RemoveObject => 30L,
            AiStudioOperation.ReplaceObject => 35L,
            AiStudioOperation.Upscale => 20L,
            _ => 35L
        };

        return new(true, "AI_PRICE_QUOTED", new(estimatedCost, "tramsangtao-v1"));
    }

    public async Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        // Trạm Sáng Tạo doesn't provide job history API
        return new(true, "AI_STUDIO_HISTORY_UNAVAILABLE", []);
    }

    public async Task<AiStudioHistoryResult> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        // Trạm Sáng Tạo doesn't support job cancellation
        return new(false, "AI_JOB_CANCEL_NOT_SUPPORTED", []);
    }

    public async Task<AiStudioExecutionResult> ExecuteAsync(
        AiStudioExecutionRequest request,
        IProgress<AiOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (imageEncoder is null || imageImport is null || !IsValidExecution(request))
            return new(false, false, "AI_STUDIO_REQUEST_INVALID", null);

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

            // Convert to base64 for Edge Function
            var sourceBase64 = Convert.ToBase64String(sourceBytes);
            var maskBase64 = maskBytes is null ? null : Convert.ToBase64String(maskBytes);

            progress?.Report(new(AiOperationPhase.Submitting, 15, "AI_JOB_SUBMITTING"));

            // Build request based on operation
            var body = request.Operation switch
            {
                AiStudioOperation.Upscale => JsonSerializer.Serialize(new
                {
                    image = sourceBase64,
                    scale = CalculateUpscaleScale(request)
                }),
                _ => JsonSerializer.Serialize(new
                {
                    prompt = request.Prompt?.Value ?? "",
                    image = sourceBase64,
                    mask = maskBase64,
                    width = request.TargetSize?.Width,
                    height = request.TargetSize?.Height,
                    num_outputs = 1
                })
            };

            var endpoint = request.Operation == AiStudioOperation.Upscale ? "upscale" : "generate";
            using var submit = new HttpRequestMessage(HttpMethod.Post, Endpoint(endpoint))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (!await AuthorizeAsync(submit, cancellationToken).ConfigureAwait(false))
                return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);

            using var submitResponse = await httpClient.SendAsync(submit,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!submitResponse.IsSuccessStatusCode)
                return new(false, false, "AI_JOB_SUBMIT_FAILED", null);

            using var submitDocument = await ReadJsonAsync(submitResponse, cancellationToken).ConfigureAwait(false);
            if (!submitDocument.RootElement.TryGetProperty("job_id", out var jobElement)
                || !jobElement.TryGetGuid(out var jobId))
                return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null);

            // Poll for completion
            for (var attempt = 0; attempt < 300; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(AiOperationPhase.Processing, Math.Min(90, 20 + attempt / 4),
                    "AI_JOB_PROCESSING"));

                using var poll = new HttpRequestMessage(HttpMethod.Get, Endpoint($"jobs/{jobId:D}"));
                if (!await AuthorizeAsync(poll, cancellationToken).ConfigureAwait(false))
                    return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);

                using var pollResponse = await httpClient.SendAsync(poll,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (!pollResponse.IsSuccessStatusCode)
                    return new(false, false, "AI_JOB_STATUS_FAILED", null);

                using var pollDocument = await ReadJsonAsync(pollResponse, cancellationToken).ConfigureAwait(false);
                if (!TryGetBoundedString(pollDocument.RootElement, "status", 32, out var status))
                    return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null);

                if (status == "completed")
                {
                    if (!TryGetBoundedString(pollDocument.RootElement, "result_url", 512, out var resultUrl))
                        return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null);

                    return await DownloadFromUrlAsync(resultUrl!, progress, cancellationToken).ConfigureAwait(false);
                }

                if (status is "failed")
                    return new(false, false, "AI_JOB_FAILED", null);

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            return new(false, false, "AI_JOB_POLL_TIMEOUT", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, true, "AI_STUDIO_CANCELLED", null);
        }
        catch (HttpRequestException) { return new(false, false, "AI_STUDIO_OFFLINE", null); }
        catch (InvalidDataException) { return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null); }
        catch (JsonException) { return new(false, false, "AI_STUDIO_RESPONSE_INVALID", null); }
    }

    private static int CalculateUpscaleScale(AiStudioExecutionRequest request)
    {
        if (request.TargetSize is null) return 2;
        var widthScale = request.TargetSize.Value.Width / request.Source.Width;
        var heightScale = request.TargetSize.Value.Height / request.Source.Height;
        return Math.Max(widthScale, heightScale);
    }

    private async Task<AiStudioExecutionResult> DownloadFromUrlAsync(
        string url,
        IProgress<AiOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(AiOperationPhase.Receiving, 95, "AI_OUTPUT_DOWNLOADING"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
        catch (JsonException) { return false; }
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
            throw new InvalidDataException("Edge Function response is oversized.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Edge Function response is oversized.");
            buffer.Write(bytes, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Uri Endpoint(string relativePath) => new(options.BaseUri, relativePath);
}
