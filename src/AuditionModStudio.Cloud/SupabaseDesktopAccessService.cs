using System.Net.Http.Json;
using System.Text.Json;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Cloud;

public sealed class SupabaseDesktopAccessService(
    HttpClient httpClient,
    ISecureSessionStore sessionStore,
    IDeviceSessionBindingStore bindingStore,
    SupabaseAuthOptions options) : IDesktopAccessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DesktopAccessResult> EnsureAccessAsync(CancellationToken cancellationToken = default)
    {
        AuthSessionSecrets? secrets;
        DeviceSessionBinding? binding;
        try
        {
            secrets = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            binding = await bindingStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DesktopAccessResult.Failure("DESKTOP_ACCESS_CANCELLED");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                     or InvalidDataException or InvalidOperationException)
        {
            return DesktopAccessResult.Failure("DESKTOP_SECURE_STORAGE_FAILED");
        }

        if (secrets is null) return DesktopAccessResult.Failure("AUTH_SESSION_MISSING");
        binding ??= new DeviceSessionBinding(Guid.NewGuid(), null);

        var action = binding.SessionId.HasValue ? "validate" : "register";
        var version = typeof(SupabaseDesktopAccessService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var payload = new
        {
            action,
            payload = new
            {
                deviceId = binding.DeviceId,
                sessionId = binding.SessionId,
                displayName = Environment.MachineName,
                clientVersion = version,
                platform = "windows-x64",
            },
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(options.ProjectUri, "rest/v1/rpc/desktop_access_api"));
            request.Headers.TryAddWithoutValidation("apikey", options.PublishableKey);
            request.Headers.Authorization = new("Bearer", secrets.AccessToken);
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                return DesktopAccessResult.Failure(code);
            }

            var result = await response.Content.ReadFromJsonAsync<DesktopAccessPayload>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (result is null || result.DeviceId == Guid.Empty || result.SessionId == Guid.Empty
                               || string.IsNullOrWhiteSpace(result.PublicDeviceCode)
                               || !string.Equals(result.DeviceStatus, "active", StringComparison.Ordinal))
                return DesktopAccessResult.Failure("DESKTOP_ACCESS_RESPONSE_INVALID");

            var nextBinding = new DeviceSessionBinding(result.DeviceId, result.SessionId);
            await bindingStore.SaveAsync(nextBinding, cancellationToken).ConfigureAwait(false);
            return DesktopAccessResult.Success(new(result.DeviceId, result.SessionId, result.PublicDeviceCode,
                result.DeviceStatus, result.LastSeenAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DesktopAccessResult.Failure("DESKTOP_ACCESS_CANCELLED");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                                                     or InvalidOperationException)
        {
            return DesktopAccessResult.Failure("DESKTOP_ACCESS_UNAVAILABLE");
        }
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<SupabaseError>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var message = error?.Message ?? string.Empty;
            return message is "EMAIL_CONFIRMATION_REQUIRED" or "USER_INACTIVE" or "DEVICE_INACTIVE"
                or "DEVICE_SESSION_INACTIVE" or "DEVICE_SESSION_REVOKED"
                ? message
                : response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "AUTH_SESSION_INVALID"
                    : "DESKTOP_ACCESS_REJECTED";
        }
        catch (JsonException) { return "DESKTOP_ACCESS_REJECTED"; }
    }

    private sealed record DesktopAccessPayload(Guid DeviceId, Guid SessionId, string PublicDeviceCode,
        string DeviceStatus, DateTimeOffset LastSeenAt);
    private sealed record SupabaseError(string? Message);
}

public sealed class UnavailableDesktopAccessService : IDesktopAccessService
{
    public Task<DesktopAccessResult> EnsureAccessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DesktopAccessResult.Failure("DESKTOP_CONFIGURATION_UNAVAILABLE"));
}
