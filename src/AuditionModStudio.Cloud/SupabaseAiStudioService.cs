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
    IImageImportService imageImport) : IAiStudioService
{
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
        progress?.Report(new(AiOperationPhase.Submitting, 10, "AI_JOB_SUBMITTING"));
        using var submit = new HttpRequestMessage(HttpMethod.Post, Endpoint("generate"));
        submit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        submit.Content = new StringContent(JsonSerializer.Serialize(new
        {
            prompt = prompt.Value, model = request.PublicOptionId, idempotency_key = request.IdempotencyKey,
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
}
