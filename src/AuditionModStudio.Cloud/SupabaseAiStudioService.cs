using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Images;

namespace AuditionModStudio.Cloud;

public sealed class SupabaseAiStudioService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IAuthenticationService authentication,
    Uri projectUri,
    IImageImportService imageImport,
    IAiTransportImageEncoder imageEncoder) : IAiStudioService
{
    public bool SupportsGenerationWithReferences => true;

    public async Task<AiStudioModelsResult> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null && (await authentication.RefreshSessionAsync(cancellationToken)).Session is null)
                return new(false, "AI_MODELS_AUTH_REQUIRED", []);
            session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null) return new(false, "AI_MODELS_AUTH_REQUIRED", []);
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, "AI_MODELS_UNAVAILABLE", []);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("models", out var source) || source.ValueKind != JsonValueKind.Array)
                return new(false, "AI_MODELS_INVALID", []);
            var result = source.EnumerateArray().Take(20).Select(item =>
            {
                var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : id;
                var parameters = item.TryGetProperty("params", out var raw) && raw.ValueKind == JsonValueKind.Object ? raw : default;
                return string.IsNullOrWhiteSpace(id) ? null : new AiStudioModelOption(
                    id!, name ?? id!, ReadOptionValues(parameters, "quality"), ReadOptionValues(parameters, "aspect_ratio"),
                    ReadOptionValues(parameters, "resolution").Count > 0 ? ReadOptionValues(parameters, "resolution") : ReadOptionValues(parameters, "size"),
                    ReadCreditCosts(item));
            }).Where(item => item is not null && IsAllowedModel(item.Id, item.Name)).Cast<AiStudioModelOption>().ToArray();
            return result.Length == 0 ? new(false, "AI_MODELS_EMPTY", []) : new(true, "AI_MODELS_LOADED", result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(false, "AI_MODELS_OFFLINE", []); }
        catch (JsonException) { return new(false, "AI_MODELS_INVALID", []); }
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Uri Endpoint(string action) => new(projectUri, $"functions/v1/tst-image?action={action}");

    public Task<AiStudioQuoteResult> GetQuoteAsync(AiStudioOperation operation, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioQuoteResult(false, "AI_QUOTE_FROM_LIVE_MODEL_REQUIRED", null));

    public Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioHistoryResult(false, "AI_HISTORY_UNAVAILABLE", []));

    public Task<AiStudioHistoryResult> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioHistoryResult(false, "AI_CANCEL_UNSUPPORTED", []));

    public async Task<AiStudioExecutionResult> ExecuteAsync(AiStudioExecutionRequest request,
        IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Prompt is not { IsValid: true } prompt || string.IsNullOrWhiteSpace(request.PublicOptionId))
            return new(false, false, "AI_STUDIO_REQUEST_INVALID", null);
        var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null && (await authentication.RefreshSessionAsync(cancellationToken)).Session is null)
            return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);
        session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return new(false, false, "AI_STUDIO_AUTH_REQUIRED", null);
        var references = new List<string>();
        if (request.Operation is not AiStudioOperation.Generate)
        {
            var source = await imageEncoder.EncodePngAsync(request.Source, cancellationToken).ConfigureAwait(false);
            if (source is null || source.Length is <= 0 or > 8 * 1024 * 1024)
                return new(false, false, "AI_SOURCE_ENCODING_FAILED", null);
            references.Add($"data:image/png;base64,{Convert.ToBase64String(source)}");
        }
        foreach (var image in request.AdditionalReferences ?? [])
        {
            var encoded = await imageEncoder.EncodePngAsync(image, cancellationToken).ConfigureAwait(false);
            if (encoded is null || encoded.Length is <= 0 or > 8 * 1024 * 1024)
                return new(false, false, "AI_REFERENCE_ENCODING_FAILED", null);
            references.Add($"data:image/png;base64,{Convert.ToBase64String(encoded)}");
        }
        if (references.Count > 5) return new(false, false, "AI_REFERENCE_LIMIT_EXCEEDED", null);
        progress?.Report(new(AiOperationPhase.Submitting, 10, "AI_JOB_SUBMITTING"));
        var effectivePrompt = prompt is { IsValid: true }
            ? await ComposePromptAsync(session.AccessToken, prompt.Value, references, cancellationToken).ConfigureAwait(false)
            : null;
        if (request.Operation is AiStudioOperation.Generate or AiStudioOperation.Edit
            or AiStudioOperation.Inpaint or AiStudioOperation.Outpaint or AiStudioOperation.ReplaceObject)
        {
            if (effectivePrompt is null) return new(false, false, "AI_PROMPT_COMPOSITION_FAILED", null);
        }
        using var submit = new HttpRequestMessage(HttpMethod.Post, Endpoint("generate"));
        submit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        submit.Content = new StringContent(JsonSerializer.Serialize(new
        {
            prompt = effectivePrompt ?? string.Empty, model = request.PublicOptionId, idempotency_key = request.IdempotencyKey,
            reference_images = references,
        }, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(submit, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new(false, false, "AI_JOB_SUBMIT_FAILED", null);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var jobId = root.TryGetProperty("job_id", out var id) ? id.GetString() : null;
        var result = root.TryGetProperty("result", out var direct) ? direct.GetString() : null;
        for (var attempt = 0; result is null && !string.IsNullOrWhiteSpace(jobId) && attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, 1 + attempt / 10)), cancellationToken).ConfigureAwait(false);
            using var poll = new HttpRequestMessage(HttpMethod.Get, Endpoint($"status&job_id={Uri.EscapeDataString(jobId)}"));
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var pollResponse = await httpClient.SendAsync(poll, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!pollResponse.IsSuccessStatusCode) return new(false, false, "AI_JOB_STATUS_FAILED", null);
            using var pollDoc = await JsonDocument.ParseAsync(await pollResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var status = pollDoc.RootElement.TryGetProperty("status", out var state) ? state.GetString() : null;
            result = pollDoc.RootElement.TryGetProperty("result", out var output) ? output.GetString() : null;
            progress?.Report(new(AiOperationPhase.Processing, Math.Min(90, 20 + attempt), status ?? "AI_JOB_PROCESSING"));
            if (status is "failed" or "cancelled") return new(false, status == "cancelled", "AI_JOB_" + status.ToUpperInvariant(), null);
        }
        if (!Uri.TryCreate(result, UriKind.Absolute, out var resultUri) || resultUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(resultUri.UserInfo))
            return new(false, false, "AI_OUTPUT_DOWNLOAD_INVALID", null);
        using var download = await httpClient.GetAsync(resultUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccessStatusCode || download.Content.Headers.ContentLength > 16 * 1024 * 1024)
            return new(false, false, "AI_OUTPUT_DOWNLOAD_INVALID", null);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var imported = await imageImport.ImportMemoryAsync(new(bytes), cancellationToken).ConfigureAwait(false);
        return imported.Succeeded && imported.Image is not null ? new(true, false, "AI_OPERATION_COMPLETED", imported.Image) : new(false, false, "AI_OUTPUT_INVALID", null);
    }

    private async Task<string?> ComposePromptAsync(string accessToken, string prompt, IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("compose"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { prompt, reference_images = references }, JsonOptions),
            Encoding.UTF8, "application/json");
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = document.RootElement.TryGetProperty("prompt", out var value) ? value.GetString()?.Trim() : null;
            return string.IsNullOrWhiteSpace(result) || result.Length > 16_000 ? null : result;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> ReadOptionValues(JsonElement parameters, string key)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(key, out var value)) return [];
        var source = value.ValueKind == JsonValueKind.Array ? value
            : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("values", out var values) ? values : default;
        return source.ValueKind != JsonValueKind.Array ? [] : source.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .Select(item => item.ToString()).Where(item => item.Length is > 0 and <= 64).Take(20).ToArray();
    }

    private static IReadOnlyList<long> ReadCreditCosts(JsonElement model)
    {
        if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Array) return [];
        var values = new List<long>();
        foreach (var item in pricing.EnumerateArray())
        {
            foreach (var key in new[] { "credits", "cost", "price" })
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value)
                    && value.TryGetInt64(out var amount) && amount > 0) values.Add(amount);
        }
        return values.Distinct().Order().Take(10).ToArray();
    }

    private static bool IsAllowedModel(string id, string name)
    {
        var value = $"{id} {name}".ToLowerInvariant().Replace('.', ' ').Replace('_', ' ');
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"\bgpt(?:[- ]?image)?[- ]?2\b")
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"\bnano[- ]?banana[- ]?pro\b")
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"\b(?:image|imagen)[- ]?4\b")
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"\bflux[- ]?2[- ]?pro\b");
    }
}
