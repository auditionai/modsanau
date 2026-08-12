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
        IPaymentWebhookVerifier verifier,
        IPaymentFulfillmentService fulfillment,
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

        var verification = verifier.Verify(rawBody, signatureValues[0]);
        if (!Enum.IsDefined(verification.Status) || !IsSafeCode(verification.DiagnosticCode)
            || verification.Status == PaymentWebhookStatus.Verified && verification.Payment is null)
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        if (verification.Status == PaymentWebhookStatus.Invalid)
            return Results.BadRequest(new GatewayErrorResponse(verification.DiagnosticCode));
        if (verification.Status == PaymentWebhookStatus.Unavailable)
            return Results.Json(new GatewayErrorResponse(verification.DiagnosticCode),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (verification.Status == PaymentWebhookStatus.Ignored)
            return Results.Ok(new { Received = true });

        var applied = await fulfillment.ApplyAsync(verification.Payment!, cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(applied.Status) || !IsSafeCode(applied.DiagnosticCode))
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        return applied.Status switch
        {
            PaymentApplyStatus.Applied or PaymentApplyStatus.Replay => Results.Ok(new { Received = true }),
            PaymentApplyStatus.Rejected => Results.Conflict(new GatewayErrorResponse(applied.DiagnosticCode)),
            _ => Results.Json(new GatewayErrorResponse(applied.DiagnosticCode),
                statusCode: StatusCodes.Status503ServiceUnavailable),
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
