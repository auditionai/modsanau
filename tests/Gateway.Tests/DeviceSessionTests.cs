using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using AuditionModStudio.Gateway.Authentication;
using AuditionModStudio.Gateway.Endpoints;
using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Tests;

public sealed class DeviceSessionTests
{
    private static readonly Guid UserId = Guid.Parse("1311c502-7648-4665-98c8-cb27085f318b");
    private static readonly Guid DeviceId = Guid.Parse("411057a8-eb68-4d13-a292-7204619e6fa8");
    private static readonly Guid SessionId = Guid.Parse("b5c45818-c150-4521-922e-31e4139b16f7");

    [Fact]
    public async Task Registration_uses_verified_subject_and_rejects_client_user_authority()
    {
        await using var factory = new DeviceSessionFactory();
        using var client = AuthenticatedClient(factory);

        var accepted = await client.PostAsJsonAsync("/v1/device-sessions/register",
            new DeviceSessionRegistrationRequest(DeviceId, "Máy chính", "1.0.0", "windows-x64"));
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        using var forgedBody = new StringContent($$"""
            {"deviceId":"{{DeviceId:D}}","displayName":"Main","clientVersion":"1.0.0",
             "platform":"windows-x64","userId":"{{Guid.NewGuid():D}}"}
            """, Encoding.UTF8, "application/json");
        var forged = await client.PostAsync("/v1/device-sessions/register", forgedBody);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(UserId, factory.Sessions.LastUser!.UserId);
        Assert.Equal(1, factory.Sessions.RegisterCallCount);
        Assert.Contains("DEVICE_SESSION_REGISTERED", acceptedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("userId", acceptedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", acceptedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(1, factory.Sessions.RegisterCallCount);
    }

    [Fact]
    public async Task Commercial_endpoint_requires_active_bound_device_session()
    {
        await using var factory = new DeviceSessionFactory();
        using var client = AuthenticatedClient(factory);

        var missing = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        AddSessionHeaders(client);
        factory.Sessions.Active = false;
        var revoked = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        var premium = await client.GetAsync("/v1/premium-templates/catalog");
        var credits = await client.GetAsync("/v1/credits");
        factory.Sessions.Active = true;
        var active = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, premium.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, credits.StatusCode);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(1, factory.Ai.CallCount);
        Assert.Equal(UserId, factory.Sessions.LastUser!.UserId);
        Assert.Equal(DeviceId, factory.Sessions.LastDeviceId);
        Assert.Equal(SessionId, factory.Sessions.LastSessionId);
    }

    [Fact]
    public async Task Revoking_current_session_blocks_the_next_cloud_request_but_keeps_management_reachable()
    {
        await using var factory = new DeviceSessionFactory();
        using var client = AuthenticatedClient(factory);
        AddSessionHeaders(client);

        var before = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        var revoked = await client.DeleteAsync($"/v1/device-sessions/{SessionId:D}");
        var after = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        var list = await client.GetAsync("/v1/device-sessions");

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(1, factory.Ai.CallCount);
        Assert.Equal(1, factory.Sessions.RevokeCallCount);
    }

    [Fact]
    public async Task Session_validation_failure_is_fail_closed_without_calling_commercial_service()
    {
        await using var factory = new DeviceSessionFactory();
        factory.Sessions.Unavailable = true;
        using var client = AuthenticatedClient(factory);
        AddSessionHeaders(client);

        var response = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Ai.CallCount);
        Assert.DoesNotContain("access-token", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_control_preserves_existing_cloud_behavior_and_disables_management_api()
    {
        await using var factory = new DeviceSessionFactory { Enabled = false };
        using var client = AuthenticatedClient(factory);

        var commercial = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));
        var registration = await client.PostAsJsonAsync("/v1/device-sessions/register",
            new DeviceSessionRegistrationRequest(DeviceId, "Main", "1.0.0", "windows-x64"));

        Assert.Equal(HttpStatusCode.OK, commercial.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, registration.StatusCode);
        Assert.Equal(1, factory.Ai.CallCount);
        Assert.Equal(0, factory.Sessions.RegisterCallCount);
    }

