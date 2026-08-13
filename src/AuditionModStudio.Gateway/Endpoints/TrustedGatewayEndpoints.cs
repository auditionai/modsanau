using System.Collections.Immutable;
using System.Security.Claims;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Core.Games;
using AuditionModStudio.Core.Mods;
using AuditionModStudio.Gateway.Services;
using AuditionModStudio.Gateway.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace AuditionModStudio.Gateway.Endpoints;

public sealed record AiGatewayRequest(
    string? Prompt,
    int? TargetWidth,
    int? TargetHeight,
    string? ImageBase64,
    string? MaskBase64);

public sealed record TemplateEntitlementRequest(
    string? TemplateId,
    string? Version,
    string? Sha256,
    string? CompatibleGameBuild);

public sealed record EntitlementGrantRequest(
    PremiumEntitlementScope Scope,
    TemplateEntitlementRequest? Template,
    string? GameId,
    string? ModId);

public sealed record PremiumTemplateAccessApiRequest(
    string? TemplateId,
    string? Version,
    string? GameId,
    string? ModId);

public sealed record AiPricingQuoteRequest(
    TrustedAiOperation Operation,
    string? ExpectedPricingVersion);

public sealed record AiJobEnqueueRequest(
    TrustedAiOperation Operation,
    AiJobInputMetadata? InputMetadata,
    string? IdempotencyKey,
    string? ExpectedPricingVersion);

public static class TrustedGatewayEndpoints
{
    private const int MaximumPromptLength = 4_000;
    private const int MaximumImageBytes = 4 * 1_024 * 1_024;
    private const int MaximumBase64Length = 5_592_408;
    private const long MaximumAiRequestBytes = 12 * 1_024 * 1_024;
    private const long MaximumEntitlementRequestBytes = 16 * 1_024;
    private const long MaximumPricingRequestBytes = 4 * 1_024;
    private const long MaximumJobRequestBytes = 20 * 1_024;
    private const long MaximumContentRequestBytes = 16 * 1_024 * 1_024;

    public static void MapTrustedGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        foreach (var operation in Enum.GetValues<TrustedAiOperation>())
        {
            var route = OperationRoute(operation);
            endpoints.MapPost($"/v1/ai/{route}", (ClaimsPrincipal principal,
                    AiGatewayRequest? request,
                    ITrustedAiGateway service,
                    CancellationToken cancellationToken) =>
                    ExecuteAiAsync(principal, operation, request, service, cancellationToken))
                .RequireAuthorization()
                .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
                .WithMetadata(new RequestSizeLimitAttribute(MaximumAiRequestBytes));
        }

        endpoints.MapGet("/v1/credits", async (ClaimsPrincipal principal,
                ITrustedCreditQueryService service,
                CancellationToken cancellationToken) =>
            MapCreditResult(await service.GetAsync(User(principal), cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/account", async (HttpContext context,
                ClaimsPrincipal principal,
                ITrustedAccountQueryService service,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                return MapAccountResult(principal,
                    await service.GetAsync(User(principal), cancellationToken).ConfigureAwait(false));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/product-catalog", async (HttpContext context,
                IProductCatalogDocumentService service, CancellationToken cancellationToken) =>
            {
                var result = await service.GetAsync(cancellationToken).ConfigureAwait(false);
                if (!IsSafeDiagnosticCode(result.DiagnosticCode)
                    || result.Status == ProductCatalogDocumentStatus.Succeeded
                    && (result.Document.IsDefaultOrEmpty || result.Document.Length > 4 * 1024 * 1024
                        || string.IsNullOrWhiteSpace(result.ETag)))
                    return InvalidTrustedResponse();
                if (result.Status != ProductCatalogDocumentStatus.Succeeded)
                    return SafeProblem(StatusCodes.Status503ServiceUnavailable,
                        "Product catalog is unavailable.", result.DiagnosticCode);
                context.Response.Headers.ETag = result.ETag;
                context.Response.Headers.CacheControl = "private, no-cache";
                return Results.Bytes(result.Document.ToArray(), "application/json; charset=utf-8");
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/ai/pricing/quote", async (ClaimsPrincipal principal,
                AiPricingQuoteRequest? request,
                IAiPricingService service,
                CancellationToken cancellationToken) =>
            {
                if (!TryCreatePricingVersion(request?.ExpectedPricingVersion, out var expectedVersion)
                    || request is null || !Enum.IsDefined(request.Operation))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["AI pricing request is invalid."],
                    });
                }

