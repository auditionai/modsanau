using Npgsql;
using NpgsqlTypes;
using AuditionModStudio.Core.Auth;

namespace AuditionModStudio.Gateway.Services;

public static class DeviceSessionHeaders
{
    public const string DeviceId = DeviceSessionProtocol.DeviceIdHeader;
    public const string SessionId = DeviceSessionProtocol.SessionIdHeader;
}

public sealed class DeviceSessionOptions
{
    public bool Enabled { get; init; }
    public int MaximumActiveDevices { get; init; }
    public TimeSpan LastSeenWriteInterval { get; init; } = TimeSpan.FromMinutes(15);

    public bool IsOperational => !Enabled
        || MaximumActiveDevices is >= 1 and <= 100
        && LastSeenWriteInterval >= TimeSpan.FromMinutes(1)
        && LastSeenWriteInterval <= TimeSpan.FromDays(1);

    public static DeviceSessionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gateway:DeviceSessions");
        return new()
        {
            Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled,
            MaximumActiveDevices = int.TryParse(section["MaximumActiveDevices"], out var maximum)
                && maximum is >= 1 and <= 100 ? maximum : 0,
            LastSeenWriteInterval = int.TryParse(section["LastSeenWriteIntervalSeconds"], out var seconds)
                && seconds is >= 60 and <= 86_400
                    ? TimeSpan.FromSeconds(seconds)
                    : TimeSpan.FromMinutes(15),
        };
    }

    public override string ToString() => "DeviceSessionOptions { [REDACTED] }";
}

public enum DeviceSessionOperationStatus
{
    Succeeded,
    Rejected,
    NotFound,
    Unavailable,
}

public sealed record DeviceSessionDescriptor(
    Guid DeviceId,
    string DisplayName,
    string ClientVersion,
    string Platform);

public sealed record DeviceSessionSnapshot(
    Guid DeviceId,
    Guid SessionId,
    string DisplayName,
    string ClientVersion,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt);

public sealed record DeviceSessionOperationResult(
    DeviceSessionOperationStatus Status,
    string DiagnosticCode,
    DeviceSessionSnapshot? Session,
    bool IsNewDevice = false);

public sealed record DeviceSessionListResult(
    DeviceSessionOperationStatus Status,
    string DiagnosticCode,
    IReadOnlyList<DeviceSessionSnapshot> Sessions);

public interface IDeviceSessionService
{
    Task<DeviceSessionOperationResult> RegisterAsync(
        AuthenticatedGatewayUser user,
        DeviceSessionDescriptor device,
        int maximumActiveDevices,
        CancellationToken cancellationToken = default);