    [Fact]
    public async Task Management_api_reuses_authenticated_user_rate_limit()
    {
        await using var factory = new DeviceSessionFactory { AuthenticatedPermitLimit = 1 };
        using var client = AuthenticatedClient(factory);
        var request = new DeviceSessionRegistrationRequest(DeviceId, "Main", "1.0.0", "windows-x64");

        var first = await client.PostAsJsonAsync("/v1/device-sessions/register", request);
        var limited = await client.PostAsJsonAsync("/v1/device-sessions/register", request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(1, factory.Sessions.RegisterCallCount);
    }

    [Fact]
    public async Task Enabled_control_with_invalid_server_limit_fails_closed()
    {
        await using var factory = new DeviceSessionFactory { MaximumActiveDevices = 0 };
        using var client = AuthenticatedClient(factory);
        AddSessionHeaders(client);

        var response = await client.PostAsJsonAsync("/v1/ai/generate",
            new AiGatewayRequest("prompt", 1, 1, null, null));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Ai.CallCount);
        Assert.Equal(0, factory.Sessions.ValidateCallCount);
    }

    [Fact]
    public void Contracts_and_storage_have_no_hardware_fingerprint_or_secret_fields()
    {
        var forbidden = new[]
        {
            "UserId", "MachineGuid", "Cpu", "Disk", "Mac", "IpAddress", "AccessToken", "RefreshToken", "Secret",
        };
        var publicProperties = new[]
        {
            typeof(DeviceSessionRegistrationRequest), typeof(DeviceSessionDescriptor),
            typeof(DeviceSessionSnapshot),
        }.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.Name).ToArray();
        var migration = File.ReadAllText(MigrationPath());

        Assert.DoesNotContain(publicProperties,
            property => forbidden.Contains(property, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(forbidden.Skip(1), term =>
            migration.Contains(term, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("input_user_id", migration, StringComparison.Ordinal);
        Assert.Contains("auth.uid()", migration, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC, anon, authenticated", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_session_control_remains_outside_local_editor_and_archive_boundaries()
    {
        var root = RepositoryRoot();
        var localPipelineProjects = new[]
        {
            "AuditionModStudio.Archives", "AuditionModStudio.Dds", "AuditionModStudio.Imaging",
            "AuditionModStudio.Projects",
        };
        var localFiles = localPipelineProjects.SelectMany(project => Directory.GetFiles(
            Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories));

        Assert.DoesNotContain(localFiles, file =>
            File.ReadAllText(file).Contains("DeviceSession", StringComparison.Ordinal));
        Assert.DoesNotContain(File.ReadAllText(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "AuditionModStudio.Gateway.csproj")), "AuditionModStudio.App", StringComparison.Ordinal);
    }

    private static void AddSessionHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(DeviceSessionHeaders.DeviceId, DeviceId.ToString("D"));
        client.DefaultRequestHeaders.Add(DeviceSessionHeaders.SessionId, SessionId.ToString("D"));
    }

    private static HttpClient AuthenticatedClient(DeviceSessionFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "access-token");
        return client;
    }

    private static string MigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations",
        "202608130001_plan86_device_sessions.sql");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class DeviceSessionFactory : WebApplicationFactory<Program>
    {
        public bool Enabled { get; init; } = true;
        public int AuthenticatedPermitLimit { get; init; } = 60;
        public int MaximumActiveDevices { get; init; } = 2;
        public StubDeviceSessionService Sessions { get; } = new();
        public CountingAiGateway Ai { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:AbuseProtection:RequireHttps", "true");
            builder.UseSetting("Gateway:AbuseProtection:AuthenticatedPermitLimit",
                AuthenticatedPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Gateway:DeviceSessions:Enabled", Enabled.ToString());
            builder.UseSetting("Gateway:DeviceSessions:MaximumActiveDevices",
                MaximumActiveDevices.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Gateway:DeviceSessions:LastSeenWriteIntervalSeconds", "900");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISupabaseAccessTokenValidator>();
                services.RemoveAll<IDeviceSessionService>();
                services.RemoveAll<ITrustedAiGateway>();
                services.AddSingleton<ISupabaseAccessTokenValidator>(new ValidTokenValidator());
                services.AddSingleton<IDeviceSessionService>(Sessions);
                services.AddSingleton<ITrustedAiGateway>(Ai);
            });
        }
    }

    private sealed class ValidTokenValidator : ISupabaseAccessTokenValidator
    {
        public Task<AccessTokenValidationResult> ValidateAsync(string accessToken,
            CancellationToken cancellationToken = default) => Task.FromResult(
            AccessTokenValidationResult.Valid(UserId, "owner@example.invalid"));
    }

    private sealed class CountingAiGateway : ITrustedAiGateway
    {
        public int CallCount { get; private set; }
        public Task<TrustedAiResult> ExecuteAsync(AuthenticatedGatewayUser user, TrustedAiRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new TrustedAiResult(TrustedServiceStatus.Succeeded, "AI_COMPLETED", "output_1"));
        }
    }