                var result = await service.QuoteAsync(User(principal), request.Operation,
                    expectedVersion, cancellationToken).ConfigureAwait(false);
                return MapPricingResult(result);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumPricingRequestBytes));

        endpoints.MapPost("/v1/ai/jobs", async (ClaimsPrincipal principal,
                AiJobEnqueueRequest? request,
                IAiJobService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null || request.InputMetadata is null
                    || !Enum.IsDefined(request.Operation) || !request.InputMetadata.IsValid
                    || !IsValidExecutionMetadata(request.Operation, request.InputMetadata)
                    || !TryCreateJobIdempotencyKey(request.IdempotencyKey, out var idempotencyKey)
                    || !TryCreatePricingVersion(request.ExpectedPricingVersion, out var expectedVersion))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["request"] = ["AI job request is invalid."],
                    });
                }
                return MapJobResult(await service.EnqueueAsync(User(principal), request.Operation,
                    request.InputMetadata, idempotencyKey!.Value, expectedVersion, cancellationToken)
                    .ConfigureAwait(false));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.ChargedOperationPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumJobRequestBytes));

        endpoints.MapPost("/v1/ai/content/{kind}", async (HttpRequest httpRequest,
                ClaimsPrincipal principal, string kind, IAiMediaValidator validator,
                IAiContentStore store, CancellationToken cancellationToken) =>
            await PutContentAsync(httpRequest, User(principal), kind, validator, store, cancellationToken)
                .ConfigureAwait(false))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumContentRequestBytes));

        endpoints.MapGet("/v1/ai/content/{contentId}", async (ClaimsPrincipal principal,
                string contentId, IAiContentStore store, CancellationToken cancellationToken) =>
            await GetContentAsync(User(principal), contentId, store, cancellationToken).ConfigureAwait(false))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/ai/jobs", async (ClaimsPrincipal principal,
                IAiJobService service,
                CancellationToken cancellationToken) =>
            MapJobListResult(await service.ListAsync(User(principal), cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapGet("/v1/ai/jobs/{jobId:guid}", async (ClaimsPrincipal principal,
                Guid jobId,
                IAiJobService service,
                CancellationToken cancellationToken) =>
            MapJobResult(await service.GetAsync(User(principal), jobId, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/ai/jobs/{jobId:guid}/cancel", async (ClaimsPrincipal principal,
                Guid jobId,
                IAiJobService service,
                CancellationToken cancellationToken) =>
            MapJobResult(await service.CancelAsync(User(principal), jobId, cancellationToken)
                .ConfigureAwait(false)))
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(0));

        endpoints.MapPost("/v1/templates/entitlement", async (ClaimsPrincipal principal,
                TemplateEntitlementRequest? request,
                ITrustedTemplateEntitlementService service,
                CancellationToken cancellationToken) =>
            {
                if (request is null || !TryCreateTemplateIdentity(request, out var identity))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["template"] = ["Template identity is invalid."],
                    });
                }

                var result = await service.CheckAsync(User(principal), identity!, cancellationToken)
                    .ConfigureAwait(false);
                return MapEntitlementResult(result);
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumEntitlementRequestBytes));

        endpoints.MapPost("/v1/entitlements/grants", async (ClaimsPrincipal principal,
                EntitlementGrantRequest? request,
                IEntitlementGrantService service,
                CancellationToken cancellationToken) =>
            {
                if (!TryCreateGrantDescriptor(request, out var descriptor))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["request"] = ["Entitlement grant request is invalid."] });
                var result = await service.IssueAsync(User(principal), descriptor!, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsSafeDiagnosticCode(result.DiagnosticCode)) return InvalidTrustedResponse();
                return result.Status switch
                {
                    TrustedServiceStatus.Succeeded when IsSafeGrant(result.Grant) => Results.Ok(new
                    {
                        result.DiagnosticCode,
                        result.Grant,
                    }),
                    TrustedServiceStatus.Rejected => Results.Json(
                        new GatewayErrorResponse(result.DiagnosticCode), statusCode: StatusCodes.Status403Forbidden),
                    _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                        "Entitlement grant service is unavailable.", result.DiagnosticCode),
                };
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumEntitlementRequestBytes));

        endpoints.MapGet("/v1/premium-templates/catalog", async (
                IPremiumTemplateCatalogService catalog, CancellationToken cancellationToken) =>
            {
                var result = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
                if (!IsSafeDiagnosticCode(result.DiagnosticCode)) return InvalidTrustedResponse();
                if (result.Status != PremiumTemplateCatalogStatus.Succeeded)
                    return SafeProblem(StatusCodes.Status503ServiceUnavailable,
                        "Premium template catalog is unavailable.", result.DiagnosticCode);
                if (result.Manifests.Any(manifest => manifest is not { IsValid: true }))
                    return InvalidTrustedResponse();
                return Results.Ok(result.Manifests.Select(manifest => new
                {
                    TemplateId = manifest.Identity.TemplateId.Value,
                    Version = manifest.Identity.Version.Value,
                    CompatibleGameBuild = manifest.Identity.CompatibleGameBuild.Value,
                    GameId = manifest.GameId.Value,
                    ModId = manifest.ModId.Value,
                    manifest.ContentLength,
                    manifest.MediaType,
                }));
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy);

        endpoints.MapPost("/v1/premium-templates/access", async (ClaimsPrincipal principal,
                PremiumTemplateAccessApiRequest? request,
                IPremiumTemplateDistributionService distribution,
                CancellationToken cancellationToken) =>
            {
                if (!TryCreatePremiumTemplateAccessRequest(request, out var accessRequest))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["request"] = ["Premium template access request is invalid."] });
                var result = await distribution.AuthorizeAsync(User(principal), accessRequest!, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsSafeDiagnosticCode(result.DiagnosticCode)) return InvalidTrustedResponse();
                return result.Status switch
                {
                    TrustedServiceStatus.Succeeded when result.Manifest is { IsValid: true }
                        && IsSafeBase64(result.PackageSignature) && result.DownloadUri is not null
                        && result.ExpiresAt is not null => Results.Ok(new
                        {
                            result.DiagnosticCode,
                            TemplateId = result.Manifest.Identity.TemplateId.Value,
                            Version = result.Manifest.Identity.Version.Value,
                            ExpectedSha256 = result.Manifest.Identity.Sha256.Value,
                            CompatibleGameBuild = result.Manifest.Identity.CompatibleGameBuild.Value,
                            result.Manifest.ContentLength,
                            result.Manifest.MediaType,
                            result.PackageSignature,
                            DownloadUrl = result.DownloadUri.AbsoluteUri,
                            result.ExpiresAt,
                        }),
                    TrustedServiceStatus.Rejected => Results.Json(
                        new GatewayErrorResponse(result.DiagnosticCode), statusCode: StatusCodes.Status403Forbidden),
                    _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                        "Premium template distribution is unavailable.", result.DiagnosticCode),
                };
            })
            .RequireAuthorization()
            .RequireRateLimiting(GatewayAbuseProtectionDefaults.AuthenticatedPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumEntitlementRequestBytes));
    }

    private static async Task<IResult> ExecuteAiAsync(
        ClaimsPrincipal principal,
        TrustedAiOperation operation,
        AiGatewayRequest? request,
        ITrustedAiGateway service,
        CancellationToken cancellationToken)
    {
        if (request is null || !TryCreateAiRequest(operation, request, out var trustedRequest))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["AI request schema is invalid for this operation."],
            });
        }

        var result = await service.ExecuteAsync(User(principal), trustedRequest!, cancellationToken)
            .ConfigureAwait(false);
        if (!Enum.IsDefined(result.Status)
            || !IsSafeDiagnosticCode(result.DiagnosticCode)
            || result.Status == TrustedServiceStatus.Succeeded && !IsSafeOutputReference(result.OutputReference))
        {
            return InvalidTrustedResponse();
        }
        return result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                result.OutputReference,
            }),
            TrustedServiceStatus.Rejected => Results.UnprocessableEntity(new GatewayErrorResponse(result.DiagnosticCode)),
            _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "Trusted AI service is unavailable.", result.DiagnosticCode),
        };
    }

    private static IResult MapCreditResult(TrustedCreditResult result)
    {
        if (!Enum.IsDefined(result.Status)
            || !IsSafeDiagnosticCode(result.DiagnosticCode)
            || result.Status == TrustedServiceStatus.Succeeded
            && (result.Snapshot is null
                || result.Snapshot.AvailableCredits < 0
                || result.Snapshot.ReservedCredits < 0))
        {
            return InvalidTrustedResponse();
        }

        return result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(result.Snapshot),
            TrustedServiceStatus.Rejected => Results.Forbid(),
            _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "Credit service is unavailable.", result.DiagnosticCode),
        };
    }

    private static IResult MapAccountResult(ClaimsPrincipal principal, TrustedAccountResult result)
    {
        if (!Enum.IsDefined(result.Status) || !IsSafeDiagnosticCode(result.DiagnosticCode))
            return InvalidTrustedResponse();
        if (result.Status != TrustedServiceStatus.Succeeded)
            return result.Status == TrustedServiceStatus.Rejected
                ? SafeProblem(StatusCodes.Status403Forbidden, "Account request was rejected.", result.DiagnosticCode)
                : SafeProblem(StatusCodes.Status503ServiceUnavailable, "Account service is unavailable.", result.DiagnosticCode);
        var snapshot = result.Snapshot;
        var email = principal.FindFirstValue(ClaimTypes.Email);
        var displayName = principal.FindFirstValue(ClaimTypes.Name);
        if (snapshot is null || snapshot.AvailableCredits < 0 || snapshot.ReservedCredits < 0
            || snapshot.CreditsGranted < 0 || snapshot.CreditsUsed < 0 || snapshot.TransactionCount < 0
            || snapshot.ObservedAt == default || snapshot.Transactions.IsDefault
            || snapshot.Transactions.Length > PostgresAccountQueryService.MaximumHistoryEntries
            || email is not null && (email.Length > 320 || email.Any(char.IsControl))
            || displayName is not null && (displayName.Length > 128 || displayName.Any(char.IsControl))
            || snapshot.Transactions.Any(item => item.TransactionId == Guid.Empty
                || item.Kind is not ("grant" or "reserve" or "capture" or "release" or "refund")
                || item.Amount <= 0 || item.AvailableAfter < 0 || item.ReservedAfter < 0
                || item.CreatedAt == default || item.CreatedAt > snapshot.ObservedAt)
            || snapshot.Transactions.Length != Math.Min(snapshot.TransactionCount,
                PostgresAccountQueryService.MaximumHistoryEntries)
            || snapshot.Transactions.Length > 0
            && (snapshot.Transactions[0].AvailableAfter != snapshot.AvailableCredits
                || snapshot.Transactions[0].ReservedAfter != snapshot.ReservedCredits)
            || !IsAccountHistoryDescending(snapshot.Transactions))
            return InvalidTrustedResponse();
        return Results.Ok(new
        {
            Profile = new { UserId = User(principal).UserId, Email = email, DisplayName = displayName },
            Wallet = new { snapshot.AvailableCredits, snapshot.ReservedCredits },
            Usage = new { snapshot.CreditsGranted, snapshot.CreditsUsed, snapshot.TransactionCount },
            Transactions = snapshot.Transactions.Select(item => new
            {
                item.TransactionId,
                item.Kind,
                item.Amount,
                item.AvailableDelta,
                item.ReservedDelta,
                item.AvailableAfter,
                item.ReservedAfter,
                item.CreatedAt,
            }),
            snapshot.ObservedAt,
        });
    }

    private static bool IsAccountHistoryDescending(ImmutableArray<TrustedAccountTransaction> history)
    {
        for (var index = 1; index < history.Length; index++)
            if (history[index - 1].CreatedAt < history[index].CreatedAt) return false;
        return true;
    }

    private static IResult MapPricingResult(AiPricingResult result)
    {
        if (!Enum.IsDefined(result.Status)
            || !IsSafeDiagnosticCode(result.DiagnosticCode)
            || result.Status is AiPricingStatus.Succeeded or AiPricingStatus.PriceChanged
            && !IsValidQuote(result.Quote))
        {
            return InvalidTrustedResponse();
        }

        var response = result.Quote is null ? null : new
        {
            Operation = result.Quote.Operation,
            CreditCost = result.Quote.CreditCost.Value,
            PricingVersion = result.Quote.PricingVersion.Value,
            result.Quote.EffectiveAt,
            result.DiagnosticCode,
        };
        return result.Status switch
        {
            AiPricingStatus.Succeeded => Results.Ok(response),
            AiPricingStatus.PriceChanged => Results.Conflict(response),
            AiPricingStatus.Rejected => Results.UnprocessableEntity(new GatewayErrorResponse(result.DiagnosticCode)),
            _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "AI pricing service is unavailable.", result.DiagnosticCode),
        };
    }

    private static IResult MapEntitlementResult(TrustedTemplateEntitlementResult result)
    {
        if (!Enum.IsDefined(result.Status) || !IsSafeDiagnosticCode(result.DiagnosticCode))
        {
            return InvalidTrustedResponse();
        }

        return result.Status switch
        {
            TrustedServiceStatus.Succeeded => Results.Ok(new { result.Granted }),
            TrustedServiceStatus.Rejected => Results.Forbid(),
            _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "Template entitlement service is unavailable.", result.DiagnosticCode),
        };
    }

    private static IResult MapJobResult(AiJobOperationResult result)
    {
        if (!Enum.IsDefined(result.Status) || !IsSafeDiagnosticCode(result.DiagnosticCode)
            || result.Status == AiJobOperationStatus.Succeeded && !IsValidJob(result.Job)
            || result.Status == AiJobOperationStatus.PriceChanged && !IsValidQuote(result.CurrentQuote))
        {
            return InvalidTrustedResponse();
        }
        return result.Status switch
        {
            AiJobOperationStatus.Succeeded => Results.Ok(JobResponse(result.Job!)),
            AiJobOperationStatus.PriceChanged => Results.Conflict(new
            {
                result.DiagnosticCode,
                Operation = result.CurrentQuote!.Operation,
                CreditCost = result.CurrentQuote.CreditCost.Value,
                PricingVersion = result.CurrentQuote.PricingVersion.Value,
                result.CurrentQuote.EffectiveAt,
            }),
            AiJobOperationStatus.Rejected => Results.UnprocessableEntity(
                new GatewayErrorResponse(result.DiagnosticCode)),
            AiJobOperationStatus.NotFound => Results.NotFound(new GatewayErrorResponse(result.DiagnosticCode)),
            _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "AI job service is unavailable.", result.DiagnosticCode),
        };
    }

    private static IResult MapJobListResult(AiJobListResult result)
    {
        if (!Enum.IsDefined(result.Status) || !IsSafeDiagnosticCode(result.DiagnosticCode)
            || result.Status == AiJobOperationStatus.Succeeded && result.Jobs.Any(job => !IsValidJob(job)))
        {
            return InvalidTrustedResponse();
        }
        return result.Status switch
        {
            AiJobOperationStatus.Succeeded => Results.Ok(new
            {
                result.DiagnosticCode,
                Jobs = result.Jobs.Select(JobResponse),
            }),
            AiJobOperationStatus.Rejected => Results.UnprocessableEntity(
                new GatewayErrorResponse(result.DiagnosticCode)),
            _ => SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "AI job service is unavailable.", result.DiagnosticCode),
        };
    }

    private static async Task<IResult> PutContentAsync(HttpRequest request, AuthenticatedGatewayUser owner,
        string kindText, IAiMediaValidator validator, IAiContentStore store, CancellationToken cancellationToken)
    {
        var kind = kindText switch
        {
            "source" => AiContentKind.SourceImage,
            "mask" => AiContentKind.Mask,
            _ => (AiContentKind?)null,
        };
        if (kind is null || request.ContentLength is null or <= 0 or > MaximumContentRequestBytes
            || request.ContentType is not { } mediaType
            || mediaType.Split(';', 2)[0].Trim().ToLowerInvariant() is not
                ("image/png" or "image/jpeg" or "image/webp" or "image/bmp"))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["content"] = ["AI content request is invalid."] });
        var bytes = new byte[request.ContentLength.Value];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return Results.BadRequest(new GatewayErrorResponse("AI_CONTENT_TRUNCATED"));
            offset += read;
        }
        var validated = await validator.ValidateAsync(mediaType.Split(';', 2)[0].Trim().ToLowerInvariant(),
            bytes, cancellationToken).ConfigureAwait(false);
        if (validated is null) return Results.UnprocessableEntity(new GatewayErrorResponse("AI_CONTENT_INVALID"));
        var result = await store.PutAsync(owner, kind.Value, validated, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Metadata is { } metadata
            && IsValidContentMetadata(metadata, owner, kind.Value, validated)
            ? Results.Ok(new
            {
                ContentId = metadata.ContentId.Value,
                metadata.Width,
                metadata.Height,
                metadata.ByteLength,
                metadata.Sha256,
                metadata.ExpiresAt
            })
            : SafeProblem(StatusCodes.Status503ServiceUnavailable,
                "AI content storage is unavailable.", result.DiagnosticCode);
    }

    private static async Task<IResult> GetContentAsync(AuthenticatedGatewayUser owner, string value,
        IAiContentStore store, CancellationToken cancellationToken)
    {
        AiContentId contentId;
        try { contentId = new(value); }
        catch (ArgumentException) { return Results.NotFound(); }
        var stored = await store.GetAsync(owner, contentId, AiContentKind.ProviderOutput, cancellationToken)
            .ConfigureAwait(false);
        return stored is null || !IsValidStoredOutput(stored, owner)
            ? Results.NotFound()
            : Results.File(stored.Bytes.ToArray(), stored.Metadata.MediaType,
                enableRangeProcessing: false);
    }

    private static bool IsValidContentMetadata(AiContentMetadata metadata, AuthenticatedGatewayUser owner,
        AiContentKind kind, AiValidatedMedia media) => metadata.OwnerUserId == owner.UserId
        && metadata.Kind == kind && metadata.MediaType == media.MediaType
        && metadata.Width == media.Width && metadata.Height == media.Height
        && metadata.ByteLength == media.Bytes.Length && metadata.ExpiresAt > DateTimeOffset.UtcNow
        && metadata.Sha256.Equals(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(media.Bytes.AsSpan())), StringComparison.Ordinal);

    private static bool IsValidStoredOutput(AiStoredContent stored, AuthenticatedGatewayUser owner)
    {
        var metadata = stored.Metadata;
        return metadata.OwnerUserId == owner.UserId && metadata.Kind == AiContentKind.ProviderOutput
            && metadata.MediaType is "image/png" or "image/jpeg" or "image/webp" or "image/bmp"
            && metadata.Width is > 0 and <= 16_384 && metadata.Height is > 0 and <= 16_384
            && (long)metadata.Width * metadata.Height <= 100_000_000
            && metadata.ByteLength == stored.Bytes.Length && metadata.ByteLength is > 0 and <= 16 * 1024 * 1024
            && metadata.ExpiresAt > DateTimeOffset.UtcNow
            && metadata.Sha256.Equals(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stored.Bytes.AsSpan())), StringComparison.Ordinal);
    }

    private static bool IsValidExecutionMetadata(TrustedAiOperation operation, AiJobInputMetadata metadata)
    {
        var hasMask = metadata.MaskReference is not null;
        var hasPrompt = !string.IsNullOrWhiteSpace(metadata.Prompt);
        var hasTarget = metadata.TargetWidth is not null && metadata.TargetHeight is not null;
        var hasSource = metadata.SourceWidth is not null && metadata.SourceHeight is not null;
        var targetIsBounded = !hasTarget || (long)metadata.TargetWidth!.Value * metadata.TargetHeight!.Value
            <= 100_000_000;
        var targetExpandsSource = hasTarget && hasSource
            && metadata.TargetWidth >= metadata.SourceWidth && metadata.TargetHeight >= metadata.SourceHeight
            && (metadata.TargetWidth > metadata.SourceWidth || metadata.TargetHeight > metadata.SourceHeight);
        return targetIsBounded && operation switch
        {
            TrustedAiOperation.Inpaint => hasSource && hasMask && hasPrompt && !hasTarget,
            TrustedAiOperation.Outpaint => hasSource && !hasMask && hasPrompt && targetExpandsSource,
            TrustedAiOperation.RemoveObject => hasSource && hasMask && !hasPrompt && !hasTarget,
            TrustedAiOperation.ReplaceObject => hasSource && hasMask && hasPrompt && !hasTarget,
            TrustedAiOperation.Upscale => hasSource && !hasMask && !hasPrompt && targetExpandsSource,
            _ => true,
        };
    }

    private static object JobResponse(AiJobSnapshot job) => new
    {
        job.JobId,
        job.Operation,
        job.Status,
        job.InputMetadata,
        job.ReservedCredits,
        job.FinalCredits,
        job.PricingVersion,
        job.OutputReference,
        job.ErrorCode,
        job.AttemptCount,
        job.CancelRequested,
        job.CreatedAt,
        job.UpdatedAt,
    };

    private static IResult SafeProblem(int statusCode, string title, string code) =>
        Results.Problem(statusCode: statusCode, title: title, extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
        });

    private static IResult InvalidTrustedResponse() =>
        SafeProblem(StatusCodes.Status500InternalServerError,
            "Trusted service returned an invalid response.", "GATEWAY_RESPONSE_INVALID");

    private static bool IsSafeDiagnosticCode(string code) =>
        !string.IsNullOrWhiteSpace(code)
        && code.Length <= 128
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static bool IsSafeOutputReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value[0] == '/') return false;
        var segments = value.Split('/');
        return segments.All(segment => segment.Length > 0
                                       && segment is not "." and not ".."
                                       && segment.All(character => char.IsAsciiLetterOrDigit(character)
                                           || character is '-' or '_' or '.'));
    }

    private static bool IsSafeGrant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8_192) return false;
        var segments = value.Split('.');
        return segments.Length == 3 && segments.All(segment => segment.Length > 0
            && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    private static bool IsSafeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return false;
        try { return Convert.FromBase64String(value).Length == 64; }
        catch (FormatException) { return false; }
    }

    private static bool TryCreateAiRequest(
        TrustedAiOperation operation,
        AiGatewayRequest request,
        out TrustedAiRequest? trustedRequest)
    {
        trustedRequest = null;
        var prompt = request.Prompt?.Trim();
        var promptProvided = request.Prompt is not null;
        var hasPrompt = !string.IsNullOrWhiteSpace(prompt)
                        && prompt.Length <= MaximumPromptLength
                        && !prompt.Any(character => char.IsControl(character)
                            && character is not '\r' and not '\n' and not '\t');
        var hasSize = request.TargetWidth is > 0 and <= 16_384
                      && request.TargetHeight is > 0 and <= 16_384;
        var hasNoSize = request.TargetWidth is null && request.TargetHeight is null;
        if (!TryDecode(request.ImageBase64, out var image) || !TryDecode(request.MaskBase64, out var mask))
        {
            return false;
        }

        var valid = operation switch
        {
            TrustedAiOperation.Generate => hasPrompt && hasSize && image.IsEmpty && mask.IsEmpty,
            TrustedAiOperation.Edit => hasPrompt && hasNoSize && !image.IsEmpty && mask.IsEmpty,
            TrustedAiOperation.Inpaint => hasPrompt && hasNoSize && !image.IsEmpty && !mask.IsEmpty,
            TrustedAiOperation.Outpaint => hasPrompt && hasSize && !image.IsEmpty && mask.IsEmpty,
            TrustedAiOperation.RemoveObject => !promptProvided && hasNoSize && !image.IsEmpty && !mask.IsEmpty,
            TrustedAiOperation.ReplaceObject => hasPrompt && hasNoSize && !image.IsEmpty && !mask.IsEmpty,
            TrustedAiOperation.Upscale => !promptProvided && hasSize && !image.IsEmpty && mask.IsEmpty,
            _ => false,
        };
        if (!valid)
        {
            return false;
        }

        trustedRequest = new(operation, hasPrompt ? prompt : null,
            hasSize ? request.TargetWidth : null, hasSize ? request.TargetHeight : null, image, mask);
        return true;
    }

    private static bool TryCreateGrantDescriptor(EntitlementGrantRequest? request,
        out EntitlementGrantDescriptor? descriptor)
    {
        descriptor = null;
        if (request is null || !Enum.IsDefined(request.Scope)) return false;
        if (request.Scope == PremiumEntitlementScope.PremiumAi)
        {
            if (request.Template is not null || request.GameId is not null || request.ModId is not null) return false;
            descriptor = new(request.Scope, EntitlementGrantDescriptor.PremiumAiAudience, null, null, null);
            return true;
        }
        if (request.Template is null || !TryCreateTemplateIdentity(request.Template, out var template)
            || !GameId.TryCreate(request.GameId, out var gameId) || !ModId.TryCreate(request.ModId, out var modId))
            return false;
        descriptor = new(request.Scope, EntitlementGrantDescriptor.TemplateAudience,
            template, gameId, modId);
        return descriptor.IsValid;
    }

    private static bool TryCreatePremiumTemplateAccessRequest(PremiumTemplateAccessApiRequest? request,
        out PremiumTemplateAccessRequest? access)
    {
        access = null;
        try
        {
            if (request is null) return false;
            access = new(new TemplateId(request.TemplateId!), new TemplateVersion(request.Version!),
                new GameId(request.GameId!), new ModId(request.ModId!));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool TryDecode(string? value, out ImmutableArray<byte> bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value)) return true;
        if (value.Length > MaximumBase64Length) return false;

        var buffer = new byte[Math.Min(MaximumImageBytes + 1, value.Length * 3 / 4 + 3)];
        if (!Convert.TryFromBase64String(value, buffer, out var written)
            || written > MaximumImageBytes
            || !HasSupportedImageSignature(buffer.AsSpan(0, written)))
        {
            return false;
        }

        bytes = ImmutableArray.Create(buffer, 0, written);
        return true;
    }

    private static bool HasSupportedImageSignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature)
        || bytes.StartsWith(JpegSignature)
        || bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)
        || bytes.StartsWith("BM"u8);

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static bool TryCreateTemplateIdentity(
        TemplateEntitlementRequest request,
        out TemplateIdentity? identity)
    {
        try
        {
            identity = new(
                new TemplateId(request.TemplateId!),
                new TemplateVersion(request.Version!),
                new TemplateSha256(request.Sha256!),
                new CompatibleGameBuild(request.CompatibleGameBuild!));
            return identity.IsValid;
        }
        catch (ArgumentException)
        {
            identity = null;
            return false;
        }
    }

    private static bool TryCreatePricingVersion(string? value, out AiPricingVersion? version)
    {
        version = null;
        if (value is null) return true;
        try
        {
            version = new AiPricingVersion(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCreateJobIdempotencyKey(string? value, out AiJobIdempotencyKey? key)
    {
        key = null;
        try
        {
            key = new AiJobIdempotencyKey(value!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidQuote(AiPricingQuote? quote) =>
        quote is not null
        && Enum.IsDefined(quote.Operation)
        && quote.CreditCost.Value is > 0 and <= AiCreditPrice.MaximumCredits
        && !string.IsNullOrEmpty(quote.PricingVersion.Value)
        && quote.EffectiveAt.Offset == TimeSpan.Zero;

    private static bool IsValidJob(AiJobSnapshot? job) =>
        job is not null
        && job.JobId != Guid.Empty
        && Enum.IsDefined(job.Operation)
        && Enum.IsDefined(job.Status)
        && job.InputMetadata.IsValid
        && job.ReservedCredits > 0
        && job.FinalCredits is null or > 0
        && (job.FinalCredits is null || job.FinalCredits <= job.ReservedCredits)
        && !string.IsNullOrWhiteSpace(job.PricingVersion)
        && (job.OutputReference is null || IsSafeOutputReference(job.OutputReference))
        && (job.ErrorCode is null || IsSafeDiagnosticCode(job.ErrorCode));

    private static AuthenticatedGatewayUser User(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId) && userId != Guid.Empty
            ? new(userId)
            : throw new InvalidOperationException("Authenticated principal is missing a valid subject.");
    }

    private static string OperationRoute(TrustedAiOperation operation) => operation switch
    {
        TrustedAiOperation.Generate => "generate",
        TrustedAiOperation.Edit => "edit",
        TrustedAiOperation.Inpaint => "inpaint",
        TrustedAiOperation.Outpaint => "outpaint",
        TrustedAiOperation.RemoveObject => "remove-object",
        TrustedAiOperation.ReplaceObject => "replace-object",
        TrustedAiOperation.Upscale => "upscale",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
