using AuditionModStudio.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AuditionModStudio.Gateway.Endpoints;

public static class PaymentWebhookEndpoints
{
    private const long MaximumWebhookBytes = 128 * 1_024;

    public static void MapPaymentWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/payments/webhooks/stripe", HandleStripeAsync)
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(MaximumWebhookBytes));
    }

    private static async Task<IResult> HandleStripeAsync(
        HttpRequest request,
        IPaymentApplicationService payments,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is <= 0 or > MaximumWebhookBytes)
            return Results.BadRequest(new GatewayErrorResponse("PAYMENT_WEBHOOK_PAYLOAD_INVALID"));

        var signatureValues = request.Headers["Stripe-Signature"];
        if (signatureValues.Count != 1)
            return Results.BadRequest(new GatewayErrorResponse("PAYMENT_WEBHOOK_SIGNATURE_INVALID"));

        var rawBody = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        if (rawBody is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var result = await payments.ProcessWebhookAsync("stripe", rawBody, signatureValues[0], cancellationToken)
            .ConfigureAwait(false);
        if (!Enum.IsDefined(result.Status) || !IsSafeCode(result.DiagnosticCode))
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        return result.Status switch
        {
            PaymentProcessingStatus.Applied or PaymentProcessingStatus.Replay
                or PaymentProcessingStatus.Pending or PaymentProcessingStatus.Ignored =>
                Results.Ok(new { Received = true }),
            PaymentProcessingStatus.Invalid => Results.BadRequest(new GatewayErrorResponse(result.DiagnosticCode)),
            PaymentProcessingStatus.Conflict => Results.Conflict(new GatewayErrorResponse(result.DiagnosticCode)),
            PaymentProcessingStatus.Retryable => Results.Json(new GatewayErrorResponse(result.DiagnosticCode),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1_024];
        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.Length == 0 ? [] : output.ToArray();
            if (output.Length + read > MaximumWebhookBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSafeCode(string code) => !string.IsNullOrWhiteSpace(code)
        && code.Length <= 128
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
