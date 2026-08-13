using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AuditionModStudio.Core.Accounts;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Cloud;

public sealed record GatewayAccountOptions(Uri BaseUri)
{
    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumHistoryEntries = 50;
    public bool IsValid => BaseUri is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" }
        && string.IsNullOrEmpty(BaseUri.UserInfo) && string.IsNullOrEmpty(BaseUri.Query)
        && string.IsNullOrEmpty(BaseUri.Fragment);
}

public sealed class GatewayAccountOverviewService(
    HttpClient httpClient,
    ISecureSessionStore sessions,
    IAuthenticationService authentication,
    GatewayAccountOptions options) : IAccountOverviewService, IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _disposed;

    public async Task<AccountOverviewResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsValid) return Unavailable("ACCOUNT_CONFIGURATION_INVALID");
        var entered = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var session = await LoadCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            if (session is null) return AuthenticationRequired();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUri, "v1/account"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthenticationRequired();
            if (response.StatusCode != HttpStatusCode.OK) return Unavailable("ACCOUNT_SERVICE_UNAVAILABLE");
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return bytes is null ? InvalidResponse() : Parse(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(AccountOverviewStatus.Cancelled, "ACCOUNT_CANCELLED", null); }
        catch (JsonException) { return InvalidResponse(); }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                                     or NotSupportedException
                                                     or UnauthorizedAccessException or InvalidOperationException)
        { return Unavailable("ACCOUNT_SERVICE_UNAVAILABLE"); }
        finally
        {
            if (entered) _refreshGate.Release();
        }
    }

    private async Task<AuthSessionSecrets?> LoadCurrentSessionAsync(CancellationToken cancellationToken)
    {
        var session = await sessions.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null && session.ExpiresAt > DateTimeOffset.UtcNow) return session;
        var refreshed = await authentication.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed.Succeeded) return null;
        session = await sessions.LoadAsync(cancellationToken).ConfigureAwait(false);
        return session is not null && session.ExpiresAt > DateTimeOffset.UtcNow ? session : null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > GatewayAccountOptions.MaximumResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > GatewayAccountOptions.MaximumResponseBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static AccountOverviewResult Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        var root = document.RootElement;
        if (!HasExactProperties(root, "profile", "wallet", "usage", "transactions", "observedAt")
            || !root.TryGetProperty("profile", out var profileElement)
            || !HasExactProperties(profileElement, "userId", "email", "displayName")
            || !profileElement.TryGetProperty("userId", out var userElement)
            || !userElement.TryGetGuid(out var userId) || userId == Guid.Empty
            || !TryReadOptionalString(profileElement, "email", 320, out var email)
            || !TryReadOptionalString(profileElement, "displayName", 128, out var displayName)
            || !root.TryGetProperty("wallet", out var walletElement)
            || !HasExactProperties(walletElement, "availableCredits", "reservedCredits")
            || !TryReadNonNegativeInt64(walletElement, "availableCredits", out var available)
            || !TryReadNonNegativeInt64(walletElement, "reservedCredits", out var reserved)
            || !root.TryGetProperty("usage", out var usageElement)
            || !HasExactProperties(usageElement, "creditsGranted", "creditsUsed", "transactionCount")
            || !TryReadNonNegativeInt64(usageElement, "creditsGranted", out var granted)
            || !TryReadNonNegativeInt64(usageElement, "creditsUsed", out var used)
            || !TryReadNonNegativeInt64(usageElement, "transactionCount", out var count)
            || !root.TryGetProperty("observedAt", out var observedElement)
            || !observedElement.TryGetDateTimeOffset(out var observedAt) || observedAt == default
            || !root.TryGetProperty("transactions", out var transactionsElement)
            || transactionsElement.ValueKind != JsonValueKind.Array
            || transactionsElement.GetArrayLength() > GatewayAccountOptions.MaximumHistoryEntries)
            return InvalidResponse();
        var transactions = ImmutableArray.CreateBuilder<AccountCreditTransaction>();
        foreach (var element in transactionsElement.EnumerateArray())
        {
            if (!TryReadTransaction(element, out var transaction)) return InvalidResponse();
            transactions.Add(transaction!);
        }
        if (transactions.Count != Math.Min(count, GatewayAccountOptions.MaximumHistoryEntries)
            || transactions.Count > 0 && (transactions[0].AvailableAfter != available
                || transactions[0].ReservedAfter != reserved)
            || !IsDescending(transactions)) return InvalidResponse();
        return new(AccountOverviewStatus.Succeeded, "ACCOUNT_READ", new(
            new(userId, email, displayName), new(available, reserved), new(granted, used, count),
            transactions.ToImmutable(), observedAt));
    }

    private static bool TryReadTransaction(JsonElement element, out AccountCreditTransaction? transaction)
    {
        transaction = null;
        if (!HasExactProperties(element, "transactionId", "kind", "amount", "availableDelta", "reservedDelta",
                "availableAfter", "reservedAfter", "createdAt")
            || !element.TryGetProperty("transactionId", out var idElement)
            || !idElement.TryGetGuid(out var id) || id == Guid.Empty
            || !element.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || !TryMapKind(kindElement.GetString(), out var kind)
            || !element.TryGetProperty("amount", out var amountElement)
            || !amountElement.TryGetInt64(out var amount) || amount <= 0
            || !element.TryGetProperty("availableDelta", out var availableDeltaElement)
            || !availableDeltaElement.TryGetInt64(out var availableDelta)
            || !element.TryGetProperty("reservedDelta", out var reservedDeltaElement)
            || !reservedDeltaElement.TryGetInt64(out var reservedDelta)
            || !TryReadNonNegativeInt64(element, "availableAfter", out var availableAfter)
            || !TryReadNonNegativeInt64(element, "reservedAfter", out var reservedAfter)
            || !element.TryGetProperty("createdAt", out var createdElement)
            || !createdElement.TryGetDateTimeOffset(out var createdAt) || createdAt == default)
            return false;
        transaction = new(id, kind, amount, availableDelta, reservedDelta,
            availableAfter, reservedAfter, createdAt);
        return true;
    }

    private static bool IsDescending(ImmutableArray<AccountCreditTransaction>.Builder transactions)
    {
        for (var index = 1; index < transactions.Count; index++)
            if (transactions[index - 1].CreatedAt < transactions[index].CreatedAt) return false;
        return true;
    }

    private static bool TryMapKind(string? value, out CreditTransactionKind kind) =>
        Enum.TryParse(value, true, out kind) && Enum.IsDefined(kind);

    private static bool TryReadNonNegativeInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value) && value >= 0;
    }

    private static bool TryReadOptionalString(JsonElement element, string name, int maximumLength, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
    }

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == names.Length
            && properties.Distinct(StringComparer.Ordinal).Count() == names.Length
            && properties.ToHashSet(StringComparer.Ordinal).SetEquals(names);
    }

    private static AccountOverviewResult AuthenticationRequired() =>
        new(AccountOverviewStatus.AuthenticationRequired, "ACCOUNT_AUTH_REQUIRED", null);
    private static AccountOverviewResult Unavailable(string code) =>
        new(AccountOverviewStatus.Unavailable, code, null);
    private static AccountOverviewResult InvalidResponse() =>
        new(AccountOverviewStatus.InvalidResponse, "ACCOUNT_RESPONSE_INVALID", null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _refreshGate.Dispose();
    }
}