    private sealed class StubDeviceSessionService : IDeviceSessionService
    {
        public bool Active { get; set; } = true;
        public bool Unavailable { get; set; }
        public int RegisterCallCount { get; private set; }
        public int RevokeCallCount { get; private set; }
        public int ValidateCallCount { get; private set; }
        public AuthenticatedGatewayUser? LastUser { get; private set; }
        public Guid LastDeviceId { get; private set; }
        public Guid LastSessionId { get; private set; }

        public Task<DeviceSessionOperationResult> RegisterAsync(AuthenticatedGatewayUser user,
            DeviceSessionDescriptor device, int maximumActiveDevices,
            CancellationToken cancellationToken = default)
        {
            RegisterCallCount++;
            LastUser = user;
            return Task.FromResult(new DeviceSessionOperationResult(DeviceSessionOperationStatus.Succeeded,
                "DEVICE_SESSION_REGISTERED", Snapshot(), true));
        }

        public Task<DeviceSessionListResult> ListAsync(AuthenticatedGatewayUser user,
            CancellationToken cancellationToken = default)
        {
            LastUser = user;
            return Task.FromResult(new DeviceSessionListResult(DeviceSessionOperationStatus.Succeeded,
                "DEVICE_SESSIONS_LISTED", [Snapshot()]));
        }

        public Task<DeviceSessionOperationResult> RevokeAsync(AuthenticatedGatewayUser user, Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            RevokeCallCount++;
            LastUser = user;
            Active = false;
            return Task.FromResult(new DeviceSessionOperationResult(DeviceSessionOperationStatus.Succeeded,
                "DEVICE_SESSION_REVOKED", Snapshot(DateTimeOffset.UnixEpoch.AddMinutes(2))));
        }

        public Task<DeviceSessionOperationResult> ValidateAsync(AuthenticatedGatewayUser user, Guid deviceId,
            Guid sessionId, TimeSpan lastSeenWriteInterval, CancellationToken cancellationToken = default)
        {
            ValidateCallCount++;
            LastUser = user;
            LastDeviceId = deviceId;
            LastSessionId = sessionId;
            if (Unavailable)
                return Task.FromResult(new DeviceSessionOperationResult(DeviceSessionOperationStatus.Unavailable,
                    "DEVICE_SESSION_SERVICE_UNAVAILABLE", null));
            return Task.FromResult(Active
                ? new DeviceSessionOperationResult(DeviceSessionOperationStatus.Succeeded,
                    "DEVICE_SESSION_ACTIVE", Snapshot())
                : new DeviceSessionOperationResult(DeviceSessionOperationStatus.Rejected,
                    "DEVICE_SESSION_INACTIVE", null));
        }

        private static DeviceSessionSnapshot Snapshot(DateTimeOffset? revokedAt = null) => new(
            DeviceId, SessionId, "Máy chính", "1.0.0", "windows-x64",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), revokedAt);
    }
}
