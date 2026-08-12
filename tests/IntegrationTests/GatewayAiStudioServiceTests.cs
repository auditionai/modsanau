using System.Net;
using System.Text;
using AuditionModStudio.Cloud;
using AuditionModStudio.Core.AI;
using AuditionModStudio.Core.Auth;
using AuditionModStudio.Core.Images;
using AuditionModStudio.Imaging;

namespace IntegrationTests;

public sealed class GatewayAiStudioServiceTests
{
    [Fact]
    public async Task Quote_uses_secure_bearer_session_and_parses_bounded_server_price()
    {
        var handler = new StubHandler(_ => Json("""
            {"creditCost":7,"pricingVersion":"v2"}
            """));
        var store = new StubSessionStore(Session("fake-access-token"));
        var service = CreateService(handler, store);

        var result = await service.GetQuoteAsync(AiStudioOperation.Generate);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Quote!.CreditCost);
        Assert.Equal("v2", result.Quote.PricingVersion);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("fake-access-token", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.DoesNotContain("fake-access-token", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_validates_owner_scoped_shape_and_derives_cancel_state()
    {
        var jobId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json($$"""
            {"jobs":[{"jobId":"{{jobId:D}}","operation":"Edit","status":"Processing","reservedCredits":9,"finalCredits":null,"createdAt":"2026-08-12T00:00:00+00:00"}]}
            """));
        var service = CreateService(handler, new StubSessionStore(Session("fake-access-token")));

        var result = await service.GetHistoryAsync();

        var job = Assert.Single(result.Jobs);
        Assert.True(result.Succeeded);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal(AiStudioJobState.Processing, job.State);
        Assert.True(job.CanCancel);
    }

    [Fact]
    public async Task Expired_session_refreshes_through_auth_service_and_oversized_response_fails_closed()
    {
        var content = new ByteArrayContent(new byte[256 * 1_024 + 1]);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var store = new StubSessionStore(new("expired", "fake-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var auth = new StubAuthenticationService(store);
        var service = CreateService(handler, store, auth);

        var result = await service.GetQuoteAsync(AiStudioOperation.Generate);

        Assert.False(result.Succeeded);
        Assert.Equal("AI_STUDIO_RESPONSE_INVALID", result.DiagnosticCode);
        Assert.Equal(1, auth.RefreshCount);
        Assert.Equal("refreshed-access", handler.LastRequest!.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Execution_uploads_private_content_polls_job_and_imports_authenticated_bounded_output()
    {
        var encoder = new AiTransportImageEncoder();
        var outputBytes = await encoder.EncodePngAsync(Image(4, 4));
        var jobId = Guid.NewGuid();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/ai/content/source" => Json("""{"contentId":"source_1"}"""),
            "/v1/ai/jobs" when request.Method == HttpMethod.Post => Json($$"""{"jobId":"{{jobId:D}}"}"""),
            _ when request.RequestUri.AbsolutePath == $"/v1/ai/jobs/{jobId:D}" =>
                Json("""{"status":"Completed","outputReference":"output_1"}"""),
            "/v1/ai/content/output_1" => ImageResponse(outputBytes!),
            _ => new(HttpStatusCode.NotFound),
        });
        var store = new StubSessionStore(Session("fake-access-token"));
        var service = new GatewayAiStudioService(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }, store,
            new StubAuthenticationService(store), new(new Uri("https://gateway.example/")),
            encoder, new ImageImportService(ImageImportResourcePolicy.Default));

        var result = await service.ExecuteAsync(new(AiStudioOperation.Upscale, Image(2, 2), null, null,
            new(4, 4), "server-default", "request-1"));

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Preview!.Width);
        Assert.Equal(4, result.Preview.Height);
        Assert.Equal([
            "/v1/ai/content/source", "/v1/ai/jobs", $"/v1/ai/jobs/{jobId:D}",
            "/v1/ai/content/output_1"], handler.Paths);
        Assert.All(handler.AuthorizationParameters, value => Assert.Equal("fake-access-token", value));
    }

    [Fact]
    public async Task Cancellation_after_enqueue_requests_server_job_cancellation_without_client_ledger_mutation()
    {
        var jobId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/ai/content/source" => Json("""{"contentId":"source_1"}"""),
            "/v1/ai/jobs" when request.Method == HttpMethod.Post => Json($$"""{"jobId":"{{jobId:D}}"}"""),
            _ when request.RequestUri.AbsolutePath == $"/v1/ai/jobs/{jobId:D}"
                => CancelAndReturnProcessing(cancellation),
            _ when request.RequestUri.AbsolutePath == $"/v1/ai/jobs/{jobId:D}/cancel"
                => Json("""{"status":"Cancelled"}"""),
            _ => new(HttpStatusCode.NotFound),
        });
        var store = new StubSessionStore(Session("fake-access-token"));
        var service = new GatewayAiStudioService(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }, store,
            new StubAuthenticationService(store), new(new Uri("https://gateway.example/")),
            new AiTransportImageEncoder(), new ImageImportService(ImageImportResourcePolicy.Default));
        var result = await service.ExecuteAsync(new(AiStudioOperation.Upscale, Image(2, 2), null, null,
            new(4, 4), "server-default", "request-1"), cancellationToken: cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Contains($"/v1/ai/jobs/{jobId:D}/cancel", handler.Paths);
    }

    private static HttpResponseMessage CancelAndReturnProcessing(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return Json("""{"status":"Processing"}""");
    }

    [Fact]
    public async Task Execution_rejects_non_expanding_geometry_before_network_use()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.InternalServerError));
        var store = new StubSessionStore(Session("fake-access-token"));
        var service = new GatewayAiStudioService(
            new HttpClient(handler), store, new StubAuthenticationService(store),
            new(new Uri("https://gateway.example/")), new AiTransportImageEncoder(),
            new ImageImportService(ImageImportResourcePolicy.Default));

        var result = await service.ExecuteAsync(new(AiStudioOperation.Upscale, Image(4, 4), null, null,
            new(2, 2), "server-default", "request-1"));

        Assert.False(result.Succeeded);
        Assert.Equal("AI_STUDIO_REQUEST_INVALID", result.DiagnosticCode);
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execution_rejects_oversized_or_invalid_download_before_publishing_preview(bool oversized)
    {
        var jobId = Guid.NewGuid();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/ai/content/source" => Json("""{"contentId":"source_1"}"""),
            "/v1/ai/jobs" when request.Method == HttpMethod.Post => Json($$"""{"jobId":"{{jobId:D}}"}"""),
            _ when request.RequestUri.AbsolutePath == $"/v1/ai/jobs/{jobId:D}" =>
                Json("""{"status":"Completed","outputReference":"output_1"}"""),
            "/v1/ai/content/output_1" => InvalidImageResponse(oversized),
            _ => new(HttpStatusCode.NotFound),
        });
        var store = new StubSessionStore(Session("fake-access-token"));
        var service = new GatewayAiStudioService(
            new HttpClient(handler), store, new StubAuthenticationService(store),
            new(new Uri("https://gateway.example/")), new AiTransportImageEncoder(),
            new ImageImportService(ImageImportResourcePolicy.Default));

        var result = await service.ExecuteAsync(new(AiStudioOperation.Upscale, Image(2, 2), null, null,
            new(4, 4), "server-default", "request-1"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Preview);
        Assert.Equal(oversized ? "AI_OUTPUT_DOWNLOAD_INVALID" : "AI_OUTPUT_IMPORT_FAILED",
            result.DiagnosticCode);
    }

    private static GatewayAiStudioService CreateService(
        HttpMessageHandler handler,
        StubSessionStore store,
        IAuthenticationService? authentication = null) => new(
        new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
        store,
        authentication ?? new StubAuthenticationService(store),
        new(new Uri("https://gateway.example/")));

    private static AuthSessionSecrets Session(string accessToken) =>
        new(accessToken, "fake-refresh-token", DateTimeOffset.UtcNow.AddHours(1));

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage InvalidImageResponse(bool oversized)
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new("image/png");
        if (oversized) content.Headers.ContentLength = 16 * 1024 * 1024 + 1;
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static InternalImage Image(int width, int height) => new(width, height, width * 4,
        Enumerable.Repeat((byte)255, width * height * 4).ToArray(),
        new(ImageSourceFormat.Png, width, height, ImageSourceOrientation.Normal, true, false));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<string> Paths { get; } = [];
        public List<string?> AuthorizationParameters { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Paths.Add(request.RequestUri!.AbsolutePath);
            AuthorizationParameters.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(response(request));
        }
    }

    private sealed class StubSessionStore(AuthSessionSecrets? session) : ISecureSessionStore
    {
        public AuthSessionSecrets? Session { get; set; } = session;
        public Task<AuthSessionSecrets?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Session);
        public Task SaveAsync(AuthSessionSecrets value, CancellationToken cancellationToken = default)
        { Session = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken cancellationToken = default)
        { Session = null; return Task.CompletedTask; }
    }

    private sealed class StubAuthenticationService(StubSessionStore store) : IAuthenticationService
    {
        public int RefreshCount { get; private set; }
        public Task<AuthenticationResult> RefreshSessionAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            store.Session = Session("refreshed-access");
            return Task.FromResult(AuthenticationResult.Success(null, null));
        }
        public Task<AuthenticationResult> SignUpAsync(AuthEmail email, AuthPassword password,
            CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> SignInAsync(AuthEmail email, AuthPassword password,
            CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> SignOutAsync(CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> ResetPasswordAsync(AuthEmail email,
            CancellationToken cancellationToken = default) => Unsupported();
        public Task<AuthenticationResult> GetProfileAsync(CancellationToken cancellationToken = default) => Unsupported();
        private static Task<AuthenticationResult> Unsupported() => Task.FromResult(
            AuthenticationResult.Failure(AuthenticationFailureReason.Unavailable, "TEST_UNAVAILABLE"));
    }
}
