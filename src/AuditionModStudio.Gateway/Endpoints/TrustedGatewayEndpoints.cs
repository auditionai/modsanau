using System.Collections.Immutable;
using System.Security.Claims;
using AuditionModStudio.Core.Archives;
using AuditionModStudio.Gateway.Services;
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

public static class TrustedGatewayEndpoints
{
    private const int MaximumPromptLength = 4_000;
    private const int MaximumImageBytes = 4 * 1_024 * 1_024;
    private const int MaximumBase64Length = 5_592_408;
    private const long MaximumAiRequestBytes = 12 * 1_024 * 1_024;
    private const long MaximumEntitlementRequestBytes = 16 * 1_024;

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
                .WithMetadata(new RequestSizeLimitAttribute(MaximumAiRequestBytes));
        }

        endpoints.MapGet("/v1/credits", async (ClaimsPrincipal principal,
                ITrustedCreditQueryService service,
                CancellationToken cancellationToken) =>
            MapCreditResult(await service.GetAsync(User(principal), cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization();

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
