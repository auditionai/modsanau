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
            if (!response.IsSuccessStatusCode) return new(true, "AI_MODELS_FALLBACK", KnownGpti2Models());
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
                    ReadCreditCosts(item)) { Settings = ReadSettings(parameters, item) };
            }).Where(item => item is not null && IsAllowedModel(item.Id, item.Name)).Cast<AiStudioModelOption>().ToArray();
            return result.Length == 0 ? new(true, "AI_MODELS_FALLBACK", KnownGpti2Models()) : new(true, "AI_MODELS_LOADED", result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(true, "AI_MODELS_FALLBACK", KnownGpti2Models()); }
        catch (JsonException) { return new(true, "AI_MODELS_FALLBACK", KnownGpti2Models()); }
    }

    public async Task<AiImagePromptPresetResult> GetImagePromptPresetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (session is null) return new(false, "AI_PRESETS_AUTH_REQUIRED", []);
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("presets"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, "AI_PRESETS_UNAVAILABLE", []);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("presets", out var rows) || rows.ValueKind != JsonValueKind.Array) return new(false, "AI_PRESETS_INVALID", []);
            return new(true, "AI_PRESETS_LOADED", rows.EnumerateArray().Select(row => new AiImagePromptPreset(
                row.TryGetProperty("presetId", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                row.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty))
                .Where(item => Guid.TryParse(item.Id, out _) && !string.IsNullOrWhiteSpace(item.Name)).ToArray());
        }
        catch (HttpRequestException) { return new(false, "AI_PRESETS_UNAVAILABLE", []); }
        catch (JsonException) { return new(false, "AI_PRESETS_INVALID", []); }
    }

    private static IReadOnlyList<AiStudioModelOption> KnownGpti2Models() =>
    [
        new("gpt-image-2", "GPT Image 2", ["low", "medium", "high"], ["1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "21:9"], ["1k", "2k", "4k"], [50]) { Settings = Gpti2Settings() },
        new("nano-banana-pro", "Nano Banana PRO", ["low", "medium", "high"], ["1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "21:9"], ["1k", "2k", "4k"], [50]) { Settings = Gpti2Settings() },
    ];
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Gpti2Settings() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["aspect_ratio"] = ["1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "21:9"],
        ["resolution"] = ["1k", "2k", "4k"], ["quality"] = ["low", "medium", "high"],
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Uri Endpoint(string action) => new(projectUri, $"functions/v1/gpti2-image?action={action}");

    public Task<AiStudioQuoteResult> GetQuoteAsync(AiStudioOperation operation, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioQuoteResult(false, "AI_QUOTE_FROM_LIVE_MODEL_REQUIRED", null));

    public async Task<AiStudioQuoteResult> GetQuoteAsync(AiStudioOperation operation, string? modelId,
        IReadOnlyDictionary<string, string>? modelSettings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return new(false, "AI_QUOTE_MODEL_REQUIRED", null);
        var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null && (await authentication.RefreshSessionAsync(cancellationToken)).Session is null)
            return new(false, "AI_QUOTE_AUTH_REQUIRED", null);
        session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return new(false, "AI_QUOTE_AUTH_REQUIRED", null);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("quote"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = modelId, settings = modelSettings ?? new Dictionary<string, string>() }, JsonOptions), Encoding.UTF8, "application/json");
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, "AI_QUOTE_UNAVAILABLE", null);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("creditCost", out var cost) || !cost.TryGetInt64(out var creditCost) || creditCost <= 0)
                return new(false, "AI_QUOTE_INVALID", null);
            var version = root.TryGetProperty("pricingVersion", out var versionValue) ? versionValue.GetString() : "live";
            return new(true, "AI_PRICE_QUOTED", new(creditCost, version ?? "live"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(false, "AI_QUOTE_OFFLINE", null); }
        catch (JsonException) { return new(false, "AI_QUOTE_INVALID", null); }
    }

    public Task<AiStudioHistoryResult> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStudioHistoryResult(false, "AI_HISTORY_UNAVAILABLE", []));

    public async Task<AiStudioHistoryResult> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) return new(false, "AI_JOB_INVALID", []);
        var session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null && (await authentication.RefreshSessionAsync(cancellationToken)).Session is null)
            return new(false, "AI_CANCEL_AUTH_REQUIRED", []);
        session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return new(false, "AI_CANCEL_AUTH_REQUIRED", []);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("cancel"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { job_id = jobId, finalize = false }, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new(true, "AI_JOB_CANCEL_REQUESTED", [])
            : new(false, "AI_JOB_CANCEL_FAILED", []);
    }

    public async Task<AiStudioExecutionResult> ExecuteAsync(AiStudioExecutionRequest request,
        IProgress<AiOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PromptPresetId) || string.IsNullOrWhiteSpace(request.PublicOptionId))
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
        using var submit = new HttpRequestMessage(HttpMethod.Post, Endpoint("generate"));
        submit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        submit.Content = new StringContent(JsonSerializer.Serialize(new
        {
            preset_id = request.PromptPresetId, model = request.PublicOptionId, idempotency_key = request.IdempotencyKey,
            reference_images = references, settings = request.ModelSettings ?? new Dictionary<string, string>(),
            creative_inputs = request.CreativeInputs ?? new Dictionary<string, string>(),
            output = request.ExactOutputSize is { } size ? new { width = size.Width, height = size.Height } : null,
        }, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(submit, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new(false, false, "AI_JOB_SUBMIT_FAILED", null);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var jobId = root.TryGetProperty("job_id", out var id) ? id.GetString() : null;
        var result = root.TryGetProperty("result", out var direct) ? direct.GetString() : null;
        try
        {
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && Guid.TryParse(jobId, out var cancelledJobId))
        {
            try { await CancelJobAsync(cancelledJobId, CancellationToken.None).ConfigureAwait(false); }
            catch (HttpRequestException) { }
            throw;
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

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSettings(JsonElement parameters, JsonElement model)
    {
        var result = parameters.ValueKind != JsonValueKind.Object
            ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            : parameters.EnumerateObject().Select(property =>
            (property.Name, Values: ReadOptionValues(parameters, property.Name)))
            .Where(item => item.Values.Count > 0)
            .ToDictionary(item => item.Name, item => item.Values, StringComparer.OrdinalIgnoreCase);
        if (model.TryGetProperty("servers", out var servers))
        {
            var values = ReadArrayValues(servers);
            if (values.Count > 0) result["server"] = values;
        }
        if (model.TryGetProperty("modes", out var modes))
        {
            var values = ReadArrayValues(modes);
            if (values.Count > 0 && !result.ContainsKey("speed")) result["speed"] = values;
        }
        return result;
    }

    private static IReadOnlyList<string> ReadArrayValues(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .Select(item => item.ToString()).Where(item => item.Length is > 0 and <= 64).Take(20).ToArray()
        : [];

    private static bool IsAllowedModel(string id, string name)
    {
        var value = $"{id} {name}".ToLowerInvariant().Replace('.', ' ').Replace('_', ' ');
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"\bgpt(?:[- ]?image)?[- ]?2\b")
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"\bnano[- ]?banana[- ]?pro\b");
    }
}