    Task<DeviceSessionListResult> ListAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default);

    Task<DeviceSessionOperationResult> RevokeAsync(
        AuthenticatedGatewayUser user,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<DeviceSessionOperationResult> ValidateAsync(
        AuthenticatedGatewayUser user,
        Guid deviceId,
        Guid sessionId,
        TimeSpan lastSeenWriteInterval,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableDeviceSessionService : IDeviceSessionService
{
    private static readonly DeviceSessionOperationResult Unavailable = new(
        DeviceSessionOperationStatus.Unavailable, "DEVICE_SESSION_SERVICE_UNAVAILABLE", null);

    public Task<DeviceSessionOperationResult> RegisterAsync(
        AuthenticatedGatewayUser user, DeviceSessionDescriptor device, int maximumActiveDevices,
        CancellationToken cancellationToken = default) => Task.FromResult(Unavailable);

    public Task<DeviceSessionListResult> ListAsync(
        AuthenticatedGatewayUser user, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceSessionListResult(DeviceSessionOperationStatus.Unavailable,
            "DEVICE_SESSION_SERVICE_UNAVAILABLE", []));

    public Task<DeviceSessionOperationResult> RevokeAsync(
        AuthenticatedGatewayUser user, Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unavailable);

    public Task<DeviceSessionOperationResult> ValidateAsync(
        AuthenticatedGatewayUser user, Guid deviceId, Guid sessionId, TimeSpan lastSeenWriteInterval,
        CancellationToken cancellationToken = default) => Task.FromResult(Unavailable);
}

public sealed class PostgresDeviceSessionService(NpgsqlDataSource dataSource) : IDeviceSessionService
{
    private static readonly HashSet<string> RejectionCodes = new(StringComparer.Ordinal)
    {
        "DEVICE_SESSION_REQUEST_INVALID",
        "DEVICE_SESSION_REVOKED",
        "DEVICE_LIMIT_REACHED",
    };

    public async Task<DeviceSessionOperationResult> RegisterAsync(
        AuthenticatedGatewayUser user,
        DeviceSessionDescriptor device,
        int maximumActiveDevices,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || device.DeviceId == Guid.Empty
            || maximumActiveDevices is < 1 or > 100 || !IsValidDescriptor(device))
            return Rejected("DEVICE_SESSION_REQUEST_INVALID");

        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT status_code, device_id, session_id, display_name, client_version, platform,
                       created_at, last_seen_at, revoked_at
                FROM private.device_session_register($1, $2, $3, $4, $5, $6)
                """);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, device.DeviceId);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, device.DisplayName);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, device.ClientVersion);
            command.Parameters.AddWithValue(NpgsqlDbType.Varchar, device.Platform);
            command.Parameters.AddWithValue(NpgsqlDbType.Integer, maximumActiveDevices);
            return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == "P0001"
            && RejectionCodes.Contains(exception.ConstraintName ?? string.Empty))
        {
            return Rejected(exception.ConstraintName!);
        }
        catch (NpgsqlException)
        {
            return Unavailable();
        }
    }

    public async Task<DeviceSessionListResult> ListAsync(
        AuthenticatedGatewayUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty)
            return new(DeviceSessionOperationStatus.Rejected, "DEVICE_SESSION_REQUEST_INVALID", []);
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT device_id, session_id, display_name, client_version, platform,
                       created_at, last_seen_at, revoked_at
                FROM private.user_device_sessions
                WHERE user_id = $1
                ORDER BY created_at DESC, session_id
                LIMIT 100
                """);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var sessions = new List<DeviceSessionSnapshot>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                sessions.Add(ReadSnapshot(reader));
            return new(DeviceSessionOperationStatus.Succeeded, "DEVICE_SESSIONS_LISTED", sessions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            return new(DeviceSessionOperationStatus.Unavailable,
                "DEVICE_SESSION_SERVICE_UNAVAILABLE", []);
        }
    }

    public async Task<DeviceSessionOperationResult> RevokeAsync(
        AuthenticatedGatewayUser user,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || sessionId == Guid.Empty)
            return Rejected("DEVICE_SESSION_REQUEST_INVALID");
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT status_code, device_id, session_id, display_name, client_version, platform,
                       created_at, last_seen_at, revoked_at
                FROM private.device_session_revoke($1, $2)
                """);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, sessionId);
            return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == "P0001"
            && exception.ConstraintName == "DEVICE_SESSION_NOT_FOUND")
        {
            return new(DeviceSessionOperationStatus.NotFound, "DEVICE_SESSION_NOT_FOUND", null);
        }
        catch (NpgsqlException)
        {
            return Unavailable();
        }
    }

    public async Task<DeviceSessionOperationResult> ValidateAsync(
        AuthenticatedGatewayUser user,
        Guid deviceId,
        Guid sessionId,
        TimeSpan lastSeenWriteInterval,
        CancellationToken cancellationToken = default)
    {
        if (user.UserId == Guid.Empty || deviceId == Guid.Empty || sessionId == Guid.Empty
            || lastSeenWriteInterval < TimeSpan.FromMinutes(1)
            || lastSeenWriteInterval > TimeSpan.FromDays(1))
            return Rejected("DEVICE_SESSION_REQUIRED");
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT status_code, device_id, session_id, display_name, client_version, platform,
                       created_at, last_seen_at, revoked_at
                FROM private.device_session_validate($1, $2, $3, $4)
                """);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, user.UserId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, deviceId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, sessionId);
            command.Parameters.AddWithValue(NpgsqlDbType.Integer,
                checked((int)lastSeenWriteInterval.TotalSeconds));
            return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == "P0001"
            && exception.ConstraintName == "DEVICE_SESSION_INACTIVE")
        {
            return Rejected("DEVICE_SESSION_INACTIVE");
        }
        catch (NpgsqlException)
        {
            return Unavailable();
        }
    }

    private static async Task<DeviceSessionOperationResult> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Unavailable();
        var code = reader.GetString(0);
        if (code is not ("DEVICE_SESSION_REGISTERED" or "DEVICE_SESSION_IDEMPOTENT_REPLAY"
            or "DEVICE_SESSION_ACTIVE" or "DEVICE_SESSION_REVOKED" or "DEVICE_SESSION_REVOKE_REPLAY"))
            return Unavailable();
        return new(DeviceSessionOperationStatus.Succeeded, code, ReadSnapshot(reader, 1),
            IsNewDevice: code == "DEVICE_SESSION_REGISTERED");
    }

    private static DeviceSessionSnapshot ReadSnapshot(NpgsqlDataReader reader, int offset = 0) => new(
        reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2),
        reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetFieldValue<DateTimeOffset>(offset + 5),
        reader.GetFieldValue<DateTimeOffset>(offset + 6),
        reader.IsDBNull(offset + 7) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 7));

    private static bool IsValidDescriptor(DeviceSessionDescriptor device) =>
        IsValidText(device.DisplayName, 64, asciiOnly: false)
        && IsValidText(device.ClientVersion, 32, asciiOnly: true)
        && IsValidText(device.Platform, 32, asciiOnly: true);

    private static bool IsValidText(string value, int maximumLength, bool asciiOnly) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
        && !value.Any(char.IsControl) && (!asciiOnly || value.All(char.IsAscii));

    private static DeviceSessionOperationResult Rejected(string code) =>
        new(DeviceSessionOperationStatus.Rejected, code, null);

    private static DeviceSessionOperationResult Unavailable() =>
        new(DeviceSessionOperationStatus.Unavailable, "DEVICE_SESSION_SERVICE_UNAVAILABLE", null);
}
